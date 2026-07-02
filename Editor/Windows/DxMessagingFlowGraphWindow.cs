#if UNITY_EDITOR
namespace DxMessaging.Editor.Windows
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Text;
    using Core;
    using Core.Diagnostics;
    using DxMessaging.Editor;
    using DxMessaging.Editor.Testing;
    using DxMessaging.Unity;
    using UnityEditor;
    using UnityEditor.SceneManagement;
    using UnityEngine;
    using UnityEngine.UIElements;

    public sealed class DxMessagingFlowGraphWindow : EditorWindow
    {
        internal const string RootClassName = "dxmessaging-flow-graph";
        internal const string ToolbarClassName = "dxmessaging-flow-graph-toolbar";
        internal const string StatusLabelName = "dxmessaging-flow-graph-status";
        internal const string FilterFieldName = "dxmessaging-flow-graph-filter";
        internal const string RefreshButtonName = "dxmessaging-flow-graph-refresh";
        internal const string ExportButtonName = "dxmessaging-flow-graph-export";
        internal const string ContentName = "dxmessaging-flow-graph-content";
        internal const string EmptyStateLabelName = "dxmessaging-flow-graph-empty";
        internal const string RouteMapName = "dxmessaging-flow-graph-route-map";
        internal const string RouteMapRouteClassName = "dxmessaging-flow-graph-route-map-route";
        internal const string RouteMapMessageLabelName = "dxmessaging-flow-graph-route-map-message";
        internal const string RouteMapTargetLabelName = "dxmessaging-flow-graph-route-map-target";
        internal const string RouteMapSummaryLabelName = "dxmessaging-flow-graph-route-map-summary";
        internal const string VisibleMessageLanesName = "dxmessaging-flow-graph-message-lanes";
        internal const string VisibleMessageLaneRowClassName =
            "dxmessaging-flow-graph-message-lane-row";
        internal const string VisibleMessageLaneMessageLabelName =
            "dxmessaging-flow-graph-message-lane-message";
        internal const string VisibleMessageLanesSummaryLabelName =
            "dxmessaging-flow-graph-message-lanes-summary";
        internal const string VisibleMessageLaneSummaryLabelName =
            "dxmessaging-flow-graph-message-lane-summary";
        internal const string VisibleMessageLaneTargetsLabelName =
            "dxmessaging-flow-graph-message-lane-targets";
        internal const string VisibleTargetLanesName = "dxmessaging-flow-graph-target-lanes";
        internal const string VisibleTargetLaneRowClassName =
            "dxmessaging-flow-graph-target-lane-row";
        internal const string VisibleTargetLaneTargetLabelName =
            "dxmessaging-flow-graph-target-lane-target";
        internal const string VisibleTargetLanesSummaryLabelName =
            "dxmessaging-flow-graph-target-lanes-summary";
        internal const string VisibleTargetLaneSummaryLabelName =
            "dxmessaging-flow-graph-target-lane-summary";
        internal const string VisibleTargetLaneMessagesLabelName =
            "dxmessaging-flow-graph-target-lane-messages";
        internal const string VisibleFlowCorridorsName = "dxmessaging-flow-graph-flow-corridors";
        internal const string VisibleFlowCorridorRowClassName =
            "dxmessaging-flow-graph-flow-corridor-row";
        internal const string VisibleFlowCorridorMessageLabelName =
            "dxmessaging-flow-graph-flow-corridor-message";
        internal const string VisibleFlowCorridorsSummaryLabelName =
            "dxmessaging-flow-graph-flow-corridors-summary";
        internal const string VisibleFlowCorridorSummaryLabelName =
            "dxmessaging-flow-graph-flow-corridor-summary";
        internal const string VisibleFlowCorridorTargetLabelName =
            "dxmessaging-flow-graph-flow-corridor-target";
        internal const string VisibleContextLanesName = "dxmessaging-flow-graph-context-lanes";
        internal const string VisibleContextLaneRowClassName =
            "dxmessaging-flow-graph-context-lane-row";
        internal const string VisibleContextLaneContextLabelName =
            "dxmessaging-flow-graph-context-lane-context";
        internal const string VisibleContextLanesSummaryLabelName =
            "dxmessaging-flow-graph-context-lanes-summary";
        internal const string VisibleContextLaneSummaryLabelName =
            "dxmessaging-flow-graph-context-lane-summary";
        internal const string VisibleContextLaneDetailsLabelName =
            "dxmessaging-flow-graph-context-lane-details";
        internal const string VisibleTraceMessageLanesName =
            "dxmessaging-flow-graph-trace-message-lanes";
        internal const string VisibleTraceMessageLaneRowClassName =
            "dxmessaging-flow-graph-trace-message-lane-row";
        internal const string VisibleTraceMessageLaneMessageLabelName =
            "dxmessaging-flow-graph-trace-message-lane-message";
        internal const string VisibleTraceMessageLanesSummaryLabelName =
            "dxmessaging-flow-graph-trace-message-lanes-summary";
        internal const string VisibleTraceMessageLaneSummaryLabelName =
            "dxmessaging-flow-graph-trace-message-lane-summary";
        internal const string VisibleTraceMessageLaneDetailsLabelName =
            "dxmessaging-flow-graph-trace-message-lane-details";
        internal const string VisibleTraceTargetLanesName =
            "dxmessaging-flow-graph-trace-target-lanes";
        internal const string VisibleTraceTargetLaneRowClassName =
            "dxmessaging-flow-graph-trace-target-lane-row";
        internal const string VisibleTraceTargetLaneTargetLabelName =
            "dxmessaging-flow-graph-trace-target-lane-target";
        internal const string VisibleTraceTargetLanesSummaryLabelName =
            "dxmessaging-flow-graph-trace-target-lanes-summary";
        internal const string VisibleTraceTargetLaneSummaryLabelName =
            "dxmessaging-flow-graph-trace-target-lane-summary";
        internal const string VisibleTraceTargetLaneDetailsLabelName =
            "dxmessaging-flow-graph-trace-target-lane-details";
        internal const string VisibleTraceRouteKindLanesName =
            "dxmessaging-flow-graph-trace-route-kind-lanes";
        internal const string VisibleTraceRouteKindLaneRowClassName =
            "dxmessaging-flow-graph-trace-route-kind-lane-row";
        internal const string VisibleTraceRouteKindLaneRouteKindLabelName =
            "dxmessaging-flow-graph-trace-route-kind-lane-route-kind";
        internal const string VisibleTraceRouteKindLanesSummaryLabelName =
            "dxmessaging-flow-graph-trace-route-kind-lanes-summary";
        internal const string VisibleTraceRouteKindLaneSummaryLabelName =
            "dxmessaging-flow-graph-trace-route-kind-lane-summary";
        internal const string VisibleTraceRouteKindLaneDetailsLabelName =
            "dxmessaging-flow-graph-trace-route-kind-lane-details";
        internal const string VisibleTraceIdLanesName = "dxmessaging-flow-graph-trace-id-lanes";
        internal const string VisibleTraceIdLaneRowClassName =
            "dxmessaging-flow-graph-trace-id-lane-row";
        internal const string VisibleTraceIdLaneTraceIdLabelName =
            "dxmessaging-flow-graph-trace-id-lane-trace-id";
        internal const string VisibleTraceIdLanesSummaryLabelName =
            "dxmessaging-flow-graph-trace-id-lanes-summary";
        internal const string VisibleTraceIdLaneSummaryLabelName =
            "dxmessaging-flow-graph-trace-id-lane-summary";
        internal const string VisibleTraceIdLaneDetailsLabelName =
            "dxmessaging-flow-graph-trace-id-lane-details";
        internal const string TracePathsName = "dxmessaging-flow-graph-trace-paths";
        internal const string TracePathRowClassName = "dxmessaging-flow-graph-trace-path-row";
        internal const string TracePathMessageLabelName =
            "dxmessaging-flow-graph-trace-path-message";
        internal const string TracePathsSummaryLabelName =
            "dxmessaging-flow-graph-trace-paths-summary";
        internal const string TracePathSummaryLabelName =
            "dxmessaging-flow-graph-trace-path-summary";
        internal const string TracePathTargetLabelName = "dxmessaging-flow-graph-trace-path-target";
        internal const string ComponentNodeClassName = "dxmessaging-flow-graph-component-node";
        internal const string MessageNodeClassName = "dxmessaging-flow-graph-message-node";
        internal const string EdgeRowClassName = "dxmessaging-flow-graph-edge-row";
        internal const string SelectedRowClassName = "dxmessaging-flow-graph-selected-row";
        internal const string NodeNameLabelName = "dxmessaging-flow-graph-node-name";
        internal const string NodeSummaryLabelName = "dxmessaging-flow-graph-node-summary";
        internal const string EdgeLabelName = "dxmessaging-flow-graph-edge-label";
        internal const string DetailsPaneName = "dxmessaging-flow-graph-details";
        internal const string DetailsTitleLabelName = "dxmessaging-flow-graph-details-title";
        internal const string DetailsBodyLabelName = "dxmessaging-flow-graph-details-body";
        internal const string WarningLabelName = "dxmessaging-flow-graph-warning";

        private const string Title = "Message Flow Graph";
        private const int ExportSchemaVersion = 5;
        private const string ExportCaptureMode = "registration-topology-with-recent-diagnostics";
        private const string ExportTraceSemantics =
            "traceId is emitted by concrete MessageBus dispatch and copied to token delivery records when diagnostics are enabled; edge traced counts are registration-handle exact, trace paths are built from token delivery records to avoid cross-bus trace-id collisions, recentTraceIdCount counts distinct trace ids observed for each trace path, and recentTraceIds lists those positive trace ids for cross-path breadth analysis.";

        private string _filterText = string.Empty;
        private string _selectedItemKey = string.Empty;
        private FlowGraphSnapshot _currentSnapshot = FlowGraphSnapshot.Empty;

        [MenuItem("Tools/Wallstop Studios/DxMessaging/Flow Graph")]
        public static void Open()
        {
            DxMessagingFlowGraphWindow window = GetWindow<DxMessagingFlowGraphWindow>();
            window.titleContent = new GUIContent(Title);
            window.minSize = new Vector2(520, 360);
            window.Refresh();
        }

        private void CreateGUI()
        {
            titleContent = new GUIContent(Title);
            Refresh();
        }

        private void Refresh()
        {
            _currentSnapshot = CaptureSnapshot();
            BuildGraphUi(
                rootVisualElement,
                _currentSnapshot,
                new FlowGraphViewState(_filterText, _selectedItemKey),
                HandleFilterChanged,
                Refresh,
                exportText => EditorGUIUtility.systemCopyBuffer = exportText,
                HandleSelectionChanged
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
            if (
                RefreshGraphContent(
                    rootVisualElement,
                    _currentSnapshot,
                    new FlowGraphViewState(_filterText, _selectedItemKey),
                    exportText => EditorGUIUtility.systemCopyBuffer = exportText,
                    HandleSelectionChanged
                )
            )
            {
                return;
            }

            BuildGraphUi(
                rootVisualElement,
                _currentSnapshot,
                new FlowGraphViewState(_filterText, _selectedItemKey),
                HandleFilterChanged,
                Refresh,
                exportText => EditorGUIUtility.systemCopyBuffer = exportText,
                HandleSelectionChanged
            );
        }

        private void HandleSelectionChanged(string selectedItemKey)
        {
            string normalizedSelectionKey = selectedItemKey ?? string.Empty;
            if (string.Equals(_selectedItemKey, normalizedSelectionKey, StringComparison.Ordinal))
            {
                return;
            }

            _selectedItemKey = normalizedSelectionKey;
            if (
                RefreshGraphContent(
                    rootVisualElement,
                    _currentSnapshot,
                    new FlowGraphViewState(_filterText, _selectedItemKey),
                    exportText => EditorGUIUtility.systemCopyBuffer = exportText,
                    HandleSelectionChanged
                )
            )
            {
                return;
            }

            BuildGraphUi(
                rootVisualElement,
                _currentSnapshot,
                new FlowGraphViewState(_filterText, _selectedItemKey),
                HandleFilterChanged,
                Refresh,
                exportText => EditorGUIUtility.systemCopyBuffer = exportText,
                HandleSelectionChanged
            );
        }

        internal static FlowGraphSnapshot CaptureSnapshot()
        {
            MessagingComponent[] components = Resources.FindObjectsOfTypeAll<MessagingComponent>();
            return CaptureSnapshot(components.Where(IsSceneComponent));
        }

        internal static FlowGraphSnapshot CaptureSnapshot(
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

            List<FlowGraphComponentNode> componentNodes = new();
            Dictionary<string, MessageNodeBuilder> messageNodes = new(StringComparer.Ordinal);
            Dictionary<string, EdgeBuilder> edgeBuilders = new(StringComparer.Ordinal);
            Dictionary<string, TracePathBuilder> tracePathBuilders = new(StringComparer.Ordinal);
            List<string> warnings = new();
            bool globalEmissionEvidenceCaptured = false;

            foreach (MessagingComponent component in orderedComponents)
            {
                string componentId = CreateComponentId(component);
                string hierarchyPath = GetHierarchyPath(component.transform);
                try
                {
                    MessagingComponentInspectorState state =
                        MessagingComponentEditorHarness.Capture(
                            component,
                            resolveSerializedProviderBus: false
                        );
                    int listenerCount = state.Listeners.Count;
                    int registrationCount = state.Listeners.Sum(listener =>
                        listener.Registrations.Count
                    );
                    int callCount = state.Listeners.Sum(listener =>
                        listener.Registrations.Sum(registration => registration.CallCount)
                    );
                    int localMessageCount = state.Listeners.Sum(listener =>
                        listener.EmissionHistory.Count
                    );
                    if (!globalEmissionEvidenceCaptured)
                    {
                        AddEmissionEvidence(
                            messageNodes,
                            state.GlobalEmissionHistory,
                            isGlobalEvidence: true
                        );
                        globalEmissionEvidenceCaptured = true;
                    }

                    componentNodes.Add(
                        new FlowGraphComponentNode(
                            componentId,
                            hierarchyPath,
                            component.GetType().Name,
                            component.gameObject.activeInHierarchy,
                            listenerCount,
                            registrationCount,
                            callCount,
                            localMessageCount
                        )
                    );

                    foreach (ListenerDiagnosticsView listener in state.Listeners)
                    {
                        AddEmissionEvidence(
                            messageNodes,
                            listener.EmissionHistory,
                            isGlobalEvidence: false
                        );
                        Dictionary<MessageRegistrationHandle, int> recentTracedDeliveryCounts =
                            CountRecentTracedDeliveriesByHandle(listener.EmissionHistory);
                        foreach (MessageRegistrationView registration in listener.Registrations)
                        {
                            recentTracedDeliveryCounts.TryGetValue(
                                registration.Handle,
                                out int recentTracedDeliveryCount
                            );
                            AddRegistrationEdge(
                                messageNodes,
                                edgeBuilders,
                                componentId,
                                hierarchyPath,
                                registration,
                                recentTracedDeliveryCount
                            );
                            if (
                                registration.Metadata.registrationType
                                == MessageRegistrationType.GlobalAcceptAll
                            )
                            {
                                AddTracedDeliveryEvidence(
                                    messageNodes,
                                    listener.EmissionHistory,
                                    registration.Handle
                                );
                            }
                            AddTracePathEvidence(
                                tracePathBuilders,
                                listener.EmissionHistory,
                                componentId,
                                hierarchyPath,
                                registration
                            );
                        }
                    }

                    AddProviderWarnings(warnings, hierarchyPath, state.ProviderDiagnostics);
                }
                catch (Exception exception)
                {
                    componentNodes.Add(
                        new FlowGraphComponentNode(
                            componentId,
                            hierarchyPath,
                            component != null ? component.GetType().Name : "<missing>",
                            component != null && component.gameObject.activeInHierarchy,
                            listenerCount: 0,
                            registrationCount: 0,
                            callCount: 0,
                            localMessageCount: 0
                        )
                    );
                    warnings.Add($"{hierarchyPath}: diagnostics capture failed: {exception}");
                }
            }

            return new FlowGraphSnapshot(
                componentNodes
                    .OrderBy(component => component.HierarchyPath, StringComparer.Ordinal)
                    .ToArray(),
                messageNodes
                    .Values.Select(builder => builder.Build())
                    .OrderBy(message => message.MessageTypeName, StringComparer.Ordinal)
                    .ToArray(),
                edgeBuilders
                    .Values.Select(builder => builder.Build())
                    .OrderBy(edge => edge.MessageTypeName, StringComparer.Ordinal)
                    .ThenBy(edge => edge.TargetComponentPath, StringComparer.Ordinal)
                    .ThenBy(edge => edge.TargetComponentId, StringComparer.Ordinal)
                    .ThenBy(edge => edge.RegistrationTypeName, StringComparer.Ordinal)
                    .ToArray(),
                tracePathBuilders
                    .Values.Select(builder => builder.Build())
                    .OrderBy(path => path.MessageTypeName, StringComparer.Ordinal)
                    .ThenBy(path => path.Context, StringComparer.Ordinal)
                    .ThenBy(path => path.TargetComponentPath, StringComparer.Ordinal)
                    .ThenBy(path => path.TargetComponentId, StringComparer.Ordinal)
                    .ThenBy(path => path.RegistrationTypeName, StringComparer.Ordinal)
                    .ToArray(),
                warnings.OrderBy(warning => warning, StringComparer.Ordinal).ToArray()
            );
        }

        internal static void BuildGraphUi(
            VisualElement root,
            FlowGraphSnapshot snapshot,
            Action onRefresh = null
        )
        {
            BuildGraphUi(root, snapshot, FlowGraphViewState.Default, onRefresh: onRefresh);
        }

        internal static void BuildGraphUi(
            VisualElement root,
            FlowGraphSnapshot snapshot,
            FlowGraphViewState viewState,
            Action<string> onFilterChanged = null,
            Action onRefresh = null,
            Action<string> onCopyExport = null,
            Action<string> onSelectionChanged = null
        )
        {
            if (root == null)
            {
                throw new ArgumentNullException(nameof(root));
            }
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            root.Clear();
            root.AddToClassList(RootClassName);
            root.style.paddingTop = 10;
            root.style.paddingRight = 12;
            root.style.paddingBottom = 12;
            root.style.paddingLeft = 12;

            FlowGraphVisibleSnapshot visibleSnapshot = FilterSnapshot(
                snapshot,
                viewState.FilterText
            );

            VisualElement toolbar = new();
            toolbar.AddToClassList(ToolbarClassName);
            toolbar.style.flexDirection = FlexDirection.Row;
            toolbar.style.alignItems = Align.Center;
            toolbar.style.justifyContent = Justify.SpaceBetween;
            toolbar.style.marginBottom = 10;

            Label title = new(Title);
            title.style.fontSize = 16;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            toolbar.Add(title);

            Label status = new(CreateStatusText(snapshot, visibleSnapshot))
            {
                name = StatusLabelName,
            };
            status.style.unityTextAlign = TextAnchor.MiddleRight;
            toolbar.Add(status);
            root.Add(toolbar);

            root.Add(
                CreateControlRow(
                    snapshot,
                    visibleSnapshot,
                    viewState,
                    onFilterChanged,
                    onRefresh,
                    onCopyExport
                )
            );

            ScrollView content = new(ScrollViewMode.Vertical) { name = ContentName };
            content.style.flexGrow = 1;
            root.Add(content);

            RenderGraphContent(content, snapshot, visibleSnapshot, viewState, onSelectionChanged);
        }

        internal static bool RefreshGraphContent(
            VisualElement root,
            FlowGraphSnapshot snapshot,
            FlowGraphViewState viewState,
            Action<string> onCopyExport = null,
            Action<string> onSelectionChanged = null
        )
        {
            if (root == null)
            {
                throw new ArgumentNullException(nameof(root));
            }
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            ScrollView content = root.Q<ScrollView>(ContentName);
            Label status = root.Q<Label>(StatusLabelName);
            Button export = root.Q<Button>(ExportButtonName);
            if (content == null || status == null)
            {
                return false;
            }

            FlowGraphVisibleSnapshot visibleSnapshot = FilterSnapshot(
                snapshot,
                viewState.FilterText
            );
            status.text = CreateStatusText(snapshot, visibleSnapshot);
            RenderGraphContent(content, snapshot, visibleSnapshot, viewState, onSelectionChanged);
            SetExportButtonEnabled(export, visibleSnapshot, onCopyExport);
            return true;
        }

        private static void RenderGraphContent(
            ScrollView content,
            FlowGraphSnapshot snapshot,
            FlowGraphVisibleSnapshot visibleSnapshot,
            FlowGraphViewState viewState,
            Action<string> onSelectionChanged
        )
        {
            if (content == null)
            {
                throw new ArgumentNullException(nameof(content));
            }

            content.Clear();
            bool hasGraphItems =
                visibleSnapshot.ComponentNodes.Count > 0
                || visibleSnapshot.MessageNodes.Count > 0
                || visibleSnapshot.Edges.Count > 0
                || visibleSnapshot.TracePaths.Count > 0;
            bool hasWarnings = visibleSnapshot.Warnings.Count > 0;

            if (!hasGraphItems && !hasWarnings)
            {
                string emptyText =
                    snapshot.ComponentNodes.Count == 0
                    && snapshot.MessageNodes.Count == 0
                    && snapshot.Edges.Count == 0
                    && snapshot.TracePaths.Count == 0
                    && snapshot.Warnings.Count == 0
                        ? "No MessagingComponent registrations are loaded in open scenes."
                        : "No graph items match the current filter.";
                Label empty = new(emptyText) { name = EmptyStateLabelName };
                empty.style.whiteSpace = WhiteSpace.Normal;
                content.Add(empty);
            }
            else if (hasGraphItems)
            {
                FlowGraphSelectedItem selectedItem = ResolveSelectedItem(
                    visibleSnapshot,
                    viewState.SelectedItemKey
                );

                content.Add(CreateRouteMap(visibleSnapshot, selectedItem.Key, onSelectionChanged));

                if (visibleSnapshot.Edges.Count > 0)
                {
                    content.Add(CreateVisibleMessageLanes(visibleSnapshot));
                    content.Add(CreateVisibleTargetLanes(visibleSnapshot));
                }

                if (visibleSnapshot.TracePaths.Count > 0)
                {
                    content.Add(CreateVisibleFlowCorridors(visibleSnapshot));
                    content.Add(CreateVisibleTraceRouteKindLanes(visibleSnapshot));
                    content.Add(CreateVisibleTraceIdLanes(visibleSnapshot));
                    content.Add(CreateVisibleTraceMessageLanes(visibleSnapshot));
                    content.Add(CreateVisibleTraceTargetLanes(visibleSnapshot));
                    content.Add(CreateVisibleContextLanes(visibleSnapshot));
                    content.Add(CreateTracePaths(visibleSnapshot));
                }

                content.Add(CreateSectionTitle("Components"));
                foreach (FlowGraphComponentNode component in visibleSnapshot.ComponentNodes)
                {
                    content.Add(
                        CreateComponentNodeRow(
                            component,
                            string.Equals(
                                selectedItem.Key,
                                CreateComponentSelectionKey(component),
                                StringComparison.Ordinal
                            ),
                            onSelectionChanged
                        )
                    );
                }

                content.Add(CreateSectionTitle("Message Types"));
                foreach (FlowGraphMessageNode message in visibleSnapshot.MessageNodes)
                {
                    content.Add(
                        CreateMessageNodeRow(
                            message,
                            string.Equals(
                                selectedItem.Key,
                                CreateMessageSelectionKey(message),
                                StringComparison.Ordinal
                            ),
                            onSelectionChanged
                        )
                    );
                }

                content.Add(CreateSectionTitle("Registration Edges"));
                foreach (FlowGraphEdge edge in visibleSnapshot.Edges)
                {
                    content.Add(
                        CreateEdgeRow(
                            edge,
                            string.Equals(
                                selectedItem.Key,
                                CreateEdgeSelectionKey(edge),
                                StringComparison.Ordinal
                            ),
                            onSelectionChanged
                        )
                    );
                }

                if (selectedItem.HasValue)
                {
                    content.Add(CreateDetailsPane(selectedItem, visibleSnapshot));
                }
            }

            foreach (string warning in visibleSnapshot.Warnings)
            {
                Label warningLabel = new(warning) { name = WarningLabelName };
                warningLabel.style.marginTop = 8;
                warningLabel.style.whiteSpace = WhiteSpace.Normal;
                content.Add(warningLabel);
            }
        }

        internal static string CreateExportText(FlowGraphSnapshot snapshot, string filterText = "")
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            FlowGraphVisibleSnapshot visibleSnapshot = FilterSnapshot(snapshot, filterText);
            StringBuilder builder = new();
            builder.AppendLine("{");
            builder.Append("  \"schemaVersion\": ").Append(ExportSchemaVersion).AppendLine(",");
            AppendJsonProperty(
                builder,
                indentSize: 2,
                "captureMode",
                ExportCaptureMode,
                trailingComma: true
            );
            AppendJsonProperty(
                builder,
                indentSize: 2,
                "traceSemantics",
                ExportTraceSemantics,
                trailingComma: true
            );
            builder
                .Append("  \"componentCount\": ")
                .Append(visibleSnapshot.ComponentNodes.Count)
                .AppendLine(",");
            builder
                .Append("  \"messageCount\": ")
                .Append(visibleSnapshot.MessageNodes.Count)
                .AppendLine(",");
            builder.Append("  \"edgeCount\": ").Append(visibleSnapshot.Edges.Count).AppendLine(",");
            builder
                .Append("  \"tracePathCount\": ")
                .Append(visibleSnapshot.TracePaths.Count)
                .AppendLine(",");
            builder.AppendLine("  \"components\": [");
            for (int i = 0; i < visibleSnapshot.ComponentNodes.Count; i++)
            {
                FlowGraphComponentNode component = visibleSnapshot.ComponentNodes[i];
                builder.AppendLine("    {");
                AppendJsonProperty(builder, "id", component.Id, trailingComma: true);
                AppendJsonProperty(
                    builder,
                    "hierarchyPath",
                    component.HierarchyPath,
                    trailingComma: true
                );
                AppendJsonProperty(
                    builder,
                    "componentType",
                    component.ComponentTypeName,
                    trailingComma: true
                );
                builder
                    .Append("      \"activeInHierarchy\": ")
                    .Append(component.ActiveInHierarchy ? "true" : "false")
                    .AppendLine(",");
                builder
                    .Append("      \"listenerCount\": ")
                    .Append(component.ListenerCount)
                    .AppendLine(",");
                builder
                    .Append("      \"registrationCount\": ")
                    .Append(component.RegistrationCount)
                    .AppendLine(",");
                builder.Append("      \"callCount\": ").Append(component.CallCount).AppendLine(",");
                builder
                    .Append("      \"localMessageCount\": ")
                    .Append(component.LocalMessageCount)
                    .AppendLine();
                builder.Append("    }");
                if (i < visibleSnapshot.ComponentNodes.Count - 1)
                {
                    builder.Append(",");
                }
                builder.AppendLine();
            }
            builder.AppendLine("  ],");

            builder.AppendLine("  \"messages\": [");
            for (int i = 0; i < visibleSnapshot.MessageNodes.Count; i++)
            {
                FlowGraphMessageNode message = visibleSnapshot.MessageNodes[i];
                builder.AppendLine("    {");
                AppendJsonProperty(
                    builder,
                    "messageType",
                    message.MessageTypeName,
                    trailingComma: true
                );
                builder
                    .Append("      \"registrationCount\": ")
                    .Append(message.RegistrationCount)
                    .AppendLine(",");
                builder.Append("      \"callCount\": ").Append(message.CallCount).AppendLine(",");
                builder
                    .Append("      \"recentGlobalEmissionCount\": ")
                    .Append(message.RecentGlobalEmissionCount)
                    .AppendLine(",");
                builder
                    .Append("      \"recentLocalMessageCount\": ")
                    .Append(message.RecentLocalMessageCount)
                    .AppendLine(",");
                builder
                    .Append("      \"recentTracedDeliveryCount\": ")
                    .Append(message.RecentTracedDeliveryCount)
                    .AppendLine();
                builder.Append("    }");
                if (i < visibleSnapshot.MessageNodes.Count - 1)
                {
                    builder.Append(",");
                }
                builder.AppendLine();
            }
            builder.AppendLine("  ],");

            builder.AppendLine("  \"edges\": [");
            for (int i = 0; i < visibleSnapshot.Edges.Count; i++)
            {
                FlowGraphEdge edge = visibleSnapshot.Edges[i];
                builder.AppendLine("    {");
                AppendJsonProperty(
                    builder,
                    "messageType",
                    edge.MessageTypeName,
                    trailingComma: true
                );
                AppendJsonProperty(
                    builder,
                    "targetComponentId",
                    edge.TargetComponentId,
                    trailingComma: true
                );
                AppendJsonProperty(
                    builder,
                    "targetComponentPath",
                    edge.TargetComponentPath,
                    trailingComma: true
                );
                AppendJsonProperty(
                    builder,
                    "registrationType",
                    edge.RegistrationTypeName,
                    trailingComma: true
                );
                builder
                    .Append("      \"registrationCount\": ")
                    .Append(edge.RegistrationCount)
                    .AppendLine(",");
                builder.Append("      \"callCount\": ").Append(edge.CallCount).AppendLine(",");
                builder
                    .Append("      \"recentTracedDeliveryCount\": ")
                    .Append(edge.RecentTracedDeliveryCount)
                    .AppendLine();
                builder.Append("    }");
                if (i < visibleSnapshot.Edges.Count - 1)
                {
                    builder.Append(",");
                }
                builder.AppendLine();
            }
            builder.AppendLine("  ],");

            builder.AppendLine("  \"tracePaths\": [");
            for (int i = 0; i < visibleSnapshot.TracePaths.Count; i++)
            {
                FlowGraphTracePath path = visibleSnapshot.TracePaths[i];
                builder.AppendLine("    {");
                AppendJsonProperty(
                    builder,
                    "messageType",
                    path.MessageTypeName,
                    trailingComma: true
                );
                AppendJsonProperty(builder, "context", path.Context, trailingComma: true);
                AppendJsonProperty(
                    builder,
                    "targetComponentId",
                    path.TargetComponentId,
                    trailingComma: true
                );
                AppendJsonProperty(
                    builder,
                    "targetComponentPath",
                    path.TargetComponentPath,
                    trailingComma: true
                );
                AppendJsonProperty(
                    builder,
                    "registrationType",
                    path.RegistrationTypeName,
                    trailingComma: true
                );
                builder
                    .Append("      \"recentTracedDeliveryCount\": ")
                    .Append(path.RecentTracedDeliveryCount)
                    .AppendLine(",");
                builder
                    .Append("      \"recentTraceIdCount\": ")
                    .Append(path.RecentTraceIdCount)
                    .AppendLine(",");
                AppendJsonLongArray(
                    builder,
                    indentSize: 6,
                    "recentTraceIds",
                    path.TraceIds,
                    trailingComma: false
                );
                builder.Append("    }");
                if (i < visibleSnapshot.TracePaths.Count - 1)
                {
                    builder.Append(",");
                }
                builder.AppendLine();
            }
            builder.AppendLine("  ],");

            builder.AppendLine("  \"warnings\": [");
            for (int i = 0; i < visibleSnapshot.Warnings.Count; i++)
            {
                builder
                    .Append("    \"")
                    .Append(EscapeJson(visibleSnapshot.Warnings[i]))
                    .Append("\"");
                if (i < visibleSnapshot.Warnings.Count - 1)
                {
                    builder.Append(",");
                }
                builder.AppendLine();
            }
            builder.AppendLine("  ]");
            builder.AppendLine("}");
            return builder.ToString();
        }

        private static void AddTracePathEvidence(
            Dictionary<string, TracePathBuilder> tracePathBuilders,
            IEnumerable<MessageEmissionData> emissions,
            string componentId,
            string componentPath,
            MessageRegistrationView registration
        )
        {
            if (tracePathBuilders == null)
            {
                throw new ArgumentNullException(nameof(tracePathBuilders));
            }
            if (emissions == null)
            {
                return;
            }

            foreach (MessageEmissionData emission in emissions)
            {
                if (
                    emission.traceId == 0
                    || emission.registrationHandle != registration.Handle
                    || emission.message == null
                )
                {
                    continue;
                }

                Type messageType = emission.message.MessageType;
                string messageTypeName = CreateMessageTypeName(messageType);
                string context = CreateTraceContextText(
                    emission.context ?? registration.Metadata.context
                );
                string registrationTypeName = registration.Metadata.registrationType.ToString();
                string key = string.Join(
                    "|",
                    messageTypeName,
                    context,
                    componentId,
                    registrationTypeName
                );
                if (!tracePathBuilders.TryGetValue(key, out TracePathBuilder builder))
                {
                    builder = new TracePathBuilder(
                        messageTypeName,
                        context,
                        componentId,
                        componentPath,
                        registrationTypeName
                    );
                    tracePathBuilders[key] = builder;
                }

                builder.RecentTracedDeliveryCount++;
                builder.AddTraceId(emission.traceId);
            }
        }

        private static void AddRegistrationEdge(
            Dictionary<string, MessageNodeBuilder> messageNodes,
            Dictionary<string, EdgeBuilder> edgeBuilders,
            string componentId,
            string componentPath,
            MessageRegistrationView registration,
            int recentTracedDeliveryCount
        )
        {
            MessageRegistrationMetadata metadata = registration.Metadata;
            string messageKey = CreateMessageKey(metadata.type);
            string messageTypeName = CreateMessageTypeName(metadata.type);
            if (!messageNodes.TryGetValue(messageKey, out MessageNodeBuilder messageBuilder))
            {
                messageBuilder = new MessageNodeBuilder(messageTypeName);
                messageNodes[messageKey] = messageBuilder;
            }

            messageBuilder.RegistrationCount++;
            messageBuilder.CallCount += registration.CallCount;
            if (metadata.registrationType != MessageRegistrationType.GlobalAcceptAll)
            {
                messageBuilder.RecentTracedDeliveryCount += recentTracedDeliveryCount;
            }

            string registrationTypeName = metadata.registrationType.ToString();
            string edgeKey = string.Join("|", messageKey, componentId, registrationTypeName);
            if (!edgeBuilders.TryGetValue(edgeKey, out EdgeBuilder edgeBuilder))
            {
                edgeBuilder = new EdgeBuilder(
                    messageTypeName,
                    componentId,
                    componentPath,
                    registrationTypeName
                );
                edgeBuilders[edgeKey] = edgeBuilder;
            }

            edgeBuilder.RegistrationCount++;
            edgeBuilder.CallCount += registration.CallCount;
            edgeBuilder.RecentTracedDeliveryCount += recentTracedDeliveryCount;
        }

        private static void AddTracedDeliveryEvidence(
            Dictionary<string, MessageNodeBuilder> messageNodes,
            IEnumerable<MessageEmissionData> emissions,
            MessageRegistrationHandle handle
        )
        {
            if (messageNodes == null)
            {
                throw new ArgumentNullException(nameof(messageNodes));
            }
            if (emissions == null)
            {
                return;
            }

            foreach (MessageEmissionData emission in emissions)
            {
                if (
                    emission.traceId == 0
                    || emission.registrationHandle != handle
                    || emission.message == null
                )
                {
                    continue;
                }

                Type messageType = emission.message.MessageType;
                string messageKey = CreateMessageKey(messageType);
                if (!messageNodes.TryGetValue(messageKey, out MessageNodeBuilder messageBuilder))
                {
                    messageBuilder = new MessageNodeBuilder(CreateMessageTypeName(messageType));
                    messageNodes[messageKey] = messageBuilder;
                }

                messageBuilder.RecentTracedDeliveryCount++;
            }
        }

        private static Dictionary<
            MessageRegistrationHandle,
            int
        > CountRecentTracedDeliveriesByHandle(IEnumerable<MessageEmissionData> emissions)
        {
            Dictionary<MessageRegistrationHandle, int> counts = new();
            if (emissions == null)
            {
                return counts;
            }

            foreach (MessageEmissionData emission in emissions)
            {
                if (
                    emission.traceId == 0
                    || emission.registrationHandle == default(MessageRegistrationHandle)
                )
                {
                    continue;
                }

                counts[emission.registrationHandle] =
                    counts.GetValueOrDefault(emission.registrationHandle) + 1;
            }

            return counts;
        }

        private static void AddEmissionEvidence(
            Dictionary<string, MessageNodeBuilder> messageNodes,
            IEnumerable<MessageEmissionData> emissions,
            bool isGlobalEvidence
        )
        {
            if (messageNodes == null)
            {
                throw new ArgumentNullException(nameof(messageNodes));
            }
            if (emissions == null)
            {
                return;
            }

            foreach (MessageEmissionData emission in emissions)
            {
                Type messageType = emission.message?.MessageType;
                string messageKey = CreateMessageKey(messageType);
                if (!messageNodes.TryGetValue(messageKey, out MessageNodeBuilder messageBuilder))
                {
                    messageBuilder = new MessageNodeBuilder(CreateMessageTypeName(messageType));
                    messageNodes[messageKey] = messageBuilder;
                }

                if (isGlobalEvidence)
                {
                    messageBuilder.RecentGlobalEmissionCount++;
                }
                else
                {
                    messageBuilder.RecentLocalMessageCount++;
                }
            }
        }

        private static FlowGraphVisibleSnapshot FilterSnapshot(
            FlowGraphSnapshot snapshot,
            string filterText
        )
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            if (string.IsNullOrWhiteSpace(filterText))
            {
                return new FlowGraphVisibleSnapshot(
                    snapshot.ComponentNodes,
                    snapshot.MessageNodes,
                    snapshot.Edges,
                    snapshot.TracePaths,
                    snapshot.Warnings
                );
            }

            string normalizedFilterText = filterText.Trim();
            HashSet<string> componentIds = new(StringComparer.Ordinal);
            HashSet<string> directMessageNames = new(StringComparer.Ordinal);

            foreach (FlowGraphComponentNode component in snapshot.ComponentNodes)
            {
                if (component.Matches(normalizedFilterText))
                {
                    componentIds.Add(component.Id);
                }
            }

            foreach (FlowGraphMessageNode message in snapshot.MessageNodes)
            {
                if (message.Matches(normalizedFilterText))
                {
                    directMessageNames.Add(message.MessageTypeName);
                }
            }

            FlowGraphEdge[] edges = snapshot
                .Edges.Where(edge =>
                    edge.Matches(normalizedFilterText)
                    || componentIds.Contains(edge.TargetComponentId)
                    || directMessageNames.Contains(edge.MessageTypeName)
                )
                .ToArray();

            HashSet<string> messageNames = new(directMessageNames, StringComparer.Ordinal);
            foreach (FlowGraphEdge edge in edges)
            {
                componentIds.Add(edge.TargetComponentId);
                messageNames.Add(edge.MessageTypeName);
            }

            FlowGraphTracePath[] tracePaths = snapshot
                .TracePaths.Where(path =>
                    path.Matches(normalizedFilterText)
                    || componentIds.Contains(path.TargetComponentId)
                    || directMessageNames.Contains(path.MessageTypeName)
                    || edges.Any(edge => EdgeMatchesTracePath(edge, path))
                )
                .ToArray();

            foreach (FlowGraphTracePath path in tracePaths)
            {
                componentIds.Add(path.TargetComponentId);
                messageNames.Add(path.MessageTypeName);
            }

            if (tracePaths.Length > 0)
            {
                HashSet<string> visibleEdgeKeys = new(
                    edges.Select(CreateEdgeSelectionKey),
                    StringComparer.Ordinal
                );
                edges = snapshot
                    .Edges.Where(edge =>
                        visibleEdgeKeys.Contains(CreateEdgeSelectionKey(edge))
                        || tracePaths.Any(path => EdgeMatchesTracePath(edge, path))
                    )
                    .ToArray();

                foreach (FlowGraphEdge edge in edges)
                {
                    componentIds.Add(edge.TargetComponentId);
                    messageNames.Add(edge.MessageTypeName);
                }
            }

            FlowGraphComponentNode[] components = snapshot
                .ComponentNodes.Where(component =>
                    componentIds.Contains(component.Id) || component.Matches(normalizedFilterText)
                )
                .ToArray();
            FlowGraphMessageNode[] messages = snapshot
                .MessageNodes.Where(message =>
                    messageNames.Contains(message.MessageTypeName)
                    || message.Matches(normalizedFilterText)
                )
                .ToArray();
            string[] warnings = snapshot
                .Warnings.Where(warning => ContainsText(warning, normalizedFilterText))
                .ToArray();

            return new FlowGraphVisibleSnapshot(components, messages, edges, tracePaths, warnings);
        }

        private static bool EdgeMatchesTracePath(FlowGraphEdge edge, FlowGraphTracePath path)
        {
            if (
                !string.Equals(
                    edge.TargetComponentId,
                    path.TargetComponentId,
                    StringComparison.Ordinal
                )
                || !string.Equals(
                    edge.RegistrationTypeName,
                    path.RegistrationTypeName,
                    StringComparison.Ordinal
                )
            )
            {
                return false;
            }

            return string.Equals(
                    edge.MessageTypeName,
                    path.MessageTypeName,
                    StringComparison.Ordinal
                )
                || string.Equals(
                    edge.RegistrationTypeName,
                    MessageRegistrationType.GlobalAcceptAll.ToString(),
                    StringComparison.Ordinal
                );
        }

        private static void AddProviderWarnings(
            List<string> warnings,
            string hierarchyPath,
            ProviderDiagnosticsView providerDiagnostics
        )
        {
            if (providerDiagnostics.SerializedProviderMissingWarning)
            {
                warnings.Add($"{hierarchyPath}: serialized provider missing");
            }
            if (providerDiagnostics.SerializedProviderNullBusWarning)
            {
                warnings.Add($"{hierarchyPath}: serialized provider resolves no bus");
            }
        }

        private static bool IsSceneComponent(MessagingComponent component)
        {
            return component != null
                && component.gameObject != null
                && component.gameObject.scene.IsValid()
                && !EditorSceneManager.IsPreviewSceneObject(component.gameObject)
                && !EditorUtility.IsPersistent(component);
        }

        private static string CreateStatusText(FlowGraphSnapshot snapshot)
        {
            return $"{snapshot.ComponentNodes.Count} components | {snapshot.MessageNodes.Count} messages | {snapshot.Edges.Count} edges | {snapshot.TracePaths.Count} trace paths";
        }

        private static string CreateStatusText(
            FlowGraphSnapshot snapshot,
            FlowGraphVisibleSnapshot visibleSnapshot
        )
        {
            if (
                visibleSnapshot.ComponentNodes.Count != snapshot.ComponentNodes.Count
                || visibleSnapshot.MessageNodes.Count != snapshot.MessageNodes.Count
                || visibleSnapshot.Edges.Count != snapshot.Edges.Count
                || visibleSnapshot.TracePaths.Count != snapshot.TracePaths.Count
            )
            {
                return $"{visibleSnapshot.ComponentNodes.Count}/{snapshot.ComponentNodes.Count} components | {visibleSnapshot.MessageNodes.Count}/{snapshot.MessageNodes.Count} messages | {visibleSnapshot.Edges.Count}/{snapshot.Edges.Count} edges | {visibleSnapshot.TracePaths.Count}/{snapshot.TracePaths.Count} trace paths";
            }

            return CreateStatusText(snapshot);
        }

        private static VisualElement CreateControlRow(
            FlowGraphSnapshot snapshot,
            FlowGraphVisibleSnapshot visibleSnapshot,
            FlowGraphViewState viewState,
            Action<string> onFilterChanged,
            Action onRefresh,
            Action<string> onCopyExport
        )
        {
            VisualElement controls = new();
            controls.style.flexDirection = FlexDirection.Row;
            controls.style.alignItems = Align.Center;
            controls.style.marginBottom = 10;

            TextField filter = new("Filter") { name = FilterFieldName };
            filter.SetValueWithoutNotify(viewState.FilterText);
            filter.style.flexGrow = 1;
            filter.style.marginRight = 8;
            Button export = null;
            if (onFilterChanged != null)
            {
                filter.RegisterValueChangedCallback(evt =>
                {
                    string nextFilter = evt.newValue ?? string.Empty;
                    onFilterChanged.Invoke(nextFilter);
                    SetExportButtonEnabled(export, snapshot, nextFilter, onCopyExport);
                });
            }
            controls.Add(filter);

            Button refresh = new(() => onRefresh?.Invoke())
            {
                name = RefreshButtonName,
                text = "Refresh",
            };
            refresh.SetEnabled(onRefresh != null);
            refresh.style.marginRight = 6;
            controls.Add(refresh);

            export = new(() => onCopyExport?.Invoke(CreateExportText(snapshot, filter.value)))
            {
                name = ExportButtonName,
                text = "Copy JSON",
            };
            SetExportButtonEnabled(export, visibleSnapshot, onCopyExport);
            controls.Add(export);

            return controls;
        }

        private static void SetExportButtonEnabled(
            Button export,
            FlowGraphSnapshot snapshot,
            string filterText,
            Action<string> onCopyExport
        )
        {
            if (export == null)
            {
                return;
            }

            SetExportButtonEnabled(export, FilterSnapshot(snapshot, filterText), onCopyExport);
        }

        private static void SetExportButtonEnabled(
            Button export,
            FlowGraphVisibleSnapshot visibleSnapshot,
            Action<string> onCopyExport
        )
        {
            if (export == null)
            {
                return;
            }

            export.SetEnabled(
                onCopyExport != null
                    && (
                        visibleSnapshot.ComponentNodes.Count > 0
                        || visibleSnapshot.MessageNodes.Count > 0
                        || visibleSnapshot.Edges.Count > 0
                        || visibleSnapshot.TracePaths.Count > 0
                        || visibleSnapshot.Warnings.Count > 0
                    )
            );
        }

        private static string CreateComponentId(MessagingComponent component)
        {
            return component == null
                ? "component:<missing>"
                : "component:" + InstanceId.StableId(component);
        }

        private static string CreateMessageKey(Type messageType)
        {
            return messageType == null
                ? "message:<unknown>"
                : "message:"
                    + (
                        messageType.AssemblyQualifiedName
                        ?? messageType.FullName
                        ?? messageType.Name
                    );
        }

        private static string CreateMessageTypeName(Type messageType)
        {
            if (messageType == null)
            {
                return "<unknown>";
            }

            string typeName = messageType.FullName ?? messageType.Name;
            string assemblyName = messageType.Assembly.GetName().Name;
            return string.IsNullOrWhiteSpace(assemblyName)
                ? typeName
                : $"{typeName} [{assemblyName}]";
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

        private static string CreateTraceContextText(InstanceId? context)
        {
            if (!context.HasValue)
            {
                return "<none>";
            }

            string contextText = context.Value.ToString();
            return string.IsNullOrWhiteSpace(contextText) ? "<none>" : contextText;
        }

        private static Label CreateSectionTitle(string text)
        {
            Label title = new(text);
            title.style.marginTop = 10;
            title.style.marginBottom = 4;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            return title;
        }

        private static VisualElement CreateRouteMap(
            FlowGraphVisibleSnapshot visibleSnapshot,
            string selectedItemKey,
            Action<string> onSelectionChanged
        )
        {
            VisualElement routeMap = new() { name = RouteMapName };
            routeMap.style.borderTopWidth = 1;
            routeMap.style.borderTopColor = DxMessagingEditorPalette.BorderPanel;
            routeMap.style.borderBottomWidth = 1;
            routeMap.style.borderBottomColor = DxMessagingEditorPalette.BorderPanel;
            routeMap.style.marginBottom = 4;
            routeMap.style.paddingTop = 8;
            routeMap.style.paddingBottom = 8;

            Label title = new("Route Map");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            routeMap.Add(title);

            Label summary = new(CreateRouteMapSummaryText(visibleSnapshot))
            {
                name = RouteMapSummaryLabelName,
            };
            summary.style.marginTop = 2;
            summary.style.whiteSpace = WhiteSpace.Normal;
            routeMap.Add(summary);

            if (visibleSnapshot.Edges.Count == 0)
            {
                Label empty = new("No visible registration routes.");
                empty.style.marginTop = 6;
                routeMap.Add(empty);
                return routeMap;
            }

            int totalVisibleCalls = SumVisibleCalls(visibleSnapshot);
            foreach (FlowGraphEdge edge in visibleSnapshot.Edges)
            {
                string selectionKey = CreateEdgeSelectionKey(edge);
                routeMap.Add(
                    CreateRouteMapRow(
                        edge,
                        CreateCallShareText(edge.CallCount, totalVisibleCalls),
                        string.Equals(selectionKey, selectedItemKey, StringComparison.Ordinal),
                        onSelectionChanged
                    )
                );
            }

            return routeMap;
        }

        private static VisualElement CreateVisibleMessageLanes(
            FlowGraphVisibleSnapshot visibleSnapshot
        )
        {
            FlowGraphMessageLane[] lanes = BuildVisibleMessageLanes(visibleSnapshot);
            VisualElement messageLanes = new() { name = VisibleMessageLanesName };
            messageLanes.style.borderTopWidth = 1;
            messageLanes.style.borderTopColor = DxMessagingEditorPalette.BorderPanel;
            messageLanes.style.borderBottomWidth = 1;
            messageLanes.style.borderBottomColor = DxMessagingEditorPalette.BorderPanel;
            messageLanes.style.marginTop = 8;
            messageLanes.style.marginBottom = 4;
            messageLanes.style.paddingTop = 8;
            messageLanes.style.paddingBottom = 8;

            Label title = new("Visible Message Lanes");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            messageLanes.Add(title);

            Label summary = new(CreateVisibleMessageLanesSummaryText(lanes))
            {
                name = VisibleMessageLanesSummaryLabelName,
            };
            summary.style.marginTop = 2;
            summary.style.whiteSpace = WhiteSpace.Normal;
            messageLanes.Add(summary);

            int totalCalls = lanes.Sum(lane => lane.CallCount);
            foreach (FlowGraphMessageLane lane in lanes)
            {
                messageLanes.Add(CreateVisibleMessageLaneRow(lane, totalCalls));
            }

            return messageLanes;
        }

        private static VisualElement CreateVisibleMessageLaneRow(
            FlowGraphMessageLane lane,
            int totalCalls
        )
        {
            VisualElement row = new();
            row.AddToClassList(VisibleMessageLaneRowClassName);
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.borderLeftWidth = 3;
            row.style.borderLeftColor = DxMessagingEditorPalette.Amber;
            row.style.borderTopWidth = 1;
            row.style.borderTopColor = DxMessagingEditorPalette.BorderSoft;
            row.style.marginTop = 6;
            row.style.paddingTop = 7;
            row.style.paddingRight = 8;
            row.style.paddingBottom = 7;
            row.style.paddingLeft = 10;

            Label message = new(lane.MessageTypeName) { name = VisibleMessageLaneMessageLabelName };
            message.style.flexBasis = 0;
            message.style.flexGrow = 2;
            message.style.unityFontStyleAndWeight = FontStyle.Bold;
            message.style.whiteSpace = WhiteSpace.Normal;
            row.Add(message);

            Label summary = new(
                $"Routes: {lane.RouteCount} | Targets: {lane.TargetCount} | Registrations: {lane.RegistrationCount} | Calls: {lane.CallCount} | Recent traced: {lane.RecentTracedDeliveryCount} | No-call routes: {lane.NoCallRouteCount} | Route kinds: {lane.RouteKindsText} | Share: {CreateCallShareText(lane.CallCount, totalCalls)}"
            )
            {
                name = VisibleMessageLaneSummaryLabelName,
            };
            summary.style.flexBasis = 0;
            summary.style.flexGrow = 2;
            summary.style.marginLeft = 8;
            summary.style.whiteSpace = WhiteSpace.Normal;
            row.Add(summary);

            Label targets = new(
                $"Targets: {lane.TargetPathsText} | Inactive: {lane.InactiveTargetCount}/{lane.TargetCount}"
            )
            {
                name = VisibleMessageLaneTargetsLabelName,
            };
            targets.style.flexBasis = 0;
            targets.style.flexGrow = 2;
            targets.style.marginLeft = 8;
            targets.style.whiteSpace = WhiteSpace.Normal;
            row.Add(targets);

            return row;
        }

        private static VisualElement CreateVisibleTargetLanes(
            FlowGraphVisibleSnapshot visibleSnapshot
        )
        {
            FlowGraphTargetLane[] lanes = BuildVisibleTargetLanes(visibleSnapshot);
            VisualElement targetLanes = new() { name = VisibleTargetLanesName };
            targetLanes.style.borderTopWidth = 1;
            targetLanes.style.borderTopColor = DxMessagingEditorPalette.BorderPanel;
            targetLanes.style.borderBottomWidth = 1;
            targetLanes.style.borderBottomColor = DxMessagingEditorPalette.BorderPanel;
            targetLanes.style.marginTop = 8;
            targetLanes.style.marginBottom = 4;
            targetLanes.style.paddingTop = 8;
            targetLanes.style.paddingBottom = 8;

            Label title = new("Visible Target Lanes");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            targetLanes.Add(title);

            Label summary = new(CreateVisibleTargetLanesSummaryText(lanes))
            {
                name = VisibleTargetLanesSummaryLabelName,
            };
            summary.style.marginTop = 2;
            summary.style.whiteSpace = WhiteSpace.Normal;
            targetLanes.Add(summary);

            int totalCalls = lanes.Sum(lane => lane.CallCount);
            foreach (FlowGraphTargetLane lane in lanes)
            {
                targetLanes.Add(CreateVisibleTargetLaneRow(lane, totalCalls));
            }

            return targetLanes;
        }

        private static VisualElement CreateVisibleTargetLaneRow(
            FlowGraphTargetLane lane,
            int totalCalls
        )
        {
            VisualElement row = new();
            row.AddToClassList(VisibleTargetLaneRowClassName);
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.borderLeftWidth = 3;
            row.style.borderLeftColor = DxMessagingEditorPalette.AmberSoft;
            row.style.borderTopWidth = 1;
            row.style.borderTopColor = DxMessagingEditorPalette.BorderSoft;
            row.style.marginTop = 6;
            row.style.paddingTop = 7;
            row.style.paddingRight = 8;
            row.style.paddingBottom = 7;
            row.style.paddingLeft = 10;

            Label target = new(lane.TargetComponentPath)
            {
                name = VisibleTargetLaneTargetLabelName,
            };
            target.style.flexBasis = 0;
            target.style.flexGrow = 2;
            target.style.unityFontStyleAndWeight = FontStyle.Bold;
            target.style.whiteSpace = WhiteSpace.Normal;
            row.Add(target);

            Label summary = new(
                $"State: {lane.TargetStateText} | Routes: {lane.RouteCount} | Messages: {lane.MessageCount} | Registrations: {lane.RegistrationCount} | Calls: {lane.CallCount} | Recent traced: {lane.RecentTracedDeliveryCount} | No-call routes: {lane.NoCallRouteCount} | Route kinds: {lane.RouteKindsText} | Share: {CreateCallShareText(lane.CallCount, totalCalls)} | Target id: {lane.TargetComponentId}"
            )
            {
                name = VisibleTargetLaneSummaryLabelName,
            };
            summary.style.flexBasis = 0;
            summary.style.flexGrow = 2;
            summary.style.marginLeft = 8;
            summary.style.whiteSpace = WhiteSpace.Normal;
            row.Add(summary);

            Label messages = new($"Messages: {lane.MessageTypesText}")
            {
                name = VisibleTargetLaneMessagesLabelName,
            };
            messages.style.flexBasis = 0;
            messages.style.flexGrow = 2;
            messages.style.marginLeft = 8;
            messages.style.whiteSpace = WhiteSpace.Normal;
            row.Add(messages);

            return row;
        }

        private static VisualElement CreateVisibleFlowCorridors(
            FlowGraphVisibleSnapshot visibleSnapshot
        )
        {
            FlowGraphFlowCorridor[] corridors = BuildVisibleFlowCorridors(
                visibleSnapshot.TracePaths
            );
            VisualElement flowCorridors = new() { name = VisibleFlowCorridorsName };
            flowCorridors.style.borderTopWidth = 1;
            flowCorridors.style.borderTopColor = DxMessagingEditorPalette.BorderPanel;
            flowCorridors.style.borderBottomWidth = 1;
            flowCorridors.style.borderBottomColor = DxMessagingEditorPalette.BorderPanel;
            flowCorridors.style.marginTop = 8;
            flowCorridors.style.marginBottom = 4;
            flowCorridors.style.paddingTop = 8;
            flowCorridors.style.paddingBottom = 8;

            Label title = new("Visible Flow Corridors");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            flowCorridors.Add(title);

            Label summary = new(CreateVisibleFlowCorridorsSummaryText(corridors))
            {
                name = VisibleFlowCorridorsSummaryLabelName,
            };
            summary.style.marginTop = 2;
            summary.style.whiteSpace = WhiteSpace.Normal;
            flowCorridors.Add(summary);

            int totalDeliveries = corridors.Sum(corridor => corridor.DeliveryCount);
            foreach (FlowGraphFlowCorridor corridor in corridors)
            {
                flowCorridors.Add(CreateVisibleFlowCorridorRow(corridor, totalDeliveries));
            }

            return flowCorridors;
        }

        private static VisualElement CreateVisibleFlowCorridorRow(
            FlowGraphFlowCorridor corridor,
            int totalDeliveries
        )
        {
            VisualElement row = new();
            row.AddToClassList(VisibleFlowCorridorRowClassName);
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.borderLeftWidth = 3;
            row.style.borderLeftColor = DxMessagingEditorPalette.AmberSoft;
            row.style.borderTopWidth = 1;
            row.style.borderTopColor = DxMessagingEditorPalette.BorderSoft;
            row.style.marginTop = 6;
            row.style.paddingTop = 7;
            row.style.paddingRight = 8;
            row.style.paddingBottom = 7;
            row.style.paddingLeft = 10;

            Label message = new(corridor.MessageTypeName)
            {
                name = VisibleFlowCorridorMessageLabelName,
            };
            message.style.flexBasis = 0;
            message.style.flexGrow = 2;
            message.style.unityFontStyleAndWeight = FontStyle.Bold;
            message.style.whiteSpace = WhiteSpace.Normal;
            row.Add(message);

            Label summary = new(
                $"Paths: {corridor.PathCount} | Contexts: {corridor.ContextCount} | Trace ids: {corridor.TraceIdCount} | Route kinds: {corridor.RouteKindsText} | Deliveries: {corridor.DeliveryCount} | Share: {CreateCallShareText(corridor.DeliveryCount, totalDeliveries)}"
            )
            {
                name = VisibleFlowCorridorSummaryLabelName,
            };
            summary.style.flexBasis = 0;
            summary.style.flexGrow = 2;
            summary.style.marginLeft = 8;
            summary.style.whiteSpace = WhiteSpace.Normal;
            row.Add(summary);

            Label target = new(corridor.TargetComponentPath)
            {
                name = VisibleFlowCorridorTargetLabelName,
            };
            target.style.flexBasis = 0;
            target.style.flexGrow = 2;
            target.style.marginLeft = 8;
            target.style.whiteSpace = WhiteSpace.Normal;
            row.Add(target);

            return row;
        }

        private static VisualElement CreateVisibleTraceRouteKindLanes(
            FlowGraphVisibleSnapshot visibleSnapshot
        )
        {
            FlowGraphTraceRouteKindLane[] lanes = BuildVisibleTraceRouteKindLanes(
                visibleSnapshot.TracePaths
            );
            VisualElement traceRouteKindLanes = new() { name = VisibleTraceRouteKindLanesName };
            traceRouteKindLanes.style.borderTopWidth = 1;
            traceRouteKindLanes.style.borderTopColor = DxMessagingEditorPalette.BorderStrong;
            traceRouteKindLanes.style.borderBottomWidth = 1;
            traceRouteKindLanes.style.borderBottomColor = DxMessagingEditorPalette.BorderStrong;
            traceRouteKindLanes.style.marginTop = 8;
            traceRouteKindLanes.style.marginBottom = 4;
            traceRouteKindLanes.style.paddingTop = 8;
            traceRouteKindLanes.style.paddingBottom = 8;

            Label title = new("Visible Trace Route Kind Lanes");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            traceRouteKindLanes.Add(title);

            Label summary = new(CreateVisibleTraceRouteKindLanesSummaryText(lanes))
            {
                name = VisibleTraceRouteKindLanesSummaryLabelName,
            };
            summary.style.marginTop = 2;
            summary.style.whiteSpace = WhiteSpace.Normal;
            traceRouteKindLanes.Add(summary);

            int totalDeliveries = lanes.Sum(lane => lane.DeliveryCount);
            foreach (FlowGraphTraceRouteKindLane lane in lanes)
            {
                traceRouteKindLanes.Add(CreateVisibleTraceRouteKindLaneRow(lane, totalDeliveries));
            }

            return traceRouteKindLanes;
        }

        private static VisualElement CreateVisibleTraceRouteKindLaneRow(
            FlowGraphTraceRouteKindLane lane,
            int totalDeliveries
        )
        {
            VisualElement row = new();
            row.AddToClassList(VisibleTraceRouteKindLaneRowClassName);
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.borderLeftWidth = 3;
            row.style.borderLeftColor = DxMessagingEditorPalette.RouteKindColor(lane.RouteKind);
            row.style.borderTopWidth = 1;
            row.style.borderTopColor = DxMessagingEditorPalette.Border;
            row.style.marginTop = 6;
            row.style.paddingTop = 7;
            row.style.paddingRight = 8;
            row.style.paddingBottom = 7;
            row.style.paddingLeft = 10;

            Label routeKind = new(lane.RouteKind)
            {
                name = VisibleTraceRouteKindLaneRouteKindLabelName,
            };
            routeKind.style.flexBasis = 0;
            routeKind.style.flexGrow = 1;
            routeKind.style.unityFontStyleAndWeight = FontStyle.Bold;
            routeKind.style.whiteSpace = WhiteSpace.Normal;
            row.Add(routeKind);

            Label summary = new(
                $"Paths: {lane.PathCount} | Messages: {lane.MessageCount} | Targets: {lane.TargetCount} | Contexts: {lane.ContextCount} | Trace ids: {lane.TraceIdCount} | Deliveries: {lane.DeliveryCount} | Share: {CreateCallShareText(lane.DeliveryCount, totalDeliveries)}"
            )
            {
                name = VisibleTraceRouteKindLaneSummaryLabelName,
            };
            summary.style.flexBasis = 0;
            summary.style.flexGrow = 2;
            summary.style.marginLeft = 8;
            summary.style.whiteSpace = WhiteSpace.Normal;
            row.Add(summary);

            Label details = new(
                $"Messages: {lane.MessageTypesText} | Targets: {lane.TargetPathsText} | Contexts: {lane.ContextsText}"
            )
            {
                name = VisibleTraceRouteKindLaneDetailsLabelName,
            };
            details.style.flexBasis = 0;
            details.style.flexGrow = 3;
            details.style.marginLeft = 8;
            details.style.whiteSpace = WhiteSpace.Normal;
            row.Add(details);

            return row;
        }

        private static VisualElement CreateVisibleTraceIdLanes(
            FlowGraphVisibleSnapshot visibleSnapshot
        )
        {
            FlowGraphTraceIdLane[] lanes = BuildVisibleTraceIdLanes(visibleSnapshot.TracePaths);
            VisualElement traceIdLanes = new() { name = VisibleTraceIdLanesName };
            traceIdLanes.style.borderTopWidth = 1;
            traceIdLanes.style.borderTopColor = DxMessagingEditorPalette.BorderStrong;
            traceIdLanes.style.borderBottomWidth = 1;
            traceIdLanes.style.borderBottomColor = DxMessagingEditorPalette.BorderStrong;
            traceIdLanes.style.marginTop = 8;
            traceIdLanes.style.marginBottom = 4;
            traceIdLanes.style.paddingTop = 8;
            traceIdLanes.style.paddingBottom = 8;

            Label title = new("Visible Trace Id Lanes");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            traceIdLanes.Add(title);

            Label summary = new(CreateVisibleTraceIdLanesSummaryText(lanes))
            {
                name = VisibleTraceIdLanesSummaryLabelName,
            };
            summary.style.marginTop = 2;
            summary.style.whiteSpace = WhiteSpace.Normal;
            traceIdLanes.Add(summary);

            int totalPathMemberships = lanes.Sum(lane => lane.PathCount);
            foreach (FlowGraphTraceIdLane lane in lanes)
            {
                traceIdLanes.Add(CreateVisibleTraceIdLaneRow(lane, totalPathMemberships));
            }

            return traceIdLanes;
        }

        private static VisualElement CreateVisibleTraceIdLaneRow(
            FlowGraphTraceIdLane lane,
            int totalPathMemberships
        )
        {
            VisualElement row = new();
            row.AddToClassList(VisibleTraceIdLaneRowClassName);
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.borderLeftWidth = 3;
            row.style.borderLeftColor = DxMessagingEditorPalette.Trace;
            row.style.borderTopWidth = 1;
            row.style.borderTopColor = DxMessagingEditorPalette.Border;
            row.style.marginTop = 6;
            row.style.paddingTop = 7;
            row.style.paddingRight = 8;
            row.style.paddingBottom = 7;
            row.style.paddingLeft = 10;

            Label traceId = new(lane.TraceId.ToString(CultureInfo.InvariantCulture))
            {
                name = VisibleTraceIdLaneTraceIdLabelName,
            };
            traceId.style.flexBasis = 0;
            traceId.style.flexGrow = 1;
            traceId.style.unityFontStyleAndWeight = FontStyle.Bold;
            traceId.style.whiteSpace = WhiteSpace.Normal;
            row.Add(traceId);

            Label summary = new(
                $"Paths: {lane.PathCount} | Messages: {lane.MessageCount} | Targets: {lane.TargetCount} | Contexts: {lane.ContextCount} | Route kinds: {lane.RouteKindsText} | Share: {CreateCallShareText(lane.PathCount, totalPathMemberships)}"
            )
            {
                name = VisibleTraceIdLaneSummaryLabelName,
            };
            summary.style.flexBasis = 0;
            summary.style.flexGrow = 2;
            summary.style.marginLeft = 8;
            summary.style.whiteSpace = WhiteSpace.Normal;
            row.Add(summary);

            Label details = new(
                $"Messages: {lane.MessageTypesText} | Targets: {lane.TargetPathsText} | Contexts: {lane.ContextsText}"
            )
            {
                name = VisibleTraceIdLaneDetailsLabelName,
            };
            details.style.flexBasis = 0;
            details.style.flexGrow = 3;
            details.style.marginLeft = 8;
            details.style.whiteSpace = WhiteSpace.Normal;
            row.Add(details);

            return row;
        }

        private static VisualElement CreateVisibleTraceMessageLanes(
            FlowGraphVisibleSnapshot visibleSnapshot
        )
        {
            FlowGraphTraceMessageLane[] lanes = BuildVisibleTraceMessageLanes(
                visibleSnapshot.TracePaths
            );
            VisualElement traceMessageLanes = new() { name = VisibleTraceMessageLanesName };
            traceMessageLanes.style.borderTopWidth = 1;
            traceMessageLanes.style.borderTopColor = DxMessagingEditorPalette.BorderStrong;
            traceMessageLanes.style.borderBottomWidth = 1;
            traceMessageLanes.style.borderBottomColor = DxMessagingEditorPalette.BorderStrong;
            traceMessageLanes.style.marginTop = 8;
            traceMessageLanes.style.marginBottom = 4;
            traceMessageLanes.style.paddingTop = 8;
            traceMessageLanes.style.paddingBottom = 8;

            Label title = new("Visible Trace Message Lanes");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            traceMessageLanes.Add(title);

            Label summary = new(CreateVisibleTraceMessageLanesSummaryText(lanes))
            {
                name = VisibleTraceMessageLanesSummaryLabelName,
            };
            summary.style.marginTop = 2;
            summary.style.whiteSpace = WhiteSpace.Normal;
            traceMessageLanes.Add(summary);

            int totalDeliveries = lanes.Sum(lane => lane.DeliveryCount);
            foreach (FlowGraphTraceMessageLane lane in lanes)
            {
                traceMessageLanes.Add(CreateVisibleTraceMessageLaneRow(lane, totalDeliveries));
            }

            return traceMessageLanes;
        }

        private static VisualElement CreateVisibleTraceMessageLaneRow(
            FlowGraphTraceMessageLane lane,
            int totalDeliveries
        )
        {
            VisualElement row = new();
            row.AddToClassList(VisibleTraceMessageLaneRowClassName);
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.borderLeftWidth = 3;
            row.style.borderLeftColor = DxMessagingEditorPalette.TraceMessage;
            row.style.borderTopWidth = 1;
            row.style.borderTopColor = DxMessagingEditorPalette.Border;
            row.style.marginTop = 6;
            row.style.paddingTop = 7;
            row.style.paddingRight = 8;
            row.style.paddingBottom = 7;
            row.style.paddingLeft = 10;

            Label message = new(lane.MessageTypeName)
            {
                name = VisibleTraceMessageLaneMessageLabelName,
            };
            message.style.flexBasis = 0;
            message.style.flexGrow = 2;
            message.style.unityFontStyleAndWeight = FontStyle.Bold;
            message.style.whiteSpace = WhiteSpace.Normal;
            row.Add(message);

            Label summary = new(
                $"Paths: {lane.PathCount} | Contexts: {lane.ContextCount} | Targets: {lane.TargetCount} | Trace ids: {lane.TraceIdCount} | Route kinds: {lane.RouteKindsText} | Deliveries: {lane.DeliveryCount} | Share: {CreateCallShareText(lane.DeliveryCount, totalDeliveries)}"
            )
            {
                name = VisibleTraceMessageLaneSummaryLabelName,
            };
            summary.style.flexBasis = 0;
            summary.style.flexGrow = 2;
            summary.style.marginLeft = 8;
            summary.style.whiteSpace = WhiteSpace.Normal;
            row.Add(summary);

            Label details = new($"Contexts: {lane.ContextsText} | Targets: {lane.TargetPathsText}")
            {
                name = VisibleTraceMessageLaneDetailsLabelName,
            };
            details.style.flexBasis = 0;
            details.style.flexGrow = 2;
            details.style.marginLeft = 8;
            details.style.whiteSpace = WhiteSpace.Normal;
            row.Add(details);

            return row;
        }

        private static VisualElement CreateVisibleTraceTargetLanes(
            FlowGraphVisibleSnapshot visibleSnapshot
        )
        {
            FlowGraphTraceTargetLane[] lanes = BuildVisibleTraceTargetLanes(
                visibleSnapshot.TracePaths
            );
            VisualElement traceTargetLanes = new() { name = VisibleTraceTargetLanesName };
            traceTargetLanes.style.borderTopWidth = 1;
            traceTargetLanes.style.borderTopColor = DxMessagingEditorPalette.BorderStrong;
            traceTargetLanes.style.borderBottomWidth = 1;
            traceTargetLanes.style.borderBottomColor = DxMessagingEditorPalette.BorderStrong;
            traceTargetLanes.style.marginTop = 8;
            traceTargetLanes.style.marginBottom = 4;
            traceTargetLanes.style.paddingTop = 8;
            traceTargetLanes.style.paddingBottom = 8;

            Label title = new("Visible Trace Target Lanes");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            traceTargetLanes.Add(title);

            Label summary = new(CreateVisibleTraceTargetLanesSummaryText(lanes))
            {
                name = VisibleTraceTargetLanesSummaryLabelName,
            };
            summary.style.marginTop = 2;
            summary.style.whiteSpace = WhiteSpace.Normal;
            traceTargetLanes.Add(summary);

            int totalDeliveries = lanes.Sum(lane => lane.DeliveryCount);
            foreach (FlowGraphTraceTargetLane lane in lanes)
            {
                traceTargetLanes.Add(CreateVisibleTraceTargetLaneRow(lane, totalDeliveries));
            }

            return traceTargetLanes;
        }

        private static VisualElement CreateVisibleTraceTargetLaneRow(
            FlowGraphTraceTargetLane lane,
            int totalDeliveries
        )
        {
            VisualElement row = new();
            row.AddToClassList(VisibleTraceTargetLaneRowClassName);
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.borderLeftWidth = 3;
            row.style.borderLeftColor = DxMessagingEditorPalette.TraceTarget;
            row.style.borderTopWidth = 1;
            row.style.borderTopColor = DxMessagingEditorPalette.Border;
            row.style.marginTop = 6;
            row.style.paddingTop = 7;
            row.style.paddingRight = 8;
            row.style.paddingBottom = 7;
            row.style.paddingLeft = 10;

            Label target = new(lane.TargetDisplayPath)
            {
                name = VisibleTraceTargetLaneTargetLabelName,
            };
            target.style.flexBasis = 0;
            target.style.flexGrow = 2;
            target.style.unityFontStyleAndWeight = FontStyle.Bold;
            target.style.whiteSpace = WhiteSpace.Normal;
            row.Add(target);

            Label summary = new(
                $"Paths: {lane.PathCount} | Messages: {lane.MessageCount} | Contexts: {lane.ContextCount} | Trace ids: {lane.TraceIdCount} | Route kinds: {lane.RouteKindsText} | Deliveries: {lane.DeliveryCount} | Share: {CreateCallShareText(lane.DeliveryCount, totalDeliveries)}"
            )
            {
                name = VisibleTraceTargetLaneSummaryLabelName,
            };
            summary.style.flexBasis = 0;
            summary.style.flexGrow = 2;
            summary.style.marginLeft = 8;
            summary.style.whiteSpace = WhiteSpace.Normal;
            row.Add(summary);

            Label details = new(
                $"Messages: {lane.MessageTypesText} | Contexts: {lane.ContextsText}"
            )
            {
                name = VisibleTraceTargetLaneDetailsLabelName,
            };
            details.style.flexBasis = 0;
            details.style.flexGrow = 2;
            details.style.marginLeft = 8;
            details.style.whiteSpace = WhiteSpace.Normal;
            row.Add(details);

            return row;
        }

        private static VisualElement CreateVisibleContextLanes(
            FlowGraphVisibleSnapshot visibleSnapshot
        )
        {
            FlowGraphContextLane[] lanes = BuildVisibleContextLanes(visibleSnapshot.TracePaths);
            VisualElement contextLanes = new() { name = VisibleContextLanesName };
            contextLanes.style.borderTopWidth = 1;
            contextLanes.style.borderTopColor = DxMessagingEditorPalette.BorderStrong;
            contextLanes.style.borderBottomWidth = 1;
            contextLanes.style.borderBottomColor = DxMessagingEditorPalette.BorderStrong;
            contextLanes.style.marginTop = 8;
            contextLanes.style.marginBottom = 4;
            contextLanes.style.paddingTop = 8;
            contextLanes.style.paddingBottom = 8;

            Label title = new("Visible Trace Context Lanes");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            contextLanes.Add(title);

            Label summary = new(CreateVisibleContextLanesSummaryText(lanes))
            {
                name = VisibleContextLanesSummaryLabelName,
            };
            summary.style.marginTop = 2;
            summary.style.whiteSpace = WhiteSpace.Normal;
            contextLanes.Add(summary);

            int totalDeliveries = lanes.Sum(lane => lane.DeliveryCount);
            foreach (FlowGraphContextLane lane in lanes)
            {
                contextLanes.Add(CreateVisibleContextLaneRow(lane, totalDeliveries));
            }

            return contextLanes;
        }

        private static VisualElement CreateVisibleContextLaneRow(
            FlowGraphContextLane lane,
            int totalDeliveries
        )
        {
            VisualElement row = new();
            row.AddToClassList(VisibleContextLaneRowClassName);
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.borderLeftWidth = 3;
            row.style.borderLeftColor = DxMessagingEditorPalette.Amber;
            row.style.borderTopWidth = 1;
            row.style.borderTopColor = DxMessagingEditorPalette.Border;
            row.style.marginTop = 6;
            row.style.paddingTop = 7;
            row.style.paddingRight = 8;
            row.style.paddingBottom = 7;
            row.style.paddingLeft = 10;

            Label context = new(lane.Context) { name = VisibleContextLaneContextLabelName };
            context.style.flexBasis = 0;
            context.style.flexGrow = 2;
            context.style.unityFontStyleAndWeight = FontStyle.Bold;
            context.style.whiteSpace = WhiteSpace.Normal;
            row.Add(context);

            Label summary = new(
                $"Paths: {lane.PathCount} | Messages: {lane.MessageCount} | Targets: {lane.TargetCount} | Trace ids: {lane.TraceIdCount} | Route kinds: {lane.RouteKindsText} | Deliveries: {lane.DeliveryCount} | Share: {CreateCallShareText(lane.DeliveryCount, totalDeliveries)}"
            )
            {
                name = VisibleContextLaneSummaryLabelName,
            };
            summary.style.flexBasis = 0;
            summary.style.flexGrow = 2;
            summary.style.marginLeft = 8;
            summary.style.whiteSpace = WhiteSpace.Normal;
            row.Add(summary);

            Label details = new(
                $"Messages: {lane.MessageTypesText} | Targets: {lane.TargetPathsText}"
            )
            {
                name = VisibleContextLaneDetailsLabelName,
            };
            details.style.flexBasis = 0;
            details.style.flexGrow = 2;
            details.style.marginLeft = 8;
            details.style.whiteSpace = WhiteSpace.Normal;
            row.Add(details);

            return row;
        }

        private static VisualElement CreateTracePaths(FlowGraphVisibleSnapshot visibleSnapshot)
        {
            VisualElement tracePaths = new() { name = TracePathsName };
            tracePaths.style.borderTopWidth = 1;
            tracePaths.style.borderTopColor = DxMessagingEditorPalette.BorderPanel;
            tracePaths.style.borderBottomWidth = 1;
            tracePaths.style.borderBottomColor = DxMessagingEditorPalette.BorderPanel;
            tracePaths.style.marginTop = 8;
            tracePaths.style.marginBottom = 4;
            tracePaths.style.paddingTop = 8;
            tracePaths.style.paddingBottom = 8;

            Label title = new("Recent Trace Paths");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            tracePaths.Add(title);

            int tracedDeliveries = visibleSnapshot.TracePaths.Sum(path =>
                path.RecentTracedDeliveryCount
            );
            Label summary = new(
                $"{FormatCount(visibleSnapshot.TracePaths.Count, "traced path")} | Deliveries: {tracedDeliveries} | Trace ids: {CountDistinctTraceIds(visibleSnapshot.TracePaths)} | {CreateWidestTraceSummary(visibleSnapshot.TracePaths)} | {CreateTraceContextVolumeSummary(visibleSnapshot.TracePaths)} | {CreateBusiestTraceContextShareSummary(visibleSnapshot.TracePaths)} | {CreateBusiestTraceMessageSummary(visibleSnapshot.TracePaths)} | {CreateBusiestTraceTargetSummary(visibleSnapshot.TracePaths)} | {CreateBusiestTracePathSummary(visibleSnapshot.TracePaths)} | {CreateBusiestTracePathShareSummary(visibleSnapshot.TracePaths)}"
            )
            {
                name = TracePathsSummaryLabelName,
            };
            summary.style.marginTop = 2;
            summary.style.whiteSpace = WhiteSpace.Normal;
            tracePaths.Add(summary);

            foreach (FlowGraphTracePath path in visibleSnapshot.TracePaths)
            {
                tracePaths.Add(CreateTracePathRow(path));
            }

            return tracePaths;
        }

        private static VisualElement CreateTracePathRow(FlowGraphTracePath path)
        {
            VisualElement row = new();
            row.AddToClassList(TracePathRowClassName);
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.borderLeftWidth = 3;
            row.style.borderLeftColor = DxMessagingEditorPalette.Amber;
            row.style.borderTopWidth = 1;
            row.style.borderTopColor = DxMessagingEditorPalette.BorderSoft;
            row.style.marginTop = 6;
            row.style.paddingTop = 7;
            row.style.paddingRight = 8;
            row.style.paddingBottom = 7;
            row.style.paddingLeft = 10;

            Label message = new(path.MessageTypeName) { name = TracePathMessageLabelName };
            message.style.flexBasis = 0;
            message.style.flexGrow = 2;
            message.style.unityFontStyleAndWeight = FontStyle.Bold;
            message.style.whiteSpace = WhiteSpace.Normal;
            row.Add(message);

            Label summary = new(
                $"Context: {NormalizeTraceContext(path.Context)} | {path.RegistrationTypeName} | Deliveries: {path.RecentTracedDeliveryCount} | Trace ids: {path.RecentTraceIdCount}"
            )
            {
                name = TracePathSummaryLabelName,
            };
            summary.style.flexBasis = 0;
            summary.style.flexGrow = 2;
            summary.style.marginLeft = 8;
            summary.style.whiteSpace = WhiteSpace.Normal;
            row.Add(summary);

            Label target = new(path.TargetComponentPath) { name = TracePathTargetLabelName };
            target.style.flexBasis = 0;
            target.style.flexGrow = 2;
            target.style.marginLeft = 8;
            target.style.whiteSpace = WhiteSpace.Normal;
            row.Add(target);

            return row;
        }

        private static string CreateVisibleFlowCorridorsSummaryText(
            IReadOnlyList<FlowGraphFlowCorridor> corridors
        )
        {
            int totalDeliveries = corridors.Sum(corridor => corridor.DeliveryCount);
            if (corridors.Count == 0 || totalDeliveries <= 0 || corridors[0].DeliveryCount <= 0)
            {
                return $"{FormatCount(corridors.Count, "visible corridor")} | Deliveries: {totalDeliveries} | Busiest corridor: none";
            }

            FlowGraphFlowCorridor busiestCorridor = corridors[0];
            return $"{FormatCount(corridors.Count, "visible corridor")} | Deliveries: {totalDeliveries} | Busiest corridor: {busiestCorridor.MessageTypeName} -> {busiestCorridor.TargetComponentPath} | Share: {CreateCallShareText(busiestCorridor.DeliveryCount, totalDeliveries)}";
        }

        private static string CreateVisibleContextLanesSummaryText(
            IReadOnlyList<FlowGraphContextLane> lanes
        )
        {
            int totalDeliveries = lanes.Sum(lane => lane.DeliveryCount);
            int traceIdCount = lanes
                .SelectMany(lane => lane.TraceIds)
                .Where(traceId => traceId > 0)
                .Distinct()
                .Count();
            if (lanes.Count == 0 || totalDeliveries <= 0 || lanes[0].DeliveryCount <= 0)
            {
                return $"{FormatCount(lanes.Count, "context lane")} | Deliveries: {totalDeliveries} | Trace ids: {traceIdCount} | Busiest context: none";
            }

            FlowGraphContextLane busiestLane = lanes[0];
            return $"{FormatCount(lanes.Count, "context lane")} | Deliveries: {totalDeliveries} | Trace ids: {traceIdCount} | Busiest context: {busiestLane.Context} | Share: {CreateCallShareText(busiestLane.DeliveryCount, totalDeliveries)}";
        }

        private static string CreateVisibleTraceMessageLanesSummaryText(
            IReadOnlyList<FlowGraphTraceMessageLane> lanes
        )
        {
            int totalDeliveries = lanes.Sum(lane => lane.DeliveryCount);
            int traceIdCount = lanes
                .SelectMany(lane => lane.TraceIds)
                .Where(traceId => traceId > 0)
                .Distinct()
                .Count();
            if (lanes.Count == 0 || totalDeliveries <= 0 || lanes[0].DeliveryCount <= 0)
            {
                return $"{FormatCount(lanes.Count, "trace message lane")} | Deliveries: {totalDeliveries} | Trace ids: {traceIdCount} | Busiest trace message: none";
            }

            FlowGraphTraceMessageLane busiestLane = lanes[0];
            return $"{FormatCount(lanes.Count, "trace message lane")} | Deliveries: {totalDeliveries} | Trace ids: {traceIdCount} | Busiest trace message: {busiestLane.MessageTypeName} | Share: {CreateCallShareText(busiestLane.DeliveryCount, totalDeliveries)}";
        }

        private static string CreateVisibleTraceIdLanesSummaryText(
            IReadOnlyList<FlowGraphTraceIdLane> lanes
        )
        {
            int totalPathMemberships = lanes.Sum(lane => lane.PathCount);
            if (lanes.Count == 0 || totalPathMemberships <= 0 || lanes[0].PathCount <= 0)
            {
                return $"{FormatCount(lanes.Count, "trace id lane")} | Path memberships: {totalPathMemberships} | Widest trace id: none";
            }

            FlowGraphTraceIdLane widestLane = lanes[0];
            return $"{FormatCount(lanes.Count, "trace id lane")} | Path memberships: {totalPathMemberships} | Widest trace id: {widestLane.TraceId} | Share: {CreateCallShareText(widestLane.PathCount, totalPathMemberships)}";
        }

        private static string CreateVisibleTraceRouteKindLanesSummaryText(
            IReadOnlyList<FlowGraphTraceRouteKindLane> lanes
        )
        {
            int totalDeliveries = lanes.Sum(lane => lane.DeliveryCount);
            int traceIdCount = lanes
                .SelectMany(lane => lane.TraceIds)
                .Where(traceId => traceId > 0)
                .Distinct()
                .Count();
            if (lanes.Count == 0 || totalDeliveries <= 0 || lanes[0].DeliveryCount <= 0)
            {
                return $"{FormatCount(lanes.Count, "trace route kind lane")} | Deliveries: {totalDeliveries} | Trace ids: {traceIdCount} | Busiest trace route kind: none";
            }

            FlowGraphTraceRouteKindLane busiestLane = lanes[0];
            return $"{FormatCount(lanes.Count, "trace route kind lane")} | Deliveries: {totalDeliveries} | Trace ids: {traceIdCount} | Busiest trace route kind: {busiestLane.RouteKind} | Share: {CreateCallShareText(busiestLane.DeliveryCount, totalDeliveries)}";
        }

        private static string CreateVisibleTraceTargetLanesSummaryText(
            IReadOnlyList<FlowGraphTraceTargetLane> lanes
        )
        {
            int totalDeliveries = lanes.Sum(lane => lane.DeliveryCount);
            int traceIdCount = lanes
                .SelectMany(lane => lane.TraceIds)
                .Where(traceId => traceId > 0)
                .Distinct()
                .Count();
            if (lanes.Count == 0 || totalDeliveries <= 0 || lanes[0].DeliveryCount <= 0)
            {
                return $"{FormatCount(lanes.Count, "trace target lane")} | Deliveries: {totalDeliveries} | Trace ids: {traceIdCount} | Busiest trace target: none";
            }

            FlowGraphTraceTargetLane busiestLane = lanes[0];
            return $"{FormatCount(lanes.Count, "trace target lane")} | Deliveries: {totalDeliveries} | Trace ids: {traceIdCount} | Busiest trace target: {busiestLane.TargetDisplayPath} | Share: {CreateCallShareText(busiestLane.DeliveryCount, totalDeliveries)}";
        }

        private static string CreateVisibleMessageLanesSummaryText(
            IReadOnlyList<FlowGraphMessageLane> lanes
        )
        {
            int totalRoutes = lanes.Sum(lane => lane.RouteCount);
            int totalTargets = CountDistinct(lanes.SelectMany(lane => lane.TargetComponentIds));
            int totalCalls = lanes.Sum(lane => lane.CallCount);
            int totalTracedDeliveries = lanes.Sum(lane => lane.RecentTracedDeliveryCount);
            int noCallRouteCount = lanes.Sum(lane => lane.NoCallRouteCount);
            if (lanes.Count == 0 || totalCalls <= 0)
            {
                return $"{FormatCount(lanes.Count, "message lane")} | Routes: {totalRoutes} | Targets: {totalTargets} | Calls: {totalCalls} | Recent traced: {totalTracedDeliveries} | No-call routes: {noCallRouteCount} | Busiest lane: none";
            }

            FlowGraphMessageLane busiestLane = lanes
                .OrderByDescending(lane => lane.CallCount)
                .ThenByDescending(lane => lane.RouteCount)
                .ThenByDescending(lane => lane.RecentTracedDeliveryCount)
                .ThenBy(lane => lane.MessageTypeName, StringComparer.Ordinal)
                .First();
            return $"{FormatCount(lanes.Count, "message lane")} | Routes: {totalRoutes} | Targets: {totalTargets} | Calls: {totalCalls} | Recent traced: {totalTracedDeliveries} | No-call routes: {noCallRouteCount} | Busiest lane: {busiestLane.MessageTypeName} | Share: {CreateCallShareText(busiestLane.CallCount, totalCalls)}";
        }

        private static FlowGraphMessageLane[] BuildVisibleMessageLanes(
            FlowGraphVisibleSnapshot visibleSnapshot
        )
        {
            Dictionary<string, FlowGraphComponentNode> componentsById = visibleSnapshot
                .ComponentNodes.Where(component => !string.IsNullOrWhiteSpace(component.Id))
                .GroupBy(component => component.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            return visibleSnapshot
                .Edges.GroupBy(edge => edge.MessageTypeName, StringComparer.Ordinal)
                .Select(group =>
                {
                    FlowGraphEdge[] groupEdges = group.ToArray();
                    string[] targetComponentIds = groupEdges
                        .Select(edge => edge.TargetComponentId)
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(id => id, StringComparer.Ordinal)
                        .ToArray();
                    string[] targetPaths = groupEdges
                        .OrderBy(edge => edge.TargetComponentPath, StringComparer.Ordinal)
                        .ThenBy(edge => edge.TargetComponentId, StringComparer.Ordinal)
                        .Select(edge => edge.TargetComponentPath)
                        .Where(path => !string.IsNullOrWhiteSpace(path))
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    string[] routeKinds = CreateVisibleRouteKindList(
                        groupEdges.Select(edge => edge.RegistrationTypeName)
                    );
                    int inactiveTargetCount = targetComponentIds.Count(targetId =>
                        componentsById.TryGetValue(targetId, out FlowGraphComponentNode component)
                        && !component.ActiveInHierarchy
                    );

                    return new FlowGraphMessageLane(
                        group.Key,
                        groupEdges.Length,
                        targetComponentIds,
                        targetPaths,
                        routeKinds,
                        groupEdges.Sum(edge => edge.RegistrationCount),
                        groupEdges.Sum(edge => edge.CallCount),
                        groupEdges.Sum(edge => edge.RecentTracedDeliveryCount),
                        groupEdges.Count(edge => edge.CallCount <= 0),
                        inactiveTargetCount
                    );
                })
                .OrderByDescending(lane => lane.RouteCount)
                .ThenByDescending(lane => lane.CallCount)
                .ThenByDescending(lane => lane.RecentTracedDeliveryCount)
                .ThenBy(lane => lane.MessageTypeName, StringComparer.Ordinal)
                .ToArray();
        }

        private static string CreateVisibleTargetLanesSummaryText(
            IReadOnlyList<FlowGraphTargetLane> lanes
        )
        {
            int totalRoutes = lanes.Sum(lane => lane.RouteCount);
            int totalMessages = CountDistinct(lanes.SelectMany(lane => lane.MessageTypes));
            int totalCalls = lanes.Sum(lane => lane.CallCount);
            int totalTracedDeliveries = lanes.Sum(lane => lane.RecentTracedDeliveryCount);
            int noCallRouteCount = lanes.Sum(lane => lane.NoCallRouteCount);
            if (lanes.Count == 0 || totalCalls <= 0)
            {
                return $"{FormatCount(lanes.Count, "target lane")} | Routes: {totalRoutes} | Messages: {totalMessages} | Calls: {totalCalls} | Recent traced: {totalTracedDeliveries} | No-call routes: {noCallRouteCount} | Busiest target: none";
            }

            FlowGraphTargetLane busiestLane = lanes
                .OrderByDescending(lane => lane.CallCount)
                .ThenByDescending(lane => lane.RouteCount)
                .ThenByDescending(lane => lane.RecentTracedDeliveryCount)
                .ThenBy(lane => lane.TargetComponentPath, StringComparer.Ordinal)
                .ThenBy(lane => lane.TargetComponentId, StringComparer.Ordinal)
                .First();
            return $"{FormatCount(lanes.Count, "target lane")} | Routes: {totalRoutes} | Messages: {totalMessages} | Calls: {totalCalls} | Recent traced: {totalTracedDeliveries} | No-call routes: {noCallRouteCount} | Busiest target: {busiestLane.TargetComponentPath} | Share: {CreateCallShareText(busiestLane.CallCount, totalCalls)}";
        }

        private static FlowGraphTargetLane[] BuildVisibleTargetLanes(
            FlowGraphVisibleSnapshot visibleSnapshot
        )
        {
            Dictionary<string, FlowGraphComponentNode> componentsById = visibleSnapshot
                .ComponentNodes.Where(component => !string.IsNullOrWhiteSpace(component.Id))
                .GroupBy(component => component.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            return visibleSnapshot
                .Edges.GroupBy(CreateVisibleTargetLaneKey, StringComparer.Ordinal)
                .Select(group =>
                {
                    FlowGraphEdge[] groupEdges = group.ToArray();
                    FlowGraphEdge firstEdge = groupEdges
                        .OrderBy(edge => edge.TargetComponentPath, StringComparer.Ordinal)
                        .ThenBy(edge => edge.TargetComponentId, StringComparer.Ordinal)
                        .First();
                    string[] messageTypes = groupEdges
                        .Select(edge => edge.MessageTypeName)
                        .Where(message => !string.IsNullOrWhiteSpace(message))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(message => message, StringComparer.Ordinal)
                        .ToArray();
                    string[] routeKinds = CreateVisibleRouteKindList(
                        groupEdges.Select(edge => edge.RegistrationTypeName)
                    );
                    string targetStateText = componentsById.TryGetValue(
                        firstEdge.TargetComponentId,
                        out FlowGraphComponentNode component
                    )
                        ? component.ActiveInHierarchy
                            ? "active"
                            : "inactive"
                        : "unknown";

                    return new FlowGraphTargetLane(
                        firstEdge.TargetComponentId,
                        firstEdge.TargetComponentPath,
                        targetStateText,
                        groupEdges.Length,
                        messageTypes,
                        routeKinds,
                        groupEdges.Sum(edge => edge.RegistrationCount),
                        groupEdges.Sum(edge => edge.CallCount),
                        groupEdges.Sum(edge => edge.RecentTracedDeliveryCount),
                        groupEdges.Count(edge => edge.CallCount <= 0)
                    );
                })
                .OrderByDescending(lane => lane.RouteCount)
                .ThenByDescending(lane => lane.CallCount)
                .ThenByDescending(lane => lane.RecentTracedDeliveryCount)
                .ThenBy(lane => lane.TargetComponentPath, StringComparer.Ordinal)
                .ThenBy(lane => lane.TargetComponentId, StringComparer.Ordinal)
                .ToArray();
        }

        private static string CreateVisibleTargetLaneKey(FlowGraphEdge edge)
        {
            return string.Join(
                "|",
                edge.TargetComponentId ?? string.Empty,
                edge.TargetComponentPath ?? string.Empty
            );
        }

        private static FlowGraphFlowCorridor[] BuildVisibleFlowCorridors(
            IEnumerable<FlowGraphTracePath> tracePaths
        )
        {
            return tracePaths
                .GroupBy(CreateVisibleFlowCorridorKey, StringComparer.Ordinal)
                .Select(group =>
                {
                    FlowGraphTracePath[] groupPaths = group.ToArray();
                    FlowGraphTracePath firstPath = groupPaths
                        .OrderBy(path => path.TargetComponentPath, StringComparer.Ordinal)
                        .ThenBy(path => path.TargetComponentId, StringComparer.Ordinal)
                        .First();
                    string[] routeKinds = CreateVisibleRouteKindList(
                        groupPaths.Select(path => path.RegistrationTypeName)
                    );

                    return new FlowGraphFlowCorridor(
                        firstPath.MessageTypeName,
                        firstPath.TargetComponentId,
                        firstPath.TargetComponentPath,
                        groupPaths.Length,
                        CountDistinct(
                            groupPaths.Select(path => NormalizeTraceContext(path.Context))
                        ),
                        CountDistinctTraceIds(groupPaths),
                        routeKinds,
                        groupPaths.Sum(path => path.RecentTracedDeliveryCount)
                    );
                })
                .OrderByDescending(corridor => corridor.DeliveryCount)
                .ThenByDescending(corridor => corridor.PathCount)
                .ThenBy(corridor => corridor.MessageTypeName, StringComparer.Ordinal)
                .ThenBy(corridor => corridor.TargetComponentPath, StringComparer.Ordinal)
                .ThenBy(corridor => corridor.TargetComponentId, StringComparer.Ordinal)
                .ToArray();
        }

        private static string CreateVisibleFlowCorridorKey(FlowGraphTracePath path)
        {
            return string.Join(
                "|",
                path.MessageTypeName ?? string.Empty,
                path.TargetComponentId ?? string.Empty
            );
        }

        private static string[] CreateVisibleRouteKindList(IEnumerable<string> routeKinds)
        {
            return routeKinds
                .Select(CreateVisibleRouteKindLabel)
                .Where(kind => !string.IsNullOrWhiteSpace(kind))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(kind => kind, StringComparer.Ordinal)
                .ToArray();
        }

        private static string CreateVisibleRouteKindLabel(string routeKind)
        {
            string taxonomyKind = DxMessagingEditorPalette.NormalizeRouteKind(routeKind);
            if (!string.IsNullOrWhiteSpace(taxonomyKind))
            {
                return taxonomyKind;
            }

            return string.IsNullOrWhiteSpace(routeKind) ? string.Empty : routeKind.Trim();
        }

        private static FlowGraphContextLane[] BuildVisibleContextLanes(
            IEnumerable<FlowGraphTracePath> tracePaths
        )
        {
            return tracePaths
                .GroupBy(path => NormalizeTraceContext(path.Context), StringComparer.Ordinal)
                .Select(group =>
                {
                    FlowGraphTracePath[] groupPaths = group.ToArray();
                    string[] messageTypes = groupPaths
                        .Select(path => path.MessageTypeName)
                        .Where(message => !string.IsNullOrWhiteSpace(message))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(message => message, StringComparer.Ordinal)
                        .ToArray();
                    string[] targetComponentIds = groupPaths
                        .Select(path => path.TargetComponentId)
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(id => id, StringComparer.Ordinal)
                        .ToArray();
                    string[] targetDisplayPaths = CreateTraceTargetDisplayPaths(groupPaths);
                    string[] routeKinds = CreateVisibleRouteKindList(
                        groupPaths.Select(path => path.RegistrationTypeName)
                    );
                    long[] traceIds = groupPaths
                        .SelectMany(path => path.TraceIds)
                        .Where(traceId => traceId > 0)
                        .Distinct()
                        .OrderBy(traceId => traceId)
                        .ToArray();

                    return new FlowGraphContextLane(
                        group.Key,
                        groupPaths.Length,
                        messageTypes,
                        targetComponentIds,
                        targetDisplayPaths,
                        traceIds,
                        routeKinds,
                        groupPaths.Sum(path => path.RecentTracedDeliveryCount)
                    );
                })
                .OrderByDescending(lane => lane.DeliveryCount)
                .ThenByDescending(lane => lane.PathCount)
                .ThenBy(lane => lane.Context, StringComparer.Ordinal)
                .ToArray();
        }

        private static FlowGraphTraceMessageLane[] BuildVisibleTraceMessageLanes(
            IEnumerable<FlowGraphTracePath> tracePaths
        )
        {
            return tracePaths
                .GroupBy(path => path.MessageTypeName, StringComparer.Ordinal)
                .Select(group =>
                {
                    FlowGraphTracePath[] groupPaths = group.ToArray();
                    string[] contexts = groupPaths
                        .Select(path => NormalizeTraceContext(path.Context))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(context => context, StringComparer.Ordinal)
                        .ToArray();
                    string[] targetComponentIds = groupPaths
                        .Select(path => path.TargetComponentId)
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(id => id, StringComparer.Ordinal)
                        .ToArray();
                    string[] targetDisplayPaths = CreateTraceTargetDisplayPaths(groupPaths);
                    string[] routeKinds = CreateVisibleRouteKindList(
                        groupPaths.Select(path => path.RegistrationTypeName)
                    );
                    long[] traceIds = groupPaths
                        .SelectMany(path => path.TraceIds)
                        .Where(traceId => traceId > 0)
                        .Distinct()
                        .OrderBy(traceId => traceId)
                        .ToArray();

                    return new FlowGraphTraceMessageLane(
                        group.Key,
                        groupPaths.Length,
                        contexts,
                        targetComponentIds,
                        targetDisplayPaths,
                        traceIds,
                        routeKinds,
                        groupPaths.Sum(path => path.RecentTracedDeliveryCount)
                    );
                })
                .OrderByDescending(lane => lane.DeliveryCount)
                .ThenByDescending(lane => lane.PathCount)
                .ThenBy(lane => lane.MessageTypeName, StringComparer.Ordinal)
                .ToArray();
        }

        private static FlowGraphTraceIdLane[] BuildVisibleTraceIdLanes(
            IEnumerable<FlowGraphTracePath> tracePaths
        )
        {
            FlowGraphTracePath[] visibleTracePaths = tracePaths.ToArray();
            Dictionary<string, int> duplicateTargetPathCounts =
                CreateDuplicateTraceTargetPathCounts(visibleTracePaths);
            List<FlowGraphTraceIdPathMembership> memberships = new();
            foreach (FlowGraphTracePath path in visibleTracePaths)
            {
                foreach (long traceId in path.TraceIds.Where(traceId => traceId > 0))
                {
                    memberships.Add(new FlowGraphTraceIdPathMembership(traceId, path));
                }
            }

            return memberships
                .GroupBy(membership => membership.TraceId)
                .Select(group =>
                {
                    FlowGraphTracePath[] groupPaths = group
                        .Select(membership => membership.Path)
                        .ToArray();
                    string[] messageTypes = groupPaths
                        .Select(path => path.MessageTypeName)
                        .Where(message => !string.IsNullOrWhiteSpace(message))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(message => message, StringComparer.Ordinal)
                        .ToArray();
                    string[] contexts = groupPaths
                        .Select(path => NormalizeTraceContext(path.Context))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(context => context, StringComparer.Ordinal)
                        .ToArray();
                    string[] targetComponentIds = groupPaths
                        .Select(path => path.TargetComponentId)
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(id => id, StringComparer.Ordinal)
                        .ToArray();
                    string[] targetDisplayPaths = CreateTraceTargetDisplayPaths(
                        groupPaths,
                        duplicateTargetPathCounts
                    );
                    string[] routeKinds = CreateVisibleRouteKindList(
                        groupPaths.Select(path => path.RegistrationTypeName)
                    );

                    return new FlowGraphTraceIdLane(
                        group.Key,
                        groupPaths.Length,
                        messageTypes,
                        targetComponentIds,
                        targetDisplayPaths,
                        contexts,
                        routeKinds
                    );
                })
                .OrderByDescending(lane => lane.PathCount)
                .ThenBy(lane => lane.TraceId)
                .ToArray();
        }

        private static FlowGraphTraceRouteKindLane[] BuildVisibleTraceRouteKindLanes(
            IEnumerable<FlowGraphTracePath> tracePaths
        )
        {
            FlowGraphTracePath[] visibleTracePaths = tracePaths.ToArray();
            Dictionary<string, int> duplicateTargetPathCounts =
                CreateDuplicateTraceTargetPathCounts(visibleTracePaths);

            return visibleTracePaths
                .GroupBy(
                    path => NormalizeTraceRouteKind(path.RegistrationTypeName),
                    StringComparer.Ordinal
                )
                .Select(group =>
                {
                    FlowGraphTracePath[] groupPaths = group.ToArray();
                    string[] messageTypes = groupPaths
                        .Select(path => path.MessageTypeName)
                        .Where(message => !string.IsNullOrWhiteSpace(message))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(message => message, StringComparer.Ordinal)
                        .ToArray();
                    string[] contexts = groupPaths
                        .Select(path => NormalizeTraceContext(path.Context))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(context => context, StringComparer.Ordinal)
                        .ToArray();
                    string[] targetComponentIds = groupPaths
                        .Select(path => path.TargetComponentId)
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(id => id, StringComparer.Ordinal)
                        .ToArray();
                    string[] targetDisplayPaths = CreateTraceTargetDisplayPaths(
                        groupPaths,
                        duplicateTargetPathCounts
                    );
                    long[] traceIds = groupPaths
                        .SelectMany(path => path.TraceIds)
                        .Where(traceId => traceId > 0)
                        .Distinct()
                        .OrderBy(traceId => traceId)
                        .ToArray();

                    return new FlowGraphTraceRouteKindLane(
                        group.Key,
                        groupPaths.Length,
                        messageTypes,
                        targetComponentIds,
                        targetDisplayPaths,
                        contexts,
                        traceIds,
                        groupPaths.Sum(path => path.RecentTracedDeliveryCount)
                    );
                })
                .OrderByDescending(lane => lane.DeliveryCount)
                .ThenByDescending(lane => lane.PathCount)
                .ThenBy(lane => lane.RouteKind, StringComparer.Ordinal)
                .ToArray();
        }

        private static string NormalizeTraceRouteKind(string routeKind)
        {
            string taxonomyKind = DxMessagingEditorPalette.NormalizeRouteKind(routeKind);
            if (!string.IsNullOrWhiteSpace(taxonomyKind))
            {
                return taxonomyKind;
            }

            return string.IsNullOrWhiteSpace(routeKind) ? "<unknown route kind>" : routeKind.Trim();
        }

        private static string CreateTraceRouteKindFilterText(string routeKind)
        {
            string taxonomyKind = DxMessagingEditorPalette.NormalizeRouteKind(routeKind);
            if (!string.IsNullOrWhiteSpace(taxonomyKind))
            {
                return taxonomyKind;
            }

            return string.IsNullOrWhiteSpace(routeKind) ? "unknown route kind" : routeKind.Trim();
        }

        private static FlowGraphTraceTargetLane[] BuildVisibleTraceTargetLanes(
            IEnumerable<FlowGraphTracePath> tracePaths
        )
        {
            FlowGraphTracePath[] visibleTracePaths = tracePaths.ToArray();
            Dictionary<string, int> duplicateTargetPathCounts =
                CreateDuplicateTraceTargetPathCounts(visibleTracePaths);

            return visibleTracePaths
                .GroupBy(CreateVisibleTraceTargetLaneKey, StringComparer.Ordinal)
                .Select(group =>
                {
                    FlowGraphTracePath[] groupPaths = group.ToArray();
                    FlowGraphTracePath firstPath = groupPaths
                        .OrderBy(path => path.TargetComponentPath, StringComparer.Ordinal)
                        .ThenBy(path => path.TargetComponentId, StringComparer.Ordinal)
                        .First();
                    string targetDisplayPath = CreateVisibleTraceTargetDisplayPath(
                        firstPath.TargetComponentPath,
                        firstPath.TargetComponentId,
                        duplicateTargetPathCounts
                    );
                    string[] messageTypes = groupPaths
                        .Select(path => path.MessageTypeName)
                        .Where(message => !string.IsNullOrWhiteSpace(message))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(message => message, StringComparer.Ordinal)
                        .ToArray();
                    string[] contexts = groupPaths
                        .Select(path => NormalizeTraceContext(path.Context))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(context => context, StringComparer.Ordinal)
                        .ToArray();
                    string[] routeKinds = CreateVisibleRouteKindList(
                        groupPaths.Select(path => path.RegistrationTypeName)
                    );
                    long[] traceIds = groupPaths
                        .SelectMany(path => path.TraceIds)
                        .Where(traceId => traceId > 0)
                        .Distinct()
                        .OrderBy(traceId => traceId)
                        .ToArray();

                    return new FlowGraphTraceTargetLane(
                        firstPath.TargetComponentId,
                        firstPath.TargetComponentPath,
                        targetDisplayPath,
                        groupPaths.Length,
                        messageTypes,
                        contexts,
                        traceIds,
                        routeKinds,
                        groupPaths.Sum(path => path.RecentTracedDeliveryCount)
                    );
                })
                .OrderByDescending(lane => lane.DeliveryCount)
                .ThenByDescending(lane => lane.PathCount)
                .ThenBy(lane => lane.TargetComponentPath, StringComparer.Ordinal)
                .ThenBy(lane => lane.TargetComponentId, StringComparer.Ordinal)
                .ToArray();
        }

        private static string CreateVisibleTraceTargetLaneKey(FlowGraphTracePath path)
        {
            return string.Join(
                "|",
                path.TargetComponentId ?? string.Empty,
                path.TargetComponentPath ?? string.Empty
            );
        }

        private static string CreateVisibleTraceTargetDisplayPath(
            string targetComponentPath,
            string targetComponentId,
            IReadOnlyDictionary<string, int> duplicateTargetPathCounts
        )
        {
            if (string.IsNullOrWhiteSpace(targetComponentPath))
            {
                return string.IsNullOrWhiteSpace(targetComponentId)
                    ? "<unknown target>"
                    : $"<unknown target> ({targetComponentId})";
            }

            if (
                duplicateTargetPathCounts.TryGetValue(targetComponentPath, out int count)
                && count > 1
                && !string.IsNullOrWhiteSpace(targetComponentId)
            )
            {
                return $"{targetComponentPath} ({targetComponentId})";
            }

            return targetComponentPath;
        }

        private static Dictionary<string, int> CreateDuplicateTraceTargetPathCounts(
            IEnumerable<FlowGraphTracePath> paths
        )
        {
            return paths
                .Where(path => !string.IsNullOrWhiteSpace(path.TargetComponentPath))
                .GroupBy(path => path.TargetComponentPath, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group =>
                        group
                            .Select(CreateVisibleTraceTargetLaneKey)
                            .Distinct(StringComparer.Ordinal)
                            .Count(),
                    StringComparer.Ordinal
                );
        }

        private static string[] CreateTraceTargetDisplayPaths(IEnumerable<FlowGraphTracePath> paths)
        {
            FlowGraphTracePath[] orderedPaths = paths
                .Where(path => !string.IsNullOrWhiteSpace(path.TargetComponentPath))
                .OrderBy(path => path.TargetComponentPath, StringComparer.Ordinal)
                .ThenBy(path => path.TargetComponentId, StringComparer.Ordinal)
                .ToArray();
            Dictionary<string, int> pathCounts = orderedPaths
                .GroupBy(path => path.TargetComponentPath, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group =>
                        group
                            .Select(path => path.TargetComponentId ?? string.Empty)
                            .Distinct(StringComparer.Ordinal)
                            .Count(),
                    StringComparer.Ordinal
                );

            return CreateTraceTargetDisplayPaths(orderedPaths, pathCounts);
        }

        private static string[] CreateTraceTargetDisplayPaths(
            IEnumerable<FlowGraphTracePath> paths,
            IReadOnlyDictionary<string, int> duplicateTargetPathCounts
        )
        {
            return paths
                .Where(path => !string.IsNullOrWhiteSpace(path.TargetComponentPath))
                .OrderBy(path => path.TargetComponentPath, StringComparer.Ordinal)
                .ThenBy(path => path.TargetComponentId, StringComparer.Ordinal)
                .Select(path =>
                    CreateVisibleTraceTargetDisplayPath(
                        path.TargetComponentPath,
                        path.TargetComponentId,
                        duplicateTargetPathCounts
                    )
                )
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        private static string CreateRouteMapSummaryText(FlowGraphVisibleSnapshot visibleSnapshot)
        {
            int routeCount = visibleSnapshot.Edges.Count;
            int messageCount = CountDistinct(
                visibleSnapshot.Edges.Select(edge => edge.MessageTypeName)
            );
            int listenerCount = CountDistinct(
                visibleSnapshot.Edges.Select(edge => edge.TargetComponentId)
            );
            int totalVisibleCalls = SumVisibleCalls(visibleSnapshot);
            int noCallRouteCount = visibleSnapshot.Edges.Count(edge => edge.CallCount <= 0);
            int tracedDeliveries = visibleSnapshot.Edges.Sum(edge =>
                edge.RecentTracedDeliveryCount
            );
            return $"{FormatCount(routeCount, "visible route")} | {FormatCount(messageCount, "message")} | {FormatCount(listenerCount, "listener")} | Calls: {totalVisibleCalls} | {CreateRouteKindMixSummary(visibleSnapshot)} | {CreateHottestRouteSummary(visibleSnapshot, totalVisibleCalls)} | {CreateWidestMessageSummary(visibleSnapshot)} | {CreateMostRoutedTargetSummary(visibleSnapshot)} | {CreateInactiveRoutedTargetsSummary(visibleSnapshot)} | No-call routes: {noCallRouteCount} | {CreateRecentTracedRoutesSummary(visibleSnapshot)} | {CreateBusiestTracedRouteSummary(visibleSnapshot.Edges)} | {CreateBusiestTracedMessageSummary(visibleSnapshot.Edges)} | {CreateBusiestTracedTargetSummary(visibleSnapshot.Edges)} | Recent traced: {tracedDeliveries} | Trace ids: {CountDistinctTraceIds(visibleSnapshot.TracePaths)} | {CreateWidestTraceSummary(visibleSnapshot.TracePaths)} | {CreateTraceContextVolumeSummary(visibleSnapshot.TracePaths)} | {CreateBusiestTraceContextShareSummary(visibleSnapshot.TracePaths)} | {CreateBusiestTraceMessageSummary(visibleSnapshot.TracePaths)} | {CreateBusiestTraceTargetSummary(visibleSnapshot.TracePaths)} | {CreateBusiestTracePathSummary(visibleSnapshot.TracePaths)} | {CreateBusiestTracePathShareSummary(visibleSnapshot.TracePaths)}";
        }

        private static string CreateRouteKindMixSummary(FlowGraphVisibleSnapshot visibleSnapshot)
        {
            string[] routeKindCounts = visibleSnapshot
                .Edges.GroupBy(
                    edge => CreateVisibleRouteKindLabel(edge.RegistrationTypeName),
                    StringComparer.Ordinal
                )
                .Where(group => !string.IsNullOrWhiteSpace(group.Key))
                .Select(group => new RouteKindSummary(group.Key, group.Count()))
                .OrderByDescending(summary => summary.RouteCount)
                .ThenBy(summary => summary.RegistrationTypeName, StringComparer.Ordinal)
                .Select(summary => $"{summary.RegistrationTypeName} {summary.RouteCount}")
                .ToArray();
            if (routeKindCounts.Length == 0)
            {
                return "Route kinds: none";
            }

            return $"Route kinds: {string.Join(", ", routeKindCounts)}";
        }

        private static string CreateHottestRouteSummary(
            FlowGraphVisibleSnapshot visibleSnapshot,
            int totalVisibleCalls
        )
        {
            if (totalVisibleCalls <= 0)
            {
                return "Hottest route: none";
            }

            FlowGraphEdge hottestEdge = visibleSnapshot
                .Edges.OrderByDescending(edge => edge.CallCount)
                .ThenBy(edge => edge.MessageTypeName, StringComparer.Ordinal)
                .ThenBy(edge => edge.TargetComponentPath, StringComparer.Ordinal)
                .ThenBy(edge => edge.RegistrationTypeName, StringComparer.Ordinal)
                .FirstOrDefault();
            if (string.IsNullOrEmpty(hottestEdge.MessageTypeName) || hottestEdge.CallCount <= 0)
            {
                return "Hottest route: none";
            }

            return $"Hottest route: {hottestEdge.MessageTypeName} -> {hottestEdge.TargetComponentPath} ({hottestEdge.RegistrationTypeName}) | Share: {CreateCallShareText(hottestEdge.CallCount, totalVisibleCalls)}";
        }

        private static string CreateWidestMessageSummary(FlowGraphVisibleSnapshot visibleSnapshot)
        {
            MessageFanOutSummary widestMessage = visibleSnapshot
                .Edges.GroupBy(edge => edge.MessageTypeName, StringComparer.Ordinal)
                .Select(group => new MessageFanOutSummary(
                    group.Key,
                    CountDistinct(group.Select(edge => edge.TargetComponentId)),
                    group.Sum(edge => edge.CallCount)
                ))
                .OrderByDescending(summary => summary.TargetComponentCount)
                .ThenByDescending(summary => summary.CallCount)
                .ThenBy(summary => summary.MessageTypeName, StringComparer.Ordinal)
                .FirstOrDefault();
            if (string.IsNullOrEmpty(widestMessage.MessageTypeName))
            {
                return "Widest message: none";
            }

            return $"Widest message: {widestMessage.MessageTypeName} ({FormatCount(widestMessage.TargetComponentCount, "target component")}, {FormatCount(widestMessage.CallCount, "call")})";
        }

        private static string CreateMostRoutedTargetSummary(
            FlowGraphVisibleSnapshot visibleSnapshot
        )
        {
            TargetFanInSummary mostRoutedTarget = visibleSnapshot
                .Edges.GroupBy(edge => edge.TargetComponentId, StringComparer.Ordinal)
                .Select(group =>
                {
                    FlowGraphEdge firstEdge = group
                        .OrderBy(edge => edge.TargetComponentPath, StringComparer.Ordinal)
                        .First();
                    return new TargetFanInSummary(
                        firstEdge.TargetComponentId,
                        firstEdge.TargetComponentPath,
                        group.Count(),
                        group.Sum(edge => edge.CallCount)
                    );
                })
                .OrderByDescending(summary => summary.RouteCount)
                .ThenByDescending(summary => summary.CallCount)
                .ThenBy(summary => summary.TargetComponentPath, StringComparer.Ordinal)
                .ThenBy(summary => summary.TargetComponentId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (mostRoutedTarget.RouteCount <= 0)
            {
                return "Most-routed target: none";
            }

            return $"Most-routed target: {mostRoutedTarget.TargetComponentPath} ({FormatCount(mostRoutedTarget.RouteCount, "route")}, {FormatCount(mostRoutedTarget.CallCount, "call")})";
        }

        private static string CreateInactiveRoutedTargetsSummary(
            FlowGraphVisibleSnapshot visibleSnapshot
        )
        {
            HashSet<string> routedTargetIds = new(StringComparer.Ordinal);
            foreach (FlowGraphEdge edge in visibleSnapshot.Edges)
            {
                if (!string.IsNullOrWhiteSpace(edge.TargetComponentId))
                {
                    routedTargetIds.Add(edge.TargetComponentId);
                }
            }

            if (routedTargetIds.Count == 0)
            {
                return "Inactive routed targets: none";
            }

            Dictionary<string, FlowGraphComponentNode> componentsById = new(StringComparer.Ordinal);
            foreach (FlowGraphComponentNode component in visibleSnapshot.ComponentNodes)
            {
                if (
                    !string.IsNullOrWhiteSpace(component.Id)
                    && !componentsById.ContainsKey(component.Id)
                )
                {
                    componentsById.Add(component.Id, component);
                }
            }

            int inactiveRoutedTargetCount = 0;
            foreach (string routedTargetId in routedTargetIds)
            {
                if (
                    componentsById.TryGetValue(routedTargetId, out FlowGraphComponentNode component)
                    && !component.ActiveInHierarchy
                )
                {
                    inactiveRoutedTargetCount++;
                }
            }

            return $"Inactive routed targets: {inactiveRoutedTargetCount}/{routedTargetIds.Count}";
        }

        private static string CreateRecentTracedRoutesSummary(
            FlowGraphVisibleSnapshot visibleSnapshot
        )
        {
            int routeCount = visibleSnapshot.Edges.Count;
            if (routeCount == 0)
            {
                return "Recent traced routes: none";
            }

            int tracedRouteCount = visibleSnapshot.Edges.Count(edge =>
                edge.RecentTracedDeliveryCount > 0
            );
            return $"Recent traced routes: {tracedRouteCount}/{routeCount}";
        }

        private static string CreateBusiestTracedRouteSummary(IEnumerable<FlowGraphEdge> edges)
        {
            FlowGraphEdge[] visibleEdges = edges.ToArray();
            int totalTracedDeliveries = visibleEdges.Sum(edge => edge.RecentTracedDeliveryCount);
            if (totalTracedDeliveries <= 0)
            {
                return "Busiest traced route: none";
            }

            FlowGraphEdge busiestEdge = visibleEdges
                .OrderByDescending(edge => edge.RecentTracedDeliveryCount)
                .ThenBy(edge => edge.MessageTypeName, StringComparer.Ordinal)
                .ThenBy(edge => edge.TargetComponentPath, StringComparer.Ordinal)
                .ThenBy(edge => edge.RegistrationTypeName, StringComparer.Ordinal)
                .FirstOrDefault();
            if (
                string.IsNullOrEmpty(busiestEdge.MessageTypeName)
                || busiestEdge.RecentTracedDeliveryCount <= 0
            )
            {
                return "Busiest traced route: none";
            }

            return $"Busiest traced route: {busiestEdge.MessageTypeName} -> {busiestEdge.TargetComponentPath} ({busiestEdge.RegistrationTypeName}) | Share: {CreateCallShareText(busiestEdge.RecentTracedDeliveryCount, totalTracedDeliveries)}";
        }

        private static string CreateBusiestTracedMessageSummary(IEnumerable<FlowGraphEdge> edges)
        {
            FlowGraphEdge[] visibleEdges = edges.ToArray();
            int totalTracedDeliveries = visibleEdges.Sum(edge => edge.RecentTracedDeliveryCount);
            if (totalTracedDeliveries <= 0)
            {
                return "Busiest traced message: none";
            }

            MessageTraceDeliverySummary busiestMessage = visibleEdges
                .GroupBy(edge => edge.MessageTypeName, StringComparer.Ordinal)
                .Select(group => new MessageTraceDeliverySummary(
                    group.Key,
                    group.Sum(edge => edge.RecentTracedDeliveryCount)
                ))
                .OrderByDescending(summary => summary.DeliveryCount)
                .ThenBy(summary => summary.MessageTypeName, StringComparer.Ordinal)
                .FirstOrDefault();
            if (
                string.IsNullOrEmpty(busiestMessage.MessageTypeName)
                || busiestMessage.DeliveryCount <= 0
            )
            {
                return "Busiest traced message: none";
            }

            return $"Busiest traced message: {busiestMessage.MessageTypeName} | Share: {CreateCallShareText(busiestMessage.DeliveryCount, totalTracedDeliveries)}";
        }

        private static string CreateBusiestTracedTargetSummary(IEnumerable<FlowGraphEdge> edges)
        {
            FlowGraphEdge[] visibleEdges = edges.ToArray();
            int totalTracedDeliveries = visibleEdges.Sum(edge => edge.RecentTracedDeliveryCount);
            if (totalTracedDeliveries <= 0)
            {
                return "Busiest traced target: none";
            }

            TraceTargetDeliverySummary busiestTarget = visibleEdges
                .GroupBy(edge => edge.TargetComponentId, StringComparer.Ordinal)
                .Select(group =>
                {
                    FlowGraphEdge firstEdge = group
                        .OrderBy(edge => edge.TargetComponentPath, StringComparer.Ordinal)
                        .ThenBy(edge => edge.TargetComponentId, StringComparer.Ordinal)
                        .First();
                    return new TraceTargetDeliverySummary(
                        firstEdge.TargetComponentId,
                        firstEdge.TargetComponentPath,
                        group.Sum(edge => edge.RecentTracedDeliveryCount)
                    );
                })
                .OrderByDescending(summary => summary.DeliveryCount)
                .ThenBy(summary => summary.TargetComponentPath, StringComparer.Ordinal)
                .ThenBy(summary => summary.TargetComponentId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (busiestTarget.DeliveryCount <= 0)
            {
                return "Busiest traced target: none";
            }

            return $"Busiest traced target: {busiestTarget.TargetComponentPath} | Share: {CreateCallShareText(busiestTarget.DeliveryCount, totalTracedDeliveries)}";
        }

        private static string CreateRouteHealthSummary(IEnumerable<FlowGraphEdge> edges)
        {
            FlowGraphEdge[] visibleEdges = edges.ToArray();
            if (visibleEdges.Length == 0)
            {
                return "Recent traced routes: none | No-call routes: 0";
            }

            int tracedRouteCount = visibleEdges.Count(edge => edge.RecentTracedDeliveryCount > 0);
            int noCallRouteCount = visibleEdges.Count(edge => edge.CallCount <= 0);
            return $"Recent traced routes: {tracedRouteCount}/{visibleEdges.Length} | No-call routes: {noCallRouteCount}";
        }

        private static string CreateBusiestTracePathSummary(
            IEnumerable<FlowGraphTracePath> tracePaths
        )
        {
            FlowGraphTracePath busiestPath = tracePaths
                .OrderByDescending(path => path.RecentTracedDeliveryCount)
                .ThenBy(path => path.MessageTypeName, StringComparer.Ordinal)
                .ThenBy(path => path.TargetComponentPath, StringComparer.Ordinal)
                .ThenBy(path => path.RegistrationTypeName, StringComparer.Ordinal)
                .ThenBy(path => NormalizeTraceContext(path.Context), StringComparer.Ordinal)
                .FirstOrDefault();
            if (busiestPath.RecentTracedDeliveryCount <= 0)
            {
                return "Busiest path: none";
            }

            string context = NormalizeTraceContext(busiestPath.Context);
            string deliveryText =
                busiestPath.RecentTracedDeliveryCount == 1 ? "delivery" : "deliveries";
            return $"Busiest path: {busiestPath.MessageTypeName} -> {busiestPath.TargetComponentPath} ({busiestPath.RegistrationTypeName}, {context}, {busiestPath.RecentTracedDeliveryCount} {deliveryText})";
        }

        private static string CreateBusiestTracePathShareSummary(
            IEnumerable<FlowGraphTracePath> tracePaths
        )
        {
            FlowGraphTracePath[] visibleTracePaths = tracePaths.ToArray();
            int totalDeliveries = visibleTracePaths.Sum(path => path.RecentTracedDeliveryCount);
            int busiestDeliveries = visibleTracePaths
                .Select(path => path.RecentTracedDeliveryCount)
                .DefaultIfEmpty()
                .Max();
            if (totalDeliveries <= 0 || busiestDeliveries <= 0)
            {
                return "Busiest path share: none";
            }

            return $"Busiest path share: {CreateCallShareText(busiestDeliveries, totalDeliveries)}";
        }

        private static string CreateBusiestTraceMessageSummary(
            IEnumerable<FlowGraphTracePath> tracePaths
        )
        {
            FlowGraphTracePath[] visibleTracePaths = tracePaths.ToArray();
            int totalDeliveries = visibleTracePaths.Sum(path => path.RecentTracedDeliveryCount);
            MessageTraceDeliverySummary busiestMessage = visibleTracePaths
                .GroupBy(path => path.MessageTypeName, StringComparer.Ordinal)
                .Select(group => new MessageTraceDeliverySummary(
                    group.Key,
                    group.Sum(path => path.RecentTracedDeliveryCount)
                ))
                .OrderByDescending(summary => summary.DeliveryCount)
                .ThenBy(summary => summary.MessageTypeName, StringComparer.Ordinal)
                .FirstOrDefault();
            if (
                string.IsNullOrEmpty(busiestMessage.MessageTypeName)
                || busiestMessage.DeliveryCount <= 0
            )
            {
                return "Busiest trace message: none";
            }

            string deliveryText = busiestMessage.DeliveryCount == 1 ? "delivery" : "deliveries";
            return $"Busiest trace message: {busiestMessage.MessageTypeName} ({busiestMessage.DeliveryCount} {deliveryText}) | Share: {CreateCallShareText(busiestMessage.DeliveryCount, totalDeliveries)}";
        }

        private static string CreateBusiestTraceTargetSummary(
            IEnumerable<FlowGraphTracePath> tracePaths
        )
        {
            FlowGraphTracePath[] visibleTracePaths = tracePaths.ToArray();
            int totalDeliveries = visibleTracePaths.Sum(path => path.RecentTracedDeliveryCount);
            TraceTargetDeliverySummary busiestTarget = visibleTracePaths
                .GroupBy(path => path.TargetComponentId, StringComparer.Ordinal)
                .Select(group =>
                {
                    FlowGraphTracePath firstPath = group
                        .OrderBy(path => path.TargetComponentPath, StringComparer.Ordinal)
                        .ThenBy(path => path.TargetComponentId, StringComparer.Ordinal)
                        .First();
                    return new TraceTargetDeliverySummary(
                        firstPath.TargetComponentId,
                        firstPath.TargetComponentPath,
                        group.Sum(path => path.RecentTracedDeliveryCount)
                    );
                })
                .OrderByDescending(summary => summary.DeliveryCount)
                .ThenBy(summary => summary.TargetComponentPath, StringComparer.Ordinal)
                .ThenBy(summary => summary.TargetComponentId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (busiestTarget.DeliveryCount <= 0)
            {
                return "Busiest target: none";
            }

            string deliveryText = busiestTarget.DeliveryCount == 1 ? "delivery" : "deliveries";
            return $"Busiest target: {busiestTarget.TargetComponentPath} ({busiestTarget.DeliveryCount} {deliveryText}) | Share: {CreateCallShareText(busiestTarget.DeliveryCount, totalDeliveries)}";
        }

        private static string CreateWidestTraceSummary(IEnumerable<FlowGraphTracePath> tracePaths)
        {
            Dictionary<long, int> pathCountsByTraceId = new();
            foreach (FlowGraphTracePath path in tracePaths)
            {
                foreach (long traceId in path.TraceIds)
                {
                    if (traceId > 0)
                    {
                        pathCountsByTraceId[traceId] =
                            pathCountsByTraceId.GetValueOrDefault(traceId) + 1;
                    }
                }
            }

            TraceIdPathSummary widestTrace = pathCountsByTraceId
                .Select(pair => new TraceIdPathSummary(pair.Key, pair.Value))
                .OrderByDescending(summary => summary.PathCount)
                .ThenBy(summary => summary.TraceId)
                .FirstOrDefault();
            if (widestTrace.PathCount <= 0)
            {
                return "Widest trace: none";
            }

            return $"Widest trace: {widestTrace.TraceId} ({FormatCount(widestTrace.PathCount, "path")})";
        }

        private static VisualElement CreateRouteMapRow(
            FlowGraphEdge edge,
            string callShareText,
            bool selected,
            Action<string> onSelectionChanged
        )
        {
            VisualElement row = new();
            row.AddToClassList(RouteMapRouteClassName);
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.borderLeftWidth = 3;
            row.style.borderLeftColor = DxMessagingEditorPalette.RouteKindColor(
                edge.RegistrationTypeName
            );
            row.style.borderTopWidth = 1;
            row.style.borderTopColor = DxMessagingEditorPalette.BorderSoft;
            row.style.marginTop = 6;
            row.style.paddingTop = 7;
            row.style.paddingRight = 8;
            row.style.paddingBottom = 7;
            row.style.paddingLeft = 10;
            ApplySelection(row, selected);

            if (onSelectionChanged != null)
            {
                string selectionKey = CreateEdgeSelectionKey(edge);
                row.RegisterCallback<ClickEvent>(_ => onSelectionChanged.Invoke(selectionKey));
            }

            Label message = new(edge.MessageTypeName) { name = RouteMapMessageLabelName };
            message.style.flexBasis = 0;
            message.style.flexGrow = 2;
            message.style.unityFontStyleAndWeight = FontStyle.Bold;
            message.style.whiteSpace = WhiteSpace.Normal;
            row.Add(message);

            Label summary = new(
                $"{edge.RegistrationTypeName} | Registrations: {edge.RegistrationCount} | Calls: {edge.CallCount} | Recent traced: {edge.RecentTracedDeliveryCount} | Share: {callShareText}"
            )
            {
                name = RouteMapSummaryLabelName,
            };
            summary.style.flexBasis = 0;
            summary.style.flexGrow = 2;
            summary.style.marginLeft = 8;
            summary.style.whiteSpace = WhiteSpace.Normal;
            row.Add(summary);

            Label target = new(edge.TargetComponentPath) { name = RouteMapTargetLabelName };
            target.style.flexBasis = 0;
            target.style.flexGrow = 2;
            target.style.marginLeft = 8;
            target.style.whiteSpace = WhiteSpace.Normal;
            row.Add(target);

            return row;
        }

        internal static string CreateComponentSelectionKey(FlowGraphComponentNode component)
        {
            return "component|" + component.Id;
        }

        internal static string CreateMessageSelectionKey(FlowGraphMessageNode message)
        {
            return "message|" + message.MessageTypeName;
        }

        internal static string CreateEdgeSelectionKey(FlowGraphEdge edge)
        {
            return CreateEdgeSelectionKey(
                edge.MessageTypeName,
                edge.TargetComponentId,
                edge.RegistrationTypeName
            );
        }

        private static string CreateEdgeSelectionKey(
            string messageTypeName,
            string targetComponentId,
            string registrationTypeName
        )
        {
            return string.Join(
                "|",
                "edge",
                messageTypeName ?? string.Empty,
                targetComponentId ?? string.Empty,
                registrationTypeName ?? string.Empty
            );
        }

        private static VisualElement CreateComponentNodeRow(
            FlowGraphComponentNode component,
            bool selected,
            Action<string> onSelectionChanged
        )
        {
            VisualElement row = CreateNodeRow(
                ComponentNodeClassName,
                DxMessagingEditorPalette.Amber
            );
            ApplySelection(row, selected);
            if (onSelectionChanged != null)
            {
                string selectionKey = CreateComponentSelectionKey(component);
                row.RegisterCallback<ClickEvent>(_ => onSelectionChanged.Invoke(selectionKey));
            }
            string activeText = component.ActiveInHierarchy ? "active" : "inactive";
            row.Add(
                new Label(
                    $"{component.HierarchyPath} ({component.ComponentTypeName}, {activeText})"
                )
                {
                    name = NodeNameLabelName,
                }
            );
            row.Add(
                new Label(
                    $"Listeners: {component.ListenerCount} | Registrations: {component.RegistrationCount} | Calls: {component.CallCount} | Local messages: {component.LocalMessageCount}"
                )
                {
                    name = NodeSummaryLabelName,
                }
            );
            return row;
        }

        private static VisualElement CreateMessageNodeRow(
            FlowGraphMessageNode message,
            bool selected,
            Action<string> onSelectionChanged
        )
        {
            VisualElement row = CreateNodeRow(
                MessageNodeClassName,
                DxMessagingEditorPalette.AmberSoft
            );
            ApplySelection(row, selected);
            if (onSelectionChanged != null)
            {
                string selectionKey = CreateMessageSelectionKey(message);
                row.RegisterCallback<ClickEvent>(_ => onSelectionChanged.Invoke(selectionKey));
            }
            row.Add(new Label(message.MessageTypeName) { name = NodeNameLabelName });
            row.Add(
                new Label(
                    $"Registrations: {message.RegistrationCount} | Calls: {message.CallCount} | Recent: {message.RecentGlobalEmissionCount} global / {message.RecentLocalMessageCount} listener | Traced deliveries: {message.RecentTracedDeliveryCount}"
                )
                {
                    name = NodeSummaryLabelName,
                }
            );
            return row;
        }

        private static VisualElement CreateEdgeRow(
            FlowGraphEdge edge,
            bool selected,
            Action<string> onSelectionChanged
        )
        {
            VisualElement row = new();
            row.AddToClassList(EdgeRowClassName);
            ApplySelection(row, selected);
            if (onSelectionChanged != null)
            {
                string selectionKey = CreateEdgeSelectionKey(edge);
                row.RegisterCallback<ClickEvent>(_ => onSelectionChanged.Invoke(selectionKey));
            }
            row.style.borderLeftWidth = 3;
            row.style.borderLeftColor = DxMessagingEditorPalette.RouteKindColor(
                edge.RegistrationTypeName
            );
            row.style.borderTopWidth = 1;
            row.style.borderTopColor = DxMessagingEditorPalette.BorderSoft;
            row.style.marginTop = 6;
            row.style.paddingTop = 7;
            row.style.paddingRight = 8;
            row.style.paddingBottom = 7;
            row.style.paddingLeft = 10;

            Label label = new(
                $"{edge.MessageTypeName} -> {edge.TargetComponentPath} ({edge.RegistrationTypeName})"
            )
            {
                name = EdgeLabelName,
            };
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            row.Add(label);
            Label summary = new(
                $"Registrations: {edge.RegistrationCount} | Calls: {edge.CallCount} | Recent traced deliveries: {edge.RecentTracedDeliveryCount}"
            )
            {
                name = NodeSummaryLabelName,
            };
            summary.style.marginTop = 2;
            row.Add(summary);
            return row;
        }

        private static void ApplySelection(VisualElement row, bool selected)
        {
            if (!selected)
            {
                return;
            }

            row.AddToClassList(SelectedRowClassName);
            row.style.backgroundColor = DxMessagingEditorPalette.SelectedWash;
        }

        private static VisualElement CreateNodeRow(string className, Color borderColor)
        {
            VisualElement row = new();
            row.AddToClassList(className);
            row.style.borderLeftWidth = 3;
            row.style.borderLeftColor = borderColor;
            row.style.borderTopWidth = 1;
            row.style.borderTopColor = DxMessagingEditorPalette.BorderSoft;
            row.style.marginTop = 6;
            row.style.paddingTop = 7;
            row.style.paddingRight = 8;
            row.style.paddingBottom = 7;
            row.style.paddingLeft = 10;
            return row;
        }

        private static VisualElement CreateDetailsPane(
            FlowGraphSelectedItem selectedItem,
            FlowGraphVisibleSnapshot visibleSnapshot
        )
        {
            VisualElement details = new() { name = DetailsPaneName };
            details.style.borderTopWidth = 1;
            details.style.borderTopColor = DxMessagingEditorPalette.BorderPanel;
            details.style.marginTop = 10;
            details.style.paddingTop = 8;

            Label title = new(CreateDetailsTitle(selectedItem)) { name = DetailsTitleLabelName };
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.whiteSpace = WhiteSpace.Normal;
            details.Add(title);

            Label body = new(CreateDetailsBody(selectedItem, visibleSnapshot))
            {
                name = DetailsBodyLabelName,
            };
            body.style.marginTop = 4;
            body.style.whiteSpace = WhiteSpace.Normal;
            details.Add(body);

            return details;
        }

        private static string CreateDetailsTitle(FlowGraphSelectedItem selectedItem)
        {
            switch (selectedItem.Kind)
            {
                case FlowGraphSelectionKind.Component:
                    return "Component: " + selectedItem.Component.HierarchyPath;
                case FlowGraphSelectionKind.Message:
                    return "Message: " + selectedItem.Message.MessageTypeName;
                case FlowGraphSelectionKind.Edge:
                    return "Route: "
                        + selectedItem.Edge.MessageTypeName
                        + " -> "
                        + selectedItem.Edge.TargetComponentPath;
                default:
                    return "Selection Details";
            }
        }

        private static string CreateDetailsBody(
            FlowGraphSelectedItem selectedItem,
            FlowGraphVisibleSnapshot visibleSnapshot
        )
        {
            switch (selectedItem.Kind)
            {
                case FlowGraphSelectionKind.Component:
                    return CreateComponentDetailsBody(selectedItem.Component, visibleSnapshot);
                case FlowGraphSelectionKind.Message:
                    return CreateMessageDetailsBody(selectedItem.Message, visibleSnapshot);
                case FlowGraphSelectionKind.Edge:
                    return CreateEdgeDetailsBody(selectedItem.Edge, visibleSnapshot);
                default:
                    return string.Empty;
            }
        }

        private static string CreateComponentDetailsBody(
            FlowGraphComponentNode component,
            FlowGraphVisibleSnapshot visibleSnapshot
        )
        {
            FlowGraphEdge[] inboundEdges = visibleSnapshot
                .Edges.Where(edge =>
                    string.Equals(edge.TargetComponentId, component.Id, StringComparison.Ordinal)
                )
                .ToArray();
            FlowGraphTracePath[] tracePaths = visibleSnapshot
                .TracePaths.Where(path =>
                    string.Equals(path.TargetComponentId, component.Id, StringComparison.Ordinal)
                )
                .ToArray();
            int selectedCalls = inboundEdges.Sum(edge => edge.CallCount);
            int totalCalls = SumVisibleCalls(visibleSnapshot);
            int selectedTracedDeliveries = inboundEdges.Sum(edge => edge.RecentTracedDeliveryCount);
            int totalTracedDeliveries = SumVisibleTracedDeliveries(visibleSnapshot);
            string activeText = component.ActiveInHierarchy ? "active" : "inactive";
            StringBuilder builder = new();
            builder
                .Append("Type: ")
                .Append(component.ComponentTypeName)
                .Append(" | ")
                .Append(activeText)
                .Append(" | Listeners: ")
                .Append(component.ListenerCount)
                .Append(" | Registrations: ")
                .Append(component.RegistrationCount)
                .Append(" | Calls: ")
                .Append(component.CallCount)
                .Append(" | Local messages: ")
                .Append(component.LocalMessageCount)
                .AppendLine();
            builder
                .Append("Inbound visible routes: ")
                .Append(inboundEdges.Length)
                .Append(" from ")
                .Append(CountDistinct(inboundEdges.Select(edge => edge.MessageTypeName)))
                .Append(" message types | Visible call share: ")
                .Append(CreateCallShareText(selectedCalls, totalCalls))
                .Append(" | Visible traced share: ")
                .Append(CreateCallShareText(selectedTracedDeliveries, totalTracedDeliveries))
                .AppendLine();
            builder
                .Append("Message types: ")
                .Append(JoinDistinctOrNone(inboundEdges.Select(edge => edge.MessageTypeName)))
                .AppendLine();
            builder.Append(CreateRouteHealthSummary(inboundEdges)).AppendLine();
            builder.Append(CreateBusiestTracedRouteSummary(inboundEdges)).AppendLine();
            builder.Append(CreateBusiestTracedMessageSummary(inboundEdges)).AppendLine();
            builder
                .Append("Recent trace paths: ")
                .Append(tracePaths.Length)
                .Append(" | Traced deliveries: ")
                .Append(tracePaths.Sum(path => path.RecentTracedDeliveryCount))
                .AppendLine();
            builder.Append(CreateBusiestTraceMessageSummary(tracePaths)).AppendLine();
            builder.Append(CreateTraceIdBreadthSummary(tracePaths)).AppendLine();
            builder
                .Append("Recent trace contexts: ")
                .Append(JoinTraceContextsOrNone(tracePaths))
                .AppendLine();
            builder.Append(CreateTraceContextVolumeSummary(tracePaths)).AppendLine();
            builder
                .Append("Trace context deliveries: ")
                .Append(CreateTraceContextDeliveryBreakdown(tracePaths))
                .AppendLine();
            builder.Append(CreateBusiestTraceContextShareSummary(tracePaths)).AppendLine();
            builder.Append(CreateBusiestTracePathSummary(tracePaths)).AppendLine();
            builder.Append(CreateBusiestTracePathShareSummary(tracePaths)).AppendLine();
            builder
                .Append("Registration kinds: ")
                .Append(JoinDistinctOrNone(inboundEdges.Select(edge => edge.RegistrationTypeName)));
            return builder.ToString();
        }

        private static string CreateMessageDetailsBody(
            FlowGraphMessageNode message,
            FlowGraphVisibleSnapshot visibleSnapshot
        )
        {
            FlowGraphEdge[] messageEdges = visibleSnapshot
                .Edges.Where(edge =>
                    string.Equals(
                        edge.MessageTypeName,
                        message.MessageTypeName,
                        StringComparison.Ordinal
                    )
                )
                .ToArray();
            FlowGraphTracePath[] tracePaths = visibleSnapshot
                .TracePaths.Where(path =>
                    string.Equals(
                        path.MessageTypeName,
                        message.MessageTypeName,
                        StringComparison.Ordinal
                    )
                )
                .ToArray();
            int visibleRegistrationCount = messageEdges.Sum(edge => edge.RegistrationCount);
            int selectedCalls = messageEdges.Sum(edge => edge.CallCount);
            int totalCalls = SumVisibleCalls(visibleSnapshot);
            int selectedTracedDeliveries = messageEdges.Sum(edge => edge.RecentTracedDeliveryCount);
            int totalTracedDeliveries = SumVisibleTracedDeliveries(visibleSnapshot);
            FlowGraphEdge busiestEdge = messageEdges
                .OrderByDescending(edge => edge.CallCount)
                .ThenBy(edge => edge.TargetComponentPath, StringComparer.Ordinal)
                .FirstOrDefault();
            string busiestText =
                messageEdges.Length == 0
                    ? "none"
                    : $"{busiestEdge.TargetComponentPath} ({busiestEdge.CallCount} calls)";
            StringBuilder builder = new();
            builder
                .Append("Visible registrations: ")
                .Append(visibleRegistrationCount)
                .Append(" | Calls: ")
                .Append(selectedCalls)
                .Append(" | Listener components: ")
                .Append(CountDistinct(messageEdges.Select(edge => edge.TargetComponentId)))
                .AppendLine();
            builder
                .Append("Recent diagnostics: ")
                .Append(message.RecentGlobalEmissionCount)
                .Append(" global emissions | ")
                .Append(message.RecentLocalMessageCount)
                .Append(" listener messages | Traced deliveries: ")
                .Append(message.RecentTracedDeliveryCount)
                .AppendLine();
            builder
                .Append("Registration kinds: ")
                .Append(JoinDistinctOrNone(messageEdges.Select(edge => edge.RegistrationTypeName)))
                .Append(" | Visible call share: ")
                .Append(CreateCallShareText(selectedCalls, totalCalls))
                .Append(" | Visible traced share: ")
                .Append(CreateCallShareText(selectedTracedDeliveries, totalTracedDeliveries))
                .AppendLine();
            builder.Append(CreateRouteHealthSummary(messageEdges)).AppendLine();
            builder.Append(CreateBusiestTracedRouteSummary(messageEdges)).AppendLine();
            builder.Append(CreateBusiestTracedTargetSummary(messageEdges)).AppendLine();
            builder
                .Append("Recent trace contexts: ")
                .Append(JoinTraceContextsOrNone(tracePaths))
                .Append(" | Trace-path deliveries: ")
                .Append(tracePaths.Sum(path => path.RecentTracedDeliveryCount))
                .AppendLine();
            builder.Append(CreateTraceContextVolumeSummary(tracePaths)).AppendLine();
            builder.Append(CreateBusiestTraceTargetSummary(tracePaths)).AppendLine();
            builder.Append(CreateTraceIdBreadthSummary(tracePaths)).AppendLine();
            builder
                .Append("Trace context deliveries: ")
                .Append(CreateTraceContextDeliveryBreakdown(tracePaths))
                .AppendLine();
            builder.Append(CreateBusiestTraceContextShareSummary(tracePaths)).AppendLine();
            builder.Append(CreateBusiestTracePathSummary(tracePaths)).AppendLine();
            builder.Append(CreateBusiestTracePathShareSummary(tracePaths)).AppendLine();
            builder.Append("Busiest listener: ").Append(busiestText);
            return builder.ToString();
        }

        private static string CreateEdgeDetailsBody(
            FlowGraphEdge edge,
            FlowGraphVisibleSnapshot visibleSnapshot
        )
        {
            int totalCalls = SumVisibleCalls(visibleSnapshot);
            int totalTracedDeliveries = SumVisibleTracedDeliveries(visibleSnapshot);
            FlowGraphTracePath[] tracePaths = visibleSnapshot
                .TracePaths.Where(path =>
                    string.Equals(
                        path.MessageTypeName,
                        edge.MessageTypeName,
                        StringComparison.Ordinal
                    )
                    && string.Equals(
                        path.TargetComponentId,
                        edge.TargetComponentId,
                        StringComparison.Ordinal
                    )
                    && string.Equals(
                        path.RegistrationTypeName,
                        edge.RegistrationTypeName,
                        StringComparison.Ordinal
                    )
                )
                .ToArray();
            StringBuilder builder = new();
            builder
                .Append("Target component: ")
                .Append(edge.TargetComponentPath)
                .Append(" | Target id: ")
                .Append(edge.TargetComponentId)
                .AppendLine();
            builder
                .Append("Registration type: ")
                .Append(edge.RegistrationTypeName)
                .Append(" | Registrations: ")
                .Append(edge.RegistrationCount)
                .Append(" | Calls: ")
                .Append(edge.CallCount)
                .Append(" | Recent traced deliveries: ")
                .Append(edge.RecentTracedDeliveryCount)
                .AppendLine();
            builder
                .Append("Visible call share: ")
                .Append(CreateCallShareText(edge.CallCount, totalCalls))
                .AppendLine();
            builder
                .Append("Visible traced share: ")
                .Append(CreateCallShareText(edge.RecentTracedDeliveryCount, totalTracedDeliveries))
                .AppendLine();
            builder
                .Append("Recent trace paths: ")
                .Append(tracePaths.Length)
                .Append(" | Trace-path deliveries: ")
                .Append(tracePaths.Sum(path => path.RecentTracedDeliveryCount))
                .Append(" | Contexts: ")
                .Append(JoinTraceContextsOrNone(tracePaths))
                .AppendLine();
            builder.Append(CreateTraceContextVolumeSummary(tracePaths)).AppendLine();
            builder.Append(CreateTraceIdBreadthSummary(tracePaths)).AppendLine();
            builder
                .Append("Trace context deliveries: ")
                .Append(CreateTraceContextDeliveryBreakdown(tracePaths))
                .AppendLine();
            builder.Append(CreateBusiestTraceContextShareSummary(tracePaths)).AppendLine();
            builder.Append(CreateBusiestTracePathSummary(tracePaths)).AppendLine();
            builder.Append(CreateBusiestTracePathShareSummary(tracePaths));
            return builder.ToString();
        }

        private static FlowGraphSelectedItem ResolveSelectedItem(
            FlowGraphVisibleSnapshot visibleSnapshot,
            string selectedItemKey
        )
        {
            if (!string.IsNullOrWhiteSpace(selectedItemKey))
            {
                foreach (FlowGraphComponentNode component in visibleSnapshot.ComponentNodes)
                {
                    if (
                        string.Equals(
                            CreateComponentSelectionKey(component),
                            selectedItemKey,
                            StringComparison.Ordinal
                        )
                    )
                    {
                        return FlowGraphSelectedItem.ForComponent(component);
                    }
                }

                foreach (FlowGraphMessageNode message in visibleSnapshot.MessageNodes)
                {
                    if (
                        string.Equals(
                            CreateMessageSelectionKey(message),
                            selectedItemKey,
                            StringComparison.Ordinal
                        )
                    )
                    {
                        return FlowGraphSelectedItem.ForMessage(message);
                    }
                }

                foreach (FlowGraphEdge edge in visibleSnapshot.Edges)
                {
                    if (
                        string.Equals(
                            CreateEdgeSelectionKey(edge),
                            selectedItemKey,
                            StringComparison.Ordinal
                        )
                    )
                    {
                        return FlowGraphSelectedItem.ForEdge(edge);
                    }
                }
            }

            if (visibleSnapshot.ComponentNodes.Count > 0)
            {
                return FlowGraphSelectedItem.ForComponent(visibleSnapshot.ComponentNodes[0]);
            }

            if (visibleSnapshot.MessageNodes.Count > 0)
            {
                return FlowGraphSelectedItem.ForMessage(visibleSnapshot.MessageNodes[0]);
            }

            if (visibleSnapshot.Edges.Count > 0)
            {
                return FlowGraphSelectedItem.ForEdge(visibleSnapshot.Edges[0]);
            }

            return FlowGraphSelectedItem.None;
        }

        private static int SumVisibleCalls(FlowGraphVisibleSnapshot visibleSnapshot)
        {
            return visibleSnapshot.Edges.Sum(edge => edge.CallCount);
        }

        private static int SumVisibleTracedDeliveries(FlowGraphVisibleSnapshot visibleSnapshot)
        {
            return visibleSnapshot.Edges.Sum(edge => edge.RecentTracedDeliveryCount);
        }

        private static int CountDistinct(IEnumerable<string> values)
        {
            return values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .Count();
        }

        private static string JoinDistinctOrNone(IEnumerable<string> values)
        {
            string[] distinctValues = values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            return distinctValues.Length == 0 ? "none" : string.Join(", ", distinctValues);
        }

        private static string JoinTraceContextsOrNone(IEnumerable<FlowGraphTracePath> tracePaths)
        {
            string[] distinctContexts = tracePaths
                .Select(path => NormalizeTraceContext(path.Context))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(context => context, StringComparer.Ordinal)
                .ToArray();
            return distinctContexts.Length == 0 ? "none" : string.Join(", ", distinctContexts);
        }

        private static string CreateTraceContextDeliveryBreakdown(
            IEnumerable<FlowGraphTracePath> tracePaths
        )
        {
            List<TraceContextDeliverySummary> summaries = BuildTraceContextDeliverySummaries(
                tracePaths
            );
            if (summaries.Count == 0)
            {
                return "none";
            }

            return string.Join(", ", summaries.Select(summary => summary.ToString()));
        }

        private static string CreateTraceContextVolumeSummary(
            IEnumerable<FlowGraphTracePath> tracePaths
        )
        {
            List<TraceContextDeliverySummary> summaries = BuildTraceContextDeliverySummaries(
                tracePaths
            );
            if (summaries.Count == 0)
            {
                return "Contexts: 0 | Busiest context: none";
            }

            return $"Contexts: {summaries.Count} | Busiest context: {summaries[0]}";
        }

        private static string CreateBusiestTraceContextShareSummary(
            IEnumerable<FlowGraphTracePath> tracePaths
        )
        {
            List<TraceContextDeliverySummary> summaries = BuildTraceContextDeliverySummaries(
                tracePaths
            );
            int totalDeliveries = summaries.Sum(summary => summary.DeliveryCount);
            if (summaries.Count == 0 || totalDeliveries <= 0 || summaries[0].DeliveryCount <= 0)
            {
                return "Busiest context share: none";
            }

            return $"Busiest context share: {summaries[0].Context} | Share: {CreateCallShareText(summaries[0].DeliveryCount, totalDeliveries)}";
        }

        private static int CountDistinctTraceIds(IEnumerable<FlowGraphTracePath> tracePaths)
        {
            HashSet<long> traceIds = new();
            foreach (FlowGraphTracePath path in tracePaths)
            {
                foreach (long traceId in path.TraceIds)
                {
                    if (traceId > 0)
                    {
                        traceIds.Add(traceId);
                    }
                }
            }

            return traceIds.Count;
        }

        private static string CreateTraceIdBreadthSummary(
            IEnumerable<FlowGraphTracePath> tracePaths
        )
        {
            FlowGraphTracePath[] visibleTracePaths = tracePaths.ToArray();
            return $"Trace ids: {CountDistinctTraceIds(visibleTracePaths)} | {CreateWidestTraceSummary(visibleTracePaths)}";
        }

        private static List<TraceContextDeliverySummary> BuildTraceContextDeliverySummaries(
            IEnumerable<FlowGraphTracePath> tracePaths
        )
        {
            Dictionary<string, int> deliveriesByContext = new(StringComparer.Ordinal);
            foreach (FlowGraphTracePath path in tracePaths)
            {
                string context = NormalizeTraceContext(path.Context);
                deliveriesByContext[context] =
                    deliveriesByContext.GetValueOrDefault(context) + path.RecentTracedDeliveryCount;
            }

            List<TraceContextDeliverySummary> summaries = new(deliveriesByContext.Count);
            foreach (KeyValuePair<string, int> pair in deliveriesByContext)
            {
                summaries.Add(new TraceContextDeliverySummary(pair.Key, pair.Value));
            }
            summaries.Sort(CompareTraceContextDeliveries);
            return summaries;
        }

        private static string NormalizeTraceContext(string context)
        {
            return string.IsNullOrWhiteSpace(context) ? "<none>" : context;
        }

        private static int CompareTraceContextDeliveries(
            TraceContextDeliverySummary left,
            TraceContextDeliverySummary right
        )
        {
            int deliveryComparison = right.DeliveryCount.CompareTo(left.DeliveryCount);
            return deliveryComparison != 0
                ? deliveryComparison
                : string.Compare(left.Context, right.Context, StringComparison.Ordinal);
        }

        private static string CreateCallShareText(int selectedCalls, int totalCalls)
        {
            if (totalCalls <= 0)
            {
                return selectedCalls + "/0 (n/a)";
            }

            int percent = (int)
                Math.Round(
                    (double)selectedCalls / totalCalls * 100d,
                    MidpointRounding.AwayFromZero
                );
            return $"{selectedCalls}/{totalCalls} ({percent}%)";
        }

        private static string FormatCount(int count, string singularText)
        {
            return count == 1 ? $"1 {singularText}" : $"{count} {singularText}s";
        }

        private static void AppendJsonProperty(
            StringBuilder builder,
            string name,
            string value,
            bool trailingComma
        )
        {
            AppendJsonProperty(builder, indentSize: 6, name, value, trailingComma);
        }

        private static void AppendJsonProperty(
            StringBuilder builder,
            int indentSize,
            string name,
            string value,
            bool trailingComma
        )
        {
            builder
                .Append(' ', indentSize)
                .Append("\"")
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

        private static void AppendJsonLongArray(
            StringBuilder builder,
            int indentSize,
            string name,
            IReadOnlyList<long> values,
            bool trailingComma
        )
        {
            builder.Append(' ', indentSize).Append("\"").Append(name).Append("\": [");
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(values[i]);
            }
            builder.Append("]");
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

        private static bool ContainsText(string value, string filterText)
        {
            return !string.IsNullOrEmpty(value)
                && value.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private sealed class MessageNodeBuilder
        {
            internal MessageNodeBuilder(string messageTypeName)
            {
                MessageTypeName = messageTypeName ?? string.Empty;
            }

            internal string MessageTypeName { get; }

            internal int RegistrationCount { get; set; }

            internal int CallCount { get; set; }

            internal int RecentGlobalEmissionCount { get; set; }

            internal int RecentLocalMessageCount { get; set; }

            internal int RecentTracedDeliveryCount { get; set; }

            internal FlowGraphMessageNode Build()
            {
                return new FlowGraphMessageNode(
                    MessageTypeName,
                    RegistrationCount,
                    CallCount,
                    RecentGlobalEmissionCount,
                    RecentLocalMessageCount,
                    RecentTracedDeliveryCount
                );
            }
        }

        private sealed class EdgeBuilder
        {
            internal EdgeBuilder(
                string messageTypeName,
                string targetComponentId,
                string targetComponentPath,
                string registrationTypeName
            )
            {
                MessageTypeName = messageTypeName ?? string.Empty;
                TargetComponentId = targetComponentId ?? string.Empty;
                TargetComponentPath = targetComponentPath ?? string.Empty;
                RegistrationTypeName = registrationTypeName ?? string.Empty;
            }

            internal string MessageTypeName { get; }

            internal string TargetComponentId { get; }

            internal string TargetComponentPath { get; }

            internal string RegistrationTypeName { get; }

            internal int RegistrationCount { get; set; }

            internal int CallCount { get; set; }

            internal int RecentTracedDeliveryCount { get; set; }

            internal FlowGraphEdge Build()
            {
                return new FlowGraphEdge(
                    MessageTypeName,
                    TargetComponentId,
                    TargetComponentPath,
                    RegistrationTypeName,
                    RegistrationCount,
                    CallCount,
                    RecentTracedDeliveryCount
                );
            }
        }

        private sealed class TracePathBuilder
        {
            internal TracePathBuilder(
                string messageTypeName,
                string context,
                string targetComponentId,
                string targetComponentPath,
                string registrationTypeName
            )
            {
                MessageTypeName = messageTypeName ?? string.Empty;
                Context = context ?? string.Empty;
                TargetComponentId = targetComponentId ?? string.Empty;
                TargetComponentPath = targetComponentPath ?? string.Empty;
                RegistrationTypeName = registrationTypeName ?? string.Empty;
            }

            internal string MessageTypeName { get; }

            internal string Context { get; }

            internal string TargetComponentId { get; }

            internal string TargetComponentPath { get; }

            internal string RegistrationTypeName { get; }

            internal int RecentTracedDeliveryCount { get; set; }

            private HashSet<long> TraceIds { get; } = new();

            internal void AddTraceId(long traceId)
            {
                if (traceId > 0)
                {
                    TraceIds.Add(traceId);
                }
            }

            internal FlowGraphTracePath Build()
            {
                return new FlowGraphTracePath(
                    MessageTypeName,
                    Context,
                    TargetComponentId,
                    TargetComponentPath,
                    RegistrationTypeName,
                    RecentTracedDeliveryCount,
                    TraceIds.OrderBy(traceId => traceId).ToArray()
                );
            }
        }

        private readonly struct TraceContextDeliverySummary
        {
            internal TraceContextDeliverySummary(string context, int deliveryCount)
            {
                Context = context ?? string.Empty;
                DeliveryCount = deliveryCount;
            }

            internal string Context { get; }

            internal int DeliveryCount { get; }

            public override string ToString()
            {
                return $"{Context} ({DeliveryCount})";
            }
        }

        private readonly struct FlowGraphMessageLane
        {
            internal FlowGraphMessageLane(
                string messageTypeName,
                int routeCount,
                IReadOnlyList<string> targetComponentIds,
                IReadOnlyList<string> targetComponentPaths,
                IReadOnlyList<string> routeKinds,
                int registrationCount,
                int callCount,
                int recentTracedDeliveryCount,
                int noCallRouteCount,
                int inactiveTargetCount
            )
            {
                MessageTypeName = messageTypeName ?? string.Empty;
                RouteCount = routeCount;
                TargetComponentIds = targetComponentIds ?? Array.Empty<string>();
                TargetComponentPaths = targetComponentPaths ?? Array.Empty<string>();
                RouteKinds = routeKinds ?? Array.Empty<string>();
                RegistrationCount = registrationCount;
                CallCount = callCount;
                RecentTracedDeliveryCount = recentTracedDeliveryCount;
                NoCallRouteCount = noCallRouteCount;
                InactiveTargetCount = inactiveTargetCount;
            }

            internal string MessageTypeName { get; }

            internal int RouteCount { get; }

            internal IReadOnlyList<string> TargetComponentIds { get; }

            internal IReadOnlyList<string> TargetComponentPaths { get; }

            internal int TargetCount => TargetComponentIds.Count;

            internal IReadOnlyList<string> RouteKinds { get; }

            internal int RegistrationCount { get; }

            internal int CallCount { get; }

            internal int RecentTracedDeliveryCount { get; }

            internal int NoCallRouteCount { get; }

            internal int InactiveTargetCount { get; }

            internal string RouteKindsText =>
                RouteKinds.Count == 0 ? "none" : string.Join(", ", RouteKinds);

            internal string TargetPathsText =>
                TargetComponentPaths.Count == 0 ? "none" : string.Join(", ", TargetComponentPaths);
        }

        private readonly struct FlowGraphTargetLane
        {
            internal FlowGraphTargetLane(
                string targetComponentId,
                string targetComponentPath,
                string targetStateText,
                int routeCount,
                IReadOnlyList<string> messageTypes,
                IReadOnlyList<string> routeKinds,
                int registrationCount,
                int callCount,
                int recentTracedDeliveryCount,
                int noCallRouteCount
            )
            {
                TargetComponentId = targetComponentId ?? string.Empty;
                TargetComponentPath = targetComponentPath ?? string.Empty;
                TargetStateText = targetStateText ?? "unknown";
                RouteCount = routeCount;
                MessageTypes = messageTypes ?? Array.Empty<string>();
                RouteKinds = routeKinds ?? Array.Empty<string>();
                RegistrationCount = registrationCount;
                CallCount = callCount;
                RecentTracedDeliveryCount = recentTracedDeliveryCount;
                NoCallRouteCount = noCallRouteCount;
            }

            internal string TargetComponentId { get; }

            internal string TargetComponentPath { get; }

            internal string TargetStateText { get; }

            internal int RouteCount { get; }

            internal IReadOnlyList<string> MessageTypes { get; }

            internal int MessageCount => MessageTypes.Count;

            internal IReadOnlyList<string> RouteKinds { get; }

            internal int RegistrationCount { get; }

            internal int CallCount { get; }

            internal int RecentTracedDeliveryCount { get; }

            internal int NoCallRouteCount { get; }

            internal string MessageTypesText =>
                MessageTypes.Count == 0 ? "none" : string.Join(", ", MessageTypes);

            internal string RouteKindsText =>
                RouteKinds.Count == 0 ? "none" : string.Join(", ", RouteKinds);
        }

        private readonly struct FlowGraphFlowCorridor
        {
            internal FlowGraphFlowCorridor(
                string messageTypeName,
                string targetComponentId,
                string targetComponentPath,
                int pathCount,
                int contextCount,
                int traceIdCount,
                IReadOnlyList<string> routeKinds,
                int deliveryCount
            )
            {
                MessageTypeName = messageTypeName ?? string.Empty;
                TargetComponentId = targetComponentId ?? string.Empty;
                TargetComponentPath = targetComponentPath ?? string.Empty;
                PathCount = pathCount;
                ContextCount = contextCount;
                TraceIdCount = traceIdCount;
                RouteKinds = routeKinds ?? Array.Empty<string>();
                DeliveryCount = deliveryCount;
            }

            internal string MessageTypeName { get; }

            internal string TargetComponentId { get; }

            internal string TargetComponentPath { get; }

            internal int PathCount { get; }

            internal int ContextCount { get; }

            internal int TraceIdCount { get; }

            internal IReadOnlyList<string> RouteKinds { get; }

            internal int DeliveryCount { get; }

            internal string RouteKindsText =>
                RouteKinds.Count == 0 ? "none" : string.Join(", ", RouteKinds);
        }

        private readonly struct FlowGraphContextLane
        {
            internal FlowGraphContextLane(
                string context,
                int pathCount,
                IReadOnlyList<string> messageTypes,
                IReadOnlyList<string> targetComponentIds,
                IReadOnlyList<string> targetComponentPaths,
                IReadOnlyList<long> traceIds,
                IReadOnlyList<string> routeKinds,
                int deliveryCount
            )
            {
                Context = context ?? string.Empty;
                PathCount = pathCount;
                MessageTypes = messageTypes ?? Array.Empty<string>();
                TargetComponentIds = targetComponentIds ?? Array.Empty<string>();
                TargetComponentPaths = targetComponentPaths ?? Array.Empty<string>();
                TraceIds = traceIds ?? Array.Empty<long>();
                RouteKinds = routeKinds ?? Array.Empty<string>();
                DeliveryCount = deliveryCount;
            }

            internal string Context { get; }

            internal int PathCount { get; }

            internal IReadOnlyList<string> MessageTypes { get; }

            internal int MessageCount => MessageTypes.Count;

            internal IReadOnlyList<string> TargetComponentIds { get; }

            internal int TargetCount => TargetComponentIds.Count;

            internal IReadOnlyList<string> TargetComponentPaths { get; }

            internal IReadOnlyList<long> TraceIds { get; }

            internal int TraceIdCount => TraceIds.Count;

            internal IReadOnlyList<string> RouteKinds { get; }

            internal int DeliveryCount { get; }

            internal string MessageTypesText =>
                MessageTypes.Count == 0 ? "none" : string.Join(", ", MessageTypes);

            internal string TargetPathsText =>
                TargetComponentPaths.Count == 0 ? "none" : string.Join(", ", TargetComponentPaths);

            internal string RouteKindsText =>
                RouteKinds.Count == 0 ? "none" : string.Join(", ", RouteKinds);
        }

        private readonly struct FlowGraphTraceMessageLane
        {
            internal FlowGraphTraceMessageLane(
                string messageTypeName,
                int pathCount,
                IReadOnlyList<string> contexts,
                IReadOnlyList<string> targetComponentIds,
                IReadOnlyList<string> targetComponentPaths,
                IReadOnlyList<long> traceIds,
                IReadOnlyList<string> routeKinds,
                int deliveryCount
            )
            {
                MessageTypeName = messageTypeName ?? string.Empty;
                PathCount = pathCount;
                Contexts = contexts ?? Array.Empty<string>();
                TargetComponentIds = targetComponentIds ?? Array.Empty<string>();
                TargetComponentPaths = targetComponentPaths ?? Array.Empty<string>();
                TraceIds = traceIds ?? Array.Empty<long>();
                RouteKinds = routeKinds ?? Array.Empty<string>();
                DeliveryCount = deliveryCount;
            }

            internal string MessageTypeName { get; }

            internal int PathCount { get; }

            internal IReadOnlyList<string> Contexts { get; }

            internal int ContextCount => Contexts.Count;

            internal IReadOnlyList<string> TargetComponentIds { get; }

            internal int TargetCount => TargetComponentIds.Count;

            internal IReadOnlyList<string> TargetComponentPaths { get; }

            internal IReadOnlyList<long> TraceIds { get; }

            internal int TraceIdCount => TraceIds.Count;

            internal IReadOnlyList<string> RouteKinds { get; }

            internal int DeliveryCount { get; }

            internal string ContextsText =>
                Contexts.Count == 0 ? "none" : string.Join(", ", Contexts);

            internal string TargetPathsText =>
                TargetComponentPaths.Count == 0 ? "none" : string.Join(", ", TargetComponentPaths);

            internal string RouteKindsText =>
                RouteKinds.Count == 0 ? "none" : string.Join(", ", RouteKinds);
        }

        private readonly struct FlowGraphTraceRouteKindLane
        {
            internal FlowGraphTraceRouteKindLane(
                string routeKind,
                int pathCount,
                IReadOnlyList<string> messageTypes,
                IReadOnlyList<string> targetComponentIds,
                IReadOnlyList<string> targetComponentPaths,
                IReadOnlyList<string> contexts,
                IReadOnlyList<long> traceIds,
                int deliveryCount
            )
            {
                RouteKind = string.IsNullOrWhiteSpace(routeKind)
                    ? "<unknown route kind>"
                    : routeKind.Trim();
                PathCount = pathCount;
                MessageTypes = messageTypes ?? Array.Empty<string>();
                TargetComponentIds = targetComponentIds ?? Array.Empty<string>();
                TargetComponentPaths = targetComponentPaths ?? Array.Empty<string>();
                Contexts = contexts ?? Array.Empty<string>();
                TraceIds = traceIds ?? Array.Empty<long>();
                DeliveryCount = deliveryCount;
            }

            internal string RouteKind { get; }

            internal int PathCount { get; }

            internal IReadOnlyList<string> MessageTypes { get; }

            internal int MessageCount => MessageTypes.Count;

            internal IReadOnlyList<string> TargetComponentIds { get; }

            internal int TargetCount => TargetComponentIds.Count;

            internal IReadOnlyList<string> TargetComponentPaths { get; }

            internal IReadOnlyList<string> Contexts { get; }

            internal int ContextCount => Contexts.Count;

            internal IReadOnlyList<long> TraceIds { get; }

            internal int TraceIdCount => TraceIds.Count;

            internal int DeliveryCount { get; }

            internal string MessageTypesText =>
                MessageTypes.Count == 0 ? "none" : string.Join(", ", MessageTypes);

            internal string TargetPathsText =>
                TargetComponentPaths.Count == 0 ? "none" : string.Join(", ", TargetComponentPaths);

            internal string ContextsText =>
                Contexts.Count == 0 ? "none" : string.Join(", ", Contexts);
        }

        private readonly struct FlowGraphTraceIdLane
        {
            internal FlowGraphTraceIdLane(
                long traceId,
                int pathCount,
                IReadOnlyList<string> messageTypes,
                IReadOnlyList<string> targetComponentIds,
                IReadOnlyList<string> targetComponentPaths,
                IReadOnlyList<string> contexts,
                IReadOnlyList<string> routeKinds
            )
            {
                TraceId = traceId;
                PathCount = pathCount;
                MessageTypes = messageTypes ?? Array.Empty<string>();
                TargetComponentIds = targetComponentIds ?? Array.Empty<string>();
                TargetComponentPaths = targetComponentPaths ?? Array.Empty<string>();
                Contexts = contexts ?? Array.Empty<string>();
                RouteKinds = routeKinds ?? Array.Empty<string>();
            }

            internal long TraceId { get; }

            internal int PathCount { get; }

            internal IReadOnlyList<string> MessageTypes { get; }

            internal int MessageCount => MessageTypes.Count;

            internal IReadOnlyList<string> TargetComponentIds { get; }

            internal int TargetCount => TargetComponentIds.Count;

            internal IReadOnlyList<string> TargetComponentPaths { get; }

            internal IReadOnlyList<string> Contexts { get; }

            internal int ContextCount => Contexts.Count;

            internal IReadOnlyList<string> RouteKinds { get; }

            internal string MessageTypesText =>
                MessageTypes.Count == 0 ? "none" : string.Join(", ", MessageTypes);

            internal string TargetPathsText =>
                TargetComponentPaths.Count == 0 ? "none" : string.Join(", ", TargetComponentPaths);

            internal string ContextsText =>
                Contexts.Count == 0 ? "none" : string.Join(", ", Contexts);

            internal string RouteKindsText =>
                RouteKinds.Count == 0 ? "none" : string.Join(", ", RouteKinds);
        }

        private readonly struct FlowGraphTraceIdPathMembership
        {
            internal FlowGraphTraceIdPathMembership(long traceId, FlowGraphTracePath path)
            {
                TraceId = traceId;
                Path = path;
            }

            internal long TraceId { get; }

            internal FlowGraphTracePath Path { get; }
        }

        private readonly struct FlowGraphTraceTargetLane
        {
            internal FlowGraphTraceTargetLane(
                string targetComponentId,
                string targetComponentPath,
                string targetDisplayPath,
                int pathCount,
                IReadOnlyList<string> messageTypes,
                IReadOnlyList<string> contexts,
                IReadOnlyList<long> traceIds,
                IReadOnlyList<string> routeKinds,
                int deliveryCount
            )
            {
                TargetComponentId = targetComponentId ?? string.Empty;
                TargetComponentPath = targetComponentPath ?? string.Empty;
                TargetDisplayPath = targetDisplayPath ?? string.Empty;
                PathCount = pathCount;
                MessageTypes = messageTypes ?? Array.Empty<string>();
                Contexts = contexts ?? Array.Empty<string>();
                TraceIds = traceIds ?? Array.Empty<long>();
                RouteKinds = routeKinds ?? Array.Empty<string>();
                DeliveryCount = deliveryCount;
            }

            internal string TargetComponentId { get; }

            internal string TargetComponentPath { get; }

            internal string TargetDisplayPath { get; }

            internal int PathCount { get; }

            internal IReadOnlyList<string> MessageTypes { get; }

            internal int MessageCount => MessageTypes.Count;

            internal IReadOnlyList<string> Contexts { get; }

            internal int ContextCount => Contexts.Count;

            internal IReadOnlyList<long> TraceIds { get; }

            internal int TraceIdCount => TraceIds.Count;

            internal IReadOnlyList<string> RouteKinds { get; }

            internal int DeliveryCount { get; }

            internal string MessageTypesText =>
                MessageTypes.Count == 0 ? "none" : string.Join(", ", MessageTypes);

            internal string ContextsText =>
                Contexts.Count == 0 ? "none" : string.Join(", ", Contexts);

            internal string RouteKindsText =>
                RouteKinds.Count == 0 ? "none" : string.Join(", ", RouteKinds);
        }

        private readonly struct TraceTargetDeliverySummary
        {
            internal TraceTargetDeliverySummary(
                string targetComponentId,
                string targetComponentPath,
                int deliveryCount
            )
            {
                TargetComponentId = targetComponentId ?? string.Empty;
                TargetComponentPath = targetComponentPath ?? string.Empty;
                DeliveryCount = deliveryCount;
            }

            internal string TargetComponentId { get; }

            internal string TargetComponentPath { get; }

            internal int DeliveryCount { get; }
        }

        private readonly struct TraceIdPathSummary
        {
            internal TraceIdPathSummary(long traceId, int pathCount)
            {
                TraceId = traceId;
                PathCount = pathCount;
            }

            internal long TraceId { get; }

            internal int PathCount { get; }
        }

        private readonly struct MessageFanOutSummary
        {
            internal MessageFanOutSummary(
                string messageTypeName,
                int targetComponentCount,
                int callCount
            )
            {
                MessageTypeName = messageTypeName ?? string.Empty;
                TargetComponentCount = targetComponentCount;
                CallCount = callCount;
            }

            internal string MessageTypeName { get; }

            internal int TargetComponentCount { get; }

            internal int CallCount { get; }
        }

        private readonly struct RouteKindSummary
        {
            internal RouteKindSummary(string registrationTypeName, int routeCount)
            {
                RegistrationTypeName = registrationTypeName ?? string.Empty;
                RouteCount = routeCount;
            }

            internal string RegistrationTypeName { get; }

            internal int RouteCount { get; }
        }

        private readonly struct MessageTraceDeliverySummary
        {
            internal MessageTraceDeliverySummary(string messageTypeName, int deliveryCount)
            {
                MessageTypeName = messageTypeName ?? string.Empty;
                DeliveryCount = deliveryCount;
            }

            internal string MessageTypeName { get; }

            internal int DeliveryCount { get; }
        }

        private readonly struct TargetFanInSummary
        {
            internal TargetFanInSummary(
                string targetComponentId,
                string targetComponentPath,
                int routeCount,
                int callCount
            )
            {
                TargetComponentId = targetComponentId ?? string.Empty;
                TargetComponentPath = targetComponentPath ?? string.Empty;
                RouteCount = routeCount;
                CallCount = callCount;
            }

            internal string TargetComponentId { get; }

            internal string TargetComponentPath { get; }

            internal int RouteCount { get; }

            internal int CallCount { get; }
        }

        private sealed class FlowGraphVisibleSnapshot
        {
            internal FlowGraphVisibleSnapshot(
                IReadOnlyList<FlowGraphComponentNode> componentNodes,
                IReadOnlyList<FlowGraphMessageNode> messageNodes,
                IReadOnlyList<FlowGraphEdge> edges,
                IReadOnlyList<FlowGraphTracePath> tracePaths,
                IReadOnlyList<string> warnings
            )
            {
                ComponentNodes =
                    componentNodes ?? throw new ArgumentNullException(nameof(componentNodes));
                MessageNodes =
                    messageNodes ?? throw new ArgumentNullException(nameof(messageNodes));
                Edges = edges ?? throw new ArgumentNullException(nameof(edges));
                TracePaths = tracePaths ?? throw new ArgumentNullException(nameof(tracePaths));
                Warnings = warnings ?? throw new ArgumentNullException(nameof(warnings));
            }

            internal IReadOnlyList<FlowGraphComponentNode> ComponentNodes { get; }

            internal IReadOnlyList<FlowGraphMessageNode> MessageNodes { get; }

            internal IReadOnlyList<FlowGraphEdge> Edges { get; }

            internal IReadOnlyList<FlowGraphTracePath> TracePaths { get; }

            internal IReadOnlyList<string> Warnings { get; }
        }

        private enum FlowGraphSelectionKind
        {
            None,
            Component,
            Message,
            Edge,
        }

        private readonly struct FlowGraphSelectedItem
        {
            private FlowGraphSelectedItem(
                FlowGraphSelectionKind kind,
                string key,
                FlowGraphComponentNode component,
                FlowGraphMessageNode message,
                FlowGraphEdge edge
            )
            {
                Kind = kind;
                Key = key ?? string.Empty;
                Component = component;
                Message = message;
                Edge = edge;
                HasValue = kind != FlowGraphSelectionKind.None;
            }

            internal static FlowGraphSelectedItem None { get; } =
                new(FlowGraphSelectionKind.None, string.Empty, default, default, default);

            internal FlowGraphSelectionKind Kind { get; }

            internal string Key { get; }

            internal FlowGraphComponentNode Component { get; }

            internal FlowGraphMessageNode Message { get; }

            internal FlowGraphEdge Edge { get; }

            internal bool HasValue { get; }

            internal static FlowGraphSelectedItem ForComponent(FlowGraphComponentNode component)
            {
                return new FlowGraphSelectedItem(
                    FlowGraphSelectionKind.Component,
                    CreateComponentSelectionKey(component),
                    component,
                    default,
                    default
                );
            }

            internal static FlowGraphSelectedItem ForMessage(FlowGraphMessageNode message)
            {
                return new FlowGraphSelectedItem(
                    FlowGraphSelectionKind.Message,
                    CreateMessageSelectionKey(message),
                    default,
                    message,
                    default
                );
            }

            internal static FlowGraphSelectedItem ForEdge(FlowGraphEdge edge)
            {
                return new FlowGraphSelectedItem(
                    FlowGraphSelectionKind.Edge,
                    CreateEdgeSelectionKey(edge),
                    default,
                    default,
                    edge
                );
            }
        }
    }

    internal readonly struct FlowGraphViewState
    {
        internal static FlowGraphViewState Default { get; } = new();

        internal FlowGraphViewState(string filterText = "", string selectedItemKey = "")
        {
            FilterText = filterText ?? string.Empty;
            SelectedItemKey = selectedItemKey ?? string.Empty;
        }

        internal string FilterText { get; }

        internal string SelectedItemKey { get; }
    }

    internal sealed class FlowGraphSnapshot
    {
        internal static FlowGraphSnapshot Empty { get; } =
            new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                Array.Empty<FlowGraphTracePath>(),
                Array.Empty<string>()
            );

        internal FlowGraphSnapshot(
            IReadOnlyList<FlowGraphComponentNode> componentNodes,
            IReadOnlyList<FlowGraphMessageNode> messageNodes,
            IReadOnlyList<FlowGraphEdge> edges,
            IReadOnlyList<string> warnings
        )
            : this(componentNodes, messageNodes, edges, Array.Empty<FlowGraphTracePath>(), warnings)
        { }

        internal FlowGraphSnapshot(
            IReadOnlyList<FlowGraphComponentNode> componentNodes,
            IReadOnlyList<FlowGraphMessageNode> messageNodes,
            IReadOnlyList<FlowGraphEdge> edges,
            IReadOnlyList<FlowGraphTracePath> tracePaths,
            IReadOnlyList<string> warnings
        )
        {
            ComponentNodes =
                componentNodes ?? throw new ArgumentNullException(nameof(componentNodes));
            MessageNodes = messageNodes ?? throw new ArgumentNullException(nameof(messageNodes));
            Edges = edges ?? throw new ArgumentNullException(nameof(edges));
            TracePaths = tracePaths ?? throw new ArgumentNullException(nameof(tracePaths));
            Warnings = warnings ?? throw new ArgumentNullException(nameof(warnings));
        }

        internal IReadOnlyList<FlowGraphComponentNode> ComponentNodes { get; }

        internal IReadOnlyList<FlowGraphMessageNode> MessageNodes { get; }

        internal IReadOnlyList<FlowGraphEdge> Edges { get; }

        internal IReadOnlyList<FlowGraphTracePath> TracePaths { get; }

        internal IReadOnlyList<string> Warnings { get; }
    }

    internal readonly struct FlowGraphComponentNode
    {
        internal FlowGraphComponentNode(
            string id,
            string hierarchyPath,
            string componentTypeName,
            bool activeInHierarchy,
            int listenerCount,
            int registrationCount,
            int callCount,
            int localMessageCount
        )
        {
            Id = id ?? string.Empty;
            HierarchyPath = hierarchyPath ?? string.Empty;
            ComponentTypeName = componentTypeName ?? string.Empty;
            ActiveInHierarchy = activeInHierarchy;
            ListenerCount = listenerCount;
            RegistrationCount = registrationCount;
            CallCount = callCount;
            LocalMessageCount = localMessageCount;
        }

        internal string Id { get; }

        internal string HierarchyPath { get; }

        internal string ComponentTypeName { get; }

        internal bool ActiveInHierarchy { get; }

        internal int ListenerCount { get; }

        internal int RegistrationCount { get; }

        internal int CallCount { get; }

        internal int LocalMessageCount { get; }

        internal bool Matches(string filterText)
        {
            return ContainsText(Id, filterText)
                || ContainsText(HierarchyPath, filterText)
                || ContainsText(ComponentTypeName, filterText)
                || ContainsText(ActiveInHierarchy ? "active" : "inactive", filterText);
        }

        private static bool ContainsText(string value, string filterText)
        {
            return !string.IsNullOrEmpty(value)
                && value.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    internal readonly struct FlowGraphMessageNode
    {
        internal FlowGraphMessageNode(
            string messageTypeName,
            int registrationCount,
            int callCount,
            int recentGlobalEmissionCount = 0,
            int recentLocalMessageCount = 0,
            int recentTracedDeliveryCount = 0
        )
        {
            MessageTypeName = messageTypeName ?? string.Empty;
            RegistrationCount = registrationCount;
            CallCount = callCount;
            RecentGlobalEmissionCount = recentGlobalEmissionCount;
            RecentLocalMessageCount = recentLocalMessageCount;
            RecentTracedDeliveryCount = recentTracedDeliveryCount;
        }

        internal string MessageTypeName { get; }

        internal int RegistrationCount { get; }

        internal int CallCount { get; }

        internal int RecentGlobalEmissionCount { get; }

        internal int RecentLocalMessageCount { get; }

        internal int RecentTracedDeliveryCount { get; }

        internal bool Matches(string filterText)
        {
            return ContainsText(MessageTypeName, filterText);
        }

        private static bool ContainsText(string value, string filterText)
        {
            return !string.IsNullOrEmpty(value)
                && value.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    internal readonly struct FlowGraphTracePath
    {
        internal FlowGraphTracePath(
            string messageTypeName,
            string context,
            string targetComponentId,
            string targetComponentPath,
            string registrationTypeName,
            int recentTracedDeliveryCount,
            IReadOnlyList<long> traceIds = null
        )
        {
            MessageTypeName = messageTypeName ?? string.Empty;
            Context = context ?? string.Empty;
            TargetComponentId = targetComponentId ?? string.Empty;
            TargetComponentPath = targetComponentPath ?? string.Empty;
            RegistrationTypeName = registrationTypeName ?? string.Empty;
            RecentTracedDeliveryCount = recentTracedDeliveryCount;
            TraceIds = NormalizeTraceIds(traceIds);
        }

        internal string MessageTypeName { get; }

        internal string Context { get; }

        internal string TargetComponentId { get; }

        internal string TargetComponentPath { get; }

        internal string RegistrationTypeName { get; }

        internal int RecentTracedDeliveryCount { get; }

        internal IReadOnlyList<long> TraceIds { get; }

        internal int RecentTraceIdCount => TraceIds.Count;

        internal bool Matches(string filterText)
        {
            return ContainsText(MessageTypeName, filterText)
                || ContainsText(Context, filterText)
                || ContainsText(NormalizeContext(Context), filterText)
                || ContainsText(TargetComponentId, filterText)
                || ContainsText(TargetComponentPath, filterText)
                || ContainsText(RegistrationTypeName, filterText)
                || ContainsText(NormalizeTraceRouteKind(RegistrationTypeName), filterText)
                || ContainsText(CreateTraceRouteKindFilterText(RegistrationTypeName), filterText)
                || TraceIds.Any(traceId =>
                    ContainsText(traceId.ToString(CultureInfo.InvariantCulture), filterText)
                );
        }

        private static IReadOnlyList<long> NormalizeTraceIds(IReadOnlyList<long> traceIds)
        {
            return traceIds == null
                ? Array.Empty<long>()
                : traceIds
                    .Where(traceId => traceId > 0)
                    .Distinct()
                    .OrderBy(traceId => traceId)
                    .ToArray();
        }

        private static string NormalizeContext(string context)
        {
            return string.IsNullOrWhiteSpace(context) ? "<none>" : context;
        }

        private static string NormalizeTraceRouteKind(string routeKind)
        {
            string taxonomyKind = DxMessagingEditorPalette.NormalizeRouteKind(routeKind);
            if (!string.IsNullOrWhiteSpace(taxonomyKind))
            {
                return taxonomyKind;
            }

            return string.IsNullOrWhiteSpace(routeKind) ? "<unknown route kind>" : routeKind.Trim();
        }

        private static string CreateTraceRouteKindFilterText(string routeKind)
        {
            string taxonomyKind = DxMessagingEditorPalette.NormalizeRouteKind(routeKind);
            if (!string.IsNullOrWhiteSpace(taxonomyKind))
            {
                return taxonomyKind;
            }

            return string.IsNullOrWhiteSpace(routeKind) ? "unknown route kind" : routeKind.Trim();
        }

        private static bool ContainsText(string value, string filterText)
        {
            return !string.IsNullOrEmpty(value)
                && value.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    internal readonly struct FlowGraphEdge
    {
        internal FlowGraphEdge(
            string messageTypeName,
            string targetComponentId,
            string targetComponentPath,
            string registrationTypeName,
            int registrationCount,
            int callCount,
            int recentTracedDeliveryCount = 0
        )
        {
            MessageTypeName = messageTypeName ?? string.Empty;
            TargetComponentId = targetComponentId ?? string.Empty;
            TargetComponentPath = targetComponentPath ?? string.Empty;
            RegistrationTypeName = registrationTypeName ?? string.Empty;
            RegistrationCount = registrationCount;
            CallCount = callCount;
            RecentTracedDeliveryCount = recentTracedDeliveryCount;
        }

        internal string MessageTypeName { get; }

        internal string TargetComponentId { get; }

        internal string TargetComponentPath { get; }

        internal string RegistrationTypeName { get; }

        internal int RegistrationCount { get; }

        internal int CallCount { get; }

        internal int RecentTracedDeliveryCount { get; }

        internal bool Matches(string filterText)
        {
            return ContainsText(MessageTypeName, filterText)
                || ContainsText(TargetComponentId, filterText)
                || ContainsText(TargetComponentPath, filterText)
                || ContainsText(RegistrationTypeName, filterText);
        }

        private static bool ContainsText(string value, string filterText)
        {
            return !string.IsNullOrEmpty(value)
                && value.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
#endif
