using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;
using WallstopStudios.DxMessaging.SourceGenerators;

namespace WallstopStudios.DxMessaging.SourceGenerators.Tests;

/// <summary>Protects positional constructor arguments from unrelated offsets in other partial files.</summary>
[TestFixture]
internal sealed class ConstructorFieldOrderTests
{
    /// <summary>Compares handwritten and generated calls under independent file padding and names.</summary>
    /// <param name="shape">The required, optional, single-file, or multi-variable declaration.</param>
    /// <param name="target">The reference or value-type target.</param>
    /// <param name="padFirst">Whether the first declaration has the larger file-local offset.</param>
    /// <param name="reverseFileNames">Whether filename sorting disagrees with syntax-tree order.</param>
    [TestCaseSource(nameof(Cases))]
    public void FieldsFollowCompilationTreeOrderBeforeTheirLocalOffsets(
        string shape,
        string target,
        bool padFirst,
        bool reverseFileNames
    )
    {
        string context =
            $"shape={shape}, target={target}, padFirst={padFirst}, reverseFileNames={reverseFileNames}";
        string declaration = target == "class" ? "partial class" : "readonly partial struct";
        bool multiple = shape == "MultipleVariables";
        bool optional = shape == "Optional";
        bool sameFile = shape == "SameFile";
        string firstFields = multiple
            ? "public readonly int First, Middle;"
            : "public readonly int First;";
        string secondFields =
            (optional ? "[DxMessaging.Core.Attributes.DxOptionalParameter(99)] " : "")
            + "public readonly int Second;";
        string parameters =
            "int First, "
            + (multiple ? "int Middle, " : "")
            + "int Second"
            + (optional ? " = 99" : "");
        string assignments =
            "this.First = First; "
            + (multiple ? "this.Middle = Middle; " : "")
            + "this.Second = Second;";
        string firstCall =
            optional ? "42"
            : multiple ? "42, 43, 99"
            : "42, 99";
        string secondCall = multiple
            ? "Second: 67, Middle: 85, First: 84"
            : "Second: 67, First: 84";
        string reads = multiple
            ? "first.First, first.Middle, first.Second, second.First, second.Middle, second.Second"
            : "first.First, first.Second, second.First, second.Second";
        object[] expected = multiple
            ? new object[] { 42, 43, 99, 84, 85, 67 }
            : new object[] { 42, 99, 84, 67 };
        string[] expectedParameters = multiple
            ? new[] { "First", "Middle", "Second" }
            : new[] { "First", "Second" };
        CSharpCompilation Create(bool manual)
        {
            string firstSource =
                (padFirst ? new string('\n', 200) : "")
                + $$"""
namespace FieldOrder
{
    {{(manual ? "" : "[DxMessaging.Core.Attributes.DxAutoConstructor]")}}
    {{(
                        target == "class"
                            ? ""
                            : "[global::System.Runtime.InteropServices.StructLayout(global::System.Runtime.InteropServices.LayoutKind.Auto)]"
                    )}}
    public {{declaration}} Packet
    {
        {{firstFields}}
        {{(sameFile ? secondFields : "")}}
        {{(manual ? $"public Packet({parameters}) {{ {assignments} }}" : "")}}
    }
    public static class Consumer
    {
        public static object[] Read()
        {
            Packet first = new Packet({{firstCall}});
            Packet second = new Packet({{secondCall}});
            return new object[] { {{reads}} };
        }
    }
}
""";
            string secondSource =
                (padFirst ? "" : new string('\n', 200))
                + $"namespace FieldOrder {{ public {declaration} Packet {{ {(sameFile ? "" : secondFields)} }} }}";
            CSharpCompilation compilation =
                GeneratorTestUtilities.CreateAutoConstructorCompilationFromShippedAttributes(
                    firstSource
                );
            SyntaxTree firstTree = compilation.SyntaxTrees.Last();
            compilation = compilation.ReplaceSyntaxTree(
                firstTree,
                firstTree.WithFilePath(reverseFileNames ? "Z.cs" : "A.cs")
            );
            return compilation.AddSyntaxTrees(
                CSharpSyntaxTree.ParseText(
                    secondSource,
                    new CSharpParseOptions(LanguageVersion.CSharp9),
                    reverseFileNames ? "A.cs" : "Z.cs"
                )
            );
        }
        AssertCallerExecutes(
            Create(manual: true),
            expected,
            expectedParameters,
            "Handwritten control: " + context
        );
        GeneratorDriver driver = CSharpGeneratorDriver
            .Create(
                generators: new ISourceGenerator[] { new DxAutoConstructorGenerator() },
                parseOptions: new CSharpParseOptions(LanguageVersion.CSharp9)
            )
            .RunGeneratorsAndUpdateCompilation(
                Create(manual: false),
                out Compilation generated,
                out ImmutableArray<Diagnostic> diagnostics
            );
        Assert.That(
            diagnostics,
            Is.Empty,
            "Retain valid declarations: " + context + "\n" + string.Join("\n", diagnostics)
        );
        Assert.That(
            driver.GetRunResult().Results.Select(result => result.Exception),
            Has.All.Null,
            "Ordering must not crash generation: " + context
        );
        AssertCallerExecutes(
            generated,
            expected,
            expectedParameters,
            "Generated caller: " + context
        );
    }

    /// <summary>Varies source shape without changing the expected caller contract.</summary>
    private static IEnumerable<TestCaseData> Cases()
    {
        foreach (string shape in new[] { "Required", "Optional", "SameFile", "MultipleVariables" })
        {
            foreach (string target in new[] { "class", "readonly struct" })
            {
                foreach (bool padFirst in new[] { false, true })
                {
                    foreach (bool reverseFileNames in new[] { false, true })
                    {
                        yield return new TestCaseData(
                            shape,
                            target,
                            padFirst,
                            reverseFileNames
                        ).SetName(
                            $"FieldsFollowCompilationTreeOrderBeforeTheirLocalOffsets({shape},{target},{padFirst},{reverseFileNames})"
                        );
                    }
                }
            }
        }
    }

    /// <summary>Checks actual emitted parameter metadata and executed caller values.</summary>
    private static void AssertCallerExecutes(
        Compilation compilation,
        object[] expected,
        string[] expectedParameters,
        string context
    )
    {
        using MemoryStream image = new();
        Microsoft.CodeAnalysis.Emit.EmitResult emitted = compilation.Emit(image);
        Assert.That(
            emitted.Success,
            Is.True,
            context + "\n" + string.Join("\n", emitted.Diagnostics)
        );
        image.Position = 0;
        AssemblyLoadContext load = new(context, isCollectible: true);
        try
        {
            Assembly assembly = load.LoadFromStream(image);
            ConstructorInfo constructor = assembly
                .GetType("FieldOrder.Packet")!
                .GetConstructors()
                .Single(candidate => candidate.GetParameters().Length == expectedParameters.Length);
            object[] actual = (object[])
                assembly.GetType("FieldOrder.Consumer")!.GetMethod("Read")!.Invoke(null, null)!;
            Assert.That(
                actual,
                Is.EqualTo(expected),
                "Preserve positional, named, and omitted values: " + context
            );
            Assert.That(
                constructor.GetParameters().Select(parameter => parameter.Name),
                Is.EqualTo(expectedParameters),
                "Preserve positional parameter order: " + context
            );
        }
        finally
        {
            load.Unload();
        }
    }
}
