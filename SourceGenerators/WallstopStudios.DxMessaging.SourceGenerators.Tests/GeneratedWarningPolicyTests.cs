using System.Linq;
using Microsoft.CodeAnalysis;
using NUnit.Framework;

namespace WallstopStudios.DxMessaging.SourceGenerators.Tests;

[TestFixture]
internal sealed class GeneratedWarningPolicyTests
{
    [TestCase("auto-constructor")]
    [TestCase("message-id")]
    public void GeneratedSourceDoesNotSuppressCompilerWarnings(string generatorName)
    {
        const string autoConstructorSource = """
using DxMessaging.Core.Attributes;

namespace Sample;

[DxAutoConstructor]
public readonly partial struct StrictMessage
{
    public readonly int value;
}
""";
        const string messageIdSource = """
using DxMessaging.Core.Attributes;

namespace Sample;

[DxUntargetedMessage]
public readonly partial struct StrictMessage { }
""";
        string source =
            generatorName == "auto-constructor" ? autoConstructorSource : messageIdSource;

        GeneratorDriverRunResult result =
            generatorName == "auto-constructor"
                ? GeneratorTestUtilities.RunDxAutoConstructor(source)
                : GeneratorTestUtilities.RunDxMessageId(source);
        string[] generatedSources = result
            .Results.SelectMany(static generatorResult => generatorResult.GeneratedSources)
            .Select(static generated => generated.SourceText.ToString())
            .ToArray();

        Assert.That(
            generatedSources,
            Is.Not.Empty,
            $"The {generatorName} generator must produce output for the strict warning-policy fixture."
        );
        Assert.That(
            generatedSources,
            Has.None.Contains("#pragma warning disable"),
            $"The {generatorName} generator must expose compiler warnings instead of suppressing them."
        );
        Diagnostic[] errors = GeneratorTestUtilities
            .CompileGeneratedOutput(source, result)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.That(
            errors,
            Is.Empty,
            $"The {generatorName} output must compile under warnings-as-errors. Diagnostics:\n"
                + string.Join("\n", errors.Select(static diagnostic => diagnostic.ToString()))
        );
    }
}
