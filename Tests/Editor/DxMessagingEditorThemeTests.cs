#if UNITY_EDITOR && UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Editor
{
    using System;
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

        private static Color ReadTokenColor(string tokenName)
        {
            string prefix = tokenName + ":";
            foreach (
                string rawLine in System.IO.File.ReadAllLines(DxMessagingEditorTheme.TokensUssPath)
            )
            {
                string line = rawLine.Trim();
                if (!line.StartsWith(prefix, StringComparison.Ordinal))
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
                $"Missing design token {tokenName} in {DxMessagingEditorTheme.TokensUssPath}."
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
