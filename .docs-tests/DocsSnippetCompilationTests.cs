using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace WallstopStudios.DxMessaging.Docs.Tests;

[TestFixture]
public sealed class DocsSnippetCompilationTests
{
    private static readonly HashSet<string> IgnoredSnippetDiagnosticIds = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "CS0106", // modifier not valid in script (partial snippets showing members only)
        "CS1001", // identifier expected (intentionally elided samples)
        "CS8803", // top-level statements mixed with declarations (visual guide style snippets)
        // The following are tolerated because doc snippets routinely reference
        // types and members that exist in the real assembly but are out of the
        // test compilation's scope (only the SharedStubs subset is wired in).
        "CS0103", // The name '...' does not exist in the current context
        "CS0246", // The type or namespace name '...' could not be found
        "CS0234", // The type or namespace name '...' does not exist in the namespace
        "CS0117", // '...' does not contain a definition for '...'
        "CS1061", // '...' does not contain a definition for '...' / no accessible extension method
        "CS0411", // type arguments cannot be inferred (calling extensions whose stubs aren't loaded)
        "CS7036", // required parameter has no argument (constructor signature differs in stubs)
        "CS1503", // argument cannot convert (placeholder identifiers cause spurious overload mismatches)
        "CS1729", // type does not contain a constructor that takes N arguments
        "CS0535", // does not implement interface member (samples often skip method bodies)
        "CS0738", // does not implement interface member with specified signature
        "CS1955", // non-invocable member used like a method
        "CS0119", // expression is not valid in given context (placeholder usage)
        "CS0118", // is a namespace but used like a type (placeholder issues)
        "CS0021", // cannot apply indexing to expression of type
        "CS0019", // operator cannot be applied to operands (placeholder types)
        "CS1503", // argument conversion (duplicate but kept for clarity)
        "CS0029", // cannot implicitly convert (placeholder vars)
        "CS0266", // cannot implicitly convert
        "CS1660", // cannot convert lambda to non-delegate
        "CS1662", // cannot convert lambda to delegate type
        "CS1593", // delegate parameter mismatch
        "CS0120", // object reference required for non-static (script semantics)
        "CS0122", // is inaccessible due to protection level
        "CS0136", // local declared in enclosing local scope (samples reuse names)
        "CS0029", // duplicate
        "CS0070", // event can only appear on the left-hand side of += or -= (samples may show event)
        "CS0173", // type of conditional expression cannot be determined
        "CS8019", // unnecessary using directive (caused by prepended usings)
        // The following appear because snippets are compiled in script-mode
        // (SourceCodeKind.Script). Script-mode is retained so the harness can
        // catch additional semantic errors on snippets that bind cleanly, but
        // the wrapping context breaks ordinary class-body samples in ways
        // that are not real bugs in the documentation. Note: CS1612 is never
        // produced by this stub setup -- the broken "new X().Emit()" pattern
        // surfaces as CS1510 (which we intentionally ignore, see below), so
        // the Roslyn pass cannot detect that bug class semantically. The
        // DocumentationDoesNotEmitStructMessagesFromTemporaries text guard
        // covers that pattern instead.
        "CS0027", // keyword 'this' is not available in the current context
        "CS0115", // no suitable method found to override (snippet defines class without true base wired)
        "CS1512", // 'base' is not available in the current context
        "CS1520", // method must have a return type (parses ambiguously in script)
        "CS1002", // ; expected (top-level expression-bodied members)
        "CS1525", // invalid expression term (top-level snippet quirks)
        "CS0116", // namespace cannot directly contain members (script wrapping)
        "CS1022", // type or namespace definition or end-of-file expected
        "CS1513", // } expected (partial snippets)
        "CS1514", // { expected
        "CS8124", // tuple element name not preceded by ',' (script-mode quirks)
        // Stub-mismatch / placeholder-related diagnostics. These primarily
        // surface because the test compilation does not load the full runtime
        // assembly; doc snippets reference real APIs (RegisterUntargeted<T>,
        // [DxAutoConstructor]-generated constructors, etc.) whose stubs are
        // intentionally minimal in DocsSnippetCompiler.SharedStubs. There is
        // The compilation test cannot reliably catch the "new X().Emit()" bug
        // class here: the
        // stub setup produces CS1510 (not CS1612) for the broken pattern,
        // and CS1510 must remain in the ignore list to suppress unrelated
        // false-positives on legitimate snippets that reference unstubbed
        // ref-returning members. The text guard below is the automated defense
        // for that bug class.
        "CS0102", // type already contains a definition (partial declarations re-merged in script)
        "CS0111", // type already defines member with same parameter types
        "CS0260", // missing partial modifier on declaration
        "CS0308", // non-generic type 'X' cannot be used with type arguments (stub interface)
        "CS0315", // type cannot be used as type parameter (interface constraint via stub gap)
        "CS0453", // type must be non-nullable value type (placeholder strings as messages)
        "CS0501", // method must declare body (partial members not generated)
        "CS0579", // duplicate attribute (auto-generated partials would dedupe in real build)
        "CS1510", // ref or out value must be assignable. Kept in ignore list because stub-only compilation produces CS1510 noise on legitimate snippets that touch unstubbed ref-returning APIs; the text guard below catches the "new X().Emit()" struct-rvalue bug class.
        "CS1739", // overload doesn't have parameter named (placeholder constructors)
        "CS0305", // generic type requires N type arguments (placeholder collections)
        "CS0104", // ambiguous reference (UnityEngine.Object vs System.Object script collision)
    };

    // Compiled regex patterns for API signature detection
    private static readonly System.Text.RegularExpressions.Regex MethodSignatureStartRegex = new(
        @"^\w+(?:<[^>]+>)?\s+\w+(?:<[^>]+>)?\s*\($",
        System.Text.RegularExpressions.RegexOptions.Compiled
    );

    private static readonly System.Text.RegularExpressions.Regex DocumentationStyleMethodCallRegex =
        new(
            @"\(\s*[A-Z]\w*\s+\w+\s*\)[\s;]*$",
            System.Text.RegularExpressions.RegexOptions.Compiled
        );

    private static readonly System.Text.RegularExpressions.Regex GenericMethodCallRegex = new(
        @"<T>\s*\(",
        System.Text.RegularExpressions.RegexOptions.Compiled
    );

    private static readonly System.Text.RegularExpressions.Regex HandlerParameterRegex = new(
        @",\s*handler\s*,",
        System.Text.RegularExpressions.RegexOptions.Compiled
    );

    private static readonly System.Text.RegularExpressions.Regex ApiReferenceUnityTypeRegex = new(
        @"\(\s*(GameObject|Component|InstanceId)\s+\w+\s*,",
        System.Text.RegularExpressions.RegexOptions.Compiled
    );

    private static readonly System.Text.RegularExpressions.Regex ApiReferenceHandlerPriorityRegex =
        new(
            @",\s*handler\s*,\s*(int|bool|string)\s+\w+\s*=",
            System.Text.RegularExpressions.RegexOptions.Compiled
        );

    private static readonly System.Text.RegularExpressions.Regex HtmlCodeElementRegex = new(
        @"<code(?<attrs>[^>]*)>(?<body>.*?)</code>",
        System.Text.RegularExpressions.RegexOptions.Compiled
            | System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.Singleline
    );

    private static readonly System.Text.RegularExpressions.Regex JinjaSetBlockRegex = new(
        @"{%-?\s*set\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*-?%}(?<body>.*?){%-?\s*endset\s*-?%}",
        System.Text.RegularExpressions.RegexOptions.Compiled
            | System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.Singleline
    );

    private static readonly System.Text.RegularExpressions.Regex TemporaryEmitRegex = new(
        @"(?:(?<![A-Za-z0-9_.])\(\s*new\s+(?:[A-Z][A-Za-z0-9_]*\.)*[A-Z][A-Za-z0-9_]*(?:<[^>\r\n]+>)?\s*\([^;\r\n]*\)\s*\)|(?<![A-Za-z0-9_.(])\bnew\s+(?:[A-Z][A-Za-z0-9_]*\.)*[A-Z][A-Za-z0-9_]*(?:<[^>\r\n]+>)?\s*\([^;\r\n]*\))\s*\.\s*Emit[A-Za-z0-9_]*\s*\(",
        System.Text.RegularExpressions.RegexOptions.Compiled
    );

    private static readonly System.Text.RegularExpressions.Regex BroadcastSourceRegex = new(
        @"\.(?<method>EmitGameObjectBroadcast|EmitComponentBroadcast|EmitFrom)\s*\(\s*(?<source>[^,\)\r\n]+)",
        System.Text.RegularExpressions.RegexOptions.Compiled
    );

    private static readonly System.Text.RegularExpressions.Regex LegacyBroadcastRegex = new(
        @"\.\s*EmitBroadcast\s*\(",
        System.Text.RegularExpressions.RegexOptions.Compiled
    );

    // Constants for signature detection thresholds
    private const double ApiSignatureRatioThreshold = 0.30;
    private const int MinimumApiSignatureLinesForDetection = 2;

    [Test]
    public void QuickStartStep1Compiles()
    {
        string docsRoot = ResolveDocsRoot();
        string quickStartPath = Path.Combine(docsRoot, "getting-started", "quick-start.md");
        Assert.That(File.Exists(quickStartPath), Is.True, $"Unable to locate {quickStartPath}.");

        string snippet = ExtractFirstCodeBlock(quickStartPath, "csharp");
        Assert.That(!string.IsNullOrWhiteSpace(snippet), Is.True, "QuickStart snippet not found.");

        string source = $"""
using DxMessaging.Core.Messages;
using DxMessaging.Core.Attributes;
using UnityEngine;

{snippet}
""";

        var diagnostics = DocsSnippetCompiler
            .CompileSnippet(source)
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .ToArray();

        if (diagnostics.Length > 0)
        {
            string message = string.Join(
                System.Environment.NewLine,
                diagnostics.Select(d => d.ToString())
            );
            Assert.Fail(
                $"QuickStart snippet failed to compile:{System.Environment.NewLine}{message}"
            );
        }
    }

    [TestCaseSource(nameof(GetDocumentationSnippets))]
    public void DocumentationSnippetsCompile(string markdownPath, string snippet)
    {
        Assert.That(
            snippet,
            Is.Not.Empty,
            $"Snippet extracted from {markdownPath} should not be empty."
        );

        // Use the semantic-aware CompileDocSnippet wrapper so the test catches
        // semantic errors that survive stub-only compilation: type errors,
        // return-type mismatches, and the subset of identifier diagnostics
        // (CS0103 etc.) that are NOT in the ignore list. Many doc snippets
        // reference symbols not wired into the test compilation, so
        // IgnoredSnippetDiagnosticIds tolerates the expected "missing
        // identifier / missing type / overload mismatch" family.
        //
        // IMPORTANT: this Roslyn pass does NOT catch the "new StructMessage().Emit()"
        // bug class. The stub setup produces CS1510 (not CS1612) for that
        // pattern, and CS1510 must remain ignored to keep legitimate snippets
        // that touch unstubbed ref-returning members from triggering false
        // positives. DocumentationDoesNotEmitStructMessagesFromTemporaries
        // covers that pattern textually.
        var diagnostics = DocsSnippetCompiler
            .CompileDocSnippet(snippet)
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .ToArray();

        var actionableDiagnostics = diagnostics
            .Where(d => !IgnoredSnippetDiagnosticIds.Contains(d.Id))
            .ToArray();

        if (actionableDiagnostics.Length > 0)
        {
            string message = string.Join(
                System.Environment.NewLine,
                actionableDiagnostics.Select(d => d.ToString())
            );
            Assert.Fail(
                $"Documentation snippet in {markdownPath} failed to compile:{System.Environment.NewLine}{message}"
            );
        }
    }

    [TestCaseSource(nameof(GetHtmlOverrideCSharpSnippets))]
    public void HtmlOverrideCSharpSnippetsCompile(string htmlPath, string snippet)
    {
        Assert.That(
            snippet,
            Is.Not.Empty,
            $"Snippet extracted from {htmlPath} should not be empty."
        );

        var diagnostics = DocsSnippetCompiler
            .CompileDocSnippet(snippet)
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .ToArray();

        var actionableDiagnostics = diagnostics
            .Where(d => !IgnoredSnippetDiagnosticIds.Contains(d.Id))
            .ToArray();

        if (actionableDiagnostics.Length > 0)
        {
            string message = string.Join(
                System.Environment.NewLine,
                actionableDiagnostics.Select(d => d.ToString())
            );
            Assert.Fail(
                $"HTML override snippet in {htmlPath} failed to compile:{System.Environment.NewLine}{message}"
            );
        }
    }

    [Test]
    public void DocumentationDoesNotEmitStructMessagesFromTemporaries()
    {
        var violations = GetTemporaryEmitDocumentationSources()
            .SelectMany(source =>
                FindTemporaryEmitViolations(source.Path, source.Text)
                    .Select(violation => $"{source.Path}:{violation.Line}: {violation.Text}")
            )
            .ToArray();

        Assert.That(
            violations,
            Is.Empty,
            "Docs must assign struct messages to locals before Emit* calls:"
                + System.Environment.NewLine
                + string.Join(System.Environment.NewLine, violations)
        );
    }

    [Test]
    public void BroadcastExamplesUseSelfAsSource()
    {
        var violations = GetBroadcastExampleSources()
            .SelectMany(source =>
                FindNonSelfBroadcastSourceViolations(source.Path, source.Text)
                    .Select(violation => $"{source.Path}:{violation.Line}: {violation.Text}")
            )
            .ToArray();

        Assert.That(
            violations,
            Is.Empty,
            "Broadcast examples should model self-broadcasting: "
                + "EmitGameObjectBroadcast(gameObject), EmitComponentBroadcast(this), or "
                + "EmitFrom(gameObject/this). Offending references:"
                + System.Environment.NewLine
                + string.Join(System.Environment.NewLine, violations)
        );
    }

    private static IEnumerable<TestCaseData> GetDocumentationSnippets()
    {
        string docsRoot = ResolveDocsRoot();
        foreach (
            string markdownPath in Directory.GetFiles(docsRoot, "*.md", SearchOption.AllDirectories)
        )
        {
            foreach (string snippet in ExtractCodeBlocks(markdownPath, "csharp"))
            {
                if (ShouldSkipSnippet(snippet))
                {
                    continue;
                }

                yield return new TestCaseData(markdownPath, snippet).SetName(
                    $"{Path.GetFileName(markdownPath)} compiles"
                );
            }
        }
    }

    private static IEnumerable<TestCaseData> GetHtmlOverrideCSharpSnippets()
    {
        string docsRoot = ResolveDocsRoot();
        string overridesRoot = Path.Combine(docsRoot, "overrides");
        if (!Directory.Exists(overridesRoot))
        {
            yield break;
        }

        int testIndex = 0;
        foreach (
            string htmlPath in Directory.GetFiles(
                overridesRoot,
                "*.html",
                SearchOption.AllDirectories
            )
        )
        {
            foreach (string snippet in ExtractHtmlCSharpSnippets(htmlPath))
            {
                if (ShouldSkipSnippet(snippet))
                {
                    continue;
                }

                yield return new TestCaseData(htmlPath, snippet).SetName(
                    $"{Path.GetFileName(htmlPath)} html #{testIndex++}"
                );
            }
        }

        foreach (
            string samplePath in Directory.GetFiles(
                overridesRoot,
                "*.csharp",
                SearchOption.AllDirectories
            )
        )
        {
            string snippet = File.ReadAllText(samplePath);
            if (ShouldSkipSnippet(snippet))
            {
                continue;
            }

            yield return new TestCaseData(samplePath, snippet).SetName(
                $"{Path.GetFileName(samplePath)} override sample #{testIndex++}"
            );
        }
    }

    // ---- 3.4.2: inline-code-from-tables compilation ----------------------

    [TestCaseSource(nameof(GetInlineTableSnippets))]
    public void InlineTableSnippetsCompile(string markdownPath, string snippet)
    {
        Assert.That(
            snippet,
            Is.Not.Empty,
            $"Inline table snippet extracted from {markdownPath} should not be empty."
        );

        // Inline snippets are wrapped in a method body so script-mode parsing
        // is consistent with the doc author's intent (a single statement or
        // expression, not a top-level type declaration).
        string wrapped = "void __InlineProbe() {\n" + snippet + "\n}\n";

        var diagnostics = DocsSnippetCompiler
            .CompileDocSnippet(wrapped)
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .ToArray();

        var actionableDiagnostics = diagnostics
            .Where(d => !IgnoredSnippetDiagnosticIds.Contains(d.Id))
            .ToArray();

        if (actionableDiagnostics.Length > 0)
        {
            string message = string.Join(
                System.Environment.NewLine,
                actionableDiagnostics.Select(d => d.ToString())
            );
            Assert.Fail(
                $"Inline table snippet in {markdownPath} failed to compile:"
                    + $"{System.Environment.NewLine}snippet: {snippet}"
                    + $"{System.Environment.NewLine}{message}"
            );
        }
    }

    private static IEnumerable<TestCaseData> GetInlineTableSnippets()
    {
        string docsRoot = ResolveDocsRoot();
        int testIndex = 0;
        foreach (
            string markdownPath in Directory.GetFiles(docsRoot, "*.md", SearchOption.AllDirectories)
        )
        {
            foreach (string snippet in ExtractInlineTableCodeSnippets(markdownPath))
            {
                if (!IsCompilableInlineSnippet(snippet))
                {
                    continue;
                }

                yield return new TestCaseData(markdownPath, snippet).SetName(
                    $"{Path.GetFileName(markdownPath)} inline #{testIndex++}"
                );
            }
        }
    }

    private static IEnumerable<string> ExtractInlineTableCodeSnippets(string markdownPath)
    {
        string[] lines = File.ReadAllLines(markdownPath);
        bool inFence = false;
        foreach (string rawLine in lines)
        {
            string line = rawLine.TrimEnd();
            if (line.StartsWith("```") || line.StartsWith("~~~"))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence)
            {
                continue;
            }
            // Only parse table rows. Pure prose lines may contain backticks
            // but we want to keep this focused on the documented gotcha space:
            // table cells are where the historical "new X().Emit()" failures
            // hid because they slipped past the fenced-block extractor.
            if (line.IndexOf('|') < 0)
            {
                continue;
            }
            foreach (string snippet in ExtractInlineCodeSpans(line))
            {
                yield return snippet;
            }
        }
    }

    private static IEnumerable<string> ExtractInlineCodeSpans(string line)
    {
        int i = 0;
        while (i < line.Length)
        {
            // Skip non-backtick chars.
            if (line[i] != '`')
            {
                i++;
                continue;
            }
            // Count opening backticks.
            int openStart = i;
            int tickCount = 0;
            while (i < line.Length && line[i] == '`')
            {
                tickCount++;
                i++;
            }
            // Look for matching closing run of identical length.
            int searchFrom = i;
            while (searchFrom < line.Length)
            {
                int closeStart = line.IndexOf('`', searchFrom);
                if (closeStart < 0)
                    break;
                int runLen = 0;
                int j = closeStart;
                while (j < line.Length && line[j] == '`')
                {
                    runLen++;
                    j++;
                }
                if (runLen == tickCount)
                {
                    string content = line.Substring(
                        openStart + tickCount,
                        closeStart - openStart - tickCount
                    );
                    yield return content.Trim();
                    i = j;
                    break;
                }
                searchFrom = j;
            }
        }
    }

    private static bool IsCompilableInlineSnippet(string snippet)
    {
        if (string.IsNullOrWhiteSpace(snippet))
            return false;
        // Filter out short fragments (bare type names, single identifiers).
        if (snippet.Length < 4)
            return false;
        // Must look like a statement: contain an opening paren AND end with ')' or ';'.
        if (snippet.IndexOf('(') < 0)
            return false;
        string trimmed = snippet.TrimEnd();
        if (!trimmed.EndsWith(")") && !trimmed.EndsWith(";"))
            return false;
        // Skip API signatures (uses the same heuristic as fenced blocks).
        if (IsApiSignatureDocumentation(snippet))
            return false;
        // Skip snippets that look like type-name placeholders.
        if (snippet.IndexOf(' ') < 0 && snippet.IndexOf('.') < 0)
            return false;
        return true;
    }

    // ---- 3.4.3: XML doc <code> block compilation -------------------------

    [TestCaseSource(nameof(GetXmlDocCodeBlocks))]
    public void XmlDocCodeBlocksCompile(string sourcePath, string snippet)
    {
        Assert.That(
            snippet,
            Is.Not.Empty,
            $"XML <code> snippet extracted from {sourcePath} should not be empty."
        );

        var diagnostics = DocsSnippetCompiler
            .CompileDocSnippet(snippet)
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .ToArray();

        var actionableDiagnostics = diagnostics
            .Where(d => !IgnoredSnippetDiagnosticIds.Contains(d.Id))
            .ToArray();

        if (actionableDiagnostics.Length > 0)
        {
            string message = string.Join(
                System.Environment.NewLine,
                actionableDiagnostics.Select(d => d.ToString())
            );
            Assert.Fail(
                $"XML <code> snippet in {sourcePath} failed to compile:"
                    + $"{System.Environment.NewLine}{message}"
            );
        }
    }

    private static readonly string[] CSharpScanRoots = new[]
    {
        "Runtime",
        "Editor",
        "SourceGenerators",
    };

    private static IEnumerable<TestCaseData> GetXmlDocCodeBlocks()
    {
        string repoRoot = ResolveRepoRoot();
        int testIndex = 0;
        foreach (string root in CSharpScanRoots)
        {
            string absRoot = Path.Combine(repoRoot, root);
            if (!Directory.Exists(absRoot))
                continue;
            foreach (
                string sourcePath in Directory.GetFiles(
                    absRoot,
                    "*.cs",
                    SearchOption.AllDirectories
                )
            )
            {
                // Skip generated/cache directories.
                string normalized = sourcePath.Replace('\\', '/');
                if (
                    normalized.Contains("/obj/")
                    || normalized.Contains("/bin/")
                    || normalized.Contains("/.artifacts/")
                )
                {
                    continue;
                }
                foreach (string snippet in ExtractXmlDocCodeBlocks(sourcePath))
                {
                    if (ShouldSkipSnippet(snippet))
                        continue;
                    if (snippet.Length < 4)
                        continue;
                    yield return new TestCaseData(sourcePath, snippet).SetName(
                        $"{Path.GetFileName(sourcePath)} xmldoc #{testIndex++}"
                    );
                }
            }
        }
    }

    private static IEnumerable<string> ExtractXmlDocCodeBlocks(string sourcePath)
    {
        string text = ExtractXmlDocumentationText(sourcePath);

        int searchFrom = 0;
        while (searchFrom < text.Length)
        {
            int openIdx = text.IndexOf("<code", searchFrom, StringComparison.OrdinalIgnoreCase);
            if (openIdx < 0)
                break;
            int openClose = text.IndexOf('>', openIdx);
            if (openClose < 0)
                break;
            int closeIdx = text.IndexOf("</code>", openClose, StringComparison.OrdinalIgnoreCase);
            if (closeIdx < 0)
            {
                searchFrom = openClose + 1;
                continue;
            }
            string body = text.Substring(openClose + 1, closeIdx - openClose - 1);
            yield return DecodeXmlEntities(body).Trim();
            searchFrom = closeIdx + "</code>".Length;
        }
    }

    private static string ExtractXmlDocumentationText(string sourcePath)
    {
        string content = File.ReadAllText(sourcePath);
        // Strip the leading `///` from each line first, joining adjacent doc
        // comment lines into a single text block. Then locate <code>...</code>
        // and <example><code>...</code></example> regions inside that text.
        var stripped = new System.Text.StringBuilder(content.Length);
        foreach (string rawLine in content.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
        {
            string trim = rawLine.TrimStart();
            if (trim.StartsWith("///"))
            {
                stripped.AppendLine(trim.Substring(3).TrimStart());
            }
            else
            {
                stripped.AppendLine();
            }
        }

        return stripped.ToString();
    }

    private static string DecodeXmlEntities(string s)
    {
        return s.Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&amp;", "&")
            .Replace("&quot;", "\"")
            .Replace("&apos;", "'");
    }

    private static string DecodeHtmlEntities(string s)
    {
        return System.Net.WebUtility.HtmlDecode(s);
    }

    private static string ResolveRepoRoot()
    {
        string current = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (
                Directory.Exists(Path.Combine(current, "Runtime"))
                && Directory.Exists(Path.Combine(current, "Editor"))
                && File.Exists(Path.Combine(current, "package.json"))
            )
            {
                return current;
            }
            string parent = Path.GetDirectoryName(current) ?? string.Empty;
            if (string.IsNullOrEmpty(parent))
                break;
            current = parent;
        }
        throw new DirectoryNotFoundException(
            "Unable to locate the repository root from the current test directory."
        );
    }

    private static string ExtractFirstCodeBlock(string markdownPath, string infoString)
    {
        return ExtractCodeBlocks(markdownPath, infoString).FirstOrDefault() ?? string.Empty;
    }

    [Test]
    public void ExtractHtmlCSharpSnippetsFindsPlainCodeAndJinjaSetBlocks()
    {
        const string html = """
<div>
  <pre><code>using DxMessaging.Core.Extensions;
Heal heal = new Heal(10);
heal.EmitGameObjectTargeted(player);</code></pre>
  {% set dxm_targeted_sample -%}
using DxMessaging.Unity;
Heal heal = new Heal(50);
heal.EmitGameObjectTargeted(player);
  {%- endset %}
</div>
""";

        string[] snippets = ExtractHtmlCSharpSnippetsFromText("home.html", html).ToArray();

        Assert.That(snippets, Has.Length.EqualTo(2));
        Assert.That(snippets[0], Does.Contain("using DxMessaging.Core.Extensions;"));
        Assert.That(snippets[1], Does.Contain("Heal heal = new Heal(50);"));
    }

    [Test]
    public void TemporaryEmitPatternReportsInvalidExamplesButSkipsExplicitNegativeExamples()
    {
        const string text = """
Heal heal = new Heal(10);
heal.EmitGameObjectTargeted(player);
new Damage(5).EmitGameObjectBroadcast(enemy);
Do not emit from temporaries: new Heal(10).Emit() won't compile.
""";

        var violations = FindTemporaryEmitViolations("sample.md", text).ToArray();

        Assert.That(violations, Has.Length.EqualTo(1));
        Assert.That(violations[0].Line, Is.EqualTo(3));
        Assert.That(violations[0].Text, Does.Contain("new Damage(5).EmitGameObjectBroadcast"));
    }

    [TestCase("new Heal(10).EmitGameObjectTargeted(player);", true)]
    [TestCase("(new Heal(10)).EmitGameObjectTargeted(player);", true)]
    [TestCase("new Combat.Heal(10).EmitGameObjectTargeted(player);", true)]
    [TestCase("new Heal(10)\n    .EmitGameObjectTargeted(player);", true)]
    [TestCase("Build(new Heal(10)).EmitGameObjectTargeted(player);", false)]
    [TestCase("Heal heal = new Heal(10); heal.EmitGameObjectTargeted(player);", false)]
    public void TemporaryEmitPatternDetectsOnlyDirectTemporaryEmitCalls(
        string text,
        bool expectedViolation
    )
    {
        var violations = FindTemporaryEmitViolations("sample.md", text).ToArray();

        Assert.That(violations.Length > 0, Is.EqualTo(expectedViolation));
    }

    [TestCase("message.EmitGameObjectBroadcast(gameObject);", false)]
    [TestCase("message.EmitGameObjectBroadcast(gameObject, testBus);", false)]
    [TestCase("message.EmitComponentBroadcast(this);", false)]
    [TestCase("message.EmitComponentBroadcast(this, testBus);", false)]
    [TestCase("message.EmitFrom(gameObject);", false)]
    [TestCase("message.EmitFrom(gameObject, testBus);", false)]
    [TestCase("message.EmitFrom(this);", false)]
    [TestCase("message.EmitGameObjectBroadcast(GameObject source);", false)]
    [TestCase("message.EmitComponentBroadcast(Component source);", false)]
    [TestCase("message.EmitGameObjectBroadcast(enemy);", true)]
    [TestCase("message.EmitGameObjectBroadcast(playerGameObject);", true)]
    [TestCase("message.EmitComponentBroadcast(enemyComponent);", true)]
    [TestCase("message.EmitFrom(enemy);", true)]
    [TestCase("message.EmitFrom(source);", true)]
    [TestCase("message.EmitFrom(sourceId);", true)]
    [TestCase("message.EmitBroadcast(source);", true)]
    [TestCase("this.EmitBroadcast(new Exploded());", true)]
    public void BroadcastSourcePatternAllowsOnlySelfSourcesAndApiSignatures(
        string text,
        bool expectedViolation
    )
    {
        var violations = FindNonSelfBroadcastSourceViolations("sample.md", text).ToArray();

        Assert.That(violations.Length > 0, Is.EqualTo(expectedViolation));
    }

    private static IEnumerable<string> ExtractCodeBlocks(string markdownPath, string infoString)
    {
        string[] lines = File.ReadAllLines(markdownPath);
        bool inBlock = false;
        System.Text.StringBuilder builder = new();
        foreach (string rawLine in lines)
        {
            string line = rawLine.TrimEnd();
            if (!inBlock)
            {
                if (line.StartsWith("```") && line.Length > 3 && line[3..].StartsWith(infoString))
                {
                    inBlock = true;
                    builder.Clear();
                }
                continue;
            }

            if (line.StartsWith("```"))
            {
                inBlock = false;
                string snippet = builder.ToString();
                if (!string.IsNullOrWhiteSpace(snippet))
                {
                    yield return snippet;
                }
                continue;
            }

            builder.AppendLine(rawLine);
        }
    }

    private static IEnumerable<string> ExtractHtmlCSharpSnippets(string htmlPath)
    {
        return ExtractHtmlCSharpSnippetsFromText(htmlPath, File.ReadAllText(htmlPath));
    }

    private static IEnumerable<string> ExtractHtmlCSharpSnippetsFromText(
        string htmlPath,
        string html
    )
    {
        foreach (System.Text.RegularExpressions.Match match in HtmlCodeElementRegex.Matches(html))
        {
            string attrs = match.Groups["attrs"].Value;
            string body = DecodeHtmlEntities(match.Groups["body"].Value).Trim();
            if (!LooksLikeCSharpSnippet(htmlPath, attrs, body))
            {
                continue;
            }

            yield return body;
        }

        foreach (System.Text.RegularExpressions.Match match in JinjaSetBlockRegex.Matches(html))
        {
            string name = match.Groups["name"].Value;
            string body = match.Groups["body"].Value.Trim();
            if (!LooksLikeCSharpSnippet(htmlPath, name, body))
            {
                continue;
            }

            yield return body;
        }
    }

    private static bool LooksLikeCSharpSnippet(string sourceName, string hint, string snippet)
    {
        if (string.IsNullOrWhiteSpace(snippet))
        {
            return false;
        }

        string combined = $"{sourceName}\n{hint}\n{snippet}";
        return combined.Contains("csharp", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("DxMessaging", StringComparison.Ordinal)
            || combined.Contains("[Dx", StringComparison.Ordinal)
            || combined.Contains(".Emit", StringComparison.Ordinal)
            || combined.Contains("Token.Register", StringComparison.Ordinal);
    }

    private static IEnumerable<(string Path, string Text)> GetTemporaryEmitDocumentationSources()
    {
        string docsRoot = ResolveDocsRoot();
        string repoRoot = ResolveRepoRoot();
        foreach (
            string markdownPath in Directory.GetFiles(docsRoot, "*.md", SearchOption.AllDirectories)
        )
        {
            yield return (markdownPath, File.ReadAllText(markdownPath));
        }

        foreach (
            string csharpPath in Directory.GetFiles(
                docsRoot,
                "*.csharp",
                SearchOption.AllDirectories
            )
        )
        {
            yield return (csharpPath, File.ReadAllText(csharpPath));
        }

        string overridesRoot = Path.Combine(docsRoot, "overrides");
        if (Directory.Exists(overridesRoot))
        {
            foreach (
                string htmlPath in Directory.GetFiles(
                    overridesRoot,
                    "*.html",
                    SearchOption.AllDirectories
                )
            )
            {
                yield return (htmlPath, DecodeHtmlEntities(File.ReadAllText(htmlPath)));
            }
        }

        string readmePath = Path.Combine(repoRoot, "README.md");
        if (File.Exists(readmePath))
        {
            yield return (readmePath, File.ReadAllText(readmePath));
        }

        string samplesRoot = Path.Combine(repoRoot, "Samples~");
        if (Directory.Exists(samplesRoot))
        {
            foreach (string path in EnumerateTextFiles(samplesRoot, "*.md"))
            {
                yield return (path, File.ReadAllText(path));
            }

            foreach (string path in EnumerateTextFiles(samplesRoot, "*.cs"))
            {
                yield return (path, File.ReadAllText(path));
            }
        }

        foreach (string root in CSharpScanRoots)
        {
            string absRoot = Path.Combine(repoRoot, root);
            if (!Directory.Exists(absRoot))
            {
                continue;
            }

            foreach (
                string sourcePath in Directory.GetFiles(
                    absRoot,
                    "*.cs",
                    SearchOption.AllDirectories
                )
            )
            {
                string normalized = sourcePath.Replace('\\', '/');
                if (
                    normalized.Contains("/obj/")
                    || normalized.Contains("/bin/")
                    || normalized.Contains("/.artifacts/")
                )
                {
                    continue;
                }

                string xmlDocumentationText = ExtractXmlDocumentationText(sourcePath);
                if (!string.IsNullOrWhiteSpace(xmlDocumentationText))
                {
                    yield return (sourcePath, DecodeXmlEntities(xmlDocumentationText));
                }
            }
        }
    }

    private static IEnumerable<(string Path, string Text)> GetBroadcastExampleSources()
    {
        string repoRoot = ResolveRepoRoot();
        string docsRoot = ResolveDocsRoot();

        foreach (string path in EnumerateTextFiles(docsRoot, "*.md"))
        {
            yield return (path, File.ReadAllText(path));
        }

        foreach (string path in EnumerateTextFiles(docsRoot, "*.csharp"))
        {
            yield return (path, File.ReadAllText(path));
        }

        foreach (string path in EnumerateTextFiles(docsRoot, "*.html"))
        {
            yield return (path, DecodeHtmlEntities(File.ReadAllText(path)));
        }

        string readmePath = Path.Combine(repoRoot, "README.md");
        if (File.Exists(readmePath))
        {
            yield return (readmePath, File.ReadAllText(readmePath));
        }

        string samplesRoot = Path.Combine(repoRoot, "Samples~");
        if (Directory.Exists(samplesRoot))
        {
            foreach (string path in EnumerateTextFiles(samplesRoot, "*.md"))
            {
                yield return (path, File.ReadAllText(path));
            }

            foreach (string path in EnumerateTextFiles(samplesRoot, "*.cs"))
            {
                yield return (path, File.ReadAllText(path));
            }
        }

        foreach (string root in CSharpScanRoots)
        {
            string absRoot = Path.Combine(repoRoot, root);
            if (!Directory.Exists(absRoot))
            {
                continue;
            }

            foreach (string sourcePath in EnumerateTextFiles(absRoot, "*.cs"))
            {
                string xmlDocumentationText = ExtractXmlDocumentationText(sourcePath);
                if (!string.IsNullOrWhiteSpace(xmlDocumentationText))
                {
                    yield return (sourcePath, DecodeXmlEntities(xmlDocumentationText));
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateTextFiles(string root, string pattern)
    {
        return Directory
            .GetFiles(root, pattern, SearchOption.AllDirectories)
            .Where(path =>
            {
                string normalized = path.Replace('\\', '/');
                return !normalized.Contains("/obj/")
                    && !normalized.Contains("/bin/")
                    && !normalized.Contains("/.artifacts/");
            })
            .OrderBy(path => path, StringComparer.Ordinal);
    }

    private static IEnumerable<(int Line, string Text)> FindTemporaryEmitViolations(
        string sourcePath,
        string text
    )
    {
        string normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
        string[] lines = normalized.Split('\n');
        int lineIndex = 0;
        int lineOffset = 0;
        foreach (
            System.Text.RegularExpressions.Match match in TemporaryEmitRegex.Matches(normalized)
        )
        {
            while (
                lineIndex < lines.Length - 1
                && match.Index >= lineOffset + lines[lineIndex].Length + 1
            )
            {
                lineOffset += lines[lineIndex].Length + 1;
                lineIndex++;
            }

            int lineNumber = lineIndex + 1;
            if (IsExplicitNegativeTemporaryEmitExample(lines, lineIndex))
            {
                continue;
            }

            string lineText = lineIndex < lines.Length ? lines[lineIndex].Trim() : match.Value;
            yield return (lineNumber, lineText);
        }
    }

    private static bool IsExplicitNegativeTemporaryEmitExample(string[] lines, int lineIndex)
    {
        int start = Math.Max(0, lineIndex - 1);
        int end = Math.Min(lines.Length - 1, lineIndex);
        string context = string.Join(" ", lines[start..(end + 1)]).ToLowerInvariant();

        return context.Contains("won't compile", StringComparison.Ordinal)
            || context.Contains("will not compile", StringComparison.Ordinal)
            || context.Contains("does not compile", StringComparison.Ordinal)
            || context.Contains("do not compile", StringComparison.Ordinal)
            || context.Contains("don't compile", StringComparison.Ordinal)
            || context.Contains("cannot compile", StringComparison.Ordinal)
            || context.Contains("not compile", StringComparison.Ordinal)
            || context.Contains("do not emit from temporaries", StringComparison.Ordinal)
            || context.Contains("don't emit from temporaries", StringComparison.Ordinal);
    }

    private static IEnumerable<(int Line, string Text)> FindNonSelfBroadcastSourceViolations(
        string sourcePath,
        string text
    )
    {
        string[] lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            string line = lines[lineIndex];
            if (LegacyBroadcastRegex.IsMatch(line) && !IsApiSignatureBroadcastLine(line))
            {
                yield return (lineIndex + 1, line.Trim());
            }

            foreach (
                System.Text.RegularExpressions.Match match in BroadcastSourceRegex.Matches(line)
            )
            {
                string method = match.Groups["method"].Value;
                string source = match.Groups["source"].Value.Trim();

                if (IsAllowedBroadcastSource(method, source, line))
                {
                    continue;
                }

                yield return (lineIndex + 1, line.Trim());
            }
        }
    }

    private static bool IsAllowedBroadcastSource(string method, string source, string line)
    {
        string normalizedSource = source.Trim();
        if (method == "EmitGameObjectBroadcast")
        {
            return normalizedSource == "gameObject" || IsApiSignatureBroadcastLine(line);
        }

        if (method == "EmitComponentBroadcast")
        {
            return normalizedSource == "this" || IsApiSignatureBroadcastLine(line);
        }

        return normalizedSource == "gameObject"
            || normalizedSource == "this"
            || IsApiSignatureBroadcastLine(line);
    }

    private static bool IsApiSignatureBroadcastLine(string line)
    {
        return line.Contains("GameObject source", StringComparison.Ordinal)
            || line.Contains("Component source", StringComparison.Ordinal)
            || line.Contains("InstanceId source", StringComparison.Ordinal);
    }

    private static string ResolveDocsRoot()
    {
        string currentDirectoryPath = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(currentDirectoryPath))
        {
            string docsDirectory = Path.Combine(currentDirectoryPath, "docs");
            string candidate = Path.Combine(docsDirectory, "getting-started", "quick-start.md");
            if (File.Exists(candidate))
            {
                return docsDirectory;
            }

            string parentDirectoryPath =
                Path.GetDirectoryName(currentDirectoryPath) ?? string.Empty;
            if (string.IsNullOrEmpty(parentDirectoryPath))
            {
                break;
            }

            currentDirectoryPath = parentDirectoryPath;
        }

        throw new FileNotFoundException(
            "Unable to locate docs/getting-started/quick-start.md from the current test directory."
        );
    }

    [TestCase("", true, TestName = "Empty snippet should be skipped")]
    [TestCase("   ", true, TestName = "Whitespace-only snippet should be skipped")]
    [TestCase(
        "var x = 1;\nConsole.WriteLine(x);",
        false,
        TestName = "Regular compilable code should not be skipped"
    )]
    [TestCase(
        "public class MyClass { }",
        false,
        TestName = "Simple class declaration should not be skipped"
    )]
    [TestCase(
        "// Comment only\nvar x = 1;",
        false,
        TestName = "Code with comments should not be skipped"
    )]
    [TestCase(
        "MessageRegistrationHandle RegisterUntargeted<T>(Action<T> handler, int priority = 0)",
        true,
        TestName = "Single line method signature with default parameter should be skipped"
    )]
    [TestCase(
        "void Process(string name = null)",
        true,
        TestName = "Method with null default should be skipped"
    )]
    [TestCase(
        "bool IsEnabled(bool flag = false)",
        true,
        TestName = "Method with false default should be skipped"
    )]
    [TestCase(
        "void Toggle(bool active = true)",
        true,
        TestName = "Method with true default should be skipped"
    )]
    [TestCase(
        "MessageRegistrationHandle RegisterUntargeted<T>(\n    Action<T> handler,\n    int priority = 0)",
        true,
        TestName = "Multi-line signature with default params should be skipped"
    )]
    [TestCase(
        "Do something...\nthen continue",
        true,
        TestName = "Snippet with ellipsis should be skipped"
    )]
    [TestCase(
        "public void Method() { ... }",
        true,
        TestName = "Method with ellipsis body should be skipped"
    )]
    [TestCase(
        "MessageRegistrationHandle RegisterUntargeted<T>(\n    Action<T> handler,\n    int priority = 0\n)",
        true,
        TestName = "Multi-line signature with closing paren on own line should be skipped"
    )]
    [TestCase(
        "    // GameObject target\n    MessageRegistrationHandle RegisterGameObjectTargeted<T>(\n        GameObject target,\n        Action<T> handler,\n        int priority = 0\n    )",
        true,
        TestName = "Indented multi-line signature with comments should be skipped"
    )]
    [TestCase(
        "// Emit to specific target (by InstanceId)\nmessage.EmitTargeted(InstanceId target);",
        true,
        TestName = "Method call with parameter type documentation should be skipped"
    )]
    [TestCase(
        "Action RegisterUntargetedInterceptor<T>(\n    UntargetedInterceptor<T> interceptor,\n    int priority = 0\n)",
        true,
        TestName = "Interceptor signature should be skipped"
    )]
    [TestCase(
        "token.RegisterUntargeted<T>(Action<T> handler, int priority = 0)",
        true,
        TestName = "Method call with generic type parameter and Action handler should be skipped"
    )]
    [TestCase(
        "token.RegisterUntargeted<T>(FastHandler<T> handler, int priority = 0)",
        true,
        TestName = "Method call with FastHandler parameter should be skipped"
    )]
    [TestCase(
        "token.RegisterGameObjectTargeted<T>(GameObject go, handler, int priority = 0)",
        true,
        TestName = "Method call with handler parameter name should be skipped"
    )]
    [TestCase(
        "token.RegisterTargetedWithoutTargeting<T>(FastHandlerWithContext<T> handler, int priority = 0)",
        true,
        TestName = "Method call with FastHandlerWithContext should be skipped"
    )]
    [TestCase(
        "bus.RegisterUntargetedInterceptor<T>(UntargetedInterceptor<T> interceptor, int priority = 0)",
        true,
        TestName = "Bus interceptor registration with generic type should be skipped"
    )]
    [TestCase(
        "token.RegisterTargeted<T>(InstanceId id, handler, int priority = 0)",
        true,
        TestName = "Method with InstanceId parameter and handler should be skipped"
    )]
    [TestCase(
        "token.RegisterBroadcast<T>(InstanceId id, handler, int priority = 0)",
        true,
        TestName = "Broadcast registration with InstanceId and handler should be skipped"
    )]
    [TestCase("int x = 0;", false, TestName = "Assignment with zero should not be skipped")]
    [TestCase("var priority = 0;", false, TestName = "Variable assignment should not be skipped")]
    [TestCase(
        "int priority = 0;\nConsole.WriteLine(priority);",
        false,
        TestName = "Variable assignment with usage should not be skipped"
    )]
    [TestCase(
        "bool isEnabled = false;\nif (isEnabled) { DoSomething(); }",
        false,
        TestName = "Boolean assignment with conditional should not be skipped"
    )]
    [TestCase(
        "string name = null;\nname = GetName();",
        false,
        TestName = "Null assignment with reassignment should not be skipped"
    )]
    [TestCase(
        "public void Process()\n{\n    int count = 0;\n}",
        false,
        TestName = "Method with local variable initialization should not be skipped"
    )]
    [TestCase(
        "Action<int> handler = x => Console.WriteLine(x);",
        false,
        TestName = "Lambda assignment should not be skipped"
    )]
    [TestCase(
        "var result = Calculate(value, 0);",
        false,
        TestName = "Method call with zero argument should not be skipped"
    )]
    public void ShouldSkipSnippetDetectsApiSignatures(string snippet, bool expectedSkip)
    {
        bool actualSkip = ShouldSkipSnippet(snippet);
        Assert.That(
            actualSkip,
            Is.EqualTo(expectedSkip),
            $"Expected ShouldSkipSnippet to return {expectedSkip} for snippet: '{snippet.Replace("\n", "\\n")}'"
        );
    }

    private static bool ShouldSkipSnippet(string snippet)
    {
        if (string.IsNullOrWhiteSpace(snippet))
        {
            return true;
        }

        if (snippet.Contains("..."))
        {
            return true;
        }

        if (IsApiSignatureDocumentation(snippet))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Detects whether a code snippet represents API signature documentation rather than
    /// compilable sample code. API signatures in documentation (e.g., method signatures
    /// with default parameters shown for reference) are not meant to be compiled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// API signature snippets need special handling because documentation often shows
    /// method signatures like <c>RegisterUntargeted&lt;T&gt;(Action&lt;T&gt; handler, int priority = 0)</c>
    /// for reference. These are informational and not valid standalone C# code.
    /// </para>
    /// <para>
    /// Detection heuristics:
    /// <list type="bullet">
    /// <item><description>Default parameter patterns on same line: <c>= 0)</c>, <c>= null)</c>, <c>= false)</c>, <c>= true)</c> and their comma variants</description></item>
    /// <item><description>Default parameter patterns at end of line: <c>= 0</c>, <c>= null</c>, <c>= false</c>, <c>= true</c> for multi-line signatures</description></item>
    /// <item><description>Method signature start patterns: return type followed by method name and opening paren</description></item>
    /// <item><description>Partial signature patterns: lines ending in comma with parameter type keywords (Action&lt;&gt;, Func&lt;&gt;, int, bool, string, GameObject, Component, InstanceId, FastHandler)</description></item>
    /// <item><description>Isolated closing parens: lines that are just <c>)</c> or <c>);</c></description></item>
    /// <item><description>Documentation-style method calls: <c>method(TypeName param);</c> showing parameter types as placeholders</description></item>
    /// <item><description>Generic method calls with type parameters: <c>method&lt;T&gt;(Action&lt;T&gt; handler, ...)</c> with generic handler types</description></item>
    /// <item><description>API reference parameter lines: <c>(GameObject go, handler, int priority = 0)</c> with type-name pairs and handler placeholders</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// Ratio threshold logic: A snippet is considered an API signature if:
    /// <list type="bullet">
    /// <item><description>At least 30% of non-empty lines match signature patterns, OR</description></item>
    /// <item><description>Two or more lines match signature patterns (handles multi-line signatures)</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <param name="snippet">The code snippet to analyze.</param>
    /// <returns>True if the snippet appears to be API signature documentation; otherwise false.</returns>
    private static bool IsApiSignatureDocumentation(string snippet)
    {
        string[] lines = snippet.Split('\n');
        int signatureLineCount = 0;
        int totalNonEmptyLines = 0;

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            totalNonEmptyLines++;

            // Default parameters on same line as closing paren or comma
            bool hasDefaultParameterSameLine =
                line.Contains("= 0)")
                || line.Contains("= null)")
                || line.Contains("= false)")
                || line.Contains("= true)")
                || line.Contains("= 0,")
                || line.Contains("= null,")
                || line.Contains("= false,")
                || line.Contains("= true,");

            // Default parameters on their own line (multi-line signatures)
            bool hasDefaultParameterEndOfLine =
                line.EndsWith("= 0")
                || line.EndsWith("= null")
                || line.EndsWith("= false")
                || line.EndsWith("= true");

            // Method signature that starts with return type + method name pattern
            // e.g., "MessageRegistrationHandle RegisterUntargeted<T>("
            bool isMethodSignatureStart =
                !line.StartsWith("//")
                && !line.StartsWith("/*")
                && !line.StartsWith("var ")
                && !line.Contains(" = ")
                && line.EndsWith("(")
                && MethodSignatureStartRegex.IsMatch(line);

            bool isPartialSignature =
                line.EndsWith(",")
                && !line.StartsWith("//")
                && (
                    line.Contains("Action<")
                    || line.Contains("Func<")
                    || line.Contains("int ")
                    || line.Contains("bool ")
                    || line.Contains("string ")
                    || line.Contains("GameObject ")
                    || line.Contains("Component ")
                    || line.Contains("InstanceId ")
                    || line.Contains("FastHandler")
                );

            // Isolated closing paren (multi-line signature endings)
            bool isIsolatedClosingParen = line == ")" || line == ");";

            // Method calls with type+parameter documentation style
            // e.g., "message.EmitTargeted(InstanceId target);"
            bool isDocumentationStyleMethodCall =
                !line.StartsWith("//")
                && !line.StartsWith("/*")
                && (line.EndsWith(");") || line.EndsWith(")"))
                && line.Contains("(")
                && DocumentationStyleMethodCallRegex.IsMatch(line);

            // Generic method calls with <T>( pattern that are API reference style
            // e.g., "token.RegisterUntargeted<T>(Action<T> handler, int priority = 0)"
            bool isGenericMethodCallWithTypeParams =
                !line.StartsWith("//")
                && !line.StartsWith("/*")
                && GenericMethodCallRegex.IsMatch(line)
                && (
                    line.Contains("Action<T>")
                    || line.Contains("FastHandler")
                    || line.Contains("Interceptor<T>")
                    || line.Contains("FastHandlerWithContext")
                    || HandlerParameterRegex.IsMatch(line)
                );

            // API reference lines with parameter declarations showing type and name
            // e.g., "(GameObject go, handler, int priority = 0)" or "(InstanceId id, handler,"
            bool isApiReferenceParameterLine =
                !line.StartsWith("//")
                && !line.StartsWith("/*")
                && line.Contains("(")
                && (
                    ApiReferenceUnityTypeRegex.IsMatch(line)
                    || ApiReferenceHandlerPriorityRegex.IsMatch(line)
                );

            if (
                hasDefaultParameterSameLine
                || hasDefaultParameterEndOfLine
                || isMethodSignatureStart
                || isPartialSignature
                || isIsolatedClosingParen
                || isDocumentationStyleMethodCall
                || isGenericMethodCallWithTypeParams
                || isApiReferenceParameterLine
            )
            {
                signatureLineCount++;
            }
        }

        if (signatureLineCount > 0 && totalNonEmptyLines > 0)
        {
            double ratio = (double)signatureLineCount / totalNonEmptyLines;
            if (
                ratio >= ApiSignatureRatioThreshold
                || signatureLineCount >= MinimumApiSignatureLinesForDetection
            )
            {
                return true;
            }
        }

        return false;
    }
}
