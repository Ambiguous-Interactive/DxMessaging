#if UNITY_EDITOR && UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Editor
{
    using System.Collections.Generic;
    using System.Linq;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Editor;
    using DxMessaging.Editor.Windows;
    using NUnit.Framework;
    using UnityEditor;
    using UnityEngine;
    using UnityEngine.UIElements;

    /// <summary>
    /// Covers the live Monitor view: the design-system classes it is supposed to bring into use,
    /// the columnar row contract, the taxonomy chip and text filtering, the footer stats, and the
    /// empty states. Built on the version-portable path (build the tree, query it by name and
    /// class) so it runs across the whole shipped editor range.
    /// </summary>
    [TestFixture]
    public sealed class DxMessagingMessageMonitorLiveViewTests
    {
        private readonly List<EditorWindow> _createdWindows = new();

        /// <summary>Matches <c>DxMessagingMessageMonitorWindow</c>'s own <c>minSize</c>.</summary>
        private const float MonitorMinimumWidth = 420f;
        private const float MonitorMinimumHeight = 320f;

        /// <summary>The `.dx-col-msg` / `.dx-row__msg` floor declared by the theme stylesheet.</summary>
        private const float MessageColumnFloor = 96f;

        /// <summary>Layout resolves in floats; a sub-pixel edge is not an overlap.</summary>
        private const float LayoutTolerance = 0.5f;

        [TearDown]
        public void TearDown()
        {
            // Every interaction test here holds its host window open until teardown, and closing a
            // shown window in -nographics CI logs a benign "No graphic device is available" error.
            // Unity resets LogAssert tolerance per phase, so ShowWindow's tolerance does not reach
            // this one; re-assert it for the teardown phase (headless only, so runs with a real GPU
            // keep full strictness).
            EditorWindowTestUtility.SuppressHeadlessWindowRenderErrors();
            EditorWindowTestUtility.CloseTrackedWindows(_createdWindows);
        }

        [Test]
        public void TheViewBringsEveryColumnarDesignSystemClassIntoUse()
        {
            VisualElement root = CreateView(Recorder(Entry(1, "PlayerSpawned")));

            // These classes shipped in the theme with no C# referencing them. The live recorder is
            // the surface they were designed for, so each one must appear on the built tree; a
            // removed class here means the stylesheet has drifted back out of use.
            string[] expected =
            {
                DxMessagingEditorTheme.RecordClassName,
                DxMessagingEditorTheme.ChipClassName,
                DxMessagingEditorTheme.ChipUntargetedClassName,
                DxMessagingEditorTheme.ChipTargetedClassName,
                DxMessagingEditorTheme.ChipBroadcastClassName,
                DxMessagingEditorTheme.ChipWideClassName,
                DxMessagingEditorTheme.FilterClassName,
                DxMessagingEditorTheme.ListHeaderClassName,
                DxMessagingEditorTheme.ColumnTimeClassName,
                DxMessagingEditorTheme.ColumnTypeClassName,
                DxMessagingEditorTheme.ColumnMessageClassName,
                DxMessagingEditorTheme.ColumnRouteClassName,
                DxMessagingEditorTheme.ColumnCountClassName,
                DxMessagingEditorTheme.RowClassName,
                DxMessagingEditorTheme.RowTimeClassName,
                DxMessagingEditorTheme.RowTypeClassName,
                DxMessagingEditorTheme.RowMessageClassName,
                DxMessagingEditorTheme.RowRouteClassName,
                DxMessagingEditorTheme.RowCountClassName,
                DxMessagingEditorTheme.DotClassName,
                DxMessagingEditorTheme.DetailClassName,
                DxMessagingEditorTheme.DetailHeadClassName,
                DxMessagingEditorTheme.DetailTitleClassName,
                DxMessagingEditorTheme.DetailFrameClassName,
                DxMessagingEditorTheme.KeyValueClassName,
                DxMessagingEditorTheme.KeyValueKeyClassName,
                DxMessagingEditorTheme.KeyValueValueClassName,
                DxMessagingEditorTheme.FooterClassName,
                DxMessagingEditorTheme.FooterStatClassName,
                DxMessagingEditorTheme.FooterNumberClassName,
            };

            // The list virtualizes, so a row is only realized once it lays out; assert the row
            // classes against the row factory the list binds instead of waiting on a frame.
            HashSet<string> present = CollectClassNames(root);
            present.UnionWith(
                CollectClassNames(
                    DxMessagingMessageMonitorLiveView.CreateRow(
                        Recorded(Entry(1, "PlayerSpawned"))[0],
                        rowIndex: 0,
                        selected: false
                    )
                )
            );

            CollectionAssert.IsSubsetOf(
                expected,
                present,
                "Every columnar design-system class must be rendered by the live view."
            );
        }

        [Test]
        public void TheAlternateRowClassMarksOddRowsOnly()
        {
            MessageMonitorLiveEntry row = Recorded(Entry(1, "A"))[0];

            Assert.IsFalse(
                DxMessagingMessageMonitorLiveView
                    .CreateRow(row, rowIndex: 0, selected: false)
                    .ClassListContains(DxMessagingEditorTheme.RowAlternateClassName)
            );
            Assert.IsTrue(
                DxMessagingMessageMonitorLiveView
                    .CreateRow(row, rowIndex: 1, selected: false)
                    .ClassListContains(DxMessagingEditorTheme.RowAlternateClassName)
            );
        }

        [Test]
        public void ARowRendersEveryColumnItsHeaderPromises()
        {
            MessageMonitorLiveEntry row = Recorded(
                Entry(1, "PlayerSpawned", "Context: Player", DxMessagingEditorPalette.TargetedKind),
                Entry(2, "PlayerSpawned", "Context: Player", DxMessagingEditorPalette.TargetedKind)
            )[0];

            VisualElement element = DxMessagingMessageMonitorLiveView.CreateRow(
                row,
                rowIndex: 0,
                selected: false
            );

            Assert.AreEqual(
                "PlayerSpawned",
                Text(element, DxMessagingMessageMonitorLiveView.RowMessageLabelName)
            );
            Assert.AreEqual(
                "Context: Player",
                Text(element, DxMessagingMessageMonitorLiveView.RowContextLabelName)
            );
            Assert.AreEqual(
                DxMessagingEditorPalette.TargetedKind,
                Text(element, DxMessagingMessageMonitorLiveView.RowRouteLabelName)
            );
            Assert.AreEqual(
                "2",
                Text(element, DxMessagingMessageMonitorLiveView.RowCountLabelName),
                "A coalesced row shows how many emissions it stands for."
            );
            Assert.IsNotNull(
                element.Q<VisualElement>(className: DxMessagingEditorTheme.DotTargetedClassName),
                "The row's taxonomy dot carries the route-kind colour."
            );
        }

        [Test]
        public void ASingleEmissionRowLeavesTheCountColumnBlank()
        {
            VisualElement element = DxMessagingMessageMonitorLiveView.CreateRow(
                Recorded(Entry(1, "A"))[0],
                rowIndex: 0,
                selected: false
            );

            Assert.AreEqual(
                string.Empty,
                Text(element, DxMessagingMessageMonitorLiveView.RowCountLabelName)
            );
        }

        [Test]
        public void RowsAreListedNewestFirst()
        {
            List<MessageMonitorLiveEntry> rows = DxMessagingMessageMonitorLiveView.FilterRows(
                Recorded(Entry(1, "Oldest"), Entry(2, "Middle"), Entry(3, "Newest")),
                MessageMonitorLiveViewState.Default
            );

            CollectionAssert.AreEqual(
                new[] { "Newest", "Middle", "Oldest" },
                rows.Select(row => row.Entry.MessageTypeName).ToArray()
            );
        }

        [TestCase(
            false,
            true,
            true,
            new[] { "Targeted", "Broadcast" },
            TestName = "untargeted off"
        )]
        [TestCase(
            true,
            false,
            true,
            new[] { "Untargeted", "Broadcast" },
            TestName = "targeted off"
        )]
        [TestCase(
            true,
            true,
            false,
            new[] { "Untargeted", "Targeted" },
            TestName = "broadcast off"
        )]
        [TestCase(false, false, false, new string[0], TestName = "all off")]
        public void TaxonomyChipsHideExactlyTheirOwnRouteKind(
            bool showUntargeted,
            bool showTargeted,
            bool showBroadcast,
            string[] expectedMessageTypeNames
        )
        {
            IReadOnlyList<MessageMonitorLiveEntry> entries = Recorded(
                Entry(
                    1,
                    DxMessagingEditorPalette.UntargetedKind,
                    "Context: none",
                    DxMessagingEditorPalette.UntargetedKind
                ),
                Entry(
                    2,
                    DxMessagingEditorPalette.TargetedKind,
                    "Context: Player",
                    DxMessagingEditorPalette.TargetedKind
                ),
                Entry(
                    3,
                    DxMessagingEditorPalette.BroadcastKind,
                    "Context: HUD",
                    DxMessagingEditorPalette.BroadcastKind
                )
            );

            List<MessageMonitorLiveEntry> rows = DxMessagingMessageMonitorLiveView.FilterRows(
                entries,
                new MessageMonitorLiveViewState(
                    filterText: string.Empty,
                    showUntargeted,
                    showTargeted,
                    showBroadcast
                )
            );

            CollectionAssert.AreEquivalent(
                expectedMessageTypeNames,
                rows.Select(row => row.Entry.MessageTypeName).ToArray()
            );
        }

        [Test]
        public void AnUnrecognizedRouteKindSurvivesEveryChipBeingOff()
        {
            // Chips can only hide what they can bring back; a row they cannot represent must not
            // become permanently invisible.
            List<MessageMonitorLiveEntry> rows = DxMessagingMessageMonitorLiveView.FilterRows(
                Recorded(Entry(1, "Mystery", "Context: none", routeKind: string.Empty)),
                new MessageMonitorLiveViewState(
                    filterText: string.Empty,
                    showUntargeted: false,
                    showTargeted: false,
                    showBroadcast: false
                )
            );

            Assert.AreEqual(1, rows.Count);
        }

        [TestCase("PlayerSpawned", 1, TestName = "plain text")]
        [TestCase("type:PlayerSpawned", 1, TestName = "typed type facet")]
        [TestCase("context:HUD", 1, TestName = "typed context facet")]
        [TestCase("nothing-matches-this", 0, TestName = "no match")]
        public void TheTextFilterUsesTheSameTypedQueryAsSnapshotMode(
            string filterText,
            int expectedRowCount
        )
        {
            IReadOnlyList<MessageMonitorLiveEntry> entries = Recorded(
                Entry(1, "PlayerSpawned", "Context: HUD"),
                Entry(2, "EnemyKilled", "Context: Player")
            );

            List<MessageMonitorLiveEntry> rows = DxMessagingMessageMonitorLiveView.FilterRows(
                entries,
                new MessageMonitorLiveViewState(filterText)
            );

            Assert.AreEqual(expectedRowCount, rows.Count);
        }

        [Test]
        public void TheDetailPaneDescribesTheSelectedRow()
        {
            VisualElement root = CreateView(
                Recorder(
                    Entry(1, "PlayerSpawned", "Context: Player"),
                    Entry(2, "EnemyKilled", "Context: HUD")
                ),
                new MessageMonitorLiveViewState(selectedTraceId: 1)
            );

            VisualElement detail = root.Q<VisualElement>(
                DxMessagingMessageMonitorLiveView.DetailName
            );

            Assert.IsNotNull(detail);
            Assert.AreEqual(
                "PlayerSpawned",
                Text(detail, DxMessagingMessageMonitorLiveView.DetailTitleLabelName),
                "Index 1 of a newest-first list is the older row."
            );
            Assert.AreEqual(
                "#1",
                Text(detail, DxMessagingMessageMonitorLiveView.DetailFrameLabelName)
            );
            Assert.AreEqual(
                5,
                detail
                    .Query<VisualElement>(className: DxMessagingEditorTheme.KeyValueClassName)
                    .ToList()
                    .Count,
                "The detail card lists type, context, count, dispatch range and observation time."
            );
        }

        [Test]
        public void ACoalescedRowReportsItsWholeDispatchRange()
        {
            MessageMonitorLiveEntry row = Recorded(Entry(1, "A"), Entry(2, "A"), Entry(3, "A"))[0];

            Assert.AreEqual("#1-#3", DxMessagingMessageMonitorLiveView.CreateTraceRangeText(row));
        }

        [Test]
        public void TheDetailPaneCarriesACompleteBorder()
        {
            VisualElement detail = CreateView(Recorder(Entry(1, "A")))
                .Q<VisualElement>(DxMessagingMessageMonitorLiveView.DetailName);

            Assert.AreEqual(1, detail.style.borderTopWidth.value);
            Assert.AreEqual(1, detail.style.borderRightWidth.value);
            Assert.AreEqual(1, detail.style.borderBottomWidth.value);
            Assert.AreEqual(1, detail.style.borderLeftWidth.value);
        }

        [Test]
        public void TheFooterReportsShownBufferedRecordedAndMissedCounts()
        {
            MessageMonitorLiveRecorder recorder = new(capacity: 8, clock: () => 0);
            recorder.Ingest(new[] { Entry(1, "A"), Entry(2, "B") });
            // A gap in the dispatch sequence is data the bus ring overwrote before the drain.
            recorder.Ingest(new[] { Entry(6, "C") });

            IReadOnlyList<MessageMonitorLiveFooterStat> stats =
                DxMessagingMessageMonitorLiveView.CreateFooterStats(recorder, shownCount: 2);

            CollectionAssert.AreEqual(
                new[] { "shown", "buffered", "recorded", "missed" },
                stats.Select(stat => stat.Label).ToArray()
            );
            CollectionAssert.AreEqual(
                new[] { "2", "3/8", "3", "3" },
                stats.Select(stat => stat.Number).ToArray()
            );
        }

        [TestCase(true, true, 0, "Waiting for messages", TestName = "recording, nothing yet")]
        [TestCase(true, false, 0, "Recording is paused", TestName = "paused, nothing yet")]
        [TestCase(false, true, 0, "Diagnostics are Off", TestName = "diagnostics off")]
        [TestCase(true, true, 1, "No matches", TestName = "filtered everything out")]
        public void TheEmptyStateNamesTheActualReasonThereIsNothingToShow(
            bool diagnosticsEnabled,
            bool recording,
            int recordedCount,
            string expectedTitle
        )
        {
            MessageMonitorLiveRecorder recorder = new(clock: () => 0);
            for (int index = 0; index < recordedCount; index++)
            {
                recorder.Ingest(new[] { Entry(index + 1, "A" + index) });
            }
            recorder.Recording = recording;

            Assert.AreEqual(
                expectedTitle,
                DxMessagingMessageMonitorLiveView.CreateEmptyTitleText(recorder, diagnosticsEnabled)
            );
        }

        [Test]
        public void AnEmptyLogRendersTheThemedEmptyStateAndNoList()
        {
            VisualElement root = CreateView(Recorder());

            Assert.IsNull(root.Q<ListView>(DxMessagingMessageMonitorLiveView.ListName));
            Assert.IsNotNull(root.Q<Label>(DxMessagingMessageMonitorLiveView.EmptyTitleName));
            Assert.IsNotNull(root.Q<Label>(DxMessagingMessageMonitorLiveView.EmptyBodyName));
            Assert.IsNotNull(
                root.Q<VisualElement>(DxMessagingMessageMonitorLiveView.ListHeaderName),
                "The column header stays so the empty log still reads as a log."
            );
            Assert.IsNotNull(
                root.Q<VisualElement>(DxMessagingMessageMonitorLiveView.FooterName),
                "The footer stays so a paused or lossy recorder can still be diagnosed."
            );
        }

        [Test]
        public void TheListVirtualizesAtTheStylesheetRowHeight()
        {
            ListView list = CreateView(Recorder(Entry(1, "A"), Entry(2, "B")))
                .Q<ListView>(DxMessagingMessageMonitorLiveView.ListName);

            Assert.IsNotNull(list);
            Assert.AreEqual(2, list.itemsSource.Count);
            Assert.AreEqual(DxMessagingMessageMonitorLiveView.RowHeight, list.fixedItemHeight);
            Assert.AreEqual(CollectionVirtualizationMethod.FixedHeight, list.virtualizationMethod);
        }

        [Test]
        public void TheRecordToggleReflectsAndDrivesTheRecorder()
        {
            MessageMonitorLiveRecorder recorder = new(clock: () => 0);
            recorder.Recording = false;
            bool? requested = null;

            VisualElement root = CreateView(
                recorder,
                MessageMonitorLiveViewState.Default,
                new MessageMonitorLiveViewCallbacks
                {
                    OnRecordingChanged = recording => requested = recording,
                }
            );

            Toggle record = root.Q<Toggle>(DxMessagingMessageMonitorLiveView.RecordToggleName);
            Assert.IsNotNull(record);
            Assert.IsFalse(record.value, "The toggle shows the recorder's real state.");

            record.value = true;

            Assert.AreEqual(true, requested);
        }

        [Test]
        public void ChangingAChipKeepsTheFilterTextAndTheOtherChips()
        {
            MessageMonitorLiveViewState? next = null;
            VisualElement root = CreateView(
                Recorder(Entry(1, "A")),
                new MessageMonitorLiveViewState(
                    "PlayerSpawned",
                    showUntargeted: true,
                    showTargeted: false,
                    showBroadcast: true
                ),
                new MessageMonitorLiveViewCallbacks { OnStateChanged = state => next = state }
            );

            root.Q<Toggle>(DxMessagingMessageMonitorLiveView.BroadcastChipName).value = false;

            Assert.IsTrue(next.HasValue);
            Assert.AreEqual("PlayerSpawned", next.Value.FilterText);
            Assert.IsTrue(next.Value.ShowUntargeted);
            Assert.IsFalse(next.Value.ShowTargeted, "An untouched chip keeps its state.");
            Assert.IsFalse(next.Value.ShowBroadcast);
        }

        [Test]
        public void TheLiveSurfaceNamesItsModeAndItsTaxonomyChips()
        {
            VisualElement root = CreateView(Recorder(Entry(1, "A")));

            Label badge = root.Q<Label>(DxMessagingMessageMonitorLiveView.ModeBadgeLabelName);
            Assert.IsNotNull(badge);
            Assert.AreEqual(DxMessagingMessageMonitorLiveView.LiveModeBadgeText, badge.text);
            Assert.AreEqual(DxMessagingMessageMonitorLiveView.LiveModeHintText, badge.tooltip);
            Assert.AreEqual(
                DxMessagingMessageMonitorLiveView.LiveModeHintText,
                Text(root, DxMessagingMessageMonitorLiveView.ModeHintLabelName),
                "The footer says what a row stands for, so the merged N column is not a mystery."
            );

            foreach (
                (string chipName, string routeKind) in new[]
                {
                    (
                        DxMessagingMessageMonitorLiveView.UntargetedChipName,
                        DxMessagingEditorPalette.UntargetedKind
                    ),
                    (
                        DxMessagingMessageMonitorLiveView.TargetedChipName,
                        DxMessagingEditorPalette.TargetedKind
                    ),
                    (
                        DxMessagingMessageMonitorLiveView.BroadcastChipName,
                        DxMessagingEditorPalette.BroadcastKind
                    ),
                }
            )
            {
                Toggle chip = root.Q<Toggle>(chipName);
                Assert.IsNotNull(chip, chipName);
                Assert.AreEqual(routeKind, chip.text, chipName);
                Assert.IsTrue(
                    chip.ClassListContains(DxMessagingEditorTheme.ChipWideClassName),
                    chipName
                );
                StringAssert.Contains(routeKind, chip.tooltip);
            }
        }

        [Test]
        public void TheLiveDetailPaneStartsWithItsStackTraceCollapsed()
        {
            VisualElement root = CreateView(
                Recorder(Entry(1, "A", stackTrace: CapturedStackTrace))
            );

            Foldout stack = root.Q<Foldout>(
                DxMessagingMessageMonitorLiveView.DetailStackFoldoutName
            );
            Assert.IsNotNull(stack);
            Assert.IsFalse(stack.value, "The stack trace disclosure must start collapsed.");
            StringAssert.Contains(
                "EmitOneOfEach",
                stack
                    .Q<Label>(DxMessagingMessageMonitorLiveView.DetailStackFirstFrameLabelName)
                    .text
            );
            Assert.That(
                stack.Query<Label>().ToList().ConvertAll(label => label.text),
                Has.None.Contains("ExtractStackTrace"),
                "Unity's own stack-capture frames are noise and must not be rendered."
            );
        }

        /// <summary>
        /// The shape <see cref="MessageEmissionData"/> actually captures: Unity's two
        /// stack-capture frames on top, then the emitting code. See the matching constant in
        /// the snapshot fixture.
        /// </summary>
        private const string CapturedStackTrace =
            "UnityEngine.Debug:ExtractStackTraceNoAlloc (byte*,int,string)\n"
            + "UnityEngine.StackTraceUtility:ExtractStackTrace ()\n"
            + "WallstopStudios.Sample.Exerciser:EmitOneOfEach () (at Assets/Sample/Exerciser.cs:185)";

        [Test]
        public void TypingInTheFilterResetsTheSelectionToTheNewestRow()
        {
            MessageMonitorLiveViewState? next = null;
            VisualElement root = CreateView(
                Recorder(Entry(1, "A"), Entry(2, "B")),
                new MessageMonitorLiveViewState(selectedTraceId: 1),
                new MessageMonitorLiveViewCallbacks { OnStateChanged = state => next = state }
            );

            root.Q<TextField>(DxMessagingMessageMonitorLiveView.FilterFieldName).value = "B";

            Assert.IsTrue(next.HasValue);
            Assert.AreEqual("B", next.Value.FilterText);
            Assert.AreEqual(
                MessageMonitorLiveViewState.FollowNewest,
                next.Value.SelectedTraceId,
                "A new filter is a new row set, so the pin is dropped."
            );
        }

        [Test]
        public void APinnedRowKeepsItsDetailPaneAsNewerRowsArrive()
        {
            // The log is newest-first, so every appended row shifts every position. A selection
            // stored as a position would silently repoint the detail pane at a different emission
            // while recording continues.
            MessageMonitorLiveRecorder recorder = Recorder(Entry(1, "Pinned"), Entry(2, "Other"));
            MessageMonitorLiveViewState pinned = new(selectedTraceId: 1);

            Assert.AreEqual(
                1,
                DxMessagingMessageMonitorLiveView.ResolveSelectedIndex(
                    DxMessagingMessageMonitorLiveView.FilterRows(recorder.Entries, pinned),
                    pinned.SelectedTraceId
                )
            );

            recorder.Ingest(new[] { Entry(3, "Newer"), Entry(4, "Newest") });

            List<MessageMonitorLiveEntry> rows = DxMessagingMessageMonitorLiveView.FilterRows(
                recorder.Entries,
                pinned
            );
            int index = DxMessagingMessageMonitorLiveView.ResolveSelectedIndex(
                rows,
                pinned.SelectedTraceId
            );

            Assert.AreEqual(3, index, "The pinned row moved down as newer rows arrived.");
            Assert.AreEqual("Pinned", rows[index].Entry.MessageTypeName);
        }

        [Test]
        public void APinnedRowThatIsFilteredAwayFallsBackWithoutLosingThePin()
        {
            MessageMonitorLiveRecorder recorder = Recorder(
                Entry(1, "PlayerSpawned"),
                Entry(2, "EnemyKilled")
            );
            MessageMonitorLiveViewState pinned = new("EnemyKilled", selectedTraceId: 1);

            List<MessageMonitorLiveEntry> rows = DxMessagingMessageMonitorLiveView.FilterRows(
                recorder.Entries,
                pinned
            );

            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual(
                0,
                DxMessagingMessageMonitorLiveView.ResolveSelectedIndex(
                    rows,
                    pinned.SelectedTraceId
                ),
                "The pinned row is filtered out, so the detail pane shows the newest match."
            );
            Assert.AreEqual(
                1,
                pinned.SelectedTraceId,
                "The pin itself is untouched, so clearing the filter restores it."
            );
        }

        [Test]
        public void TheClearAndSnapshotButtonsRaiseTheirCallbacks()
        {
            bool cleared = false;
            bool exited = false;
            VisualElement root = CreateView(
                Recorder(Entry(1, "A")),
                MessageMonitorLiveViewState.Default,
                new MessageMonitorLiveViewCallbacks
                {
                    OnClear = () => cleared = true,
                    OnExitLiveMode = () => exited = true,
                }
            );

            SendClick(root.Q<Button>(DxMessagingMessageMonitorLiveView.ClearButtonName));
            SendClick(root.Q<Button>(DxMessagingMessageMonitorLiveView.SnapshotButtonName));

            Assert.IsTrue(cleared);
            Assert.IsTrue(exited);
        }

        /// <summary>
        /// The half of #344's "we can't go Un-Live once live" that lives on this side: the LIVE
        /// badge is itself the way back, so a reader who learned the switch in snapshot mode
        /// finds it in the same place here.
        /// </summary>
        [Test]
        public void TheLiveBadgeSwitchesBackToSnapshot()
        {
            int exited = 0;
            VisualElement root = CreateView(
                Recorder(Entry(1, "A")),
                MessageMonitorLiveViewState.Default,
                new MessageMonitorLiveViewCallbacks { OnExitLiveMode = () => exited++ }
            );

            Label badge = root.Q<Label>(DxMessagingMessageMonitorLiveView.ModeBadgeLabelName);
            Assert.IsNotNull(badge);
            Assert.IsTrue(
                badge.ClassListContains(DxMessagingEditorTheme.ClickableClassName),
                "The word that names the mode must say it can be clicked."
            );
            Assert.IsTrue(badge.focusable, "A keyboard must reach whatever a mouse reaches.");

            SendClick(badge);

            Assert.AreEqual(1, exited, "Clicking the LIVE badge returns to the snapshot Monitor.");
        }

        /// <summary>
        /// The explicit button must sit beside the badge, not at the end of a wrapping toolbar
        /// where #344 could not find it.
        /// </summary>
        [Test]
        public void TheSnapshotButtonSitsBesideTheModeBadge()
        {
            VisualElement root = CreateView(Recorder(Entry(1, "A")));

            VisualElement toolbar = root.Q<VisualElement>(
                DxMessagingMessageMonitorLiveView.ToolbarName
            );
            Assert.IsNotNull(toolbar);
            List<VisualElement> children = toolbar.Children().ToList();
            int badgeIndex = children.FindIndex(child =>
                child.name == DxMessagingMessageMonitorLiveView.ModeBadgeLabelName
            );
            int buttonIndex = children.FindIndex(child =>
                child.name == DxMessagingMessageMonitorLiveView.SnapshotButtonName
            );
            Assert.Greater(badgeIndex, -1);
            Assert.Greater(buttonIndex, -1);
            Assert.AreEqual(
                badgeIndex + 1,
                buttonIndex,
                "The control that leaves live mode must not be the last thing a wrapping "
                    + "toolbar pushes onto a second row."
            );
        }

        [Test]
        public void RenderingTheBodyLeavesTheToolbarAndItsFilterFieldAlone()
        {
            VisualElement root = CreateView(Recorder(Entry(1, "A")));
            TextField filter = root.Q<TextField>(DxMessagingMessageMonitorLiveView.FilterFieldName);

            DxMessagingMessageMonitorLiveView.RenderBody(
                root.Q<VisualElement>(DxMessagingMessageMonitorLiveView.BodyName),
                new MessageMonitorLiveRecorder(clock: () => 0),
                MessageMonitorLiveViewState.Default,
                diagnosticsEnabled: true
            );

            Assert.AreSame(
                filter,
                root.Q<TextField>(DxMessagingMessageMonitorLiveView.FilterFieldName),
                "Re-rendering the body must not replace the field the user is typing into."
            );
        }

        /// <summary>
        /// Four groups - what the log is doing, what the window does next, what is shown, what is
        /// searched - across two rows. The row boundary divides the pairs, so one rule inside each
        /// row divides the two groups that share it.
        /// </summary>
        [TestCase(DxMessagingMessageMonitorLiveView.ToolbarName)]
        [TestCase(DxMessagingMessageMonitorLiveView.FilterRowName)]
        public void EachToolbarRowSeparatesTheTwoControlGroupsThatShareIt(string rowName)
        {
            VisualElement root = CreateView(Recorder(Entry(1, "A")));

            Assert.AreEqual(
                1,
                root.Q<VisualElement>(rowName)
                    .Query<VisualElement>(className: DxMessagingEditorTheme.SeparatorClassName)
                    .ToList()
                    .Count,
                $"`{rowName}` carries two control groups, so one rule divides them."
            );
        }

        [Test]
        public void ALossyLogSaysSoInsteadOfLeavingItToTheMissedStat()
        {
            MessageMonitorLiveRecorder recorder = Recorder(Entry(1, "A"));
            VisualElement root = CreateView(recorder);

            Assert.IsNull(
                root.Q<VisualElement>(DxMessagingMessageMonitorLiveView.GapNoticeName),
                "A complete log says nothing."
            );

            // Skipping a dispatch id is exactly what the bus overwriting records looks like to the
            // recorder, and it is the one condition that makes every count on screen wrong.
            recorder.Ingest(new[] { Entry(9, "B") });
            RenderBody(root.Q<VisualElement>(DxMessagingMessageMonitorLiveView.BodyName), recorder);

            VisualElement notice = root.Q<VisualElement>(
                DxMessagingMessageMonitorLiveView.GapNoticeName
            );
            Assert.IsNotNull(notice);
            Assert.IsTrue(
                notice.ClassListContains(DxMessagingEditorTheme.DangerClassName),
                "A hole in the log is a danger, not a caution."
            );
            Assert.That(
                notice.style.borderTopColor.value,
                Is.EqualTo(DxMessagingEditorPalette.Danger),
                "Problem notices must use semantic danger red, not a message taxonomy color."
            );
            Assert.That(
                notice.style.borderRightColor.value,
                Is.EqualTo(DxMessagingEditorPalette.Danger)
            );
            Assert.That(
                notice.style.borderBottomColor.value,
                Is.EqualTo(DxMessagingEditorPalette.Danger)
            );
            Assert.That(
                notice.style.borderLeftColor.value,
                Is.EqualTo(DxMessagingEditorPalette.Danger)
            );
            Assert.IsTrue(
                root.Q<Label>(DxMessagingMessageMonitorLiveView.GapNoticeTitleName)
                    .ClassListContains(DxMessagingEditorTheme.AdmonitionTitleClassName)
            );
            Assert.AreEqual(7, recorder.MissedCount);

            Label body = notice.Q<Label>(DxMessagingMessageMonitorLiveView.GapNoticeBodyName);
            Assert.IsTrue(
                body.text.Contains("7"),
                "The notice names how many emissions were lost."
            );
            Assert.IsFalse(
                body.ClassListContains(DxMessagingEditorTheme.EmptyBodyClassName),
                "The empty-state body caps width at 260px and centers; a full-width notice wraps."
            );
            Assert.AreEqual(WhiteSpace.Normal, body.style.whiteSpace.value);
        }

        [TestCase(1L, "1 emission before")]
        [TestCase(2L, "2 emissions before")]
        public void TheGapNoticeCountsInWholeEmissions(long missedCount, string expected)
        {
            Assert.That(
                DxMessagingMessageMonitorLiveView.CreateGapNoticeBodyText(missedCount),
                Does.Contain(expected)
            );
        }

        [Test]
        public void APollThatAddsRowsRefreshesTheListInsteadOfRebuildingIt()
        {
            MessageMonitorLiveRecorder recorder = Recorder(Entry(1, "First"));
            VisualElement root = CreateView(recorder);
            VisualElement body = root.Q<VisualElement>(DxMessagingMessageMonitorLiveView.BodyName);
            ListView list = root.Q<ListView>(DxMessagingMessageMonitorLiveView.ListName);
            Assert.IsNotNull(list);

            recorder.Ingest(new[] { Entry(2, "Second") });
            RenderBody(body, recorder);

            // A rebuilt list starts scrolled to the top, so keeping the instance is what keeps a
            // reader who had scrolled into older rows where they were (issue #303).
            Assert.AreSame(
                list,
                root.Q<ListView>(DxMessagingMessageMonitorLiveView.ListName),
                "A poll must refresh the list, not replace it."
            );
            Assert.AreEqual(2, list.itemsSource.Count, "The refreshed list shows the new row.");
        }

        [Test]
        public void ARefreshedListBindsTheNewRowsAndReportsToTheCurrentHost()
        {
            MessageMonitorLiveRecorder recorder = Recorder(Entry(1, "First"));
            VisualElement root = CreateView(recorder);
            VisualElement body = root.Q<VisualElement>(DxMessagingMessageMonitorLiveView.BodyName);
            MessageMonitorLiveViewState? observed = null;

            recorder.Ingest(new[] { Entry(2, "Second") });
            RenderBody(
                body,
                recorder,
                callbacks: new MessageMonitorLiveViewCallbacks
                {
                    OnStateChanged = state => observed = state,
                }
            );

            // The row binding is driven directly rather than waiting for the virtualized list to
            // realize a row. What it must prove is that the kept list binds against the current row
            // set and the current callbacks instead of the ones it was first built with.
            ListView list = root.Q<ListView>(DxMessagingMessageMonitorLiveView.ListName);
            VisualElement bound = new();
            root.Add(bound);
            list.bindItem(bound, 0);

            Assert.AreEqual(
                "Second",
                Text(bound, DxMessagingMessageMonitorLiveView.RowMessageLabelName),
                "The log is newest first, so index 0 is the row the last poll added."
            );

            SendClick(bound[0]);

            Assert.IsTrue(observed.HasValue, "A row click reaches the host that rendered last.");
            Assert.AreEqual(2, observed.Value.SelectedTraceId);
        }

        [Test]
        public void APollThatDoesNotChangeTheSelectedRowKeepsTheDetailPane()
        {
            MessageMonitorLiveRecorder recorder = Recorder(Entry(1, "PlayerSpawned"));
            VisualElement root = CreateView(recorder);
            VisualElement body = root.Q<VisualElement>(DxMessagingMessageMonitorLiveView.BodyName);
            VisualElement detail = root.Q<VisualElement>(
                DxMessagingMessageMonitorLiveView.DetailName
            );
            Assert.IsNotNull(detail, "The selected live row must render its details pane.");

            RenderBody(body, recorder);

            // The pane carries a scrollable stack trace, so rebuilding it for an unchanged row would
            // scroll a reader back to the top of that trace on every poll.
            Assert.AreSame(
                detail,
                root.Q<VisualElement>(DxMessagingMessageMonitorLiveView.DetailName)
            );

            recorder.Ingest(new[] { Entry(2, "EnemyDied") });
            RenderBody(body, recorder);

            Assert.AreNotSame(
                detail,
                root.Q<VisualElement>(DxMessagingMessageMonitorLiveView.DetailName),
                "A different selected row is a different pane."
            );
            Assert.AreEqual(
                "EnemyDied",
                Text(
                    root.Q<VisualElement>(DxMessagingMessageMonitorLiveView.DetailName),
                    DxMessagingMessageMonitorLiveView.DetailTitleLabelName
                )
            );
        }

        [Test]
        public void CoalescingIntoThePinnedRowRefreshesItsDetailPane()
        {
            MessageMonitorEntry repeated = Entry(1, "Tick");
            MessageMonitorLiveRecorder recorder = Recorder(repeated);
            VisualElement root = CreateView(recorder);
            VisualElement body = root.Q<VisualElement>(DxMessagingMessageMonitorLiveView.BodyName);

            // Folding does not add a row, so the pane's row identity is unchanged while the count it
            // renders is not. Keying the reuse on the dispatch range and the count is what keeps the
            // pane honest here.
            recorder.Ingest(new[] { Entry(2, "Tick"), Entry(3, "Tick") });
            RenderBody(body, recorder);

            VisualElement detail = root.Q<VisualElement>(
                DxMessagingMessageMonitorLiveView.DetailName
            );
            Assert.AreEqual(
                1,
                root.Q<ListView>(DxMessagingMessageMonitorLiveView.ListName).itemsSource.Count
            );
            Assert.AreEqual(
                "#1-#3",
                Text(detail, DxMessagingMessageMonitorLiveView.DetailFrameLabelName)
            );
        }

        [Test]
        public void AnEmptyLogDropsTheListAndTheNextRowBringsItBack()
        {
            MessageMonitorLiveRecorder recorder = Recorder(Entry(1, "First"));
            VisualElement root = CreateView(recorder);
            VisualElement body = root.Q<VisualElement>(DxMessagingMessageMonitorLiveView.BodyName);

            recorder.Clear();
            RenderBody(body, recorder);

            Assert.IsNull(
                root.Q<ListView>(DxMessagingMessageMonitorLiveView.ListName),
                "Nothing to show means no list, so an empty log cannot report stale rows."
            );
            Assert.IsNull(root.Q<VisualElement>(DxMessagingMessageMonitorLiveView.DetailName));
            Assert.IsNotNull(root.Q<Label>(DxMessagingMessageMonitorLiveView.EmptyTitleName));

            recorder.Ingest(new[] { Entry(2, "Second") });
            RenderBody(body, recorder);

            ListView rebuilt = root.Q<ListView>(DxMessagingMessageMonitorLiveView.ListName);
            Assert.IsNotNull(rebuilt);
            Assert.AreEqual(1, rebuilt.itemsSource.Count);
            Assert.IsNull(root.Q<Label>(DxMessagingMessageMonitorLiveView.EmptyTitleName));
        }

        [Test]
        public void ASelectionThatIsNoLongerInTheLogFallsBackToTheNewestRow()
        {
            VisualElement root = CreateView(
                Recorder(Entry(1, "Older"), Entry(2, "Newer")),
                new MessageMonitorLiveViewState(selectedTraceId: 999)
            );

            // A pinned row can age out of the bounded log or be filtered away. Falling back to the
            // newest row keeps the detail pane on something real.
            Assert.AreEqual(
                "Newer",
                Text(
                    root.Q<VisualElement>(DxMessagingMessageMonitorLiveView.DetailName),
                    DxMessagingMessageMonitorLiveView.DetailTitleLabelName
                )
            );
        }

        [Test]
        public void TheSnapshotMonitorOffersALiveButtonThatSwitchesModes()
        {
            EditorWindow window = CreateTrackedEditorWindow();
            bool entered = false;

            DxMessagingMessageMonitorWindow.BuildMonitorUi(
                window.rootVisualElement,
                new MessageMonitorSnapshot(
                    diagnosticsEnabled: true,
                    capacity: 100,
                    entries: new[] { Entry(1, "PlayerSpawned") }
                ),
                MessageMonitorViewState.Default,
                onEnterLiveMode: () => entered = true
            );

            Button live = window.rootVisualElement.Q<Button>(
                DxMessagingMessageMonitorWindow.LiveButtonName
            );
            Assert.IsNotNull(live);
            Assert.IsTrue(live.enabledSelf);

            SendClick(live);

            Assert.IsTrue(entered);
        }

        [Test]
        public void LiveModeUsesTheSharedLogDetailsDividerAndRememberedHeight()
        {
            const float RememberedHeight = 240f;
            float resizedHeight = 0f;
            EditorWindow window = CreateTrackedEditorWindow();
            window.position = new Rect(0f, 0f, 600f, 700f);
            window.rootVisualElement.style.width = 600f;
            window.rootVisualElement.style.height = 700f;
            VisualElement root = DxMessagingMessageMonitorLiveView.Create(
                Recorder(
                    Enumerable
                        .Range(1, 20)
                        .Select(index => Entry(index, $"PlayerSpawned{index}"))
                        .ToArray()
                ),
                MessageMonitorLiveViewState.Default,
                diagnosticsEnabled: true,
                callbacks: new MessageMonitorLiveViewCallbacks
                {
                    InitialDetailsPaneHeight = RememberedHeight,
                    OnDetailsPaneHeightChanged = height => resizedHeight = height,
                }
            );
            window.rootVisualElement.Add(root);

            VisualElement divider = root.Q<VisualElement>(
                DxMessagingMessageMonitorWindow.DetailsPaneResizerName
            );
            Assert.IsNotNull(divider, "Live mode must use the same log/details divider.");
            VisualElement detail = root.Q<VisualElement>(
                DxMessagingMessageMonitorLiveView.DetailName
            );
            Assert.IsNotNull(detail, "The selected live row must render its details pane.");
            Assert.AreEqual(
                RememberedHeight,
                detail.parent.style.height.value.value,
                "The height remembered by the window must carry across the mode switch."
            );
            Assert.IsNull(
                root.Q<VisualElement>("dxmessaging-monitor-details-stack-resizer"),
                "Live mode must not restore the nested stack-trace resizer."
            );

            EditorSurfaceCapture.InvokeInheritedPanelMethod(
                root.panel,
                "ValidateLayout",
                System.Array.Empty<object>()
            );
            // The first pass realizes the virtualized rows and changes the list's content basis;
            // the second measures the settled split that the reader sees.
            EditorSurfaceCapture.InvokeInheritedPanelMethod(
                root.panel,
                "ValidateLayout",
                System.Array.Empty<object>()
            );
            float initialDetailsHeight = detail.parent.resolvedStyle.height;
            ListView list = root.Q<ListView>();
            Assert.IsNotNull(list, "A populated live view must render its scrolling log.");
            float initialListHeight = list.resolvedStyle.height;

            DragResizeHandle(divider, deltaY: -80f);
            EditorSurfaceCapture.InvokeInheritedPanelMethod(
                root.panel,
                "ValidateLayout",
                System.Array.Empty<object>()
            );
            EditorSurfaceCapture.InvokeInheritedPanelMethod(
                root.panel,
                "ValidateLayout",
                System.Array.Empty<object>()
            );

            Assert.Greater(
                detail.parent.resolvedStyle.height,
                initialDetailsHeight,
                "Dragging the live divider upward must grow the visible details pane."
            );
            Assert.Less(
                list.resolvedStyle.height,
                initialListHeight,
                "The live log must give the space moved into the details pane."
            );
            Assert.Greater(
                resizedHeight,
                RememberedHeight,
                "A real live-mode drag must report its new height to the host window."
            );
        }

        [Test]
        public void LiveModeKeepsWrappedStackFramesInsideItsScrollingDetailsPane()
        {
            string longFrame =
                "WallstopStudios.Sample.Deep.Namespace.ExerciserWithALongTypeName:"
                + "EmitOneOfEachWithALongMethodName (at Packages/"
                + "com.wallstop-studios.dxmessaging/Editor/Windows/"
                + "DxMessagingMessageMonitorWindow.cs:185)";
            EditorWindow window = CreateTrackedEditorWindow();
            window.position = new Rect(0f, 0f, 420f, 380f);
            window.rootVisualElement.style.width = 420f;
            window.rootVisualElement.style.height = 380f;
            VisualElement root = DxMessagingMessageMonitorLiveView.Create(
                Recorder(
                    Entry(
                        1,
                        "PlayerSpawned",
                        stackTrace: longFrame + "\n" + longFrame.Replace(":185)", ":138)")
                    )
                ),
                MessageMonitorLiveViewState.Default,
                diagnosticsEnabled: true
            );
            window.rootVisualElement.Add(root);

            VisualElement detail = root.Q<VisualElement>(
                DxMessagingMessageMonitorLiveView.DetailName
            );
            Assert.IsNotNull(detail, "The live selection must render its details pane.");
            ScrollView detailScroll = detail.Q<ScrollView>();
            Assert.IsNotNull(
                detailScroll,
                "The whole live details body must scroll, including an expanded stack trace."
            );
            Foldout stack = root.Q<Foldout>(
                DxMessagingMessageMonitorLiveView.DetailStackFoldoutName
            );
            Assert.IsNotNull(stack, "A captured live trace must expose its stack disclosure.");
            stack.value = true;
            EditorSurfaceCapture.InvokeInheritedPanelMethod(
                root.panel,
                "ValidateLayout",
                System.Array.Empty<object>()
            );

            List<VisualElement> rows = stack
                .Query<VisualElement>(
                    className: DxMessagingMessageMonitorWindow.DetailsStackFrameRowClassName
                )
                .ToList();
            Assert.AreEqual(2, rows.Count, "Both live caller frames must remain visible.");
            foreach (VisualElement row in rows)
            {
                Label label = row.Q<Label>();
                Assert.IsNotNull(label, "Every live stack row must render its frame text.");
                Assert.AreEqual(
                    WhiteSpace.Normal,
                    label.resolvedStyle.whiteSpace,
                    "A long live frame must keep wrapping enabled."
                );
                Assert.Greater(
                    label.worldBound.height,
                    label.resolvedStyle.fontSize * 1.5f,
                    "The deliberately long live frame must wrap onto multiple lines."
                );
                Assert.GreaterOrEqual(
                    row.worldBound.height,
                    label.worldBound.height - 0.5f,
                    "The live frame row must contain the full wrapped label."
                );
                Button open = row.Q<Button>();
                Assert.IsNotNull(
                    open,
                    "A real package source path must render its live Open link in the regression."
                );
                Assert.LessOrEqual(
                    open.resolvedStyle.height,
                    18.5f,
                    "A toolbar-height live Open button must not add blank lines between frames."
                );
            }

            float gap = rows[1].worldBound.yMin - rows[0].worldBound.yMax;
            Assert.That(
                gap,
                Is.InRange(0f, 4f),
                $"Live stack rows should read continuously; measured gap was {gap}px."
            );
        }

        [Test]
        public void LiveModeConstrainsRememberedTallDetailsInsideTheMinimumWindow()
        {
            EditorWindow window = CreateTrackedEditorWindow();
            window.position = new Rect(0f, 0f, 420f, 320f);
            window.rootVisualElement.style.width = 420f;
            window.rootVisualElement.style.height = 320f;
            float resizedHeight = 0f;
            VisualElement root = DxMessagingMessageMonitorLiveView.Create(
                Recorder(Entry(1, "PlayerSpawned")),
                MessageMonitorLiveViewState.Default,
                diagnosticsEnabled: true,
                new MessageMonitorLiveViewCallbacks
                {
                    InitialDetailsPaneHeight =
                        DxMessagingMessageMonitorWindow.DetailsPaneResizeMaxHeight,
                    OnDetailsPaneHeightChanged = height => resizedHeight = height,
                }
            );
            window.rootVisualElement.Add(root);
            EditorSurfaceCapture.InvokeInheritedPanelMethod(
                root.panel,
                "ValidateLayout",
                System.Array.Empty<object>()
            );
            EditorSurfaceCapture.InvokeInheritedPanelMethod(
                root.panel,
                "ValidateLayout",
                System.Array.Empty<object>()
            );

            VisualElement detail = root.Q<VisualElement>(
                DxMessagingMessageMonitorLiveView.DetailName
            );
            VisualElement divider = root.Q<VisualElement>(
                DxMessagingMessageMonitorWindow.DetailsPaneResizerName
            );
            VisualElement header = root.Q<VisualElement>(
                DxMessagingMessageMonitorLiveView.ListHeaderName
            );
            ListView list = root.Q<ListView>();
            VisualElement footer = root.Q<VisualElement>(
                DxMessagingMessageMonitorLiveView.FooterName
            );
            Assert.IsNotNull(detail, "The minimum live window must retain selected details.");
            Assert.IsNotNull(divider, "The minimum live window must retain its divider.");
            Assert.IsNotNull(header, "The minimum live window must retain its list header.");
            Assert.IsNotNull(list, "The minimum live window must retain its scrolling log.");
            Assert.IsNotNull(footer, "The minimum live window must retain its footer.");

            foreach (VisualElement element in new[] { header, list, divider, detail, footer })
            {
                Assert.GreaterOrEqual(
                    element.worldBound.yMin,
                    root.worldBound.yMin - 0.5f,
                    $"{element.name} must start inside the minimum live window."
                );
                Assert.LessOrEqual(
                    element.worldBound.yMax,
                    root.worldBound.yMax + 0.5f,
                    $"{element.name} must end inside the minimum live window."
                );
            }

            float visibleHeight = detail.parent.resolvedStyle.height;
            Assert.Less(
                visibleHeight,
                DxMessagingMessageMonitorWindow.DetailsPaneResizeMaxHeight,
                "The live hierarchy must constrain a 900px preference in its minimum window."
            );
            DragResizeHandle(divider, deltaY: -20f);
            Assert.AreEqual(
                Mathf.Clamp(
                    visibleHeight + 20f,
                    DxMessagingMessageMonitorWindow.DetailsPaneMinHeight,
                    DxMessagingMessageMonitorWindow.DetailsPaneResizeMaxHeight
                ),
                resizedHeight,
                1f,
                "The first live drag must start from the visible height, then honor the pane bounds."
            );
        }

        [Test]
        public void LiveModeHidesTheDetailsDividerWhenThereIsNoSelectedRow()
        {
            MessageMonitorLiveRecorder recorder = Recorder(Entry(1, "PlayerSpawned"));
            VisualElement root = CreateView(
                recorder,
                callbacks: new MessageMonitorLiveViewCallbacks { InitialDetailsPaneHeight = 240f }
            );
            VisualElement body = root.Q<VisualElement>(DxMessagingMessageMonitorLiveView.BodyName);
            VisualElement detailSlot = root.Q<VisualElement>(
                DxMessagingMessageMonitorLiveView.DetailName
            ).parent;

            VisualElement divider = root.Q<VisualElement>(
                DxMessagingMessageMonitorWindow.DetailsPaneResizerName
            );
            Assert.IsNotNull(divider, "The persistent body keeps its divider for later rows.");
            Assert.AreEqual(
                DisplayStyle.Flex,
                detailSlot.style.display.value,
                "A selected row must begin with its live details slot visible."
            );

            RenderBody(
                body,
                recorder,
                new MessageMonitorLiveViewState(
                    filterText: "does-not-match",
                    showUntargeted: true,
                    showTargeted: true,
                    showBroadcast: true
                )
            );
            Assert.AreEqual(
                DisplayStyle.None,
                divider.style.display.value,
                "An empty log must not show a divider that has no details pane to resize."
            );
            Assert.AreEqual(
                DisplayStyle.None,
                detailSlot.style.display.value,
                "A filter-empty log must not reserve the remembered detail height as blank space."
            );

            RenderBody(body, recorder);
            Assert.AreEqual(
                DisplayStyle.Flex,
                divider.style.display.value,
                "The live divider must return when filtered rows become visible again."
            );
            Assert.AreEqual(
                DisplayStyle.Flex,
                detailSlot.style.display.value,
                "The existing detail slot must return when rows become visible again."
            );
        }

        [Test]
        public void TheLiveButtonIsDisabledWhenNoHostCanSwitchModes()
        {
            EditorWindow window = CreateTrackedEditorWindow();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(
                window.rootVisualElement,
                new MessageMonitorSnapshot(
                    diagnosticsEnabled: true,
                    capacity: 100,
                    entries: new[] { Entry(1, "PlayerSpawned") }
                )
            );

            Assert.IsFalse(
                window
                    .rootVisualElement.Q<Button>(DxMessagingMessageMonitorWindow.LiveButtonName)
                    .enabledSelf
            );
        }

        /// <summary>
        /// Issue #435 reported the toolbar drawn on top of the log header. The toolbar was one row
        /// with <c>flex-wrap: wrap</c>, and at the Monitor's minimum width its chips, search field
        /// and buttons fall onto extra lines. Unity 2021.3 does not resolve a wrapping row's height
        /// from the lines it wraps onto, so those lines rendered outside the row's own box - which
        /// is why the fix is two declared rows rather than a row that grows.
        /// </summary>
        [Test]
        public void TheLiveToolbarRowsStayInsideTheirOwnBoxAtTheWindowsMinimumWidth()
        {
            VisualElement root = CreateViewInWindowSizedTo(
                MonitorMinimumWidth,
                MonitorMinimumHeight,
                Recorder(Entry(1, "PlayerSpawned"))
            );

            VisualElement header = root.Q<VisualElement>(
                DxMessagingMessageMonitorLiveView.ListHeaderName
            );
            Assert.IsNotNull(header, "The live view must render its log header.");

            VisualElement lastRow = null;
            foreach (
                string rowName in new[]
                {
                    DxMessagingMessageMonitorLiveView.ToolbarName,
                    DxMessagingMessageMonitorLiveView.FilterRowName,
                }
            )
            {
                VisualElement row = root.Q<VisualElement>(rowName);
                Assert.IsNotNull(row, $"The live view must render its `{rowName}` row.");
                Assert.Greater(row.childCount, 0, $"`{rowName}` must render its controls.");

                // Re-adding `flex-wrap` is what this catches, and it catches it where it matters:
                // a wrapped row's height is unresolved on Unity 2021.3, so the second line lands
                // outside the row and this assertion fails on that leg.
                foreach (VisualElement control in row.Children())
                {
                    Assert.LessOrEqual(
                        control.worldBound.yMax,
                        row.worldBound.yMax + LayoutTolerance,
                        $"Control '{DescribeControl(control)}' renders past the bottom of "
                            + $"`{rowName}`, so it paints over whatever the window draws beneath it."
                    );
                }

                lastRow = row;
            }

            Assert.LessOrEqual(
                lastRow.worldBound.yMax,
                header.worldBound.yMin + LayoutTolerance,
                "The toolbar rows must end before the log header begins."
            );
        }

        /// <summary>
        /// The other half of #435: MESSAGE and CONTEXT printed over each other once their columns
        /// shrank, because only the row cells clipped their text. A heading has to clip and
        /// ellipsize exactly like the cell beneath it, and the message column - the one carrying
        /// what was emitted - must keep a readable width at the Monitor's minimum size.
        /// </summary>
        [Test]
        public void EveryLogHeaderColumnClipsItsHeadingLikeTheRowCellBeneathIt()
        {
            VisualElement root = CreateViewInWindowSizedTo(
                MonitorMinimumWidth,
                MonitorMinimumHeight,
                Recorder(Entry(1, "AVeryLongFullyQualifiedPlayerSpawnedMessageTypeName"))
            );

            (string Column, string Cell)[] pairs =
            {
                (
                    DxMessagingEditorTheme.ColumnTimeClassName,
                    DxMessagingEditorTheme.RowTimeClassName
                ),
                (
                    DxMessagingEditorTheme.ColumnTypeClassName,
                    DxMessagingEditorTheme.RowTypeClassName
                ),
                (
                    DxMessagingEditorTheme.ColumnMessageClassName,
                    DxMessagingEditorTheme.RowMessageClassName
                ),
                (
                    DxMessagingEditorTheme.ColumnRouteClassName,
                    DxMessagingEditorTheme.RowRouteClassName
                ),
                (
                    DxMessagingEditorTheme.ColumnCountClassName,
                    DxMessagingEditorTheme.RowCountClassName
                ),
            };

            foreach ((string columnClass, string cellClass) in pairs)
            {
                VisualElement column = root.Q<VisualElement>(className: columnClass);
                Assert.IsNotNull(column, $"The log header must render a `{columnClass}` heading.");
                // `IResolvedStyle` exposes no `overflow`, so the clip itself is asserted on the
                // stylesheet by `DxMessagingEditorThemeTests`; what is observable here is the
                // ellipsis and the single line that clip is there to produce.
                Assert.AreEqual(
                    TextOverflow.Ellipsis,
                    column.resolvedStyle.textOverflow,
                    $"Heading `{columnClass}` must ellipsize when its column is too narrow."
                );
                Assert.AreEqual(
                    WhiteSpace.NoWrap,
                    column.resolvedStyle.whiteSpace,
                    $"Heading `{columnClass}` must stay on the header's single line."
                );

                VisualElement cell = root.Q<VisualElement>(className: cellClass);
                Assert.IsNotNull(cell, $"A rendered row must carry a `{cellClass}` cell.");
                Assert.AreEqual(
                    cell.resolvedStyle.width > 0f,
                    column.resolvedStyle.width > 0f,
                    $"Heading `{columnClass}` and cell `{cellClass}` must agree on whether the "
                        + "column is shown at all."
                );
            }

            VisualElement messageColumn = root.Q<VisualElement>(
                className: DxMessagingEditorTheme.ColumnMessageClassName
            );
            Assert.GreaterOrEqual(
                messageColumn.resolvedStyle.width,
                MessageColumnFloor - LayoutTolerance,
                "The message column collapsed to nothing at the Monitor's minimum width, which is "
                    + "the one column a reader opens the Monitor to read."
            );
        }

        private static string DescribeControl(VisualElement control)
        {
            return string.IsNullOrEmpty(control.name) ? control.GetType().Name : control.name;
        }

        /// <summary>
        /// Builds the live view inside a host window sized like the Monitor, so layout tests read
        /// the widths a reader actually gets rather than whatever a default host window happens
        /// to be.
        /// </summary>
        private VisualElement CreateViewInWindowSizedTo(
            float width,
            float height,
            MessageMonitorLiveRecorder recorder
        )
        {
            VisualElement view = DxMessagingMessageMonitorLiveView.Create(
                recorder,
                default,
                diagnosticsEnabled: true,
                callbacks: null
            );
            EditorWindow window = EditorWindowTestUtility.CreateWindow();
            _createdWindows.Add(window);
            window.position = new Rect(0f, 0f, width, height);
            EditorWindowTestUtility.ShowWindow(window);
            window.rootVisualElement.Add(view);

            // The first pass realizes the virtualized rows and changes the list's content basis;
            // the second measures the settled layout the reader sees.
            EditorSurfaceCapture.InvokeInheritedPanelMethod(
                window.rootVisualElement.panel,
                "ValidateLayout",
                System.Array.Empty<object>()
            );
            EditorSurfaceCapture.InvokeInheritedPanelMethod(
                window.rootVisualElement.panel,
                "ValidateLayout",
                System.Array.Empty<object>()
            );
            return view;
        }

        private EditorWindow CreateTrackedEditorWindow()
        {
            EditorWindow window = EditorWindowTestUtility.CreateWindow();
            _createdWindows.Add(window);
            EditorWindowTestUtility.ShowWindow(window);
            return window;
        }

        /// <summary>
        /// The window glue itself: the Live button has to swap the whole surface over, drain what
        /// the bus is already holding, and let the Snapshot button swap back. Every other test here
        /// exercises the view or the recorder in isolation.
        /// </summary>
        [Test]
        public void TheMonitorWindowSwapsIntoLiveModeAndShowsWhatTheBusAlreadyHeld()
        {
            MessageBus messageBus = MessageHandler.MessageBus as MessageBus;
            Assert.IsNotNull(messageBus);
            bool previousDiagnosticsMode = messageBus.DiagnosticsMode;
            string preferenceKey = DxMessagingMessageMonitorWindow.DetailsPaneHeightPreferenceKey;
            bool hadPreviousHeight = EditorPrefs.HasKey(preferenceKey);
            float previousHeight = EditorPrefs.GetFloat(preferenceKey, 0f);
            DxMessagingMessageMonitorWindow window =
                ScriptableObject.CreateInstance<DxMessagingMessageMonitorWindow>();
            _createdWindows.Add(window);

            try
            {
                messageBus.DiagnosticsMode = true;
                messageBus._emissionBuffer.Clear();
                LiveModeProbeMessage message = default;
                messageBus.UntargetedBroadcast(ref message);

                window.position = new Rect(0f, 0f, 700f, 700f);
                EditorWindowTestUtility.ShowWindow(window);
                VisualElement root = window.rootVisualElement;

                Assert.IsNull(
                    root.Q<VisualElement>(DxMessagingMessageMonitorLiveView.RootName),
                    "The window opens in snapshot mode."
                );
                EditorSurfaceCapture.InvokeInheritedPanelMethod(
                    root.panel,
                    "ValidateLayout",
                    System.Array.Empty<object>()
                );
                VisualElement snapshotDivider = root.Q<VisualElement>(
                    DxMessagingMessageMonitorWindow.DetailsPaneResizerName
                );
                Assert.IsNotNull(
                    snapshotDivider,
                    "Snapshot mode must expose the shared divider before switching modes."
                );
                DragResizeHandle(snapshotDivider, deltaY: -60f);
                float draggedHeight = root.Q<VisualElement>(
                    DxMessagingMessageMonitorWindow.DetailsPaneName
                ).parent.style.height.value.value;

                SendClick(root.Q<Button>(DxMessagingMessageMonitorWindow.LiveButtonName));

                Assert.IsNotNull(
                    root.Q<VisualElement>(DxMessagingMessageMonitorLiveView.RootName),
                    "The Live button must replace the snapshot surface with the live view."
                );
                Assert.AreEqual(
                    nameof(LiveModeProbeMessage),
                    root.Q<Label>(DxMessagingMessageMonitorLiveView.DetailTitleLabelName)?.text,
                    "Entering live mode drains what the bus was already holding."
                );
                Assert.AreEqual(
                    draggedHeight,
                    root.Q<VisualElement>(
                        DxMessagingMessageMonitorLiveView.DetailName
                    ).parent.style.height.value.value,
                    "A height dragged in snapshot mode must render after switching to live mode."
                );

                SendClick(root.Q<Button>(DxMessagingMessageMonitorLiveView.SnapshotButtonName));

                Assert.IsNull(
                    root.Q<VisualElement>(DxMessagingMessageMonitorLiveView.RootName),
                    "The Snapshot button swaps back."
                );
                Assert.IsNotNull(
                    root.Q<Button>(DxMessagingMessageMonitorWindow.LiveButtonName),
                    "Switching back must restore the snapshot Live button."
                );
                Assert.AreEqual(
                    draggedHeight,
                    root.Q<VisualElement>(
                        DxMessagingMessageMonitorWindow.DetailsPaneName
                    ).parent.style.height.value.value,
                    "The shared height must still render after switching back to snapshot mode."
                );
            }
            finally
            {
                messageBus.DiagnosticsMode = previousDiagnosticsMode;
                messageBus._emissionBuffer.Clear();
                // Close first because production OnDisable persists the dragged value. Restore
                // the developer's preference afterwards so this fixture is order-independent and
                // leaves the shared editor exactly as it found it.
                EditorWindowTestUtility.CloseWindow(window);
                _createdWindows.Remove(window);
                if (hadPreviousHeight)
                {
                    EditorPrefs.SetFloat(preferenceKey, previousHeight);
                }
                else
                {
                    EditorPrefs.DeleteKey(preferenceKey);
                }
            }
        }

        private readonly struct LiveModeProbeMessage : IUntargetedMessage<LiveModeProbeMessage> { }

        /// <summary>
        /// Builds the view inside a shown host window. UI Toolkit dispatches change and click
        /// events through the panel, so a detached tree would silently swallow every interaction
        /// these tests drive.
        /// </summary>
        private VisualElement CreateView(
            MessageMonitorLiveRecorder recorder,
            MessageMonitorLiveViewState viewState = default,
            MessageMonitorLiveViewCallbacks callbacks = null
        )
        {
            VisualElement view = DxMessagingMessageMonitorLiveView.Create(
                recorder,
                viewState,
                diagnosticsEnabled: true,
                callbacks
            );
            CreateTrackedEditorWindow().rootVisualElement.Add(view);
            return view;
        }

        /// <summary>
        /// Re-renders an already-built body the way the window's poll does, so the tests exercise the
        /// same incremental path rather than a fresh <see cref="DxMessagingMessageMonitorLiveView.Create"/>.
        /// </summary>
        private static void RenderBody(
            VisualElement body,
            MessageMonitorLiveRecorder recorder,
            MessageMonitorLiveViewState viewState = default,
            MessageMonitorLiveViewCallbacks callbacks = null
        )
        {
            Assert.IsNotNull(body, "The view must expose a body to re-render.");
            DxMessagingMessageMonitorLiveView.RenderBody(
                body,
                recorder,
                viewState,
                diagnosticsEnabled: true,
                callbacks
            );
        }

        private static void DragResizeHandle(VisualElement handle, float deltaY)
        {
            Vector2 start = new(handle.worldBound.center.x, handle.worldBound.center.y);
            using (
                PointerDownEvent down = PointerDownEvent.GetPooled(
                    new Event
                    {
                        type = EventType.MouseDown,
                        mousePosition = start,
                        button = 0,
                    }
                )
            )
            {
                down.target = handle;
                handle.SendEvent(down);
            }
            using (
                PointerMoveEvent move = PointerMoveEvent.GetPooled(
                    new Event
                    {
                        type = EventType.MouseDrag,
                        mousePosition = new Vector2(start.x, start.y + deltaY),
                        button = 0,
                    }
                )
            )
            {
                move.target = handle;
                handle.SendEvent(move);
            }
            using (
                PointerUpEvent up = PointerUpEvent.GetPooled(
                    new Event
                    {
                        type = EventType.MouseUp,
                        mousePosition = new Vector2(start.x, start.y + deltaY),
                        button = 0,
                    }
                )
            )
            {
                up.target = handle;
                handle.SendEvent(up);
            }
        }

        private static MessageMonitorLiveRecorder Recorder(params MessageMonitorEntry[] entries)
        {
            MessageMonitorLiveRecorder recorder = new(clock: () => 0);
            recorder.Ingest(entries);
            return recorder;
        }

        private static IReadOnlyList<MessageMonitorLiveEntry> Recorded(
            params MessageMonitorEntry[] entries
        )
        {
            return Recorder(entries).Entries;
        }

        private static MessageMonitorEntry Entry(
            long traceId,
            string messageTypeName,
            string contextText = "Context: none",
            string routeKind = DxMessagingEditorPalette.UntargetedKind,
            string stackTrace = ""
        )
        {
            return new MessageMonitorEntry(
                messageTypeName,
                contextText,
                stackTrace,
                messageTypeIdentity: messageTypeName,
                messageTypeDisplayPath: messageTypeName,
                routeKind: routeKind,
                traceId: traceId
            );
        }

        private static void SendClick(VisualElement element)
        {
            Assert.IsNotNull(element, "Cannot click a missing visual element.");
            using (ClickEvent click = ClickEvent.GetPooled())
            {
                click.target = element;
                element.SendEvent(click);
            }
        }

        private static string Text(VisualElement root, string name)
        {
            Label label = root.Q<Label>(name);
            Assert.IsNotNull(label, $"Expected a label named '{name}'.");
            return label.text;
        }

        private static HashSet<string> CollectClassNames(VisualElement root)
        {
            HashSet<string> names = new();
            void Visit(VisualElement element)
            {
                foreach (string className in element.GetClasses())
                {
                    names.Add(className);
                }

                for (int index = 0; index < element.childCount; index++)
                {
                    Visit(element[index]);
                }
            }

            Visit(root);
            return names;
        }
    }
}
#endif
