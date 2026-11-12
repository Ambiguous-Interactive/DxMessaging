using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using NUnit.Framework;

namespace WallstopStudios.DxMessaging.SourceGenerators.Tests;

/// <summary>Executes constructors and getters over the original serialized fields.</summary>
[TestFixture]
internal sealed class GeneratedSerializedFieldTests
{
    private const string SerializeFieldDeclaration = """
        namespace UnityEngine
        {
            [System.AttributeUsage(System.AttributeTargets.Field)]
            public sealed class SerializeField : System.Attribute { }
        }
        """;

    private static IEnumerable<TestCaseData> SerializedFieldCases()
    {
        foreach (string kind in new[] { "class", "struct" })
        {
            string[] accessibilities =
                kind == "class"
                    ? new[]
                    {
                        "public",
                        "private",
                        "internal",
                        "protected",
                        "protected internal",
                        "private protected",
                    }
                    : new[] { "public", "private", "internal" };
            foreach (string accessibility in accessibilities)
            {
                foreach (bool properties in new[] { false, true })
                {
                    yield return new TestCaseData(kind, accessibility, properties).SetName(
                        $"SerializedFieldsPreserveStorageAndDefaults({kind},{accessibility},{properties})"
                    );
                }
            }
        }
    }

    /// <summary>Replaces the repeated field, constructor, and getter lists without replacing storage.</summary>
    [TestCaseSource(nameof(SerializedFieldCases))]
    public void SerializedFieldsPreserveStorageAndDefaults(
        string kind,
        string accessibility,
        bool properties
    )
    {
        string source = $$"""
            using DxMessaging.Core.Attributes;
            using UnityEngine;
            [System.Serializable]
            [DxAutoConstructor(IncludeSerializedFields = true, GenerateSerializedProperties = {{(
                properties ? "true" : "false"
            )}})]
            public partial {{kind}} Packet
            {
                [SerializeField] {{accessibility}} int _count;
                public readonly int Keep;
                [SerializeField, DxOptionalParameter("unset")] {{accessibility}} string _label;
                {{(properties ? "" : "public int Count => _count; public string Label => _label;")}}
            }
            public static class Consumer
            {
                public static bool Verify()
                {
                    Packet implicitLabel = new Packet(count: 42, Keep: 7);
                    Packet explicitLabel = new Packet(count: -1, Keep: 8, label: "manual");
                    Packet nullLabel = new Packet(count: 0, Keep: 0, label: null);
                    System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                    System.Type type = typeof(Packet);
                    System.Reflection.FieldInfo count = type.GetField("_count", flags);
                    System.Reflection.FieldInfo label = type.GetField("_label", flags);
                    return implicitLabel.Count == 42 && implicitLabel.Keep == 7 && implicitLabel.Label == "unset"
                        && explicitLabel.Count == -1 && explicitLabel.Keep == 8 && explicitLabel.Label == "manual"
                        && nullLabel.Count == 0 && nullLabel.Label == null
                        && count != null && label != null
                        && count.GetCustomAttributes(typeof(SerializeField), false).Length == 1
                        && label.GetCustomAttributes(typeof(SerializeField), false).Length == 1
                        && type.GetProperty("Count").SetMethod == null
                        && type.GetProperty("Label").SetMethod == null
                        && type.GetConstructor(new[] { typeof(int), typeof(int), typeof(string) })
                            .GetParameters()[2].DefaultValue.Equals("unset");
                }
            }
            """;
        string context = $"kind={kind}, accessibility={accessibility}, properties={properties}";
        (_, Compilation output, ImmutableArray<Diagnostic> diagnostics) = Generate(source);
        Assert.That(diagnostics, Is.Empty, context);
        AssertCompilationExecutes(output, context);
    }

    /// <summary>Retains existing public-field signatures and private initialized storage when the options are off.</summary>
    [TestCase(false)]
    [TestCase(true)]
    public void DefaultOptionsRetainPublicFieldNamesAndPrivateStorage(bool explicitDefaults)
    {
        string arguments = explicitDefaults
            ? "(IncludeSerializedFields = false, GenerateSerializedProperties = false)"
            : "";
        string source = $$"""
            using DxMessaging.Core.Attributes;
            using UnityEngine;
            [DxAutoConstructor{{arguments}}]
            public partial class Packet
            {
                [SerializeField] private int _private = 11;
                [SerializeField] public readonly int _public;
                public readonly int Keep;
                public int Private => _private;
            }
            public static class Consumer
            {
                public static bool Verify()
                {
                    Packet packet = new Packet(_public: 42, Keep: 7);
                    return packet._public == 42 && packet.Keep == 7 && packet.Private == 11
                        && typeof(Packet).GetProperty("Public") == null;
                }
            }
            """;
        string context = $"explicitDefaults={explicitDefaults}";
        (
            GeneratorDriverRunResult result,
            Compilation output,
            ImmutableArray<Diagnostic> diagnostics
        ) = Generate(source);
        Assert.That(diagnostics, Is.Empty, context);
        Assert.That(result.GeneratedTrees, Has.Length.EqualTo(1), context);
        string generated = result.GeneratedTrees.Single().GetText().ToString();
        TestContext.Out.WriteLine(
            "DEFAULT_GENERATED_SHA256="
                + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(generated)))
        );
        AssertCompilationExecutes(output, context);
    }

    /// <summary>Escapes constructor keywords and uses invariant property casing after removing underscores.</summary>
    [TestCase("_class", "@class", "Class")]
    [TestCase("__event", "@event", "Event")]
    [TestCase("_record", "@record", "Record")]
    [TestCase("_id", "id", "Id")]
    [SetCulture("tr-TR")]
    public void SerializedNamesPreserveLegalIdentifiers(
        string field,
        string parameter,
        string property
    )
    {
        string source = $$"""
            using DxMessaging.Core.Attributes;
            using UnityEngine;
            [DxAutoConstructor(GenerateSerializedProperties = true)]
            public partial struct Packet
            {
                [SerializeField] private int {{field}};
            }
            public static class Consumer
            {
                public static bool Verify() => new Packet({{parameter}}: 42).{{property}} == 42;
            }
            """;
        string context =
            $"field={field}, parameter={parameter}, property={property}, culture=tr-TR";
        (_, Compilation output, ImmutableArray<Diagnostic> diagnostics) = Generate(source);
        Assert.That(diagnostics, Is.Empty, context);
        AssertCompilationExecutes(output, context);
    }

    /// <summary>Keeps each field's nullable context, optional default, and compilation order across partial files.</summary>
    [Test]
    public void SerializedPropertiesPreservePartialFileNullableContexts()
    {
        const string source = """
            #nullable disable
            using DxMessaging.Core.Attributes;
            using UnityEngine;
            [DxAutoConstructor(GenerateSerializedProperties = true)]
            public partial class Packet
            {
                [SerializeField] private int _count;
            }
            public static class Consumer
            {
                public static bool Verify() => new Packet(count: 42).Count == 42
                    && new Packet(count: 42).Label == null
                    && new Packet(count: -1, label: "manual").Label == "manual";
            }
            """;
        const string second = """
            #nullable enable
            using DxMessaging.Core.Attributes;
            using Saved = UnityEngine.SerializeField;
            public partial class Packet
            {
                [Saved, DxOptionalParameter(null)] private string? _label;
            }
            """;
        (_, Compilation output, ImmutableArray<Diagnostic> diagnostics) = Generate(source, second);
        Assert.That(diagnostics, Is.Empty, "Partial files with disabled and enabled annotations.");
        INamedTypeSymbol type = output.GetTypeByMetadataName("Packet")!;
        IPropertySymbol label = type.GetMembers("Label").OfType<IPropertySymbol>().Single();
        Assert.That(label.NullableAnnotation, Is.EqualTo(NullableAnnotation.Annotated));
        IParameterSymbol parameter = type
            .InstanceConstructors.Single(c => c.Parameters.Length == 2)
            .Parameters[1];
        Assert.That(parameter.Name, Is.EqualTo("label"));
        Assert.That(parameter.NullableAnnotation, Is.EqualTo(NullableAnnotation.Annotated));
        Assert.That(parameter.HasExplicitDefaultValue, Is.True);
        Assert.That(parameter.ExplicitDefaultValue, Is.Null);
        AssertCompilationExecutes(
            output,
            "Partial files preserve alias, declaration order, and nullable storage."
        );
    }

    /// <summary>Retains outer generic type parameters in both generated constructor and getter types.</summary>
    [Test]
    public void SerializedPropertiesRetainContainingGenericParameters()
    {
        const string source = """
            using DxMessaging.Core.Attributes;
            using UnityEngine;
            public partial class Envelope<T> where T : class
            {
                [DxAutoConstructor(GenerateSerializedProperties = true)]
                public partial class Packet
                {
                    [SerializeField] private T _value;
                    [SerializeField, DxOptionalParameter(null)] private T _fallback;
                }
            }
            public static class Consumer
            {
                public static bool Verify()
                {
                    Envelope<string>.Packet packet = new Envelope<string>.Packet(value: "value");
                    return packet.Value == "value" && packet.Fallback == null;
                }
            }
            """;
        (_, Compilation output, ImmutableArray<Diagnostic> diagnostics) = Generate(source);
        Assert.That(diagnostics, Is.Empty, "Nested generic serialized storage.");
        AssertCompilationExecutes(output, "Nested generic serialized storage.");
    }

    /// <summary>Includes only real declared instance fields with the semantic Unity attribute.</summary>
    [Test]
    public void SerializedSelectionIgnoresStaticConstantBackingAndUnrelatedFields()
    {
        const string source = """
            using DxMessaging.Core.Attributes;
            using UnityEngine;
            [DxAutoConstructor(GenerateSerializedProperties = true)]
            public partial class Packet
            {
                [SerializeField] public static int Static = 4;
                [SerializeField] public const int Constant = 8;
                [Other.SerializeField] private int _unrelated = 11;
                [field: SerializeField] public int Cached { get; set; } = 3;
                [SerializeField] private int _count;
                public int Unrelated => _unrelated;
            }
            public static class Consumer
            {
                public static bool Verify()
                {
                    Packet packet = new Packet(count: 42);
                    return packet.Count == 42 && packet.Unrelated == 11 && packet.Cached == 3
                        && Packet.Static == 4 && Packet.Constant == 8;
                }
            }
            namespace Other
            {
                [System.AttributeUsage(System.AttributeTargets.Field)]
                public sealed class SerializeField : System.Attribute { }
            }
            """;
        (_, Compilation output, ImmutableArray<Diagnostic> diagnostics) = Generate(source);
        Assert.That(
            diagnostics,
            Is.Empty,
            "Exclude static, const, compiler backing fields and unrelated attributes."
        );
        AssertCompilationExecutes(
            output,
            "Exclude static, const, compiler backing fields and unrelated attributes."
        );
    }

    private static IEnumerable<TestCaseData> InvalidNameCases()
    {
        TestCaseData[] cases = new[]
        {
            new TestCaseData(
                "EmptyParameter",
                "[SerializeField] private int _ = 1; public int Read() => _;",
                false,
                "_",
                ""
            ),
            new TestCaseData(
                "UnderscoreParameter",
                "[SerializeField] private int __ = 1; public int Read() => __;",
                false,
                "__",
                ""
            ),
            new TestCaseData(
                "NumericParameter",
                "[SerializeField] private int _1 = 1; public int Read() => _1;",
                false,
                "_1",
                "1"
            ),
            new TestCaseData(
                "ParameterCollision",
                "[SerializeField] private int _count = 1; public int count; public int Read() => _count + count;",
                false,
                "count",
                "count"
            ),
            new TestCaseData(
                "ExistingGetter",
                "[SerializeField] private int _count = 1; public int Count => _count;",
                true,
                "_count",
                "Count"
            ),
            new TestCaseData(
                "PropertyCollision",
                "[SerializeField] private int _foo = 1; [SerializeField] private int _Foo = 2; public int Read() => _foo + _Foo;",
                true,
                "_Foo",
                "Foo"
            ),
            new TestCaseData(
                "TypeName",
                "[SerializeField] private int _packet = 1; public int Read() => _packet;",
                true,
                "_packet",
                "Packet"
            ),
            new TestCaseData(
                "InheritedMember",
                "[SerializeField] private int _count = 1; public int Read() => _count;",
                true,
                "_count",
                "Count"
            ),
            new TestCaseData(
                "TypeParameter",
                "[SerializeField] private int _count = 1; public int Read() => _count;",
                true,
                "_count",
                "Count"
            ),
        };
        foreach (TestCaseData testCase in cases)
        {
            yield return testCase.SetName(
                $"SerializedNameConflictsReportOneDiagnostic({testCase.Arguments[0]})"
            );
        }
    }

    /// <summary>Reports an actionable generator diagnostic instead of producing ambiguous or illegal C#.</summary>
    [TestCaseSource(nameof(InvalidNameCases))]
    public void SerializedNameConflictsReportOneDiagnostic(
        string scenario,
        string fields,
        bool properties,
        string field,
        string generatedName
    )
    {
        string inheritance = scenario == "InheritedMember" ? ": Parent" : "";
        string typeParameters = scenario == "TypeParameter" ? "<Count>" : "";
        string source = $$"""
            using DxMessaging.Core.Attributes;
            using UnityEngine;
            [DxAutoConstructor(IncludeSerializedFields = true, GenerateSerializedProperties = {{(
                properties ? "true" : "false"
            )}})]
            public partial class Packet{{typeParameters}} {{inheritance}} { {{fields}} }
            public class Parent { public int Count => 9; }
            """;
        string context =
            $"scenario={scenario}, fields={fields}, properties={properties}, field={field}, generatedName={generatedName}";
        (
            GeneratorDriverRunResult result,
            Compilation output,
            ImmutableArray<Diagnostic> diagnostics
        ) = Generate(source);
        context += "\n" + string.Join("\n", output.GetDiagnostics());
        Assert.That(diagnostics, Has.Length.EqualTo(1), context);
        Diagnostic diagnostic = diagnostics.Single();
        Assert.That(diagnostic.Id, Is.EqualTo("DXMSG016"), context);
        Assert.That(diagnostic.Severity, Is.EqualTo(DiagnosticSeverity.Error), context);
        Assert.That(
            diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture),
            Does.Contain($"Field '{field}'")
                .And.Contain($"'{generatedName}'")
                .And.Contain("Rename the field"),
            context
        );
        Assert.That(diagnostic.Location.SourceTree!.FilePath, Is.EqualTo("UserSource.cs"), context);
        Assert.That(
            source.Substring(
                diagnostic.Location.SourceSpan.Start,
                diagnostic.Location.SourceSpan.Length
            ),
            Is.EqualTo(field),
            context
        );
        Assert.That(result.GeneratedTrees, Is.Empty, context);
        Assert.That(output.GetDiagnostics(), Is.Empty, context);
    }

    private static IEnumerable<TestCaseData> MessagePropertyCases()
    {
        foreach (string route in new[] { "Untargeted", "Targeted", "Broadcast" })
        {
            foreach (bool collision in new[] { false, true })
            {
                yield return new TestCaseData(route, collision).SetName(
                    $"SerializedPropertiesRespectGeneratedMessageMembers({route},{collision})"
                );
            }
        }
    }

    /// <summary>Composes both packaged generators and protects the generated MessageType member.</summary>
    [TestCaseSource(nameof(MessagePropertyCases))]
    public void SerializedPropertiesRespectGeneratedMessageMembers(string route, bool collision)
    {
        string field = collision ? "_messageType" : "_observedType";
        string verify = collision
            ? "true"
            : "new Packet(observedType: typeof(int)).ObservedType == typeof(int) && new Packet(observedType: typeof(int)).MessageType == typeof(Packet)";
        string source = $$"""
            using DxMessaging.Core.Attributes;
            using UnityEngine;
            [Dx{{route}}Message, DxAutoConstructor(GenerateSerializedProperties = true)]
            public partial class Packet
            {
                [SerializeField] private System.Type {{field}} = typeof(string);
                public System.Type Read() => {{field}};
            }
            public static class Consumer { public static bool Verify() => {{verify}}; }
            """;
        CSharpParseOptions options = new(LanguageVersion.CSharp9);
        CSharpCompilation input = GeneratorTestUtilities
            .CreateAutoConstructorCompilationFromShippedAttributes(source)
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(SerializeFieldDeclaration, options));
        string attributePath = input
            .SyntaxTrees.First(t =>
                t.FilePath.EndsWith("DxAutoConstructorAttribute.cs", StringComparison.Ordinal)
            )
            .FilePath;
        string root = Path.GetFullPath(
            Path.Combine(Path.GetDirectoryName(attributePath)!, "..", "..")
        );
        foreach (
            string relative in new[]
            {
                "Runtime/Core/IMessage.cs",
                "Runtime/Core/Messages/IUntargetedMessage.cs",
                "Runtime/Core/Messages/ITargetedMessage.cs",
                "Runtime/Core/Messages/IBroadcastMessage.cs",
                "Runtime/Attributes/DxUntargetedMessageAttribute.cs",
                "Runtime/Attributes/DxTargetedMessageAttribute.cs",
                "Runtime/Attributes/DxBroadcastMessageAttribute.cs",
            }
        )
        {
            string path = Path.Combine(root, relative);
            input = input.AddSyntaxTrees(
                CSharpSyntaxTree.ParseText(File.ReadAllText(path), options, path)
            );
        }
        GeneratorDriver driver = CSharpGeneratorDriver
            .Create(
                generators: new ISourceGenerator[]
                {
                    new DxAutoConstructorGenerator(),
                    new DxMessageIdGenerator(),
                },
                parseOptions: options
            )
            .RunGeneratorsAndUpdateCompilation(
                input,
                out Compilation output,
                out ImmutableArray<Diagnostic> diagnostics
            );
        string context = $"route={route}, collision={collision}, field={field}, verify={verify}";
        Assert.That(
            driver.GetRunResult().Results.Select(result => result.Exception),
            Has.All.Null,
            context
        );
        if (collision)
        {
            context += "\n" + string.Join("\n", output.GetDiagnostics());
            Assert.That(diagnostics, Has.Length.EqualTo(1), context);
            Assert.That(diagnostics.Single().Id, Is.EqualTo("DXMSG016"), context);
            Assert.That(
                driver
                    .GetRunResult()
                    .Results.Single(result => result.Generator is DxAutoConstructorGenerator)
                    .GeneratedSources,
                Is.Empty,
                context
            );
            Assert.That(output.GetDiagnostics(), Is.Empty, context);
        }
        else
        {
            Assert.That(diagnostics, Is.Empty, context);
            AssertCompilationExecutes(output, context);
        }
    }

    private static (
        GeneratorDriverRunResult Result,
        Compilation Output,
        ImmutableArray<Diagnostic> Diagnostics
    ) Generate(string source, string? additionalSource = null)
    {
        CSharpCompilation input = GeneratorTestUtilities
            .CreateAutoConstructorCompilationFromShippedAttributes(source)
            .AddSyntaxTrees(
                CSharpSyntaxTree.ParseText(
                    SerializeFieldDeclaration,
                    new CSharpParseOptions(LanguageVersion.CSharp9)
                )
            );
        if (additionalSource != null)
        {
            input = input.AddSyntaxTrees(
                CSharpSyntaxTree.ParseText(
                    additionalSource,
                    new CSharpParseOptions(LanguageVersion.CSharp9),
                    "SecondPartial.cs"
                )
            );
        }
        GeneratorDriver driver = CSharpGeneratorDriver
            .Create(
                generators: new ISourceGenerator[] { new DxAutoConstructorGenerator() },
                parseOptions: new CSharpParseOptions(LanguageVersion.CSharp9)
            )
            .RunGeneratorsAndUpdateCompilation(
                input,
                out Compilation output,
                out ImmutableArray<Diagnostic> diagnostics
            );
        GeneratorDriverRunResult result = driver.GetRunResult();
        Assert.That(result.Results.Select(run => run.Exception), Has.All.Null, source);
        return (result, output, diagnostics);
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
        EmitResult emitted = compilation.Emit(image);
        Assert.That(
            emitted.Success,
            Is.True,
            context + "\n" + string.Join("\n", emitted.Diagnostics)
        );
        image.Position = 0;
        AssemblyLoadContext load = new("SerializedFieldConsumer", isCollectible: true);
        try
        {
            Assembly assembly = load.LoadFromStream(image);
            MethodInfo? verify = assembly.GetType("Consumer")?.GetMethod("Verify");
            Assert.That(verify, Is.Not.Null, context);
            Assert.That(verify!.Invoke(null, null), Is.EqualTo(true), context);
        }
        finally
        {
            load.Unload();
        }
    }
}
