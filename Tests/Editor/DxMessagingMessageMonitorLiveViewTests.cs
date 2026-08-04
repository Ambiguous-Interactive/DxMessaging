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
                stack.Q<Label>(DxMessagingMessageMonitorLiveView.DetailStackLabelName).text
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

        [Test]
        public void TheToolbarSeparatesItsControlGroups()
        {
            VisualElement root = CreateView(Recorder(Entry(1, "A")));

            Assert.AreEqual(
                3,
                root.Q<VisualElement>(DxMessagingMessageMonitorLiveView.ToolbarName)
                    .Query<VisualElement>(className: DxMessagingEditorTheme.SeparatorClassName)
                    .ToList()
                    .Count,
                "Recording, taxonomy, search and mode are four groups, so three rules divide them."
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
            Assert.IsNotNull(detail);

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
            DxMessagingMessageMonitorWindow window =
                ScriptableObject.CreateInstance<DxMessagingMessageMonitorWindow>();
            _createdWindows.Add(window);

            try
            {
                messageBus.DiagnosticsMode = true;
                messageBus._emissionBuffer.Clear();
                LiveModeProbeMessage message = default;
                messageBus.UntargetedBroadcast(ref message);

                EditorWindowTestUtility.ShowWindow(window);
                VisualElement root = window.rootVisualElement;

                Assert.IsNull(
                    root.Q<VisualElement>(DxMessagingMessageMonitorLiveView.RootName),
                    "The window opens in snapshot mode."
                );

                SendClick(root.Q<Button>(DxMessagingMessageMonitorWindow.LiveButtonName));

                Assert.IsNotNull(root.Q<VisualElement>(DxMessagingMessageMonitorLiveView.RootName));
                Assert.AreEqual(
                    nameof(LiveModeProbeMessage),
                    root.Q<Label>(DxMessagingMessageMonitorLiveView.DetailTitleLabelName)?.text,
                    "Entering live mode drains what the bus was already holding."
                );

                SendClick(root.Q<Button>(DxMessagingMessageMonitorLiveView.SnapshotButtonName));

                Assert.IsNull(
                    root.Q<VisualElement>(DxMessagingMessageMonitorLiveView.RootName),
                    "The Snapshot button swaps back."
                );
                Assert.IsNotNull(root.Q<Button>(DxMessagingMessageMonitorWindow.LiveButtonName));
            }
            finally
            {
                messageBus.DiagnosticsMode = previousDiagnosticsMode;
                messageBus._emissionBuffer.Clear();
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
