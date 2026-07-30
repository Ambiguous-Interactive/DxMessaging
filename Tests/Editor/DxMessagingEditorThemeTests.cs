#if UNITY_EDITOR && UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Editor
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Text.RegularExpressions;
    using DxMessaging.Editor;
    using NUnit.Framework;
    using UnityEditor;
    using UnityEngine;
    using UnityEngine.UIElements;

    [TestFixture]
    public sealed class DxMessagingEditorThemeTests
    {
        [Test]
        public void ThemeAssetsLoadFromPackagePaths()
        {
            Assert.That(
                AssetDatabase.LoadAssetAtPath<StyleSheet>(DxMessagingEditorTheme.TokensUssPath),
                Is.Not.Null
            );
            Assert.That(
                AssetDatabase.LoadAssetAtPath<StyleSheet>(DxMessagingEditorTheme.ThemeUssPath),
                Is.Not.Null
            );

            AssertIconLoads(DxMessagingEditorTheme.Icon32FileName, 32);
            AssertIconLoads(DxMessagingEditorTheme.Icon48FileName, 48);
            AssertIconLoads(DxMessagingEditorTheme.Icon256FileName, 256);
        }

        [Test]
        public void ApplyAddsThemeSkinAndStylesheetsIdempotently()
        {
            VisualElement root = new();

            DxMessagingEditorTheme.Apply(root);

            Assert.That(root.ClassListContains(DxMessagingEditorTheme.ThemeClassName), Is.True);
            Assert.That(
                root.ClassListContains(DxMessagingEditorTheme.LightSkinClassName),
                Is.EqualTo(!EditorGUIUtility.isProSkin)
            );
            Assert.That(
                root.ClassListContains(DxMessagingEditorTheme.DarkSkinClassName),
                Is.EqualTo(EditorGUIUtility.isProSkin)
            );
            Assert.That(
                StyleSheetCount(root, DxMessagingEditorTheme.LoadTokensStylesheet()),
                Is.EqualTo(1)
            );
            Assert.That(
                StyleSheetCount(root, DxMessagingEditorTheme.LoadThemeStylesheet()),
                Is.EqualTo(1)
            );
            int stylesheetCount = root.styleSheets.count;

            DxMessagingEditorTheme.Apply(root);

            Assert.That(root.styleSheets.count, Is.EqualTo(stylesheetCount));
            Assert.That(
                StyleSheetCount(root, DxMessagingEditorTheme.LoadTokensStylesheet()),
                Is.EqualTo(1)
            );
            Assert.That(
                StyleSheetCount(root, DxMessagingEditorTheme.LoadThemeStylesheet()),
                Is.EqualTo(1)
            );
        }

        [Test]
        public void PaletteColorsMatchCanonicalDesignTokens()
        {
            AssertColor(DxMessagingEditorPalette.Amber, ReadTokenColor("--dx-accent"));
            AssertColor(DxMessagingEditorPalette.AmberSoft, ReadTokenColor("--dx-accent-soft"));
            AssertColor(DxMessagingEditorPalette.Untargeted, ReadTokenColor("--dx-untargeted"));
            AssertColor(DxMessagingEditorPalette.Targeted, ReadTokenColor("--dx-targeted"));
            AssertColor(DxMessagingEditorPalette.Broadcast, ReadTokenColor("--dx-broadcast"));
            AssertColor(DxMessagingEditorPalette.Trace, ReadTokenColor("--dx-untargeted"));
            AssertColor(DxMessagingEditorPalette.TraceMessage, ReadTokenColor("--dx-broadcast"));
            AssertColor(DxMessagingEditorPalette.TraceTarget, ReadTokenColor("--dx-accent-soft"));

            // The IMGUI component inspector cannot read the stylesheet, so it reads these two pairs
            // from the palette instead. Both skins are pinned, because a light-skin value that drifts
            // is exactly the unreadable label this replaced.
            AssertColor(
                DxMessagingEditorPalette.BroadcastText,
                ReadTokenColor("--dx-broadcast-text")
            );
            AssertColor(
                DxMessagingEditorPalette.BroadcastTextOnLight,
                ReadTokenColor("--dx-broadcast-text", lightSkin: true)
            );
            AssertColor(
                DxMessagingEditorPalette.AmberOnLight,
                ReadTokenColor("--dx-accent", lightSkin: true)
            );
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ForSkinPicksTheValueBelongingToTheSkinInUse(bool proSkin)
        {
            Assert.That(
                DxMessagingEditorPalette.ForSkin(Color.red, Color.blue, proSkin),
                Is.EqualTo(proSkin ? Color.red : Color.blue)
            );
        }

        /// <summary>
        /// Every <c>.dx-*</c> class the stylesheets declare has to be referenced by the editor
        /// sources.
        /// </summary>
        /// <remarks>
        /// The theme was migrated from its design-system spec wholesale, so it described a larger
        /// surface than the windows rendered, and the leftovers were only ever found by hand-auditing
        /// -- which is why the audit that closed the last of them (issue #304) still missed
        /// <c>.dx-sep</c>. Asserting the invariant instead of the leftovers is what stops that
        /// recurring: a class added to the sheet but never rendered fails here, and so does a
        /// rendered class whose last C# reference was deleted. A class with genuinely no home belongs
        /// out of the sheet, not on an allowlist here.
        /// </remarks>
        [Test]
        public void EveryStylesheetClassIsRenderedByAnEditorSurface()
        {
            SortedSet<string> declared = new(StringComparer.Ordinal);
            CollectStylesheetClassNames(DxMessagingEditorTheme.TokensUssPath, declared);
            CollectStylesheetClassNames(DxMessagingEditorTheme.ThemeUssPath, declared);
            Assert.That(
                declared,
                Is.Not.Empty,
                "The stylesheet scan found no classes at all, so it is not reading the sheets."
            );

            string sources = ReadEditorSourceText();
            List<string> dead = new();
            foreach (string className in declared)
            {
                // The quoted form only: a class named in an XML doc comment is documentation, not a
                // surface that renders it.
                if (!sources.Contains("\"" + className + "\"", StringComparison.Ordinal))
                {
                    dead.Add(className);
                }
            }

            Assert.That(
                dead,
                Is.Empty,
                "These stylesheet classes are dead style. Render each one on a real surface or "
                    + "remove it from the sheet: "
                    + string.Join(", ", dead)
            );
        }

        [Test]
        public void BrandingPrototypeAssemblyAndWindowsAreNotLoaded()
        {
            const string brandingAssemblyName = "WallstopStudios.DxMessaging.Editor.Branding";
            const string brandingNamespacePrefix = "WallstopStudios.DxMessaging.Editor.Branding.";
            List<string> loadedPrototypeArtifacts = new();

            foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (
                    string.Equals(
                        assembly.GetName().Name,
                        brandingAssemblyName,
                        StringComparison.Ordinal
                    )
                )
                {
                    loadedPrototypeArtifacts.Add("assembly: " + assembly.FullName);
                }
            }

            foreach (Type windowType in TypeCache.GetTypesDerivedFrom<EditorWindow>())
            {
                if (
                    windowType.FullName != null
                    && windowType.FullName.StartsWith(
                        brandingNamespacePrefix,
                        StringComparison.Ordinal
                    )
                )
                {
                    loadedPrototypeArtifacts.Add("window: " + windowType.AssemblyQualifiedName);
                }
            }

            Assert.That(
                loadedPrototypeArtifacts,
                Is.Empty,
                "The design-system branding prototype is imported into the host project. "
                    + "Remove its design-system/production/unity-package content so only the "
                    + "canonical Tools/Wallstop Studios/DxMessaging windows remain. Loaded "
                    + "prototype artifacts: "
                    + string.Join(", ", loadedPrototypeArtifacts)
            );
        }

        [Test]
        public void CreateEmptyStateBuildsContainerWithTitleAndBody()
        {
            VisualElement empty = DxMessagingEditorTheme.CreateEmptyState(
                "No data",
                "Nothing to show yet.",
                bodyName: "body-name",
                titleName: "title-name"
            );

            Assert.That(empty.ClassListContains(DxMessagingEditorTheme.EmptyClassName), Is.True);

            Label title = empty.Q<Label>("title-name");
            Assert.That(title, Is.Not.Null);
            Assert.That(title.text, Is.EqualTo("No data"));
            Assert.That(
                title.ClassListContains(DxMessagingEditorTheme.EmptyTitleClassName),
                Is.True
            );

            Label body = empty.Q<Label>("body-name");
            Assert.That(body, Is.Not.Null);
            Assert.That(body.text, Is.EqualTo("Nothing to show yet."));
            Assert.That(body.ClassListContains(DxMessagingEditorTheme.EmptyBodyClassName), Is.True);
            Assert.That(body.style.whiteSpace.value, Is.EqualTo(WhiteSpace.Normal));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void CreateEmptyStateOmitsTitleWhenBlank(string blankTitle)
        {
            VisualElement empty = DxMessagingEditorTheme.CreateEmptyState(
                blankTitle,
                body: "Body only.",
                bodyName: "body-name"
            );

            Assert.That(empty.ClassListContains(DxMessagingEditorTheme.EmptyClassName), Is.True);
            Assert.That(
                empty.Query<Label>(className: DxMessagingEditorTheme.EmptyTitleClassName).ToList(),
                Is.Empty
            );
            Assert.That(empty.Q<Label>("body-name"), Is.Not.Null);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void CreateEmptyStateOmitsBodyWhenBlank(string blankBody)
        {
            VisualElement empty = DxMessagingEditorTheme.CreateEmptyState(
                "Title only",
                blankBody,
                titleName: "title-name"
            );

            Assert.That(empty.ClassListContains(DxMessagingEditorTheme.EmptyClassName), Is.True);
            Assert.That(empty.Q<Label>("title-name"), Is.Not.Null);
            Assert.That(
                empty.Query<Label>(className: DxMessagingEditorTheme.EmptyBodyClassName).ToList(),
                Is.Empty
            );
        }

        [Test]
        public void ApplyCompleteBorderSetsUniformOnePixelBorder()
        {
            VisualElement element = new();

            DxMessagingEditorTheme.ApplyCompleteBorder(element, DxMessagingEditorPalette.Amber);

            Assert.That(
                element.style.borderTopWidth.value,
                Is.EqualTo(DxMessagingEditorTheme.CompleteBorderWidth)
            );
            Assert.That(
                element.style.borderRightWidth.value,
                Is.EqualTo(DxMessagingEditorTheme.CompleteBorderWidth)
            );
            Assert.That(
                element.style.borderBottomWidth.value,
                Is.EqualTo(DxMessagingEditorTheme.CompleteBorderWidth)
            );
            Assert.That(
                element.style.borderLeftWidth.value,
                Is.EqualTo(DxMessagingEditorTheme.CompleteBorderWidth)
            );
            AssertColor(element.style.borderTopColor.value, DxMessagingEditorPalette.Amber);
            AssertColor(element.style.borderRightColor.value, DxMessagingEditorPalette.Amber);
            AssertColor(element.style.borderBottomColor.value, DxMessagingEditorPalette.Amber);
            AssertColor(element.style.borderLeftColor.value, DxMessagingEditorPalette.Amber);
        }

        private static void AssertIconLoads(string fileName, int expectedSize)
        {
            Texture2D icon = DxMessagingEditorTheme.LoadIcon(fileName);

            Assert.That(icon, Is.Not.Null, $"Missing icon {fileName}.");
            Assert.That(icon.width, Is.EqualTo(expectedSize), fileName);
            Assert.That(icon.height, Is.EqualTo(expectedSize), fileName);
        }

        private static void AssertColor(Color actual, string expectedHex)
        {
            Assert.That(
                ColorUtility.TryParseHtmlString(expectedHex, out Color expected),
                Is.True,
                expectedHex
            );
            AssertColor(actual, expected, expectedHex);
        }

        private static void AssertColor(Color actual, Color expected, string message = null)
        {
            Assert.That(actual.r, Is.EqualTo(expected.r).Within(0.0001f), message);
            Assert.That(actual.g, Is.EqualTo(expected.g).Within(0.0001f), message);
            Assert.That(actual.b, Is.EqualTo(expected.b).Within(0.0001f), message);
            Assert.That(actual.a, Is.EqualTo(expected.a).Within(0.0001f), message);
        }

        /// <summary>
        /// Adds every <c>.dx-*</c> class selector the sheet declares. Comments are stripped first:
        /// the brand-font rules are commented out until the TTFs are imported, and a commented rule
        /// styles nothing.
        /// </summary>
        private static void CollectStylesheetClassNames(string ussPath, ISet<string> classNames)
        {
            string uss = StripBlockComments(System.IO.File.ReadAllText(ussPath));
            foreach (Match match in Regex.Matches(uss, @"\.(dx-[a-z0-9_-]+)"))
            {
                classNames.Add(match.Groups[1].Value);
            }
        }

        private static string StripBlockComments(string text)
        {
            StringBuilder stripped = new(text.Length);
            int index = 0;
            while (index < text.Length)
            {
                int open = text.IndexOf("/*", index, StringComparison.Ordinal);
                if (open < 0)
                {
                    stripped.Append(text, index, text.Length - index);
                    break;
                }

                stripped.Append(text, index, open - index);
                int close = text.IndexOf("*/", open + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    break;
                }

                index = close + 2;
            }

            return stripped.ToString();
        }

        private static string ReadEditorSourceText()
        {
            string editorRoot = DxMessagingEditorTheme.PackageRoot + "/Editor";
            string[] sourcePaths = System.IO.Directory.GetFiles(
                editorRoot,
                "*.cs",
                System.IO.SearchOption.AllDirectories
            );
            Assert.That(
                sourcePaths,
                Is.Not.Empty,
                $"Expected editor sources under '{editorRoot}'."
            );

            StringBuilder sources = new();
            foreach (string sourcePath in sourcePaths)
            {
                sources.Append(System.IO.File.ReadAllText(sourcePath));
            }

            return sources.ToString();
        }

        /// <summary>
        /// The value of a design token, from the default block or from the
        /// <c>.dx-theme.dx-light</c> override block.
        /// </summary>
        private static Color ReadTokenColor(string tokenName, bool lightSkin = false)
        {
            string prefix = tokenName + ":";
            bool inLightBlock = false;
            foreach (
                string rawLine in System.IO.File.ReadAllLines(DxMessagingEditorTheme.TokensUssPath)
            )
            {
                string line = rawLine.Trim();
                if (line.StartsWith(".dx-theme", StringComparison.Ordinal))
                {
                    inLightBlock = line.Contains(
                        DxMessagingEditorTheme.LightSkinClassName,
                        StringComparison.Ordinal
                    );
                }

                if (!line.StartsWith(prefix, StringComparison.Ordinal) || inLightBlock != lightSkin)
                {
                    continue;
                }

                string value = line.Substring(prefix.Length).Trim();
                int semicolonIndex = value.IndexOf(';');
                if (semicolonIndex >= 0)
                {
                    value = value.Substring(0, semicolonIndex);
                }

                int commentIndex = value.IndexOf("/*", StringComparison.Ordinal);
                if (commentIndex >= 0)
                {
                    value = value.Substring(0, commentIndex);
                }

                value = value.Trim();
                Assert.That(
                    ColorUtility.TryParseHtmlString(value, out Color color),
                    Is.True,
                    $"{tokenName} should be a hex color token."
                );
                return color;
            }

            Assert.Fail(
                $"Missing design token {tokenName} ({(lightSkin ? "light" : "default")} skin) in "
                    + DxMessagingEditorTheme.TokensUssPath
                    + "."
            );
            return Color.clear;
        }

        private static int StyleSheetCount(VisualElement root, StyleSheet styleSheet)
        {
            int count = 0;
            for (int index = 0; index < root.styleSheets.count; index++)
            {
                if (root.styleSheets[index] == styleSheet)
                {
                    count++;
                }
            }

            return count;
        }
    }
}
#endif
