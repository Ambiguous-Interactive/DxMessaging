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
        /// <summary>
        /// The three declarations that together clip one line of text to its own box. All three are
        /// required: `text-overflow` does nothing without `overflow` and `white-space`.
        /// </summary>
        private static readonly string[] ClipDeclarations =
        {
            "overflow: hidden;",
            "text-overflow: ellipsis;",
            "white-space: nowrap;",
        };

        private const float ProbeWindowWidth = 240f;
        private const float ProbeWindowHeight = 240f;
        private const float ProbeChildWidth = 100f;
        private const float ProbeChildHeight = 20f;
        private const int ProbeChildCount = 4;

        /// <summary>
        /// How many probe children fit on one line at <see cref="ProbeWindowWidth"/>.
        /// </summary>
        private const int SingleLineChildCount = 2;

        /// <summary>
        /// One line of children, which is exactly what Unity 2021.3 leaves a wrapping container at.
        /// </summary>
        private const float PinnedHeight = ProbeChildHeight;

        private readonly List<EditorWindow> _createdWindows = new();

        [TearDown]
        public void TearDown()
        {
            EditorWindowTestUtility.SuppressHeadlessWindowRenderErrors();
            EditorWindowTestUtility.CloseTrackedWindows(_createdWindows);
        }

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
            AssertColor(DxMessagingEditorPalette.Danger, ReadTokenColor("--dx-danger"));
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

        [Test]
        public void TaxonomyPaletteUsesBluePurpleGreenAndReservesRedForProblems()
        {
            Color untargeted = ReadTokenColor("--dx-untargeted");
            Color targeted = ReadTokenColor("--dx-targeted");
            Color broadcast = ReadTokenColor("--dx-broadcast");
            Color danger = ReadTokenColor("--dx-danger");

            AssertHueBetween(untargeted, 0.52f, 0.66f, "Untargeted must stay blue.");
            AssertHueBetween(targeted, 0.70f, 0.82f, "Targeted must be purple.");
            AssertHueBetween(broadcast, 0.27f, 0.43f, "Broadcast must stay green.");

            Color.RGBToHSV(
                danger,
                out float dangerHue,
                out float dangerSaturation,
                out float dangerValue
            );
            Assert.That(
                dangerHue,
                Is.LessThanOrEqualTo(0.04f).Or.GreaterThanOrEqualTo(0.96f),
                "Problem states must keep the red hue removed from the message taxonomy."
            );
            Assert.That(dangerSaturation, Is.GreaterThanOrEqualTo(0.5f));
            Assert.That(dangerValue, Is.GreaterThanOrEqualTo(0.5f));
            Assert.That(danger, Is.Not.EqualTo(targeted));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TargetedRouteHueMeetsNonTextContrastInBothSkins(bool lightSkin)
        {
            Color targeted = ReadTokenColor("--dx-targeted");
            string[] surfaceTokens =
            {
                "--dx-tool-window",
                "--dx-tool-card",
                "--dx-tool-row-a",
                "--dx-tool-row-b",
            };

            foreach (string surfaceToken in surfaceTokens)
            {
                Color surface = ReadTokenColor(surfaceToken, lightSkin);
                Assert.That(
                    ContrastRatio(targeted, surface),
                    Is.GreaterThanOrEqualTo(3f),
                    $"Targeted route lines and complete borders must remain visible against {surfaceToken} in both editor skins."
                );
            }
        }

        [Test]
        public void TargetedBadgeInkMeetsNormalTextContrast()
        {
            Color targeted = ReadTokenColor("--dx-targeted");
            Color ink = ReadTokenColor("--dx-accent-ink");

            Assert.That(
                ContrastRatio(ink, targeted),
                Is.GreaterThanOrEqualTo(4.5f),
                "Targeted chips and type badges use 10 px text, so their ink must satisfy normal-text contrast."
            );
        }

        [Test]
        public void DangerAdmonitionUsesProblemTokensInsteadOfTaxonomyColor()
        {
            string theme = System.IO.File.ReadAllText(DxMessagingEditorTheme.ThemeUssPath);
            Match dangerRule = Regex.Match(
                theme,
                @"\.dx-danger\s+\.dx-admonition__title\s*\{(?<body>[^}]*)\}",
                RegexOptions.Singleline
            );

            Assert.That(dangerRule.Success, Is.True, "The danger-title rule is missing.");
            Assert.That(dangerRule.Groups["body"].Value, Does.Contain("var(--dx-danger-text)"));
            Assert.That(dangerRule.Groups["body"].Value, Does.Not.Contain("var(--dx-targeted)"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AccentInkMeetsNormalTextContrastInBothSkins(bool lightSkin)
        {
            Color background = ReadTokenColor("--dx-accent", lightSkin);
            Color foreground = ReadTokenColor("--dx-accent-ink", lightSkin);

            Assert.That(
                ContrastRatio(foreground, background),
                Is.GreaterThanOrEqualTo(4.5f),
                "Accent-backed badges and buttons use 10 px text, so their ink must satisfy normal-text contrast."
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

        /// <summary>
        /// UI Toolkit defaults <c>flex-shrink</c> to 0, unlike CSS. A rule that grows but cannot
        /// shrink pushes its siblings past the edge of the panel as soon as its content is wider
        /// than the space it was given, which is what a long fully-qualified type name did to the
        /// Monitor detail pane's <c>.dx-kv__v</c> value.
        /// </summary>
        [Test]
        public void EveryGrowingStylesheetRuleAlsoDeclaresHowItShrinks()
        {
            string uss = StripBlockComments(
                System.IO.File.ReadAllText(DxMessagingEditorTheme.ThemeUssPath)
            );
            List<string> growOnly = new();
            foreach (string block in uss.Split('}'))
            {
                int open = block.IndexOf('{');
                if (open < 0)
                {
                    continue;
                }

                string selector = block.Substring(0, open).Trim().Replace("\n", " ");
                string body = block.Substring(open + 1);
                if (
                    body.Contains("flex-grow", StringComparison.Ordinal)
                    && !body.Contains("flex-shrink", StringComparison.Ordinal)
                )
                {
                    growOnly.Add(selector);
                }
            }

            Assert.That(
                growOnly,
                Is.Empty,
                "UI Toolkit defaults flex-shrink to 0, so these rules grow but never give space "
                    + "back and overflow the panel: "
                    + string.Join(", ", growOnly)
            );
        }

        /// <summary>
        /// The toolbar rule pinned `height`, so a row whose content exceeded one line's worth drew
        /// outside its own box, on top of whatever the window rendered beneath - the overlay #435
        /// reported. `min-height` carries the designed size without capping it. The live Monitor's
        /// row no longer wraps at all (Unity 2021.3 does not resolve a wrapped row's height), but a
        /// pinned `height` would still clip any row that outgrows it.
        /// </summary>
        [Test]
        public void TheSharedToolbarRuleSizesFromItsContentInsteadOfPinningOneLine()
        {
            string uss = StripBlockComments(
                System.IO.File.ReadAllText(DxMessagingEditorTheme.ThemeUssPath)
            );
            string body = FindRuleBody(uss, "." + DxMessagingEditorTheme.ToolbarClassName);

            Assert.That(
                body,
                Does.Contain("min-height:"),
                "The toolbar keeps its designed size through `min-height`, not `height`."
            );
            Assert.That(
                Regex.IsMatch(body, @"(?:^|;)\s*height\s*:"),
                Is.False,
                "A pinned `height` on a wrapping toolbar draws its wrapped lines outside the box "
                    + "and over the content beneath it (#435)."
            );
            Assert.That(
                body,
                Does.Contain("flex-shrink: 0;"),
                "A grown toolbar must not be squeezed back onto its neighbours by the log below."
            );
        }

        /// <summary>
        /// Issue #435's second half: MESSAGE and CONTEXT printed over each other once their columns
        /// shrank, because only the row cells clipped their text. A heading has to clip exactly like
        /// the cell beneath it. <c>IResolvedStyle</c> exposes no <c>overflow</c>, so the clip is
        /// asserted here on the sheet; the ellipsis it produces is asserted on the built tree by
        /// <c>DxMessagingMessageMonitorLiveViewTests</c>.
        /// </summary>
        [Test]
        public void EveryLogColumnHeadingClipsItsTextLikeTheRowCellBeneathIt()
        {
            string uss = StripBlockComments(
                System.IO.File.ReadAllText(DxMessagingEditorTheme.ThemeUssPath)
            );
            string headingSelector = string.Join(
                ", ",
                "." + DxMessagingEditorTheme.ColumnTimeClassName,
                "." + DxMessagingEditorTheme.ColumnTypeClassName,
                "." + DxMessagingEditorTheme.ColumnMessageClassName,
                "." + DxMessagingEditorTheme.ColumnRouteClassName,
                "." + DxMessagingEditorTheme.ColumnCountClassName
            );
            string headingBody = FindRuleBody(uss, headingSelector);

            foreach (string declaration in ClipDeclarations)
            {
                Assert.That(
                    headingBody,
                    Does.Contain(declaration),
                    $"Log headings must declare `{declaration}` or a shrunken heading prints over "
                        + "the one next to it (#435)."
                );
            }

            foreach (
                string cellClass in new[]
                {
                    DxMessagingEditorTheme.RowMessageClassName,
                    DxMessagingEditorTheme.RowRouteClassName,
                }
            )
            {
                string cellBody = FindRuleBody(uss, "." + cellClass);
                foreach (string declaration in ClipDeclarations)
                {
                    Assert.That(
                        cellBody,
                        Does.Contain(declaration),
                        $"Row cell `{cellClass}` must keep the `{declaration}` its heading mirrors."
                    );
                }
            }

            // The flexible column is the one carrying the message. With the default content basis
            // a long message name gave the row a wider column than the word MESSAGE gave the
            // header, so the two drifted out of alignment; the floor keeps it readable at the
            // Monitor's 420px minimum width.
            foreach (
                string flexibleSelector in new[]
                {
                    "." + DxMessagingEditorTheme.ColumnMessageClassName,
                    "." + DxMessagingEditorTheme.RowMessageClassName,
                }
            )
            {
                string body = FindRuleBody(uss, flexibleSelector);
                Assert.That(
                    body,
                    Does.Contain("flex-basis: 0;").And.Contain("min-width:"),
                    $"`{flexibleSelector}` must size from grow and shrink alone, with a floor, so "
                        + "the heading and the cell beneath it stay in one column."
                );
            }
        }

        [Test]
        public void FlowGraphRelationshipCardsUseReadableTextTokensAndSizes()
        {
            string uss = StripBlockComments(
                System.IO.File.ReadAllText(DxMessagingEditorTheme.ThemeUssPath)
            );

            string roleBody = FindRuleBody(
                uss,
                ".dxmessaging-flow-graph-details-relationship .dx-card__label"
            );
            string identityBody = FindRuleBody(
                uss,
                ".dxmessaging-flow-graph-details-relationship .dx-kv__v"
            );
            string metadataBody = FindRuleBody(
                uss,
                ".dxmessaging-flow-graph-details-relationship .dx-detail__frame"
            );

            Assert.That(
                roleBody,
                Does.Contain("color: var(--dx-text);").And.Contain("font-size: 11px;"),
                "Relationship role labels should not inherit the tiny muted card-caption treatment."
            );
            Assert.That(
                identityBody,
                Does.Contain("color: var(--dx-text);").And.Contain("font-size: 12px;"),
                "Relationship identities should be larger than generic diagnostic values."
            );
            Assert.That(
                metadataBody,
                Does.Contain("color: var(--dx-text-dim);").And.Contain("font-size: 11px;"),
                "Relationship metadata should use readable secondary text instead of the faint token."
            );
        }

        /// <summary>
        /// Issue #344: "There is no 'pointer' intelligence for anything clickable." A hover
        /// background only tells a reader who already guessed the element was interactive, so
        /// every shape that answers a click has to declare a cursor. The list is the styles a
        /// click actually lands on; anything added beside them has to say so here too, which is
        /// the point -- a new interactive shape that forgets the cursor fails rather than
        /// shipping the same gap again.
        /// </summary>
        // The row's CHILDREN are listed on purpose. USS `cursor` is NOT inherited, so a rule on
        // `.dx-row` alone paints the pointer over its 14px gutter and nothing else -- the column
        // labels cover the rest of the row and would compute the default arrow, which is the
        // exact gap #344 reported.
        [TestCase(".dx-tool-btn", "link")]
        [TestCase(".dx-btn-accent", "link")]
        [TestCase(".dx-btn-ghost", "link")]
        [TestCase(".dx-chip", "link")]
        [TestCase(".dx-record", "link")]
        [TestCase(".dx-row", "link")]
        [TestCase(".dx-row__time", "link")]
        [TestCase(".dx-row__type", "link")]
        [TestCase(".dx-row__msg", "link")]
        [TestCase(".dx-row__route", "link")]
        [TestCase(".dx-row__count", "link")]
        [TestCase(".dx-dot", "link")]
        [TestCase(".dx-detail__link", "link")]
        [TestCase(".dx-clickable", "link")]
        [TestCase(".dx-resizer", "split-resize-up-down")]
        public void EveryInteractiveStylesheetRuleDeclaresACursor(
            string selector,
            string expectedCursor
        )
        {
            string uss = StripBlockComments(
                System.IO.File.ReadAllText(DxMessagingEditorTheme.ThemeUssPath)
            );

            string body = null;
            foreach (string block in uss.Split('}'))
            {
                int open = block.IndexOf('{');
                if (open < 0)
                {
                    continue;
                }

                string blockSelector = block.Substring(0, open).Trim().Replace("\n", " ");
                if (string.Equals(blockSelector, selector, StringComparison.Ordinal))
                {
                    body = block.Substring(open + 1);
                    break;
                }
            }

            Assert.That(body, Is.Not.Null, $"The stylesheet declares no `{selector}` rule.");
            Assert.That(
                body.Contains($"cursor: {expectedCursor};", StringComparison.Ordinal),
                Is.True,
                $"`{selector}` must declare `cursor: {expectedCursor};`. Asserting only that some "
                    + "cursor is present would accept `cursor: arrow`, which is the default and "
                    + "tells a reader nothing."
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

        [Test]
        public void ApplyCompleteBorderSupportsUniformCustomWidth()
        {
            VisualElement element = new();

            DxMessagingEditorTheme.ApplyCompleteBorder(
                element,
                DxMessagingEditorPalette.AmberSoft,
                3f
            );

            Assert.That(element.style.borderTopWidth.value, Is.EqualTo(3f));
            Assert.That(element.style.borderRightWidth.value, Is.EqualTo(3f));
            Assert.That(element.style.borderBottomWidth.value, Is.EqualTo(3f));
            Assert.That(element.style.borderLeftWidth.value, Is.EqualTo(3f));
            AssertColor(element.style.borderTopColor.value, DxMessagingEditorPalette.AmberSoft);
            AssertColor(element.style.borderRightColor.value, DxMessagingEditorPalette.AmberSoft);
            AssertColor(element.style.borderBottomColor.value, DxMessagingEditorPalette.AmberSoft);
            AssertColor(element.style.borderLeftColor.value, DxMessagingEditorPalette.AmberSoft);
        }

        /// <summary>
        /// Issue #440: on Unity 2021.3 a wrapping container keeps its single-line height, so the
        /// lines its children wrap onto draw outside it and over whatever the window paints
        /// beneath. Pinning the height reproduces that refusal to grow on every editor, which is
        /// what makes this red-green here as well as on the 2021.3 leg.
        /// </summary>
        [Test]
        public void AWrappingContainerTooShortForItsLinesGrowsUntilEveryChildIsInsideIt()
        {
            EditorWindow window = CreateWrapProbeWindow();

            VisualElement control = AddWrapProbe(window, PinnedHeight, contentSized: false);
            VisualElement fixedProbe = AddWrapProbe(window, PinnedHeight, contentSized: true);
            EditorWindowTestUtility.SettleLayout(window);

            Assert.That(
                DxMessagingEditorTheme.MeasureWrappedContentHeight(control),
                Is.GreaterThan(
                    control.resolvedStyle.height + EditorWindowTestUtility.LayoutTolerance
                ),
                "The control probe must reproduce the defect, otherwise the fixed probe below "
                    + "proves nothing: a wrapping container that will not grow leaves its "
                    + "wrapped lines outside its own box."
            );

            Assert.That(
                DxMessagingEditorTheme.MeasureWrappedContentHeight(fixedProbe),
                Is.LessThanOrEqualTo(
                    fixedProbe.resolvedStyle.height + EditorWindowTestUtility.LayoutTolerance
                ),
                "A container built through ApplyContentSizedWrap must end up tall enough for "
                    + "every line its children wrap onto."
            );
        }

        /// <summary>
        /// An editor that already sizes a wrapping container must be left alone: no inline
        /// `min-height` is written, so the helper cannot lower a height the stylesheet declared.
        /// </summary>
        [Test]
        public void AWrappingContainerThatAlreadyFitsIsLeftWithoutAnInlineHeight()
        {
            EditorWindow window = CreateWrapProbeWindow();

            // Two children fit on one line, so no editor has to derive a wrapped height and this
            // reads the same on Unity 2021.3 as it does on 6000.x.
            VisualElement probe = AddWrapProbe(
                window,
                pinnedHeight: 0f,
                contentSized: true,
                childCount: SingleLineChildCount
            );
            EditorWindowTestUtility.SettleLayout(window);

            Assert.That(
                DxMessagingEditorTheme.MeasureWrappedContentHeight(probe),
                Is.LessThanOrEqualTo(
                    probe.resolvedStyle.height + EditorWindowTestUtility.LayoutTolerance
                ),
                "The probe must already fit, otherwise this asserts the wrong case."
            );
            Assert.That(
                probe.style.minHeight.keyword,
                Is.EqualTo(StyleKeyword.Null),
                "A container that already fits its lines must keep the height its stylesheet "
                    + "gives it, so the helper must not write an inline min-height."
            );
        }

        /// <summary>
        /// A container that leaves the panel is about to be rebuilt or reused, so it must forget
        /// the height its last content needed. Otherwise a details pane that once held a deep
        /// hierarchy trail keeps that gap for every selection after it.
        /// </summary>
        [Test]
        public void AGrownWrappingContainerForgetsItsHeightWhenItLeavesThePanel()
        {
            EditorWindow window = CreateWrapProbeWindow();

            VisualElement probe = AddWrapProbe(window, PinnedHeight, contentSized: true);
            EditorWindowTestUtility.SettleLayout(window);
            Assert.That(
                probe.resolvedStyle.height,
                Is.GreaterThan(PinnedHeight + EditorWindowTestUtility.LayoutTolerance),
                "The probe must have grown first, otherwise there is no height to forget."
            );

            probe.RemoveFromHierarchy();
            EditorWindowTestUtility.SettleLayout(window);

            Assert.That(
                probe.style.minHeight.keyword,
                Is.EqualTo(StyleKeyword.Null),
                "Leaving the panel must release the height, so the container measures its next "
                    + "content from its own stylesheet height."
            );

            while (probe.childCount > SingleLineChildCount)
            {
                probe.RemoveAt(probe.childCount - 1);
            }
            window.rootVisualElement.Add(probe);
            EditorWindowTestUtility.SettleLayout(window);

            Assert.That(
                probe.resolvedStyle.height,
                Is.EqualTo(PinnedHeight).Within(EditorWindowTestUtility.LayoutTolerance),
                "Two children need one line, so the re-added container must be one line tall."
            );
            Assert.That(
                DxMessagingEditorTheme.MeasureWrappedContentHeight(probe),
                Is.LessThanOrEqualTo(
                    probe.resolvedStyle.height + EditorWindowTestUtility.LayoutTolerance
                ),
                "The re-measured container must still contain its children."
            );
        }

        /// <summary>
        /// Drift guard for the whole class of defect in issue #440: turning wrapping on by hand,
        /// in a source file or in a stylesheet, re-creates it, because nothing then supplies the
        /// height Unity 2021.3 does not derive. Everything shipped must go through the helper.
        /// </summary>
        [Test]
        public void EveryEditorWrapContainerGoesThroughTheContentSizedWrapHelper()
        {
            const string bannedDeclaration = "style.flex" + "Wrap";
            string editorRoot = DxMessagingEditorTheme.PackageRoot + "/Editor";
            string allowedRelativePath = "Editor/DxMessagingEditorTheme.cs";

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

            List<string> offenders = new();
            foreach (string sourcePath in sourcePaths)
            {
                string normalized = sourcePath.Replace('\\', '/');
                if (normalized.EndsWith(allowedRelativePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string[] lines = System.IO.File.ReadAllLines(sourcePath);
                for (int index = 0; index < lines.Length; index++)
                {
                    if (lines[index].Contains(bannedDeclaration))
                    {
                        offenders.Add($"{normalized}:{index + 1}");
                    }
                }
            }

            // A stylesheet can turn wrapping on just as well as a source file can, and
            // nothing would then supply the height either.
            foreach (
                string ussPath in new[]
                {
                    DxMessagingEditorTheme.TokensUssPath,
                    DxMessagingEditorTheme.ThemeUssPath,
                }
            )
            {
                string[] ussLines = System.IO.File.ReadAllLines(ussPath);
                for (int index = 0; index < ussLines.Length; index++)
                {
                    if (ussLines[index].Contains("flex-" + "wrap:"))
                    {
                        offenders.Add($"{ussPath}:{index + 1}");
                    }
                }
            }

            Assert.That(
                offenders,
                Is.Empty,
                "These editor sources or stylesheets turn wrapping on directly instead of calling "
                    + "DxMessagingEditorTheme.ApplyContentSizedWrap, so on Unity 2021.3 their "
                    + "wrapped lines draw outside the container (GitHub #440):\n"
                    + string.Join("\n", offenders)
            );
        }

        private EditorWindow CreateWrapProbeWindow()
        {
            EditorWindow window = EditorWindowTestUtility.CreateWindow();
            _createdWindows.Add(window);
            window.position = new Rect(0f, 0f, ProbeWindowWidth, ProbeWindowHeight);
            EditorWindowTestUtility.ShowWindow(window);
            return window;
        }

        /// <summary>
        /// A row of equal children too wide to fit on one line. A non-zero
        /// <paramref name="pinnedHeight"/> holds the container at one line's worth of height,
        /// which is what Unity 2021.3 does to a wrapping container on its own.
        /// </summary>
        private static VisualElement AddWrapProbe(
            EditorWindow window,
            float pinnedHeight,
            bool contentSized,
            int childCount = ProbeChildCount
        )
        {
            VisualElement probe = new();
            probe.style.flexDirection = FlexDirection.Row;
            probe.style.width = ProbeWindowWidth;
            probe.style.flexShrink = 0f;
            if (contentSized)
            {
                DxMessagingEditorTheme.ApplyContentSizedWrap(probe);
            }
            else
            {
                probe.style.flexWrap = Wrap.Wrap;
            }

            if (pinnedHeight > 0f)
            {
                probe.style.height = pinnedHeight;
            }

            for (int index = 0; index < childCount; index++)
            {
                VisualElement child = new();
                child.style.width = ProbeChildWidth;
                child.style.height = ProbeChildHeight;
                child.style.flexShrink = 0f;
                probe.Add(child);
            }

            window.rootVisualElement.Add(probe);
            return probe;
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

        private static void AssertHueBetween(
            Color color,
            float minimum,
            float maximum,
            string message
        )
        {
            Color.RGBToHSV(color, out float hue, out float saturation, out float value);
            Assert.That(hue, Is.InRange(minimum, maximum), message);
            Assert.That(saturation, Is.GreaterThanOrEqualTo(0.25f), message);
            Assert.That(value, Is.GreaterThanOrEqualTo(0.5f), message);
        }

        private static float ContrastRatio(Color first, Color second)
        {
            float firstLuminance = RelativeLuminance(first);
            float secondLuminance = RelativeLuminance(second);
            float lighter = Mathf.Max(firstLuminance, secondLuminance);
            float darker = Mathf.Min(firstLuminance, secondLuminance);
            return (lighter + 0.05f) / (darker + 0.05f);
        }

        private static float RelativeLuminance(Color color)
        {
            return 0.2126f * Linearize(color.r)
                + 0.7152f * Linearize(color.g)
                + 0.0722f * Linearize(color.b);
        }

        private static float Linearize(float channel)
        {
            return channel <= 0.04045f
                ? channel / 12.92f
                : Mathf.Pow((channel + 0.055f) / 1.055f, 2.4f);
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

        private static string FindRuleBody(string uss, string selector)
        {
            foreach (string block in uss.Split('}'))
            {
                int open = block.IndexOf('{');
                if (open < 0)
                {
                    continue;
                }

                string blockSelector = block.Substring(0, open).Trim().Replace("\n", " ");
                if (string.Equals(blockSelector, selector, StringComparison.Ordinal))
                {
                    return block.Substring(open + 1);
                }
            }

            Assert.Fail($"The stylesheet declares no `{selector}` rule.");
            return null;
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
