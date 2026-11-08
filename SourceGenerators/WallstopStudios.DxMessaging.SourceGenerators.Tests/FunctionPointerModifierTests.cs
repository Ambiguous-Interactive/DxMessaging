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

[TestFixture]
internal sealed class FunctionPointerModifierTests
{
    [TestCaseSource(nameof(Cases))]
    public void GeneratedFunctionPointerParametersRetainSignatureIdentity(
        string shape,
        bool referenceType
    )
    {
        string context = $"shape={shape}; referenceType={referenceType}";
        AssertCallerExecutes(
            CreateCompilation(CreateSource(shape, referenceType, manual: true)),
            "Handwritten oracle: " + context
        );
        CSharpCompilation input = CreateCompilation(
            CreateSource(shape, referenceType, manual: false)
        );
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new ISourceGenerator[] { new DxAutoConstructorGenerator() },
            parseOptions: new CSharpParseOptions(LanguageVersion.CSharp9)
        );
        driver = driver.RunGeneratorsAndUpdateCompilation(
            input,
            out Compilation generated,
            out ImmutableArray<Diagnostic> diagnostics
        );
        Assert.That(
            driver.GetRunResult().Results.Select(result => result.Exception),
            Has.All.Null,
            context
        );
        Assert.That(diagnostics, Is.Empty, context + "\n" + string.Join("\n", diagnostics));
        Assert.That(driver.GetRunResult().GeneratedTrees, Has.Length.EqualTo(1), context);
        AssertCallerExecutes(generated, "Generated caller: " + context);
    }

    private static IEnumerable<TestCaseData> Cases()
    {
        foreach (
            string shape in new[]
            {
                "RefReturn",
                "OutParameter",
                "ReadOnlyReturn",
                "InParameter",
                "NestedPointer",
                "NestedFunction",
                "ArrayRefReturn",
                "JaggedReadOnly",
            }
        )
        {
            foreach (bool referenceType in new[] { false, true })
            {
                yield return new TestCaseData(shape, referenceType).SetName(
                    $"GeneratedFunctionPointerParametersRetainSignatureIdentity({shape},{referenceType})"
                );
            }
        }
    }

    private static string CreateSource(string shape, bool referenceType, bool manual)
    {
        (string element, string function) = shape switch
        {
            "RefReturn" or "ArrayRefReturn" => (
                "delegate*<ref int, ref int>",
                "private static ref int Identity(ref int value) => ref value;"
            ),
            "OutParameter" => (
                "delegate*<out int, void>",
                "private static void Identity(out int value) { value = 37; }"
            ),
            "ReadOnlyReturn" or "JaggedReadOnly" => (
                "delegate*<in int, ref readonly int>",
                "private static ref readonly int Identity(in int value) => ref value;"
            ),
            "InParameter" => (
                "delegate*<in int, int>",
                "private static int Identity(in int value) => value;"
            ),
            "NestedPointer" => (
                "delegate*<int*, void>",
                "private static unsafe void Identity(int* value) { }"
            ),
            "NestedFunction" => (
                "delegate*<delegate*<int, int>, void>",
                "private static unsafe void Identity(delegate*<int, int> value) { }"
            ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(shape),
                shape,
                "Unknown function pointer shape"
            ),
        };
        string suffix =
            shape == "ArrayRefReturn" ? "[]"
            : shape == "JaggedReadOnly" ? "[][]"
            : "";
        string type = element + suffix;
        string setup = suffix switch
        {
            "[]" => $"{type} expected = new {element}[1]; expected[0] = &Identity;",
            "[][]" =>
                $"{type} expected = new {element}[1][]; expected[0] = new {element}[1]; expected[0][0] = &Identity;",
            _ => $"{type} expected = &Identity;",
        };
        string comparison = suffix switch
        {
            "[]" =>
                "object.ReferenceEquals(packet.Value, expected) && (void*)packet.Value[0] == (void*)expected[0]",
            "[][]" =>
                "object.ReferenceEquals(packet.Value, expected) && (void*)packet.Value[0][0] == (void*)expected[0][0]",
            _ => "(void*)packet.Value == (void*)expected",
        };
        string constructor = manual ? $"public Packet({type} Value) {{ this.Value = Value; }}" : "";
        return $$"""
            namespace FunctionPointerModifiers
            {
                {{(manual ? "" : "[DxMessaging.Core.Attributes.DxAutoConstructor]")}}
                public unsafe {{(
                referenceType ? "partial class" : "readonly partial struct"
            )}} Packet
                {
                    public readonly {{type}} Value;
                    {{constructor}}
                }
                public static class Caller
                {
                    {{function}}
                    public static unsafe bool Read()
                    {
                        {{setup}}
                        Packet packet = new Packet(expected);
                        return {{comparison}};
                    }
                }
            }
            """;
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        CSharpCompilation compilation =
            GeneratorTestUtilities.CreateAutoConstructorCompilationFromShippedAttributes(source);
        return compilation.WithOptions(compilation.Options.WithAllowUnsafe(true));
    }

    private static void AssertCallerExecutes(Compilation compilation, string context)
    {
        using MemoryStream stream = new();
        Microsoft.CodeAnalysis.Emit.EmitResult emitted = compilation.Emit(stream);
        Assert.That(
            emitted.Success,
            Is.True,
            context + "\n" + string.Join("\n", emitted.Diagnostics)
        );
        AssemblyLoadContext load = new("FunctionPointerModifiers", isCollectible: true);
        try
        {
            stream.Position = 0;
            Assembly assembly = load.LoadFromStream(stream);
            object? result = assembly
                .GetType("FunctionPointerModifiers.Caller")!
                .GetMethod("Read")!
                .Invoke(null, null);
            Assert.That(
                result,
                Is.True,
                context
                    + ": preserve original pointer or array identity without invoking the function"
            );
        }
        finally
        {
            load.Unload();
        }
    }
}
