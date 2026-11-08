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
internal sealed class FunctionPointerConstructorContextTests
{
    [TestCaseSource(nameof(Cases))]
    public void GeneratedConstructorsPreserveFunctionPointerContexts(
        bool returnsVoid,
        string target,
        string scope
    )
    {
        string context = $"returnsVoid={returnsVoid}; target={target}; scope={scope}";
        AssertCallerExecutes(
            CreateCompilation(CreateSource(returnsVoid, target, scope, manual: true)),
            "Handwritten oracle: " + context
        );
        CSharpCompilation input = CreateCompilation(
            CreateSource(returnsVoid, target, scope, manual: false)
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
        foreach (bool returnsVoid in new[] { false, true })
        {
            foreach (string target in new[] { "class", "readonly struct" })
            {
                foreach (string scope in new[] { "Type", "Outer", "Field" })
                {
                    yield return new TestCaseData(returnsVoid, target, scope).SetName(
                        $"GeneratedConstructorsPreserveFunctionPointerContexts({returnsVoid},{target},{scope})"
                    );
                }
            }
        }
    }

    private static string CreateSource(bool returnsVoid, string target, string scope, bool manual)
    {
        string type = returnsVoid ? "delegate*<void>" : "delegate*<int, int>";
        string unsafeType = scope == "Type" ? "unsafe " : "";
        string unsafeOuter = scope == "Outer" ? "unsafe " : "";
        string unsafeField = scope == "Field" ? "unsafe " : "";
        string declaration = target == "class" ? "partial class" : "readonly partial struct";
        string outerOpen = scope == "Outer" ? $"public {unsafeOuter}partial class Envelope {{" : "";
        string outerClose = scope == "Outer" ? "}" : "";
        string closedType = scope == "Outer" ? "Envelope.Packet" : "Packet";
        string manualConstructor = manual
            ? $"public unsafe Packet({type} Value) {{ this.Value = Value; }}"
            : "";
        string function = returnsVoid
            ? "private static void Identity() { }"
            : "private static int Identity(int value) => value;";
        return $$"""
            namespace FunctionPointerConsumer
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
                    {{function}}
                    public static unsafe int Read()
                    {
                        {{type}} expected = &Identity;
                        {{closedType}} packet = new {{closedType}}(expected);
                        return (void*)packet.Value == (void*)expected ? 37 : -1;
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
        AssemblyLoadContext load = new("FunctionPointerConstructorConsumer", isCollectible: true);
        try
        {
            stream.Position = 0;
            Assembly assembly = load.LoadFromStream(stream);
            object? result = assembly
                .GetType("FunctionPointerConsumer.Caller")!
                .GetMethod("Read")!
                .Invoke(null, null);
            Assert.That(
                result,
                Is.EqualTo(37),
                context + ": retain function pointer identity without invoking it"
            );
        }
        finally
        {
            load.Unload();
        }
    }
}
