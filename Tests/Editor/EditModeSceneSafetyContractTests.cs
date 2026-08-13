#if UNITY_EDITOR && UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Editor
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
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

        [TestCase("GameObject host = new GameObject(\"classic\");", 1)]
        [TestCase("GameObject host = new GameObject { name = \"initializer\" };", 1)]
        [TestCase("UnityEngine.GameObject host = new UnityEngine.GameObject(\"qualified\");", 1)]
        [TestCase("GameObject host = new(\"target typed\");", 1)]
        [TestCase("GameObject host = (new(\"parenthesized\"));", 1)]
        [TestCase("GameObject host;\nhost = new(\"deferred\");", 1)]
        [TestCase("GameObject host;\nhost = (new(\"parenthesized deferred\"));", 1)]
        [TestCase(
            "void First() { GameObject receiver = new(\"unsafe\"); }\n"
                + "void Second() { FlowGraphComponentNode receiver = new(); }",
            1
        )]
        [TestCase("// GameObject host = new(\"comment\");", 0)]
        [TestCase("string sample = \"GameObject host = new(\\\"text\\\");\";", 0)]
        [TestCase(
            "GameObject host = EditorUtility.CreateGameObjectWithHideFlags(\"safe\", HideFlags.HideAndDontSave);",
            0
        )]
        [TestCase("GameObject Host { get; } = new(\"property\");", 1)]
        [TestCase("GameObject CreateHost() => new(\"arrow\");", 1)]
        [TestCase("GameObject CreateHost() { return new(\"return\"); }", 1)]
        [TestCase("GameObject CreateHost() { return condition ? new(\"return\") : null; }", 1)]
        [TestCase("GameObject Host { get { return new(\"getter\"); } }", 1)]
        [TestCase("Func<GameObject> factory = () => new(\"lambda\");", 1)]
        [TestCase("GameObject host = condition ? new(\"branch\") : null;", 1)]
        [TestCase("GameObject[] hosts = { new(\"array\") };", 1)]
        [TestCase("GameObject[] hosts = new GameObject[] { new(\"explicit array\") };", 1)]
        [TestCase("GameObject host; this.host = new(\"member\");", 1)]
        [TestCase("GameObject host; host ??= new(\"coalesce\");", 1)]
        [TestCase("using GO = UnityEngine.GameObject; GO host = new GO(\"alias\");", 1)]
        [TestCase("GameObject.CreatePrimitive(PrimitiveType.Cube);", 1)]
        [TestCase("Object.Instantiate(prefab);", 1)]
        [TestCase("Object.Instantiate<GameObject>(prefab);", 1)]
        [TestCase("PrefabUtility.InstantiatePrefab(prefab);", 1)]
        [TestCase("EditorUtility.CreateGameObjectWithHideFlags(\"unsafe\", HideFlags.None);", 1)]
        [TestCase(
            "EditorUtility.CreateGameObjectWithHideFlags(\"unsafe\", condition ? HideFlags.HideAndDontSave : HideFlags.None);",
            1
        )]
        [TestCase("string value = $\"{new GameObject(\"unsafe\")}\";", 1)]
        [TestCase(
            "GameObject receiver; void Probe() { FlowGraphComponentNode receiver; receiver = new(); }",
            0
        )]
        [TestCase("GameObject host = condition ? Make() : Wrap(new());", 0)]
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
                if (IsGameObjectTarget(sanitized, construction.Index, gameObjectTypes))
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
                int lineNumber = 1;
                for (int cursor = 0; cursor < index; cursor++)
                {
                    if (sanitized[cursor] == '\n')
                    {
                        lineNumber++;
                    }
                }

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

        private static bool IsGameObjectTarget(
            string source,
            int constructionIndex,
            ISet<string> gameObjectTypes
        )
        {
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
                    || Regex.IsMatch(
                        statement,
                        @"\b(?:(?:global::)?UnityEngine\.)?GameObject\s*\[\]\s+[A-Za-z_]\w*\s*=\s*{[^}]*$"
                    )
                )
            )
            {
                return true;
            }

            int windowStart = Math.Max(0, constructionIndex - 500);
            string window = source.Substring(windowStart, constructionIndex - windowStart);
            if (
                gameObjectTypes.Any(type =>
                    Regex.IsMatch(
                        window,
                        $@"(?:^|[^\w.]){Regex.Escape(type)}\s+[A-Za-z_]\w*\s*{{[^{{}}]*}}\s*=\s*$"
                    )
                )
            )
            {
                return true;
            }

            Match assigned = AssignedTarget.Match(statement);
            if (assigned.Success)
            {
                return ResolvesToGameObject(
                    source,
                    assigned.Groups["name"].Value,
                    constructionIndex,
                    gameObjectTypes
                );
            }

            string before = source.Substring(0, constructionIndex);
            if (Regex.IsMatch(statement, @"\breturn\b"))
            {
                if (IsInsideGameObjectReturningMethod(source, constructionIndex, gameObjectTypes))
                {
                    return true;
                }
            }

            if (
                Regex.IsMatch(
                    statement,
                    @"\bFunc\s*<\s*(?:(?:global::)?UnityEngine\.)?GameObject\s*>[^;]*=>[^;]*$"
                )
            )
            {
                return true;
            }

            if (Regex.IsMatch(statement, @"\bget\s*{[^{}]*\breturn\b"))
            {
                return IsInsideGameObjectProperty(source, constructionIndex, gameObjectTypes);
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
                    Regex.IsMatch(
                        text,
                        $@"(?:^|[^\w.]){escaped}\s*(?:\[\])?\s*\??\s+[A-Za-z_]\w*[^;]*="
                    )
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
            return Regex.IsMatch(targetPrefix, @"^\s*\(*\s*$");
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
                Regex.IsMatch(
                    targetPrefix,
                    $@"^\s*new\s+{Regex.Escape(type)}\s*\[\s*\]\s*\{{[^{{}}]*$"
                )
            );
        }

        private static bool ResolvesToGameObject(
            string source,
            string name,
            int constructionIndex,
            ISet<string> gameObjectTypes
        )
        {
            Regex declaration = new(
                $@"(?<type>[A-Za-z_][\w.:<>?\[\]]*)\s+{Regex.Escape(name)}\s*(?:[;=,\){{])"
            );
            Match nearest = declaration
                .Matches(source.Substring(0, constructionIndex))
                .Cast<Match>()
                .Where(match => constructionIndex < FindEnclosingScopeEnd(source, match.Index))
                .LastOrDefault();
            return nearest != null
                && gameObjectTypes.Contains(nearest.Groups["type"].Value.TrimEnd('?'));
        }

        private static bool IsInsideGameObjectReturningMethod(
            string source,
            int constructionIndex,
            ISet<string> gameObjectTypes
        )
        {
            foreach (string type in gameObjectTypes)
            {
                Regex method = new(
                    $@"(?:^|[^\w.]){Regex.Escape(type)}\s+[A-Za-z_]\w*\s*\([^;{{}}]*\)\s*{{"
                );
                foreach (Match match in method.Matches(source))
                {
                    int openBrace = source.IndexOf('{', match.Index);
                    if (
                        openBrace >= 0
                        && openBrace < constructionIndex
                        && constructionIndex < FindMatchingBrace(source, openBrace)
                    )
                    {
                        return true;
                    }
                }
            }
            return false;
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
                Regex.IsMatch(
                    signature,
                    $@"(?:^|[^\w.]){Regex.Escape(type)}\s+[A-Za-z_]\w*\s*\([^)]*\)\s*$"
                )
            );
        }

        private static int FindEnclosingScopeEnd(string source, int declarationIndex)
        {
            Stack<int> openBraces = new();
            for (int index = 0; index < declarationIndex; index++)
            {
                if (source[index] == '{')
                {
                    openBraces.Push(index);
                }
                else if (source[index] == '}' && openBraces.Count > 0)
                {
                    openBraces.Pop();
                }
            }
            return openBraces.Count == 0
                ? source.Length
                : FindMatchingBrace(source, openBraces.Peek());
        }

        private static int FindMatchingBrace(string source, int openBrace)
        {
            int depth = 0;
            for (int index = openBrace; index < source.Length; index++)
            {
                if (source[index] == '{')
                {
                    depth++;
                }
                else if (source[index] == '}' && --depth == 0)
                {
                    return index;
                }
            }
            return source.Length;
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

        private static bool IsInsideGameObjectProperty(
            string source,
            int constructionIndex,
            ISet<string> gameObjectTypes
        )
        {
            foreach (string type in gameObjectTypes)
            {
                Regex property = new($@"(?:^|[^\w.]){Regex.Escape(type)}\s+[A-Za-z_]\w*\s*{{");
                foreach (Match match in property.Matches(source))
                {
                    int openBrace = source.IndexOf('{', match.Index);
                    if (
                        openBrace >= 0
                        && openBrace < constructionIndex
                        && constructionIndex < FindMatchingBrace(source, openBrace)
                    )
                    {
                        return true;
                    }
                }
            }
            return false;
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
            return Regex.IsMatch(id, @"^[a-z0-9]+(?:-[a-z0-9]+)*$") ? id : string.Empty;
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
