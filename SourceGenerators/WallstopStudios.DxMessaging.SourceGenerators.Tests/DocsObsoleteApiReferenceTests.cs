using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NUnit.Framework;

namespace WallstopStudios.DxMessaging.SourceGenerators.Tests;

/// <summary>
/// Drift-guard ensuring the published documentation under <c>docs/</c> never teaches an
/// <c>[Obsolete]</c> API. The set of obsolete members is DERIVED from the runtime/editor
/// source (not hard-coded), so the guard automatically covers any member that is marked
/// obsolete in the future -- there is nothing to keep in sync by hand.
/// </summary>
/// <remarks>
/// <para>
/// Why this is needed: <see cref="DocsSnippetCompilationTests"/> compiles doc snippets in a
/// stub-only context where references to real members surface as tolerated "missing member"
/// diagnostics (CS0117 / CS1061 / CS0103). That makes it blind to a snippet that names an
/// obsolete-but-still-present API -- exactly the staleness this guard catches. The match is on
/// the qualified <c>Type.Member</c> form (e.g. <c>IMessageBus.GlobalDiagnosticsMode</c>) so a
/// generic member name like <c>Unknown</c> or <c>None</c> cannot produce false positives. An
/// unqualified bare reference (e.g. the word <c>GlobalDiagnosticsMode</c> in prose without its
/// type) is a deliberate, accepted false-negative -- the generic enum member names make
/// bare-name matching infeasible, and doc snippets/prose use the qualified form in practice.
/// </para>
/// <para>
/// Scope is intentionally <c>docs/</c> only (the customer-facing published site). The repo-root
/// <c>CHANGELOG.md</c> -- which legitimately NAMES a deprecated API to document the deprecation
/// itself -- sits outside <c>docs/</c> and is therefore not scanned. The in-<c>docs/</c> migration
/// guide, by contrast, IS scanned, so it must steer readers to the REPLACEMENT API in its prose
/// and snippets rather than naming the obsolete member.
/// </para>
/// </remarks>
[TestFixture]
public sealed class DocsObsoleteApiReferenceTests
{
    private static readonly string[] SourceScanRoots = { "Runtime", "Editor" };

    // Resolve + parse once per test-run process: both tests share the same Roslyn scan of
    // Runtime/ and Editor/ (the same once-per-domain reflection-walk caching used elsewhere in
    // this suite, e.g. PublicSurfaceContractTests.AllReflectableTypes).
    private static readonly Lazy<string> RepoRoot = new(ResolveRepoRoot);

    private static readonly Lazy<IReadOnlyList<ObsoleteMember>> ObsoleteMembers = new(() =>
        CollectObsoleteMembers(RepoRoot.Value)
    );

    [Test]
    public void PublishedDocsDoNotReferenceObsoleteApis()
    {
        string repoRoot = RepoRoot.Value;
        IReadOnlyList<ObsoleteMember> obsoleteMembers = ObsoleteMembers.Value;

        Assert.That(
            obsoleteMembers,
            Is.Not.Empty,
            "No [Obsolete] members were discovered by scanning Runtime/ and Editor/. The Roslyn "
                + "scan or attribute detection is broken; a passing guard here would be vacuous."
        );

        string docsRoot = Path.Combine(repoRoot, "docs");
        Assert.That(
            Directory.Exists(docsRoot),
            Is.True,
            $"Unable to locate docs root at {docsRoot}."
        );

        var violations = new List<string>();
        // Ordered so the failure message is deterministic across machines/filesystems
        // (Directory.GetFiles order is OS-dependent); members are pre-sorted in CollectObsoleteMembers.
        foreach (
            string markdownPath in Directory
                .GetFiles(docsRoot, "*.md", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
        )
        {
            string[] lines = File.ReadAllLines(markdownPath);
            string relativePath = Path.GetRelativePath(repoRoot, markdownPath).Replace('\\', '/');
            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                foreach (ObsoleteMember member in obsoleteMembers)
                {
                    if (!member.ReferenceRegex.IsMatch(lines[lineIndex]))
                    {
                        continue;
                    }

                    string hint = string.IsNullOrEmpty(member.Message)
                        ? "."
                        : $" -- {member.Message}";
                    violations.Add(
                        $"{relativePath}:{lineIndex + 1} references obsolete API "
                            + $"'{member.QualifiedName}'{hint}"
                    );
                }
            }
        }

        Assert.That(
            violations,
            Is.Empty,
            "Published documentation under docs/ must not reference [Obsolete] APIs. Update the "
                + "snippet or prose to the current API (docs/guides/diagnostics.md is the canonical "
                + "diagnostics example), then re-run. Offending references:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, violations)
        );
    }

    [Test]
    public void ObsoleteMemberScanFindsKnownObsoleteApis()
    {
        // Anchors the Roslyn scan against the obsolete members that exist today, so a regression
        // that silently stops discovering obsolete members cannot make the docs guard pass
        // vacuously. Update this list only when the corresponding member is un-obsoleted/removed.
        HashSet<string> discovered = ObsoleteMembers
            .Value.Select(member => member.QualifiedName)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(discovered, Does.Contain("IMessageBus.GlobalDiagnosticsMode"));
            Assert.That(discovered, Does.Contain("MessageBusRebindMode.Unknown"));
            Assert.That(discovered, Does.Contain("ReflexiveSendMode.None"));
        });
    }

    private static IReadOnlyList<ObsoleteMember> CollectObsoleteMembers(string repoRoot)
    {
        // Keyed by qualified name so the same obsolete member found in multiple partial
        // declarations collapses to one entry.
        var byQualifiedName = new Dictionary<string, ObsoleteMember>(StringComparer.Ordinal);

        foreach (string root in SourceScanRoots)
        {
            string absoluteRoot = Path.Combine(repoRoot, root);
            if (!Directory.Exists(absoluteRoot))
            {
                continue;
            }

            foreach (
                string sourcePath in Directory.GetFiles(
                    absoluteRoot,
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

                SyntaxNode syntaxRoot = CSharpSyntaxTree
                    .ParseText(File.ReadAllText(sourcePath))
                    .GetRoot();
                foreach (
                    MemberDeclarationSyntax member in syntaxRoot
                        .DescendantNodes()
                        .OfType<MemberDeclarationSyntax>()
                )
                {
                    AttributeSyntax? obsoleteAttribute = FindObsoleteAttribute(member);
                    if (obsoleteAttribute is null)
                    {
                        continue;
                    }

                    string? enclosingType = member
                        .Parent?.FirstAncestorOrSelf<BaseTypeDeclarationSyntax>()
                        ?.Identifier.Text;
                    if (string.IsNullOrEmpty(enclosingType))
                    {
                        // Top-level / type-level obsoletes are not referenced as `Type.Member`.
                        continue;
                    }

                    string message = ExtractObsoleteMessage(obsoleteAttribute);
                    foreach (string memberName in MemberNames(member))
                    {
                        string qualifiedName = $"{enclosingType}.{memberName}";
                        byQualifiedName[qualifiedName] = new ObsoleteMember(qualifiedName, message);
                    }
                }
            }
        }

        return byQualifiedName
            .Values.OrderBy(member => member.QualifiedName, StringComparer.Ordinal)
            .ToList();
    }

    private static AttributeSyntax? FindObsoleteAttribute(MemberDeclarationSyntax member)
    {
        foreach (AttributeListSyntax attributeList in member.AttributeLists)
        {
            foreach (AttributeSyntax attribute in attributeList.Attributes)
            {
                string simpleName = attribute.Name switch
                {
                    QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
                    IdentifierNameSyntax identifier => identifier.Identifier.Text,
                    _ => attribute.Name.ToString(),
                };
                if (simpleName is "Obsolete" or "ObsoleteAttribute")
                {
                    return attribute;
                }
            }
        }

        return null;
    }

    private static List<string> MemberNames(MemberDeclarationSyntax member)
    {
        // Only members that documentation references via `Type.Member` are returned.
        // Constructors, operators, indexers, destructors, and nested type declarations are
        // intentionally excluded because they are not named in qualified `Type.Member` form.
        var names = new List<string>();
        switch (member)
        {
            case EnumMemberDeclarationSyntax enumMember:
                names.Add(enumMember.Identifier.Text);
                break;
            case PropertyDeclarationSyntax property:
                names.Add(property.Identifier.Text);
                break;
            case MethodDeclarationSyntax method:
                names.Add(method.Identifier.Text);
                break;
            case EventDeclarationSyntax eventDeclaration:
                names.Add(eventDeclaration.Identifier.Text);
                break;
            case FieldDeclarationSyntax field:
                names.AddRange(field.Declaration.Variables.Select(v => v.Identifier.Text));
                break;
            case EventFieldDeclarationSyntax eventField:
                names.AddRange(eventField.Declaration.Variables.Select(v => v.Identifier.Text));
                break;
        }

        return names;
    }

    private static string ExtractObsoleteMessage(AttributeSyntax attribute)
    {
        AttributeArgumentSyntax? firstArgument = attribute.ArgumentList?.Arguments.FirstOrDefault();
        if (
            firstArgument?.Expression is LiteralExpressionSyntax literal
            && literal.IsKind(SyntaxKind.StringLiteralExpression)
        )
        {
            return literal.Token.ValueText;
        }

        return string.Empty;
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
            {
                break;
            }

            current = parent;
        }

        throw new DirectoryNotFoundException(
            "Unable to locate the repository root from the current test directory."
        );
    }

    private sealed class ObsoleteMember
    {
        public ObsoleteMember(string qualifiedName, string message)
        {
            QualifiedName = qualifiedName;
            Message = message;
            // Word-boundary assertions keep `Type.Member` from matching where it is merely a
            // sub-token of a longer identifier: the trailing one rejects a longer member name
            // (`ReflexiveSendMode.None` must not hit `ReflexiveSendMode.NoneOfTheAbove`); the
            // leading one rejects a longer type name (`XIMessageBus.GlobalDiagnosticsMode`). A
            // preceding namespace qualifier (`Core.MessageBus.IMessageBus.GlobalDiagnosticsMode`)
            // still matches -- that is the same obsolete member -- because `.` is not a word char.
            ReferenceRegex = new Regex(
                @"(?<![A-Za-z0-9_])" + Regex.Escape(qualifiedName) + @"(?![A-Za-z0-9_])",
                RegexOptions.Compiled
            );
        }

        public string QualifiedName { get; }

        public string Message { get; }

        public Regex ReferenceRegex { get; }
    }
}
