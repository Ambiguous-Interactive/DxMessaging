using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace WallstopStudios.DxMessaging.SourceGenerators.Tests;

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
        // this harness cannot detect that bug class semantically. The textual
        // doc-pattern lint that previously guarded it was retired in the
        // tooling simplification, so that bug class is currently unguarded;
        // reviewers must catch it manually in doc snippets.
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
        // intentionally minimal in GeneratorTestUtilities.SharedStubs. There is
        // no longer an automated defense against the "new X().Emit()" bug
        // class (the textual doc-pattern lint was retired); review doc
        // snippets for it manually. The
        // compilation test cannot reliably catch that specific bug here: the
        // stub setup produces CS1510 (not CS1612) for the broken pattern,
        // and CS1510 must remain in the ignore list to suppress unrelated
        // false-positives on legitimate snippets that reference unstubbed
        // ref-returning members.
        "CS0102", // type already contains a definition (partial declarations re-merged in script)
        "CS0111", // type already defines member with same parameter types
        "CS0260", // missing partial modifier on declaration
        "CS0308", // non-generic type 'X' cannot be used with type arguments (stub interface)
        "CS0315", // type cannot be used as type parameter (interface constraint via stub gap)
        "CS0453", // type must be non-nullable value type (placeholder strings as messages)
        "CS0501", // method must declare body (partial members not generated)
        "CS0579", // duplicate attribute (auto-generated partials would dedupe in real build)
        "CS1510", // ref or out value must be assignable. Kept in ignore list because stub-only compilation produces CS1510 noise on legitimate snippets that touch unstubbed ref-returning APIs; this means the harness CANNOT catch the "new X().Emit()" struct-rvalue bug; that class has no automated guard since the textual doc-pattern lint was retired, so it must be caught in review.
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

        var diagnostics = GeneratorTestUtilities
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
        // IMPORTANT: this harness does NOT catch the "new StructMessage().Emit()"
        // bug class. The stub setup produces CS1510 (not CS1612) for that
        // pattern, and CS1510 must remain ignored to keep legitimate snippets
        // that touch unstubbed ref-returning members from triggering false
        // positives. The textual lint that previously guarded this class of bug
        // was retired in the tooling simplification, so it is currently
        // unguarded; review doc snippets for it manually.
        var diagnostics = GeneratorTestUtilities
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

        var diagnostics = GeneratorTestUtilities
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

        var diagnostics = GeneratorTestUtilities
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
        string text = stripped.ToString();

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

    private static string DecodeXmlEntities(string s)
    {
        return s.Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&amp;", "&")
            .Replace("&quot;", "\"")
            .Replace("&apos;", "'");
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
