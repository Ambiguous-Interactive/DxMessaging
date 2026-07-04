#if UNITY_EDITOR
namespace DxMessaging.Editor.Windows
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using Core;
    using Core.Diagnostics;
    using Core.MessageBus;
    using Core.Messages;
    using DxMessaging.Editor;
    using DxMessaging.Editor.Testing;
    using DxMessaging.Unity;
    using UnityEditor;
    using UnityEditor.SceneManagement;
    using UnityEngine;
    using UnityEngine.UIElements;

    public sealed class DxMessagingMessageMonitorWindow : EditorWindow
    {
        internal const string RootClassName = "dxmessaging-monitor";
        internal const string ToolbarClassName = "dxmessaging-monitor-toolbar";
        internal const string RowClassName = "dxmessaging-monitor-row";
        internal const string StatusLabelName = "dxmessaging-monitor-status";
        internal const string FilterFieldName = "dxmessaging-monitor-filter";
        internal const string ActiveFilterSummaryName = "dxmessaging-monitor-active-filter";
        internal const string ActiveFilterSummaryLabelName =
            "dxmessaging-monitor-active-filter-label";
        internal const string ActiveFilterTokenScrollViewName =
            "dxmessaging-monitor-active-filter-token-scroll";
        internal const string ActiveFilterTokenClassName =
            "dxmessaging-monitor-active-filter-token";
        internal const string ActiveFilterClearButtonName =
            "dxmessaging-monitor-active-filter-clear";
        internal const string RefreshButtonName = "dxmessaging-monitor-refresh";
        internal const string ExportButtonName = "dxmessaging-monitor-export";
        internal const string ContentContainerName = "dxmessaging-monitor-content";
        internal const string MessageSectionName = "dxmessaging-monitor-message-section";
        internal const string EmptyStateLabelName = "dxmessaging-monitor-empty";
        internal const string MessageTypeLabelName = "dxmessaging-monitor-message-type";
        internal const string RouteKindLabelName = "dxmessaging-monitor-route-kind";
        internal const string ContextLabelName = "dxmessaging-monitor-context";
        internal const string StackTraceLabelName = "dxmessaging-monitor-stack";
        internal const string DetailsPaneName = "dxmessaging-monitor-details";
        internal const string DetailsTypeLabelName = "dxmessaging-monitor-details-type";
        internal const string DetailsContextLabelName = "dxmessaging-monitor-details-context";
        internal const string DetailsStackTraceLabelName = "dxmessaging-monitor-details-stack";
        internal const string VisibleMessageTypeLanesName =
            "dxmessaging-monitor-message-type-lanes";
        internal const string VisibleMessageTypeLaneScrollViewName =
            "dxmessaging-monitor-message-type-lane-scroll";
        internal const string VisibleMessageTypeLaneRowClassName =
            "dxmessaging-monitor-message-type-lane-row";
        internal const string VisibleMessageTypeLanesSummaryLabelName =
            "dxmessaging-monitor-message-type-lanes-summary";
        internal const string VisibleMessageTypeLaneTypeLabelName =
            "dxmessaging-monitor-message-type-lane-type";
        internal const string VisibleMessageTypeLaneSummaryLabelName =
            "dxmessaging-monitor-message-type-lane-summary";
        internal const string VisibleMessageTypeLaneContextsLabelName =
            "dxmessaging-monitor-message-type-lane-contexts";
        internal const string VisibleMessageTypeLaneFilterButtonName =
            "dxmessaging-monitor-message-type-lane-filter";
        internal const string VisibleContextLanesName = "dxmessaging-monitor-context-lanes";
        internal const string VisibleContextLaneScrollViewName =
            "dxmessaging-monitor-context-lane-scroll";
        internal const string VisibleContextLaneRowClassName =
            "dxmessaging-monitor-context-lane-row";
        internal const string VisibleContextLanesSummaryLabelName =
            "dxmessaging-monitor-context-lanes-summary";
        internal const string VisibleContextLaneContextLabelName =
            "dxmessaging-monitor-context-lane-context";
        internal const string VisibleContextLaneSummaryLabelName =
            "dxmessaging-monitor-context-lane-summary";
        internal const string VisibleContextLaneMessagesLabelName =
            "dxmessaging-monitor-context-lane-messages";
        internal const string VisibleContextLaneFilterButtonName =
            "dxmessaging-monitor-context-lane-filter";
        internal const string ComponentPanelName = "dxmessaging-monitor-components";
        internal const string ComponentScrollViewName = "dxmessaging-monitor-component-scroll";
        internal const string ComponentRowClassName = "dxmessaging-monitor-component-row";
        internal const string ComponentNameLabelName = "dxmessaging-monitor-component-name";
        internal const string ComponentSummaryLabelName = "dxmessaging-monitor-component-summary";
        internal const string ComponentProviderLabelName = "dxmessaging-monitor-component-provider";
        internal const string ComponentWarningLabelName = "dxmessaging-monitor-component-warning";
        internal const string ComponentEmptyStateLabelName = "dxmessaging-monitor-component-empty";

        private const string Title = "Message Monitor";

        private string _filterText = string.Empty;
        private int _selectedEntryIndex;
        private MessageMonitorSnapshot _currentSnapshot = MessageMonitorSnapshot.Unavailable(
            "No message monitor snapshot has been captured yet."
        );
        private IReadOnlyList<ComponentMonitorEntry> _currentComponents =
            Array.Empty<ComponentMonitorEntry>();

        [MenuItem("Tools/Wallstop Studios/DxMessaging/Message Monitor")]
        public static void Open()
        {
            DxMessagingMessageMonitorWindow window = GetWindow<DxMessagingMessageMonitorWindow>();
            window.titleContent = new GUIContent(Title, DxMessagingEditorTheme.LoadIcon());
            window.minSize = new Vector2(420, 320);
            window.Refresh();
        }

        private void CreateGUI()
        {
            titleContent = new GUIContent(Title, DxMessagingEditorTheme.LoadIcon());
            Refresh();
        }

        private void Refresh()
        {
            MessageMonitorSnapshot snapshot = MessageHandler.MessageBus is MessageBus messageBus
                ? CaptureSnapshot(messageBus)
                : MessageMonitorSnapshot.Unavailable(
                    "The active global bus is not the default DxMessaging MessageBus."
                );
            IReadOnlyList<ComponentMonitorEntry> components = CaptureComponentSnapshots();
            _currentSnapshot = snapshot;
            _currentComponents = components;
            BuildMonitorUi(
                rootVisualElement,
                snapshot,
                new MessageMonitorViewState(_filterText, _selectedEntryIndex),
                HandleFilterChanged,
                HandleSelectedEntryChanged,
                Refresh,
                exportText => EditorGUIUtility.systemCopyBuffer = exportText,
                components
            );
        }

        private void HandleFilterChanged(string filterText)
        {
            string normalizedFilterText = filterText ?? string.Empty;
            if (string.Equals(_filterText, normalizedFilterText, StringComparison.Ordinal))
            {
                return;
            }

            _filterText = normalizedFilterText;
            _selectedEntryIndex = 0;
            RefreshCurrentSnapshotContent();
        }

        private void HandleSelectedEntryChanged(int selectedEntryIndex)
        {
            int normalizedSelectedEntryIndex = Math.Max(0, selectedEntryIndex);
            if (_selectedEntryIndex == normalizedSelectedEntryIndex)
            {
                return;
            }

            _selectedEntryIndex = normalizedSelectedEntryIndex;
            RefreshCurrentSnapshotContent();
        }

        private void RefreshCurrentSnapshotContent()
        {
            VisualElement messageSection = rootVisualElement.Q<VisualElement>(MessageSectionName);
            Label status = rootVisualElement.Q<Label>(StatusLabelName);
            if (messageSection == null || status == null)
            {
                Refresh();
                return;
            }

            MessageMonitorViewState viewState = new(_filterText, _selectedEntryIndex);
            IReadOnlyList<MessageMonitorEntry> filteredEntries = FilterEntries(
                _currentSnapshot.Entries,
                viewState.FilterText
            );
            status.text = CreateStatusText(_currentSnapshot, filteredEntries.Count);
            void RequestFilterChange(string filterText)
            {
                TextField filter = rootVisualElement.Q<TextField>(FilterFieldName);
                if (filter != null)
                {
                    filter.value = filterText ?? string.Empty;
                    return;
                }

                HandleFilterChanged(filterText);
            }

            RenderMessageSection(
                messageSection,
                _currentSnapshot,
                filteredEntries,
                viewState,
                HandleSelectedEntryChanged,
                RequestFilterChange
            );
        }

        internal static MessageMonitorSnapshot CaptureSnapshot(MessageBus messageBus)
        {
            if (messageBus == null)
            {
                throw new ArgumentNullException(nameof(messageBus));
            }

            IReadOnlyList<MessageMonitorEntry> entries = messageBus
                ._emissionBuffer.Reverse()
                .Select(MessageMonitorEntry.FromEmission)
                .ToArray();

            return new MessageMonitorSnapshot(
                messageBus.DiagnosticsMode,
                IMessageBus.GlobalMessageBufferSize,
                entries
            );
        }

        internal static void BuildMonitorUi(VisualElement root, MessageMonitorSnapshot snapshot)
        {
            BuildMonitorUi(root, snapshot, MessageMonitorViewState.Default);
        }

        internal static void BuildMonitorUi(
            VisualElement root,
            MessageMonitorSnapshot snapshot,
            IReadOnlyList<ComponentMonitorEntry> componentEntries
        )
        {
            BuildMonitorUi(
                root,
                snapshot,
                MessageMonitorViewState.Default,
                componentEntries: componentEntries
            );
        }

        internal static void BuildMonitorUi(
            VisualElement root,
            MessageMonitorSnapshot snapshot,
            MessageMonitorViewState viewState,
            Action<string> onFilterChanged = null,
            Action<int> onSelectedEntryChanged = null,
            Action onRefresh = null,
            Action<string> onCopyExport = null,
            IReadOnlyList<ComponentMonitorEntry> componentEntries = null
        )
        {
            if (root == null)
            {
                throw new ArgumentNullException(nameof(root));
            }

            root.Clear();
            componentEntries ??= Array.Empty<ComponentMonitorEntry>();
            DxMessagingEditorTheme.ApplyWindow(root);
            root.AddToClassList(RootClassName);
            root.style.paddingTop = 10;
            root.style.paddingRight = 12;
            root.style.paddingBottom = 12;
            root.style.paddingLeft = 12;

            VisualElement toolbar = new();
            toolbar.AddToClassList(ToolbarClassName);
            toolbar.AddToClassList(DxMessagingEditorTheme.ToolbarClassName);
            toolbar.style.flexDirection = FlexDirection.Row;
            toolbar.style.justifyContent = Justify.SpaceBetween;
            toolbar.style.alignItems = Align.Center;
            toolbar.style.marginBottom = 10;

            Label title = new(Title);
            title.style.fontSize = 16;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            toolbar.Add(title);

            IReadOnlyList<MessageMonitorEntry> filteredEntries = FilterEntries(
                snapshot.Entries,
                viewState.FilterText
            );

            Label status = new(CreateStatusText(snapshot, filteredEntries.Count))
            {
                name = StatusLabelName,
            };
            status.style.unityTextAlign = TextAnchor.MiddleRight;
            toolbar.Add(status);
            root.Add(toolbar);

            VisualElement content = new() { name = ContentContainerName };
            content.style.flexGrow = 1;

            if (!snapshot.Available)
            {
                root.Add(content);
                AddEmptyState(content, snapshot.UnavailableReason);
                content.Add(CreateComponentPanel(componentEntries));
                return;
            }

            Action<string> applyFilterText = null;
            void RequestFilterChange(string filterText)
            {
                applyFilterText?.Invoke(filterText);
            }

            void RefreshLocalContent(string filterText)
            {
                IReadOnlyList<MessageMonitorEntry> nextFilteredEntries = FilterEntries(
                    snapshot.Entries,
                    filterText
                );
                status.text = CreateStatusText(snapshot, nextFilteredEntries.Count);
                RenderMonitorContent(
                    content,
                    snapshot,
                    nextFilteredEntries,
                    componentEntries,
                    new MessageMonitorViewState(filterText),
                    onSelectedEntryChanged,
                    RequestFilterChange
                );
            }

            root.Add(
                CreateControlRow(
                    snapshot,
                    viewState,
                    onFilterChanged,
                    onRefresh,
                    onCopyExport,
                    onFilterChanged == null ? RefreshLocalContent : null,
                    out applyFilterText
                )
            );

            root.Add(content);
            RenderMonitorContent(
                content,
                snapshot,
                filteredEntries,
                componentEntries,
                viewState,
                onSelectedEntryChanged,
                RequestFilterChange
            );
        }

        internal static string CreateExportText(MessageMonitorSnapshot snapshot, string filterText)
        {
            return CreateExportText(snapshot, FilterEntries(snapshot.Entries, filterText));
        }

        internal static IReadOnlyList<ComponentMonitorEntry> CaptureComponentSnapshots()
        {
            MessagingComponent[] components = FindMessagingComponentsInLoadedScenes();
            return CaptureComponentSnapshots(components.Where(IsSceneComponent));
        }

        internal static IReadOnlyList<ComponentMonitorEntry> CaptureComponentSnapshots(
            IEnumerable<MessagingComponent> components
        )
        {
            if (components == null)
            {
                throw new ArgumentNullException(nameof(components));
            }

            MessagingComponent[] orderedComponents = components
                .Where(component => component != null)
                .OrderBy(component => GetHierarchyPath(component.transform), StringComparer.Ordinal)
                .ThenBy(component => InstanceId.StableId(component))
                .ToArray();

            List<ComponentMonitorEntry> entries = new(orderedComponents.Length);
            foreach (MessagingComponent component in orderedComponents)
            {
                try
                {
                    entries.Add(CreateComponentMonitorEntry(component));
                }
                catch (Exception exception)
                {
                    entries.Add(CreateFailedComponentMonitorEntry(component, exception));
                }
            }

            return entries;
        }

        internal static string CreateExportText(
            MessageMonitorSnapshot snapshot,
            IReadOnlyList<MessageMonitorEntry> entries
        )
        {
            if (entries == null)
            {
                throw new ArgumentNullException(nameof(entries));
            }

            IReadOnlyList<MessageMonitorEntry> exportEntries = snapshot.DiagnosticsEnabled
                ? entries
                : Array.Empty<MessageMonitorEntry>();

            StringBuilder builder = new();
            builder.AppendLine("{");
            builder
                .Append("  \"diagnosticsEnabled\": ")
                .Append(snapshot.DiagnosticsEnabled ? "true" : "false")
                .AppendLine(",");
            builder.Append("  \"capacity\": ").Append(snapshot.Capacity).AppendLine(",");
            builder.Append("  \"entryCount\": ").Append(exportEntries.Count).AppendLine(",");
            builder.AppendLine("  \"entries\": [");
            for (int i = 0; i < exportEntries.Count; i++)
            {
                MessageMonitorEntry entry = exportEntries[i];
                builder.AppendLine("    {");
                AppendJsonProperty(
                    builder,
                    "messageType",
                    entry.MessageTypeName,
                    trailingComma: true
                );
                AppendJsonProperty(builder, "context", entry.ContextText, trailingComma: true);
                AppendJsonProperty(builder, "stackTrace", entry.StackTrace, trailingComma: false);
                builder.Append("    }");
                if (i < exportEntries.Count - 1)
                {
                    builder.Append(",");
                }
                builder.AppendLine();
            }
            builder.AppendLine("  ]");
            builder.Append("}");
            return builder.ToString();
        }

        private static void RenderMonitorContent(
            VisualElement content,
            MessageMonitorSnapshot snapshot,
            IReadOnlyList<MessageMonitorEntry> filteredEntries,
            IReadOnlyList<ComponentMonitorEntry> componentEntries,
            MessageMonitorViewState viewState,
            Action<int> onSelectedEntryChanged,
            Action<string> onFilterRequested
        )
        {
            if (content == null)
            {
                throw new ArgumentNullException(nameof(content));
            }
            if (filteredEntries == null)
            {
                throw new ArgumentNullException(nameof(filteredEntries));
            }
            if (componentEntries == null)
            {
                throw new ArgumentNullException(nameof(componentEntries));
            }

            content.Clear();
            VisualElement messageSection = new() { name = MessageSectionName };
            messageSection.style.flexGrow = 1;
            content.Add(messageSection);
            RenderMessageSection(
                messageSection,
                snapshot,
                filteredEntries,
                viewState,
                onSelectedEntryChanged,
                onFilterRequested
            );
            content.Add(CreateComponentPanel(componentEntries));
        }

        private static void RenderMessageSection(
            VisualElement messageSection,
            MessageMonitorSnapshot snapshot,
            IReadOnlyList<MessageMonitorEntry> filteredEntries,
            MessageMonitorViewState viewState,
            Action<int> onSelectedEntryChanged,
            Action<string> onFilterRequested
        )
        {
            if (messageSection == null)
            {
                throw new ArgumentNullException(nameof(messageSection));
            }

            messageSection.Clear();

            if (!snapshot.DiagnosticsEnabled)
            {
                AddEmptyState(
                    messageSection,
                    "Diagnostics are Off. Enable diagnostics to collect message history."
                );
                return;
            }

            if (snapshot.Entries.Count == 0)
            {
                AddEmptyState(
                    messageSection,
                    "Diagnostics are On. No messages have been recorded yet."
                );
                return;
            }

            if (filteredEntries.Count == 0)
            {
                AddEmptyState(messageSection, "No messages match the current filter.");
                return;
            }

            messageSection.Add(CreateVisibleMessageTypeLanes(filteredEntries, onFilterRequested));
            messageSection.Add(CreateVisibleContextLanes(filteredEntries, onFilterRequested));

            ScrollView list = new(ScrollViewMode.Vertical);
            list.style.flexGrow = 1;
            int selectedEntryIndex = ClampSelectedIndex(
                viewState.SelectedEntryIndex,
                filteredEntries.Count
            );
            for (int i = 0; i < filteredEntries.Count; i++)
            {
                list.Add(
                    CreateRow(
                        filteredEntries[i],
                        i,
                        i == selectedEntryIndex,
                        onSelectedEntryChanged
                    )
                );
            }
            messageSection.Add(list);
            messageSection.Add(CreateDetailsPane(filteredEntries[selectedEntryIndex]));
        }

        private static IReadOnlyList<MessageMonitorEntry> FilterEntries(
            IReadOnlyList<MessageMonitorEntry> entries,
            string filterText
        )
        {
            if (entries == null)
            {
                throw new ArgumentNullException(nameof(entries));
            }

            if (string.IsNullOrWhiteSpace(filterText))
            {
                return entries;
            }

            return entries.Where(entry => entry.Matches(filterText)).ToArray();
        }

        private static string CreateStatusText(MessageMonitorSnapshot snapshot, int visibleCount)
        {
            if (!snapshot.Available)
            {
                return "Unavailable";
            }
            string enabled = snapshot.DiagnosticsEnabled ? "On" : "Off";
            if (
                snapshot.DiagnosticsEnabled
                && visibleCount >= 0
                && visibleCount != snapshot.Entries.Count
            )
            {
                return $"Diagnostics {enabled} | {visibleCount}/{snapshot.Entries.Count} shown | {snapshot.Entries.Count}/{snapshot.Capacity}";
            }
            return $"Diagnostics {enabled} | {snapshot.Entries.Count}/{snapshot.Capacity}";
        }

        private static VisualElement CreateVisibleMessageTypeLanes(
            IReadOnlyList<MessageMonitorEntry> entries,
            Action<string> onFilterRequested
        )
        {
            MessageMonitorTypeLane[] lanes = BuildVisibleMessageTypeLanes(entries);
            VisualElement lanesRoot = new() { name = VisibleMessageTypeLanesName };
            DxMessagingEditorTheme.ApplyCompleteBorder(
                lanesRoot,
                DxMessagingEditorPalette.BorderPanel
            );
            lanesRoot.style.marginBottom = 8;
            lanesRoot.style.paddingTop = 8;
            lanesRoot.style.paddingRight = 8;
            lanesRoot.style.paddingBottom = 8;
            lanesRoot.style.paddingLeft = 8;

            Label title = new("Visible Message Type Lanes");
            title.AddToClassList(DxMessagingEditorTheme.CardLabelClassName);
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            lanesRoot.Add(title);

            Label summary = new(CreateVisibleMessageTypeLanesSummaryText(lanes))
            {
                name = VisibleMessageTypeLanesSummaryLabelName,
            };
            summary.style.marginTop = 2;
            summary.style.whiteSpace = WhiteSpace.Normal;
            lanesRoot.Add(summary);

            ScrollView laneRows = new(ScrollViewMode.Vertical)
            {
                name = VisibleMessageTypeLaneScrollViewName,
            };
            laneRows.style.maxHeight = 160;
            laneRows.style.marginTop = 2;
            lanesRoot.Add(laneRows);

            int totalEntries = lanes.Sum(lane => lane.EntryCount);
            foreach (MessageMonitorTypeLane lane in lanes)
            {
                laneRows.Add(
                    CreateVisibleMessageTypeLaneRow(lane, totalEntries, onFilterRequested)
                );
            }

            return lanesRoot;
        }

        private static VisualElement CreateVisibleContextLanes(
            IReadOnlyList<MessageMonitorEntry> entries,
            Action<string> onFilterRequested
        )
        {
            MessageMonitorContextLane[] lanes = BuildVisibleContextLanes(entries);
            VisualElement lanesRoot = new() { name = VisibleContextLanesName };
            DxMessagingEditorTheme.ApplyCompleteBorder(
                lanesRoot,
                DxMessagingEditorPalette.BorderPanel
            );
            lanesRoot.style.marginBottom = 8;
            lanesRoot.style.paddingTop = 8;
            lanesRoot.style.paddingRight = 8;
            lanesRoot.style.paddingBottom = 8;
            lanesRoot.style.paddingLeft = 8;

            Label title = new("Visible Context Lanes");
            title.AddToClassList(DxMessagingEditorTheme.CardLabelClassName);
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            lanesRoot.Add(title);

            Label summary = new(CreateVisibleContextLanesSummaryText(lanes))
            {
                name = VisibleContextLanesSummaryLabelName,
            };
            summary.style.marginTop = 2;
            summary.style.whiteSpace = WhiteSpace.Normal;
            lanesRoot.Add(summary);

            ScrollView laneRows = new(ScrollViewMode.Vertical)
            {
                name = VisibleContextLaneScrollViewName,
            };
            laneRows.style.maxHeight = 160;
            laneRows.style.marginTop = 2;
            lanesRoot.Add(laneRows);

            int totalEntries = lanes.Sum(lane => lane.EntryCount);
            foreach (MessageMonitorContextLane lane in lanes)
            {
                laneRows.Add(CreateVisibleContextLaneRow(lane, totalEntries, onFilterRequested));
            }

            return lanesRoot;
        }

        private static VisualElement CreateVisibleMessageTypeLaneRow(
            MessageMonitorTypeLane lane,
            int totalEntries,
            Action<string> onFilterRequested
        )
        {
            VisualElement row = new();
            row.AddToClassList(VisibleMessageTypeLaneRowClassName);
            row.AddToClassList(DxMessagingEditorTheme.CardClassName);
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            DxMessagingEditorTheme.ApplyCompleteBorder(row, DxMessagingEditorPalette.Amber);
            row.style.marginTop = 6;
            row.style.paddingTop = 7;
            row.style.paddingRight = 8;
            row.style.paddingBottom = 7;
            row.style.paddingLeft = 10;

            Label type = new(lane.MessageTypeName) { name = VisibleMessageTypeLaneTypeLabelName };
            type.style.flexBasis = 0;
            type.style.flexGrow = 1;
            type.style.unityFontStyleAndWeight = FontStyle.Bold;
            type.style.whiteSpace = WhiteSpace.Normal;
            row.Add(type);

            Label summary = new(
                $"Entries: {lane.EntryCount} | Contexts: {lane.ContextCount} | Share: {CreateEntryShareText(lane.EntryCount, totalEntries)}"
            )
            {
                name = VisibleMessageTypeLaneSummaryLabelName,
            };
            summary.style.flexBasis = 0;
            summary.style.flexGrow = 2;
            summary.style.marginLeft = 8;
            summary.style.whiteSpace = WhiteSpace.Normal;
            row.Add(summary);

            Label contexts = new($"Contexts: {lane.ContextsText}")
            {
                name = VisibleMessageTypeLaneContextsLabelName,
            };
            contexts.style.flexBasis = 0;
            contexts.style.flexGrow = 3;
            contexts.style.marginLeft = 8;
            contexts.style.whiteSpace = WhiteSpace.Normal;
            row.Add(contexts);

            row.Add(
                CreateLaneFilterButton(
                    VisibleMessageTypeLaneFilterButtonName,
                    CreateMessageTypeLaneFilterText(lane.MessageTypeName),
                    onFilterRequested
                )
            );

            return row;
        }

        private static VisualElement CreateVisibleContextLaneRow(
            MessageMonitorContextLane lane,
            int totalEntries,
            Action<string> onFilterRequested
        )
        {
            VisualElement row = new();
            row.AddToClassList(VisibleContextLaneRowClassName);
            row.AddToClassList(DxMessagingEditorTheme.CardClassName);
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            DxMessagingEditorTheme.ApplyCompleteBorder(row, DxMessagingEditorPalette.AmberSoft);
            row.style.marginTop = 6;
            row.style.paddingTop = 7;
            row.style.paddingRight = 8;
            row.style.paddingBottom = 7;
            row.style.paddingLeft = 10;

            Label context = new(lane.ContextText) { name = VisibleContextLaneContextLabelName };
            context.style.flexBasis = 0;
            context.style.flexGrow = 1;
            context.style.unityFontStyleAndWeight = FontStyle.Bold;
            context.style.whiteSpace = WhiteSpace.Normal;
            row.Add(context);

            Label summary = new(
                $"Entries: {lane.EntryCount} | Message types: {lane.MessageTypeCount} | Share: {CreateEntryShareText(lane.EntryCount, totalEntries)}"
            )
            {
                name = VisibleContextLaneSummaryLabelName,
            };
            summary.style.flexBasis = 0;
            summary.style.flexGrow = 2;
            summary.style.marginLeft = 8;
            summary.style.whiteSpace = WhiteSpace.Normal;
            row.Add(summary);

            Label messages = new($"Messages: {lane.MessageTypesText}")
            {
                name = VisibleContextLaneMessagesLabelName,
            };
            messages.style.flexBasis = 0;
            messages.style.flexGrow = 3;
            messages.style.marginLeft = 8;
            messages.style.whiteSpace = WhiteSpace.Normal;
            row.Add(messages);

            row.Add(
                CreateLaneFilterButton(
                    VisibleContextLaneFilterButtonName,
                    CreateContextLaneFilterText(lane.ContextText),
                    onFilterRequested
                )
            );

            return row;
        }

        private static Button CreateLaneFilterButton(
            string name,
            string filterText,
            Action<string> onFilterRequested
        )
        {
            Button button = new() { name = name, text = "Filter" };
            button.AddToClassList(DxMessagingEditorTheme.ButtonGhostClassName);
            button.tooltip = $"Filter to {filterText}";
            button.SetEnabled(onFilterRequested != null && !string.IsNullOrWhiteSpace(filterText));
            button.RegisterCallback<ClickEvent>(_ => onFilterRequested?.Invoke(filterText));
            button.style.marginLeft = 8;
            button.style.flexShrink = 0;
            return button;
        }

        private static string CreateMessageTypeLaneFilterText(string messageTypeName)
        {
            return $"type:{NormalizeMessageTypeName(messageTypeName)}";
        }

        private static string CreateContextLaneFilterText(string contextText)
        {
            return $"context:{QuoteFilterValue(NormalizeContextText(contextText))}";
        }

        private static string QuoteFilterValue(string value)
        {
            return $"\"{(value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
        }

        private static string CreateVisibleMessageTypeLanesSummaryText(
            IReadOnlyList<MessageMonitorTypeLane> lanes
        )
        {
            int totalEntries = lanes.Sum(lane => lane.EntryCount);
            if (lanes.Count == 0 || totalEntries <= 0 || lanes[0].EntryCount <= 0)
            {
                return $"{FormatCount(lanes.Count, "message type lane")} | Entries: {totalEntries} | Busiest message type: none";
            }

            MessageMonitorTypeLane busiestLane = lanes[0];
            return $"{FormatCount(lanes.Count, "message type lane")} | Entries: {totalEntries} | Busiest message type: {busiestLane.MessageTypeName} | Share: {CreateEntryShareText(busiestLane.EntryCount, totalEntries)}";
        }

        private static string CreateVisibleContextLanesSummaryText(
            IReadOnlyList<MessageMonitorContextLane> lanes
        )
        {
            int totalEntries = lanes.Sum(lane => lane.EntryCount);
            if (lanes.Count == 0 || totalEntries <= 0 || lanes[0].EntryCount <= 0)
            {
                return $"{FormatCount(lanes.Count, "context lane")} | Entries: {totalEntries} | Busiest context: none";
            }

            MessageMonitorContextLane busiestLane = lanes[0];
            return $"{FormatCount(lanes.Count, "context lane")} | Entries: {totalEntries} | Busiest context: {busiestLane.ContextText} | Share: {CreateEntryShareText(busiestLane.EntryCount, totalEntries)}";
        }

        private static MessageMonitorTypeLane[] BuildVisibleMessageTypeLanes(
            IReadOnlyList<MessageMonitorEntry> entries
        )
        {
            var laneGroups = entries
                .GroupBy(entry => NormalizeMessageTypeIdentity(entry))
                .Select(group =>
                {
                    MessageMonitorEntry[] groupEntries = group.ToArray();
                    MessageMonitorEntry firstEntry = groupEntries[0];
                    string[] contexts = groupEntries
                        .Select(entry => entry.ContextText)
                        .Where(context => !string.IsNullOrWhiteSpace(context))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(context => context, StringComparer.Ordinal)
                        .ToArray();

                    return new
                    {
                        MessageTypeName = NormalizeMessageTypeName(firstEntry.MessageTypeName),
                        MessageTypeDisplayPath = NormalizeMessageTypeName(
                            firstEntry.MessageTypeDisplayPath
                        ),
                        EntryCount = groupEntries.Length,
                        Contexts = contexts,
                    };
                })
                .ToArray();

            HashSet<string> duplicateDisplayNames = new(
                laneGroups
                    .GroupBy(group => group.MessageTypeName, StringComparer.Ordinal)
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key),
                StringComparer.Ordinal
            );

            return laneGroups
                .Select(group => new MessageMonitorTypeLane(
                    duplicateDisplayNames.Contains(group.MessageTypeName)
                        ? group.MessageTypeDisplayPath
                        : group.MessageTypeName,
                    group.EntryCount,
                    group.Contexts
                ))
                .OrderByDescending(lane => lane.EntryCount)
                .ThenBy(lane => lane.MessageTypeName, StringComparer.Ordinal)
                .ToArray();
        }

        private static MessageMonitorContextLane[] BuildVisibleContextLanes(
            IReadOnlyList<MessageMonitorEntry> entries
        )
        {
            HashSet<string> duplicateDisplayNames = CreateDuplicateMessageTypeDisplayNames(entries);

            return entries
                .GroupBy(entry => NormalizeContextText(entry.ContextText), StringComparer.Ordinal)
                .Select(group =>
                {
                    MessageMonitorEntry[] groupEntries = group.ToArray();
                    string[] messageTypes = CreateVisibleMessageTypeDisplayNames(
                        groupEntries,
                        duplicateDisplayNames
                    );

                    return new MessageMonitorContextLane(
                        group.Key,
                        groupEntries.Length,
                        messageTypes
                    );
                })
                .OrderByDescending(lane => lane.EntryCount)
                .ThenBy(lane => lane.ContextText, StringComparer.Ordinal)
                .ToArray();
        }

        private static string[] CreateVisibleMessageTypeDisplayNames(
            IReadOnlyList<MessageMonitorEntry> entries,
            HashSet<string> duplicateDisplayNames
        )
        {
            var typeGroups = entries
                .GroupBy(entry => NormalizeMessageTypeIdentity(entry))
                .Select(group =>
                {
                    MessageMonitorEntry firstEntry = group.First();
                    return new
                    {
                        MessageTypeName = NormalizeMessageTypeName(firstEntry.MessageTypeName),
                        MessageTypeDisplayPath = NormalizeMessageTypeName(
                            firstEntry.MessageTypeDisplayPath
                        ),
                    };
                })
                .ToArray();

            return typeGroups
                .Select(group =>
                    duplicateDisplayNames.Contains(group.MessageTypeName)
                        ? group.MessageTypeDisplayPath
                        : group.MessageTypeName
                )
                .OrderBy(messageType => messageType, StringComparer.Ordinal)
                .ToArray();
        }

        private static HashSet<string> CreateDuplicateMessageTypeDisplayNames(
            IReadOnlyList<MessageMonitorEntry> entries
        )
        {
            string[] typeNames = entries
                .GroupBy(entry => NormalizeMessageTypeIdentity(entry))
                .Select(group => NormalizeMessageTypeName(group.First().MessageTypeName))
                .ToArray();

            return new HashSet<string>(
                typeNames
                    .GroupBy(typeName => typeName, StringComparer.Ordinal)
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key),
                StringComparer.Ordinal
            );
        }

        private static string NormalizeMessageTypeName(string messageTypeName)
        {
            return string.IsNullOrWhiteSpace(messageTypeName)
                ? "<unknown>"
                : messageTypeName.Trim();
        }

        private static string NormalizeMessageTypeIdentity(MessageMonitorEntry entry)
        {
            return string.IsNullOrWhiteSpace(entry.MessageTypeIdentity)
                ? NormalizeMessageTypeName(entry.MessageTypeName)
                : entry.MessageTypeIdentity.Trim();
        }

        private static string NormalizeContextText(string contextText)
        {
            return string.IsNullOrWhiteSpace(contextText) ? "Context: none" : contextText.Trim();
        }

        private static string CreateEntryShareText(int count, int total)
        {
            if (total <= 0)
            {
                return $"{count}/{total} (n/a)";
            }

            int percent = (int)
                Math.Round((double)count / total * 100, MidpointRounding.AwayFromZero);
            return $"{count}/{total} ({percent}%)";
        }

        private static string FormatCount(int count, string noun)
        {
            return count == 1 ? $"1 {noun}" : $"{count} {noun}s";
        }

        private static VisualElement CreateControlRow(
            MessageMonitorSnapshot snapshot,
            MessageMonitorViewState viewState,
            Action<string> onFilterChanged,
            Action onRefresh,
            Action<string> onCopyExport,
            Action<string> onLocalFilterChanged,
            out Action<string> applyFilterText
        )
        {
            VisualElement controls = new();
            controls.style.marginBottom = 10;

            VisualElement filterRow = new();
            filterRow.style.flexDirection = FlexDirection.Row;
            filterRow.style.alignItems = Align.Center;
            controls.Add(filterRow);

            TextField filter = new("Filter") { name = FilterFieldName };
            filter.AddToClassList(DxMessagingEditorTheme.SearchClassName);
            filter.SetValueWithoutNotify(viewState.FilterText);
            filter.tooltip =
                "Use plain text, or field filters such as type:, message:, context:, and stack:.";
            filter.style.flexGrow = 1;
            filter.style.marginRight = 8;
            Button export = null;
            VisualElement activeFilter = null;
            void ApplyFilterState(string filterText)
            {
                string normalizedFilterText = filterText ?? string.Empty;
                onFilterChanged?.Invoke(normalizedFilterText);
                onLocalFilterChanged?.Invoke(normalizedFilterText);
                SetExportButtonEnabled(export, snapshot, normalizedFilterText, onCopyExport);
                UpdateActiveFilterSummary(
                    activeFilter,
                    normalizedFilterText,
                    () => ClearFilter(filter, activeFilter, snapshot, onCopyExport, export)
                );
            }

            applyFilterText = filterText =>
            {
                string normalizedFilterText = filterText ?? string.Empty;
                if (filter.panel != null)
                {
                    if (
                        !string.Equals(filter.value, normalizedFilterText, StringComparison.Ordinal)
                    )
                    {
                        filter.value = normalizedFilterText;
                        return;
                    }

                    ApplyFilterState(normalizedFilterText);
                    return;
                }

                filter.SetValueWithoutNotify(normalizedFilterText);
                ApplyFilterState(normalizedFilterText);
            };
            filter.RegisterValueChangedCallback(evt =>
            {
                ApplyFilterState(evt.newValue);
            });
            filterRow.Add(filter);

            Button refresh = new(() => onRefresh?.Invoke())
            {
                name = RefreshButtonName,
                text = "Refresh",
            };
            refresh.AddToClassList(DxMessagingEditorTheme.ToolButtonClassName);
            refresh.SetEnabled(onRefresh != null);
            refresh.style.marginRight = 6;
            filterRow.Add(refresh);

            export = new(() => onCopyExport?.Invoke(CreateExportText(snapshot, filter.value)))
            {
                name = ExportButtonName,
                text = "Copy JSON",
            };
            export.AddToClassList(DxMessagingEditorTheme.ToolButtonClassName);
            SetExportButtonEnabled(export, snapshot, viewState.FilterText, onCopyExport);
            filterRow.Add(export);

            activeFilter = CreateActiveFilterSummary(
                viewState.FilterText,
                () => ClearFilter(filter, activeFilter, snapshot, onCopyExport, export)
            );
            controls.Add(activeFilter);

            return controls;
        }

        private static VisualElement CreateActiveFilterSummary(string filterText, Action onClear)
        {
            VisualElement summary = new() { name = ActiveFilterSummaryName };
            summary.AddToClassList(DxMessagingEditorTheme.CardClassName);
            summary.style.flexDirection = FlexDirection.Row;
            summary.style.alignItems = Align.Center;
            summary.style.marginTop = 6;
            summary.style.paddingTop = 5;
            summary.style.paddingRight = 6;
            summary.style.paddingBottom = 5;
            summary.style.paddingLeft = 8;
            DxMessagingEditorTheme.ApplyCompleteBorder(summary, DxMessagingEditorPalette.Amber);

            UpdateActiveFilterSummary(summary, filterText, onClear);
            return summary;
        }

        private static void UpdateActiveFilterSummary(
            VisualElement summary,
            string filterText,
            Action onClear
        )
        {
            if (summary == null)
            {
                return;
            }

            summary.Clear();
            if (string.IsNullOrWhiteSpace(filterText))
            {
                summary.style.display = DisplayStyle.None;
                return;
            }

            summary.style.display = DisplayStyle.Flex;
            bool typedFilter = MessageMonitorFilterQuery.TryCreateDisplayTokens(
                filterText,
                out string[] displayTokens
            );
            if (!typedFilter)
            {
                displayTokens = new[] { filterText.Trim() };
            }

            Label label = new(typedFilter ? "Active typed filters" : "Active text filter")
            {
                name = ActiveFilterSummaryLabelName,
            };
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.marginRight = 8;
            label.style.flexShrink = 0;
            summary.Add(label);

            Button clear = new() { name = ActiveFilterClearButtonName, text = "Clear" };
            clear.AddToClassList(DxMessagingEditorTheme.ButtonGhostClassName);
            clear.RegisterCallback<ClickEvent>(_ => onClear?.Invoke());
            clear.style.marginRight = 8;
            clear.style.flexShrink = 0;
            summary.Add(clear);

            ScrollView tokenScroll = new(ScrollViewMode.Vertical)
            {
                name = ActiveFilterTokenScrollViewName,
            };
            tokenScroll.style.flexGrow = 1;
            tokenScroll.style.flexShrink = 1;
            tokenScroll.style.maxHeight = 72;
            tokenScroll.contentContainer.style.flexDirection = FlexDirection.Row;
            tokenScroll.contentContainer.style.flexWrap = Wrap.Wrap;
            summary.Add(tokenScroll);

            foreach (string token in displayTokens)
            {
                Label tokenLabel = new(token);
                tokenLabel.AddToClassList(ActiveFilterTokenClassName);
                tokenLabel.style.marginTop = 2;
                tokenLabel.style.marginRight = 6;
                tokenLabel.style.marginBottom = 2;
                tokenLabel.style.paddingTop = 2;
                tokenLabel.style.paddingRight = 5;
                tokenLabel.style.paddingBottom = 2;
                tokenLabel.style.paddingLeft = 5;
                DxMessagingEditorTheme.ApplyCompleteBorder(
                    tokenLabel,
                    DxMessagingEditorPalette.Border
                );
                tokenLabel.style.whiteSpace = WhiteSpace.Normal;
                tokenScroll.Add(tokenLabel);
            }
        }

        private static void ClearFilter(
            TextField filter,
            VisualElement activeFilter,
            MessageMonitorSnapshot snapshot,
            Action<string> onCopyExport,
            Button export
        )
        {
            if (filter == null || string.IsNullOrEmpty(filter.value))
            {
                return;
            }

            if (filter.panel != null)
            {
                filter.value = string.Empty;
                return;
            }

            filter.SetValueWithoutNotify(string.Empty);
            SetExportButtonEnabled(export, snapshot, string.Empty, onCopyExport);
            UpdateActiveFilterSummary(activeFilter, string.Empty, null);
        }

        private static void SetExportButtonEnabled(
            Button export,
            MessageMonitorSnapshot snapshot,
            string filterText,
            Action<string> onCopyExport
        )
        {
            if (export == null)
            {
                return;
            }

            export.SetEnabled(
                onCopyExport != null
                    && snapshot.DiagnosticsEnabled
                    && FilterEntries(snapshot.Entries, filterText).Count > 0
            );
        }

        private static bool IsSceneComponent(MessagingComponent component)
        {
            return component != null
                && component.gameObject != null
                && component.gameObject.scene.IsValid()
                && !EditorSceneManager.IsPreviewSceneObject(component.gameObject)
                && !EditorUtility.IsPersistent(component);
        }

        private static MessagingComponent[] FindMessagingComponentsInLoadedScenes()
        {
#if UNITY_6000_0_OR_NEWER
            return UnityEngine.Object.FindObjectsByType<MessagingComponent>(
                FindObjectsInactive.Include
            );
#elif UNITY_2023_1_OR_NEWER
            return UnityEngine.Object.FindObjectsByType<MessagingComponent>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None
            );
#else
            return UnityEngine.Object.FindObjectsOfType<MessagingComponent>(includeInactive: true);
#endif
        }

        private static ComponentMonitorEntry CreateComponentMonitorEntry(
            MessagingComponent component
        )
        {
            MessagingComponentInspectorState state = MessagingComponentEditorHarness.Capture(
                component,
                resolveSerializedProviderBus: false
            );
            int listenerCount = state.Listeners.Count;
            int enabledListenerCount = state.Listeners.Count(listener => listener.TokenEnabled);
            int diagnosticsListenerCount = state.Listeners.Count(listener =>
                listener.DiagnosticsEnabled
            );
            int registrationCount = state.Listeners.Sum(listener => listener.Registrations.Count);
            int callCount = state.Listeners.Sum(listener =>
                listener.Registrations.Sum(registration => registration.CallCount)
            );
            int localEmissionCount = state.Listeners.Sum(listener =>
                listener.EmissionHistory.Count
            );

            return new ComponentMonitorEntry(
                GetHierarchyPath(component.transform),
                component.GetType().Name,
                component.gameObject.activeInHierarchy,
                listenerCount,
                enabledListenerCount,
                diagnosticsListenerCount,
                registrationCount,
                callCount,
                localEmissionCount,
                CreateProviderStatusText(state.ProviderDiagnostics),
                CreateProviderWarningText(state.ProviderDiagnostics)
            );
        }

        private static ComponentMonitorEntry CreateFailedComponentMonitorEntry(
            MessagingComponent component,
            Exception exception
        )
        {
            return new ComponentMonitorEntry(
                GetHierarchyPath(component != null ? component.transform : null),
                component != null ? component.GetType().Name : "<missing>",
                component != null && component.gameObject.activeInHierarchy,
                listenerCount: 0,
                enabledListenerCount: 0,
                diagnosticsListenerCount: 0,
                registrationCount: 0,
                callCount: 0,
                localEmissionCount: 0,
                providerStatusText: "Provider: unavailable",
                warningText: $"Diagnostics capture failed: {exception}"
            );
        }

        private static string CreateProviderStatusText(ProviderDiagnosticsView providerDiagnostics)
        {
            List<string> states = new();
            if (providerDiagnostics.HasMessageBusOverride)
            {
                states.Add("bus override");
            }
            if (providerDiagnostics.HasRuntimeProvider)
            {
                states.Add("runtime provider");
            }
            if (providerDiagnostics.HasSerializedProvider)
            {
                states.Add("serialized provider");
            }
            if (states.Count == 0)
            {
                states.Add("global bus");
            }
            if (providerDiagnostics.AutoConfigureSerializedProviderOnAwake)
            {
                states.Add("auto-configure");
            }

            return "Provider: " + string.Join(", ", states);
        }

        private static string CreateProviderWarningText(ProviderDiagnosticsView providerDiagnostics)
        {
            List<string> warnings = new();
            if (providerDiagnostics.SerializedProviderMissingWarning)
            {
                warnings.Add("Serialized provider missing");
            }
            if (providerDiagnostics.SerializedProviderNullBusWarning)
            {
                warnings.Add("Serialized provider resolves no bus");
            }

            return string.Join("; ", warnings);
        }

        private static string GetHierarchyPath(Transform transform)
        {
            if (transform == null)
            {
                return "<missing>";
            }

            Stack<string> segments = new();
            Transform current = transform;
            while (current != null)
            {
                segments.Push(current.name);
                current = current.parent;
            }

            return string.Join("/", segments);
        }

        private static void AddEmptyState(VisualElement root, string text)
        {
            Label empty = new(text) { name = EmptyStateLabelName };
            empty.AddToClassList(DxMessagingEditorTheme.EmptyBodyClassName);
            empty.style.whiteSpace = WhiteSpace.Normal;
            empty.style.marginTop = 8;
            root.Add(empty);
        }

        private static VisualElement CreateRow(
            MessageMonitorEntry entry,
            int entryIndex,
            bool selected,
            Action<int> onSelectedEntryChanged
        )
        {
            VisualElement row = new();
            row.AddToClassList(RowClassName);
            row.AddToClassList(DxMessagingEditorTheme.CardClassName);
            Color routeColor = DxMessagingEditorPalette.RouteKindColor(entry.RouteKind);
            DxMessagingEditorTheme.ApplyCompleteBorder(row, routeColor);
            row.style.marginBottom = 8;
            row.style.paddingTop = 8;
            row.style.paddingRight = 8;
            row.style.paddingBottom = 8;
            row.style.paddingLeft = 10;
            if (selected)
            {
                row.style.backgroundColor = DxMessagingEditorPalette.SelectedWash;
            }
            if (onSelectedEntryChanged != null)
            {
                row.RegisterCallback<ClickEvent>(_ => onSelectedEntryChanged.Invoke(entryIndex));
            }

            Label type = new(entry.MessageTypeName) { name = MessageTypeLabelName };
            type.style.unityFontStyleAndWeight = FontStyle.Bold;
            row.Add(type);

            string routeKind = DxMessagingEditorPalette.NormalizeRouteKind(entry.RouteKind);
            if (!string.IsNullOrWhiteSpace(routeKind))
            {
                Label kind = new(routeKind) { name = RouteKindLabelName };
                DxMessagingEditorTheme.AddRouteKindTypeBadgeClasses(kind, routeKind);
                kind.style.marginTop = 2;
                kind.style.unityFontStyleAndWeight = FontStyle.Bold;
                row.Add(kind);
            }

            Label context = new(entry.ContextText) { name = ContextLabelName };
            context.style.marginTop = 2;
            row.Add(context);

            if (!string.IsNullOrWhiteSpace(entry.StackTrace))
            {
                Label stack = new(entry.StackTrace) { name = StackTraceLabelName };
                stack.style.marginTop = 6;
                stack.style.whiteSpace = WhiteSpace.Normal;
                row.Add(stack);
            }

            return row;
        }

        private static VisualElement CreateComponentPanel(
            IReadOnlyList<ComponentMonitorEntry> componentEntries
        )
        {
            VisualElement panel = new() { name = ComponentPanelName };
            DxMessagingEditorTheme.ApplyCompleteBorder(panel, DxMessagingEditorPalette.BorderPanel);
            panel.style.marginTop = 10;
            panel.style.paddingTop = 8;
            panel.style.paddingRight = 8;
            panel.style.paddingBottom = 8;
            panel.style.paddingLeft = 8;

            Label title = new($"Component Diagnostics ({componentEntries.Count})");
            title.AddToClassList(DxMessagingEditorTheme.CardLabelClassName);
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            panel.Add(title);

            if (componentEntries.Count == 0)
            {
                Label empty = new("No MessagingComponent instances are loaded in open scenes.")
                {
                    name = ComponentEmptyStateLabelName,
                };
                empty.AddToClassList(DxMessagingEditorTheme.EmptyBodyClassName);
                empty.style.whiteSpace = WhiteSpace.Normal;
                empty.style.marginTop = 6;
                panel.Add(empty);
                return panel;
            }

            ScrollView componentScroll = new(ScrollViewMode.Vertical)
            {
                name = ComponentScrollViewName,
            };
            componentScroll.style.maxHeight = 180;
            componentScroll.style.marginTop = 2;
            panel.Add(componentScroll);

            foreach (ComponentMonitorEntry componentEntry in componentEntries)
            {
                componentScroll.Add(CreateComponentRow(componentEntry));
            }

            return panel;
        }

        private static VisualElement CreateComponentRow(ComponentMonitorEntry componentEntry)
        {
            VisualElement row = new();
            row.AddToClassList(ComponentRowClassName);
            row.AddToClassList(DxMessagingEditorTheme.CardClassName);
            DxMessagingEditorTheme.ApplyCompleteBorder(row, DxMessagingEditorPalette.Amber);
            row.style.marginTop = 8;
            row.style.paddingTop = 8;
            row.style.paddingRight = 8;
            row.style.paddingBottom = 8;
            row.style.paddingLeft = 10;

            string activeText = componentEntry.ActiveInHierarchy ? "active" : "inactive";
            Label name = new(
                $"{componentEntry.HierarchyPath} ({componentEntry.ComponentTypeName}, {activeText})"
            )
            {
                name = ComponentNameLabelName,
            };
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            row.Add(name);

            Label summary = new(
                $"Listeners: {componentEntry.ListenerCount} ({componentEntry.EnabledListenerCount} enabled, {componentEntry.DiagnosticsListenerCount} diagnostics) | Registrations: {componentEntry.RegistrationCount} | Calls: {componentEntry.CallCount} | Local messages: {componentEntry.LocalEmissionCount}"
            )
            {
                name = ComponentSummaryLabelName,
            };
            summary.style.marginTop = 2;
            summary.style.whiteSpace = WhiteSpace.Normal;
            row.Add(summary);

            Label provider = new(componentEntry.ProviderStatusText)
            {
                name = ComponentProviderLabelName,
            };
            provider.style.marginTop = 2;
            provider.style.whiteSpace = WhiteSpace.Normal;
            row.Add(provider);

            if (!string.IsNullOrWhiteSpace(componentEntry.WarningText))
            {
                Label warning = new(componentEntry.WarningText)
                {
                    name = ComponentWarningLabelName,
                };
                warning.AddToClassList(DxMessagingEditorTheme.WarningClassName);
                warning.style.marginTop = 4;
                warning.style.whiteSpace = WhiteSpace.Normal;
                row.Add(warning);
            }

            return row;
        }

        private static VisualElement CreateDetailsPane(MessageMonitorEntry entry)
        {
            VisualElement details = new() { name = DetailsPaneName };
            details.AddToClassList(DxMessagingEditorTheme.CardClassName);
            details.style.borderTopWidth = 1;
            details.style.borderTopColor = DxMessagingEditorPalette.BorderPanel;
            details.style.marginTop = 8;
            details.style.paddingTop = 8;

            Label title = new("Details");
            title.AddToClassList(DxMessagingEditorTheme.CardLabelClassName);
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            details.Add(title);

            Label type = new($"Message: {entry.MessageTypeName}") { name = DetailsTypeLabelName };
            type.style.marginTop = 4;
            details.Add(type);

            Label context = new(entry.ContextText) { name = DetailsContextLabelName };
            context.style.marginTop = 2;
            details.Add(context);

            string stackText = string.IsNullOrWhiteSpace(entry.StackTrace)
                ? "Stack trace: not captured"
                : entry.StackTrace;
            Label stack = new(stackText) { name = DetailsStackTraceLabelName };
            stack.style.marginTop = 6;
            stack.style.whiteSpace = WhiteSpace.Normal;
            details.Add(stack);

            return details;
        }

        private static int ClampSelectedIndex(int selectedEntryIndex, int entryCount)
        {
            if (entryCount <= 0)
            {
                return 0;
            }
            if (selectedEntryIndex < 0)
            {
                return 0;
            }
            return selectedEntryIndex >= entryCount ? entryCount - 1 : selectedEntryIndex;
        }

        private static void AppendJsonProperty(
            StringBuilder builder,
            string name,
            string value,
            bool trailingComma
        )
        {
            builder
                .Append("      \"")
                .Append(name)
                .Append("\": \"")
                .Append(EscapeJson(value))
                .Append("\"");
            if (trailingComma)
            {
                builder.Append(",");
            }
            builder.AppendLine();
        }

        private static string EscapeJson(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            StringBuilder builder = new(value.Length + 8);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (char.IsControl(c))
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            builder.Append(c);
                        }
                        break;
                }
            }
            return builder.ToString();
        }
    }

    internal readonly struct MessageMonitorViewState
    {
        internal static MessageMonitorViewState Default { get; } = new();

        internal MessageMonitorViewState(string filterText = "", int selectedEntryIndex = 0)
        {
            FilterText = filterText ?? string.Empty;
            SelectedEntryIndex = selectedEntryIndex;
        }

        internal string FilterText { get; }

        internal int SelectedEntryIndex { get; }
    }

    internal readonly struct ComponentMonitorEntry
    {
        internal ComponentMonitorEntry(
            string hierarchyPath,
            string componentTypeName,
            bool activeInHierarchy,
            int listenerCount,
            int enabledListenerCount,
            int diagnosticsListenerCount,
            int registrationCount,
            int callCount,
            int localEmissionCount,
            string providerStatusText,
            string warningText
        )
        {
            HierarchyPath = hierarchyPath ?? string.Empty;
            ComponentTypeName = componentTypeName ?? string.Empty;
            ActiveInHierarchy = activeInHierarchy;
            ListenerCount = listenerCount;
            EnabledListenerCount = enabledListenerCount;
            DiagnosticsListenerCount = diagnosticsListenerCount;
            RegistrationCount = registrationCount;
            CallCount = callCount;
            LocalEmissionCount = localEmissionCount;
            ProviderStatusText = providerStatusText ?? string.Empty;
            WarningText = warningText ?? string.Empty;
        }

        internal string HierarchyPath { get; }

        internal string ComponentTypeName { get; }

        internal bool ActiveInHierarchy { get; }

        internal int ListenerCount { get; }

        internal int EnabledListenerCount { get; }

        internal int DiagnosticsListenerCount { get; }

        internal int RegistrationCount { get; }

        internal int CallCount { get; }

        internal int LocalEmissionCount { get; }

        internal string ProviderStatusText { get; }

        internal string WarningText { get; }
    }

    internal readonly struct MessageMonitorTypeLane
    {
        internal MessageMonitorTypeLane(
            string messageTypeName,
            int entryCount,
            IReadOnlyList<string> contexts
        )
        {
            MessageTypeName = string.IsNullOrWhiteSpace(messageTypeName)
                ? "<unknown>"
                : messageTypeName.Trim();
            EntryCount = entryCount;
            Contexts = contexts ?? Array.Empty<string>();
        }

        internal string MessageTypeName { get; }

        internal int EntryCount { get; }

        internal IReadOnlyList<string> Contexts { get; }

        internal int ContextCount => Contexts.Count;

        internal string ContextsText => Contexts.Count == 0 ? "none" : string.Join(", ", Contexts);
    }

    internal readonly struct MessageMonitorContextLane
    {
        internal MessageMonitorContextLane(
            string contextText,
            int entryCount,
            IReadOnlyList<string> messageTypes
        )
        {
            ContextText = string.IsNullOrWhiteSpace(contextText)
                ? "Context: none"
                : contextText.Trim();
            EntryCount = entryCount;
            MessageTypes = messageTypes ?? Array.Empty<string>();
        }

        internal string ContextText { get; }

        internal int EntryCount { get; }

        internal IReadOnlyList<string> MessageTypes { get; }

        internal int MessageTypeCount => MessageTypes.Count;

        internal string MessageTypesText =>
            MessageTypes.Count == 0 ? "none" : string.Join(", ", MessageTypes);
    }

    internal readonly struct MessageMonitorSnapshot
    {
        internal MessageMonitorSnapshot(
            bool diagnosticsEnabled,
            int capacity,
            IReadOnlyList<MessageMonitorEntry> entries,
            bool available = true,
            string unavailableReason = ""
        )
        {
            DiagnosticsEnabled = diagnosticsEnabled;
            Capacity = capacity;
            Entries = entries ?? throw new ArgumentNullException(nameof(entries));
            Available = available;
            UnavailableReason = unavailableReason ?? string.Empty;
        }

        internal bool Available { get; }

        internal bool DiagnosticsEnabled { get; }

        internal int Capacity { get; }

        internal IReadOnlyList<MessageMonitorEntry> Entries { get; }

        internal string UnavailableReason { get; }

        internal static MessageMonitorSnapshot Unavailable(string reason)
        {
            return new MessageMonitorSnapshot(
                diagnosticsEnabled: false,
                capacity: 0,
                entries: Array.Empty<MessageMonitorEntry>(),
                available: false,
                unavailableReason: reason
            );
        }
    }

    internal readonly struct MessageMonitorEntry
    {
        private const string EmptyContextText = "Context: none";

        internal MessageMonitorEntry(
            string messageTypeName,
            string contextText,
            string stackTrace,
            string messageTypeIdentity = null,
            string messageTypeDisplayPath = null,
            string routeKind = null
        )
        {
            MessageTypeName = messageTypeName;
            MessageTypeIdentity = string.IsNullOrWhiteSpace(messageTypeIdentity)
                ? messageTypeName
                : messageTypeIdentity;
            MessageTypeDisplayPath = string.IsNullOrWhiteSpace(messageTypeDisplayPath)
                ? messageTypeName
                : messageTypeDisplayPath;
            ContextText = contextText;
            StackTrace = stackTrace;
            RouteKind = routeKind ?? string.Empty;
        }

        internal string MessageTypeName { get; }

        internal string MessageTypeIdentity { get; }

        internal string MessageTypeDisplayPath { get; }

        internal string ContextText { get; }

        internal string StackTrace { get; }

        internal string RouteKind { get; }

        internal static MessageMonitorEntry FromEmission(MessageEmissionData emission)
        {
            Type messageType = emission.message?.MessageType;
            string typeName = messageType == null ? "<unknown>" : messageType.Name;
            string typeIdentity = CreateMessageTypeIdentity(messageType, typeName);
            string typeDisplayPath = CreateMessageTypeDisplayPath(messageType, typeName);
            string contextText = FormatContext(emission.context);
            return new MessageMonitorEntry(
                typeName,
                contextText,
                emission.stackTrace ?? string.Empty,
                typeIdentity,
                typeDisplayPath,
                CreateRouteKind(messageType)
            );
        }

        private static string CreateRouteKind(Type messageType)
        {
            if (messageType == null)
            {
                return string.Empty;
            }
            if (typeof(IUntargetedMessage).IsAssignableFrom(messageType))
            {
                return DxMessagingEditorPalette.UntargetedKind;
            }
            if (typeof(ITargetedMessage).IsAssignableFrom(messageType))
            {
                return DxMessagingEditorPalette.TargetedKind;
            }
            if (typeof(IBroadcastMessage).IsAssignableFrom(messageType))
            {
                return DxMessagingEditorPalette.BroadcastKind;
            }
            return string.Empty;
        }

        internal bool Matches(string filterText)
        {
            if (string.IsNullOrWhiteSpace(filterText))
            {
                return true;
            }

            if (
                !MessageMonitorFilterQuery.TryCreateTerms(
                    filterText,
                    out MessageMonitorFilterTerm[] terms
                )
            )
            {
                return ContainsAnyField(filterText);
            }

            return terms.All(MatchesFilterTerm);
        }

        private bool MatchesFilterTerm(MessageMonitorFilterTerm term)
        {
            if (string.IsNullOrWhiteSpace(term.Text))
            {
                return true;
            }

            switch (term.Facet)
            {
                case MessageMonitorFilterFacet.MessageType:
                    return Contains(MessageTypeName, term.Text)
                        || Contains(MessageTypeDisplayPath, term.Text);
                case MessageMonitorFilterFacet.Context:
                    if (term.Exact)
                    {
                        return string.Equals(
                            ContextText,
                            term.Text,
                            StringComparison.OrdinalIgnoreCase
                        );
                    }

                    return Contains(ContextText, term.Text);
                case MessageMonitorFilterFacet.Stack:
                    return Contains(StackTrace, term.Text);
                default:
                    return ContainsAnyField(term.Text);
            }
        }

        private bool ContainsAnyField(string filterText)
        {
            return Contains(MessageTypeName, filterText)
                || Contains(MessageTypeDisplayPath, filterText)
                || Contains(ContextText, filterText)
                || Contains(StackTrace, filterText);
        }

        private static string CreateMessageTypeIdentity(Type messageType, string fallback)
        {
            if (messageType == null)
            {
                return fallback;
            }

            return messageType.AssemblyQualifiedName ?? messageType.FullName ?? fallback;
        }

        private static string CreateMessageTypeDisplayPath(Type messageType, string fallback)
        {
            if (messageType == null)
            {
                return fallback;
            }

            return (messageType.FullName ?? fallback).Replace('+', '.');
        }

        private static bool Contains(string value, string filterText)
        {
            return !string.IsNullOrEmpty(value)
                && value.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string FormatContext(InstanceId? context)
        {
            if (!context.HasValue)
            {
                return EmptyContextText;
            }

            InstanceId instanceId = context.Value;
#if UNITY_2021_3_OR_NEWER
            UnityEngine.Object unityObject = instanceId.Object;
            if (unityObject != null)
            {
                return $"Context: {unityObject.name} ({instanceId.Id})";
            }
#endif
            return $"Context: {instanceId.Id}";
        }
    }

    internal enum MessageMonitorFilterFacet
    {
        Any,
        MessageType,
        Context,
        Stack,
    }

    internal static class MessageMonitorFilterQuery
    {
        internal static bool TryCreateTerms(string filterText, out MessageMonitorFilterTerm[] terms)
        {
            string[] tokens = SplitFilterTokens(filterText);
            if (tokens.Length == 0)
            {
                terms = Array.Empty<MessageMonitorFilterTerm>();
                return false;
            }

            List<MessageMonitorFilterTerm> parsedTerms = new(tokens.Length);
            foreach (string token in tokens)
            {
                if (!TryCreateFilterTerm(token, out MessageMonitorFilterTerm term, out _))
                {
                    terms = Array.Empty<MessageMonitorFilterTerm>();
                    return false;
                }

                parsedTerms.Add(term);
            }

            terms = parsedTerms.ToArray();
            return true;
        }

        internal static bool TryCreateDisplayTokens(string filterText, out string[] displayTokens)
        {
            string[] tokens = SplitFilterTokens(filterText);
            if (tokens.Length == 0)
            {
                displayTokens = Array.Empty<string>();
                return false;
            }

            List<string> parsedTokens = new(tokens.Length);
            foreach (string token in tokens)
            {
                if (!TryCreateFilterTerm(token, out _, out string displayToken))
                {
                    displayTokens = Array.Empty<string>();
                    return false;
                }

                parsedTokens.Add(displayToken);
            }

            displayTokens = parsedTokens.ToArray();
            return true;
        }

        private static string[] SplitFilterTokens(string filterText)
        {
            string source = filterText ?? string.Empty;
            if (source.Length == 0)
            {
                return Array.Empty<string>();
            }

            List<string> tokens = new();
            StringBuilder current = new();
            bool quoted = false;
            bool escaped = false;

            foreach (char character in source)
            {
                if (escaped)
                {
                    current.Append(character);
                    escaped = false;
                    continue;
                }

                if (quoted && character == '\\')
                {
                    current.Append(character);
                    escaped = true;
                    continue;
                }

                if (character == '"')
                {
                    quoted = !quoted;
                    current.Append(character);
                    continue;
                }

                if (!quoted && char.IsWhiteSpace(character))
                {
                    AddCurrentToken(tokens, current);
                    continue;
                }

                current.Append(character);
            }

            AddCurrentToken(tokens, current);
            return tokens.ToArray();
        }

        private static void AddCurrentToken(List<string> tokens, StringBuilder current)
        {
            if (current.Length == 0)
            {
                return;
            }

            tokens.Add(current.ToString());
            current.Clear();
        }

        private static bool TryCreateFilterTerm(
            string token,
            out MessageMonitorFilterTerm term,
            out string displayToken
        )
        {
            int separatorIndex = token.IndexOf(':');
            if (separatorIndex <= 0)
            {
                term = default;
                displayToken = string.Empty;
                return false;
            }

            string prefix = token.Substring(0, separatorIndex);
            string rawValue = token.Substring(separatorIndex + 1);
            if (
                !TryCreateFilterValue(rawValue, out string value, out bool exact)
                || !TryCreateFilterFacet(prefix, out MessageMonitorFilterFacet facet)
            )
            {
                term = default;
                displayToken = string.Empty;
                return false;
            }

            term = new MessageMonitorFilterTerm(facet, value, exact);
            displayToken =
                $"{CreateFacetDisplayPrefix(prefix, facet)}:{CreateFilterValueDisplay(value, exact)}";
            return true;
        }

        private static bool TryCreateFilterValue(string rawValue, out string value, out bool exact)
        {
            string trimmedValue = rawValue?.Trim() ?? string.Empty;
            value = string.Empty;
            exact = false;
            if (string.IsNullOrWhiteSpace(trimmedValue))
            {
                return false;
            }

            bool startsQuoted = trimmedValue[0] == '"';
            bool endsQuoted = trimmedValue[trimmedValue.Length - 1] == '"';
            if (startsQuoted || endsQuoted)
            {
                if (!startsQuoted || !endsQuoted || trimmedValue.Length < 2)
                {
                    return false;
                }

                exact = true;
                return TryUnescapeQuotedFilterValue(
                        trimmedValue.Substring(1, trimmedValue.Length - 2),
                        out value
                    ) && !string.IsNullOrWhiteSpace(value);
            }

            value = trimmedValue;
            return true;
        }

        private static bool TryUnescapeQuotedFilterValue(string quotedValue, out string value)
        {
            StringBuilder builder = new();
            bool escaped = false;

            foreach (char character in quotedValue ?? string.Empty)
            {
                if (escaped)
                {
                    builder.Append(character);
                    escaped = false;
                    continue;
                }

                if (character == '\\')
                {
                    escaped = true;
                    continue;
                }

                builder.Append(character);
            }

            value = builder.ToString();
            return !escaped;
        }

        private static string CreateFilterValueDisplay(string value, bool exact)
        {
            if (!exact && !RequiresQuotedFilterValue(value))
            {
                return value;
            }

            return $"\"{EscapeQuotedFilterValue(value)}\"";
        }

        private static bool RequiresQuotedFilterValue(string value)
        {
            return (value ?? string.Empty).Any(char.IsWhiteSpace);
        }

        private static string EscapeQuotedFilterValue(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static bool TryCreateFilterFacet(string prefix, out MessageMonitorFilterFacet facet)
        {
            switch (prefix?.Trim().ToLowerInvariant())
            {
                case "type":
                case "message":
                    facet = MessageMonitorFilterFacet.MessageType;
                    return true;
                case "context":
                    facet = MessageMonitorFilterFacet.Context;
                    return true;
                case "stack":
                    facet = MessageMonitorFilterFacet.Stack;
                    return true;
                default:
                    facet = MessageMonitorFilterFacet.Any;
                    return false;
            }
        }

        private static string CreateFacetDisplayPrefix(
            string prefix,
            MessageMonitorFilterFacet facet
        )
        {
            string normalizedPrefix = prefix?.Trim().ToLowerInvariant();
            if (string.Equals(normalizedPrefix, "message", StringComparison.Ordinal))
            {
                return "message";
            }

            switch (facet)
            {
                case MessageMonitorFilterFacet.MessageType:
                    return "type";
                case MessageMonitorFilterFacet.Context:
                    return "context";
                case MessageMonitorFilterFacet.Stack:
                    return "stack";
                default:
                    return normalizedPrefix ?? string.Empty;
            }
        }
    }

    internal readonly struct MessageMonitorFilterTerm
    {
        internal MessageMonitorFilterTerm(MessageMonitorFilterFacet facet, string text, bool exact)
        {
            Facet = facet;
            Text = text ?? string.Empty;
            Exact = exact;
        }

        internal MessageMonitorFilterFacet Facet { get; }

        internal string Text { get; }

        internal bool Exact { get; }
    }
}
#endif
