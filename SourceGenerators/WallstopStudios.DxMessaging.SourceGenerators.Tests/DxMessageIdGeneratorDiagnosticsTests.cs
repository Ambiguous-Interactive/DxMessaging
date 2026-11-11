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
internal sealed class DxMessageIdGeneratorDiagnosticsTests
{
    private static readonly string[] Il2CppPreprocessorSymbols =
    {
        "ENABLE_IL2CPP",
        "UNITY_2021_3_OR_NEWER",
    };

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
            Has.Some.Matches<Diagnostic>(d => d is not null && d.Id == "DXMSG002"),
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
            Has.Some.Matches<Diagnostic>(d => d is not null && d.Id == "DXMSG003"),
            "DXMSG003 should be reported when a nested message type lives inside a non-partial container."
        );
        Assert.That(
            diagnostics,
            Has.Some.Matches<Diagnostic>(d => d is not null && d.Id == "DXMSG004"),
            "DXMSG004 should suggest adding the partial keyword for the containing type."
        );
    }

    /*
        ------------------------------------------------------------------------------------------
        Phase D: generic message structs.
        ------------------------------------------------------------------------------------------
    */

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

    /*
        ------------------------------------------------------------------------------------------
        Phase D: record struct messages.
        ------------------------------------------------------------------------------------------
    */

    [Test]
    public void RecordStructMessageEmitsId()
    {
        /*
            Pins current contract: `[DxUntargetedMessage] partial record struct` is supported and
            produces a partial declaration whose kind is rendered as `record struct`.
        */
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

    /*
        ------------------------------------------------------------------------------------------
        Phase D: deep partial nesting.
        ------------------------------------------------------------------------------------------
    */

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
        /*
            Pins current contract: ANY non-partial link in the container chain triggers DXMSG003 +
            DXMSG004.
        */
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
            Has.Some.Matches<Diagnostic>(d => d is not null && d.Id == "DXMSG003"),
            "DXMSG003 should fire when ANY container in the chain is non-partial."
        );
        Assert.That(
            diagnostics,
            Has.Some.Matches<Diagnostic>(d => d is not null && d.Id == "DXMSG004"),
            "DXMSG004 should suggest making the non-partial container partial."
        );
    }

    /*
        ------------------------------------------------------------------------------------------
        Phase D: multiple message attributes; permutations.
        ------------------------------------------------------------------------------------------
    */

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
            Has.Some.Matches<Diagnostic>(d => d is not null && d.Id == "DXMSG002"),
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
            Has.Some.Matches<Diagnostic>(d => d is not null && d.Id == "DXMSG002"),
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
            Has.Some.Matches<Diagnostic>(d => d is not null && d.Id == "DXMSG002"),
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
            Has.Some.Matches<Diagnostic>(d => d is not null && d.Id == "DXMSG002"),
            "DXMSG002 should fire when all three Dx message attributes are present."
        );
    }

    /*
        ------------------------------------------------------------------------------------------
        Phase D: nullable annotations.
        ------------------------------------------------------------------------------------------
    */

    [Test]
    public void MessageWithNullableReferenceFieldsCompiles()
    {
        /*
            The generated partial emits `#nullable enable annotations` so the consumer's nullable
            reference fields must round-trip cleanly when the user source itself opts in via
            `#nullable enable`.
        */
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

    /*
        ------------------------------------------------------------------------------------------
        AOT bridge generation.
        ------------------------------------------------------------------------------------------
    */

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
    public void ManualInterfaceRegistrarUsesOrdinalTypeNameOrder()
    {
        string source = """
using DxMessaging.Core.Messages;

namespace Sample;

public readonly struct ZuluManual : IUntargetedMessage<ZuluManual> { }
public readonly struct AlphaManual : IUntargetedMessage<AlphaManual> { }
""";

        GeneratorDriverRunResult result = GeneratorTestUtilities.RunDxMessageId(source);
        string generated = GetGeneratedSource(result);
        int alphaIndex = generated.IndexOf(
            "__DxMessagingAotUntargetedBridge_0_global__Sample_AlphaManual",
            StringComparison.Ordinal
        );
        int zuluIndex = generated.IndexOf(
            "__DxMessagingAotUntargetedBridge_1_global__Sample_ZuluManual",
            StringComparison.Ordinal
        );

        Assert.That(result.Results[0].Diagnostics, Is.Empty);
        Assert.That(alphaIndex, Is.GreaterThanOrEqualTo(0));
        Assert.That(zuluIndex, Is.GreaterThan(alphaIndex));
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
            Has.Some.Matches<Diagnostic>(d => d is not null && d.Id == "DXMSG002")
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

    /// <summary>
    /// Compiles generated declarations and their consumers for escaped and ordinary identifiers.
    /// </summary>
    /// <remarks>
    /// 2026-10-11: Escaped identifiers previously caused invalid file hints or unescaped source.
    /// Source generation must preserve semantic names while emitting valid C# and file hints.
    /// </remarks>
    [TestCase("Sample", "Message", "", "", "value")]
    [TestCase("Sample", "@class", "", "", "value")]
    [TestCase("Sample", "Message", "@event", "", "value")]
    [TestCase("Sample", "Message", "", "@for", "value")]
    [TestCase("Sample", "Message", "Outer<@for>", "", "value")]
    [TestCase("Sample", "Message", "Outer", "", "@event")]
    [TestCase("Sample", "@class", "@event<@for>", "@while", "@switch")]
    [TestCase("@namespace", "Message", "", "", "value")]
    [TestCase("Sample", "\u03a9", "\u0394", "\u03a3", "\u03c6")]
    public void GeneratedDeclarationsPreserveEscapedIdentifiers(
        string namespaceName,
        string typeName,
        string containerName,
        string typeParameter,
        string fieldName
    )
    {
        string typeParameters = string.IsNullOrEmpty(typeParameter) ? "" : $"<{typeParameter}>";
        string containerOpen = string.IsNullOrEmpty(containerName)
            ? ""
            : $"public partial class {containerName} {{";
        string containerClose = string.IsNullOrEmpty(containerName) ? "" : "}";
        string source = $$"""
using DxMessaging.Core.Attributes;

namespace {{namespaceName}};

{{containerOpen}}
[DxUntargetedMessage]
public readonly partial struct {{typeName}}{{typeParameters}}
{
    public readonly int {{fieldName}};
    public static System.Type ReadMessageType() => new {{typeName}}{{typeParameters}}().MessageType;
}
{{containerClose}}
""";

        GeneratorDriverRunResult result = GeneratorTestUtilities.RunDxMessageId(source);
        Assert.That(
            result.Results.SelectMany(static run => run.GeneratedSources),
            Is.Not.Empty,
            "A valid attributed declaration must produce source. Diagnostics:\n"
                + string.Join(
                    "\n",
                    result.Diagnostics.Select(static diagnostic => diagnostic.ToString())
                )
                + string.Join("\n", result.Results.Select(static run => run.Exception?.ToString()))
        );
        Diagnostic[] errors = GeneratorTestUtilities
            .CompileGeneratedOutput(source, result)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.That(
            errors,
            Is.Empty,
            $"Generated output must compile for {containerName}/{typeName}/{typeParameter}/{fieldName}:\n"
                + string.Join("\n", errors.Select(static diagnostic => diagnostic.ToString()))
        );
        AssertGeneratedOutputCompilesForIl2Cpp(source);
    }

    /// <summary>
    /// Distinct legal type declarations must retain both generated member sets.
    /// </summary>
    /// <remarks>
    /// 2026-10-11: Generic delimiters previously normalized to identifier underscores,
    /// so a generic type and a matching nongeneric name could share one file hint.
    /// </remarks>
    [TestCase("T", "Packet_T_", false, false)]
    [TestCase("T", "Packet_T_", false, true)]
    [TestCase("T_U", "Packet_T_U_", false, false)]
    [TestCase("@event", "Packet_event_", false, false)]
    [TestCase("T", "Outer_T_", true, false)]
    [TestCase("T", "Outer_T_", true, true)]
    [TestCase("T", "DifferentPacket", false, false)]
    [TestCase("T", "DifferentOuter", true, true)]
    public void DistinctTypeNamesKeepGeneratedFilesSeparate(
        string typeParameter,
        string nongenericName,
        bool nested,
        bool reversed
    )
    {
        string parameters = nested ? "" : $"<{typeParameter}>";
        string first = $$"""
[DxUntargetedMessage]
public readonly partial struct Packet{{parameters}}
{
    public readonly int value;
    public static System.Type ReadGeneratedMember() => new Packet{{parameters}}().MessageType;
}
""";
        string secondType = nested ? "Packet" : nongenericName;
        string second = $$"""
[DxUntargetedMessage]
public readonly partial struct {{secondType}}
{
    public readonly int value;
    public static System.Type ReadGeneratedMember() => new {{secondType}}().MessageType;
}
""";
        if (nested)
        {
            first = $"public partial class Outer<{typeParameter}> {{ {first} }}";
            second = $"public partial class {nongenericName} {{ {second} }}";
        }
        string declarations = reversed ? second + first : first + second;
        string source = $"using DxMessaging.Core.Attributes; namespace Sample {{ {declarations} }}";
        GeneratorDriverRunResult result = GeneratorTestUtilities.RunDxMessageId(source);
        Assert.That(
            result.Results.Select(static run => run.Exception),
            Has.All.Null,
            "Distinct attributed types must not abort generation. Diagnostics:\n"
                + string.Join(
                    "\n",
                    result.Diagnostics.Select(static diagnostic => diagnostic.ToString())
                )
        );
        Diagnostic[] errors = GeneratorTestUtilities
            .CompileGeneratedOutput(source, result)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.That(
            errors,
            Is.Empty,
            $"Both generated member sets must compile for {typeParameter}/{nongenericName}/{nested}/{reversed}:\n"
                + string.Join("\n", errors.Select(static diagnostic => diagnostic.ToString()))
        );
        AssertGeneratedOutputCompilesForIl2Cpp(source);
    }

    /*
        ------------------------------------------------------------------------------------------
        Helpers.
        ------------------------------------------------------------------------------------------
    */

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
        while (0 <= (index = text.IndexOf(value, index, StringComparison.Ordinal)))
        {
            ++count;
            index += value.Length;
        }

        return count;
    }

    /// <summary>Enumerates closed message shapes across every marker and dispatch route.</summary>
    public static IEnumerable<TestCaseData> ClosedGenericMessageCases()
    {
        foreach (string kind in new[] { "Untargeted", "Targeted", "Broadcast" })
        {
            foreach (string shape in new[] { "struct", "class" })
            {
                foreach (bool attributed in new[] { false, true })
                {
                    yield return new TestCaseData(kind, shape, attributed);
                }
            }
        }
    }

    /// <summary>Roots the selected closed type before any typed runtime use.</summary>
    /// <remarks>2026-11-11: Generic definitions previously produced no startup AOT registration.</remarks>
    [TestCaseSource(nameof(ClosedGenericMessageCases))]
    public void AssemblySelectionRootsClosedGenericMessages(
        string kind,
        string shape,
        bool attributed
    )
    {
        string marker = attributed ? $"[Dx{kind}Message]" : string.Empty;
        string contract = attributed ? string.Empty : $": I{kind}Message<ClosedMessage<T>>";
        string source = $$"""
using DxMessaging.Core.Attributes;
using DxMessaging.Core.Messages;
[assembly: DxAotMessage(typeof(Sample.ClosedMessage<bool>))]
namespace Sample
{
    {{marker}}
    public partial {{shape}} ClosedMessage<T> {{contract}} { }
}
""";
        GeneratorDriverRunResult result = GeneratorTestUtilities.RunDxMessageId(source);
        string generated = GetGeneratedSource(result);
        Assert.That(result.Diagnostics, Is.Empty, $"{kind}/{shape}/attributed={attributed}");
        Assert.That(generated, Does.Contain("DxMessagingIl2CppMessageRegistrar"), source);
        Assert.That(generated, Does.Contain("typeof(global::Sample.ClosedMessage<bool>)"), source);
        string runtimeKind = kind == "Broadcast" ? "Sourced" : kind;
        Assert.That(generated, Does.Contain($"RegisterAot{runtimeKind}Bridge"), source);
        AssertGeneratedOutputCompilesForIl2Cpp(source);
    }

    /// <summary>Checks stable source output for duplicate and reordered nested closed selections.</summary>
    [Test]
    public void AssemblySelectionsDeduplicateAndSortClosedNestedMessages()
    {
        string source = """
using DxMessaging.Core.Attributes;
[assembly: DxAotMessage(typeof(Sample.Container<bool>.Message<int>))]
[assembly: DxAotMessage(typeof(Sample.Container<bool>.Message<bool>))]
[assembly: DxAotMessage(typeof(Sample.Container<bool>.Message<int>))]
namespace Sample
{
    public partial class Container<T>
    {
        [DxUntargetedMessage]
        public partial struct Message<U> { }
    }
}
""";
        GeneratorDriverRunResult result = GeneratorTestUtilities.RunDxMessageId(source);
        string generated = GetGeneratedSource(result);
        Assert.That(result.Diagnostics, Is.Empty, source);
        Assert.That(
            CountOccurrences(generated, "RegisterAotUntargetedBridge"),
            Is.EqualTo(2),
            source
        );
        Assert.That(
            generated.IndexOf(
                "typeof(global::Sample.Container<bool>.Message<bool>)",
                StringComparison.Ordinal
            ),
            Is.LessThan(
                generated.IndexOf(
                    "typeof(global::Sample.Container<bool>.Message<int>)",
                    StringComparison.Ordinal
                )
            ),
            source
        );
        string normalizedSource = source.Replace("\r\n", "\n", StringComparison.Ordinal);
        string reordered = normalizedSource.Replace(
            "[assembly: DxAotMessage(typeof(Sample.Container<bool>.Message<int>))]\n[assembly: DxAotMessage(typeof(Sample.Container<bool>.Message<bool>))]\n[assembly: DxAotMessage(typeof(Sample.Container<bool>.Message<int>))]",
            "[assembly: DxAotMessage(typeof(Sample.Container<bool>.Message<bool>))]\n[assembly: DxAotMessage(typeof(Sample.Container<bool>.Message<int>))]",
            StringComparison.Ordinal
        );
        Assert.That(
            reordered,
            Is.Not.EqualTo(normalizedSource),
            "The ordering control must actually change the assembly selections."
        );
        Assert.That(
            GetGeneratedSource(GeneratorTestUtilities.RunDxMessageId(reordered)),
            Is.EqualTo(generated),
            "Duplicate multiplicity and assembly attribute order must not change the registrar."
        );
        AssertGeneratedOutputCompilesForIl2Cpp(source);
    }

    /// <summary>Checks the diagnostic for open, abstract, ref-like and non-message selections.</summary>
    [TestCase("typeof(Sample.Message<>)")]
    [TestCase("typeof(int)")]
    [TestCase("typeof(Sample.AbstractMessage<int>)")]
    [TestCase("typeof(Sample.IMessage<int>)")]
    [TestCase("null")]
    public void InvalidAssemblySelectionReportsDiagnostic(string expression)
    {
        string source = $$"""
using DxMessaging.Core.Attributes;
using DxMessaging.Core.Messages;
[assembly: DxAotMessage({{expression}})]
namespace Sample
{
    [DxUntargetedMessage] public partial struct Message<T> { }
    public abstract class AbstractMessage<T> : IUntargetedMessage { }
    public interface IMessage<T> : IUntargetedMessage { }
}
""";
        GeneratorDriverRunResult result = GeneratorTestUtilities.RunDxMessageId(source);
        Assert.That(
            result.Diagnostics,
            Has.Exactly(1).Matches<Diagnostic>(d => d is not null && d.Id == "DXMSG014"),
            expression
        );
        Assert.That(
            GetGeneratedSource(result),
            Does.Not.Contain("DxMessagingIl2CppMessageRegistrar"),
            expression
        );
    }

    /// <summary>Checks one bridge per implemented route for a multi-interface selection.</summary>
    [Test]
    public void AssemblySelectionRootsEveryImplementedRouteOnce()
    {
        string source = """
using DxMessaging.Core.Attributes;
using DxMessaging.Core.Messages;
[assembly: DxAotMessage(typeof(Sample.Multi<int[]>))]
[assembly: DxAotMessage(typeof(Sample.Multi<int[]>))]
namespace Sample
{
    public struct Multi<T> : IUntargetedMessage<Multi<T>>, ITargetedMessage<Multi<T>>, IBroadcastMessage<Multi<T>>
    { public System.Type MessageType => typeof(Multi<T>); }
}
""";
        GeneratorDriverRunResult result = GeneratorTestUtilities.RunDxMessageId(source);
        string generated = GetGeneratedSource(result);
        Assert.That(result.Diagnostics, Is.Empty, source);
        foreach (string kind in new[] { "Untargeted", "Targeted", "Sourced" })
        {
            Assert.That(
                CountOccurrences(generated, $"RegisterAot{kind}Bridge"),
                Is.EqualTo(1),
                kind
            );
        }
        AssertGeneratedOutputCompilesForIl2Cpp(source);
    }

    /// <summary>Checks that explicit selection preserves the existing single non-generic startup registration.</summary>
    [Test]
    public void AssemblySelectionDoesNotDuplicateOrdinaryAttributedBridge()
    {
        string source = """
using DxMessaging.Core.Attributes;
[assembly: DxAotMessage(typeof(Sample.Ordinary))]
namespace Sample
{
    [DxUntargetedMessage] public partial struct Ordinary { }
}
""";
        GeneratorDriverRunResult result = GeneratorTestUtilities.RunDxMessageId(source);
        string generated = GetGeneratedSource(result);
        Assert.That(result.Diagnostics, Is.Empty, source);
        Assert.That(
            CountOccurrences(generated, "RegisterAotUntargetedBridge"),
            Is.EqualTo(1),
            source
        );
        Assert.That(generated, Does.Not.Contain("DxMessagingIl2CppMessageRegistrar"), source);
        AssertGeneratedOutputCompilesForIl2Cpp(source);
    }

    /// <summary>Checks that inaccessible containing types or generic arguments cannot produce invalid registrar code.</summary>
    [TestCase("Sample.Container.PrivateMessage<int>")]
    [TestCase("Sample.Container.PublicMessage<Sample.Container.PrivateArgument>")]
    public void InaccessibleAssemblySelectionsReportDiagnostic(string selected)
    {
        string source = $$"""
using DxMessaging.Core.Attributes;
using DxMessaging.Core.Messages;
[assembly: DxAotMessage(typeof({{selected}}))]
namespace Sample
{
    public class Container
    {
        private class PrivateArgument { }
        private struct PrivateMessage<T> : IUntargetedMessage<PrivateMessage<T>> { }
        public struct PublicMessage<T> : IUntargetedMessage<PublicMessage<T>> { }
    }
}
""";
        GeneratorDriverRunResult result = GeneratorTestUtilities.RunDxMessageId(source);
        Assert.That(
            result.Diagnostics,
            Has.Exactly(1).Matches<Diagnostic>(d => d is not null && d.Id == "DXMSG014"),
            selected
        );
        Assert.That(
            GetGeneratedSource(result),
            Does.Not.Contain("DxMessagingIl2CppMessageRegistrar"),
            selected
        );
    }

    /// <summary>Checks closed registration in an assembly containing only selections for externally declared messages.</summary>
    [Test]
    public void AssemblyOnlyConsumerRootsReferencedGenericMessage()
    {
        PortableExecutableReference referencedAssembly =
            GeneratorTestUtilities.CompileBaseCallAnalyzerParent(
                """
namespace External
{
    public readonly struct Message<T> : DxMessaging.Core.Messages.IUntargetedMessage<Message<T>> { }
}
"""
            );
        CSharpParseOptions options = new(
            LanguageVersion.CSharp9,
            preprocessorSymbols: Il2CppPreprocessorSymbols
        );
        string source =
            "[assembly: DxMessaging.Core.Attributes.DxAotMessage(typeof(External.Message<bool>))]";
        CSharpCompilation compilation = CSharpCompilation.Create(
            "AssemblyOnlyConsumer",
            new[] { CSharpSyntaxTree.ParseText(source, options) },
            GeneratorTestUtilities.CompilationReferences.Add(referencedAssembly),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                generalDiagnosticOption: ReportDiagnostic.Error
            )
        );
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new DxMessageIdGenerator() },
            parseOptions: options
        );
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out Compilation output,
            out System.Collections.Immutable.ImmutableArray<Diagnostic> diagnostics
        );
        Assert.That(
            diagnostics,
            Is.Empty,
            "A referenced closed message must be accepted without any local type declarations."
        );
        Assert.That(
            output.GetDiagnostics(),
            Is.Empty,
            "The cross-assembly startup registrar must compile without warnings."
        );
        Assert.That(
            GetGeneratedSource(driver.GetRunResult()),
            Does.Contain("typeof(global::External.Message<bool>)"),
            "The registrar must root the selected external closed type."
        );
    }

    /// <summary>Rejects metadata-only markers whose referenced assembly never generated the required route interface.</summary>
    /// <remarks>2026-11-11: A referenced assembly cannot receive a missing interface from this consumer's generator.</remarks>
    [TestCase("Untargeted", "", false)]
    [TestCase("Targeted", "", false)]
    [TestCase("Broadcast", "", false)]
    [TestCase("Untargeted", "Untargeted", true)]
    [TestCase("Targeted", "Targeted", true)]
    [TestCase("Broadcast", "Broadcast", true)]
    [TestCase("Untargeted", "Targeted", false)]
    [TestCase("Targeted", "Broadcast", false)]
    [TestCase("Broadcast", "Untargeted", false)]
    public void ReferencedMarkedMessagesRequireTheirActualRoute(
        string marker,
        string implemented,
        bool valid
    )
    {
        string inheritance =
            implemented.Length == 0
                ? ""
                : $" : DxMessaging.Core.Messages.I{implemented}Message<MarkedMessage<T>>";
        PortableExecutableReference external = GeneratorTestUtilities.CompileBaseCallAnalyzerParent(
            $"namespace External {{ [DxMessaging.Core.Attributes.Dx{marker}Message] public readonly struct MarkedMessage<T>{inheritance} {{ }} }}"
        );
        CSharpParseOptions options = new(
            LanguageVersion.CSharp9,
            preprocessorSymbols: Il2CppPreprocessorSymbols
        );
        CSharpCompilation compilation = CSharpCompilation.Create(
            "ExternalMarkedConsumer",
            new[]
            {
                CSharpSyntaxTree.ParseText(
                    "[assembly: DxMessaging.Core.Attributes.DxAotMessage(typeof(External.MarkedMessage<bool>))]",
                    options
                ),
            },
            GeneratorTestUtilities.CompilationReferences.Add(external),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                generalDiagnosticOption: ReportDiagnostic.Error
            )
        );
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new DxMessageIdGenerator() },
            parseOptions: options
        );
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out Compilation output,
            out System.Collections.Immutable.ImmutableArray<Diagnostic> diagnostics
        );
        if (valid)
        {
            Assert.That(
                diagnostics,
                Is.Empty,
                $"{marker}/{implemented}: an external implemented route must be accepted."
            );
            Assert.That(
                GetGeneratedSource(driver.GetRunResult()),
                Does.Contain("typeof(global::External.MarkedMessage<bool>)"),
                "The accepted external route must retain its startup root."
            );
        }
        else
        {
            Assert.That(
                diagnostics,
                Has.Exactly(1).Matches<Diagnostic>(d => d != null && d.Id == "DXMSG014"),
                $"{marker}/{implemented}: an unimplemented external marker must report DXMSG014."
            );
            Assert.That(
                GetGeneratedSource(driver.GetRunResult()),
                Does.Not.Contain("DxMessagingIl2CppMessageRegistrar"),
                "Rejected external types must not produce invalid bridges."
            );
        }
        Assert.That(
            output.GetDiagnostics(),
            Is.Empty,
            $"{marker}/{implemented}: generated consumer output must compile cleanly."
        );
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
        /*
            Walk every generated tree, concatenate text, then assert. Roslyn's GeneratedSources is
            an ImmutableArray<GeneratedSourceResult>; we don't care about the partition here.
        */
        string joined = GetGeneratedSource(result);
        Assert.That(joined, Does.Contain(fragment), failureMessage);
    }

    private static void AssertGeneratedSourceParses(GeneratorDriverRunResult result)
    {
        /*
            Parse the generator's output as standalone trees and assert they have no syntax errors.
            This catches indentation, accessibility, and type-param mismatch regressions that surface
            as parse-level diagnostics, but does NOT perform a full compile against referenced types.
        */
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
