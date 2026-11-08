using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using NUnit.Framework;
using WallstopStudios.DxMessaging.SourceGenerators;

namespace WallstopStudios.DxMessaging.SourceGenerators.Tests;

[TestFixture]
internal sealed class GeneratedInterfaceContainerTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.CSharp9);
    private static readonly ImmutableArray<SyntaxTree> ShippedContracts = ReadShippedContracts();

    [TestCaseSource(nameof(ContainerCases))]
    public void GeneratedMembersPreserveLegalContainingDeclarations(
        string container,
        string target,
        bool constructor,
        string? message
    )
    {
        string context =
            $"container={container}, target={target}, constructor={constructor}, message={message ?? "none"}";
        string manual = CreateSource(container, target, constructor, message, manual: true);
        AssertCompilationExecutes(CreateCompilation(manual), "Handwritten oracle: " + context);

        string source = CreateSource(container, target, constructor, message, manual: false);
        GeneratorDriver driver = CreateDriver();
        driver = driver.RunGeneratorsAndUpdateCompilation(
            CreateCompilation(source),
            out Compilation generated,
            out ImmutableArray<Diagnostic> diagnostics
        );
        GeneratorDriverRunResult result = driver.GetRunResult();
        Assert.That(result.Results.Select(run => run.Exception), Has.All.Null, context);
        Assert.That(diagnostics, Is.Empty, context + "\n" + string.Join("\n", diagnostics));
        Assert.That(
            result.GeneratedTrees,
            Has.Length.EqualTo((constructor ? 1 : 0) + (message == null ? 0 : 1)),
            "Generate every requested member set: " + context
        );
        AssertCompilationExecutes(generated, "Generated caller: " + context);
    }

    [TestCaseSource(nameof(AttributeCases))]
    public void NonpartialInterfaceRetainsTheContainerDiagnostics(bool constructor, string? message)
    {
        string context = $"constructor={constructor}, message={message ?? "none"}";
        string source = CreateSource(
                "Interface",
                "readonly struct",
                constructor,
                message,
                manual: false
            )
            .Replace("partial interface Envelope", "interface Envelope", StringComparison.Ordinal);
        GeneratorDriver driver = CreateDriver().RunGenerators(CreateCompilation(source));
        GeneratorDriverRunResult result = driver.GetRunResult();
        Diagnostic[] diagnostics = result.Diagnostics.ToArray();
        Assert.That(result.Results.Select(run => run.Exception), Has.All.Null, context);
        Assert.That(
            result.GeneratedTrees,
            Is.Empty,
            "Do not emit an invalid container: " + context
        );
        Assert.That(diagnostics.Select(d => d.Id), Does.Contain("DXMSG003"), context);
        Assert.That(diagnostics.Select(d => d.Id), Does.Contain("DXMSG004"), context);
        Assert.That(
            diagnostics.All(d => d.Id is "DXMSG003" or "DXMSG004"),
            Is.True,
            "Retain only the declared container diagnostics: "
                + context
                + "\n"
                + string.Join("\n", diagnostics.AsEnumerable())
        );
    }

    private static IEnumerable<TestCaseData> ContainerCases()
    {
        foreach (
            string container in new[]
            {
                "Interface",
                "GenericInterface",
                "ClassInterface",
                "InterfaceClass",
                "Class",
                "Struct",
            }
        )
        {
            foreach (string target in new[] { "class", "readonly struct" })
            {
                foreach ((bool constructor, string? message) in Attributes())
                {
                    yield return new TestCaseData(container, target, constructor, message).SetName(
                        $"GeneratedMembersPreserveLegalContainingDeclarations({container},{target},{constructor},{message ?? "none"})"
                    );
                }
            }
        }
    }

    private static IEnumerable<TestCaseData> AttributeCases()
    {
        foreach ((bool constructor, string? message) in Attributes())
        {
            yield return new TestCaseData(constructor, message).SetName(
                $"NonpartialInterfaceRetainsTheContainerDiagnostics({constructor},{message ?? "none"})"
            );
        }
    }

    private static IEnumerable<(bool Constructor, string? Message)> Attributes()
    {
        yield return (true, null);
        foreach (string message in new[] { "Untargeted", "Targeted", "Broadcast" })
        {
            yield return (false, message);
            yield return (true, message);
        }
    }

    private static string CreateSource(
        string container,
        string target,
        bool constructor,
        string? message,
        bool manual
    )
    {
        (string open, string close, string name) = container switch
        {
            "Interface" => ("public partial interface Envelope {", "}", "Envelope.Packet"),
            "GenericInterface" => (
                "public partial interface Envelope<T> where T : struct {",
                "}",
                "Envelope<int>.Packet"
            ),
            "ClassInterface" => (
                "public partial class Outer { public partial interface Envelope {",
                "} }",
                "Outer.Envelope.Packet"
            ),
            "InterfaceClass" => (
                "public partial interface Envelope { public partial class Inner {",
                "} }",
                "Envelope.Inner.Packet"
            ),
            "Class" => ("public partial class Envelope {", "}", "Envelope.Packet"),
            "Struct" => ("public partial struct Envelope {", "}", "Envelope.Packet"),
            _ => throw new ArgumentOutOfRangeException(
                nameof(container),
                container,
                "Unknown test container"
            ),
        };
        string declaration = target == "class" ? "partial class" : "readonly partial struct";
        string attributes = manual
            ? ""
            : (constructor ? "[DxMessaging.Core.Attributes.DxAutoConstructor]" : "")
                + (message == null ? "" : $"[DxMessaging.Core.Attributes.Dx{message}Message]");
        string baseType =
            manual && message != null ? $" : DxMessaging.Core.Messages.I{message}Message" : "";
        string members =
            manual || !constructor ? "public Packet(int value) { Value = value; }" : "";
        if (manual && message != null)
        {
            members += "public System.Type MessageType => typeof(Packet);";
        }
        string verifyIdentity = message == null ? "" : $" && packet.MessageType == typeof({name})";
        return $$"""
namespace ConsumerExample
{
    {{open}}
    {{attributes}}
    public {{declaration}} Packet{{baseType}}
    {
        public readonly int Value;
        {{members}}
    }
    {{close}}
    public static class Consumer
    {
        public static bool Verify()
        {
            {{name}} packet = new {{name}}(42);
            return packet.Value == 42{{verifyIdentity}};
        }
    }
}
""";
    }

    private static CSharpGeneratorDriver CreateDriver() =>
        CSharpGeneratorDriver.Create(
            generators: new ISourceGenerator[]
            {
                new DxAutoConstructorGenerator(),
                new DxMessageIdGenerator(),
            },
            parseOptions: ParseOptions
        );

    private static CSharpCompilation CreateCompilation(string source) =>
        CSharpCompilation.Create(
            "InterfaceContainerConsumer",
            ShippedContracts.Add(CSharpSyntaxTree.ParseText(source, ParseOptions, "Consumer.cs")),
            GeneratorTestUtilities.CompilationReferences,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                generalDiagnosticOption: ReportDiagnostic.Error
            )
        );

    private static ImmutableArray<SyntaxTree> ReadShippedContracts()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "package.json")))
        {
            root = root.Parent;
        }
        if (root == null)
        {
            throw new DirectoryNotFoundException(
                "Find the repository containing the real messaging contracts."
            );
        }
        string[] paths =
        {
            "Runtime/Core/IMessage.cs",
            "Runtime/Core/Messages/IUntargetedMessage.cs",
            "Runtime/Core/Messages/ITargetedMessage.cs",
            "Runtime/Core/Messages/IBroadcastMessage.cs",
            "Runtime/Core/Attributes/DxAutoConstructorAttribute.cs",
            "Runtime/Core/Attributes/DxOptionalParameterAttribute.cs",
            "Runtime/Core/Attributes/DxUntargetedMessageAttribute.cs",
            "Runtime/Core/Attributes/DxTargetedMessageAttribute.cs",
            "Runtime/Core/Attributes/DxBroadcastMessageAttribute.cs",
        };
        return paths
            .Select(relative =>
            {
                string path = Path.Combine(root.FullName, relative);
                return CSharpSyntaxTree.ParseText(File.ReadAllText(path), ParseOptions, path);
            })
            .ToImmutableArray<SyntaxTree>();
    }

    private static void AssertCompilationExecutes(Compilation compilation, string context)
    {
        Diagnostic[] diagnostics = compilation.GetDiagnostics().ToArray();
        Assert.That(
            diagnostics,
            Is.Empty,
            context + "\n" + string.Join("\n", diagnostics.AsEnumerable())
        );
        using MemoryStream image = new();
        EmitResult emit = compilation.Emit(image);
        Assert.That(emit.Success, Is.True, context + "\n" + string.Join("\n", emit.Diagnostics));
        image.Position = 0;
        AssemblyLoadContext load = new("InterfaceContainerConsumer", isCollectible: true);
        try
        {
            Assembly assembly = load.LoadFromStream(image);
            MethodInfo? verify = assembly.GetType("ConsumerExample.Consumer")?.GetMethod("Verify");
            Assert.That(verify, Is.Not.Null, "The explicit caller must be emitted: " + context);
            Assert.That(
                verify!.Invoke(null, null),
                Is.EqualTo(true),
                "Field and identity must agree: " + context
            );
        }
        finally
        {
            load.Unload();
        }
    }
}
