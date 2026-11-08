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
internal sealed class PointerConstructorContextTests
{
    [TestCaseSource(nameof(Cases))]
    public void GeneratedConstructorsPreservePointerFieldContexts(
        string shape,
        string target,
        string scope
    )
    {
        bool pointers = shape is not "Int" and not "IntPtr";
        string context = $"shape={shape}; target={target}; scope={scope}";
        AssertCallerExecutes(
            CreateCompilation(CreateSource(shape, target, scope, manual: true), pointers),
            "Handwritten oracle: " + context
        );
        CSharpCompilation input = CreateCompilation(
            CreateSource(shape, target, scope, manual: false),
            pointers
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
        if (!pointers)
        {
            Assert.That(
                driver.GetRunResult().GeneratedTrees[0].ToString(),
                Does.Not.Contain("unsafe"),
                "Ordinary callers must compile without enabling unsafe code: " + context
            );
        }
        AssertCallerExecutes(generated, "Generated caller: " + context);
    }

    private static IEnumerable<TestCaseData> Cases()
    {
        foreach (
            string shape in new[]
            {
                "IntPointer",
                "VoidPointer",
                "PointerPointer",
                "PointerArray",
                "PointerRectangle",
                "PointerJagged",
                "Int",
                "IntPtr",
            }
        )
        {
            foreach (string target in new[] { "class", "readonly struct" })
            {
                foreach (string scope in new[] { "Type", "Outer", "Field" })
                {
                    if ((shape is "Int" or "IntPtr") && scope == "Field")
                    {
                        continue;
                    }
                    yield return new TestCaseData(shape, target, scope).SetName(
                        $"GeneratedConstructorsPreservePointerFieldContexts({shape},{target},{scope})"
                    );
                }
            }
        }
    }

    private static string CreateSource(string shape, string target, string scope, bool manual)
    {
        bool pointers = shape is not "Int" and not "IntPtr";
        (string type, string setup, string compare) = shape switch
        {
            "IntPointer" => (
                "int*",
                "int value = 37; int* expected = &value;",
                "packet.Value == expected"
            ),
            "VoidPointer" => (
                "void*",
                "int value = 37; void* expected = &value;",
                "packet.Value == expected"
            ),
            "PointerPointer" => (
                "int**",
                "int value = 37; int* inner = &value; int** expected = &inner;",
                "packet.Value == expected"
            ),
            "PointerArray" => (
                "int*[]",
                "int value = 37; int*[] expected = new int*[1]; expected[0] = &value;",
                "object.ReferenceEquals(packet.Value, expected) && packet.Value[0] == &value"
            ),
            "PointerRectangle" => (
                "int*[,]",
                "int value = 37; int*[,] expected = new int*[1,1]; expected[0,0] = &value;",
                "object.ReferenceEquals(packet.Value, expected) && packet.Value[0,0] == &value"
            ),
            "PointerJagged" => (
                "int*[][]",
                "int value = 37; int*[][] expected = new int*[1][]; expected[0] = new int*[1]; expected[0][0] = &value;",
                "object.ReferenceEquals(packet.Value, expected) && packet.Value[0][0] == &value"
            ),
            "Int" => ("int", "int expected = 37;", "packet.Value == expected"),
            "IntPtr" => (
                "System.IntPtr",
                "System.IntPtr expected = new System.IntPtr(37);",
                "packet.Value == expected"
            ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(shape),
                shape,
                "Unknown pointer shape"
            ),
        };
        string unsafeType = pointers && scope == "Type" ? "unsafe " : "";
        string unsafeOuter = pointers && scope == "Outer" ? "unsafe " : "";
        string unsafeField = pointers && scope == "Field" ? "unsafe " : "";
        string declaration = target == "class" ? "partial class" : "readonly partial struct";
        string outerOpen = scope == "Outer" ? $"public {unsafeOuter}partial class Envelope {{" : "";
        string outerClose = scope == "Outer" ? "}" : "";
        string closedType = scope == "Outer" ? "Envelope.Packet" : "Packet";
        string manualConstructor = manual
            ? $"public {(pointers ? "unsafe " : "")}Packet({type} Value) {{ this.Value = Value; }}"
            : "";
        return $$"""
            namespace PointerConsumer
            {
                {{outerOpen}}
                {{(manual ? "" : "[DxMessaging.Core.Attributes.DxAutoConstructor]")}}
                public {{unsafeType}}{{declaration}} Packet
                {
                    public {{unsafeField}}readonly {{type}} Value;
                    {{manualConstructor}}
                }
                {{outerClose}}
                public static class Caller
                {
                    public static {{(pointers ? "unsafe " : "")}}int Read()
                    {
                        {{setup}}
                        {{closedType}} packet = new {{closedType}}(expected);
                        return {{compare}} ? 37 : -1;
                    }
                }
            }
            """;
    }

    private static CSharpCompilation CreateCompilation(string source, bool pointers)
    {
        CSharpCompilation compilation =
            GeneratorTestUtilities.CreateAutoConstructorCompilationFromShippedAttributes(source);
        return compilation.WithOptions(compilation.Options.WithAllowUnsafe(pointers));
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
        AssemblyLoadContext load = new("PointerConstructorConsumer", isCollectible: true);
        try
        {
            stream.Position = 0;
            Assembly assembly = load.LoadFromStream(stream);
            object? result = assembly
                .GetType("PointerConsumer.Caller")!
                .GetMethod("Read")!
                .Invoke(null, null);
            Assert.That(
                result,
                Is.EqualTo(37),
                context + ": retain original pointer or array identity without dereferencing it"
            );
        }
        finally
        {
            load.Unload();
        }
    }
}
