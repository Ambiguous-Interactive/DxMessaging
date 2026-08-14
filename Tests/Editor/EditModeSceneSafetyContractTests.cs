#if UNITY_EDITOR && UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Editor
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
    using NUnit.Framework;
    using UnityEngine;
    using UnityEngine.SceneManagement;

    [TestFixture]
    [Category("Contract")]
    public sealed class EditModeSceneSafetyContractTests
    {
        private const string PackageName = "com.wallstop-studios.dxmessaging";

        private static readonly IReadOnlyDictionary<
            string,
            SceneResidentExemption
        > SceneResidentExemptions = new Dictionary<string, SceneResidentExemption>(
            StringComparer.Ordinal
        );

        private static readonly string ScanFiller = BuildScanFiller(200);

        private static IEnumerable<TestCaseData> ConstructionCases()
        {
            yield return Case("classic", "GameObject host = new GameObject(\"classic\");", 1);
            yield return Case(
                "object initializer",
                "GameObject host = new GameObject { name = \"initializer\" };",
                1
            );
            yield return Case(
                "qualified",
                "UnityEngine.GameObject host = new UnityEngine.GameObject(\"qualified\");",
                1
            );
            yield return Case("target typed", "GameObject host = new(\"target typed\");", 1);
            yield return Case("parenthesized", "GameObject host = (new(\"parenthesized\"));", 1);
            yield return Case("deferred", "GameObject host;\nhost = new(\"deferred\");", 1);
            yield return Case(
                "parenthesized deferred",
                "GameObject host;\nhost = (new(\"parenthesized deferred\"));",
                1
            );
            yield return Case(
                "shadowed local in a sibling method",
                "void First() { GameObject receiver = new(\"unsafe\"); }\n"
                    + "void Second() { FlowGraphComponentNode receiver = new(); }",
                1
            );
            yield return Case("comment", "// GameObject host = new(\"comment\");", 0);
            yield return Case(
                "string literal",
                "string sample = \"GameObject host = new(\\\"text\\\");\";",
                0
            );
            yield return Case(
                "hide flag factory",
                "GameObject host = EditorUtility.CreateGameObjectWithHideFlags(\"safe\", HideFlags.HideAndDontSave);",
                0
            );
            yield return Case(
                "property initializer",
                "GameObject Host { get; } = new(\"property\");",
                1
            );
            yield return Case("expression bodied", "GameObject CreateHost() => new(\"arrow\");", 1);
            yield return Case("return", "GameObject CreateHost() { return new(\"return\"); }", 1);
            yield return Case(
                "conditional return",
                "GameObject CreateHost() { return condition ? new(\"return\") : null; }",
                1
            );
            yield return Case("getter", "GameObject Host { get { return new(\"getter\"); } }", 1);
            yield return Case(
                "lambda factory",
                "Func<GameObject> factory = () => new(\"lambda\");",
                1
            );
            yield return Case(
                "ternary branch",
                "GameObject host = condition ? new(\"branch\") : null;",
                1
            );
            yield return Case("array initializer", "GameObject[] hosts = { new(\"array\") };", 1);
            yield return Case(
                "explicit array initializer",
                "GameObject[] hosts = new GameObject[] { new(\"explicit array\") };",
                1
            );
            yield return Case("member target", "GameObject host; this.host = new(\"member\");", 1);
            yield return Case(
                "null coalescing assignment",
                "GameObject host; host ??= new(\"coalesce\");",
                1
            );
            yield return Case(
                "using alias",
                "using GO = UnityEngine.GameObject; GO host = new GO(\"alias\");",
                1
            );
            yield return Case(
                "create primitive",
                "GameObject.CreatePrimitive(PrimitiveType.Cube);",
                1
            );
            yield return Case("instantiate", "Object.Instantiate(prefab);", 1);
            yield return Case("generic instantiate", "Object.Instantiate<GameObject>(prefab);", 1);
            yield return Case("instantiate prefab", "PrefabUtility.InstantiatePrefab(prefab);", 1);
            yield return Case(
                "hide flag factory without hide and dont save",
                "EditorUtility.CreateGameObjectWithHideFlags(\"unsafe\", HideFlags.None);",
                1
            );
            yield return Case(
                "hide flag factory with a conditional flag",
                "EditorUtility.CreateGameObjectWithHideFlags(\"unsafe\", condition ? HideFlags.HideAndDontSave : HideFlags.None);",
                1
            );
            yield return Case(
                "interpolation hole",
                "string value = $\"{new GameObject(\"unsafe\")}\";",
                1
            );
            yield return Case(
                "shadowed local in a nested scope",
                "GameObject receiver; void Probe() { FlowGraphComponentNode receiver; receiver = new(); }",
                0
            );
            yield return Case(
                "nested call argument",
                "GameObject host = condition ? Make() : Wrap(new());",
                0
            );
        }

        private static TestCaseData Case(string name, string source, int expectedCount)
        {
            return new TestCaseData(source, expectedCount).SetName($"{{m}}({name})");
        }

        [TestCaseSource(nameof(ConstructionCases))]
        public void ScannerFindsOnlyActiveSceneGameObjectConstruction(
            string source,
            int expectedCount
        )
        {
            IReadOnlyList<EditModeGameObjectConstruction> actual =
                EditModeGameObjectConstructionScanner.Find("Tests/Editor/Probe.cs", source);

            Assert.That(
                actual.Count,
                Is.EqualTo(expectedCount),
                $"Source '{source}' should report {expectedCount} unsafe construction(s), but "
                    + $"reported {actual.Count}: {string.Join(" | ", actual.Select(item => item.Display))}."
            );
        }

        /// <summary>
        /// The scanner answers line numbers, scope ends, and declaration lookups against a
        /// per-file index, and every one of those is position-sensitive. The isolated cases are
        /// all single-scope one-liners, so they cannot catch an index that resolves the wrong
        /// entry once a file is large. Embedding each case after unrelated but structurally busy
        /// code -- nested scopes, hundreds of declarations, and a benign target-typed
        /// construction per method -- pins those lookups to the right entry.
        /// <see cref="EditModeFixturesDoNotConstructGameObjectsInDeveloperScenes"/> remains the
        /// scale guard; it scans the real corpus and is what caught the quadratic scan.
        /// </summary>
        [TestCaseSource(nameof(ConstructionCases))]
        public void ScannerResultsDoNotDependOnSurroundingFileSize(string source, int expectedCount)
        {
            int fillerLines = ScanFiller.Count(character => character == '\n');

            IReadOnlyList<EditModeGameObjectConstruction> isolated =
                EditModeGameObjectConstructionScanner.Find("Tests/Editor/Probe.cs", source);
            IReadOnlyList<EditModeGameObjectConstruction> embedded =
                EditModeGameObjectConstructionScanner.Find(
                    "Tests/Editor/Probe.cs",
                    ScanFiller + source
                );

            Assert.That(
                embedded.Count,
                Is.EqualTo(expectedCount),
                $"Source '{source}' preceded by {ScanFiller.Length} characters of unrelated code "
                    + $"should still report {expectedCount} unsafe construction(s), but reported "
                    + $"{embedded.Count}: {string.Join(" | ", embedded.Select(item => item.Display))}."
            );
            Assert.That(
                embedded.Select(item => item.SourceLine).ToArray(),
                Is.EqualTo(isolated.Select(item => item.SourceLine).ToArray()),
                $"Source '{source}' should report the same lines regardless of file size."
            );
            Assert.That(
                embedded.Select(item => item.LineNumber).ToArray(),
                Is.EqualTo(isolated.Select(item => item.LineNumber + fillerLines).ToArray()),
                $"Source '{source}' should report line numbers offset by exactly the "
                    + $"{fillerLines} filler lines that precede it."
            );
        }

        [Test]
        public void EditModeFixturesDoNotConstructGameObjectsInDeveloperScenes()
        {
            string packageRoot = GetPackageRoot();
            string editorTestsRoot = Path.Combine(packageRoot, "Tests", "Editor");
            List<string> offenders = new();
            Dictionary<string, int> exemptionUses = SceneResidentExemptions.ToDictionary(
                pair => pair.Key,
                _ => 0,
                StringComparer.Ordinal
            );
            int scannedFiles = 0;

            foreach (
                string file in Directory
                    .EnumerateFiles(editorTestsRoot, "*.cs", SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.Ordinal)
            )
            {
                scannedFiles++;
                string relativePath = NormalizeRelativePath(packageRoot, file);
                string source = File.ReadAllText(file);
                foreach (
                    EditModeGameObjectConstruction construction in EditModeGameObjectConstructionScanner.Find(
                        relativePath,
                        source
                    )
                )
                {
                    if (
                        !string.IsNullOrEmpty(construction.ExemptionId)
                        && SceneResidentExemptions.TryGetValue(
                            construction.ExemptionId,
                            out SceneResidentExemption exemption
                        )
                        && exemption.Matches(construction)
                    )
                    {
                        exemptionUses[construction.ExemptionId]++;
                        continue;
                    }

                    offenders.Add(construction.Display);
                }
            }

            Assert.That(
                scannedFiles,
                Is.GreaterThan(0),
                $"Expected to scan EditMode fixtures under '{editorTestsRoot}', but found none."
            );
            Assert.That(
                offenders,
                Is.Empty,
                "EditMode fixtures must not construct GameObjects in the developer's active scene. "
                    + "Use EditorUtility.CreateGameObjectWithHideFlags(name, "
                    + "HideFlags.HideAndDontSave, ...) for scene-less objects. A fixture that "
                    + "genuinely tests scene residency must own and close an isolated scene and "
                    + "carry an exact DXM-SCENE-RESIDENCY exemption. Offenders:\n"
                    + string.Join("\n", offenders)
            );

            string[] invalidExemptions = exemptionUses
                .Where(pair => pair.Value != 1)
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair =>
                    $"{pair.Key}: used {pair.Value} times; reason: {SceneResidentExemptions[pair.Key].Reason}"
                )
                .ToArray();
            Assert.That(
                invalidExemptions,
                Is.Empty,
                "Every scene-residency exemption must name exactly one live construction. Remove stale "
                    + "entries and split duplicate uses instead of leaving fixture files unchecked:\n"
                    + string.Join("\n", invalidExemptions)
            );
        }

        [Test]
        public void OwnedSceneRefusesToClaimAnAlreadyLoadedPath()
        {
            string scenePath =
                "Packages/com.wallstop-studios.dxmessaging/Tests/Editor/Fixtures/EditModeSceneSafety.unity";
            using OwnedEditModeScene first = OwnedEditModeScene.OpenAuthored(scenePath);
            int sceneCount = SceneManager.sceneCount;

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            {
                using OwnedEditModeScene _ = OwnedEditModeScene.OpenAuthored(scenePath);
            });

            Assert.That(exception.Message, Does.Contain("already loaded"));
            Assert.That(SceneManager.sceneCount, Is.EqualTo(sceneCount));
            Assert.That(first.Scene.isLoaded, Is.True);
        }

        [Test]
        public void OwnedSceneRestoresEditorStateWhenTheBodyThrows()
        {
            string scenePath =
                "Packages/com.wallstop-studios.dxmessaging/Tests/Editor/Fixtures/EditModeSceneSafety.unity";
            Scene originalActive = SceneManager.GetActiveScene();
            int originalSceneCount = SceneManager.sceneCount;
            bool originalDirty = originalActive.isDirty;

            Assert.Throws<InvalidOperationException>(() =>
            {
                using OwnedEditModeScene owned = OwnedEditModeScene.OpenAuthored(scenePath);
                owned.Activate();
                _ = owned.CreateGameObject("Throwing fixture object");
                throw new InvalidOperationException("Expected test-body failure.");
            });

            Assert.That(SceneManager.sceneCount, Is.EqualTo(originalSceneCount));
            Assert.That(SceneManager.GetActiveScene().handle, Is.EqualTo(originalActive.handle));
            Assert.That(originalActive.isDirty, Is.EqualTo(originalDirty));
        }

        [Test]
        public void OwnedSceneCreationNeverDirtiesTheDeveloperScene()
        {
            string scenePath =
                "Packages/com.wallstop-studios.dxmessaging/Tests/Editor/Fixtures/EditModeSceneSafety.unity";
            Scene originalActive = SceneManager.GetActiveScene();
            bool originalDirty = originalActive.isDirty;

            using (OwnedEditModeScene authored = OwnedEditModeScene.OpenAuthored(scenePath))
            {
                GameObject authoredObject = authored.CreateGameObject("Authored object");
                Assert.That(authoredObject.scene.handle, Is.EqualTo(authored.Scene.handle));
                Assert.That(originalActive.isDirty, Is.EqualTo(originalDirty));
            }

            using (OwnedEditModeScene preview = OwnedEditModeScene.CreatePreview())
            {
                GameObject previewObject = preview.CreateGameObject("Preview object");
                Assert.That(previewObject.scene.handle, Is.EqualTo(preview.Scene.handle));
                Assert.That(originalActive.isDirty, Is.EqualTo(originalDirty));
            }
        }

        /// <summary>
        /// Unrelated but structurally busy code: nested scopes, local declarations, and a benign
        /// target-typed construction per method, none of which the scanner may report.
        /// </summary>
        private static string BuildScanFiller(int methods)
        {
            StringBuilder builder = new();
            for (int index = 0; index < methods; index++)
            {
                builder
                    .Append("private int DxFiller")
                    .Append(index)
                    .Append("(int dxSeed")
                    .Append(index)
                    .Append(")\n{\n    List<int> dxValues")
                    .Append(index)
                    .Append(" = new();\n    dxValues")
                    .Append(index)
                    .Append(".Add(dxSeed")
                    .Append(index)
                    .Append(");\n    return dxValues")
                    .Append(index)
                    .Append("[0];\n}\n");
            }
            return builder.ToString();
        }

        private static string GetPackageRoot()
        {
            return Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", "Packages", PackageName)
            );
        }

        private static string NormalizeRelativePath(string packageRoot, string file)
        {
            string relative = file.Substring(packageRoot.Length).TrimStart('/', '\\');
            return relative.Replace('\\', '/');
        }
    }

    internal readonly struct SceneResidentExemption
    {
        internal SceneResidentExemption(
            string expectedRelativePath,
            string expectedSourceFragment,
            string reason
        )
        {
            ExpectedRelativePath = expectedRelativePath;
            ExpectedSourceFragment = expectedSourceFragment;
            Reason = reason;
        }

        internal string ExpectedRelativePath { get; }
        internal string ExpectedSourceFragment { get; }
        internal string Reason { get; }

        internal bool Matches(EditModeGameObjectConstruction construction)
        {
            return string.Equals(
                    construction.RelativePath,
                    ExpectedRelativePath,
                    StringComparison.Ordinal
                )
                && construction.SourceLine.IndexOf(ExpectedSourceFragment, StringComparison.Ordinal)
                    >= 0;
        }
    }

    internal static class EditModeGameObjectConstructionScanner
    {
        private const string ExemptionPrefix = "// DXM-SCENE-RESIDENCY:";

        private static readonly Regex AliasDeclaration = new(
            @"\busing\s+(?<alias>[A-Za-z_]\w*)\s*=\s*(?:global::)?UnityEngine\.GameObject\s*;",
            RegexOptions.Compiled
        );

        private static readonly Regex ExplicitConstruction = new(
            @"\bnew\s+(?<type>(?:(?:global::)?UnityEngine\.)?GameObject|[A-Za-z_]\w*)\s*(?:\(|\{)",
            RegexOptions.Compiled
        );

        private static readonly Regex ImplicitConstruction = new(
            @"\bnew\s*\(",
            RegexOptions.Compiled
        );

        private static readonly Regex AssignedTarget = new(
            @"(?:(?:this|base)\.)?(?<name>[A-Za-z_]\w*)\s*(?:=|\?\?=)\s*\(*\s*$",
            RegexOptions.Compiled
        );

        private static readonly Regex DangerousFactory = new(
            @"\b(?:GameObject\.CreatePrimitive|(?:Object|UnityEngine\.Object)\.Instantiate(?:\s*<[^>]+>)?|PrefabUtility\.InstantiatePrefab)\s*\(",
            RegexOptions.Compiled
        );

        private static readonly Regex HideFlagFactory = new(
            @"\bEditorUtility\.CreateGameObjectWithHideFlags\s*\(",
            RegexOptions.Compiled
        );

        private static readonly Regex ArrayInitializerTarget = new(
            @"\b(?:(?:global::)?UnityEngine\.)?GameObject\s*\[\]\s+[A-Za-z_]\w*\s*=\s*{[^}]*$",
            RegexOptions.Compiled
        );

        private static readonly Regex ReturnKeyword = new(@"\breturn\b", RegexOptions.Compiled);

        private static readonly Regex GameObjectFactoryLambda = new(
            @"\bFunc\s*<\s*(?:(?:global::)?UnityEngine\.)?GameObject\s*>[^;]*=>[^;]*$",
            RegexOptions.Compiled
        );

        private static readonly Regex GetterReturn = new(
            @"\bget\s*{[^{}]*\breturn\b",
            RegexOptions.Compiled
        );

        private static readonly Regex EmptyTargetPrefix = new(
            @"^\s*\(*\s*$",
            RegexOptions.Compiled
        );

        private static readonly Regex ExemptionIdFormat = new(
            @"^[a-z0-9]+(?:-[a-z0-9]+)*$",
            RegexOptions.Compiled
        );

        /// <summary>
        /// Name-agnostic form of the per-name declaration probe. Matching it once per file and
        /// grouping by name replaces one whole-prefix scan per construction, which is what made
        /// the scan superlinear in file length.
        /// </summary>
        private static readonly Regex Declaration = new(
            @"(?<type>[A-Za-z_][\w.:<>?\[\]]*)\s+(?<name>[A-Za-z_]\w*)\s*(?:[;=,\){])",
            RegexOptions.Compiled
        );

        /// <summary>
        /// Patterns interpolated from a GameObject type name. Building them inline recompiles on
        /// every call, because interpolated patterns evict each other from the regex cache.
        /// </summary>
        private static readonly ConcurrentDictionary<string, Regex> InterpolatedPatterns = new();

        private static Regex ForPattern(string pattern)
        {
            return InterpolatedPatterns.GetOrAdd(
                pattern,
                key => new Regex(key, RegexOptions.Compiled)
            );
        }

        internal static IReadOnlyList<EditModeGameObjectConstruction> Find(
            string relativePath,
            string source
        )
        {
            if (relativePath == null)
            {
                throw new ArgumentNullException(nameof(relativePath));
            }
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            LexedSource lexed = Lex(source);
            string sanitized = lexed.Sanitized;
            HashSet<string> gameObjectTypes = new(StringComparer.Ordinal)
            {
                "GameObject",
                "UnityEngine.GameObject",
                "global::UnityEngine.GameObject",
            };
            foreach (Match alias in AliasDeclaration.Matches(sanitized))
            {
                gameObjectTypes.Add(alias.Groups["alias"].Value);
            }

            ScanIndex scan = new(sanitized, gameObjectTypes);
            List<int> constructionIndexes = new();
            foreach (Match construction in ExplicitConstruction.Matches(sanitized))
            {
                if (gameObjectTypes.Contains(construction.Groups["type"].Value))
                {
                    constructionIndexes.Add(construction.Index);
                }
            }

            foreach (Match construction in ImplicitConstruction.Matches(sanitized))
            {
                if (IsGameObjectTarget(scan, construction.Index))
                {
                    constructionIndexes.Add(construction.Index);
                }
            }

            constructionIndexes.AddRange(
                DangerousFactory.Matches(sanitized).Cast<Match>().Select(match => match.Index)
            );
            foreach (Match factory in HideFlagFactory.Matches(sanitized))
            {
                int close = FindMatchingParenthesis(sanitized, factory.Index + factory.Length - 1);
                string invocation = sanitized.Substring(
                    factory.Index,
                    Math.Max(0, close - factory.Index + 1)
                );
                int open = invocation.IndexOf('(');
                IReadOnlyList<string> arguments =
                    open >= 0 && invocation.Length > open + 1
                        ? SplitTopLevelArguments(
                            invocation.Substring(open + 1, invocation.Length - open - 2)
                        )
                        : Array.Empty<string>();
                if (
                    close < 0
                    || arguments.Count < 2
                    || !string.Equals(
                        arguments[1].Trim(),
                        "HideFlags.HideAndDontSave",
                        StringComparison.Ordinal
                    )
                )
                {
                    constructionIndexes.Add(factory.Index);
                }
            }

            string[] sourceLines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            List<EditModeGameObjectConstruction> results = new();
            foreach (int index in constructionIndexes.Distinct().OrderBy(value => value))
            {
                int lineNumber = scan.LineNumberAt(index);
                string sourceLine = sourceLines[lineNumber - 1].Trim();
                string exemptionId = ParseExemptionId(lexed.LineComments, lineNumber);
                results.Add(
                    new EditModeGameObjectConstruction(
                        relativePath,
                        lineNumber,
                        sourceLine,
                        exemptionId
                    )
                );
            }

            return results;
        }

        private static bool IsGameObjectTarget(ScanIndex scan, int constructionIndex)
        {
            string source = scan.Source;
            ISet<string> gameObjectTypes = scan.GameObjectTypes;
            int statementStart = source.LastIndexOf(';', Math.Max(0, constructionIndex - 1));
            string statement = source.Substring(
                statementStart + 1,
                constructionIndex - statementStart - 1
            );
            if (
                ContainsGameObjectDeclaration(statement, gameObjectTypes)
                && (
                    IsTopLevelTargetExpression(statement)
                    || IsParenthesizedTargetExpression(statement)
                    || IsExplicitGameObjectArrayInitializer(statement, gameObjectTypes)
                    || ArrayInitializerTarget.IsMatch(statement)
                )
            )
            {
                return true;
            }

            int windowStart = Math.Max(0, constructionIndex - 500);
            string window = source.Substring(windowStart, constructionIndex - windowStart);
            if (
                gameObjectTypes.Any(type =>
                    ForPattern(
                            $@"(?:^|[^\w.]){Regex.Escape(type)}\s+[A-Za-z_]\w*\s*{{[^{{}}]*}}\s*=\s*$"
                        )
                        .IsMatch(window)
                )
            )
            {
                return true;
            }

            Match assigned = AssignedTarget.Match(statement);
            if (assigned.Success)
            {
                return scan.ResolvesToGameObject(assigned.Groups["name"].Value, constructionIndex);
            }

            if (ReturnKeyword.IsMatch(statement))
            {
                if (scan.IsInsideGameObjectReturningMethod(constructionIndex))
                {
                    return true;
                }
            }

            if (GameObjectFactoryLambda.IsMatch(statement))
            {
                return true;
            }

            if (GetterReturn.IsMatch(statement))
            {
                return scan.IsInsideGameObjectProperty(constructionIndex);
            }

            return IsInsideExpressionBodiedGameObjectMethod(
                source,
                constructionIndex,
                gameObjectTypes
            );
        }

        private static bool ContainsGameObjectDeclaration(string text, ISet<string> gameObjectTypes)
        {
            foreach (string type in gameObjectTypes)
            {
                string escaped = Regex.Escape(type);
                if (
                    ForPattern($@"(?:^|[^\w.]){escaped}\s*(?:\[\])?\s*\??\s+[A-Za-z_]\w*[^;]*=")
                        .IsMatch(text)
                )
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsParenthesizedTargetExpression(string statement)
        {
            int assignment = statement.IndexOf('=');
            if (assignment < 0)
            {
                return false;
            }

            string targetPrefix = statement.Substring(assignment + 1);
            return EmptyTargetPrefix.IsMatch(targetPrefix);
        }

        private static bool IsExplicitGameObjectArrayInitializer(
            string statement,
            IEnumerable<string> gameObjectTypes
        )
        {
            int assignment = statement.IndexOf('=');
            if (assignment < 0)
            {
                return false;
            }

            string targetPrefix = statement.Substring(assignment + 1);
            return gameObjectTypes.Any(type =>
                ForPattern($@"^\s*new\s+{Regex.Escape(type)}\s*\[\s*\]\s*\{{[^{{}}]*$")
                    .IsMatch(targetPrefix)
            );
        }

        private static bool IsInsideExpressionBodiedGameObjectMethod(
            string source,
            int constructionIndex,
            ISet<string> gameObjectTypes
        )
        {
            int arrow = source.LastIndexOf("=>", constructionIndex, StringComparison.Ordinal);
            int semicolon = source.LastIndexOf(';', Math.Max(0, constructionIndex - 1));
            if (arrow < 0 || arrow < semicolon)
            {
                return false;
            }

            string signature = source.Substring(semicolon + 1, arrow - semicolon - 1);
            return gameObjectTypes.Any(type =>
                ForPattern($@"(?:^|[^\w.]){Regex.Escape(type)}\s+[A-Za-z_]\w*\s*\([^)]*\)\s*$")
                    .IsMatch(signature)
            );
        }

        private static int FindMatchingParenthesis(string source, int openParenthesis)
        {
            int depth = 0;
            for (int index = openParenthesis; index < source.Length; index++)
            {
                if (source[index] == '(')
                {
                    depth++;
                }
                else if (source[index] == ')' && --depth == 0)
                {
                    return index;
                }
            }
            return -1;
        }

        private static bool IsTopLevelTargetExpression(string statement)
        {
            int assignment = statement.IndexOf('=');
            if (assignment < 0)
            {
                return false;
            }

            int depth = 0;
            for (int index = assignment + 1; index < statement.Length; index++)
            {
                char current = statement[index];
                if (current == '(' || current == '[' || current == '{')
                {
                    depth++;
                }
                else if (current == ')' || current == ']' || current == '}')
                {
                    depth--;
                }
            }
            return depth == 0;
        }

        private static IReadOnlyList<string> SplitTopLevelArguments(string arguments)
        {
            List<string> result = new();
            int start = 0;
            int depth = 0;
            for (int index = 0; index < arguments.Length; index++)
            {
                char current = arguments[index];
                if (current == '(' || current == '[' || current == '{' || current == '<')
                {
                    depth++;
                }
                else if (current == ')' || current == ']' || current == '}' || current == '>')
                {
                    depth--;
                }
                else if (current == ',' && depth == 0)
                {
                    result.Add(arguments.Substring(start, index - start));
                    start = index + 1;
                }
            }
            result.Add(arguments.Substring(start));
            return result;
        }

        private static string ParseExemptionId(
            IReadOnlyDictionary<int, string> lineComments,
            int lineNumber
        )
        {
            if (!lineComments.TryGetValue(lineNumber, out string comment))
            {
                return string.Empty;
            }

            string trimmed = comment.Trim();
            if (!trimmed.StartsWith(ExemptionPrefix, StringComparison.Ordinal))
            {
                return string.Empty;
            }

            string id = trimmed.Substring(ExemptionPrefix.Length).Trim();
            return ExemptionIdFormat.IsMatch(id) ? id : string.Empty;
        }

        private static LexedSource Lex(string source)
        {
            char[] sanitized = source.ToCharArray();
            Dictionary<int, string> lineComments = new();
            Stack<LexerFrame> frames = new();
            frames.Push(new LexerFrame(LexerState.Code, 0));
            int lineNumber = 1;

            for (int index = 0; index < source.Length; index++)
            {
                char current = source[index];
                char next = index + 1 < source.Length ? source[index + 1] : '\0';
                LexerFrame frame = frames.Peek();
                bool isCode = frame.State == LexerState.Code;

                if (isCode && frame.InterpolationDepth > 0 && current == '}')
                {
                    sanitized[index] = ' ';
                    frame.InterpolationDepth--;
                    frames.Pop();
                    if (frame.InterpolationDepth > 0)
                    {
                        frames.Push(frame);
                    }
                    continue;
                }

                if (isCode && current == '/' && next == '/')
                {
                    int end = source.IndexOf('\n', index);
                    if (end < 0)
                    {
                        end = source.Length;
                    }
                    lineComments[lineNumber] = source.Substring(index, end - index);
                    frames.Push(new LexerFrame(LexerState.LineComment, 0));
                }
                else if (isCode && current == '/' && next == '*')
                {
                    frames.Push(new LexerFrame(LexerState.BlockComment, 0));
                }
                else if (isCode && current == '\'')
                {
                    sanitized[index] = ' ';
                    frames.Push(new LexerFrame(LexerState.Character, 0));
                    continue;
                }
                else if (isCode && current == '$' && next == '"')
                {
                    sanitized[index] = ' ';
                    sanitized[index + 1] = ' ';
                    index++;
                    frames.Push(new LexerFrame(LexerState.InterpolatedString, 0));
                    continue;
                }
                else if (
                    isCode
                    && (
                        (
                            current == '$'
                            && next == '@'
                            && index + 2 < source.Length
                            && source[index + 2] == '"'
                        )
                        || (
                            current == '@'
                            && next == '$'
                            && index + 2 < source.Length
                            && source[index + 2] == '"'
                        )
                    )
                )
                {
                    sanitized[index] = ' ';
                    sanitized[index + 1] = ' ';
                    sanitized[index + 2] = ' ';
                    index += 2;
                    frames.Push(new LexerFrame(LexerState.InterpolatedVerbatimString, 0));
                    continue;
                }
                else if (isCode && current == '@' && next == '"')
                {
                    sanitized[index] = ' ';
                    sanitized[index + 1] = ' ';
                    index++;
                    frames.Push(new LexerFrame(LexerState.VerbatimString, 0));
                    continue;
                }
                else if (isCode && current == '"')
                {
                    sanitized[index] = ' ';
                    frames.Push(new LexerFrame(LexerState.String, 0));
                    continue;
                }
                else if (isCode && frame.InterpolationDepth > 0 && current == '{')
                {
                    frame.InterpolationDepth++;
                    frames.Pop();
                    frames.Push(frame);
                }

                if (frames.Peek().State != LexerState.Code)
                {
                    sanitized[index] = current == '\r' || current == '\n' ? current : ' ';
                }

                LexerState state = frames.Peek().State;
                if (state == LexerState.LineComment && (current == '\r' || current == '\n'))
                {
                    frames.Pop();
                }
                else if (state == LexerState.BlockComment && current == '*' && next == '/')
                {
                    sanitized[index + 1] = ' ';
                    index++;
                    frames.Pop();
                }
                else if (
                    (state == LexerState.String || state == LexerState.Character)
                    && current == '\\'
                    && next != '\0'
                )
                {
                    sanitized[index + 1] = ' ';
                    index++;
                }
                else if (
                    (state == LexerState.String && current == '"')
                    || (state == LexerState.Character && current == '\'')
                )
                {
                    frames.Pop();
                }
                else if (
                    (
                        state == LexerState.VerbatimString
                        || state == LexerState.InterpolatedVerbatimString
                    )
                    && current == '"'
                )
                {
                    if (next == '"')
                    {
                        sanitized[index + 1] = ' ';
                        index++;
                    }
                    else
                    {
                        frames.Pop();
                    }
                }
                else if (state == LexerState.InterpolatedString && current == '\\' && next != '\0')
                {
                    sanitized[index + 1] = ' ';
                    index++;
                }
                else if (state == LexerState.InterpolatedString && current == '"')
                {
                    frames.Pop();
                }
                else if (
                    (
                        state == LexerState.InterpolatedString
                        || state == LexerState.InterpolatedVerbatimString
                    )
                    && current == '{'
                )
                {
                    if (next == '{')
                    {
                        sanitized[index + 1] = ' ';
                        index++;
                    }
                    else
                    {
                        frames.Push(new LexerFrame(LexerState.Code, 1));
                    }
                }

                if (current == '\n')
                {
                    lineNumber++;
                }
            }

            return new LexedSource(new string(sanitized), lineComments);
        }

        private enum LexerState
        {
            Code,
            LineComment,
            BlockComment,
            String,
            VerbatimString,
            InterpolatedString,
            InterpolatedVerbatimString,
            Character,
        }

        private struct LexerFrame
        {
            internal LexerFrame(LexerState state, int interpolationDepth)
            {
                State = state;
                InterpolationDepth = interpolationDepth;
            }

            internal LexerState State;
            internal int InterpolationDepth;
        }

        private readonly struct LexedSource
        {
            internal LexedSource(string sanitized, IReadOnlyDictionary<int, string> lineComments)
            {
                Sanitized = sanitized;
                LineComments = lineComments;
            }

            internal string Sanitized { get; }
            internal IReadOnlyDictionary<int, string> LineComments { get; }
        }

        private readonly struct BraceSpan
        {
            internal BraceSpan(int open, int close)
            {
                Open = open;
                Close = close;
            }

            internal int Open { get; }
            internal int Close { get; }

            internal bool Contains(int index)
            {
                return Open < index && index < Close;
            }
        }

        private readonly struct TypedDeclaration
        {
            internal TypedDeclaration(int start, int end, string type)
            {
                Start = start;
                End = end;
                Type = type;
            }

            internal int Start { get; }
            internal int End { get; }
            internal string Type { get; }
        }

        /// <summary>
        /// Whole-file lookups computed once per <see cref="Find"/> call. Every query the scanner
        /// used to answer by re-scanning the source per construction -- line numbers, brace
        /// matching, enclosing scope ends, declarations by name, and GameObject-returning method
        /// and property bodies -- resolves here against a prebuilt index instead, which is what
        /// keeps the scan linear in file length rather than quadratic.
        /// </summary>
        private sealed class ScanIndex
        {
            private readonly int[] _lineStarts;
            private readonly Dictionary<int, int> _closeByOpenBrace;
            private readonly int[] _scopeChangeAt;
            private readonly int[] _scopeChangeEnd;
            private Dictionary<string, List<TypedDeclaration>> _declarationsByName;
            private List<BraceSpan> _gameObjectMethodBodies;
            private List<BraceSpan> _gameObjectPropertyBodies;

            internal ScanIndex(string source, ISet<string> gameObjectTypes)
            {
                Source = source;
                GameObjectTypes = gameObjectTypes;

                List<int> lineStarts = new() { 0 };
                List<int> scopeChangeAt = new() { 0 };
                List<int> scopeChangeOpenBrace = new() { -1 };
                Stack<int> openBraces = new();
                _closeByOpenBrace = new Dictionary<int, int>();

                for (int index = 0; index < source.Length; index++)
                {
                    char current = source[index];
                    if (current == '\n')
                    {
                        lineStarts.Add(index + 1);
                    }
                    else if (current == '{')
                    {
                        openBraces.Push(index);
                        scopeChangeAt.Add(index + 1);
                        scopeChangeOpenBrace.Add(index);
                    }
                    else if (current == '}' && openBraces.Count > 0)
                    {
                        _closeByOpenBrace[openBraces.Pop()] = index;
                        scopeChangeAt.Add(index + 1);
                        scopeChangeOpenBrace.Add(openBraces.Count > 0 ? openBraces.Peek() : -1);
                    }
                }

                _lineStarts = lineStarts.ToArray();
                _scopeChangeAt = scopeChangeAt.ToArray();
                _scopeChangeEnd = new int[scopeChangeOpenBrace.Count];
                for (int change = 0; change < scopeChangeOpenBrace.Count; change++)
                {
                    int openBrace = scopeChangeOpenBrace[change];
                    _scopeChangeEnd[change] = MatchingBrace(openBrace);
                }
            }

            internal string Source { get; }

            internal ISet<string> GameObjectTypes { get; }

            internal int LineNumberAt(int index)
            {
                return FloorIndex(_lineStarts, index) + 1;
            }

            /// <summary>
            /// Matches the enclosing-scope rule the recursive scan used: the innermost brace open
            /// before <paramref name="index"/>, resolved to its close, or the end of the source
            /// when nothing encloses it.
            /// </summary>
            internal int EnclosingScopeEnd(int index)
            {
                return _scopeChangeEnd[FloorIndex(_scopeChangeAt, index)];
            }

            internal bool ResolvesToGameObject(string name, int constructionIndex)
            {
                EnsureDeclarations();
                if (!_declarationsByName.TryGetValue(name, out List<TypedDeclaration> declarations))
                {
                    return false;
                }

                for (int index = declarations.Count - 1; index >= 0; index--)
                {
                    TypedDeclaration declaration = declarations[index];
                    if (declaration.End > constructionIndex)
                    {
                        continue;
                    }
                    if (constructionIndex >= EnclosingScopeEnd(declaration.Start))
                    {
                        continue;
                    }
                    return GameObjectTypes.Contains(declaration.Type.TrimEnd('?'));
                }

                return false;
            }

            internal bool IsInsideGameObjectReturningMethod(int constructionIndex)
            {
                _gameObjectMethodBodies ??= FindBodies(type =>
                    $@"(?:^|[^\w.]){Regex.Escape(type)}\s+[A-Za-z_]\w*\s*\([^;{{}}]*\)\s*{{"
                );
                return IsInsideAny(_gameObjectMethodBodies, constructionIndex);
            }

            internal bool IsInsideGameObjectProperty(int constructionIndex)
            {
                _gameObjectPropertyBodies ??= FindBodies(type =>
                    $@"(?:^|[^\w.]){Regex.Escape(type)}\s+[A-Za-z_]\w*\s*{{"
                );
                return IsInsideAny(_gameObjectPropertyBodies, constructionIndex);
            }

            private int MatchingBrace(int openBrace)
            {
                return openBrace >= 0 && _closeByOpenBrace.TryGetValue(openBrace, out int close)
                    ? close
                    : Source.Length;
            }

            private void EnsureDeclarations()
            {
                if (_declarationsByName != null)
                {
                    return;
                }

                _declarationsByName = new Dictionary<string, List<TypedDeclaration>>(
                    StringComparer.Ordinal
                );
                foreach (Match match in Declaration.Matches(Source))
                {
                    string name = match.Groups["name"].Value;
                    if (
                        !_declarationsByName.TryGetValue(
                            name,
                            out List<TypedDeclaration> declarations
                        )
                    )
                    {
                        declarations = new List<TypedDeclaration>();
                        _declarationsByName[name] = declarations;
                    }
                    declarations.Add(
                        new TypedDeclaration(
                            match.Index,
                            match.Index + match.Length,
                            match.Groups["type"].Value
                        )
                    );
                }
            }

            private List<BraceSpan> FindBodies(Func<string, string> patternForType)
            {
                List<BraceSpan> bodies = new();
                foreach (string type in GameObjectTypes)
                {
                    foreach (Match match in ForPattern(patternForType(type)).Matches(Source))
                    {
                        int openBrace = Source.IndexOf('{', match.Index);
                        if (openBrace >= 0)
                        {
                            bodies.Add(new BraceSpan(openBrace, MatchingBrace(openBrace)));
                        }
                    }
                }
                return bodies;
            }

            private static bool IsInsideAny(List<BraceSpan> bodies, int index)
            {
                foreach (BraceSpan body in bodies)
                {
                    if (body.Contains(index))
                    {
                        return true;
                    }
                }
                return false;
            }

            private static int FloorIndex(int[] sortedPositions, int index)
            {
                int position = Array.BinarySearch(sortedPositions, index);
                if (position < 0)
                {
                    position = ~position - 1;
                }
                return Math.Max(0, position);
            }
        }
    }

    internal readonly struct EditModeGameObjectConstruction
    {
        internal EditModeGameObjectConstruction(
            string relativePath,
            int lineNumber,
            string sourceLine,
            string exemptionId
        )
        {
            RelativePath = relativePath;
            LineNumber = lineNumber;
            SourceLine = sourceLine;
            ExemptionId = exemptionId;
        }

        internal string RelativePath { get; }

        internal int LineNumber { get; }

        internal string SourceLine { get; }

        internal string ExemptionId { get; }

        internal string Display => $"{RelativePath}:{LineNumber}: {SourceLine}";
    }
}
#endif
