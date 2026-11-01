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
internal sealed class OptionalPointerDefaultTests
{
    [TestCaseSource(nameof(Cases))]
    public void GeneratedPointerDefaultsKeepLegalNullConversions(
        string type,
        bool referenceType,
        bool expression
    )
    {
        string context = $"type={type}; referenceType={referenceType}; expression={expression}";
        AssertCallerExecutes(
            CreateCompilation(CreateSource(type, referenceType, expression, manual: true)),
            "Handwritten oracle: " + context
        );
        CSharpCompilation input = CreateCompilation(
            CreateSource(type, referenceType, expression, manual: false)
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
        Assert.That(
            diagnostics,
            Is.Empty,
            "Valid null pointer conversion: " + context + "\n" + string.Join("\n", diagnostics)
        );
        Assert.That(driver.GetRunResult().GeneratedTrees, Has.Length.EqualTo(1), context);
        AssertCallerExecutes(generated, "Generated caller: " + context);
    }

    private static IEnumerable<TestCaseData> Cases()
    {
        foreach (
            string type in new[]
            {
                "int*",
                "void*",
                "delegate*<int, int>",
                "delegate*<void>",
                "int*[]",
            }
        )
        {
            foreach (bool referenceType in new[] { false, true })
            {
                foreach (bool expression in new[] { false, true })
                {
                    yield return new TestCaseData(type, referenceType, expression).SetName(
                        $"GeneratedPointerDefaultsKeepLegalNullConversions({type},{referenceType},{expression})"
                    );
                }
            }
        }
    }

    private static string CreateSource(
        string type,
        bool referenceType,
        bool expression,
        bool manual
    )
    {
        string attribute = expression
            ? "[DxMessaging.Core.Attributes.DxOptionalParameter(Expression = \"null\")]"
            : "[DxMessaging.Core.Attributes.DxOptionalParameter(null)]";
        string constructor = manual
            ? $"public Packet(int Required, {type} Value = null) {{ this.Required = Required; this.Value = Value; }}"
            : "";
        string comparison =
            type == "int*[]" ? "packet.Value == null" : "(void*)packet.Value == null";
        return $$"""
            namespace OptionalPointerConsumer
            {
                {{(manual ? "" : "[DxMessaging.Core.Attributes.DxAutoConstructor]")}}
                public unsafe {{(
                referenceType ? "partial class" : "readonly partial struct"
            )}} Packet
                {
                    public readonly int Required;
                    {{attribute}}
                    public readonly {{type}} Value;
                    {{constructor}}
                }
                public static class Caller
                {
                    public static unsafe bool Read()
                    {
                        Packet packet = new Packet(37);
                        return packet.Required == 37 && {{comparison}};
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
        AssemblyLoadContext load = new("OptionalPointerConsumer", isCollectible: true);
        try
        {
            stream.Position = 0;
            Assembly assembly = load.LoadFromStream(stream);
            object? result = assembly
                .GetType("OptionalPointerConsumer.Caller")!
                .GetMethod("Read")!
                .Invoke(null, null);
            Assert.That(
                result,
                Is.True,
                context + ": preserve required value and null pointer default"
            );
        }
        finally
        {
            load.Unload();
        }
    }
}
