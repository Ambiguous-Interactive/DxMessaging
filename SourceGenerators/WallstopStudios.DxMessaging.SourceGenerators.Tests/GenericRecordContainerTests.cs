using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
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
internal sealed class GenericRecordContainerTests
{
    private static readonly ImmutableArray<(string Path, string Source)> Contracts =
        ReadContracts();

    [TestCaseSource(nameof(Cases))]
    public void GeneratedMembersPreserveClosedGenericRecordIdentity(
        string topology,
        string target,
        bool constructor,
        string? message,
        int language
    )
    {
        string context =
            $"topology={topology}; target={target}; constructor={constructor}; message={message ?? "none"}; language={language}";
        bool supported = LanguageVersionFacts.TryParse(
            language.ToString(CultureInfo.InvariantCulture),
            out LanguageVersion version
        );
        Assert.That(
            supported,
            Is.True,
            "The selected compiler must support this profile: " + context
        );
        CSharpParseOptions options = new(version);
        AssertCompilationExecutes(
            CreateCompilation(CreateSource(topology, target, constructor, message, true), options),
            "Handwritten oracle: " + context
        );
        CSharpCompilation input = CreateCompilation(
            CreateSource(topology, target, constructor, message, false),
            options
        );
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new ISourceGenerator[] { new DxAutoConstructorGenerator(), new DxMessageIdGenerator() },
            parseOptions: options
        );
        driver = driver.RunGeneratorsAndUpdateCompilation(
            input,
            out Compilation generated,
            out ImmutableArray<Diagnostic> diagnostics
        );
        GeneratorDriverRunResult result = driver.GetRunResult();
        Assert.That(result.Results.Select(run => run.Exception), Has.All.Null, context);
        Assert.That(diagnostics, Is.Empty, context + "\n" + string.Join("\n", diagnostics));
        Assert.That(
            result.GeneratedTrees,
            Has.Length.EqualTo((constructor ? 1 : 0) + (message == null ? 0 : 1)),
            context
        );
        AssertCompilationExecutes(generated, "Generated caller: " + context);
    }

    private static IEnumerable<TestCaseData> Cases()
    {
        foreach (
            string topology in new[] { "ClassClass", "ClassStruct", "StructClass", "StructStruct" }
        )
        {
            foreach (
                string target in new[]
                {
                    "class",
                    "readonly struct",
                    "record",
                    "readonly record struct",
                }
            )
            {
                int language =
                    topology == "ClassClass" && target != "readonly record struct" ? 9 : 10;
                foreach ((bool constructor, string? message) in Modes())
                {
                    yield return new TestCaseData(
                        topology,
                        target,
                        constructor,
                        message,
                        language
                    ).SetName(
                        $"GeneratedMembersPreserveClosedGenericRecordIdentity({topology},{target},{constructor},{message ?? "none"},CSharp{language})"
                    );
                }
            }
        }
    }

    private static IEnumerable<(bool Constructor, string? Message)> Modes()
    {
        yield return (true, null);
        foreach (string message in new[] { "Untargeted", "Targeted", "Broadcast" })
        {
            yield return (false, message);
            yield return (true, message);
        }
    }

    private static string CreateSource(
        string topology,
        string target,
        bool constructor,
        string? message,
        bool manual
    )
    {
        (string outer, string inner) = topology switch
        {
            "ClassClass" => ("record", "record"),
            "ClassStruct" => ("record", "record struct"),
            "StructClass" => ("record struct", "record"),
            "StructStruct" => ("record struct", "record struct"),
            _ => throw new ArgumentOutOfRangeException(
                nameof(topology),
                topology,
                "Unknown topology"
            ),
        };
        string declaration = target switch
        {
            "class" => "partial class",
            "readonly struct" => "readonly partial struct",
            "record" => "partial record",
            "readonly record struct" => "readonly partial record struct",
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown target"),
        };
        string attributes = manual
            ? ""
            : (constructor ? "[DxMessaging.Core.Attributes.DxAutoConstructor]" : "")
                + (message == null ? "" : $"[DxMessaging.Core.Attributes.Dx{message}Message]");
        string contract =
            manual && message != null ? $" : DxMessaging.Core.Messages.I{message}Message" : "";
        string members =
            manual || !constructor
                ? "public Packet(T outer, U inner, V target) { OuterValue = outer; InnerValue = inner; TargetValue = target; }"
                : "";
        if (manual && message != null)
        {
            members += "public System.Type MessageType => typeof(Packet<V>);";
        }
        string identity =
            message == null
                ? ""
                : " && first.MessageType == typeof(Outer<int>.Envelope<string>.Packet<long>)"
                    + " && second.MessageType == typeof(Outer<long>.Envelope<object>.Packet<int>)"
                    + " && first.MessageType != second.MessageType";
        return $$"""
namespace GenericRecordConsumer
{
    public partial {{outer}} Outer<T> where T : struct
    {
        public partial {{inner}} Envelope<U> where U : class
        {
            {{attributes}}
            public {{declaration}} Packet<V>{{contract}} where V : struct
            {
                public readonly T OuterValue;
                public readonly U InnerValue;
                public readonly V TargetValue;
                {{members}}
            }
        }
    }
    public static class Consumer
    {
        public static bool Verify()
        {
            object shared = new object();
            Outer<int>.Envelope<string>.Packet<long> first = new Outer<int>.Envelope<string>.Packet<long>(17, "first", 29L);
            Outer<long>.Envelope<object>.Packet<int> second = new Outer<long>.Envelope<object>.Packet<int>(19L, shared, 31);
            return first.OuterValue == 17 && first.InnerValue == "first" && first.TargetValue == 29L
                && second.OuterValue == 19L && object.ReferenceEquals(second.InnerValue, shared) && second.TargetValue == 31{{identity}};
        }
    }
}
""";
    }

    private static CSharpCompilation CreateCompilation(string source, CSharpParseOptions options)
    {
        IEnumerable<SyntaxTree> trees = Contracts.Select(contract =>
            CSharpSyntaxTree.ParseText(contract.Source, options, contract.Path)
        );
        return CSharpCompilation.Create(
            "GenericRecordConsumer",
            trees.Append(CSharpSyntaxTree.ParseText(source, options, "Consumer.cs")),
            GeneratorTestUtilities.CompilationReferences,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                generalDiagnosticOption: ReportDiagnostic.Error
            )
        );
    }

    private static ImmutableArray<(string Path, string Source)> ReadContracts()
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
            "Runtime/Attributes/DxAutoConstructorAttribute.cs",
            "Runtime/Attributes/DxOptionalParameterAttribute.cs",
            "Runtime/Attributes/DxUntargetedMessageAttribute.cs",
            "Runtime/Attributes/DxTargetedMessageAttribute.cs",
            "Runtime/Attributes/DxBroadcastMessageAttribute.cs",
        };
        return paths
            .Select(relative =>
            {
                string path = Path.Combine(root.FullName, relative);
                return (path, File.ReadAllText(path));
            })
            .ToImmutableArray();
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
        AssemblyLoadContext load = new("GenericRecordConsumer", isCollectible: true);
        try
        {
            Assembly assembly = load.LoadFromStream(image);
            MethodInfo? verify = assembly
                .GetType("GenericRecordConsumer.Consumer")
                ?.GetMethod("Verify");
            Assert.That(verify, Is.Not.Null, "The explicit caller must be emitted: " + context);
            Assert.That(
                verify!.Invoke(null, null),
                Is.EqualTo(true),
                "Closed fields and identity: " + context
            );
        }
        finally
        {
            load.Unload();
        }
    }
}
