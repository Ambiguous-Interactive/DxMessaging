#if UNITY_EDITOR && UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Editor
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using Core;
    using Core.Diagnostics;
    using Core.MessageBus;
    using Core.Messages;
    using DxMessaging.Editor;
    using DxMessaging.Editor.Windows;
    using DxMessaging.Unity;
    using NUnit.Framework;
    using UnityEditor;
    using UnityEditor.SceneManagement;
    using UnityEngine;
    using UnityEngine.SceneManagement;
    using UnityEngine.UIElements;
    using Object = UnityEngine.Object;

    [TestFixture]
    public sealed class DxMessagingMessageMonitorWindowTests
    {
        private readonly List<Object> _createdObjects = new();
        private readonly List<string> _createdAssetPaths = new();
        private readonly List<EditorWindow> _createdWindows = new();
        private const string MessageTypeLanesName = "dxmessaging-monitor-message-type-lanes";
        private const string MessageTypeLaneScrollViewName =
            "dxmessaging-monitor-message-type-lane-scroll";
        private const string MessageTypeLaneRowClassName =
            "dxmessaging-monitor-message-type-lane-row";
        private const string MessageTypeLanesSummaryLabelName =
            "dxmessaging-monitor-message-type-lanes-summary";
        private const string MessageTypeLaneTypeLabelName =
            "dxmessaging-monitor-message-type-lane-type";
        private const string MessageTypeLaneSummaryLabelName =
            "dxmessaging-monitor-message-type-lane-summary";
        private const string MessageTypeLaneContextsLabelName =
            "dxmessaging-monitor-message-type-lane-contexts";
        private const string MessageTypeLaneFilterButtonName =
            "dxmessaging-monitor-message-type-lane-filter";
        private const string ContextLanesName = "dxmessaging-monitor-context-lanes";
        private const string ContextLaneScrollViewName = "dxmessaging-monitor-context-lane-scroll";
        private const string ContextLaneRowClassName = "dxmessaging-monitor-context-lane-row";
        private const string ContextLanesSummaryLabelName =
            "dxmessaging-monitor-context-lanes-summary";
        private const string ContextLaneContextLabelName =
            "dxmessaging-monitor-context-lane-context";
        private const string ContextLaneSummaryLabelName =
            "dxmessaging-monitor-context-lane-summary";
        private const string ContextLaneMessagesLabelName =
            "dxmessaging-monitor-context-lane-messages";
        private const string ContextLaneFilterButtonName =
            "dxmessaging-monitor-context-lane-filter";
        private const string ActiveFilterSummaryName = "dxmessaging-monitor-active-filter";
        private const string ActiveFilterSummaryLabelName =
            "dxmessaging-monitor-active-filter-label";
        private const string ActiveFilterTokenScrollViewName =
            "dxmessaging-monitor-active-filter-token-scroll";
        private const string ActiveFilterTokenClassName = "dxmessaging-monitor-active-filter-token";
        private const string ActiveFilterClearButtonName =
            "dxmessaging-monitor-active-filter-clear";

        [TearDown]
        public void TearDown()
        {
            foreach (Object instance in _createdObjects)
            {
                if (instance != null)
                {
                    if (instance is GameObject gameObject)
                    {
                        foreach (
                            MessagingComponent messagingComponent in gameObject.GetComponentsInChildren<MessagingComponent>(
                                includeInactive: true
                            )
                        )
                        {
                            messagingComponent.EditorResetRuntimeState();
                        }
                    }

                    Object.DestroyImmediate(instance);
                }
            }
            _createdObjects.Clear();

            foreach (string assetPath in _createdAssetPaths)
            {
                if (!string.IsNullOrWhiteSpace(assetPath))
                {
                    EditorWindowTestUtility.IgnoreUnityInvalidGcHandleAsserts(() =>
                        AssetDatabase.DeleteAsset(assetPath)
                    );
                }
            }
            _createdAssetPaths.Clear();

            EditorWindowTestUtility.CloseTrackedWindows(_createdWindows);

            if (MessageHandler.MessageBus is MessageBus messageBus)
            {
                messageBus.DiagnosticsMode = false;
                messageBus._emissionBuffer.Clear();
            }
        }

        [Test]
        public void MessageMonitorDoesNotUseFocusedInspectorTickRefresh()
        {
            Assert.That(
                typeof(DxMessagingMessageMonitorWindow).GetMethod(
                    "OnInspectorUpdate",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
                ),
                Is.Null
            );
        }

        [Test]
        public void BuildMonitorUiRendersDisabledState()
        {
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: false,
                capacity: IMessageBus.DefaultMessageBufferSize,
                entries: new[] { CreateEntry(new OlderMessage(), null) }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(
                root,
                snapshot,
                MessageMonitorViewState.Default,
                onCopyExport: _ => { }
            );

            Assert.That(
                root.ClassListContains(DxMessagingMessageMonitorWindow.RootClassName),
                Is.True
            );
            Assert.That(root.ClassListContains(DxMessagingEditorTheme.ThemeClassName), Is.True);
            Assert.That(root.ClassListContains(DxMessagingEditorTheme.WindowClassName), Is.True);
            Assert.That(
                root.Query<VisualElement>(
                        className: DxMessagingMessageMonitorWindow.ToolbarClassName
                    )
                    .First()
                    .ClassListContains(DxMessagingEditorTheme.ToolbarClassName),
                Is.True
            );
            Assert.That(
                root.Q<Button>(DxMessagingMessageMonitorWindow.ExportButtonName)
                    .ClassListContains(DxMessagingEditorTheme.ToolButtonClassName),
                Is.True
            );
            Assert.That(
                root.Q<Label>(DxMessagingMessageMonitorWindow.StatusLabelName).text,
                Does.Contain("Off")
            );
            Assert.That(
                root.Query<VisualElement>(className: DxMessagingMessageMonitorWindow.RowClassName)
                    .ToList(),
                Is.Empty
            );
            Label emptyBody = root.Q<Label>(DxMessagingMessageMonitorWindow.EmptyStateLabelName);
            Assert.That(emptyBody, Is.Not.Null);
            Assert.That(emptyBody.text, Does.Contain("Enable"));
            Assert.That(
                emptyBody.ClassListContains(DxMessagingEditorTheme.EmptyBodyClassName),
                Is.True
            );
            Assert.That(
                emptyBody.parent.ClassListContains(DxMessagingEditorTheme.EmptyClassName),
                Is.True
            );
            Label emptyTitle = root.Q<Label>(
                DxMessagingMessageMonitorWindow.EmptyStateTitleLabelName
            );
            Assert.That(emptyTitle, Is.Not.Null);
            Assert.That(
                emptyTitle.ClassListContains(DxMessagingEditorTheme.EmptyTitleClassName),
                Is.True
            );
            Assert.That(
                root.Q<Button>(DxMessagingMessageMonitorWindow.ExportButtonName).enabledSelf,
                Is.False
            );
            Assert.That(
                DxMessagingMessageMonitorWindow.CreateExportText(snapshot, snapshot.Entries),
                Does.Contain("\"entryCount\": 0")
            );
        }

        // The snapshot is built inside the test body (not passed as a parameter) because
        // MessageMonitorSnapshot is internal, and a public [Test] method may not expose an
        // internal parameter type (CS0051).
        [TestCase("unavailable", "Monitor unavailable", "active global bus")]
        [TestCase("diagnostics-off", "Diagnostics are Off", "Enable diagnostics")]
        [TestCase("no-messages-yet", "No messages yet", "recorded")]
        public void BuildMonitorUiEmptyStateHasExpectedTitleAndBody(
            string state,
            string expectedTitle,
            string expectedBodySubstring
        )
        {
            MessageMonitorSnapshot snapshot = state switch
            {
                "unavailable" => MessageMonitorSnapshot.Unavailable(
                    "The active global bus is not the default DxMessaging MessageBus."
                ),
                "diagnostics-off" => new MessageMonitorSnapshot(
                    diagnosticsEnabled: false,
                    capacity: 8,
                    entries: Array.Empty<MessageMonitorEntry>()
                ),
                _ => new MessageMonitorSnapshot(
                    diagnosticsEnabled: true,
                    capacity: 8,
                    entries: Array.Empty<MessageMonitorEntry>()
                ),
            };
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(root, snapshot);

            Label title = root.Q<Label>(DxMessagingMessageMonitorWindow.EmptyStateTitleLabelName);
            Assert.That(title, Is.Not.Null);
            Assert.That(title.text, Is.EqualTo(expectedTitle));
            Assert.That(
                title.ClassListContains(DxMessagingEditorTheme.EmptyTitleClassName),
                Is.True
            );

            Label body = root.Q<Label>(DxMessagingMessageMonitorWindow.EmptyStateLabelName);
            Assert.That(body, Is.Not.Null);
            Assert.That(body.text, Does.Contain(expectedBodySubstring));
            Assert.That(body.ClassListContains(DxMessagingEditorTheme.EmptyBodyClassName), Is.True);
            Assert.That(
                body.parent.ClassListContains(DxMessagingEditorTheme.EmptyClassName),
                Is.True
            );
        }

        [Test]
        public void BuildMonitorUiRendersMostRecentEntriesFirst()
        {
            MessageMonitorEntry older = CreateEntry(new OlderMessage(), null);
            MessageMonitorEntry newer = CreateEntry(new NewerMessage(), new InstanceId(123));
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { newer, older }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(root, snapshot);

            List<VisualElement> rows = root.Query<VisualElement>(
                    className: DxMessagingMessageMonitorWindow.RowClassName
                )
                .ToList();
            Assert.That(rows.Count, Is.EqualTo(2));
            Assert.That(
                rows[0].Q<Label>(DxMessagingMessageMonitorWindow.MessageTypeLabelName).text,
                Is.EqualTo(nameof(NewerMessage))
            );
            Assert.That(
                rows[0].Q<Label>(DxMessagingMessageMonitorWindow.ContextLabelName).text,
                Does.Contain("123")
            );
            Assert.That(
                rows[1].Q<Label>(DxMessagingMessageMonitorWindow.MessageTypeLabelName).text,
                Is.EqualTo(nameof(OlderMessage))
            );
        }

        [Test]
        public void BuildMonitorUiRendersTaxonomyChipForKnownMessageKinds()
        {
            MessageMonitorEntry untargeted = CreateEntry(new OlderMessage(), null);
            MessageMonitorEntry targeted = CreateEntry(new NewerMessage(), new InstanceId(123));
            MessageMonitorEntry broadcast = CreateEntry(
                new BroadcastMessage(),
                new InstanceId(456)
            );
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { untargeted, targeted, broadcast }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(root, snapshot);

            Dictionary<string, VisualElement> rowsByType = root.Query<VisualElement>(
                    className: DxMessagingMessageMonitorWindow.RowClassName
                )
                .ToList()
                .ToDictionary(
                    row => row.Q<Label>(DxMessagingMessageMonitorWindow.MessageTypeLabelName).text,
                    StringComparer.Ordinal
                );

            AssertTaxonomyRow(
                rowsByType[nameof(OlderMessage)],
                "Untargeted",
                DxMessagingEditorPalette.Untargeted
            );
            AssertTaxonomyRow(
                rowsByType[nameof(NewerMessage)],
                "Targeted",
                DxMessagingEditorPalette.Targeted
            );
            AssertTaxonomyRow(
                rowsByType[nameof(BroadcastMessage)],
                "Broadcast",
                DxMessagingEditorPalette.Broadcast
            );
        }

        [Test]
        public void BuildMonitorUiFiltersEntriesByTypeAndContext()
        {
            MessageMonitorEntry older = CreateEntry(new OlderMessage(), null);
            MessageMonitorEntry newer = CreateEntry(new NewerMessage(), new InstanceId(123));
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { newer, older }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(
                root,
                snapshot,
                new MessageMonitorViewState("123")
            );

            TextField filter = root.Q<TextField>(DxMessagingMessageMonitorWindow.FilterFieldName);
            Assert.That(filter, Is.Not.Null);
            Assert.That(filter.value, Is.EqualTo("123"));
            Assert.That(
                root.Q<Label>(DxMessagingMessageMonitorWindow.StatusLabelName).text,
                Does.Contain("1/2 shown")
            );

            List<VisualElement> rows = root.Query<VisualElement>(
                    className: DxMessagingMessageMonitorWindow.RowClassName
                )
                .ToList();
            Assert.That(rows.Count, Is.EqualTo(1));
            Assert.That(
                rows[0].Q<Label>(DxMessagingMessageMonitorWindow.MessageTypeLabelName).text,
                Is.EqualTo(nameof(NewerMessage))
            );
        }

        [Test]
        public void BuildMonitorUiFiltersEntriesByMessageTypeFacet()
        {
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[]
                {
                    new MessageMonitorEntry(
                        "DuplicateMessage",
                        "Context: one",
                        string.Empty,
                        "Type.One.DuplicateMessage",
                        "Type.One.DuplicateMessage"
                    ),
                    new MessageMonitorEntry(
                        "DuplicateMessage",
                        "Context: two",
                        string.Empty,
                        "Type.Two.DuplicateMessage",
                        "Type.Two.DuplicateMessage"
                    ),
                }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(
                root,
                snapshot,
                new MessageMonitorViewState("type:Type.Two")
            );

            List<VisualElement> rows = root.Query<VisualElement>(
                    className: DxMessagingMessageMonitorWindow.RowClassName
                )
                .ToList();
            Assert.That(rows.Count, Is.EqualTo(1));
            Assert.That(
                rows[0].Q<Label>(DxMessagingMessageMonitorWindow.ContextLabelName).text,
                Does.Contain("two")
            );
        }

        [Test]
        public void BuildMonitorUiFiltersEntriesByContextAndStackFacets()
        {
            MessageMonitorEntry first = new(
                nameof(OlderMessage),
                "Context: Enemy",
                "Game.Combat.Apply"
            );
            MessageMonitorEntry second = new(
                nameof(NewerMessage),
                "Context: Player",
                "Game.Ui.Refresh"
            );
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { first, second }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(
                root,
                snapshot,
                new MessageMonitorViewState("context:Enemy stack:Combat")
            );

            List<VisualElement> rows = root.Query<VisualElement>(
                    className: DxMessagingMessageMonitorWindow.RowClassName
                )
                .ToList();
            Assert.That(rows.Count, Is.EqualTo(1));
            Assert.That(
                rows[0].Q<Label>(DxMessagingMessageMonitorWindow.MessageTypeLabelName).text,
                Is.EqualTo(nameof(OlderMessage))
            );
        }

        [Test]
        public void BuildMonitorUiFiltersEntriesByMessageAliasFacet()
        {
            MessageMonitorEntry older = CreateEntry(new OlderMessage(), null);
            MessageMonitorEntry newer = CreateEntry(new NewerMessage(), new InstanceId(123));
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { newer, older }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(
                root,
                snapshot,
                new MessageMonitorViewState("message:Newer")
            );

            List<VisualElement> rows = root.Query<VisualElement>(
                    className: DxMessagingMessageMonitorWindow.RowClassName
                )
                .ToList();
            Assert.That(rows.Count, Is.EqualTo(1));
            Assert.That(
                rows[0].Q<Label>(DxMessagingMessageMonitorWindow.MessageTypeLabelName).text,
                Is.EqualTo(nameof(NewerMessage))
            );
        }

        [Test]
        public void BuildMonitorUiPreservesPlainTextFilterAsWholeSubstring()
        {
            MessageMonitorEntry entry = new(
                nameof(NewerMessage),
                "Context: Player",
                "Game.Ui.Refresh"
            );
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { entry }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(
                root,
                snapshot,
                new MessageMonitorViewState("NewerMessage Player")
            );

            Assert.That(
                root.Query<VisualElement>(className: DxMessagingMessageMonitorWindow.RowClassName)
                    .ToList(),
                Is.Empty
            );
            Assert.That(
                root.Q<Label>(DxMessagingMessageMonitorWindow.EmptyStateLabelName).text,
                Does.Contain("No messages match")
            );
        }

        [Test]
        public void BuildMonitorUiPreservesFieldLookingPlainTextAsWholeSubstring()
        {
            MessageMonitorEntry playerType = new("PlayerAlert", "Context: Enemy", string.Empty);
            MessageMonitorEntry playerContext = new(
                nameof(OlderMessage),
                "Context: Player",
                string.Empty
            );
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { playerType, playerContext }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(
                root,
                snapshot,
                new MessageMonitorViewState("Context: Player")
            );

            List<VisualElement> rows = root.Query<VisualElement>(
                    className: DxMessagingMessageMonitorWindow.RowClassName
                )
                .ToList();
            Assert.That(rows.Count, Is.EqualTo(1));
            Assert.That(
                rows[0].Q<Label>(DxMessagingMessageMonitorWindow.ContextLabelName).text,
                Is.EqualTo("Context: Player")
            );
        }

        [Test]
        public void BuildMonitorUiDoesNotPartiallyScopeSpacedFacetValues()
        {
            MessageMonitorEntry entry = new("ShipMessage", "Context: Player", string.Empty);
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { entry }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(
                root,
                snapshot,
                new MessageMonitorViewState("context:Player Ship")
            );

            Assert.That(
                root.Query<VisualElement>(className: DxMessagingMessageMonitorWindow.RowClassName)
                    .ToList(),
                Is.Empty
            );
        }

        [Test]
        public void BuildMonitorUiRendersActiveTypedFilterSummary()
        {
            MessageMonitorEntry entry = new(
                nameof(NewerMessage),
                "Context: Player",
                "Game.Ui.Refresh"
            );
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { entry }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(
                root,
                snapshot,
                new MessageMonitorViewState("type:Newer context:Player")
            );

            VisualElement summary = root.Q<VisualElement>(ActiveFilterSummaryName);
            Assert.That(summary, Is.Not.Null);
            Assert.That(summary.style.display.value, Is.EqualTo(DisplayStyle.Flex));
            AssertCompleteBorder(summary, DxMessagingEditorPalette.Amber);
            Assert.That(
                summary.Q<Label>(ActiveFilterSummaryLabelName).text,
                Is.EqualTo("Active typed filters")
            );
            CollectionAssert.AreEqual(
                new[] { "type:Newer", "context:Player" },
                summary
                    .Query<Label>(className: ActiveFilterTokenClassName)
                    .ToList()
                    .ConvertAll(label => label.text)
            );
            Assert.That(summary.Q<Button>(ActiveFilterClearButtonName), Is.Not.Null);
        }

        [Test]
        public void BuildMonitorUiRendersActivePlainTextFilterSummary()
        {
            MessageMonitorEntry entry = new(
                nameof(NewerMessage),
                "Context: Player",
                "Game.Ui.Refresh"
            );
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { entry }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(
                root,
                snapshot,
                new MessageMonitorViewState("Context: Player")
            );

            VisualElement summary = root.Q<VisualElement>(ActiveFilterSummaryName);
            Assert.That(summary, Is.Not.Null);
            Assert.That(summary.style.display.value, Is.EqualTo(DisplayStyle.Flex));
            Assert.That(
                summary.Q<Label>(ActiveFilterSummaryLabelName).text,
                Is.EqualTo("Active text filter")
            );
            CollectionAssert.AreEqual(
                new[] { "Context: Player" },
                summary
                    .Query<Label>(className: ActiveFilterTokenClassName)
                    .ToList()
                    .ConvertAll(label => label.text)
            );
        }

        [Test]
        public void BuildMonitorUiHidesActiveFilterSummaryWithoutFilter()
        {
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { CreateEntry(new OlderMessage(), null) }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(root, snapshot);

            VisualElement summary = root.Q<VisualElement>(ActiveFilterSummaryName);
            Assert.That(summary, Is.Not.Null);
            Assert.That(summary.style.display.value, Is.EqualTo(DisplayStyle.None));
            Assert.That(
                summary.Query<Label>(className: ActiveFilterTokenClassName).ToList(),
                Is.Empty
            );
        }

        [Test]
        public void BuildMonitorUiClearButtonClearsFilterTextAndCallback()
        {
            MessageMonitorEntry older = CreateEntry(new OlderMessage(), null);
            MessageMonitorEntry newer = CreateEntry(new NewerMessage(), new InstanceId(123));
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { newer, older }
            );
            EditorWindow window = CreateTrackedEditorWindow();
            string observedFilter = null;

            try
            {
                EditorWindowTestUtility.ShowWindow(window);
                VisualElement root = window.rootVisualElement;
                DxMessagingMessageMonitorWindow.BuildMonitorUi(
                    root,
                    snapshot,
                    new MessageMonitorViewState("type:Newer"),
                    filterText => observedFilter = filterText,
                    onCopyExport: _ => { }
                );

                TextField filter = root.Q<TextField>(
                    DxMessagingMessageMonitorWindow.FilterFieldName
                );
                VisualElement summary = root.Q<VisualElement>(ActiveFilterSummaryName);
                Button clear = summary.Q<Button>(ActiveFilterClearButtonName);

                Assert.That(filter.value, Is.EqualTo("type:Newer"));
                Assert.That(summary.style.display.value, Is.EqualTo(DisplayStyle.Flex));

                SendClick(clear);

                Assert.That(observedFilter, Is.EqualTo(string.Empty));
                Assert.That(filter.value, Is.EqualTo(string.Empty));
                Assert.That(summary.style.display.value, Is.EqualTo(DisplayStyle.None));
            }
            finally
            {
                EditorWindowTestUtility.CloseWindow(window);
            }
        }

        [Test]
        public void BuildMonitorUiClearButtonUpdatesAttachedUiWithoutFilterCallback()
        {
            MessageMonitorEntry older = CreateEntry(new OlderMessage(), null);
            MessageMonitorEntry newer = CreateEntry(new NewerMessage(), new InstanceId(123));
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { newer, older }
            );
            EditorWindow window = CreateTrackedEditorWindow();

            try
            {
                EditorWindowTestUtility.ShowWindow(window);
                VisualElement root = window.rootVisualElement;
                DxMessagingMessageMonitorWindow.BuildMonitorUi(
                    root,
                    snapshot,
                    new MessageMonitorViewState("type:Missing"),
                    onCopyExport: _ => { }
                );

                TextField filter = root.Q<TextField>(
                    DxMessagingMessageMonitorWindow.FilterFieldName
                );
                VisualElement summary = root.Q<VisualElement>(ActiveFilterSummaryName);
                Button clear = summary.Q<Button>(ActiveFilterClearButtonName);
                Button export = root.Q<Button>(DxMessagingMessageMonitorWindow.ExportButtonName);

                Assert.That(filter.value, Is.EqualTo("type:Missing"));
                Assert.That(summary.style.display.value, Is.EqualTo(DisplayStyle.Flex));
                Assert.That(export.enabledSelf, Is.False);
                Assert.That(
                    root.Q<Label>(DxMessagingMessageMonitorWindow.StatusLabelName).text,
                    Does.Contain("0/2 shown")
                );
                Assert.That(
                    root.Query<VisualElement>(
                            className: DxMessagingMessageMonitorWindow.RowClassName
                        )
                        .ToList(),
                    Is.Empty
                );

                SendClick(clear);

                Assert.That(filter.value, Is.EqualTo(string.Empty));
                Assert.That(summary.style.display.value, Is.EqualTo(DisplayStyle.None));
                Assert.That(export.enabledSelf, Is.True);
                Assert.That(
                    root.Q<Label>(DxMessagingMessageMonitorWindow.StatusLabelName).text,
                    Does.Not.Contain("shown")
                );
                Assert.That(
                    root.Query<VisualElement>(
                            className: DxMessagingMessageMonitorWindow.RowClassName
                        )
                        .ToList()
                        .Count,
                    Is.EqualTo(2)
                );
            }
            finally
            {
                EditorWindowTestUtility.CloseWindow(window);
            }
        }

        [Test]
        public void BuildMonitorUiKeepsClearReachableForLongActiveFilter()
        {
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { CreateEntry(new OlderMessage(), null) }
            );
            string longFilter = string.Join(
                " ",
                Enumerable.Range(0, 24).Select(index => $"type:Message{index:00}")
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(
                root,
                snapshot,
                new MessageMonitorViewState(longFilter)
            );

            VisualElement summary = root.Q<VisualElement>(ActiveFilterSummaryName);
            Button clear = summary.Q<Button>(ActiveFilterClearButtonName);
            ScrollView tokenScroll = summary.Q<ScrollView>(ActiveFilterTokenScrollViewName);
            List<VisualElement> children = summary.Children().ToList();

            Assert.That(clear, Is.Not.Null);
            Assert.That(tokenScroll, Is.Not.Null);
            Assert.That(children.IndexOf(clear), Is.LessThan(children.IndexOf(tokenScroll)));
            Assert.That(tokenScroll.style.maxHeight.value.value, Is.EqualTo(72f));
            Assert.That(
                tokenScroll.Query<Label>(className: ActiveFilterTokenClassName).ToList().Count,
                Is.EqualTo(24)
            );
        }

        [Test]
        public void CreateExportTextDoesNotExportActiveFilterSummary()
        {
            MessageMonitorEntry older = CreateEntry(new OlderMessage(), null);
            MessageMonitorEntry newer = CreateEntry(new NewerMessage(), new InstanceId(123));
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { newer, older }
            );

            string exportText = DxMessagingMessageMonitorWindow.CreateExportText(
                snapshot,
                "type:Newer"
            );

            Assert.That(exportText, Does.Not.Contain("activeFilter"));
            Assert.That(exportText, Does.Not.Contain("filterSummary"));
            Assert.That(exportText, Does.Contain(nameof(NewerMessage)));
            Assert.That(exportText, Does.Not.Contain(nameof(OlderMessage)));
        }

        [Test]
        public void BuildMonitorUiRendersVisibleContextLanesFromVisibleEntries()
        {
            MessageMonitorEntry enemyOlder = new(
                nameof(OlderMessage),
                "Context: Enemy",
                string.Empty
            );
            MessageMonitorEntry playerOlder = new(
                nameof(OlderMessage),
                "Context: Player",
                string.Empty
            );
            MessageMonitorEntry playerNewer = new(
                nameof(NewerMessage),
                "Context: Player",
                string.Empty
            );
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { playerNewer, playerOlder, enemyOlder }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(root, snapshot);

            VisualElement lanes = root.Q<VisualElement>(ContextLanesName);
            Assert.That(lanes, Is.Not.Null);
            Assert.That(
                lanes.Q<Label>(ContextLanesSummaryLabelName).text,
                Is.EqualTo(
                    "2 context lanes | Entries: 3 | Busiest context: Context: Player | Share: 2/3 (67%)"
                )
            );

            List<VisualElement> rows = lanes
                .Query<VisualElement>(className: ContextLaneRowClassName)
                .ToList();
            Assert.That(rows.Count, Is.EqualTo(2));
            Assert.That(
                rows[0].Q<Label>(ContextLaneContextLabelName).text,
                Is.EqualTo("Context: Player")
            );
            Assert.That(
                rows[0].Q<Label>(ContextLaneSummaryLabelName).text,
                Is.EqualTo("Entries: 2 | Message types: 2 | Share: 2/3 (67%)")
            );
            Assert.That(
                rows[0].Q<Label>(ContextLaneMessagesLabelName).text,
                Does.Contain(nameof(OlderMessage))
            );
            Assert.That(
                rows[0].Q<Label>(ContextLaneMessagesLabelName).text,
                Does.Contain(nameof(NewerMessage))
            );
            Assert.That(
                rows[1].Q<Label>(ContextLaneContextLabelName).text,
                Is.EqualTo("Context: Enemy")
            );
        }

        [Test]
        public void BuildMonitorUiUsesCompleteBordersForVisibleLaneGroups()
        {
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[]
                {
                    new MessageMonitorEntry(nameof(OlderMessage), "Context: Player", string.Empty),
                    new MessageMonitorEntry(nameof(NewerMessage), "Context: HUD", string.Empty),
                }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(root, snapshot);

            AssertCompleteBorder(
                root.Q<VisualElement>(MessageTypeLanesName),
                DxMessagingEditorPalette.BorderPanel
            );
            AssertCompleteBorder(
                root.Q<VisualElement>(ContextLanesName),
                DxMessagingEditorPalette.BorderPanel
            );
        }

        [Test]
        public void BuildMonitorUiScopesVisibleContextLanesToFilteredEntries()
        {
            MessageMonitorEntry older = new(nameof(OlderMessage), "Context: Enemy", string.Empty);
            MessageMonitorEntry newer = new(nameof(NewerMessage), "Context: Player", string.Empty);
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { newer, older }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(
                root,
                snapshot,
                new MessageMonitorViewState("context:Player")
            );

            VisualElement lanes = root.Q<VisualElement>(ContextLanesName);
            Assert.That(lanes, Is.Not.Null);
            Assert.That(
                lanes.Q<Label>(ContextLanesSummaryLabelName).text,
                Is.EqualTo(
                    "1 context lane | Entries: 1 | Busiest context: Context: Player | Share: 1/1 (100%)"
                )
            );

            List<VisualElement> rows = lanes
                .Query<VisualElement>(className: ContextLaneRowClassName)
                .ToList();
            Assert.That(rows.Count, Is.EqualTo(1));
            Assert.That(
                rows[0].Q<Label>(ContextLaneContextLabelName).text,
                Is.EqualTo("Context: Player")
            );
        }

        [Test]
        public void BuildMonitorUiContextLaneFilterButtonAppliesVisibleContextFilter()
        {
            MessageMonitorEntry enemy = new(nameof(OlderMessage), "Context: Enemy", string.Empty);
            MessageMonitorEntry player = new(nameof(NewerMessage), "Context: Player", string.Empty);
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { player, enemy }
            );
            EditorWindow window = CreateTrackedEditorWindow();

            try
            {
                EditorWindowTestUtility.ShowWindow(window);
                VisualElement root = window.rootVisualElement;
                DxMessagingMessageMonitorWindow.BuildMonitorUi(
                    root,
                    snapshot,
                    MessageMonitorViewState.Default,
                    onCopyExport: _ => { }
                );

                VisualElement playerLane = root.Q<VisualElement>(ContextLanesName)
                    .Query<VisualElement>(className: ContextLaneRowClassName)
                    .ToList()
                    .First(row =>
                        row.Q<Label>(ContextLaneContextLabelName).text == "Context: Player"
                    );
                Button filterButton = playerLane.Q<Button>(ContextLaneFilterButtonName);
                Assert.That(filterButton, Is.Not.Null);

                SendClick(filterButton);

                Assert.That(
                    root.Q<TextField>(DxMessagingMessageMonitorWindow.FilterFieldName).value,
                    Is.EqualTo("context:\"Context: Player\"")
                );
                Assert.That(
                    root.Q<VisualElement>(ActiveFilterSummaryName)
                        .Q<Label>(ActiveFilterSummaryLabelName)
                        .text,
                    Is.EqualTo("Active typed filters")
                );
                CollectionAssert.AreEqual(
                    new[] { "context:\"Context: Player\"" },
                    root.Q<VisualElement>(ActiveFilterSummaryName)
                        .Query<Label>(className: ActiveFilterTokenClassName)
                        .ToList()
                        .ConvertAll(label => label.text)
                );
                Assert.That(
                    root.Q<Label>(DxMessagingMessageMonitorWindow.StatusLabelName).text,
                    Does.Contain("1/2 shown")
                );

                List<VisualElement> rows = root.Query<VisualElement>(
                        className: DxMessagingMessageMonitorWindow.RowClassName
                    )
                    .ToList();
                Assert.That(rows.Count, Is.EqualTo(1));
                Assert.That(
                    rows[0].Q<Label>(DxMessagingMessageMonitorWindow.ContextLabelName).text,
                    Is.EqualTo("Context: Player")
                );
                Assert.That(
                    root.Q<Button>(DxMessagingMessageMonitorWindow.ExportButtonName).enabledSelf,
                    Is.True
                );
            }
            finally
            {
                EditorWindowTestUtility.CloseWindow(window);
            }
        }

        [Test]
        public void BuildMonitorUiContextLaneFilterButtonMatchesOverlappingContextExactly()
        {
            MessageMonitorEntry player = new(nameof(NewerMessage), "Context: Player", string.Empty);
            MessageMonitorEntry ship = new(
                nameof(OlderMessage),
                "Context: Player Ship",
                string.Empty
            );
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { ship, player }
            );
            EditorWindow window = CreateTrackedEditorWindow();

            try
            {
                EditorWindowTestUtility.ShowWindow(window);
                VisualElement root = window.rootVisualElement;
                DxMessagingMessageMonitorWindow.BuildMonitorUi(
                    root,
                    snapshot,
                    MessageMonitorViewState.Default,
                    onCopyExport: _ => { }
                );

                VisualElement playerLane = root.Q<VisualElement>(ContextLanesName)
                    .Query<VisualElement>(className: ContextLaneRowClassName)
                    .ToList()
                    .First(row =>
                        row.Q<Label>(ContextLaneContextLabelName).text == "Context: Player"
                    );
                Button filterButton = playerLane.Q<Button>(ContextLaneFilterButtonName);
                Assert.That(filterButton, Is.Not.Null);

                SendClick(filterButton);

                Assert.That(
                    root.Q<TextField>(DxMessagingMessageMonitorWindow.FilterFieldName).value,
                    Is.EqualTo("context:\"Context: Player\"")
                );
                Assert.That(
                    root.Q<VisualElement>(ActiveFilterSummaryName)
                        .Q<Label>(ActiveFilterSummaryLabelName)
                        .text,
                    Is.EqualTo("Active typed filters")
                );
                CollectionAssert.AreEqual(
                    new[] { "context:\"Context: Player\"" },
                    root.Q<VisualElement>(ActiveFilterSummaryName)
                        .Query<Label>(className: ActiveFilterTokenClassName)
                        .ToList()
                        .ConvertAll(label => label.text)
                );
                Assert.That(
                    root.Q<Label>(DxMessagingMessageMonitorWindow.StatusLabelName).text,
                    Does.Contain("1/2 shown")
                );

                List<VisualElement> rows = root.Query<VisualElement>(
                        className: DxMessagingMessageMonitorWindow.RowClassName
                    )
                    .ToList();
                Assert.That(rows.Count, Is.EqualTo(1));
                Assert.That(
                    rows[0].Q<Label>(DxMessagingMessageMonitorWindow.ContextLabelName).text,
                    Is.EqualTo("Context: Player")
                );
            }
            finally
            {
                EditorWindowTestUtility.CloseWindow(window);
            }
        }

        [Test]
        public void BuildMonitorUiKeepsDistinctContextLaneMessagesForSameSimpleNames()
        {
            MessageMonitorEntry first = CreateEntry(new CollisionOne.DuplicateMessage(), null);
            MessageMonitorEntry second = CreateEntry(new CollisionTwo.DuplicateMessage(), null);
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { second, first }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(root, snapshot);

            List<VisualElement> rows = root.Q<VisualElement>(ContextLanesName)
                .Query<VisualElement>(className: ContextLaneRowClassName)
                .ToList();
            Assert.That(rows.Count, Is.EqualTo(1));
            Assert.That(
                rows[0].Q<Label>(ContextLaneSummaryLabelName).text,
                Is.EqualTo("Entries: 2 | Message types: 2 | Share: 2/2 (100%)")
            );
            Assert.That(
                rows[0].Q<Label>(ContextLaneMessagesLabelName).text,
                Does.Contain("CollisionOne.DuplicateMessage")
            );
            Assert.That(
                rows[0].Q<Label>(ContextLaneMessagesLabelName).text,
                Does.Contain("CollisionTwo.DuplicateMessage")
            );
        }

        [Test]
        public void BuildMonitorUiKeepsDistinctContextLaneMessagesAcrossSplitContexts()
        {
            MessageMonitorEntry first = new(
                "DuplicateMessage",
                "Context: Player",
                string.Empty,
                "Collision.One.DuplicateMessage",
                "CollisionOne.DuplicateMessage"
            );
            MessageMonitorEntry second = new(
                "DuplicateMessage",
                "Context: Enemy",
                string.Empty,
                "Collision.Two.DuplicateMessage",
                "CollisionTwo.DuplicateMessage"
            );
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { second, first }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(root, snapshot);

            List<VisualElement> rows = root.Q<VisualElement>(ContextLanesName)
                .Query<VisualElement>(className: ContextLaneRowClassName)
                .ToList();
            Assert.That(rows.Count, Is.EqualTo(2));
            Assert.That(
                rows[0].Q<Label>(ContextLaneMessagesLabelName).text,
                Does.Contain("CollisionTwo.DuplicateMessage")
            );
            Assert.That(
                rows[1].Q<Label>(ContextLaneMessagesLabelName).text,
                Does.Contain("CollisionOne.DuplicateMessage")
            );
        }

        [Test]
        public void BuildMonitorUiBoundsVisibleContextLaneRows()
        {
            MessageMonitorEntry[] entries = Enumerable
                .Range(0, 24)
                .Select(index => new MessageMonitorEntry(
                    $"Message{index:00}",
                    $"Context: {index:00}",
                    string.Empty
                ))
                .ToArray();
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 32,
                entries: entries
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(root, snapshot);

            VisualElement lanes = root.Q<VisualElement>(ContextLanesName);
            ScrollView scroll = lanes.Q<ScrollView>(ContextLaneScrollViewName);
            Assert.That(scroll, Is.Not.Null);
            Assert.That(scroll.style.maxHeight.value.value, Is.EqualTo(160f));
            Assert.That(
                scroll.Query<VisualElement>(className: ContextLaneRowClassName).ToList().Count,
                Is.EqualTo(24)
            );
        }

        [Test]
        public void CreateExportTextDoesNotExportVisibleContextLaneAggregates()
        {
            MessageMonitorEntry older = new(nameof(OlderMessage), "Context: Enemy", string.Empty);
            MessageMonitorEntry newer = new(nameof(NewerMessage), "Context: Player", string.Empty);
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { newer, older }
            );

            string exportText = DxMessagingMessageMonitorWindow.CreateExportText(
                snapshot,
                snapshot.Entries
            );

            Assert.That(exportText, Does.Not.Contain("contextLanes"));
            Assert.That(exportText, Does.Not.Contain("visibleContextLanes"));
        }

        [Test]
        public void BuildMonitorUiWiresAttachedFilterAndRowCallbacksWithoutRebuildingRoot()
        {
            MessageMonitorEntry older = CreateEntry(new OlderMessage(), null);
            MessageMonitorEntry newer = CreateEntry(new NewerMessage(), new InstanceId(123));
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { newer, older }
            );
            EditorWindow window = CreateTrackedEditorWindow();
            string observedFilter = null;
            int observedSelectedEntryIndex = -1;

            try
            {
                EditorWindowTestUtility.ShowWindow(window);
                VisualElement root = window.rootVisualElement;
                DxMessagingMessageMonitorWindow.BuildMonitorUi(
                    root,
                    snapshot,
                    MessageMonitorViewState.Default,
                    filterText => observedFilter = filterText,
                    selectedEntryIndex => observedSelectedEntryIndex = selectedEntryIndex,
                    onCopyExport: _ => { }
                );

                TextField filter = root.Q<TextField>(
                    DxMessagingMessageMonitorWindow.FilterFieldName
                );
                Button refresh = root.Q<Button>(DxMessagingMessageMonitorWindow.RefreshButtonName);
                Button export = root.Q<Button>(DxMessagingMessageMonitorWindow.ExportButtonName);
                List<VisualElement> rows = root.Query<VisualElement>(
                        className: DxMessagingMessageMonitorWindow.RowClassName
                    )
                    .ToList();
                int childCountBeforeFilterChange = root.childCount;

                Assert.That(refresh.enabledSelf, Is.False);
                Assert.That(export.enabledSelf, Is.True);
                Assert.That(rows.Count, Is.EqualTo(2));

                using (ClickEvent click = ClickEvent.GetPooled())
                {
                    click.target = rows[1];
                    rows[1].SendEvent(click);
                }
                Assert.That(observedSelectedEntryIndex, Is.EqualTo(1));

                filter.value = "missing";

                Assert.That(observedFilter, Is.EqualTo("missing"));
                Assert.That(root.childCount, Is.EqualTo(childCountBeforeFilterChange));
                Assert.That(export.enabledSelf, Is.False);

                filter.value = "123";

                Assert.That(observedFilter, Is.EqualTo("123"));
                Assert.That(root.childCount, Is.EqualTo(childCountBeforeFilterChange));
                Assert.That(export.enabledSelf, Is.True);
            }
            finally
            {
                EditorWindowTestUtility.CloseWindow(window);
            }
        }

        [Test]
        public void BuildMonitorUiRendersNoFilteredMatchesState()
        {
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { CreateEntry(new OlderMessage(), null) }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(
                root,
                snapshot,
                new MessageMonitorViewState("missing")
            );

            Assert.That(
                root.Query<VisualElement>(className: DxMessagingMessageMonitorWindow.RowClassName)
                    .ToList(),
                Is.Empty
            );
            Assert.That(
                root.Q<Label>(DxMessagingMessageMonitorWindow.EmptyStateTitleLabelName).text,
                Is.EqualTo("No matches")
            );
            Assert.That(
                root.Q<Label>(DxMessagingMessageMonitorWindow.EmptyStateLabelName).text,
                Does.Contain("No messages match")
            );
            Assert.That(
                root.Q<VisualElement>(DxMessagingMessageMonitorWindow.DetailsPaneName),
                Is.Null
            );
        }

        [Test]
        public void BuildMonitorUiRendersSelectedEntryDetails()
        {
            MessageMonitorEntry older = CreateEntry(new OlderMessage(), null);
            MessageMonitorEntry newer = CreateEntry(new NewerMessage(), new InstanceId(123));
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { newer, older }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(
                root,
                snapshot,
                new MessageMonitorViewState(selectedEntryIndex: 1)
            );

            VisualElement details = root.Q<VisualElement>(
                DxMessagingMessageMonitorWindow.DetailsPaneName
            );
            Assert.That(details, Is.Not.Null);
            Assert.That(
                details.Q<Label>(DxMessagingMessageMonitorWindow.DetailsTypeLabelName).text,
                Does.Contain(nameof(OlderMessage))
            );
            Assert.That(
                details.Q<Label>(DxMessagingMessageMonitorWindow.DetailsContextLabelName).text,
                Does.Contain("none")
            );
        }

        [Test]
        public void BuildMonitorUiRendersVisibleMessageTypeLanesFromVisibleEntries()
        {
            MessageMonitorEntry olderWithoutContext = CreateEntry(new OlderMessage(), null);
            MessageMonitorEntry olderWithContext = CreateEntry(
                new OlderMessage(),
                new InstanceId(42)
            );
            MessageMonitorEntry newerWithContext = CreateEntry(
                new NewerMessage(),
                new InstanceId(123)
            );
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { newerWithContext, olderWithContext, olderWithoutContext }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(root, snapshot);

            VisualElement lanes = root.Q<VisualElement>(MessageTypeLanesName);
            Assert.That(lanes, Is.Not.Null);
            Assert.That(
                lanes.Q<Label>(MessageTypeLanesSummaryLabelName).text,
                Is.EqualTo(
                    "2 message type lanes | Entries: 3 | Busiest message type: OlderMessage | Share: 2/3 (67%)"
                )
            );

            List<VisualElement> rows = lanes
                .Query<VisualElement>(className: MessageTypeLaneRowClassName)
                .ToList();
            Assert.That(rows.Count, Is.EqualTo(2));
            Assert.That(
                rows[0].Q<Label>(MessageTypeLaneTypeLabelName).text,
                Is.EqualTo(nameof(OlderMessage))
            );
            Assert.That(
                rows[0].Q<Label>(MessageTypeLaneSummaryLabelName).text,
                Is.EqualTo("Entries: 2 | Contexts: 2 | Share: 2/3 (67%)")
            );
            Assert.That(
                rows[0].Q<Label>(MessageTypeLaneContextsLabelName).text,
                Does.Contain("Context: 42")
            );
            Assert.That(
                rows[0].Q<Label>(MessageTypeLaneContextsLabelName).text,
                Does.Contain("Context: none")
            );
            Assert.That(
                rows[1].Q<Label>(MessageTypeLaneTypeLabelName).text,
                Is.EqualTo(nameof(NewerMessage))
            );
        }

        [Test]
        public void BuildMonitorUiBoundsVisibleMessageTypeLaneRows()
        {
            MessageMonitorEntry[] entries = Enumerable
                .Range(0, 24)
                .Select(index => new MessageMonitorEntry(
                    $"Message{index:00}",
                    $"Context: {index:00}",
                    string.Empty
                ))
                .ToArray();
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 32,
                entries: entries
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(root, snapshot);

            VisualElement lanes = root.Q<VisualElement>(MessageTypeLanesName);
            ScrollView scroll = lanes.Q<ScrollView>(MessageTypeLaneScrollViewName);
            Assert.That(scroll, Is.Not.Null);
            Assert.That(scroll.style.maxHeight.value.value, Is.EqualTo(160f));
            Assert.That(
                scroll.Query<VisualElement>(className: MessageTypeLaneRowClassName).ToList().Count,
                Is.EqualTo(24)
            );
        }

        [Test]
        public void BuildMonitorUiKeepsDistinctMessageTypeIdentityForSameSimpleNames()
        {
            MessageMonitorEntry first = CreateEntry(
                new CollisionOne.DuplicateMessage(),
                new InstanceId(1)
            );
            MessageMonitorEntry second = CreateEntry(
                new CollisionTwo.DuplicateMessage(),
                new InstanceId(2)
            );
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { second, first }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(root, snapshot);

            List<VisualElement> rows = root.Q<VisualElement>(MessageTypeLanesName)
                .Query<VisualElement>(className: MessageTypeLaneRowClassName)
                .ToList();
            Assert.That(rows.Count, Is.EqualTo(2));
            Assert.That(
                rows[0].Q<Label>(MessageTypeLaneTypeLabelName).text,
                Does.Contain("CollisionOne.DuplicateMessage")
            );
            Assert.That(
                rows[1].Q<Label>(MessageTypeLaneTypeLabelName).text,
                Does.Contain("CollisionTwo.DuplicateMessage")
            );
        }

        [Test]
        public void BuildMonitorUiScopesVisibleMessageTypeLanesToFilteredEntries()
        {
            MessageMonitorEntry older = CreateEntry(new OlderMessage(), null);
            MessageMonitorEntry newer = CreateEntry(new NewerMessage(), new InstanceId(123));
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { newer, older }
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(
                root,
                snapshot,
                new MessageMonitorViewState(nameof(NewerMessage))
            );

            VisualElement lanes = root.Q<VisualElement>(MessageTypeLanesName);
            Assert.That(lanes, Is.Not.Null);
            Assert.That(
                lanes.Q<Label>(MessageTypeLanesSummaryLabelName).text,
                Is.EqualTo(
                    "1 message type lane | Entries: 1 | Busiest message type: NewerMessage | Share: 1/1 (100%)"
                )
            );

            List<VisualElement> rows = lanes
                .Query<VisualElement>(className: MessageTypeLaneRowClassName)
                .ToList();
            Assert.That(rows.Count, Is.EqualTo(1));
            Assert.That(
                rows[0].Q<Label>(MessageTypeLaneTypeLabelName).text,
                Is.EqualTo(nameof(NewerMessage))
            );
        }

        [Test]
        public void BuildMonitorUiMessageTypeLaneFilterButtonAppliesTypedFilter()
        {
            MessageMonitorEntry older = CreateEntry(new OlderMessage(), null);
            MessageMonitorEntry newer = CreateEntry(new NewerMessage(), new InstanceId(123));
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { newer, older }
            );
            EditorWindow window = CreateTrackedEditorWindow();

            try
            {
                EditorWindowTestUtility.ShowWindow(window);
                VisualElement root = window.rootVisualElement;
                DxMessagingMessageMonitorWindow.BuildMonitorUi(
                    root,
                    snapshot,
                    MessageMonitorViewState.Default,
                    onCopyExport: _ => { }
                );

                VisualElement newerLane = root.Q<VisualElement>(MessageTypeLanesName)
                    .Query<VisualElement>(className: MessageTypeLaneRowClassName)
                    .ToList()
                    .First(row =>
                        row.Q<Label>(MessageTypeLaneTypeLabelName).text == nameof(NewerMessage)
                    );
                Button filterButton = newerLane.Q<Button>(MessageTypeLaneFilterButtonName);
                Assert.That(filterButton, Is.Not.Null);

                SendClick(filterButton);

                Assert.That(
                    root.Q<TextField>(DxMessagingMessageMonitorWindow.FilterFieldName).value,
                    Is.EqualTo("type:NewerMessage")
                );
                Assert.That(
                    root.Q<VisualElement>(ActiveFilterSummaryName)
                        .Q<Label>(ActiveFilterSummaryLabelName)
                        .text,
                    Is.EqualTo("Active typed filters")
                );
                Assert.That(
                    root.Q<Label>(DxMessagingMessageMonitorWindow.StatusLabelName).text,
                    Does.Contain("1/2 shown")
                );

                List<VisualElement> rows = root.Query<VisualElement>(
                        className: DxMessagingMessageMonitorWindow.RowClassName
                    )
                    .ToList();
                Assert.That(rows.Count, Is.EqualTo(1));
                Assert.That(
                    rows[0].Q<Label>(DxMessagingMessageMonitorWindow.MessageTypeLabelName).text,
                    Is.EqualTo(nameof(NewerMessage))
                );
                Assert.That(
                    root.Q<Button>(DxMessagingMessageMonitorWindow.ExportButtonName).enabledSelf,
                    Is.True
                );
            }
            finally
            {
                EditorWindowTestUtility.CloseWindow(window);
            }
        }

        [Test]
        public void CreateExportTextDoesNotExportVisibleMessageTypeLaneAggregates()
        {
            MessageMonitorEntry older = CreateEntry(new OlderMessage(), null);
            MessageMonitorEntry newer = CreateEntry(new NewerMessage(), new InstanceId(123));
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { newer, older }
            );

            string exportText = DxMessagingMessageMonitorWindow.CreateExportText(
                snapshot,
                snapshot.Entries
            );

            Assert.That(exportText, Does.Not.Contain("messageTypeLanes"));
            Assert.That(exportText, Does.Not.Contain("visibleMessageTypeLanes"));
        }

        [Test]
        public void BuildMonitorUiRendersComponentDiagnosticsPanel()
        {
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: Array.Empty<MessageMonitorEntry>()
            );
            ComponentMonitorEntry component = new(
                "Root/Emitter",
                "MessagingComponent",
                activeInHierarchy: true,
                listenerCount: 2,
                enabledListenerCount: 1,
                diagnosticsListenerCount: 1,
                registrationCount: 3,
                callCount: 7,
                localEmissionCount: 4,
                providerStatusText: "Provider: global bus",
                warningText: "Serialized provider missing"
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(root, snapshot, new[] { component });

            VisualElement panel = root.Q<VisualElement>(
                DxMessagingMessageMonitorWindow.ComponentPanelName
            );
            Assert.That(panel, Is.Not.Null);
            AssertCompleteBorder(panel, DxMessagingEditorPalette.BorderPanel);

            List<VisualElement> rows = root.Query<VisualElement>(
                    className: DxMessagingMessageMonitorWindow.ComponentRowClassName
                )
                .ToList();
            Assert.That(
                root.Q<ScrollView>(DxMessagingMessageMonitorWindow.ComponentScrollViewName),
                Is.Not.Null
            );
            Assert.That(rows.Count, Is.EqualTo(1));
            AssertCompleteBorder(rows[0], DxMessagingEditorPalette.Amber);
            Assert.That(
                rows[0].Q<Label>(DxMessagingMessageMonitorWindow.ComponentNameLabelName).text,
                Does.Contain("Root/Emitter")
            );
            Assert.That(
                rows[0].Q<Label>(DxMessagingMessageMonitorWindow.ComponentSummaryLabelName).text,
                Does.Contain("Registrations: 3")
            );
            Assert.That(
                rows[0].Q<Label>(DxMessagingMessageMonitorWindow.ComponentSummaryLabelName).text,
                Does.Contain("Calls: 7")
            );
            Assert.That(
                rows[0].Q<Label>(DxMessagingMessageMonitorWindow.ComponentProviderLabelName).text,
                Does.Contain("global bus")
            );
            Assert.That(
                rows[0].Q<Label>(DxMessagingMessageMonitorWindow.ComponentWarningLabelName).text,
                Does.Contain("Serialized provider missing")
            );
        }

        [Test]
        public void BuildMonitorUiKeepsComponentPanelVisibleWhenSnapshotUnavailable()
        {
            MessageMonitorSnapshot snapshot = MessageMonitorSnapshot.Unavailable(
                "The active global bus is not the default DxMessaging MessageBus."
            );
            ComponentMonitorEntry component = new(
                "Root/Listener",
                "MessagingComponent",
                activeInHierarchy: true,
                listenerCount: 1,
                enabledListenerCount: 1,
                diagnosticsListenerCount: 0,
                registrationCount: 1,
                callCount: 0,
                localEmissionCount: 0,
                providerStatusText: "Provider: runtime provider",
                warningText: string.Empty
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(root, snapshot, new[] { component });

            Assert.That(
                root.Q<Label>(DxMessagingMessageMonitorWindow.StatusLabelName).text,
                Is.EqualTo("Unavailable")
            );
            Assert.That(
                root.Q<Label>(DxMessagingMessageMonitorWindow.EmptyStateLabelName).text,
                Does.Contain("active global bus")
            );
            Assert.That(
                root.Q<TextField>(DxMessagingMessageMonitorWindow.FilterFieldName),
                Is.Null
            );
            Assert.That(
                root.Query<VisualElement>(
                        className: DxMessagingMessageMonitorWindow.ComponentRowClassName
                    )
                    .ToList()
                    .Count,
                Is.EqualTo(1)
            );
        }

        [Test]
        public void BuildMonitorUiKeepsComponentPanelVisibleWhenMessageDiagnosticsAreOff()
        {
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: false,
                capacity: 8,
                entries: Array.Empty<MessageMonitorEntry>()
            );
            ComponentMonitorEntry component = new(
                "Root/Listener",
                "MessagingComponent",
                activeInHierarchy: false,
                listenerCount: 1,
                enabledListenerCount: 0,
                diagnosticsListenerCount: 0,
                registrationCount: 1,
                callCount: 0,
                localEmissionCount: 0,
                providerStatusText: "Provider: global bus",
                warningText: string.Empty
            );
            VisualElement root = new();

            DxMessagingMessageMonitorWindow.BuildMonitorUi(root, snapshot, new[] { component });

            Assert.That(
                root.Q<Label>(DxMessagingMessageMonitorWindow.EmptyStateTitleLabelName).text,
                Is.EqualTo("Diagnostics are Off")
            );
            Assert.That(
                root.Q<Label>(DxMessagingMessageMonitorWindow.EmptyStateLabelName).text,
                Does.Contain("Enable diagnostics")
            );
            Assert.That(
                root.Query<VisualElement>(
                        className: DxMessagingMessageMonitorWindow.ComponentRowClassName
                    )
                    .ToList()
                    .Count,
                Is.EqualTo(1)
            );
            Assert.That(
                root.Q<Label>(DxMessagingMessageMonitorWindow.ComponentNameLabelName).text,
                Does.Contain("inactive")
            );
        }

        [Test]
        public void CaptureComponentSnapshotsReadsMessagingComponentHarnessState()
        {
            GameObject host = CreateTrackedObject("ComponentPanelHost");
            MessagingComponent messagingComponent = host.AddComponent<MessagingComponent>();
            TestListener listener = host.AddComponent<TestListener>();
            MessageBus messageBus = MessageHandler.MessageBus as MessageBus;
            Assert.That(messageBus, Is.Not.Null);
            int initialRegistrationCount = CountMessageBusRegistrations(messageBus);

            MessageRegistrationToken token = messagingComponent.Create(listener);
            token.DiagnosticMode = true;
            token.RegisterUntargeted<OlderMessage>(listener.OnOlderMessage);
            token.Enable();

            messageBus.DiagnosticsMode = true;
            messageBus._emissionBuffer.Clear();

            OlderMessage message = default;
            MessageHandler.MessageBus.UntargetedBroadcast(ref message);

            IReadOnlyList<ComponentMonitorEntry> components =
                DxMessagingMessageMonitorWindow.CaptureComponentSnapshots(
                    new[] { messagingComponent }
                );

            Assert.That(components.Count, Is.EqualTo(1));
            Assert.That(components[0].HierarchyPath, Is.EqualTo("ComponentPanelHost"));
            Assert.That(components[0].ListenerCount, Is.EqualTo(1));
            Assert.That(components[0].EnabledListenerCount, Is.EqualTo(1));
            Assert.That(components[0].DiagnosticsListenerCount, Is.EqualTo(1));
            Assert.That(components[0].RegistrationCount, Is.EqualTo(1));
            Assert.That(components[0].CallCount, Is.EqualTo(1));
            Assert.That(components[0].LocalEmissionCount, Is.GreaterThan(0));
            Assert.That(components[0].ProviderStatusText, Does.Contain("global bus"));
            Assert.That(components[0].WarningText, Is.Empty);

            messagingComponent.EditorResetRuntimeState();
            Assert.That(
                CountMessageBusRegistrations(messageBus),
                Is.EqualTo(initialRegistrationCount)
            );
        }

        [Test]
        public void CaptureComponentSnapshotsFindsSceneComponentsAndSkipsPersistentAssets()
        {
            string suffix = Guid.NewGuid().ToString("N");
            string sceneName = "SceneComponentHost-" + suffix;
            string prefabName = "PrefabComponentHost-" + suffix;
            string prefabPath = $"Assets/{prefabName}.prefab";
            GameObject sceneHost = CreateTrackedObject(sceneName);
            MessagingComponent sceneComponent = sceneHost.AddComponent<MessagingComponent>();
            GameObject prefabHost = new(prefabName);
            _createdAssetPaths.Add(prefabPath);

            try
            {
                prefabHost.AddComponent<MessagingComponent>();
                GameObject prefabAsset = null;
                EditorWindowTestUtility.IgnoreUnityInvalidGcHandleAsserts(() =>
                    prefabAsset = PrefabUtility.SaveAsPrefabAsset(prefabHost, prefabPath)
                );
                Assert.That(prefabAsset, Is.Not.Null);
                Object.DestroyImmediate(prefabHost);
                prefabHost = null;

                MessagingComponent prefabComponent = prefabAsset.GetComponent<MessagingComponent>();
                Assert.That(prefabComponent, Is.Not.Null);
                Assert.That(EditorUtility.IsPersistent(prefabComponent), Is.True);
                MessagingComponent[] unfiltered = Array.Empty<MessagingComponent>();
                EditorWindowTestUtility.IgnoreUnityInvalidGcHandleAsserts(() =>
                    unfiltered = Resources.FindObjectsOfTypeAll<MessagingComponent>()
                );
                Assert.That(unfiltered, Has.Member(sceneComponent));
                Assert.That(unfiltered, Has.Member(prefabComponent));

                IReadOnlyList<ComponentMonitorEntry> components =
                    Array.Empty<ComponentMonitorEntry>();
                EditorWindowTestUtility.IgnoreUnityInvalidGcHandleAsserts(() =>
                    components = DxMessagingMessageMonitorWindow.CaptureComponentSnapshots()
                );

                Assert.That(
                    components.Any(component => component.HierarchyPath == sceneName),
                    Is.True
                );
                Assert.That(
                    components.Any(component => component.HierarchyPath == prefabName),
                    Is.False
                );
            }
            finally
            {
                if (prefabHost != null)
                {
                    Object.DestroyImmediate(prefabHost);
                }
            }
        }

        [Test]
        public void CaptureComponentSnapshotsDoesNotResolveSerializedProviders()
        {
            ThrowingScriptableMessageBusProvider provider = CreateTrackedObject(
                ScriptableObject.CreateInstance<ThrowingScriptableMessageBusProvider>()
            );
            GameObject host = CreateTrackedObject("SerializedProviderHost");
            MessagingComponent messagingComponent = host.AddComponent<MessagingComponent>();
            SerializedObject serializedObject = new(messagingComponent);
            SerializedProperty handleProperty = serializedObject.FindProperty(
                "_serializedProviderHandle"
            );
            Assert.That(handleProperty, Is.Not.Null);
            SerializedProperty providerProperty = handleProperty.FindPropertyRelative("_provider");
            Assert.That(providerProperty, Is.Not.Null);
            providerProperty.objectReferenceValue = provider;
            serializedObject.ApplyModifiedPropertiesWithoutUndo();

            IReadOnlyList<ComponentMonitorEntry> components =
                DxMessagingMessageMonitorWindow.CaptureComponentSnapshots(
                    new[] { messagingComponent }
                );

            Assert.That(components.Count, Is.EqualTo(1));
            Assert.That(provider.ResolveCount, Is.EqualTo(0));
            Assert.That(components[0].ProviderStatusText, Does.Contain("serialized provider"));
            Assert.That(components[0].WarningText, Is.Empty);
        }

        [Test]
        public void CaptureComponentSnapshotsSkipsPreviewSceneComponents()
        {
            string suffix = Guid.NewGuid().ToString("N");
            string sceneName = "MonitorSceneHost-" + suffix;
            string previewName = "MonitorPreviewHost-" + suffix;
            GameObject sceneHost = CreateTrackedObject(sceneName);
            sceneHost.AddComponent<MessagingComponent>();
            Scene previewScene = EditorSceneManager.NewPreviewScene();
            GameObject previewHost = new(previewName);

            try
            {
                SceneManager.MoveGameObjectToScene(previewHost, previewScene);
                MessagingComponent previewComponent =
                    previewHost.AddComponent<MessagingComponent>();
                Assert.That(previewComponent.gameObject.scene.IsValid(), Is.True);
                Assert.That(EditorSceneManager.IsPreviewSceneObject(previewHost), Is.True);

                IReadOnlyList<ComponentMonitorEntry> components =
                    Array.Empty<ComponentMonitorEntry>();
                EditorWindowTestUtility.IgnoreUnityInvalidGcHandleAsserts(() =>
                    components = DxMessagingMessageMonitorWindow.CaptureComponentSnapshots()
                );

                Assert.That(
                    components.Any(component => component.HierarchyPath == sceneName),
                    Is.True
                );
                Assert.That(
                    components.Any(component => component.HierarchyPath == previewName),
                    Is.False
                );
            }
            finally
            {
                if (previewHost != null)
                {
                    Object.DestroyImmediate(previewHost);
                }
                if (previewScene.IsValid())
                {
                    EditorSceneManager.ClosePreviewScene(previewScene);
                }
            }
        }

        [Test]
        public void CaptureComponentSnapshotsReportsPerComponentCaptureFailures()
        {
            GameObject host = CreateTrackedObject("BrokenComponentHost");
            MessagingComponent messagingComponent = host.AddComponent<MessagingComponent>();
            TestListener listener = host.AddComponent<TestListener>();
            messagingComponent._registeredListeners[listener] = null;

            try
            {
                IReadOnlyList<ComponentMonitorEntry> components =
                    DxMessagingMessageMonitorWindow.CaptureComponentSnapshots(
                        new[] { messagingComponent }
                    );

                Assert.That(components.Count, Is.EqualTo(1));
                Assert.That(components[0].HierarchyPath, Is.EqualTo("BrokenComponentHost"));
                Assert.That(components[0].WarningText, Does.Contain("Diagnostics capture failed"));
                Assert.That(components[0].ProviderStatusText, Does.Contain("unavailable"));
            }
            finally
            {
                messagingComponent._registeredListeners.Clear();
            }
        }

        [Test]
        public void CreateExportTextIncludesVisibleSnapshotEntries()
        {
            MessageMonitorEntry newer = CreateEntry(new NewerMessage(), new InstanceId(42));
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { newer }
            );

            string exportText = DxMessagingMessageMonitorWindow.CreateExportText(
                snapshot,
                snapshot.Entries
            );

            Assert.That(exportText, Does.Contain("\"diagnosticsEnabled\": true"));
            Assert.That(exportText, Does.Contain("\"capacity\": 8"));
            Assert.That(exportText, Does.Contain(nameof(NewerMessage)));
            Assert.That(exportText, Does.Contain("42"));
        }

        [Test]
        public void CreateExportTextFiltersVisibleSnapshotEntries()
        {
            MessageMonitorEntry older = CreateEntry(new OlderMessage(), null);
            MessageMonitorEntry newer = CreateEntry(new NewerMessage(), new InstanceId(42));
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { newer, older }
            );

            string exportText = DxMessagingMessageMonitorWindow.CreateExportText(
                snapshot,
                nameof(NewerMessage)
            );

            Assert.That(exportText, Does.Contain("\"entryCount\": 1"));
            Assert.That(exportText, Does.Contain(nameof(NewerMessage)));
            Assert.That(exportText, Does.Not.Contain(nameof(OlderMessage)));
        }

        [Test]
        public void CreateExportTextEscapesJsonStringValues()
        {
            MessageMonitorEntry entry = new(
                "Quote\"Message",
                "Context: slash\\line\nnext",
                "Stack\tTrace\r\u0001"
            );
            MessageMonitorSnapshot snapshot = new(
                diagnosticsEnabled: true,
                capacity: 8,
                entries: new[] { entry }
            );

            string exportText = DxMessagingMessageMonitorWindow.CreateExportText(
                snapshot,
                snapshot.Entries
            );

            Assert.That(exportText, Does.Contain("Quote\\\"Message"));
            Assert.That(exportText, Does.Contain("slash\\\\line\\nnext"));
            Assert.That(exportText, Does.Contain("Stack\\tTrace\\r\\u0001"));
            Assert.That(exportText, Does.Not.Contain("\u0001"));
        }

        [Test]
        public void CaptureSnapshotReadsDefaultMessageBusHistory()
        {
            MessageBus messageBus = new() { DiagnosticsMode = true };
            messageBus._emissionBuffer.Add(new MessageEmissionData(new OlderMessage()));
            messageBus._emissionBuffer.Add(
                new MessageEmissionData(new NewerMessage(), new InstanceId(42))
            );

            MessageMonitorSnapshot snapshot = DxMessagingMessageMonitorWindow.CaptureSnapshot(
                messageBus
            );

            Assert.That(snapshot.DiagnosticsEnabled, Is.True);
            Assert.That(snapshot.Capacity, Is.EqualTo(IMessageBus.GlobalMessageBufferSize));
            Assert.That(snapshot.Entries.Count, Is.EqualTo(2));
            Assert.That(snapshot.Entries[0].MessageTypeName, Is.EqualTo(nameof(NewerMessage)));
            Assert.That(snapshot.Entries[0].ContextText, Does.Contain("42"));
            Assert.That(snapshot.Entries[1].MessageTypeName, Is.EqualTo(nameof(OlderMessage)));
        }

        private static MessageMonitorEntry CreateEntry(IMessage message, InstanceId? context)
        {
            return MessageMonitorEntry.FromEmission(new MessageEmissionData(message, context));
        }

        private static void AssertTaxonomyRow(
            VisualElement row,
            string expectedKind,
            Color expectedColor
        )
        {
            Label kind = row.Q<Label>(DxMessagingMessageMonitorWindow.RouteKindLabelName);
            Assert.That(kind, Is.Not.Null);
            Assert.That(kind.text, Is.EqualTo(expectedKind));
            Assert.That(row.ClassListContains(DxMessagingEditorTheme.CardClassName), Is.True);
            Assert.That(kind.ClassListContains(DxMessagingEditorTheme.TypeBadgeClassName), Is.True);
            Assert.That(kind.ClassListContains(ExpectedTypeBadgeClass(expectedKind)), Is.True);
            AssertCompleteBorder(row, expectedColor);
        }

        private static void AssertCompleteBorder(VisualElement element, Color expectedColor)
        {
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
            AssertColor(element.style.borderTopColor.value, expectedColor);
            AssertColor(element.style.borderRightColor.value, expectedColor);
            AssertColor(element.style.borderBottomColor.value, expectedColor);
            AssertColor(element.style.borderLeftColor.value, expectedColor);
        }

        private static string ExpectedTypeBadgeClass(string routeKind)
        {
            switch (routeKind)
            {
                case DxMessagingEditorPalette.UntargetedKind:
                    return DxMessagingEditorTheme.TypeBadgeUntargetedClassName;
                case DxMessagingEditorPalette.TargetedKind:
                    return DxMessagingEditorTheme.TypeBadgeTargetedClassName;
                case DxMessagingEditorPalette.BroadcastKind:
                    return DxMessagingEditorTheme.TypeBadgeBroadcastClassName;
                default:
                    return string.Empty;
            }
        }

        private static void AssertColor(Color actual, Color expected)
        {
            Assert.That(actual.r, Is.EqualTo(expected.r).Within(0.001f));
            Assert.That(actual.g, Is.EqualTo(expected.g).Within(0.001f));
            Assert.That(actual.b, Is.EqualTo(expected.b).Within(0.001f));
            Assert.That(actual.a, Is.EqualTo(expected.a).Within(0.001f));
        }

        private static void SendClick(VisualElement element)
        {
            Assert.That(element, Is.Not.Null, "Cannot click a missing visual element.");
            using (ClickEvent click = ClickEvent.GetPooled())
            {
                click.target = element;
                element.SendEvent(click);
            }
        }

        private static int CountMessageBusRegistrations(IMessageBus messageBus)
        {
            return messageBus.RegisteredUntargeted
                + messageBus.RegisteredTargeted
                + messageBus.RegisteredBroadcast
                + messageBus.RegisteredInterceptors
                + messageBus.RegisteredPostProcessors
                + messageBus.RegisteredGlobalAcceptAll;
        }

        private GameObject CreateTrackedObject(string name)
        {
            GameObject gameObject = new(name);
            _createdObjects.Add(gameObject);
            return gameObject;
        }

        private T CreateTrackedObject<T>(T unityObject)
            where T : Object
        {
            if (unityObject != null)
            {
                _createdObjects.Add(unityObject);
            }
            return unityObject;
        }

        private EditorWindow CreateTrackedEditorWindow()
        {
            EditorWindow window = EditorWindowTestUtility.CreateWindow();
            _createdWindows.Add(window);
            return window;
        }

        private readonly struct OlderMessage : IUntargetedMessage { }

        private readonly struct NewerMessage : ITargetedMessage { }

        private readonly struct BroadcastMessage : IBroadcastMessage { }

        private static class CollisionOne
        {
            internal readonly struct DuplicateMessage : IUntargetedMessage<DuplicateMessage> { }
        }

        private static class CollisionTwo
        {
            internal readonly struct DuplicateMessage : IUntargetedMessage<DuplicateMessage> { }
        }

        private sealed class TestListener : MonoBehaviour
        {
            public void OnOlderMessage(ref OlderMessage message) { }
        }

        private sealed class ThrowingScriptableMessageBusProvider : ScriptableMessageBusProvider
        {
            public int ResolveCount { get; private set; }

            public override IMessageBus Resolve()
            {
                ResolveCount++;
                throw new InvalidOperationException("Provider resolution should not run here.");
            }
        }
    }
}
#endif
