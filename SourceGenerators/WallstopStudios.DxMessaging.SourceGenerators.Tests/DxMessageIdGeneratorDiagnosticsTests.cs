using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using NUnit.Framework;
using WallstopStudios.DxMessaging.SourceGenerators;
using WallstopStudios.DxMessaging.SourceGenerators.Analyzers;

namespace WallstopStudios.DxMessaging.SourceGenerators.Tests;

[TestFixture]
public sealed class DxMessageIdGeneratorDiagnosticsTests
{
    [Test]
    public void GeneratorsUseUnity2021CompatibleClassicSourceGeneratorApi()
    {
        Assert.That(new DxMessageIdGenerator(), Is.AssignableTo<ISourceGenerator>());
        Assert.That(new DxAutoConstructorGenerator(), Is.AssignableTo<ISourceGenerator>());
        Assert.That(new DxMessageIdGenerator(), Is.Not.AssignableTo<IIncrementalGenerator>());
        Assert.That(new DxAutoConstructorGenerator(), Is.Not.AssignableTo<IIncrementalGenerator>());
    }

    [TestCase(typeof(DxMessageIdGenerator), "source generator")]
    [TestCase(typeof(MessageAwareComponentBaseCallAnalyzer), "analyzer")]
    public void ShippedCompilerHostAssembliesReferenceUnity2021CompatibleRoslyn(
        Type compilerHostType,
        string label
    )
    {
        Version expectedVersion = new(3, 8, 0, 0);
        AssemblyName[] references = compilerHostType.Assembly.GetReferencedAssemblies();
        Version? codeAnalysisVersion = references
            .Single(reference => reference.Name == "Microsoft.CodeAnalysis")
            .Version;
        Version? csharpVersion = references
            .Single(reference => reference.Name == "Microsoft.CodeAnalysis.CSharp")
            .Version;

        Assert.Multiple(() =>
        {
            Assert.That(codeAnalysisVersion, Is.EqualTo(expectedVersion), label);
            Assert.That(csharpVersion, Is.EqualTo(expectedVersion), label);
        });
    }

    [Test]
    public void ReportsMultipleMessageAttributes()
    {
        string source = """
using DxMessaging.Core.Attributes;
using DxMessaging.Core.Messages;

namespace Sample;

[DxUntargetedMessage]
public readonly partial struct ConflictingMessage : IUntargetedMessage { }

[DxBroadcastMessage]
public readonly partial struct ConflictingMessage : IBroadcastMessage { }
""";

        GeneratorDriverRunResult result = GeneratorTestUtilities.RunDxMessageId(source);
        Diagnostic[] diagnostics = result.Results[0].Diagnostics.ToArray();

        Assert.That(
            diagnostics,
            Has.Some.Matches<Diagnostic>(d => d.Id == "DXMSG002"),
            "DXMSG002 should be reported when a message type has multiple Dx message attributes."
        );
    }

    [Test]
    public void ReportsNonPartialContainerForMessageIds()
    {
        string source = """
using DxMessaging.Core.Attributes;
using DxMessaging.Core.Messages;

namespace Sample;

public class Container
{
    [DxTargetedMessage]
    public readonly struct NestedMessage : ITargetedMessage { }
}
""";

        GeneratorDriverRunResult result = GeneratorTestUtilities.RunDxMessageId(source);
        Diagnostic[] diagnostics = result.Results[0].Diagnostics.ToArray();

        Assert.That(
            diagnostics,
            Has.Some.Matches<Diagnostic>(d => d.Id == "DXMSG003"),
            "DXMSG003 should be reported when a nested message type lives inside a non-partial container."
        );
        Assert.That(
            diagnostics,
            Has.Some.Matches<Diagnostic>(d => d.Id == "DXMSG004"),
            "DXMSG004 should suggest adding the partial keyword for the containing type."
        );
    }

    // ------------------------------------------------------------------------------------------
    // Phase D: generic message structs.
    // ------------------------------------------------------------------------------------------

    [Test]
    public void GenericMessageStructEmitsId()
    {
        string source = """
using DxMessaging.Core.Attributes;

namespace Sample;

[DxUntargetedMessage]
public readonly partial struct MyMessage<T> { }
""";

        GeneratorDriverRunResult result = GeneratorTestUtilities.RunDxMessageId(source);

        Assert.That(
            result.Results[0].Diagnostics,
            Is.Empty,
            "A generic message struct must not produce diagnostics."
        );
        AssertGeneratedSourceContains(
            result,
            "MyMessage<T>",
            "Generic message struct should generate a partial declaration with its type parameter."
        );
        AssertGeneratedSourceParses(result);
    }

    [Test]
    public void GenericMessageWithMultipleTypeParametersEmitsId()
    {
        string source = """
using DxMessaging.Core.Attributes;

namespace Sample;

[DxBroadcastMessage]
public readonly partial struct MyMessage<T1, T2> { }
""";

        GeneratorDriverRunResult result = GeneratorTestUtilities.RunDxMessageId(source);

        Assert.That(
            result.Results[0].Diagnostics,
            Is.Empty,
            "A multi-parameter generic message struct must not produce diagnostics."
        );
        AssertGeneratedSourceContains(
            result,
            "MyMessage<T1, T2>",
            "Generic message struct should generate a partial declaration with all of its type parameters."
        );
        AssertGeneratedSourceParses(result);
    }

    [Test]
    public void GenericMessageWithConstraintEmitsId()
    {
        string source = """
using DxMessaging.Core.Attributes;

namespace Sample;

[DxUntargetedMessage]
public readonly partial struct MyMessage<T> where T : struct { }
""";

        GeneratorDriverRunResult result = GeneratorTestUtilities.RunDxMessageId(source);

        Assert.That(
            result.Results[0].Diagnostics,
            Is.Empty,
            "A generic message struct with a constraint must not produce diagnostics."
        );
        AssertGeneratedSourceContains(
            result,
            "MyMessage<T>",
            "Generic message struct should generate the partial declaration with its type parameter."
        );
        AssertGeneratedSourceParses(result);
    }

    // ------------------------------------------------------------------------------------------
    // Phase D: record struct messages.
    // ------------------------------------------------------------------------------------------

    [Test]
    public void RecordStructMessageEmitsId()
    {
        // Pins current contract: `[DxUntargetedMessage] partial record struct` is supported and
        // produces a partial declaration whose kind is rendered as `record struct`.
        string source = """
using DxMessaging.Core.Attributes;

namespace Sample;

[DxUntargetedMessage]
public readonly partial record struct MyRecordMessage(int Value);
""";

        GeneratorDriverRunResult result = GeneratorTestUtilities.RunDxMessageId(source);

        Assert.That(
            result.Results[0].Diagnostics,
            Is.Empty,
            "A record struct message must not produce diagnostics."
        );
        AssertGeneratedSourceContains(
            result,
            "record struct MyRecordMessage",
            "The generator should emit a partial record struct declaration."
        );
        AssertGeneratedSourceParses(result);
    }

    // ------------------------------------------------------------------------------------------
    // Phase D: deep partial nesting.
    // ------------------------------------------------------------------------------------------

    [Test]
    public void MessageInThreeLevelNestedPartialContainerEmitsId()
    {
        string source = """
using DxMessaging.Core.Attributes;

namespace Sample;

public partial class A
{
    public partial class B
    {
        public partial class C
        {
            [DxUntargetedMessage]
            public readonly partial struct M { }
        }
    }
}
""";

        GeneratorDriverRunResult result = GeneratorTestUtilities.RunDxMessageId(source);

        Assert.That(
            result.Results[0].Diagnostics,
            Is.Empty,
            "A message nested in three partial containers must not produce diagnostics."
        );
        AssertGeneratedSourceContains(
            result,
            "partial struct M",
            "The generator should emit a partial declaration for the inner message type."
        );
        AssertGeneratedSourceContains(
            result,
            "partial class A",
            "The generator should re-open the outermost container as partial."
        );
        AssertGeneratedSourceParses(result);
    }

    [Test]
    public void MessageInNonPartialContainerEmitsDiagnostic()
    {
        // Pins current contract: ANY non-partial link in the container chain triggers DXMSG003 +
        // DXMSG004.
        string source = """
using DxMessaging.Core.Attributes;

namespace Sample;

public partial class A
{
    public class B
    {
        public partial class C
        {
            [DxUntargetedMessage]
            public readonly partial struct M { }
        }
    }
}
""";

        GeneratorDriverRunResult result = GeneratorTestUtilities.RunDxMessageId(source);
        Diagnostic[] diagnostics = result.Results[0].Diagnostics.ToArray();

        Assert.That(
            diagnostics,
            Has.Some.Matches<Diagnostic>(d => d.Id == "DXMSG003"),
            "DXMSG003 should fire when ANY container in the chain is non-partial."
        );
        Assert.That(
            diagnostics,
            Has.Some.Matches<Diagnostic>(d => d.Id == "DXMSG004"),
            "DXMSG004 should suggest making the non-partial container partial."
        );
    }

    // ------------------------------------------------------------------------------------------
    // Phase D: multiple message attributes; permutations.
    // ------------------------------------------------------------------------------------------

    [Test]
    public void MultipleMessageAttributesUntargetedAndTargetedEmitsDxmsg002()
    {
        string source = """
using DxMessaging.Core.Attributes;

namespace Sample;

[DxUntargetedMessage]
[DxTargetedMessage]
public readonly partial struct Conflicting { }
""";

        GeneratorDriverRunResult result = GeneratorTestUtilities.RunDxMessageId(source);
        Diagnostic[] diagnostics = result.Results[0].Diagnostics.ToArray();

        Assert.That(
            diagnostics,
            Has.Some.Matches<Diagnostic>(d => d.Id == "DXMSG002"),
            "DXMSG002 should fire for [DxUntargetedMessage] + [DxTargetedMessage]."
        );
    }

    [Test]
    public void MultipleMessageAttributesUntargetedAndBroadcastEmitsDxmsg002()
    {
        string source = """
using DxMessaging.Core.Attributes;

namespace Sample;

[DxUntargetedMessage]
[DxBroadcastMessage]
public readonly partial struct Conflicting { }
""";

        GeneratorDriverRunResult result = GeneratorTestUtilities.RunDxMessageId(source);
        Diagnostic[] diagnostics = result.Results[0].Diagnostics.ToArray();

        Assert.That(
            diagnostics,
            Has.Some.Matches<Diagnostic>(d => d.Id == "DXMSG002"),
            "DXMSG002 should fire for [DxUntargetedMessage] + [DxBroadcastMessage]."
        );
    }

    [Test]
    public void MultipleMessageAttributesTargetedAndBroadcastEmitsDxmsg002()
    {
        string source = """
using DxMessaging.Core.Attributes;

namespace Sample;

[DxTargetedMessage]
[DxBroadcastMessage]
public readonly partial struct Conflicting { }
""";

        GeneratorDriverRunResult result = GeneratorTestUtilities.RunDxMessageId(source);
        Diagnostic[] diagnostics = result.Results[0].Diagnostics.ToArray();

        Assert.That(
            diagnostics,
            Has.Some.Matches<Diagnostic>(d => d.Id == "DXMSG002"),
            "DXMSG002 should fire for [DxTargetedMessage] + [DxBroadcastMessage]."
        );
    }

    [Test]
    public void MultipleMessageAttributesAllThreeEmitsDxmsg002()
    {
        string source = """
using DxMessaging.Core.Attributes;

namespace Sample;

[DxUntargetedMessage]
[DxTargetedMessage]
[DxBroadcastMessage]
public readonly partial struct Conflicting { }
""";

        GeneratorDriverRunResult result = GeneratorTestUtilities.RunDxMessageId(source);
        Diagnostic[] diagnostics = result.Results[0].Diagnostics.ToArray();

        Assert.That(
            diagnostics,
            Has.Some.Matches<Diagnostic>(d => d.Id == "DXMSG002"),
            "DXMSG002 should fire when all three Dx message attributes are present."
        );
    }

    // ------------------------------------------------------------------------------------------
    // Phase D: nullable annotations.
    // ------------------------------------------------------------------------------------------

    [Test]
    public void MessageWithNullableReferenceFieldsCompiles()
    {
        // The generated partial emits `#nullable enable annotations` so the consumer's nullable
        // reference fields must round-trip cleanly when the user source itself opts in via
        // `#nullable enable`.
        string source = """
#nullable enable
using DxMessaging.Core.Attributes;

namespace Sample;

[DxUntargetedMessage]
public partial struct M
{
    public string? Optional;
    public int Required;
}
""";

        GeneratorDriverRunResult result = GeneratorTestUtilities.RunDxMessageId(source);

        Assert.That(
            result.Results[0].Diagnostics,
            Is.Empty,
            "A message struct with nullable reference fields must not produce diagnostics."
        );
        AssertGeneratedSourceContains(
            result,
            "partial struct M",
            "The generator should emit a partial declaration even when the user struct has nullable fields."
        );
        AssertGeneratedSourceParses(result);
    }

    // ------------------------------------------------------------------------------------------
    // AOT bridge generation.
    // ------------------------------------------------------------------------------------------

    [Test]
    public void AttributedConcreteMessagesEmitIl2CppAotBridges()
    {
        string source = """
using DxMessaging.Core.Attributes;

namespace Sample;

[DxUntargetedMessage]
public readonly partial struct AttributedUntargeted { }

[DxTargetedMessage]
public sealed partial class AttributedTargeted { }

[DxBroadcastMessage]
public readonly partial struct AttributedBroadcast { }
""";

        GeneratorDriverRunResult result = GeneratorTestUtilities.RunDxMessageId(source);
        string generated = GetGeneratedSource(result);

        Assert.That(result.Results[0].Diagnostics, Is.Empty);
        Assert.That(
            generated,
            Does.Contain("#if ENABLE_IL2CPP && UNITY_2021_3_OR_NEWER"),
            "AOT bridge code must stay behind IL2CPP/Unity guards."
        );
        Assert.That(generated, Does.Contain("RegisterAotUntargetedBridge"));
        Assert.That(generated, Does.Contain("RegisterAotTargetedBridge"));
        Assert.That(generated, Does.Contain("RegisterAotSourcedBridge"));
        AssertGeneratedOutputCompilesForIl2Cpp(source);
    }

    [Test]
    public void ManualInterfaceMessagesEmitTopLevelIl2CppRegistrar()
    {
        string source = """
using DxMessaging.Core.Messages;

namespace Sample;

public readonly struct ManualUntargeted : IUntargetedMessage<ManualUntargeted> { }

internal sealed class ManualTargeted : ITargetedMessage<ManualTargeted> { }

public readonly struct ManualBroadcast : IBroadcastMessage<ManualBroadcast> { }
""";

        GeneratorDriverRunResult result = GeneratorTestUtilities.RunDxMessageId(source);
        string generated = GetGeneratedSource(result);

        Assert.That(result.Results[0].Diagnostics, Is.Empty);
        Assert.That(generated, Does.Contain("DxMessagingIl2CppMessageRegistrar"));
        Assert.That(generated, Does.Contain("RegisterAotUntargetedBridge"));
        Assert.That(generated, Does.Contain("RegisterAotTargetedBridge"));
        Assert.That(generated, Does.Contain("RegisterAotSourcedBridge"));
        AssertGeneratedOutputCompilesForIl2Cpp(source);
    }

    [Test]
    public void OpenGenericMessagesDoNotEmitIl2CppAotBridges()
    {
        string source = """
using DxMessaging.Core.Attributes;
using DxMessaging.Core.Messages;

namespace Sample;

[DxUntargetedMessage]
public readonly partial struct AttributedGeneric<T> { }

public readonly struct ManualGeneric<T> : IUntargetedMessage<ManualGeneric<T>> { }
""";

        GeneratorDriverRunResult result = GeneratorTestUtilities.RunDxMessageId(source);
        string generated = GetGeneratedSource(result);

        Assert.That(result.Results[0].Diagnostics, Is.Empty);
        Assert.That(
            generated,
            Does.Not.Contain("RuntimeInitializeOnLoadMethod"),
            "Open generic definitions cannot root a concrete IL2CPP bridge."
        );
        Assert.That(generated, Does.Not.Contain("RegisterAotUntargetedBridge"));
        AssertGeneratedOutputCompilesForIl2Cpp(source);
    }

    [Test]
    public void ConflictingMessageAttributesDoNotEmitIl2CppAotRegistrar()
    {
        string source = """
using DxMessaging.Core.Attributes;

namespace Sample;

[DxUntargetedMessage]
[DxTargetedMessage]
public readonly partial struct Conflicting { }
""";

        GeneratorDriverRunResult result = GeneratorTestUtilities.RunDxMessageId(source);
        string generated = GetGeneratedSource(result);

        Assert.That(
            result.Results[0].Diagnostics,
            Has.Some.Matches<Diagnostic>(d => d.Id == "DXMSG002")
        );
        Assert.That(generated, Does.Not.Contain("DxMessagingIl2CppMessageRegistrar"));
        Assert.That(generated, Does.Not.Contain("RegisterAot"));
    }

    [Test]
    public void ManualMessageWithMultipleInterfacesEmitsEachAotBridgeOnce()
    {
        string source = """
using System;
using DxMessaging.Core.Messages;

namespace Sample;

public readonly struct Omnibus : IUntargetedMessage, ITargetedMessage, IBroadcastMessage
{
    public Type MessageType => typeof(Omnibus);
}
""";

        GeneratorDriverRunResult result = GeneratorTestUtilities.RunDxMessageId(source);
        string generated = GetGeneratedSource(result);

        Assert.That(result.Results[0].Diagnostics, Is.Empty);
        Assert.That(CountOccurrences(generated, "RegisterAotUntargetedBridge"), Is.EqualTo(1));
        Assert.That(CountOccurrences(generated, "RegisterAotTargetedBridge"), Is.EqualTo(1));
        Assert.That(CountOccurrences(generated, "RegisterAotSourcedBridge"), Is.EqualTo(1));
        AssertGeneratedOutputCompilesForIl2Cpp(source);
    }

    // ------------------------------------------------------------------------------------------
    // Helpers.
    // ------------------------------------------------------------------------------------------

    private static string GetGeneratedSource(GeneratorDriverRunResult result)
    {
        return string.Join(
            "\n",
            result.Results.SelectMany(r => r.GeneratedSources).Select(g => g.SourceText.ToString())
        );
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            ++count;
            index += value.Length;
        }

        return count;
    }

    private static void AssertGeneratedOutputCompilesForIl2Cpp(string source)
    {
        Diagnostic[] errors = GeneratorTestUtilities
            .CompileDxMessageIdGeneratedOutput(source, "ENABLE_IL2CPP", "UNITY_2021_3_OR_NEWER")
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();

        Assert.That(
            errors,
            Is.Empty,
            "Generated IL2CPP output must compile cleanly. Errors:\n"
                + string.Join("\n", errors.Select(static diagnostic => diagnostic.ToString()))
        );
    }

    private static void AssertGeneratedSourceContains(
        GeneratorDriverRunResult result,
        string fragment,
        string failureMessage
    )
    {
        // Walk every generated tree, concatenate text, then assert. Roslyn's GeneratedSources is
        // an ImmutableArray<GeneratedSourceResult>; we don't care about the partition here.
        string joined = GetGeneratedSource(result);
        Assert.That(joined, Does.Contain(fragment), failureMessage);
    }

    private static void AssertGeneratedSourceParses(GeneratorDriverRunResult result)
    {
        // Parse the generator's output as standalone trees and assert they have no syntax errors.
        // This catches indentation, accessibility, and type-param mismatch regressions that surface
        // as parse-level diagnostics, but does NOT perform a full compile against referenced types.
        Assert.That(
            result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error),
            Is.Empty,
            "Generator must not surface compile-error diagnostics."
        );

        foreach (GeneratorRunResult r in result.Results)
        {
            foreach (GeneratedSourceResult g in r.GeneratedSources)
            {
                SyntaxTree tree = CSharpSyntaxTree.ParseText(g.SourceText);
                Assert.That(
                    tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error),
                    Is.Empty,
                    $"Generated file '{g.HintName}' has parse errors."
                );
            }
        }
    }
}
