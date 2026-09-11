using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using NUnit.Framework;

namespace WallstopStudios.DxMessaging.SourceGenerators.Tests;

[TestFixture]
internal sealed class RepositoryCSharpStyleContractTests
{
    private static readonly string[] SourceRoots =
    {
        "Editor",
        "Runtime",
        "Samples~",
        "SourceGenerators",
        "Tests",
    };

    [Test]
    public void TrackedCSharpUsesBlockCommentsAndAscendingComparisons()
    {
        string repositoryRoot = FindRepositoryRoot();
        List<string> violations = new();

        foreach (string path in EnumerateSourceFiles(repositoryRoot))
        {
            string relativePath = Path.GetRelativePath(repositoryRoot, path).Replace('\u005c', '/');
            string source = File.ReadAllText(path);
            FindDescendingComparisons(relativePath, source, violations);
            FindConsecutiveLineComments(relativePath, source, violations);
        }

        Assert.That(
            violations,
            Is.Empty,
            "C# style violations:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, violations)
        );
    }

    private static IEnumerable<string> EnumerateSourceFiles(string repositoryRoot)
    {
        return SourceRoots
            .SelectMany(root =>
                Directory.EnumerateFiles(
                    Path.Combine(repositoryRoot, root),
                    "*.cs",
                    SearchOption.AllDirectories
                )
            )
            .OrderBy(path => path, StringComparer.Ordinal);
    }

    private static void FindDescendingComparisons(
        string relativePath,
        string source,
        List<string> violations
    )
    {
        HashSet<TextSpan> reportedSpans = new();
        string[] conditionalSymbols = Regex
            .Matches(source, @"(?m)^\s*#(?:if|elif)\s+.*$")
            .SelectMany(directive => Regex.Matches(directive.Value, @"\b[A-Z][A-Z0-9_]+\b"))
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(symbol => symbol, StringComparer.Ordinal)
            .ToArray();
        int configurationCount = 1 << conditionalSymbols.Length;
        for (int mask = 0; mask < configurationCount; mask++)
        {
            string[] symbols = conditionalSymbols
                .Where((_, index) => (mask & (1 << index)) != 0)
                .ToArray();
            CSharpSyntaxTree tree = (CSharpSyntaxTree)
                CSharpSyntaxTree.ParseText(
                    source,
                    new CSharpParseOptions(preprocessorSymbols: symbols),
                    path: relativePath
                );
            foreach (
                BinaryExpressionSyntax expression in tree.GetRoot()
                    .DescendantNodes()
                    .OfType<BinaryExpressionSyntax>()
            )
            {
                if (
                    expression.IsKind(SyntaxKind.GreaterThanExpression)
                    || expression.IsKind(SyntaxKind.GreaterThanOrEqualExpression)
                )
                {
                    if (reportedSpans.Add(expression.OperatorToken.Span))
                    {
                        int line =
                            tree.GetLineSpan(expression.OperatorToken.Span).StartLinePosition.Line
                            + 1;
                        violations.Add($"{relativePath}:{line}: use an ascending-order comparison");
                    }
                }
            }
        }
    }

    private static void FindConsecutiveLineComments(
        string relativePath,
        string source,
        List<string> violations
    )
    {
        string[] lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int index = 0; index + 1 < lines.Length; index++)
        {
            if (IsFullLineComment(lines[index]) && IsFullLineComment(lines[index + 1]))
            {
                violations.Add(
                    $"{relativePath}:{index + 1}: use an indented block comment for multi-line prose"
                );
            }
        }
    }

    private static bool IsFullLineComment(string line)
    {
        string trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
            && !trimmed.StartsWith("///", StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(TestContext.CurrentContext.TestDirectory);
        while (current is not null)
        {
            if (
                File.Exists(Path.Combine(current.FullName, "package.json"))
                && Directory.Exists(Path.Combine(current.FullName, "Runtime"))
            )
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the DxMessaging repository root.");
    }
}
