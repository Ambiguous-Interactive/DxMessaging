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
internal sealed class GeneratedNestedAccessibilityTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.CSharp9);
    private static readonly ImmutableArray<SyntaxTree> ShippedContracts = ReadShippedContracts();

    [TestCaseSource(nameof(AccessibilityCases))]
    public void GeneratedDeclarationsPreserveNestedAccessibility(
        string containerAccess,
        TypeAttributes containerVisibility,
        string targetAccess,
        TypeAttributes targetVisibility,
        bool referenceType,
        string? message
    )
    {
        string context = $"container={containerAccess}; target={targetAccess}; referenceType={referenceType}; message={message ?? "none"}";
        AssertCompilationExecutes(
            CreateCompilation(CreateSource(containerAccess, targetAccess, referenceType, message, true)),
            containerVisibility,
            targetVisibility,
            "Handwritten oracle: " + context
        );
        CSharpCompilation input = CreateCompilation(CreateSource(containerAccess, targetAccess, referenceType, message, false));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new ISourceGenerator[] { new DxAutoConstructorGenerator(), new DxMessageIdGenerator() }, parseOptions: ParseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(input, out Compilation generated, out ImmutableArray<Diagnostic> diagnostics);
        GeneratorDriverRunResult result = driver.GetRunResult();
        Assert.That(result.Results.Select(run => run.Exception), Has.All.Null, context);
        Assert.That(diagnostics, Is.Empty, context + "\n" + string.Join("\n", diagnostics));
        Assert.That(result.GeneratedTrees, Has.Length.EqualTo(message == null ? 1 : 2), context);
        AssertCompilationExecutes(generated, containerVisibility, targetVisibility, "Generated caller: " + context);
    }

    private static IEnumerable<TestCaseData> AccessibilityCases()
    {
        foreach ((string containerAccess, TypeAttributes containerVisibility) in Accessibilities())
        {
            foreach ((string targetAccess, TypeAttributes targetVisibility) in Accessibilities())
            {
                foreach (bool referenceType in new[] { false, true })
                {
                    foreach (string? message in new[] { null, "Untargeted", "Targeted", "Broadcast" })
                    {
                        yield return new TestCaseData(containerAccess, containerVisibility, targetAccess, targetVisibility, referenceType, message).SetName(
                            $"GeneratedDeclarationsPreserveNestedAccessibility({containerAccess},{targetAccess},{referenceType},{message ?? "none"})"
                        );
                    }
                }
            }
        }
    }

    private static IEnumerable<(string Access, TypeAttributes Visibility)> Accessibilities()
    {
        // ECMA-335 II.23.1.15 defines the nested visibility values.
        yield return ("public", (TypeAttributes)2);
        yield return ("private", (TypeAttributes)3);
        yield return ("protected", (TypeAttributes)4);
        yield return ("internal", (TypeAttributes)5);
        yield return ("protected internal", (TypeAttributes)7);
        yield return ("private protected", (TypeAttributes)6);
    }

    private static string CreateSource(string containerAccess, string targetAccess, bool referenceType, string? message, bool manual)
    {
        string declaration = referenceType ? "partial class" : "readonly partial struct";
        string attributes = manual ? "" : "[DxMessaging.Core.Attributes.DxAutoConstructor]" + (message == null ? "" : $"[DxMessaging.Core.Attributes.Dx{message}Message]");
        string contract = manual && message != null ? $" : DxMessaging.Core.Messages.I{message}Message" : "";
        string members = manual ? "public Packet(int value) { Value = value; }" : "";
        if (manual && message != null)
        {
            members += "public System.Type MessageType => typeof(Packet);";
        }
        string identity = message == null ? "" : $" && ((DxMessaging.Core.Messages.I{message}Message)packet).MessageType == typeof(Packet)";
        return $$"""
namespace ConsumerExample
{
    public partial class Outer
    {
        {{containerAccess}} partial class Envelope
        {
            {{attributes}}
            {{targetAccess}} {{declaration}} Packet{{contract}}
            {
                public readonly int Value;
                {{members}}
            }
            public static bool Verify()
            {
                Packet packet = new Packet(42);
                return packet.Value == 42{{identity}};
            }
        }
    }
}
""";
    }

    private static CSharpCompilation CreateCompilation(string source) =>
        CSharpCompilation.Create(
            "NestedAccessibilityConsumer",
            ShippedContracts.Add(CSharpSyntaxTree.ParseText(source, ParseOptions, "Consumer.cs")),
            GeneratorTestUtilities.CompilationReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, generalDiagnosticOption: ReportDiagnostic.Error)
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
            throw new DirectoryNotFoundException("Find the repository containing the real messaging contracts.");
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
                return CSharpSyntaxTree.ParseText(File.ReadAllText(path), ParseOptions, path);
            })
            .ToImmutableArray<SyntaxTree>();
    }

    private static void AssertCompilationExecutes(Compilation compilation, TypeAttributes containerVisibility, TypeAttributes targetVisibility, string context)
    {
        Diagnostic[] diagnostics = compilation.GetDiagnostics().ToArray();
        Assert.That(diagnostics, Is.Empty, context + "\n" + string.Join("\n", diagnostics.AsEnumerable()));
        using MemoryStream image = new();
        EmitResult emit = compilation.Emit(image);
        Assert.That(emit.Success, Is.True, context + "\n" + string.Join("\n", emit.Diagnostics));
        image.Position = 0;
        AssemblyLoadContext load = new("NestedAccessibilityConsumer", isCollectible: true);
        try
        {
            Assembly assembly = load.LoadFromStream(image);
            Type? envelope = assembly.GetType("ConsumerExample.Outer+Envelope");
            Assert.That(envelope, Is.Not.Null, "The containing type must be emitted: " + context);
            Assert.That(envelope!.Attributes & TypeAttributes.VisibilityMask, Is.EqualTo(containerVisibility), "Container visibility: " + context);
            Type? packet = envelope.GetNestedType("Packet", BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(packet, Is.Not.Null, "The target must be emitted: " + context);
            Assert.That(packet!.Attributes & TypeAttributes.VisibilityMask, Is.EqualTo(targetVisibility), "Target visibility: " + context);
            MethodInfo? verify = envelope.GetMethod("Verify", BindingFlags.Public | BindingFlags.Static);
            Assert.That(verify, Is.Not.Null, "The in-scope caller must be emitted: " + context);
            Assert.That(verify!.Invoke(null, null), Is.EqualTo(true), "Field and identity: " + context);
        }
        finally
        {
            load.Unload();
        }
    }
}
