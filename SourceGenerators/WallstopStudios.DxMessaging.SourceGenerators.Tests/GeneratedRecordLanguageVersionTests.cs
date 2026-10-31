using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace WallstopStudios.DxMessaging.SourceGenerators.Tests;

[TestFixture]
internal sealed class GeneratedRecordLanguageVersionTests
{
    [Test]
    public void GeneratedReferenceDeclarationsCompileWithCSharp9(
        [Values(
            "DxAutoConstructor",
            "DxUntargetedMessage",
            "DxTargetedMessage",
            "DxBroadcastMessage"
        )]
            string attribute,
        [Values(false, true)] bool nested,
        [Values(false, true)] bool generic,
        [Values(false, true)] bool record
    )
    {
        string kind = record ? "record" : "class";
        string targetName = generic ? "Packet<TMessage>" : "Packet";
        string containerName = generic ? "Envelope<TOwner>" : "Envelope";
        string targetKind = nested ? "readonly partial struct" : "partial " + kind;
        string field = attribute == "DxAutoConstructor" ? "public readonly int Value;" : "";
        string declaration = $"[{attribute}] public {targetKind} {targetName} {{ {field} }}";
        if (nested)
        {
            declaration = $"public partial {kind} {containerName} {{ {declaration} }}";
        }
        string closedTarget = generic ? "Packet<int>" : "Packet";
        if (nested)
        {
            closedTarget = (generic ? "Envelope<object>." : "Envelope.") + closedTarget;
        }
        string consumer =
            attribute == "DxAutoConstructor"
                ? $"public static int Read() => new {closedTarget}(7).Value;"
                : $"public static System.Type Read() => ((DxMessaging.Core.IMessage)new {closedTarget}()).MessageType;";
        string source = $$"""
using DxMessaging.Core.Attributes;
namespace Sample
{
    {{declaration}}
    public static class Consumer { {{consumer}} }
}
""";
        string context =
            $"attribute={attribute}, nested={nested}, generic={generic}, record={record}";
        GeneratorDriverRunResult result =
            attribute == "DxAutoConstructor"
                ? GeneratorTestUtilities.RunDxAutoConstructor(source)
                : GeneratorTestUtilities.RunDxMessageId(source);
        Assert.That(result.Diagnostics, Is.Empty, context);
        Assert.That(
            result.Results.SelectMany(generated => generated.GeneratedSources),
            Is.Not.Empty,
            context
        );
        ImmutableArray<Diagnostic> diagnostics = GeneratorTestUtilities.CompileGeneratedOutput(
            source,
            result,
            LanguageVersion.CSharp9
        );
        Assert.That(
            diagnostics,
            Is.Empty,
            context
                + "\n"
                + string.Join("\n", diagnostics.Select(diagnostic => diagnostic.ToString()))
        );
    }
}
