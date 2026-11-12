using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace WallstopStudios.DxMessaging.Docs.Tests;

/// <summary>Executes the actual generated shipping guard against the package's current assembly names.</summary>
[TestFixture]
internal sealed class ShippingAssemblyInventoryTests
{
    private AssemblyLoadContext _load = null!;
    private MethodInfo _check = null!;

    [OneTimeSetUp]
    public void CompileTheProductionBuilderInventoryGuard()
    {
        string root = DocsSnippetCompilationTests.ResolveRepoRoot();
        string path = Path.Combine(root, "scripts", "unity", "run-ci-tests.ps1");
        string source = File.ReadAllText(path);
        int builder = source.IndexOf(
            "function New-ShippingFidelityBuilderSource {",
            StringComparison.Ordinal
        );
        Assert.That(
            builder,
            Is.GreaterThanOrEqualTo(0),
            "Locate the actual shipping builder generator."
        );
        int start = source.IndexOf(
            "    private static void AssertNoTestAssemblies(",
            builder,
            StringComparison.Ordinal
        );
        int end = source.IndexOf(
            "    private static void WriteAssemblyEvidence(",
            start,
            StringComparison.Ordinal
        );
        Assert.That(
            start,
            Is.GreaterThan(builder),
            "Select the builder's guard, separate from the runtime loaded-assembly check."
        );
        Assert.That(
            end,
            Is.GreaterThan(start),
            "Extract the complete unchanged production method."
        );
        string guard = source[start..end];
        string compiled =
            "using System; public static class ShippingInventoryGuard {\n"
            + guard
            + "\npublic static void Check(string[] names) => AssertNoTestAssemblies(names); }";
        string references = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("Runtime references are unavailable.");
        CSharpCompilation compilation = CSharpCompilation.Create(
            "ActualShippingInventoryGuard",
            new[]
            {
                CSharpSyntaxTree.ParseText(
                    compiled,
                    new CSharpParseOptions(LanguageVersion.CSharp9),
                    path
                ),
            },
            references
                .Split(Path.PathSeparator)
                .Select(file => MetadataReference.CreateFromFile(file)),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                generalDiagnosticOption: ReportDiagnostic.Error
            )
        );
        using MemoryStream image = new();
        Microsoft.CodeAnalysis.Emit.EmitResult emitted = compilation.Emit(image);
        Assert.That(
            emitted.Success,
            Is.True,
            "Compile the actual guard without Unity stubs or duplicated validation:\n"
                + string.Join("\n", emitted.Diagnostics)
        );
        image.Position = 0;
        _load = new AssemblyLoadContext("ActualShippingInventoryGuard", isCollectible: true);
        Assembly assembly = _load.LoadFromStream(image);
        _check = assembly
            .GetType("ShippingInventoryGuard", throwOnError: true)!
            .GetMethod("Check")!;
    }

    [OneTimeTearDown]
    public void ReleaseCompiledGuard()
    {
        _load?.Unload();
    }

    [TestCase("current", true)]
    [TestCase("missing-attributes", false)]
    [TestCase("missing-runtime", false)]
    [TestCase("nunit", false)]
    [TestCase("unrelated", false)]
    [TestCase("duplicate", false)]
    [TestCase("reversed", false)]
    public void GeneratedBuilderAcceptsOnlyTheCurrentSelectedInventory(string shape, bool accepted)
    {
        string root = DocsSnippetCompilationTests.ResolveRepoRoot();
        using JsonDocument runtime = JsonDocument.Parse(
            File.ReadAllText(
                Path.Combine(root, "Runtime", "Core", "WallstopStudios.DxMessaging.asmdef")
            )
        );
        using JsonDocument attributes = JsonDocument.Parse(
            File.ReadAllText(
                Path.Combine(root, "Runtime", "WallstopStudios.DxMessaging.Attributes.asmdef")
            )
        );
        string runtimeName = runtime.RootElement.GetProperty("name").GetString()!;
        string attributesName = attributes.RootElement.GetProperty("name").GetString()!;
        string[] names = { "Assembly-CSharp", runtimeName, attributesName };
        Array.Sort(names, StringComparer.Ordinal);
        switch (shape)
        {
            case "missing-attributes":
                names = new[] { "Assembly-CSharp", runtimeName };
                break;
            case "missing-runtime":
                names = new[] { "Assembly-CSharp", attributesName };
                break;
            case "nunit":
                names = names.Append("nunit.framework").ToArray();
                break;
            case "unrelated":
                names = names.Append("StaleShippingPlugin").ToArray();
                break;
            case "duplicate":
                names = new[] { "Assembly-CSharp", runtimeName, runtimeName };
                break;
            case "reversed":
                Array.Reverse(names);
                break;
            default:
                Assert.That(
                    shape,
                    Is.EqualTo("current"),
                    "Every shape is an explicit inventory control."
                );
                break;
        }
        if (accepted)
        {
            Assert.That(
                () => _check.Invoke(null, new object[] { names }),
                Throws.Nothing,
                "The real consumer plus both actual package asmdefs must be accepted."
            );
        }
        else
        {
            TargetInvocationException? failure = Assert.Throws<TargetInvocationException>(
                () => _check.Invoke(null, new object[] { names }),
                shape
            );
            Assert.That(
                failure!.InnerException,
                Is.TypeOf<InvalidOperationException>(),
                shape + ": reject through the actual production guard."
            );
        }
    }
}
