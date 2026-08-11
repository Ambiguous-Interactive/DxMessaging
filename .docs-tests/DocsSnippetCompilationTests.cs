using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace WallstopStudios.DxMessaging.Docs.Tests;

[TestFixture]
internal sealed class DocsSnippetCompilationTests
{
    private static readonly HashSet<string> ContextOnlyDiagnosticIds = new(StringComparer.Ordinal)
    {
        "CS0103", // A surrounding example supplies the named local, field, or method.
        "CS0117", // The synthetic stubs do not contain the referenced package static member.
        "CS0115", // A surrounding example supplies the omitted base type.
        "CS0234", // The synthetic stubs do not contain the referenced package namespace member.
        "CS0246", // A surrounding example or the real package supplies the referenced type.
        "CS1061", // The synthetic stubs do not contain the referenced package extension/member.
    };

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

    private static readonly System.Text.RegularExpressions.Regex NullDelegateAssignmentRegex = new(
        @"(?im)^[^\r\n]*(?:Action\s*<|FastHandler|Interceptor|Callback|Processor|Observer|\bhandler\b)[^\r\n]*=\s*(?:default\s*!?|null)\s*;",
        System.Text.RegularExpressions.RegexOptions.Compiled
            | System.Text.RegularExpressions.RegexOptions.IgnoreCase
    );

    private static readonly System.Text.RegularExpressions.Regex NullRegistrationArgumentRegex =
        new(
            @"(?im)^[^\r\n]*\bRegister(?:Untargeted|GameObjectTargeted|ComponentTargeted|Targeted|TargetedWithoutTargeting|GameObjectBroadcast|ComponentBroadcast|Broadcast|BroadcastWithoutSource|GlobalAcceptAll)[A-Za-z0-9_]*(?:<[^>\r\n]+>)?\s*\([^\r\n]*(?:\bnull\b|\bdefault\s*!?\b)[^\r\n]*$",
            System.Text.RegularExpressions.RegexOptions.Compiled
                | System.Text.RegularExpressions.RegexOptions.IgnoreCase
        );

    private static readonly System.Text.RegularExpressions.Regex RegistrationOverrideRegex = new(
        @"protected\s+override\s+void\s+RegisterMessageHandlers\s*\(\s*\)\s*(?:\{(?<body>.*?)\}|(?<body>=>[^;\r\n]*;))",
        System.Text.RegularExpressions.RegexOptions.Compiled
            | System.Text.RegularExpressions.RegexOptions.Singleline
    );

    private static readonly System.Text.RegularExpressions.Regex RegistrationBaseCallRegex = new(
        @"(?m)(?:^|=>)\s*base\s*\.\s*RegisterMessageHandlers\s*\(\s*\)\s*;\s*(?://.*)?$",
        System.Text.RegularExpressions.RegexOptions.Compiled
    );

    [Test]
    public void QuickStartStep1Compiles()
    {
        string docsRoot = ResolveDocsRoot();
        string quickStartPath = Path.Combine(docsRoot, "getting-started", "quick-start.md");
        Assert.That(File.Exists(quickStartPath), Is.True, $"Unable to locate {quickStartPath}.");

        string snippet = ExtractFirstCodeBlock(quickStartPath, "csharp");
        Assert.That(!string.IsNullOrWhiteSpace(snippet), Is.True, "QuickStart snippet not found.");

        var diagnostics = DocsSnippetCompiler
            .CompileDocSnippet(snippet)
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

    [Test]
    public void SnippetCompilerPromotesOrdinaryWarningsToErrors()
    {
        Microsoft.CodeAnalysis.Diagnostic[] diagnostics = DocsSnippetCompiler
            .CompileSnippet(
                "public sealed class WarningSample { public void Run() { int unused = 42; } }"
            )
            .ToArray();

        Assert.That(
            diagnostics,
            Has.Some.Matches<Microsoft.CodeAnalysis.Diagnostic>(diagnostic =>
                diagnostic.Id == "CS0219"
                && diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error
            ),
            "The snippet compiler must fail documentation builds on ordinary compiler warnings."
        );
    }

    [Test]
    public void SnippetCompilerRejectsUnassignedFields()
    {
        Microsoft.CodeAnalysis.Diagnostic[] diagnostics = DocsSnippetCompiler
            .CompileSnippet(
                "public sealed class UnassignedSample { private int value; public int Read() => value; }"
            )
            .ToArray();

        Assert.That(
            diagnostics,
            Has.Some.Matches<Microsoft.CodeAnalysis.Diagnostic>(diagnostic =>
                diagnostic.Id == "CS0649"
                && diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error
            ),
            "Ordinary unassigned fields must remain fatal; externally assigned examples must initialize fields explicitly."
        );
        Assert.That(
            diagnostics,
            Has.None.Matches<Microsoft.CodeAnalysis.Diagnostic>(diagnostic =>
                diagnostic.Id == "CS1591"
            ),
            "CS1591 must remain ignored because standalone snippets and synthetic stubs are not complete public API assemblies."
        );

        diagnostics = DocsSnippetCompiler
            .CompileDocSnippet(
                "public sealed class SerializedSample { [SerializeField] private int value; public int Read() => value; }"
            )
            .ToArray();
        Assert.That(
            diagnostics,
            Has.None.Matches<Microsoft.CodeAnalysis.Diagnostic>(diagnostic =>
                diagnostic.Id == "CS0649"
            ),
            "Unity-serialized fields are the narrow external-assignment exception."
        );
    }

    [Test]
    public void SnippetCompilerStillRejectsMalformedXmlDocumentation()
    {
        Microsoft.CodeAnalysis.Diagnostic[] diagnostics = DocsSnippetCompiler
            .CompileSnippet(
                "/// <summary>Broken <see cref=\"System.String\"></summary>\npublic sealed class BrokenDocumentation { }"
            )
            .ToArray();

        Assert.That(
            diagnostics,
            Has.Some.Matches<Microsoft.CodeAnalysis.Diagnostic>(diagnostic =>
                diagnostic.Id == "CS1570"
                && diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error
            ),
            "Malformed XML documentation must remain fatal even though missing public API comments are outside the snippet contract."
        );
    }

    [TestCase(
        "public static int Convert() => \"wrong\";",
        "CS0029",
        TestName = "Snippet compiler rejects invalid conversions"
    )]
    [TestCase(
        "public static void Accept(int value) { } public static void Run() => Accept(\"wrong\");",
        "CS1503",
        TestName = "Snippet compiler rejects invalid argument types"
    )]
    [TestCase(
        "using DxMessaging.Core.Attributes; [DxAutoConstructor] public partial struct Generated { public int value; } public static class Usage { public static Generated Create() => new Generated(1, 2); }",
        "CS1729",
        TestName = "Snippet compiler rejects invalid generated constructor arity"
    )]
    [TestCase(
        "using DxMessaging.Core.Attributes; [DxAutoConstructor] public partial struct Generated { public int value; } public static class Usage { public static Generated Create() => new Generated(wrong: 1); }",
        "CS1739",
        TestName = "Snippet compiler rejects invalid generated constructor argument names"
    )]
    public void SnippetCompilerRejectsSemanticErrors(string source, string diagnosticId)
    {
        Microsoft.CodeAnalysis.Diagnostic[] diagnostics = DocsSnippetCompiler
            .CompileSnippet(source)
            .Where(diagnostic => !ContextOnlyDiagnosticIds.Contains(diagnostic.Id))
            .ToArray();

        Assert.That(
            diagnostics,
            Has.Some.Matches<Microsoft.CodeAnalysis.Diagnostic>(diagnostic =>
                diagnostic.Id == diagnosticId
                && diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error
            )
        );
    }

    [TestCaseSource(nameof(GetDocumentationSnippets))]
    public void DocumentationSnippetsCompile(string markdownPath, string snippet)
    {
        Assert.That(
            snippet,
            Is.Not.Empty,
            $"Snippet extracted from {markdownPath} should not be empty."
        );

        var diagnostics = DocsSnippetCompiler
            .CompileDocSnippet(snippet)
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .Where(d => !ContextOnlyDiagnosticIds.Contains(d.Id))
            .ToArray();

        if (diagnostics.Length > 0)
        {
            string message = string.Join(
                System.Environment.NewLine,
                diagnostics.Select(d => d.ToString())
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
            .Where(d => !ContextOnlyDiagnosticIds.Contains(d.Id))
            .ToArray();

        if (diagnostics.Length > 0)
        {
            string message = string.Join(
                System.Environment.NewLine,
                diagnostics.Select(d => d.ToString())
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
    public void DocumentationDoesNotRegisterNullOrDefaultDelegates()
    {
        string[] violations = GetTemporaryEmitDocumentationSources()
            .SelectMany(source =>
                NullDelegateAssignmentRegex
                    .Matches(source.Text)
                    .Cast<System.Text.RegularExpressions.Match>()
                    .Concat(
                        NullRegistrationArgumentRegex
                            .Matches(source.Text)
                            .Cast<System.Text.RegularExpressions.Match>()
                    )
                    .Select(match => $"{source.Path}: {match.Value.Trim()}")
            )
            .ToArray();

        Assert.That(
            violations,
            Is.Empty,
            "Handler, interceptor, callback, processor, and observer examples must use "
                + "real delegates instead of null/default placeholders:"
                + System.Environment.NewLine
                + string.Join(System.Environment.NewLine, violations)
        );
    }

    [Test]
    public void MessageAwareComponentExamplesCallBaseRegistrationHook()
    {
        string[] violations = GetBroadcastExampleSources()
            .SelectMany(source =>
                RegistrationOverrideRegex
                    .Matches(source.Text)
                    .Cast<System.Text.RegularExpressions.Match>()
                    .Where(match => !RegistrationBaseCallRegex.IsMatch(match.Groups["body"].Value))
                    .Select(match =>
                        $"{source.Path}:{source.Text[..match.Index].Count(character => character == '\n') + 1}"
                    )
            )
            .ToArray();

        Assert.That(
            violations,
            Is.Empty,
            "MessageAwareComponent examples must call base.RegisterMessageHandlers():"
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
        int testIndex = 0;
        foreach (string markdownPath in EnumeratePublishedMarkdownFiles())
        {
            foreach (string snippet in ExtractCodeBlocks(markdownPath, "csharp"))
            {
                if (ShouldSkipSnippet(snippet))
                {
                    continue;
                }

                yield return new TestCaseData(markdownPath, snippet).SetName(
                    $"{Path.GetFileName(markdownPath)} csharp #{testIndex++}"
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
        string statement = snippet.TrimEnd();
        if (!statement.EndsWith(';'))
        {
            statement += ";";
        }
        string wrapped = "void __InlineProbe() {\n" + statement + "\n}\n";

        var diagnostics = DocsSnippetCompiler
            .CompileDocSnippet(wrapped)
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .Where(d => !ContextOnlyDiagnosticIds.Contains(d.Id))
            .ToArray();

        if (diagnostics.Length > 0)
        {
            string message = string.Join(
                System.Environment.NewLine,
                diagnostics.Select(d => d.ToString())
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
        int testIndex = 0;
        foreach (string markdownPath in EnumeratePublishedMarkdownFiles())
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
            if (
                line.StartsWith("```", StringComparison.Ordinal)
                || line.StartsWith("~~~", StringComparison.Ordinal)
            )
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
            if (line.IndexOf('|', StringComparison.Ordinal) < 0)
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
        if (
            snippet.Contains('{', StringComparison.Ordinal)
            || snippet.Contains('}', StringComparison.Ordinal)
        )
            return false;
        // Filter out short fragments (bare type names, single identifiers).
        if (snippet.Length < 4)
            return false;
        // Must look like a statement: contain an opening paren AND end with ')' or ';'.
        if (snippet.IndexOf('(', StringComparison.Ordinal) < 0)
            return false;
        string trimmed = snippet.TrimEnd();
        if (!trimmed.EndsWith(')') && !trimmed.EndsWith(';'))
            return false;
        string statement = trimmed.EndsWith(';') ? snippet : snippet + ";";
        Microsoft.CodeAnalysis.SyntaxTree syntaxTree =
            Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
                "class InlineSnippet { void Run() { " + statement + " } }"
            );
        if (
            syntaxTree
                .GetDiagnostics()
                .Any(diagnostic =>
                    diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error
                )
            || syntaxTree
                .GetRoot()
                .DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.LocalFunctionStatementSyntax>()
                .Any()
        )
            return false;
        // Skip snippets that look like type-name placeholders.
        if (
            snippet.IndexOf(' ', StringComparison.Ordinal) < 0
            && snippet.IndexOf('.', StringComparison.Ordinal) < 0
        )
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
            .Where(d => !ContextOnlyDiagnosticIds.Contains(d.Id))
            .ToArray();

        if (diagnostics.Length > 0)
        {
            string message = string.Join(
                System.Environment.NewLine,
                diagnostics.Select(d => d.ToString())
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
                    normalized.Contains("/obj/", StringComparison.Ordinal)
                    || normalized.Contains("/bin/", StringComparison.Ordinal)
                    || normalized.Contains("/.artifacts/", StringComparison.Ordinal)
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
        foreach (
            string rawLine in content
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\r", "\n", StringComparison.Ordinal)
                .Split('\n')
        )
        {
            string trim = rawLine.TrimStart();
            if (trim.StartsWith("///", StringComparison.Ordinal))
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
        return s.Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal)
            .Replace("&amp;", "&", StringComparison.Ordinal)
            .Replace("&quot;", "\"", StringComparison.Ordinal)
            .Replace("&apos;", "'", StringComparison.Ordinal);
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
    public void ExtractCodeBlocksHandlesCommonMarkContainers()
    {
        const string markdown = """
1. List example:

   ```csharp
   int listValue = 1;
   ```

> Quoted example:
>
> ```csharp
> int quoteValue = 2;
> ```
""";

        string[] snippets = ExtractCodeBlocksFromLines(markdown.Split('\n'), "csharp").ToArray();

        Assert.That(snippets, Has.Length.EqualTo(2));
        Assert.That(snippets[0].Trim(), Is.EqualTo("int listValue = 1;"));
        Assert.That(snippets[1].Trim(), Is.EqualTo("int quoteValue = 2;"));
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
        return ExtractCodeBlocksFromLines(File.ReadLines(markdownPath), infoString);
    }

    private static IEnumerable<string> ExtractCodeBlocksFromLines(
        IEnumerable<string> lines,
        string infoString
    )
    {
        bool inBlock = false;
        int containerIndent = 0;
        int quoteDepth = 0;
        int fenceLength = 0;
        System.Text.StringBuilder builder = new();
        foreach (string rawLine in lines)
        {
            if (!inBlock)
            {
                string openingLine = StripMarkdownContainerPrefix(
                        rawLine,
                        out int openingQuoteDepth,
                        out int openingIndent
                    )
                    .TrimEnd();
                int openingFenceLength = CountLeadingBackticks(openingLine);
                if (
                    openingFenceLength >= 3
                    && openingLine.Length > openingFenceLength
                    && openingLine[openingFenceLength..]
                        .StartsWith(infoString, StringComparison.Ordinal)
                )
                {
                    inBlock = true;
                    containerIndent = openingIndent;
                    quoteDepth = openingQuoteDepth;
                    fenceLength = openingFenceLength;
                    builder.Clear();
                }
                continue;
            }

            string contentLine = StripMarkdownContainerPrefix(rawLine, quoteDepth, containerIndent);
            string line = contentLine.TrimEnd();
            int closingFenceLength = CountLeadingBackticks(line);
            if (
                closingFenceLength >= fenceLength
                && string.IsNullOrWhiteSpace(line[closingFenceLength..])
            )
            {
                inBlock = false;
                string snippet = builder.ToString();
                if (!string.IsNullOrWhiteSpace(snippet))
                {
                    yield return snippet;
                }
                continue;
            }

            builder.AppendLine(contentLine);
        }
    }

    private static string StripMarkdownContainerPrefix(
        string line,
        out int quoteDepth,
        out int containerIndent
    )
    {
        int index = 0;
        quoteDepth = 0;
        while (TryConsumeBlockQuoteMarker(line, ref index))
        {
            quoteDepth++;
        }

        containerIndent = ConsumeSpaces(line, ref index, 3);
        return line[index..];
    }

    private static string StripMarkdownContainerPrefix(
        string line,
        int quoteDepth,
        int containerIndent
    )
    {
        int index = 0;
        for (int i = 0; i < quoteDepth; i++)
        {
            if (!TryConsumeBlockQuoteMarker(line, ref index))
            {
                return string.Empty;
            }
        }

        ConsumeSpaces(line, ref index, containerIndent);
        return line[index..];
    }

    private static bool TryConsumeBlockQuoteMarker(string line, ref int index)
    {
        int markerStart = index;
        ConsumeSpaces(line, ref index, 3);
        if (index >= line.Length || line[index] != '>')
        {
            index = markerStart;
            return false;
        }

        index++;
        if (index < line.Length && line[index] == ' ')
        {
            index++;
        }
        return true;
    }

    private static int ConsumeSpaces(string line, ref int index, int maximum)
    {
        int consumed = 0;
        while (consumed < maximum && index < line.Length && line[index] == ' ')
        {
            consumed++;
            index++;
        }
        return consumed;
    }

    private static int CountLeadingBackticks(string line)
    {
        int count = 0;
        while (count < line.Length && line[count] == '`')
        {
            count++;
        }
        return count;
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
                    normalized.Contains("/obj/", StringComparison.Ordinal)
                    || normalized.Contains("/bin/", StringComparison.Ordinal)
                    || normalized.Contains("/.artifacts/", StringComparison.Ordinal)
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
                return !normalized.Contains("/obj/", StringComparison.Ordinal)
                    && !normalized.Contains("/bin/", StringComparison.Ordinal)
                    && !normalized.Contains("/.artifacts/", StringComparison.Ordinal);
            })
            .OrderBy(path => path, StringComparer.Ordinal);
    }

    private static IEnumerable<string> EnumeratePublishedMarkdownFiles()
    {
        string repoRoot = ResolveRepoRoot();
        string docsRoot = ResolveDocsRoot();
        string samplesRoot = Path.Combine(repoRoot, "Samples~");
        string readmePath = Path.Combine(repoRoot, "README.md");

        IEnumerable<string> paths = EnumerateTextFiles(docsRoot, "*.md")
            .Concat(EnumerateTextFiles(docsRoot, "*.markdown"));
        if (File.Exists(readmePath))
        {
            paths = paths.Append(readmePath);
        }
        if (Directory.Exists(samplesRoot))
        {
            paths = paths
                .Concat(EnumerateTextFiles(samplesRoot, "*.md"))
                .Concat(EnumerateTextFiles(samplesRoot, "*.markdown"));
        }

        return paths.Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal);
    }

    private static IEnumerable<(int Line, string Text)> FindTemporaryEmitViolations(
        string sourcePath,
        string text
    )
    {
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
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

    private static readonly string[] NegativeCompileMarkers =
    {
        "won't compile",
        "will not compile",
        "does not compile",
        "do not compile",
        "don't compile",
        "cannot compile",
        "do not emit from temporaries",
        "don't emit from temporaries",
    };

    private static bool HasNegativeCompileMarker(string line)
    {
        string lowered = line.ToUpperInvariant();
        foreach (string marker in NegativeCompileMarkers)
        {
            if (lowered.Contains(marker.ToUpperInvariant(), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsExplicitNegativeTemporaryEmitExample(string[] lines, int lineIndex)
    {
        // A deliberately-bad example labels itself on the SAME line as the temporary
        // emit (inline prose or comment). A marker on the preceding line only counts
        // when that line is a code comment, so ordinary explanatory prose that merely
        // mentions compilation cannot silently suppress a real `new ...().Emit*`
        // violation on the following line.
        if (HasNegativeCompileMarker(lines[lineIndex]))
        {
            return true;
        }

        if (lineIndex > 0)
        {
            string previous = lines[lineIndex - 1].TrimStart();
            if (
                previous.StartsWith("//", StringComparison.Ordinal)
                && HasNegativeCompileMarker(previous)
            )
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<(int Line, string Text)> FindNonSelfBroadcastSourceViolations(
        string sourcePath,
        string text
    )
    {
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Split('\n');
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
        false,
        TestName = "Single-line method signatures remain compiler-visible"
    )]
    [TestCase(
        "void Process(string name = null)",
        false,
        TestName = "Method signatures with null defaults remain compiler-visible"
    )]
    [TestCase(
        "bool IsEnabled(bool flag = false)",
        false,
        TestName = "Method signatures with false defaults remain compiler-visible"
    )]
    [TestCase(
        "void Toggle(bool active = true)",
        false,
        TestName = "Method signatures with true defaults remain compiler-visible"
    )]
    [TestCase(
        "MessageRegistrationHandle RegisterUntargeted<T>(\n    Action<T> handler,\n    int priority = 0)",
        false,
        TestName = "Multi-line signatures remain compiler-visible"
    )]
    [TestCase(
        "Do something...\nthen continue",
        false,
        TestName = "Prose ellipses do not skip snippets"
    )]
    [TestCase(
        "public void Method() { ... }",
        false,
        TestName = "Invalid ellipsis bodies remain compiler-visible"
    )]
    [TestCase(
        "MessageRegistrationHandle RegisterUntargeted<T>(\n    Action<T> handler,\n    int priority = 0\n)",
        false,
        TestName = "Unterminated multi-line signatures remain compiler-visible"
    )]
    [TestCase(
        "    // GameObject target\n    MessageRegistrationHandle RegisterGameObjectTargeted<T>(\n        GameObject target,\n        Action<T> handler,\n        int priority = 0\n    )",
        false,
        TestName = "Indented signatures remain compiler-visible"
    )]
    [TestCase(
        "// Emit to specific target (by InstanceId)\nmessage.EmitTargeted(InstanceId target);",
        false,
        TestName = "Invalid documentation-style calls remain compiler-visible"
    )]
    [TestCase(
        "Action RegisterUntargetedInterceptor<T>(\n    UntargetedInterceptor<T> interceptor,\n    int priority = 0\n)",
        false,
        TestName = "Interceptor signatures remain compiler-visible"
    )]
    [TestCase(
        "token.RegisterUntargeted<T>(Action<T> handler, int priority = 0)",
        false,
        TestName = "Generic Action placeholders remain compiler-visible"
    )]
    [TestCase(
        "token.RegisterUntargeted<T>(FastHandler<T> handler, int priority = 0)",
        false,
        TestName = "FastHandler placeholders remain compiler-visible"
    )]
    [TestCase(
        "token.RegisterGameObjectTargeted<T>(GameObject go, handler, int priority = 0)",
        false,
        TestName = "Handler parameter placeholders remain compiler-visible"
    )]
    [TestCase(
        "token.RegisterTargetedWithoutTargeting<T>(FastHandlerWithContext<T> handler, int priority = 0)",
        false,
        TestName = "Context handler placeholders remain compiler-visible"
    )]
    [TestCase(
        "bus.RegisterUntargetedInterceptor<T>(UntargetedInterceptor<T> interceptor, int priority = 0)",
        false,
        TestName = "Bus interceptor placeholders remain compiler-visible"
    )]
    [TestCase(
        "token.RegisterTargeted<T>(InstanceId id, handler, int priority = 0)",
        false,
        TestName = "Targeted handler placeholders remain compiler-visible"
    )]
    [TestCase(
        "token.RegisterBroadcast<T>(InstanceId id, handler, int priority = 0)",
        false,
        TestName = "Broadcast handler placeholders remain compiler-visible"
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
    [TestCase(
        "// This example will not compile.\nnew void OnEnable() { }",
        true,
        TestName = "Explicit negative compilation examples should be skipped"
    )]
    [TestCase(
        "// WRONG but valid C# should still be compiled.\nint value = 1;",
        false,
        TestName = "Pedagogical wrong labels do not skip compilable examples"
    )]
    [TestCase(
        "// Manual implementation.\nint value = 1;",
        false,
        TestName = "Manual labels do not skip compilable examples"
    )]
    public void ShouldSkipSnippetUsesOnlyExplicitNegativeMarkers(string snippet, bool expectedSkip)
    {
        bool actualSkip = ShouldSkipSnippet(snippet);
        Assert.That(
            actualSkip,
            Is.EqualTo(expectedSkip),
            $"Expected ShouldSkipSnippet to return {expectedSkip} for snippet: '{snippet.Replace("\n", "\\n", StringComparison.Ordinal)}'"
        );
    }

    private static bool ShouldSkipSnippet(string snippet)
    {
        if (string.IsNullOrWhiteSpace(snippet))
        {
            return true;
        }

        if (
            snippet
                .Split('\n')
                .Any(line =>
                    line.TrimStart().StartsWith("//", StringComparison.Ordinal)
                    && HasNegativeCompileMarker(line)
                )
        )
        {
            return true;
        }

        return false;
    }
}
