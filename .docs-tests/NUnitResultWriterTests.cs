using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

// cspell:ignore udfff ufffe uffff
namespace WallstopStudios.DxMessaging.Docs.Tests;

/// <summary>Compares the generated result writer with Unity's NUnit 3.5 XML contract.</summary>
[TestFixture]
[NonParallelizable]
internal sealed class NUnitResultWriterTests
{
    private AssemblyLoadContext? _context;
    private Func<string?, string, string, string, string, string, string>? _compare;
    private Func<string, string, string>? _publication;

    /// <summary>Compiles the actual writer and oracle fixture against Unity's NUnit ABI.</summary>
    [OneTimeSetUp]
    public void CompileActualWriterAgainstNUnit35()
    {
        string framework = Path.Combine(AppContext.BaseDirectory, "NUnit35", "nunit.framework.dll");
        string platform =
            (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("Runtime reference assemblies are unavailable.");
        MetadataReference[] references = platform
            .Split(Path.PathSeparator)
            .Where(path =>
                !string.Equals(
                    Path.GetFileName(path),
                    "nunit.framework.dll",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .Append(framework)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
        string[] paths =
        [
            Path.Combine(AppContext.BaseDirectory, "DxmNUnitResultWriter.cs.txt"),
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "NUnitResultWriterFixture.cs.txt"),
        ];
        CSharpCompilation compilation = CSharpCompilation.Create(
            "ResultWriterProbe_" + Guid.NewGuid().ToString("N"),
            paths.Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path)),
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        using MemoryStream image = new();
        Microsoft.CodeAnalysis.Emit.EmitResult emitted = compilation.Emit(image);
        Assert.That(
            emitted.Success,
            Is.True,
            string.Join(Environment.NewLine, emitted.Diagnostics)
        );
        _context = new AssemblyLoadContext("NUnit35ResultWriter", isCollectible: true);
        _context.LoadFromAssemblyPath(framework);
        image.Position = 0;
        Assembly assembly = _context.LoadFromStream(image);
        MethodInfo method = assembly
            .GetType("ResultWriterProbe.Probe", throwOnError: true)!
            .GetMethod("Compare", BindingFlags.Public | BindingFlags.Static)!;
        _compare = method.CreateDelegate<
            Func<string?, string, string, string, string, string, string>
        >();
        _publication = assembly
            .GetType("ResultWriterProbe.Probe", throwOnError: true)!
            .GetMethod("Publication", BindingFlags.Public | BindingFlags.Static)!
            .CreateDelegate<Func<string, string, string>>();
    }

    /// <summary>Releases the isolated compatibility assembly and its delegates.</summary>
    [OneTimeTearDown]
    public void ReleaseCompiledFixture()
    {
        _compare = null;
        _publication = null;
        _context?.Unload();
        _context = null;
    }

    /// <summary>Combines XML character, result-state, extension, and culture inputs.</summary>
    private static IEnumerable<TestCaseData> Cases()
    {
        string?[] payloads =
        [
            null,
            "",
            "plain ASCII",
            "<tag key=\"value\">&'",
            "before]]>after]]>",
            "tabs\tand\r\nlines",
            "\0\u0001\u000b\u001f",
            "\u007f\u0080\u00e9\u4e2d",
            "\ud83d\ude00",
            "\ud800",
            "\udfff",
            "\ufffe\uffff",
            new string('x', 65536),
            string.Concat(Enumerable.Range(0, 128).Select(value => (char)value)),
        ];
        (string Status, string Label, string Site)[] states =
        [
            ("Passed", "", "Test"),
            ("Failed", "Error", "SetUp"),
            ("Skipped", "Ignored", "Test"),
            ("Inconclusive", "", "Test"),
            ("Failed", "", "TearDown"),
            ("99", "Unexpected", "Test"),
        ];
        int index = 0;
        foreach (string? payload in payloads)
        {
            foreach ((string status, string label, string site) in states)
            {
                foreach (
                    string shape in new[]
                    {
                        "standard",
                        "custom-test",
                        "custom-result",
                        "custom-suite",
                        "suite-failure",
                        "empty",
                    }
                )
                {
                    foreach (string culture in new[] { "en-US", "fr-FR" })
                    {
                        yield return new TestCaseData(
                            payload,
                            status,
                            label,
                            site,
                            shape,
                            culture
                        ).SetName($"ResultXml{index++}_{status}_{shape}_{culture}");
                    }
                }
            }
        }
    }

    /// <summary>Requires byte-identical XML from the candidate and the upstream implementation.</summary>
    [TestCaseSource(nameof(Cases))]
    public void PreservesCompleteUpstreamXml(
        string? payload,
        string status,
        string label,
        string site,
        string shape,
        string culture
    )
    {
        Assert.That(
            _compare!(payload, status, label, site, shape, culture),
            Is.EqualTo("equal"),
            $"status={status}; label={label}; site={site}; shape={shape}; culture={culture}; payloadLength={payload?.Length}"
        );
    }

    /// <summary>Publishes byte-identical complete reports with standard and delegated leaves.</summary>
    [TestCase("standard")]
    [TestCase("custom")]
    public void PublishesCompleteReport(string mode)
    {
        string directory = Path.Combine(Path.GetTempPath(), "dxm-result-" + Guid.NewGuid());
        string path = Path.Combine(directory, "nested", "results.xml");
        try
        {
            Assert.That(_publication!(path, mode), Is.EqualTo("equal"));
            Assert.That(
                Directory.GetFiles(directory, "*.tmp", SearchOption.AllDirectories),
                Is.Empty
            );
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Never publishes partial XML after serializer or final publication failures.</summary>
    [TestCase("throw-first")]
    [TestCase("throw-last")]
    [TestCase("publish-directory")]
    [TestCase("prior-report")]
    public void FailedReportDoesNotReplacePriorEvidence(string mode)
    {
        string directory = Path.Combine(Path.GetTempPath(), "dxm-result-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "results.xml");
        string unrelated = Path.Combine(directory, "other.tmp");
        File.WriteAllText(unrelated, "unrelated evidence");
        if (mode == "prior-report")
        {
            File.WriteAllText(path, "prior report");
        }
        try
        {
            IOException? error = Assert.Catch<IOException>(() => _publication!(path, mode));
            if (mode.StartsWith("throw-", StringComparison.Ordinal))
            {
                Assert.That(error!.Message, Is.EqualTo("controlled serializer failure"));
            }
            else
            {
                Assert.That(
                    Directory.Exists(path),
                    Is.EqualTo(mode == "publish-directory"),
                    "The selected publication conflict must exist."
                );
                Assert.That(
                    new StackTrace(error!)
                        .GetFrames()
                        .Any(frame =>
                            frame.GetMethod()?.DeclaringType == typeof(File)
                            && frame.GetMethod()?.Name == nameof(File.Move)
                        ),
                    Is.True,
                    "The IOException must come from final publication after serialization and writer disposal."
                );
            }
            Assert.That(File.Exists(path), Is.EqualTo(mode == "prior-report"));
            if (mode == "prior-report")
            {
                Assert.That(File.ReadAllText(path), Is.EqualTo("prior report"));
            }
            Assert.That(Directory.GetFiles(directory, "*.tmp"), Is.EqualTo(new[] { unrelated }));
            Assert.That(File.ReadAllText(unrelated), Is.EqualTo("unrelated evidence"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
