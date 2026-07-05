#if UNITY_EDITOR && UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Editor
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
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
    public sealed class DxMessagingFlowGraphWindowTests
    {
        private readonly List<Object> _createdObjects = new();
        private readonly List<string> _createdAssetPaths = new();
        private readonly List<EditorWindow> _createdWindows = new();
        private const string MessageLanesName = "dxmessaging-flow-graph-message-lanes";
        private const string MessageLaneRowClassName = "dxmessaging-flow-graph-message-lane-row";
        private const string MessageLanesSummaryLabelName =
            "dxmessaging-flow-graph-message-lanes-summary";
        private const string MessageLaneMessageLabelName =
            "dxmessaging-flow-graph-message-lane-message";
        private const string MessageLaneSummaryLabelName =
            "dxmessaging-flow-graph-message-lane-summary";
        private const string MessageLaneTargetsLabelName =
            "dxmessaging-flow-graph-message-lane-targets";
        private const string TargetLanesName = "dxmessaging-flow-graph-target-lanes";
        private const string TargetLaneRowClassName = "dxmessaging-flow-graph-target-lane-row";
        private const string TargetLanesSummaryLabelName =
            "dxmessaging-flow-graph-target-lanes-summary";
        private const string TargetLaneTargetLabelName =
            "dxmessaging-flow-graph-target-lane-target";
        private const string TargetLaneSummaryLabelName =
            "dxmessaging-flow-graph-target-lane-summary";
        private const string TargetLaneMessagesLabelName =
            "dxmessaging-flow-graph-target-lane-messages";
        private const string ContextLanesName = "dxmessaging-flow-graph-context-lanes";
        private const string ContextLaneRowClassName = "dxmessaging-flow-graph-context-lane-row";
        private const string ContextLanesSummaryLabelName =
            "dxmessaging-flow-graph-context-lanes-summary";
        private const string ContextLaneContextLabelName =
            "dxmessaging-flow-graph-context-lane-context";
        private const string ContextLaneSummaryLabelName =
            "dxmessaging-flow-graph-context-lane-summary";
        private const string ContextLaneDetailsLabelName =
            "dxmessaging-flow-graph-context-lane-details";
        private const string TraceMessageLanesName = "dxmessaging-flow-graph-trace-message-lanes";
        private const string TraceMessageLaneRowClassName =
            "dxmessaging-flow-graph-trace-message-lane-row";
        private const string TraceMessageLanesSummaryLabelName =
            "dxmessaging-flow-graph-trace-message-lanes-summary";
        private const string TraceMessageLaneMessageLabelName =
            "dxmessaging-flow-graph-trace-message-lane-message";
        private const string TraceMessageLaneSummaryLabelName =
            "dxmessaging-flow-graph-trace-message-lane-summary";
        private const string TraceMessageLaneDetailsLabelName =
            "dxmessaging-flow-graph-trace-message-lane-details";
        private const string TraceTargetLanesName = "dxmessaging-flow-graph-trace-target-lanes";
        private const string TraceTargetLaneRowClassName =
            "dxmessaging-flow-graph-trace-target-lane-row";
        private const string TraceTargetLanesSummaryLabelName =
            "dxmessaging-flow-graph-trace-target-lanes-summary";
        private const string TraceTargetLaneTargetLabelName =
            "dxmessaging-flow-graph-trace-target-lane-target";
        private const string TraceTargetLaneSummaryLabelName =
            "dxmessaging-flow-graph-trace-target-lane-summary";
        private const string TraceTargetLaneDetailsLabelName =
            "dxmessaging-flow-graph-trace-target-lane-details";
        private const string TraceRouteKindLanesName =
            "dxmessaging-flow-graph-trace-route-kind-lanes";
        private const string TraceRouteKindLaneRowClassName =
            "dxmessaging-flow-graph-trace-route-kind-lane-row";
        private const string TraceRouteKindLanesSummaryLabelName =
            "dxmessaging-flow-graph-trace-route-kind-lanes-summary";
        private const string TraceRouteKindLaneRouteKindLabelName =
            "dxmessaging-flow-graph-trace-route-kind-lane-route-kind";
        private const string TraceRouteKindLaneSummaryLabelName =
            "dxmessaging-flow-graph-trace-route-kind-lane-summary";
        private const string TraceRouteKindLaneDetailsLabelName =
            "dxmessaging-flow-graph-trace-route-kind-lane-details";
        private const string TraceIdLanesName = "dxmessaging-flow-graph-trace-id-lanes";
        private const string TraceIdLaneRowClassName = "dxmessaging-flow-graph-trace-id-lane-row";
        private const string TraceIdLanesSummaryLabelName =
            "dxmessaging-flow-graph-trace-id-lanes-summary";
        private const string TraceIdLaneTraceIdLabelName =
            "dxmessaging-flow-graph-trace-id-lane-trace-id";
        private const string TraceIdLaneSummaryLabelName =
            "dxmessaging-flow-graph-trace-id-lane-summary";
        private const string TraceIdLaneDetailsLabelName =
            "dxmessaging-flow-graph-trace-id-lane-details";

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
        public void BuildGraphUiRendersSummaryNodesAndEdges()
        {
            FlowGraphSnapshot snapshot = new(
                new[]
                {
                    new FlowGraphComponentNode(
                        "component:1",
                        "Root/Listener",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 3,
                        localMessageCount: 2
                    ),
                },
                new[] { new FlowGraphMessageNode("FlowGraphMessage", 1, 3) },
                new[]
                {
                    new FlowGraphEdge(
                        "FlowGraphMessage",
                        "component:1",
                        "Root/Listener",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 3
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            Assert.That(root.ClassListContains(DxMessagingFlowGraphWindow.RootClassName), Is.True);
            Assert.That(root.ClassListContains(DxMessagingEditorTheme.ThemeClassName), Is.True);
            Assert.That(root.ClassListContains(DxMessagingEditorTheme.WindowClassName), Is.True);
            Assert.That(
                root.Query<VisualElement>(className: DxMessagingFlowGraphWindow.ToolbarClassName)
                    .First()
                    .ClassListContains(DxMessagingEditorTheme.ToolbarClassName),
                Is.True
            );
            Assert.That(
                root.Q<Label>(DxMessagingFlowGraphWindow.StatusLabelName).text,
                Does.Contain("1 components")
            );
            Assert.That(root.Q<TextField>(DxMessagingFlowGraphWindow.FilterFieldName), Is.Not.Null);
            Assert.That(
                root.Q<TextField>(DxMessagingFlowGraphWindow.FilterFieldName)
                    .ClassListContains(DxMessagingEditorTheme.SearchClassName),
                Is.True
            );
            Assert.That(root.Q<Button>(DxMessagingFlowGraphWindow.ExportButtonName), Is.Not.Null);
            Assert.That(
                root.Q<Button>(DxMessagingFlowGraphWindow.ExportButtonName)
                    .ClassListContains(DxMessagingEditorTheme.ToolButtonClassName),
                Is.True
            );
            Label routeMapKind = root.Q<VisualElement>(DxMessagingFlowGraphWindow.RouteMapName)
                .Query<VisualElement>(className: DxMessagingFlowGraphWindow.RouteMapRouteClassName)
                .First()
                .Q<Label>(DxMessagingFlowGraphWindow.RouteMapRouteKindLabelName);
            AssertRouteKindBadge(routeMapKind, DxMessagingEditorPalette.UntargetedKind);
            Assert.That(
                root.Query<VisualElement>(
                        className: DxMessagingFlowGraphWindow.ComponentNodeClassName
                    )
                    .ToList()
                    .Count,
                Is.EqualTo(1)
            );
            Assert.That(
                root.Query<VisualElement>(
                        className: DxMessagingFlowGraphWindow.MessageNodeClassName
                    )
                    .ToList()
                    .Count,
                Is.EqualTo(1)
            );
            List<VisualElement> edges = root.Query<VisualElement>(
                    className: DxMessagingFlowGraphWindow.EdgeRowClassName
                )
                .ToList();
            Assert.That(edges.Count, Is.EqualTo(1));
            Assert.That(edges[0].ClassListContains(DxMessagingEditorTheme.CardClassName), Is.True);
            Assert.That(
                edges[0].Q<Label>(DxMessagingFlowGraphWindow.EdgeLabelName).text,
                Does.Contain("FlowGraphMessage -> Root/Listener")
            );
            AssertRouteKindBadge(
                edges[0].Q<Label>(DxMessagingFlowGraphWindow.EdgeRouteKindLabelName),
                DxMessagingEditorPalette.UntargetedKind
            );
        }

        [Test]
        public void BuildGraphUiColorsRouteRowsByRegistrationTaxonomy()
        {
            FlowGraphSnapshot snapshot = new(
                new[]
                {
                    new FlowGraphComponentNode(
                        "component:1",
                        "Root/Listener",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 2,
                        callCount: 5,
                        localMessageCount: 0
                    ),
                },
                new[] { new FlowGraphMessageNode("FlowGraphMessage", 2, 5) },
                new[]
                {
                    new FlowGraphEdge(
                        "FlowGraphMessage",
                        "component:1",
                        "Root/Listener",
                        "TargetedWithoutTargeting",
                        registrationCount: 1,
                        callCount: 3
                    ),
                    new FlowGraphEdge(
                        "FlowGraphMessage",
                        "component:1",
                        "Root/Listener",
                        "BroadcastPostProcessor",
                        registrationCount: 1,
                        callCount: 2
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            string routeMapSummary = root.Q<VisualElement>(DxMessagingFlowGraphWindow.RouteMapName)
                .Q<Label>(DxMessagingFlowGraphWindow.RouteMapSummaryLabelName)
                .text;
            Assert.That(routeMapSummary, Does.Contain("Route kinds: Broadcast 1, Targeted 1"));
            string messageLaneSummary = root.Q<VisualElement>(MessageLanesName)
                .Query<VisualElement>(className: MessageLaneRowClassName)
                .First()
                .Q<Label>(MessageLaneSummaryLabelName)
                .text;
            Assert.That(messageLaneSummary, Does.Contain("Route kinds: Broadcast, Targeted"));
            Assert.That(messageLaneSummary, Does.Not.Contain("TargetedWithoutTargeting"));
            string targetLaneSummary = root.Q<VisualElement>(TargetLanesName)
                .Query<VisualElement>(className: TargetLaneRowClassName)
                .First()
                .Q<Label>(TargetLaneSummaryLabelName)
                .text;
            Assert.That(targetLaneSummary, Does.Contain("Route kinds: Broadcast, Targeted"));
            Assert.That(targetLaneSummary, Does.Not.Contain("BroadcastPostProcessor"));

            Dictionary<string, VisualElement> edgesByKind = root.Query<VisualElement>(
                    className: DxMessagingFlowGraphWindow.EdgeRowClassName
                )
                .ToList()
                .ToDictionary(
                    row =>
                        row.Q<Label>(DxMessagingFlowGraphWindow.EdgeLabelName)
                            .text.Contains("BroadcastPostProcessor")
                            ? "Broadcast"
                            : "Targeted",
                    StringComparer.Ordinal
                );

            AssertCompleteBorder(edgesByKind["Targeted"], DxMessagingEditorPalette.Targeted);
            AssertCompleteBorder(edgesByKind["Broadcast"], DxMessagingEditorPalette.Broadcast);
        }

        [Test]
        public void BuildGraphUiColorsVisibleTraceLanesFromEditorPalette()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "FlowGraphMessage",
                        "source: { Id = 42 }",
                        "component:1",
                        "Root/Listener",
                        "BroadcastPostProcessor",
                        recentTracedDeliveryCount: 2,
                        traceIds: new long[] { 101 }
                    ),
                    new FlowGraphTracePath(
                        "FlowGraphMessage",
                        "source: { Id = 42 }",
                        "component:1",
                        "Root/Listener",
                        "BroadcastWithoutSourcePostProcessor",
                        recentTracedDeliveryCount: 3,
                        traceIds: new long[] { 101 }
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            List<VisualElement> routeKindRows = root.Query<VisualElement>(
                    className: DxMessagingFlowGraphWindow.VisibleTraceRouteKindLaneRowClassName
                )
                .ToList();
            Assert.That(routeKindRows.Count, Is.EqualTo(1));
            Assert.That(
                routeKindRows[0]
                    .Q<Label>(
                        DxMessagingFlowGraphWindow.VisibleTraceRouteKindLaneRouteKindLabelName
                    )
                    .ClassListContains(DxMessagingEditorTheme.TypeBadgeClassName),
                Is.True
            );
            Assert.That(
                routeKindRows[0]
                    .Q<Label>(
                        DxMessagingFlowGraphWindow.VisibleTraceRouteKindLaneRouteKindLabelName
                    )
                    .text,
                Is.EqualTo("Broadcast")
            );
            string traceMessageSummary = FirstRow(
                    DxMessagingFlowGraphWindow.VisibleTraceMessageLaneRowClassName
                )
                .Q<Label>(DxMessagingFlowGraphWindow.VisibleTraceMessageLaneSummaryLabelName)
                .text;
            Assert.That(traceMessageSummary, Does.Contain("Route kinds: Broadcast"));
            Assert.That(traceMessageSummary, Does.Not.Contain("BroadcastPostProcessor"));
            Assert.That(
                traceMessageSummary,
                Does.Not.Contain("BroadcastWithoutSourcePostProcessor")
            );

            AssertCompleteBorder(
                FirstRow(DxMessagingFlowGraphWindow.VisibleTraceRouteKindLaneRowClassName),
                DxMessagingEditorPalette.Broadcast
            );
            AssertCompleteBorder(
                FirstRow(DxMessagingFlowGraphWindow.VisibleTraceIdLaneRowClassName),
                DxMessagingEditorPalette.Trace
            );
            AssertCompleteBorder(
                FirstRow(DxMessagingFlowGraphWindow.VisibleTraceMessageLaneRowClassName),
                DxMessagingEditorPalette.TraceMessage
            );
            AssertCompleteBorder(
                FirstRow(DxMessagingFlowGraphWindow.VisibleTraceTargetLaneRowClassName),
                DxMessagingEditorPalette.TraceTarget
            );
            AssertCompleteBorder(
                FirstRow(DxMessagingFlowGraphWindow.VisibleContextLaneRowClassName),
                DxMessagingEditorPalette.Amber
            );

            VisualElement FirstRow(string className)
            {
                return root.Query<VisualElement>(className: className).First();
            }
        }

        [Test]
        public void BuildGraphUiUsesCompleteBordersForRouteAndLaneGroups()
        {
            FlowGraphSnapshot snapshot = new(
                new[]
                {
                    new FlowGraphComponentNode(
                        "component:1",
                        "Root/Listener",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 4,
                        localMessageCount: 0
                    ),
                },
                new[] { new FlowGraphMessageNode("FlowGraphMessage", 1, 4) },
                new[]
                {
                    new FlowGraphEdge(
                        "FlowGraphMessage",
                        "component:1",
                        "Root/Listener",
                        "TargetedWithoutTargeting",
                        registrationCount: 1,
                        callCount: 4
                    ),
                },
                new[]
                {
                    new FlowGraphTracePath(
                        "FlowGraphMessage",
                        "target: { Id = 42 }",
                        "component:1",
                        "Root/Listener",
                        "TargetedWithoutTargeting",
                        recentTracedDeliveryCount: 4,
                        traceIds: new long[] { 101, 102 }
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            AssertCompleteBorder(
                root.Q<VisualElement>(DxMessagingFlowGraphWindow.RouteMapName),
                DxMessagingEditorPalette.BorderPanel
            );
            AssertCompleteBorder(
                root.Q<VisualElement>(DxMessagingFlowGraphWindow.VisibleMessageLanesName),
                DxMessagingEditorPalette.BorderPanel
            );
            AssertCompleteBorder(
                root.Q<VisualElement>(DxMessagingFlowGraphWindow.VisibleTargetLanesName),
                DxMessagingEditorPalette.BorderPanel
            );
            AssertCompleteBorder(
                root.Q<VisualElement>(DxMessagingFlowGraphWindow.VisibleFlowCorridorsName),
                DxMessagingEditorPalette.BorderPanel
            );
            AssertCompleteBorder(
                root.Q<VisualElement>(DxMessagingFlowGraphWindow.VisibleTraceRouteKindLanesName),
                DxMessagingEditorPalette.BorderStrong
            );
            AssertCompleteBorder(
                root.Q<VisualElement>(DxMessagingFlowGraphWindow.VisibleTraceIdLanesName),
                DxMessagingEditorPalette.BorderStrong
            );
            AssertCompleteBorder(
                root.Q<VisualElement>(DxMessagingFlowGraphWindow.VisibleTraceMessageLanesName),
                DxMessagingEditorPalette.BorderStrong
            );
            AssertCompleteBorder(
                root.Q<VisualElement>(DxMessagingFlowGraphWindow.VisibleTraceTargetLanesName),
                DxMessagingEditorPalette.BorderStrong
            );
            AssertCompleteBorder(
                root.Q<VisualElement>(DxMessagingFlowGraphWindow.VisibleContextLanesName),
                DxMessagingEditorPalette.BorderStrong
            );
            AssertCompleteBorder(
                root.Q<VisualElement>(DxMessagingFlowGraphWindow.TracePathsName),
                DxMessagingEditorPalette.BorderPanel
            );
        }

        [Test]
        public void BuildGraphUiFiltersGraphItemsAndKeepsConnectedEdgesVisible()
        {
            FlowGraphSnapshot snapshot = CreateTwoEdgeSnapshot();
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot, new FlowGraphViewState("Beta"));

            Assert.That(
                root.Q<Label>(DxMessagingFlowGraphWindow.StatusLabelName).text,
                Does.Contain("1/2 components")
            );
            Assert.That(
                root.Q<Label>(DxMessagingFlowGraphWindow.StatusLabelName).text,
                Does.Contain("1/2 edges")
            );

            List<VisualElement> components = root.Query<VisualElement>(
                    className: DxMessagingFlowGraphWindow.ComponentNodeClassName
                )
                .ToList();
            List<VisualElement> messages = root.Query<VisualElement>(
                    className: DxMessagingFlowGraphWindow.MessageNodeClassName
                )
                .ToList();
            List<VisualElement> edges = root.Query<VisualElement>(
                    className: DxMessagingFlowGraphWindow.EdgeRowClassName
                )
                .ToList();

            Assert.That(components.Count, Is.EqualTo(1));
            Assert.That(messages.Count, Is.EqualTo(1));
            Assert.That(edges.Count, Is.EqualTo(1));
            Assert.That(
                components[0].Q<Label>(DxMessagingFlowGraphWindow.NodeNameLabelName).text,
                Does.Contain("Root/Beta")
            );
            Assert.That(
                messages[0].Q<Label>(DxMessagingFlowGraphWindow.NodeNameLabelName).text,
                Does.Contain("ScoreChanged")
            );
            Assert.That(
                edges[0].Q<Label>(DxMessagingFlowGraphWindow.EdgeLabelName).text,
                Does.Contain("ScoreChanged -> Root/Beta")
            );
        }

        [Test]
        public void BuildGraphUiRendersSelectionDetailsAndHighlightsFirstComponent()
        {
            FlowGraphSnapshot snapshot = CreateTwoEdgeSnapshot();
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            VisualElement details = root.Q<VisualElement>(
                DxMessagingFlowGraphWindow.DetailsPaneName
            );
            Assert.That(details, Is.Not.Null);
            Assert.That(
                details.Q<Label>(DxMessagingFlowGraphWindow.DetailsTitleLabelName).text,
                Does.Contain("Root/Alpha")
            );
            Assert.That(
                details.Q<Label>(DxMessagingFlowGraphWindow.DetailsBodyLabelName).text,
                Does.Contain("Inbound visible routes: 1 from 1 message types")
            );
            Assert.That(
                details.Q<Label>(DxMessagingFlowGraphWindow.DetailsBodyLabelName).text,
                Does.Contain("Visible call share: 4/6 (67%)")
            );

            List<VisualElement> components = root.Query<VisualElement>(
                    className: DxMessagingFlowGraphWindow.ComponentNodeClassName
                )
                .ToList();
            Assert.That(
                components[0].ClassListContains(DxMessagingFlowGraphWindow.SelectedRowClassName),
                Is.True
            );
            Assert.That(
                components[1].ClassListContains(DxMessagingFlowGraphWindow.SelectedRowClassName),
                Is.False
            );
        }

        [Test]
        public void BuildGraphUiRendersMessageSelectionPathInsight()
        {
            FlowGraphSnapshot snapshot = CreateTwoEdgeSnapshot();
            FlowGraphMessageNode message = snapshot.MessageNodes[1];
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(
                root,
                snapshot,
                new FlowGraphViewState(
                    selectedItemKey: DxMessagingFlowGraphWindow.CreateMessageSelectionKey(message)
                )
            );

            VisualElement details = root.Q<VisualElement>(
                DxMessagingFlowGraphWindow.DetailsPaneName
            );

            Assert.That(
                details.Q<Label>(DxMessagingFlowGraphWindow.DetailsTitleLabelName).text,
                Does.Contain("ScoreChanged")
            );
            Assert.That(
                details.Q<Label>(DxMessagingFlowGraphWindow.DetailsBodyLabelName).text,
                Does.Contain("Listener components: 1")
            );
            Assert.That(
                details.Q<Label>(DxMessagingFlowGraphWindow.DetailsBodyLabelName).text,
                Does.Contain("Busiest listener: Root/Beta (2 calls)")
            );
        }

        [Test]
        public void BuildGraphUiRendersMessageRecentDiagnosticsEvidence()
        {
            FlowGraphSnapshot snapshot = new(
                new[]
                {
                    new FlowGraphComponentNode(
                        "component:beta",
                        "Root/Beta",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 2,
                        localMessageCount: 3
                    ),
                },
                new[]
                {
                    new FlowGraphMessageNode(
                        "ScoreChanged",
                        registrationCount: 1,
                        callCount: 2,
                        recentGlobalEmissionCount: 5,
                        recentLocalMessageCount: 3,
                        recentTracedDeliveryCount: 2
                    ),
                },
                new[]
                {
                    new FlowGraphEdge(
                        "ScoreChanged",
                        "component:beta",
                        "Root/Beta",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 2,
                        recentTracedDeliveryCount: 2
                    ),
                },
                Array.Empty<string>()
            );
            FlowGraphMessageNode message = snapshot.MessageNodes[0];
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(
                root,
                snapshot,
                new FlowGraphViewState(
                    selectedItemKey: DxMessagingFlowGraphWindow.CreateMessageSelectionKey(message)
                )
            );

            string summary = root.Query<VisualElement>(
                    className: DxMessagingFlowGraphWindow.MessageNodeClassName
                )
                .ToList()[0]
                .Q<Label>(DxMessagingFlowGraphWindow.NodeSummaryLabelName)
                .text;
            string details = root.Q<Label>(DxMessagingFlowGraphWindow.DetailsBodyLabelName).text;
            string exportText = DxMessagingFlowGraphWindow.CreateExportText(snapshot);
            FlowGraphExportPayload exportPayload = JsonUtility.FromJson<FlowGraphExportPayload>(
                exportText
            );

            Assert.That(summary, Does.Contain("Recent: 5 global / 3 listener"));
            Assert.That(summary, Does.Contain("Traced deliveries: 2"));
            Assert.That(
                details,
                Does.Contain("Recent diagnostics: 5 global emissions | 3 listener messages")
            );
            Assert.That(details, Does.Contain("Traced deliveries: 2"));
            Assert.That(exportPayload.schemaVersion, Is.EqualTo(5));
            Assert.That(
                exportPayload.captureMode,
                Is.EqualTo("registration-topology-with-recent-diagnostics")
            );
            Assert.That(
                exportPayload.traceSemantics,
                Does.Contain("built from token delivery records")
            );
            Assert.That(exportText, Does.Contain("\"recentGlobalEmissionCount\": 5"));
            Assert.That(exportText, Does.Contain("\"recentLocalMessageCount\": 3"));
            Assert.That(exportText, Does.Contain("\"recentTracedDeliveryCount\": 2"));
            Assert.That(exportPayload.messageCount, Is.EqualTo(1));
            Assert.That(exportPayload.messages, Has.Length.EqualTo(1));
            Assert.That(exportPayload.messages[0].recentGlobalEmissionCount, Is.EqualTo(5));
            Assert.That(exportPayload.messages[0].recentLocalMessageCount, Is.EqualTo(3));
            Assert.That(exportPayload.messages[0].recentTracedDeliveryCount, Is.EqualTo(2));
        }

        [Test]
        public void BuildGraphUiRendersRecentTracePathsAndExportsThem()
        {
            FlowGraphSnapshot snapshot = new(
                new[]
                {
                    new FlowGraphComponentNode(
                        "component:beta",
                        "Root/Beta",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 2,
                        localMessageCount: 2
                    ),
                },
                new[]
                {
                    new FlowGraphMessageNode(
                        "ScoreChanged",
                        registrationCount: 1,
                        callCount: 2,
                        recentGlobalEmissionCount: 2,
                        recentLocalMessageCount: 2,
                        recentTracedDeliveryCount: 2
                    ),
                },
                new[]
                {
                    new FlowGraphEdge(
                        "ScoreChanged",
                        "component:beta",
                        "Root/Beta",
                        "Broadcast",
                        registrationCount: 1,
                        callCount: 2,
                        recentTracedDeliveryCount: 2
                    ),
                },
                new[]
                {
                    new FlowGraphTracePath(
                        "ScoreChanged",
                        "source: { Id = 42 }",
                        "component:beta",
                        "Root/Beta",
                        "Broadcast",
                        recentTracedDeliveryCount: 2
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot, new FlowGraphViewState("42"));

            VisualElement tracePaths = root.Q<VisualElement>(
                DxMessagingFlowGraphWindow.TracePathsName
            );
            List<VisualElement> traceRows = tracePaths
                .Query<VisualElement>(className: DxMessagingFlowGraphWindow.TracePathRowClassName)
                .ToList();
            List<VisualElement> edgeRows = root.Query<VisualElement>(
                    className: DxMessagingFlowGraphWindow.EdgeRowClassName
                )
                .ToList();
            string exportText = DxMessagingFlowGraphWindow.CreateExportText(snapshot, "42");
            FlowGraphExportPayload exportPayload = JsonUtility.FromJson<FlowGraphExportPayload>(
                exportText
            );

            Assert.That(tracePaths, Is.Not.Null);
            Assert.That(traceRows.Count, Is.EqualTo(1));
            Assert.That(edgeRows.Count, Is.EqualTo(1));
            Assert.That(
                traceRows[0].Q<Label>(DxMessagingFlowGraphWindow.TracePathMessageLabelName).text,
                Does.Contain("ScoreChanged")
            );
            Assert.That(
                traceRows[0].Q<Label>(DxMessagingFlowGraphWindow.TracePathSummaryLabelName).text,
                Does.Contain("Context: source: { Id = 42 }")
            );
            Assert.That(
                traceRows[0].Q<Label>(DxMessagingFlowGraphWindow.TracePathSummaryLabelName).text,
                Does.Contain("Deliveries: 2")
            );
            Assert.That(
                traceRows[0].Q<Label>(DxMessagingFlowGraphWindow.TracePathTargetLabelName).text,
                Does.Contain("Root/Beta")
            );
            Assert.That(exportPayload.schemaVersion, Is.EqualTo(5));
            Assert.That(exportPayload.tracePathCount, Is.EqualTo(1));
            Assert.That(exportPayload.tracePaths, Has.Length.EqualTo(1));
            Assert.That(exportPayload.tracePaths[0].messageType, Is.EqualTo("ScoreChanged"));
            Assert.That(exportPayload.tracePaths[0].context, Is.EqualTo("source: { Id = 42 }"));
            Assert.That(exportPayload.tracePaths[0].targetComponentPath, Is.EqualTo("Root/Beta"));
            Assert.That(exportPayload.tracePaths[0].registrationType, Is.EqualTo("Broadcast"));
            Assert.That(exportPayload.tracePaths[0].recentTracedDeliveryCount, Is.EqualTo(2));
        }

        [Test]
        public void BuildGraphUiRendersTracePathRowNormalizesEmptyContext()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "ScoreChanged",
                        string.Empty,
                        "component:beta",
                        "Root/Beta",
                        "Broadcast",
                        recentTracedDeliveryCount: 2
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            VisualElement tracePaths = root.Q<VisualElement>(
                DxMessagingFlowGraphWindow.TracePathsName
            );
            VisualElement row = tracePaths
                .Query<VisualElement>(className: DxMessagingFlowGraphWindow.TracePathRowClassName)
                .First();
            string rowSummary = row.Q<Label>(
                DxMessagingFlowGraphWindow.TracePathSummaryLabelName
            ).text;
            string exportText = DxMessagingFlowGraphWindow.CreateExportText(snapshot);
            FlowGraphExportPayload exportPayload = JsonUtility.FromJson<FlowGraphExportPayload>(
                exportText
            );

            Assert.That(rowSummary, Does.Contain("Context: <none>"));
            Assert.That(rowSummary, Does.Not.Contain("Context:  |"));
            Assert.That(exportPayload.tracePaths[0].context, Is.EqualTo(string.Empty));
            Assert.That(exportText, Does.Not.Contain("\"context\": \"<none>\""));
        }

        [Test]
        public void BuildGraphUiFiltersTracePathRowsByNormalizedEmptyContext()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "ScoreChanged",
                        string.Empty,
                        "component:beta",
                        "Root/Beta",
                        "Broadcast",
                        recentTracedDeliveryCount: 2
                    ),
                    new FlowGraphTracePath(
                        "ScoreChanged",
                        "source: { Id = 42 }",
                        "component:gamma",
                        "Root/Gamma",
                        "Broadcast",
                        recentTracedDeliveryCount: 3
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot, new FlowGraphViewState("none"));

            VisualElement tracePaths = root.Q<VisualElement>(
                DxMessagingFlowGraphWindow.TracePathsName
            );
            List<VisualElement> rows = tracePaths
                .Query<VisualElement>(className: DxMessagingFlowGraphWindow.TracePathRowClassName)
                .ToList();
            string exportText = DxMessagingFlowGraphWindow.CreateExportText(snapshot, "none");
            FlowGraphExportPayload exportPayload = JsonUtility.FromJson<FlowGraphExportPayload>(
                exportText
            );

            Assert.That(rows.Count, Is.EqualTo(1));
            Assert.That(
                rows[0].Q<Label>(DxMessagingFlowGraphWindow.TracePathSummaryLabelName).text,
                Does.Contain("Context: <none>")
            );
            Assert.That(exportPayload.tracePathCount, Is.EqualTo(1));
            Assert.That(exportPayload.tracePaths[0].context, Is.EqualTo(string.Empty));
            Assert.That(exportText, Does.Not.Contain("Root/Gamma"));
        }

        [Test]
        public void BuildGraphUiRendersTracePathTraceIdCountsAndExportsThem()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "ScoreChanged",
                        "source: { Id = 42 }",
                        "component:beta",
                        "Root/Beta",
                        "Broadcast",
                        recentTracedDeliveryCount: 3,
                        traceIds: new long[] { 102, 101, 102 }
                    ),
                    new FlowGraphTracePath(
                        "InventoryChanged",
                        "source: { Id = 7 }",
                        "component:gamma",
                        "Root/Gamma",
                        "Broadcast",
                        recentTracedDeliveryCount: 5,
                        traceIds: new long[] { 201 }
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot, new FlowGraphViewState("101"));

            VisualElement tracePaths = root.Q<VisualElement>(
                DxMessagingFlowGraphWindow.TracePathsName
            );
            List<VisualElement> rows = tracePaths
                .Query<VisualElement>(className: DxMessagingFlowGraphWindow.TracePathRowClassName)
                .ToList();
            string summary = tracePaths
                .Q<Label>(DxMessagingFlowGraphWindow.TracePathsSummaryLabelName)
                .text;
            string rowSummary = rows[0]
                .Q<Label>(DxMessagingFlowGraphWindow.TracePathSummaryLabelName)
                .text;
            string exportText = DxMessagingFlowGraphWindow.CreateExportText(snapshot, "101");
            FlowGraphExportPayload exportPayload = JsonUtility.FromJson<FlowGraphExportPayload>(
                exportText
            );

            Assert.That(rows.Count, Is.EqualTo(1));
            Assert.That(summary, Does.Contain("Trace ids: 2"));
            Assert.That(summary, Does.Not.Contain("Trace ids: 3"));
            Assert.That(rowSummary, Does.Contain("Trace ids: 2"));
            Assert.That(exportPayload.schemaVersion, Is.EqualTo(5));
            Assert.That(exportPayload.tracePathCount, Is.EqualTo(1));
            Assert.That(exportPayload.tracePaths[0].recentTraceIdCount, Is.EqualTo(2));
            Assert.That(exportText, Does.Contain("Root/Beta"));
            Assert.That(exportText, Does.Not.Contain("Root/Gamma"));
        }

        [Test]
        public void BuildGraphUiRendersVisibleMessageLanesFromVisibleEdges()
        {
            FlowGraphSnapshot snapshot = new(
                new[]
                {
                    new FlowGraphComponentNode(
                        "component:alpha",
                        "Root/Alpha",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 4,
                        localMessageCount: 1
                    ),
                    new FlowGraphComponentNode(
                        "component:beta",
                        "Root/Beta",
                        "MessagingComponent",
                        activeInHierarchy: false,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 3,
                        localMessageCount: 0
                    ),
                    new FlowGraphComponentNode(
                        "component:gamma",
                        "Root/Gamma",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 2,
                        localMessageCount: 0
                    ),
                },
                new[]
                {
                    new FlowGraphMessageNode("InventoryChanged", 3, 7),
                    new FlowGraphMessageNode("ScoreChanged", 1, 2),
                },
                new[]
                {
                    new FlowGraphEdge(
                        "InventoryChanged",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        registrationCount: 1,
                        callCount: 4,
                        recentTracedDeliveryCount: 4
                    ),
                    new FlowGraphEdge(
                        "InventoryChanged",
                        "component:beta",
                        "Root/Beta",
                        "Targeted",
                        registrationCount: 2,
                        callCount: 3,
                        recentTracedDeliveryCount: 1
                    ),
                    new FlowGraphEdge(
                        "ScoreChanged",
                        "component:gamma",
                        "Root/Gamma",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 2,
                        recentTracedDeliveryCount: 0
                    ),
                    new FlowGraphEdge(
                        "ScoreChanged",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        registrationCount: 1,
                        callCount: 0,
                        recentTracedDeliveryCount: 0
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            VisualElement messageLanes = root.Q<VisualElement>(MessageLanesName);
            Assert.That(messageLanes, Is.Not.Null);

            string summary = messageLanes.Q<Label>(MessageLanesSummaryLabelName).text;
            List<VisualElement> rows = messageLanes
                .Query<VisualElement>(className: MessageLaneRowClassName)
                .ToList();

            Assert.That(summary, Does.Contain("2 message lanes"));
            Assert.That(summary, Does.Contain("Routes: 4"));
            Assert.That(summary, Does.Contain("Targets: 3"));
            Assert.That(summary, Does.Contain("Calls: 9"));
            Assert.That(summary, Does.Contain("Recent traced: 5"));
            Assert.That(summary, Does.Contain("No-call routes: 1"));
            Assert.That(summary, Does.Contain("Busiest lane: InventoryChanged | Share: 7/9 (78%)"));
            Assert.That(rows.Count, Is.EqualTo(2));
            Assert.That(
                rows[0].Q<Label>(MessageLaneMessageLabelName).text,
                Is.EqualTo("InventoryChanged")
            );
            Assert.That(
                rows[0].Q<Label>(MessageLaneSummaryLabelName).text,
                Does.Contain(
                    "Routes: 2 | Targets: 2 | Registrations: 3 | Calls: 7 | Recent traced: 5 | No-call routes: 0 | Route kinds: Broadcast, Targeted | Share: 7/9 (78%)"
                )
            );
            Assert.That(
                rows[0].Q<Label>(MessageLaneTargetsLabelName).text,
                Does.Contain("Targets: Root/Alpha, Root/Beta | Inactive: 1/2")
            );
        }

        [Test]
        public void BuildGraphUiScopesVisibleMessageLanesToFilteredEdges()
        {
            FlowGraphSnapshot snapshot = CreateSharedMessageSnapshot();
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot, new FlowGraphViewState("Beta"));

            VisualElement messageLanes = root.Q<VisualElement>(MessageLanesName);
            string summary = messageLanes.Q<Label>(MessageLanesSummaryLabelName).text;
            List<VisualElement> rows = messageLanes
                .Query<VisualElement>(className: MessageLaneRowClassName)
                .ToList();

            Assert.That(summary, Does.Contain("1 message lane"));
            Assert.That(summary, Does.Contain("Routes: 1"));
            Assert.That(summary, Does.Contain("Targets: 1"));
            Assert.That(summary, Does.Contain("Calls: 2"));
            Assert.That(rows.Count, Is.EqualTo(1));
            Assert.That(
                rows[0].Q<Label>(MessageLaneTargetsLabelName).text,
                Does.Contain("Targets: Root/Beta")
            );
            Assert.That(
                rows[0].Q<Label>(MessageLaneTargetsLabelName).text,
                Does.Not.Contain("Root/Alpha")
            );
        }

        [Test]
        public void BuildGraphUiRendersVisibleMessageLanesWithDeterministicTieBreakers()
        {
            string summary = RenderVisibleMessageLanesSummary(
                new FlowGraphEdge(
                    "BetaMessage",
                    "component:beta",
                    "Root/Beta",
                    "Broadcast",
                    registrationCount: 1,
                    callCount: 3,
                    recentTracedDeliveryCount: 1
                ),
                new FlowGraphEdge(
                    "AlphaMessage",
                    "component:alpha",
                    "Root/Alpha",
                    "Broadcast",
                    registrationCount: 1,
                    callCount: 3,
                    recentTracedDeliveryCount: 1
                )
            );

            Assert.That(summary, Does.Contain("Busiest lane: AlphaMessage | Share: 3/6 (50%)"));
        }

        [Test]
        public void BuildGraphUiRendersVisibleMessageLaneZeroCallShareAsNotAvailable()
        {
            FlowGraphSnapshot snapshot = new(
                new[]
                {
                    new FlowGraphComponentNode(
                        "component:alpha",
                        "Root/Alpha",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 0,
                        localMessageCount: 0
                    ),
                    new FlowGraphComponentNode(
                        "component:beta",
                        "Root/Beta",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 0,
                        localMessageCount: 0
                    ),
                },
                new[] { new FlowGraphMessageNode("IdleMessage", 2, 0) },
                new[]
                {
                    new FlowGraphEdge(
                        "IdleMessage",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        registrationCount: 1,
                        callCount: 0
                    ),
                    new FlowGraphEdge(
                        "IdleMessage",
                        "component:beta",
                        "Root/Beta",
                        "Targeted",
                        registrationCount: 1,
                        callCount: 0
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            VisualElement row = root.Q<VisualElement>(MessageLanesName)
                .Query<VisualElement>(className: MessageLaneRowClassName)
                .First();
            string summary = row.Q<Label>(MessageLaneSummaryLabelName).text;

            Assert.That(summary, Does.Contain("No-call routes: 2"));
            Assert.That(summary, Does.Contain("Share: 0/0 (n/a)"));
        }

        [Test]
        public void CreateExportTextDoesNotExportVisibleMessageLaneAggregates()
        {
            FlowGraphSnapshot snapshot = CreateTwoEdgeSnapshot();

            string exportText = DxMessagingFlowGraphWindow.CreateExportText(snapshot);
            FlowGraphExportPayload exportPayload = JsonUtility.FromJson<FlowGraphExportPayload>(
                exportText
            );

            Assert.That(exportPayload.schemaVersion, Is.EqualTo(5));
            Assert.That(exportText, Does.Not.Contain("messageLanes"));
            Assert.That(exportText, Does.Not.Contain("visibleMessageLanes"));
        }

        [Test]
        public void BuildGraphUiRendersVisibleTargetLanesFromVisibleEdges()
        {
            FlowGraphSnapshot snapshot = new(
                new[]
                {
                    new FlowGraphComponentNode(
                        "component:alpha",
                        "Root/Alpha",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 2,
                        callCount: 4,
                        localMessageCount: 1
                    ),
                    new FlowGraphComponentNode(
                        "component:beta",
                        "Root/Beta",
                        "MessagingComponent",
                        activeInHierarchy: false,
                        listenerCount: 1,
                        registrationCount: 2,
                        callCount: 3,
                        localMessageCount: 0
                    ),
                    new FlowGraphComponentNode(
                        "component:gamma",
                        "Root/Gamma",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 2,
                        localMessageCount: 0
                    ),
                },
                new[]
                {
                    new FlowGraphMessageNode("InventoryChanged", 2, 7),
                    new FlowGraphMessageNode("ScoreChanged", 2, 2),
                },
                new[]
                {
                    new FlowGraphEdge(
                        "InventoryChanged",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        registrationCount: 1,
                        callCount: 4,
                        recentTracedDeliveryCount: 4
                    ),
                    new FlowGraphEdge(
                        "ScoreChanged",
                        "component:alpha",
                        "Root/Alpha",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 0,
                        recentTracedDeliveryCount: 0
                    ),
                    new FlowGraphEdge(
                        "InventoryChanged",
                        "component:beta",
                        "Root/Beta",
                        "Targeted",
                        registrationCount: 2,
                        callCount: 3,
                        recentTracedDeliveryCount: 1
                    ),
                    new FlowGraphEdge(
                        "ScoreChanged",
                        "component:gamma",
                        "Root/Gamma",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 2,
                        recentTracedDeliveryCount: 0
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            VisualElement targetLanes = root.Q<VisualElement>(TargetLanesName);
            Assert.That(targetLanes, Is.Not.Null);

            string summary = targetLanes.Q<Label>(TargetLanesSummaryLabelName).text;
            List<VisualElement> rows = targetLanes
                .Query<VisualElement>(className: TargetLaneRowClassName)
                .ToList();

            Assert.That(summary, Does.Contain("3 target lanes"));
            Assert.That(summary, Does.Contain("Routes: 4"));
            Assert.That(summary, Does.Contain("Messages: 2"));
            Assert.That(summary, Does.Contain("Calls: 9"));
            Assert.That(summary, Does.Contain("Recent traced: 5"));
            Assert.That(summary, Does.Contain("No-call routes: 1"));
            Assert.That(summary, Does.Contain("Busiest target: Root/Alpha | Share: 4/9 (44%)"));
            Assert.That(rows.Count, Is.EqualTo(3));
            Assert.That(rows[0].Q<Label>(TargetLaneTargetLabelName).text, Is.EqualTo("Root/Alpha"));
            Assert.That(
                rows[0].Q<Label>(TargetLaneSummaryLabelName).text,
                Does.Contain(
                    "State: active | Routes: 2 | Messages: 2 | Registrations: 2 | Calls: 4 | Recent traced: 4 | No-call routes: 1 | Route kinds: Broadcast, Untargeted | Share: 4/9 (44%)"
                )
            );
            Assert.That(
                rows[0].Q<Label>(TargetLaneMessagesLabelName).text,
                Does.Contain("Messages: InventoryChanged, ScoreChanged")
            );
        }

        [Test]
        public void BuildGraphUiScopesVisibleTargetLanesToFilteredEdges()
        {
            FlowGraphSnapshot snapshot = CreateTwoEdgeSnapshot();
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot, new FlowGraphViewState("Beta"));

            VisualElement targetLanes = root.Q<VisualElement>(TargetLanesName);
            string summary = targetLanes.Q<Label>(TargetLanesSummaryLabelName).text;
            List<VisualElement> rows = targetLanes
                .Query<VisualElement>(className: TargetLaneRowClassName)
                .ToList();

            Assert.That(summary, Does.Contain("1 target lane"));
            Assert.That(summary, Does.Contain("Routes: 1"));
            Assert.That(summary, Does.Contain("Messages: 1"));
            Assert.That(summary, Does.Contain("Calls: 2"));
            Assert.That(rows.Count, Is.EqualTo(1));
            Assert.That(rows[0].Q<Label>(TargetLaneTargetLabelName).text, Is.EqualTo("Root/Beta"));
            Assert.That(
                rows[0].Q<Label>(TargetLaneMessagesLabelName).text,
                Does.Not.Contain("InventoryChanged")
            );
        }

        [Test]
        public void BuildGraphUiRendersVisibleTargetLanesWithDeterministicTieBreakers()
        {
            string summary = RenderVisibleTargetLanesSummary(
                new FlowGraphEdge(
                    "BetaMessage",
                    "component:beta",
                    "Root/Beta",
                    "Broadcast",
                    registrationCount: 1,
                    callCount: 3,
                    recentTracedDeliveryCount: 1
                ),
                new FlowGraphEdge(
                    "AlphaMessage",
                    "component:alpha",
                    "Root/Alpha",
                    "Broadcast",
                    registrationCount: 1,
                    callCount: 3,
                    recentTracedDeliveryCount: 1
                )
            );

            Assert.That(summary, Does.Contain("Busiest target: Root/Alpha | Share: 3/6 (50%)"));
        }

        [Test]
        public void BuildGraphUiRendersVisibleTargetLaneZeroCallShareAsNotAvailable()
        {
            FlowGraphSnapshot snapshot = new(
                new[]
                {
                    new FlowGraphComponentNode(
                        "component:alpha",
                        "Root/Alpha",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 2,
                        callCount: 0,
                        localMessageCount: 0
                    ),
                },
                new[] { new FlowGraphMessageNode("IdleMessage", 2, 0) },
                new[]
                {
                    new FlowGraphEdge(
                        "IdleMessage",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        registrationCount: 1,
                        callCount: 0
                    ),
                    new FlowGraphEdge(
                        "IdleMessage",
                        "component:alpha",
                        "Root/Alpha",
                        "Targeted",
                        registrationCount: 1,
                        callCount: 0
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            VisualElement row = root.Q<VisualElement>(TargetLanesName)
                .Query<VisualElement>(className: TargetLaneRowClassName)
                .First();
            string summary = row.Q<Label>(TargetLaneSummaryLabelName).text;

            Assert.That(summary, Does.Contain("No-call routes: 2"));
            Assert.That(summary, Does.Contain("Share: 0/0 (n/a)"));
        }

        [Test]
        public void BuildGraphUiKeepsVisibleTargetLanesSplitByDuplicateTargetPathIds()
        {
            FlowGraphSnapshot snapshot = new(
                new[]
                {
                    new FlowGraphComponentNode(
                        "component:first",
                        "Root/Duplicate",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 2,
                        localMessageCount: 0
                    ),
                    new FlowGraphComponentNode(
                        "component:second",
                        "Root/Duplicate",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 2,
                        localMessageCount: 0
                    ),
                },
                new[] { new FlowGraphMessageNode("SharedMessage", 2, 4) },
                new[]
                {
                    new FlowGraphEdge(
                        "SharedMessage",
                        "component:first",
                        "Root/Duplicate",
                        "Broadcast",
                        registrationCount: 1,
                        callCount: 2
                    ),
                    new FlowGraphEdge(
                        "SharedMessage",
                        "component:second",
                        "Root/Duplicate",
                        "Broadcast",
                        registrationCount: 1,
                        callCount: 2
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            List<VisualElement> rows = root.Q<VisualElement>(TargetLanesName)
                .Query<VisualElement>(className: TargetLaneRowClassName)
                .ToList();

            Assert.That(rows.Count, Is.EqualTo(2));
            Assert.That(
                rows[0].Q<Label>(TargetLaneSummaryLabelName).text,
                Does.Contain("Target id: component:first")
            );
            Assert.That(
                rows[1].Q<Label>(TargetLaneSummaryLabelName).text,
                Does.Contain("Target id: component:second")
            );
        }

        [Test]
        public void CreateExportTextDoesNotExportVisibleTargetLaneAggregates()
        {
            FlowGraphSnapshot snapshot = CreateTwoEdgeSnapshot();

            string exportText = DxMessagingFlowGraphWindow.CreateExportText(snapshot);
            FlowGraphExportPayload exportPayload = JsonUtility.FromJson<FlowGraphExportPayload>(
                exportText
            );

            Assert.That(exportPayload.schemaVersion, Is.EqualTo(5));
            Assert.That(exportText, Does.Not.Contain("targetLanes"));
            Assert.That(exportText, Does.Not.Contain("visibleTargetLanes"));
        }

        [Test]
        public void BuildGraphUiRendersVisibleFlowCorridorsFromVisibleTracePaths()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "AlphaMessage",
                        "visible context a",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        recentTracedDeliveryCount: 2,
                        traceIds: new long[] { 101 }
                    ),
                    new FlowGraphTracePath(
                        "AlphaMessage",
                        "visible context b",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        recentTracedDeliveryCount: 3,
                        traceIds: new long[] { 101, 102 }
                    ),
                    new FlowGraphTracePath(
                        "BetaMessage",
                        "visible context c",
                        "component:beta",
                        "Root/Beta",
                        "Untargeted",
                        recentTracedDeliveryCount: 2,
                        traceIds: new long[] { 201 }
                    ),
                    new FlowGraphTracePath(
                        "GammaMessage",
                        "hidden context",
                        "component:gamma",
                        "Root/Gamma",
                        "Targeted",
                        recentTracedDeliveryCount: 9,
                        traceIds: new long[] { 301 }
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(
                root,
                snapshot,
                new FlowGraphViewState("visible")
            );

            VisualElement corridors = root.Q<VisualElement>(
                DxMessagingFlowGraphWindow.VisibleFlowCorridorsName
            );
            Assert.That(corridors, Is.Not.Null);

            string summary = corridors
                .Q<Label>(DxMessagingFlowGraphWindow.VisibleFlowCorridorsSummaryLabelName)
                .text;
            List<VisualElement> rows = corridors
                .Query<VisualElement>(
                    className: DxMessagingFlowGraphWindow.VisibleFlowCorridorRowClassName
                )
                .ToList();

            Assert.That(summary, Does.Contain("2 visible corridors"));
            Assert.That(summary, Does.Contain("Deliveries: 7"));
            Assert.That(
                summary,
                Does.Contain("Busiest corridor: AlphaMessage -> Root/Alpha | Share: 5/7 (71%)")
            );
            Assert.That(rows.Count, Is.EqualTo(2));
            Assert.That(
                rows[0]
                    .Q<Label>(DxMessagingFlowGraphWindow.VisibleFlowCorridorMessageLabelName)
                    .text,
                Is.EqualTo("AlphaMessage")
            );
            Assert.That(
                rows[0]
                    .Q<Label>(DxMessagingFlowGraphWindow.VisibleFlowCorridorSummaryLabelName)
                    .text,
                Does.Contain(
                    "Paths: 2 | Contexts: 2 | Trace ids: 2 | Route kinds: Broadcast | Deliveries: 5 | Share: 5/7 (71%)"
                )
            );
            Assert.That(
                rows[0]
                    .Q<Label>(DxMessagingFlowGraphWindow.VisibleFlowCorridorTargetLabelName)
                    .text,
                Is.EqualTo("Root/Alpha")
            );
            Assert.That(summary, Does.Not.Contain("GammaMessage"));
        }

        [Test]
        public void BuildGraphUiRendersVisibleFlowCorridorsWithDeterministicTieBreakers()
        {
            string summary = RenderVisibleFlowCorridorsSummary(
                new FlowGraphTracePath(
                    "BetaMessage",
                    "source: { Id = 9 }",
                    "component:beta",
                    "Root/Beta",
                    "Broadcast",
                    recentTracedDeliveryCount: 3
                ),
                new FlowGraphTracePath(
                    "AlphaMessage",
                    "source: { Id = 7 }",
                    "component:alpha",
                    "Root/Alpha",
                    "Broadcast",
                    recentTracedDeliveryCount: 3
                )
            );

            Assert.That(
                summary,
                Does.Contain("Busiest corridor: AlphaMessage -> Root/Alpha | Share: 3/6 (50%)")
            );
        }

        [Test]
        public void BuildGraphUiScopesVisibleFlowCorridorsToFilteredTargetTracePaths()
        {
            FlowGraphSnapshot snapshot = new(
                new[]
                {
                    new FlowGraphComponentNode(
                        "component:beta",
                        "Root/Beta",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 3,
                        localMessageCount: 3
                    ),
                    new FlowGraphComponentNode(
                        "component:gamma",
                        "Root/Gamma",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 9,
                        localMessageCount: 9
                    ),
                },
                new[] { new FlowGraphMessageNode("ScoreChanged", 2, 12) },
                new[]
                {
                    new FlowGraphEdge(
                        "ScoreChanged",
                        "component:beta",
                        "Root/Beta",
                        "Broadcast",
                        registrationCount: 1,
                        callCount: 3,
                        recentTracedDeliveryCount: 3
                    ),
                    new FlowGraphEdge(
                        "ScoreChanged",
                        "component:gamma",
                        "Root/Gamma",
                        "Broadcast",
                        registrationCount: 1,
                        callCount: 9,
                        recentTracedDeliveryCount: 9
                    ),
                },
                new[]
                {
                    new FlowGraphTracePath(
                        "ScoreChanged",
                        "source: { Id = 42 }",
                        "component:beta",
                        "Root/Beta",
                        "Broadcast",
                        recentTracedDeliveryCount: 3,
                        traceIds: new long[] { 101 }
                    ),
                    new FlowGraphTracePath(
                        "ScoreChanged",
                        "source: { Id = 99 }",
                        "component:gamma",
                        "Root/Gamma",
                        "Broadcast",
                        recentTracedDeliveryCount: 9,
                        traceIds: new long[] { 201 }
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot, new FlowGraphViewState("Beta"));

            VisualElement corridors = root.Q<VisualElement>(
                DxMessagingFlowGraphWindow.VisibleFlowCorridorsName
            );
            string summary = corridors
                .Q<Label>(DxMessagingFlowGraphWindow.VisibleFlowCorridorsSummaryLabelName)
                .text;
            string exportText = DxMessagingFlowGraphWindow.CreateExportText(snapshot, "Beta");

            Assert.That(summary, Does.Contain("1 visible corridor"));
            Assert.That(
                summary,
                Does.Contain("Busiest corridor: ScoreChanged -> Root/Beta | Share: 3/3 (100%)")
            );
            Assert.That(summary, Does.Not.Contain("Root/Gamma"));
            Assert.That(exportText, Does.Contain("Root/Beta"));
            Assert.That(exportText, Does.Not.Contain("Root/Gamma"));
        }

        [Test]
        public void BuildGraphUiKeepsGlobalAcceptAllRouteForTracePathOnlyFilter()
        {
            FlowGraphSnapshot snapshot = new(
                new[]
                {
                    new FlowGraphComponentNode(
                        "component:beta",
                        "Root/Beta",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 1,
                        localMessageCount: 1
                    ),
                },
                new[]
                {
                    new FlowGraphMessageNode("ConcreteMessage", 0, 0, recentTracedDeliveryCount: 1),
                    new FlowGraphMessageNode("DxMessaging.Core.IMessage", 1, 1),
                },
                new[]
                {
                    new FlowGraphEdge(
                        "DxMessaging.Core.IMessage",
                        "component:beta",
                        "Root/Beta",
                        "GlobalAcceptAll",
                        registrationCount: 1,
                        callCount: 1,
                        recentTracedDeliveryCount: 1
                    ),
                },
                new[]
                {
                    new FlowGraphTracePath(
                        "ConcreteMessage",
                        "source: { Id = 42 }",
                        "component:beta",
                        "Root/Beta",
                        "GlobalAcceptAll",
                        recentTracedDeliveryCount: 1,
                        traceIds: new long[] { 4242 }
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot, new FlowGraphViewState("4242"));

            string routeMapSummary = root.Q<VisualElement>(DxMessagingFlowGraphWindow.RouteMapName)
                .Q<Label>(DxMessagingFlowGraphWindow.RouteMapSummaryLabelName)
                .text;
            string exportText = DxMessagingFlowGraphWindow.CreateExportText(snapshot, "4242");

            Assert.That(routeMapSummary, Does.Contain("1 visible route"));
            Assert.That(routeMapSummary, Does.Contain("GlobalAcceptAll 1"));
            Assert.That(exportText, Does.Contain("GlobalAcceptAll"));
            Assert.That(exportText, Does.Contain("ConcreteMessage"));
        }

        [Test]
        public void CreateExportTextDoesNotExportVisibleFlowCorridorAggregates()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "AlphaMessage",
                        "source: { Id = 7 }",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        recentTracedDeliveryCount: 3,
                        traceIds: new long[] { 101 }
                    ),
                },
                Array.Empty<string>()
            );

            string exportText = DxMessagingFlowGraphWindow.CreateExportText(snapshot);
            FlowGraphExportPayload exportPayload = JsonUtility.FromJson<FlowGraphExportPayload>(
                exportText
            );

            Assert.That(exportPayload.schemaVersion, Is.EqualTo(5));
            Assert.That(exportText, Does.Not.Contain("flowCorridors"));
            Assert.That(exportText, Does.Not.Contain("visibleCorridors"));
        }

        [Test]
        public void BuildGraphUiRendersVisibleContextLanesFromVisibleTracePaths()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "AlphaMessage",
                        "source: { Id = 42 }",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        recentTracedDeliveryCount: 2,
                        traceIds: new long[] { 101, 102 }
                    ),
                    new FlowGraphTracePath(
                        "BetaMessage",
                        "source: { Id = 42 }",
                        "component:beta",
                        "Root/Beta",
                        "Untargeted",
                        recentTracedDeliveryCount: 3,
                        traceIds: new long[] { 102, 103 }
                    ),
                    new FlowGraphTracePath(
                        "GammaMessage",
                        "source: { Id = 7 }",
                        "component:gamma",
                        "Root/Gamma",
                        "Targeted",
                        recentTracedDeliveryCount: 1,
                        traceIds: new long[] { 201 }
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            VisualElement contextLanes = root.Q<VisualElement>(ContextLanesName);
            Assert.That(contextLanes, Is.Not.Null);

            string summary = contextLanes.Q<Label>(ContextLanesSummaryLabelName).text;
            List<VisualElement> rows = contextLanes
                .Query<VisualElement>(className: ContextLaneRowClassName)
                .ToList();

            Assert.That(summary, Does.Contain("2 context lanes"));
            Assert.That(summary, Does.Contain("Deliveries: 6"));
            Assert.That(summary, Does.Contain("Trace ids: 4"));
            Assert.That(
                summary,
                Does.Contain("Busiest context: source: { Id = 42 } | Share: 5/6 (83%)")
            );
            Assert.That(rows.Count, Is.EqualTo(2));
            Assert.That(
                rows[0].Q<Label>(ContextLaneContextLabelName).text,
                Is.EqualTo("source: { Id = 42 }")
            );
            Assert.That(
                rows[0].Q<Label>(ContextLaneSummaryLabelName).text,
                Does.Contain(
                    "Paths: 2 | Messages: 2 | Targets: 2 | Trace ids: 3 | Route kinds: Broadcast, Untargeted | Deliveries: 5 | Share: 5/6 (83%)"
                )
            );
            Assert.That(
                rows[0].Q<Label>(ContextLaneDetailsLabelName).text,
                Does.Contain("Messages: AlphaMessage, BetaMessage | Targets: Root/Alpha, Root/Beta")
            );
        }

        [Test]
        public void BuildGraphUiScopesVisibleContextLanesToFilteredTracePaths()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "ScoreChanged",
                        "source: { Id = 42 }",
                        "component:beta",
                        "Root/Beta",
                        "Broadcast",
                        recentTracedDeliveryCount: 3,
                        traceIds: new long[] { 101 }
                    ),
                    new FlowGraphTracePath(
                        "ScoreChanged",
                        "source: { Id = 99 }",
                        "component:gamma",
                        "Root/Gamma",
                        "Broadcast",
                        recentTracedDeliveryCount: 9,
                        traceIds: new long[] { 201 }
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot, new FlowGraphViewState("Beta"));

            VisualElement contextLanes = root.Q<VisualElement>(ContextLanesName);
            string summary = contextLanes.Q<Label>(ContextLanesSummaryLabelName).text;
            List<VisualElement> rows = contextLanes
                .Query<VisualElement>(className: ContextLaneRowClassName)
                .ToList();

            Assert.That(summary, Does.Contain("1 context lane"));
            Assert.That(summary, Does.Contain("Deliveries: 3"));
            Assert.That(
                summary,
                Does.Contain("Busiest context: source: { Id = 42 } | Share: 3/3 (100%)")
            );
            Assert.That(summary, Does.Not.Contain("source: { Id = 99 }"));
            Assert.That(rows.Count, Is.EqualTo(1));
            Assert.That(
                rows[0].Q<Label>(ContextLaneDetailsLabelName).text,
                Does.Contain("Targets: Root/Beta")
            );
            Assert.That(
                rows[0].Q<Label>(ContextLaneDetailsLabelName).text,
                Does.Not.Contain("Root/Gamma")
            );
        }

        [Test]
        public void BuildGraphUiRendersVisibleContextLanesWithDeterministicTieBreakers()
        {
            string summary = RenderVisibleContextLanesSummary(
                new FlowGraphTracePath(
                    "SharedMessage",
                    "source: { Id = 9 }",
                    "component:beta",
                    "Root/Beta",
                    "Broadcast",
                    recentTracedDeliveryCount: 3
                ),
                new FlowGraphTracePath(
                    "SharedMessage",
                    "source: { Id = 7 }",
                    "component:alpha",
                    "Root/Alpha",
                    "Broadcast",
                    recentTracedDeliveryCount: 3
                )
            );

            Assert.That(
                summary,
                Does.Contain("Busiest context: source: { Id = 7 } | Share: 3/6 (50%)")
            );
        }

        [Test]
        public void BuildGraphUiNormalizesBlankVisibleContextLaneContexts()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "AlphaMessage",
                        string.Empty,
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        recentTracedDeliveryCount: 2
                    ),
                    new FlowGraphTracePath(
                        "BetaMessage",
                        "   ",
                        "component:beta",
                        "Root/Beta",
                        "Untargeted",
                        recentTracedDeliveryCount: 3
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            VisualElement contextLanes = root.Q<VisualElement>(ContextLanesName);
            string summary = contextLanes.Q<Label>(ContextLanesSummaryLabelName).text;
            List<VisualElement> rows = contextLanes
                .Query<VisualElement>(className: ContextLaneRowClassName)
                .ToList();

            Assert.That(summary, Does.Contain("1 context lane"));
            Assert.That(summary, Does.Contain("Busiest context: <none> | Share: 5/5 (100%)"));
            Assert.That(rows.Count, Is.EqualTo(1));
            Assert.That(rows[0].Q<Label>(ContextLaneContextLabelName).text, Is.EqualTo("<none>"));
            Assert.That(
                rows[0].Q<Label>(ContextLaneSummaryLabelName).text,
                Does.Contain("Paths: 2 | Messages: 2 | Targets: 2")
            );
        }

        [Test]
        public void BuildGraphUiRendersVisibleContextLaneZeroDeliveryShareAsNotAvailable()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "IdleMessage",
                        "source: { Id = 7 }",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        recentTracedDeliveryCount: 0
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            VisualElement contextLanes = root.Q<VisualElement>(ContextLanesName);
            string summary = contextLanes.Q<Label>(ContextLanesSummaryLabelName).text;
            VisualElement row = contextLanes
                .Query<VisualElement>(className: ContextLaneRowClassName)
                .First();
            string rowSummary = row.Q<Label>(ContextLaneSummaryLabelName).text;

            Assert.That(summary, Does.Contain("1 context lane"));
            Assert.That(summary, Does.Contain("Deliveries: 0"));
            Assert.That(summary, Does.Contain("Busiest context: none"));
            Assert.That(rowSummary, Does.Contain("Share: 0/0 (n/a)"));
        }

        [Test]
        public void BuildGraphUiKeepsVisibleContextLaneRepeatedTargetPathsClean()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "FirstMessage",
                        "source: { Id = 7 }",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        recentTracedDeliveryCount: 2
                    ),
                    new FlowGraphTracePath(
                        "SecondMessage",
                        "source: { Id = 7 }",
                        "component:alpha",
                        "Root/Alpha",
                        "Untargeted",
                        recentTracedDeliveryCount: 3
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            VisualElement row = root.Q<VisualElement>(ContextLanesName)
                .Query<VisualElement>(className: ContextLaneRowClassName)
                .First();
            string summary = row.Q<Label>(ContextLaneSummaryLabelName).text;
            string details = row.Q<Label>(ContextLaneDetailsLabelName).text;

            Assert.That(summary, Does.Contain("Targets: 1"));
            Assert.That(details, Does.Contain("Targets: Root/Alpha"));
            Assert.That(details, Does.Not.Contain("Root/Alpha (component:alpha)"));
        }

        [Test]
        public void CreateExportTextDoesNotExportVisibleContextLaneAggregates()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "AlphaMessage",
                        "source: { Id = 7 }",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        recentTracedDeliveryCount: 3,
                        traceIds: new long[] { 101 }
                    ),
                },
                Array.Empty<string>()
            );

            string exportText = DxMessagingFlowGraphWindow.CreateExportText(snapshot);
            FlowGraphExportPayload exportPayload = JsonUtility.FromJson<FlowGraphExportPayload>(
                exportText
            );

            Assert.That(exportPayload.schemaVersion, Is.EqualTo(5));
            Assert.That(exportText, Does.Not.Contain("contextLanes"));
            Assert.That(exportText, Does.Not.Contain("traceContextLanes"));
            Assert.That(exportText, Does.Not.Contain("visibleContextLanes"));
        }

        [Test]
        public void BuildGraphUiRendersVisibleTraceMessageLanesFromVisibleTracePaths()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "AlphaMessage",
                        "source: { Id = 42 }",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        recentTracedDeliveryCount: 2,
                        traceIds: new long[] { 101, 102 }
                    ),
                    new FlowGraphTracePath(
                        "AlphaMessage",
                        "source: { Id = 77 }",
                        "component:beta",
                        "Root/Beta",
                        "Untargeted",
                        recentTracedDeliveryCount: 3,
                        traceIds: new long[] { 102, 103 }
                    ),
                    new FlowGraphTracePath(
                        "BetaMessage",
                        "source: { Id = 7 }",
                        "component:gamma",
                        "Root/Gamma",
                        "Targeted",
                        recentTracedDeliveryCount: 1,
                        traceIds: new long[] { 201 }
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            VisualElement traceMessageLanes = root.Q<VisualElement>(TraceMessageLanesName);
            Assert.That(traceMessageLanes, Is.Not.Null);

            string summary = traceMessageLanes.Q<Label>(TraceMessageLanesSummaryLabelName).text;
            List<VisualElement> rows = traceMessageLanes
                .Query<VisualElement>(className: TraceMessageLaneRowClassName)
                .ToList();

            Assert.That(summary, Does.Contain("2 trace message lanes"));
            Assert.That(summary, Does.Contain("Deliveries: 6"));
            Assert.That(summary, Does.Contain("Trace ids: 4"));
            Assert.That(
                summary,
                Does.Contain("Busiest trace message: AlphaMessage | Share: 5/6 (83%)")
            );
            Assert.That(rows.Count, Is.EqualTo(2));
            Assert.That(
                rows[0].Q<Label>(TraceMessageLaneMessageLabelName).text,
                Is.EqualTo("AlphaMessage")
            );
            Assert.That(
                rows[0].Q<Label>(TraceMessageLaneSummaryLabelName).text,
                Does.Contain(
                    "Paths: 2 | Contexts: 2 | Targets: 2 | Trace ids: 3 | Route kinds: Broadcast, Untargeted | Deliveries: 5 | Share: 5/6 (83%)"
                )
            );
            Assert.That(
                rows[0].Q<Label>(TraceMessageLaneDetailsLabelName).text,
                Does.Contain(
                    "Contexts: source: { Id = 42 }, source: { Id = 77 } | Targets: Root/Alpha, Root/Beta"
                )
            );
        }

        [Test]
        public void BuildGraphUiScopesVisibleTraceMessageLanesToFilteredTracePaths()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "ScoreChanged",
                        "source: { Id = 42 }",
                        "component:beta",
                        "Root/Beta",
                        "Broadcast",
                        recentTracedDeliveryCount: 3,
                        traceIds: new long[] { 101 }
                    ),
                    new FlowGraphTracePath(
                        "InventoryChanged",
                        "source: { Id = 99 }",
                        "component:gamma",
                        "Root/Gamma",
                        "Broadcast",
                        recentTracedDeliveryCount: 9,
                        traceIds: new long[] { 201 }
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot, new FlowGraphViewState("Beta"));

            VisualElement traceMessageLanes = root.Q<VisualElement>(TraceMessageLanesName);
            string summary = traceMessageLanes.Q<Label>(TraceMessageLanesSummaryLabelName).text;
            List<VisualElement> rows = traceMessageLanes
                .Query<VisualElement>(className: TraceMessageLaneRowClassName)
                .ToList();

            Assert.That(summary, Does.Contain("1 trace message lane"));
            Assert.That(summary, Does.Contain("Deliveries: 3"));
            Assert.That(
                summary,
                Does.Contain("Busiest trace message: ScoreChanged | Share: 3/3 (100%)")
            );
            Assert.That(summary, Does.Not.Contain("InventoryChanged"));
            Assert.That(rows.Count, Is.EqualTo(1));
            Assert.That(
                rows[0].Q<Label>(TraceMessageLaneDetailsLabelName).text,
                Does.Contain("Targets: Root/Beta")
            );
            Assert.That(
                rows[0].Q<Label>(TraceMessageLaneDetailsLabelName).text,
                Does.Not.Contain("Root/Gamma")
            );
        }

        [Test]
        public void BuildGraphUiRendersVisibleTraceMessageLanesWithDeterministicTieBreakers()
        {
            string summary = RenderVisibleTraceMessageLanesSummary(
                new FlowGraphTracePath(
                    "BetaMessage",
                    "source: { Id = 9 }",
                    "component:beta",
                    "Root/Beta",
                    "Broadcast",
                    recentTracedDeliveryCount: 3
                ),
                new FlowGraphTracePath(
                    "AlphaMessage",
                    "source: { Id = 7 }",
                    "component:alpha",
                    "Root/Alpha",
                    "Broadcast",
                    recentTracedDeliveryCount: 3
                )
            );

            Assert.That(
                summary,
                Does.Contain("Busiest trace message: AlphaMessage | Share: 3/6 (50%)")
            );
        }

        [Test]
        public void BuildGraphUiNormalizesBlankVisibleTraceMessageLaneContexts()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "IdleMessage",
                        string.Empty,
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        recentTracedDeliveryCount: 2
                    ),
                    new FlowGraphTracePath(
                        "IdleMessage",
                        "   ",
                        "component:beta",
                        "Root/Beta",
                        "Untargeted",
                        recentTracedDeliveryCount: 3
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            VisualElement traceMessageLanes = root.Q<VisualElement>(TraceMessageLanesName);
            List<VisualElement> rows = traceMessageLanes
                .Query<VisualElement>(className: TraceMessageLaneRowClassName)
                .ToList();

            Assert.That(rows.Count, Is.EqualTo(1));
            Assert.That(
                rows[0].Q<Label>(TraceMessageLaneSummaryLabelName).text,
                Does.Contain("Paths: 2 | Contexts: 1 | Targets: 2")
            );
            Assert.That(
                rows[0].Q<Label>(TraceMessageLaneDetailsLabelName).text,
                Does.Contain("Contexts: <none>")
            );
        }

        [Test]
        public void BuildGraphUiRendersVisibleTraceMessageLaneZeroDeliveryShareAsNotAvailable()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "IdleMessage",
                        "source: { Id = 7 }",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        recentTracedDeliveryCount: 0
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            VisualElement traceMessageLanes = root.Q<VisualElement>(TraceMessageLanesName);
            string summary = traceMessageLanes.Q<Label>(TraceMessageLanesSummaryLabelName).text;
            VisualElement row = traceMessageLanes
                .Query<VisualElement>(className: TraceMessageLaneRowClassName)
                .First();
            string rowSummary = row.Q<Label>(TraceMessageLaneSummaryLabelName).text;

            Assert.That(summary, Does.Contain("1 trace message lane"));
            Assert.That(summary, Does.Contain("Deliveries: 0"));
            Assert.That(summary, Does.Contain("Busiest trace message: none"));
            Assert.That(rowSummary, Does.Contain("Share: 0/0 (n/a)"));
        }

        [Test]
        public void BuildGraphUiKeepsVisibleTraceMessageLaneDuplicateTargetPathsDiscoverable()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "SharedMessage",
                        "source: { Id = 7 }",
                        "component:first",
                        "Root/Duplicate",
                        "Broadcast",
                        recentTracedDeliveryCount: 2
                    ),
                    new FlowGraphTracePath(
                        "SharedMessage",
                        "source: { Id = 9 }",
                        "component:second",
                        "Root/Duplicate",
                        "Broadcast",
                        recentTracedDeliveryCount: 3
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            VisualElement row = root.Q<VisualElement>(TraceMessageLanesName)
                .Query<VisualElement>(className: TraceMessageLaneRowClassName)
                .First();
            string summary = row.Q<Label>(TraceMessageLaneSummaryLabelName).text;
            string details = row.Q<Label>(TraceMessageLaneDetailsLabelName).text;

            Assert.That(summary, Does.Contain("Targets: 2"));
            Assert.That(details, Does.Contain("Root/Duplicate (component:first)"));
            Assert.That(details, Does.Contain("Root/Duplicate (component:second)"));
        }

        [Test]
        public void BuildGraphUiKeepsVisibleTraceMessageLaneRepeatedTargetPathsClean()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "SharedMessage",
                        "source: { Id = 7 }",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        recentTracedDeliveryCount: 2
                    ),
                    new FlowGraphTracePath(
                        "SharedMessage",
                        "source: { Id = 9 }",
                        "component:alpha",
                        "Root/Alpha",
                        "Untargeted",
                        recentTracedDeliveryCount: 3
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            VisualElement row = root.Q<VisualElement>(TraceMessageLanesName)
                .Query<VisualElement>(className: TraceMessageLaneRowClassName)
                .First();
            string summary = row.Q<Label>(TraceMessageLaneSummaryLabelName).text;
            string details = row.Q<Label>(TraceMessageLaneDetailsLabelName).text;

            Assert.That(summary, Does.Contain("Targets: 1"));
            Assert.That(details, Does.Contain("Targets: Root/Alpha"));
            Assert.That(details, Does.Not.Contain("Root/Alpha (component:alpha)"));
        }

        [Test]
        public void CreateExportTextDoesNotExportVisibleTraceMessageLaneAggregates()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "AlphaMessage",
                        "source: { Id = 7 }",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        recentTracedDeliveryCount: 3,
                        traceIds: new long[] { 101 }
                    ),
                },
                Array.Empty<string>()
            );

            string exportText = DxMessagingFlowGraphWindow.CreateExportText(snapshot);
            FlowGraphExportPayload exportPayload = JsonUtility.FromJson<FlowGraphExportPayload>(
                exportText
            );

            Assert.That(exportPayload.schemaVersion, Is.EqualTo(5));
            Assert.That(exportText, Does.Not.Contain("messageTraceLanes"));
            Assert.That(exportText, Does.Not.Contain("traceMessageLanes"));
            Assert.That(exportText, Does.Not.Contain("visibleTraceMessageLanes"));
        }

        [Test]
        public void BuildGraphUiRendersVisibleTraceTargetLanesFromVisibleTracePaths()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "AlphaMessage",
                        "source: { Id = 42 }",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        recentTracedDeliveryCount: 2,
                        traceIds: new long[] { 101, 102 }
                    ),
                    new FlowGraphTracePath(
                        "BetaMessage",
                        "source: { Id = 77 }",
                        "component:alpha",
                        "Root/Alpha",
                        "Untargeted",
                        recentTracedDeliveryCount: 3,
                        traceIds: new long[] { 102, 103 }
                    ),
                    new FlowGraphTracePath(
                        "GammaMessage",
                        "source: { Id = 7 }",
                        "component:gamma",
                        "Root/Gamma",
                        "Targeted",
                        recentTracedDeliveryCount: 1,
                        traceIds: new long[] { 201 }
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            VisualElement traceTargetLanes = root.Q<VisualElement>(TraceTargetLanesName);
            Assert.That(traceTargetLanes, Is.Not.Null);

            string summary = traceTargetLanes.Q<Label>(TraceTargetLanesSummaryLabelName).text;
            List<VisualElement> rows = traceTargetLanes
                .Query<VisualElement>(className: TraceTargetLaneRowClassName)
                .ToList();

            Assert.That(summary, Does.Contain("2 trace target lanes"));
            Assert.That(summary, Does.Contain("Deliveries: 6"));
            Assert.That(summary, Does.Contain("Trace ids: 4"));
            Assert.That(
                summary,
                Does.Contain("Busiest trace target: Root/Alpha | Share: 5/6 (83%)")
            );
            Assert.That(rows.Count, Is.EqualTo(2));
            Assert.That(
                rows[0].Q<Label>(TraceTargetLaneTargetLabelName).text,
                Is.EqualTo("Root/Alpha")
            );
            Assert.That(
                rows[0].Q<Label>(TraceTargetLaneSummaryLabelName).text,
                Does.Contain(
                    "Paths: 2 | Messages: 2 | Contexts: 2 | Trace ids: 3 | Route kinds: Broadcast, Untargeted | Deliveries: 5 | Share: 5/6 (83%)"
                )
            );
            Assert.That(
                rows[0].Q<Label>(TraceTargetLaneDetailsLabelName).text,
                Does.Contain(
                    "Messages: AlphaMessage, BetaMessage | Contexts: source: { Id = 42 }, source: { Id = 77 }"
                )
            );
        }

        [Test]
        public void BuildGraphUiScopesVisibleTraceTargetLanesToFilteredTracePaths()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "ScoreChanged",
                        "source: { Id = 42 }",
                        "component:beta",
                        "Root/Beta",
                        "Broadcast",
                        recentTracedDeliveryCount: 3,
                        traceIds: new long[] { 101 }
                    ),
                    new FlowGraphTracePath(
                        "InventoryChanged",
                        "source: { Id = 99 }",
                        "component:gamma",
                        "Root/Gamma",
                        "Broadcast",
                        recentTracedDeliveryCount: 9,
                        traceIds: new long[] { 201 }
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot, new FlowGraphViewState("Beta"));

            VisualElement traceTargetLanes = root.Q<VisualElement>(TraceTargetLanesName);
            string summary = traceTargetLanes.Q<Label>(TraceTargetLanesSummaryLabelName).text;
            List<VisualElement> rows = traceTargetLanes
                .Query<VisualElement>(className: TraceTargetLaneRowClassName)
                .ToList();

            Assert.That(summary, Does.Contain("1 trace target lane"));
            Assert.That(summary, Does.Contain("Deliveries: 3"));
            Assert.That(
                summary,
                Does.Contain("Busiest trace target: Root/Beta | Share: 3/3 (100%)")
            );
            Assert.That(summary, Does.Not.Contain("Root/Gamma"));
            Assert.That(rows.Count, Is.EqualTo(1));
            Assert.That(
                rows[0].Q<Label>(TraceTargetLaneDetailsLabelName).text,
                Does.Contain("Messages: ScoreChanged")
            );
            Assert.That(
                rows[0].Q<Label>(TraceTargetLaneDetailsLabelName).text,
                Does.Not.Contain("InventoryChanged")
            );
        }

        [Test]
        public void BuildGraphUiRendersVisibleTraceTargetLanesWithDeterministicTieBreakers()
        {
            string summary = RenderVisibleTraceTargetLanesSummary(
                new FlowGraphTracePath(
                    "SharedMessage",
                    "source: { Id = 9 }",
                    "component:beta",
                    "Root/Beta",
                    "Broadcast",
                    recentTracedDeliveryCount: 3
                ),
                new FlowGraphTracePath(
                    "SharedMessage",
                    "source: { Id = 7 }",
                    "component:alpha",
                    "Root/Alpha",
                    "Broadcast",
                    recentTracedDeliveryCount: 3
                )
            );

            Assert.That(
                summary,
                Does.Contain("Busiest trace target: Root/Alpha | Share: 3/6 (50%)")
            );
        }

        [Test]
        public void BuildGraphUiNormalizesBlankVisibleTraceTargetLaneContexts()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "IdleMessage",
                        string.Empty,
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        recentTracedDeliveryCount: 2
                    ),
                    new FlowGraphTracePath(
                        "IdleMessage",
                        "   ",
                        "component:alpha",
                        "Root/Alpha",
                        "Untargeted",
                        recentTracedDeliveryCount: 3
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            VisualElement traceTargetLanes = root.Q<VisualElement>(TraceTargetLanesName);
            List<VisualElement> rows = traceTargetLanes
                .Query<VisualElement>(className: TraceTargetLaneRowClassName)
                .ToList();

            Assert.That(rows.Count, Is.EqualTo(1));
            Assert.That(
                rows[0].Q<Label>(TraceTargetLaneSummaryLabelName).text,
                Does.Contain("Paths: 2 | Messages: 1 | Contexts: 1")
            );
            Assert.That(
                rows[0].Q<Label>(TraceTargetLaneDetailsLabelName).text,
                Does.Contain("Contexts: <none>")
            );
        }

        [Test]
        public void BuildGraphUiRendersVisibleTraceTargetLaneZeroDeliveryShareAsNotAvailable()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "IdleMessage",
                        "source: { Id = 7 }",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        recentTracedDeliveryCount: 0
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            VisualElement traceTargetLanes = root.Q<VisualElement>(TraceTargetLanesName);
            string summary = traceTargetLanes.Q<Label>(TraceTargetLanesSummaryLabelName).text;
            VisualElement row = traceTargetLanes
                .Query<VisualElement>(className: TraceTargetLaneRowClassName)
                .First();
            string rowSummary = row.Q<Label>(TraceTargetLaneSummaryLabelName).text;

            Assert.That(summary, Does.Contain("1 trace target lane"));
            Assert.That(summary, Does.Contain("Deliveries: 0"));
            Assert.That(summary, Does.Contain("Busiest trace target: none"));
            Assert.That(rowSummary, Does.Contain("Share: 0/0 (n/a)"));
        }

        [Test]
        public void BuildGraphUiKeepsVisibleTraceTargetLaneDuplicateTargetPathsDiscoverable()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "FirstMessage",
                        "source: { Id = 7 }",
                        "component:first",
                        "Root/Duplicate",
                        "Broadcast",
                        recentTracedDeliveryCount: 3
                    ),
                    new FlowGraphTracePath(
                        "SecondMessage",
                        "source: { Id = 9 }",
                        "component:second",
                        "Root/Duplicate",
                        "Broadcast",
                        recentTracedDeliveryCount: 3
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            List<VisualElement> rows = root.Q<VisualElement>(TraceTargetLanesName)
                .Query<VisualElement>(className: TraceTargetLaneRowClassName)
                .ToList();

            Assert.That(rows.Count, Is.EqualTo(2));
            Assert.That(
                rows.Select(row => row.Q<Label>(TraceTargetLaneTargetLabelName).text),
                Is.EqualTo(
                    new[]
                    {
                        "Root/Duplicate (component:first)",
                        "Root/Duplicate (component:second)",
                    }
                )
            );
        }

        [Test]
        public void CreateExportTextDoesNotExportVisibleTraceTargetLaneAggregates()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "AlphaMessage",
                        "source: { Id = 7 }",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        recentTracedDeliveryCount: 3,
                        traceIds: new long[] { 101 }
                    ),
                },
                Array.Empty<string>()
            );

            string exportText = DxMessagingFlowGraphWindow.CreateExportText(snapshot);
            FlowGraphExportPayload exportPayload = JsonUtility.FromJson<FlowGraphExportPayload>(
                exportText
            );

            Assert.That(exportPayload.schemaVersion, Is.EqualTo(5));
            Assert.That(exportText, Does.Not.Contain("targetTraceLanes"));
            Assert.That(exportText, Does.Not.Contain("traceTargetLanes"));
            Assert.That(exportText, Does.Not.Contain("visibleTraceTargetLanes"));
        }

        [Test]
        public void BuildGraphUiRendersVisibleTraceRouteKindLanesFromVisibleTracePaths()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "AlphaMessage",
                        "source: { Id = 42 }",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        recentTracedDeliveryCount: 2,
                        traceIds: new long[] { 101, 102 }
                    ),
                    new FlowGraphTracePath(
                        "BetaMessage",
                        "source: { Id = 77 }",
                        "component:beta",
                        "Root/Beta",
                        "Broadcast",
                        recentTracedDeliveryCount: 3,
                        traceIds: new long[] { 101 }
                    ),
                    new FlowGraphTracePath(
                        "GammaMessage",
                        "source: { Id = 7 }",
                        "component:gamma",
                        "Root/Gamma",
                        "Targeted",
                        recentTracedDeliveryCount: 1,
                        traceIds: new long[] { 201 }
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            VisualElement traceRouteKindLanes = root.Q<VisualElement>(TraceRouteKindLanesName);
            Assert.That(traceRouteKindLanes, Is.Not.Null);

            string summary = traceRouteKindLanes.Q<Label>(TraceRouteKindLanesSummaryLabelName).text;
            List<VisualElement> rows = traceRouteKindLanes
                .Query<VisualElement>(className: TraceRouteKindLaneRowClassName)
                .ToList();

            Assert.That(summary, Does.Contain("2 trace route kind lanes"));
            Assert.That(summary, Does.Contain("Deliveries: 6"));
            Assert.That(summary, Does.Contain("Trace ids: 3"));
            Assert.That(
                summary,
                Does.Contain("Busiest trace route kind: Broadcast | Share: 5/6 (83%)")
            );
            Assert.That(rows.Count, Is.EqualTo(2));
            Assert.That(
                rows[0].Q<Label>(TraceRouteKindLaneRouteKindLabelName).text,
                Is.EqualTo("Broadcast")
            );
            Assert.That(
                rows[0].Q<Label>(TraceRouteKindLaneSummaryLabelName).text,
                Does.Contain(
                    "Paths: 2 | Messages: 2 | Targets: 2 | Contexts: 2 | Trace ids: 2 | Deliveries: 5 | Share: 5/6 (83%)"
                )
            );
            Assert.That(
                rows[0].Q<Label>(TraceRouteKindLaneDetailsLabelName).text,
                Does.Contain(
                    "Messages: AlphaMessage, BetaMessage | Targets: Root/Alpha, Root/Beta | Contexts: source: { Id = 42 }, source: { Id = 77 }"
                )
            );
        }

        [Test]
        public void BuildGraphUiScopesVisibleTraceRouteKindLanesToFilteredTracePaths()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "ScoreChanged",
                        "source: { Id = 42 }",
                        "component:beta",
                        "Root/Beta",
                        "Broadcast",
                        recentTracedDeliveryCount: 3,
                        traceIds: new long[] { 101 }
                    ),
                    new FlowGraphTracePath(
                        "InventoryChanged",
                        "source: { Id = 99 }",
                        "component:gamma",
                        "Root/Gamma",
                        "Targeted",
                        recentTracedDeliveryCount: 9,
                        traceIds: new long[] { 201 }
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot, new FlowGraphViewState("Beta"));

            VisualElement traceRouteKindLanes = root.Q<VisualElement>(TraceRouteKindLanesName);
            string summary = traceRouteKindLanes.Q<Label>(TraceRouteKindLanesSummaryLabelName).text;
            List<VisualElement> rows = traceRouteKindLanes
                .Query<VisualElement>(className: TraceRouteKindLaneRowClassName)
                .ToList();

            Assert.That(summary, Does.Contain("1 trace route kind lane"));
            Assert.That(summary, Does.Contain("Deliveries: 3"));
            Assert.That(
                summary,
                Does.Contain("Busiest trace route kind: Broadcast | Share: 3/3 (100%)")
            );
            Assert.That(summary, Does.Not.Contain("Targeted"));
            Assert.That(rows.Count, Is.EqualTo(1));
            Assert.That(
                rows[0].Q<Label>(TraceRouteKindLaneDetailsLabelName).text,
                Does.Contain("Messages: ScoreChanged")
            );
            Assert.That(
                rows[0].Q<Label>(TraceRouteKindLaneDetailsLabelName).text,
                Does.Not.Contain("InventoryChanged")
            );
        }

        [Test]
        public void BuildGraphUiRendersVisibleTraceRouteKindLanesWithDeterministicTieBreakers()
        {
            string summary = RenderVisibleTraceRouteKindLanesSummary(
                new FlowGraphTracePath(
                    "TargetedMessage",
                    "source: { Id = 9 }",
                    "component:beta",
                    "Root/Beta",
                    "Targeted",
                    recentTracedDeliveryCount: 3
                ),
                new FlowGraphTracePath(
                    "BroadcastMessage",
                    "source: { Id = 7 }",
                    "component:alpha",
                    "Root/Alpha",
                    "Broadcast",
                    recentTracedDeliveryCount: 3
                )
            );

            Assert.That(
                summary,
                Does.Contain("Busiest trace route kind: Broadcast | Share: 3/6 (50%)")
            );
        }

        [Test]
        public void BuildGraphUiPrefersWiderVisibleTraceRouteKindLaneWhenDeliveriesTie()
        {
            string summary = RenderVisibleTraceRouteKindLanesSummary(
                new FlowGraphTracePath(
                    "TargetedMessage",
                    "source: { Id = 9 }",
                    "component:beta",
                    "Root/Beta",
                    "Targeted",
                    recentTracedDeliveryCount: 3
                ),
                new FlowGraphTracePath(
                    "BroadcastMessage",
                    "source: { Id = 7 }",
                    "component:alpha",
                    "Root/Alpha",
                    "Broadcast",
                    recentTracedDeliveryCount: 2
                ),
                new FlowGraphTracePath(
                    "BroadcastFollowUp",
                    "source: { Id = 8 }",
                    "component:gamma",
                    "Root/Gamma",
                    "Broadcast",
                    recentTracedDeliveryCount: 1
                )
            );

            Assert.That(
                summary,
                Does.Contain("Busiest trace route kind: Broadcast | Share: 3/6 (50%)")
            );
        }

        [Test]
        public void BuildGraphUiGroupsBlankVisibleTraceRouteKindLanesAsUnknown()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "IdleMessage",
                        string.Empty,
                        "component:alpha",
                        "Root/Alpha",
                        string.Empty,
                        recentTracedDeliveryCount: 2
                    ),
                    new FlowGraphTracePath(
                        "IdleMessage",
                        "   ",
                        "component:alpha",
                        "Root/Alpha",
                        "   ",
                        recentTracedDeliveryCount: 3
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            VisualElement traceRouteKindLanes = root.Q<VisualElement>(TraceRouteKindLanesName);
            List<VisualElement> rows = traceRouteKindLanes
                .Query<VisualElement>(className: TraceRouteKindLaneRowClassName)
                .ToList();

            Assert.That(rows.Count, Is.EqualTo(1));
            Assert.That(
                rows[0].Q<Label>(TraceRouteKindLaneRouteKindLabelName).text,
                Is.EqualTo("<unknown route kind>")
            );
            Assert.That(
                rows[0].Q<Label>(TraceRouteKindLaneSummaryLabelName).text,
                Does.Contain(
                    "Paths: 2 | Messages: 1 | Targets: 1 | Contexts: 1 | Trace ids: 0 | Deliveries: 5 | Share: 5/5 (100%)"
                )
            );
            Assert.That(
                rows[0].Q<Label>(TraceRouteKindLaneDetailsLabelName).text,
                Does.Contain("Contexts: <none>")
            );
        }

        [Test]
        public void BuildGraphUiFiltersBlankVisibleTraceRouteKindLanesByUnknownLabel()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "IdleMessage",
                        string.Empty,
                        "component:alpha",
                        "Root/Alpha",
                        string.Empty,
                        recentTracedDeliveryCount: 2
                    ),
                    new FlowGraphTracePath(
                        "ActiveMessage",
                        "source: { Id = 7 }",
                        "component:beta",
                        "Root/Beta",
                        "Broadcast",
                        recentTracedDeliveryCount: 3
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(
                root,
                snapshot,
                new FlowGraphViewState("unknown route kind")
            );

            VisualElement traceRouteKindLanes = root.Q<VisualElement>(TraceRouteKindLanesName);
            Assert.That(traceRouteKindLanes, Is.Not.Null);

            List<VisualElement> rows = traceRouteKindLanes
                .Query<VisualElement>(className: TraceRouteKindLaneRowClassName)
                .ToList();

            Assert.That(rows.Count, Is.EqualTo(1));
            Assert.That(
                rows[0].Q<Label>(TraceRouteKindLaneRouteKindLabelName).text,
                Is.EqualTo("<unknown route kind>")
            );
            Assert.That(
                rows[0].Q<Label>(TraceRouteKindLaneDetailsLabelName).text,
                Does.Contain("Messages: IdleMessage")
            );
            Assert.That(
                rows[0].Q<Label>(TraceRouteKindLaneDetailsLabelName).text,
                Does.Not.Contain("ActiveMessage")
            );
        }

        [Test]
        public void BuildGraphUiRendersVisibleTraceRouteKindLaneZeroDeliveryShareAsNotAvailable()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "IdleMessage",
                        "source: { Id = 7 }",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        recentTracedDeliveryCount: 0
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            VisualElement traceRouteKindLanes = root.Q<VisualElement>(TraceRouteKindLanesName);
            string summary = traceRouteKindLanes.Q<Label>(TraceRouteKindLanesSummaryLabelName).text;
            VisualElement row = traceRouteKindLanes
                .Query<VisualElement>(className: TraceRouteKindLaneRowClassName)
                .First();
            string rowSummary = row.Q<Label>(TraceRouteKindLaneSummaryLabelName).text;

            Assert.That(summary, Does.Contain("1 trace route kind lane"));
            Assert.That(summary, Does.Contain("Deliveries: 0"));
            Assert.That(summary, Does.Contain("Busiest trace route kind: none"));
            Assert.That(rowSummary, Does.Contain("Share: 0/0 (n/a)"));
        }

        [Test]
        public void BuildGraphUiKeepsVisibleTraceRouteKindLaneDuplicateTargetPathsDiscoverable()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "FirstMessage",
                        "source: { Id = 7 }",
                        "component:first",
                        "Root/Duplicate",
                        "Broadcast",
                        recentTracedDeliveryCount: 2
                    ),
                    new FlowGraphTracePath(
                        "SecondMessage",
                        "source: { Id = 9 }",
                        "component:second",
                        "Root/Duplicate",
                        "Broadcast",
                        recentTracedDeliveryCount: 3
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            VisualElement row = root.Q<VisualElement>(TraceRouteKindLanesName)
                .Query<VisualElement>(className: TraceRouteKindLaneRowClassName)
                .First();
            string summary = row.Q<Label>(TraceRouteKindLaneSummaryLabelName).text;
            string details = row.Q<Label>(TraceRouteKindLaneDetailsLabelName).text;

            Assert.That(summary, Does.Contain("Targets: 2"));
            Assert.That(details, Does.Contain("Root/Duplicate (component:first)"));
            Assert.That(details, Does.Contain("Root/Duplicate (component:second)"));
        }

        [Test]
        public void BuildGraphUiKeepsVisibleTraceRouteKindLaneGlobalDuplicateTargetPathsDiscoverable()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "FirstMessage",
                        "source: { Id = 7 }",
                        "component:first",
                        "Root/Duplicate",
                        "Broadcast",
                        recentTracedDeliveryCount: 2
                    ),
                    new FlowGraphTracePath(
                        "SecondMessage",
                        "source: { Id = 9 }",
                        "component:second",
                        "Root/Duplicate",
                        "Targeted",
                        recentTracedDeliveryCount: 2
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            List<VisualElement> rows = root.Q<VisualElement>(TraceRouteKindLanesName)
                .Query<VisualElement>(className: TraceRouteKindLaneRowClassName)
                .ToList();

            Assert.That(rows.Count, Is.EqualTo(2));
            Assert.That(
                rows[0].Q<Label>(TraceRouteKindLaneDetailsLabelName).text,
                Does.Contain("Targets: Root/Duplicate (component:first)")
            );
            Assert.That(
                rows[1].Q<Label>(TraceRouteKindLaneDetailsLabelName).text,
                Does.Contain("Targets: Root/Duplicate (component:second)")
            );
        }

        [Test]
        public void CreateExportTextDoesNotExportVisibleTraceRouteKindLaneAggregates()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "AlphaMessage",
                        "source: { Id = 7 }",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        recentTracedDeliveryCount: 3,
                        traceIds: new long[] { 101 }
                    ),
                },
                Array.Empty<string>()
            );

            string exportText = DxMessagingFlowGraphWindow.CreateExportText(snapshot);
            FlowGraphExportPayload exportPayload = JsonUtility.FromJson<FlowGraphExportPayload>(
                exportText
            );

            Assert.That(exportPayload.schemaVersion, Is.EqualTo(5));
            Assert.That(exportText, Does.Not.Contain("traceRouteKindLanes"));
            Assert.That(exportText, Does.Not.Contain("routeKindTraceLanes"));
            Assert.That(exportText, Does.Not.Contain("visibleTraceRouteKindLanes"));
        }

        [Test]
        public void BuildGraphUiRendersVisibleTraceIdLanesFromVisibleTracePaths()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "AlphaMessage",
                        "source: { Id = 42 }",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        recentTracedDeliveryCount: 2,
                        traceIds: new long[] { 101, 102 }
                    ),
                    new FlowGraphTracePath(
                        "BetaMessage",
                        "source: { Id = 77 }",
                        "component:beta",
                        "Root/Beta",
                        "Untargeted",
                        recentTracedDeliveryCount: 3,
                        traceIds: new long[] { 101 }
                    ),
                    new FlowGraphTracePath(
                        "GammaMessage",
                        "source: { Id = 7 }",
                        "component:gamma",
                        "Root/Gamma",
                        "Targeted",
                        recentTracedDeliveryCount: 1,
                        traceIds: new long[] { 201 }
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            VisualElement traceIdLanes = root.Q<VisualElement>(TraceIdLanesName);
            Assert.That(traceIdLanes, Is.Not.Null);

            string summary = traceIdLanes.Q<Label>(TraceIdLanesSummaryLabelName).text;
            List<VisualElement> rows = traceIdLanes
                .Query<VisualElement>(className: TraceIdLaneRowClassName)
                .ToList();

            Assert.That(summary, Does.Contain("3 trace id lanes"));
            Assert.That(summary, Does.Contain("Path memberships: 4"));
            Assert.That(summary, Does.Contain("Widest trace id: 101 | Share: 2/4 (50%)"));
            Assert.That(rows.Count, Is.EqualTo(3));
            Assert.That(rows[0].Q<Label>(TraceIdLaneTraceIdLabelName).text, Is.EqualTo("101"));
            Assert.That(
                rows[0].Q<Label>(TraceIdLaneSummaryLabelName).text,
                Does.Contain(
                    "Paths: 2 | Messages: 2 | Targets: 2 | Contexts: 2 | Route kinds: Broadcast, Untargeted | Share: 2/4 (50%)"
                )
            );
            Assert.That(
                rows[0].Q<Label>(TraceIdLaneDetailsLabelName).text,
                Does.Contain(
                    "Messages: AlphaMessage, BetaMessage | Targets: Root/Alpha, Root/Beta | Contexts: source: { Id = 42 }, source: { Id = 77 }"
                )
            );
        }

        [Test]
        public void BuildGraphUiScopesVisibleTraceIdLanesToFilteredTracePaths()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "ScoreChanged",
                        "source: { Id = 42 }",
                        "component:beta",
                        "Root/Beta",
                        "Broadcast",
                        recentTracedDeliveryCount: 3,
                        traceIds: new long[] { 101, 102 }
                    ),
                    new FlowGraphTracePath(
                        "InventoryChanged",
                        "source: { Id = 99 }",
                        "component:gamma",
                        "Root/Gamma",
                        "Broadcast",
                        recentTracedDeliveryCount: 9,
                        traceIds: new long[] { 201 }
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot, new FlowGraphViewState("Beta"));

            VisualElement traceIdLanes = root.Q<VisualElement>(TraceIdLanesName);
            string summary = traceIdLanes.Q<Label>(TraceIdLanesSummaryLabelName).text;
            List<VisualElement> rows = traceIdLanes
                .Query<VisualElement>(className: TraceIdLaneRowClassName)
                .ToList();

            Assert.That(summary, Does.Contain("2 trace id lanes"));
            Assert.That(summary, Does.Contain("Path memberships: 2"));
            Assert.That(summary, Does.Contain("Widest trace id: 101 | Share: 1/2 (50%)"));
            Assert.That(summary, Does.Not.Contain("201"));
            Assert.That(rows.Count, Is.EqualTo(2));
            Assert.That(
                rows[0].Q<Label>(TraceIdLaneDetailsLabelName).text,
                Does.Contain("Messages: ScoreChanged")
            );
            Assert.That(
                rows[0].Q<Label>(TraceIdLaneDetailsLabelName).text,
                Does.Not.Contain("InventoryChanged")
            );
        }

        [Test]
        public void BuildGraphUiRendersVisibleTraceIdLanesWithDeterministicTieBreakers()
        {
            string summary = RenderVisibleTraceIdLanesSummary(
                new FlowGraphTracePath(
                    "BetaMessage",
                    "source: { Id = 9 }",
                    "component:beta",
                    "Root/Beta",
                    "Broadcast",
                    recentTracedDeliveryCount: 3,
                    traceIds: new long[] { 202 }
                ),
                new FlowGraphTracePath(
                    "AlphaMessage",
                    "source: { Id = 7 }",
                    "component:alpha",
                    "Root/Alpha",
                    "Broadcast",
                    recentTracedDeliveryCount: 3,
                    traceIds: new long[] { 101 }
                )
            );

            Assert.That(summary, Does.Contain("Widest trace id: 101 | Share: 1/2 (50%)"));
        }

        [Test]
        public void BuildGraphUiIgnoresNonPositiveVisibleTraceIdLaneIds()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "LegacyMessage",
                        "source: { Id = 7 }",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        recentTracedDeliveryCount: 3,
                        traceIds: new long[] { 0, -7 }
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            VisualElement traceIdLanes = root.Q<VisualElement>(TraceIdLanesName);
            string summary = traceIdLanes.Q<Label>(TraceIdLanesSummaryLabelName).text;
            List<VisualElement> rows = traceIdLanes
                .Query<VisualElement>(className: TraceIdLaneRowClassName)
                .ToList();

            Assert.That(summary, Does.Contain("0 trace id lanes"));
            Assert.That(summary, Does.Contain("Path memberships: 0"));
            Assert.That(summary, Does.Contain("Widest trace id: none"));
            Assert.That(rows, Is.Empty);
        }

        [Test]
        public void BuildGraphUiKeepsVisibleTraceIdLaneDuplicateTargetPathsDiscoverable()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "FirstMessage",
                        "source: { Id = 7 }",
                        "component:first",
                        "Root/Duplicate",
                        "Broadcast",
                        recentTracedDeliveryCount: 2,
                        traceIds: new long[] { 101 }
                    ),
                    new FlowGraphTracePath(
                        "SecondMessage",
                        "source: { Id = 9 }",
                        "component:second",
                        "Root/Duplicate",
                        "Broadcast",
                        recentTracedDeliveryCount: 3,
                        traceIds: new long[] { 101 }
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            VisualElement row = root.Q<VisualElement>(TraceIdLanesName)
                .Query<VisualElement>(className: TraceIdLaneRowClassName)
                .First();
            string summary = row.Q<Label>(TraceIdLaneSummaryLabelName).text;
            string details = row.Q<Label>(TraceIdLaneDetailsLabelName).text;

            Assert.That(summary, Does.Contain("Targets: 2"));
            Assert.That(details, Does.Contain("Root/Duplicate (component:first)"));
            Assert.That(details, Does.Contain("Root/Duplicate (component:second)"));
        }

        [Test]
        public void BuildGraphUiKeepsVisibleTraceIdLaneGlobalDuplicateTargetPathsDiscoverable()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "FirstMessage",
                        "source: { Id = 7 }",
                        "component:first",
                        "Root/Duplicate",
                        "Broadcast",
                        recentTracedDeliveryCount: 2,
                        traceIds: new long[] { 101 }
                    ),
                    new FlowGraphTracePath(
                        "SecondMessage",
                        "source: { Id = 9 }",
                        "component:second",
                        "Root/Duplicate",
                        "Broadcast",
                        recentTracedDeliveryCount: 3,
                        traceIds: new long[] { 202 }
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            List<VisualElement> rows = root.Q<VisualElement>(TraceIdLanesName)
                .Query<VisualElement>(className: TraceIdLaneRowClassName)
                .ToList();

            Assert.That(rows.Count, Is.EqualTo(2));
            Assert.That(
                rows[0].Q<Label>(TraceIdLaneDetailsLabelName).text,
                Does.Contain("Targets: Root/Duplicate (component:first)")
            );
            Assert.That(
                rows[1].Q<Label>(TraceIdLaneDetailsLabelName).text,
                Does.Contain("Targets: Root/Duplicate (component:second)")
            );
        }

        [Test]
        public void CreateExportTextDoesNotExportVisibleTraceIdLaneAggregates()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "AlphaMessage",
                        "source: { Id = 7 }",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        recentTracedDeliveryCount: 3,
                        traceIds: new long[] { 101 }
                    ),
                },
                Array.Empty<string>()
            );

            string exportText = DxMessagingFlowGraphWindow.CreateExportText(snapshot);
            FlowGraphExportPayload exportPayload = JsonUtility.FromJson<FlowGraphExportPayload>(
                exportText
            );

            Assert.That(exportPayload.schemaVersion, Is.EqualTo(5));
            Assert.That(exportText, Does.Not.Contain("traceIdLanes"));
            Assert.That(exportText, Does.Not.Contain("visibleTraceIdLanes"));
        }

        [Test]
        public void BuildGraphUiRendersWidestTraceSummaryAndExportsTraceIds()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "ScoreChanged",
                        "source: { Id = 42 }",
                        "component:beta",
                        "Root/Beta",
                        "Broadcast",
                        recentTracedDeliveryCount: 3,
                        traceIds: new long[] { 205, 101, 205 }
                    ),
                    new FlowGraphTracePath(
                        "ScoreChanged",
                        "source: { Id = 42 }",
                        "component:gamma",
                        "Root/Gamma",
                        "Broadcast",
                        recentTracedDeliveryCount: 1,
                        traceIds: new long[] { 205 }
                    ),
                    new FlowGraphTracePath(
                        "InventoryChanged",
                        "source: { Id = 7 }",
                        "component:delta",
                        "Root/Delta",
                        "Broadcast",
                        recentTracedDeliveryCount: 4,
                        traceIds: new long[] { 201, 202 }
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(
                root,
                snapshot,
                new FlowGraphViewState("Score")
            );

            string summary = root.Q<VisualElement>(DxMessagingFlowGraphWindow.TracePathsName)
                .Q<Label>(DxMessagingFlowGraphWindow.TracePathsSummaryLabelName)
                .text;
            string exportText = DxMessagingFlowGraphWindow.CreateExportText(snapshot, "Score");
            FlowGraphExportPayload exportPayload = JsonUtility.FromJson<FlowGraphExportPayload>(
                exportText
            );

            Assert.That(summary, Does.Contain("2 traced paths"));
            Assert.That(summary, Does.Contain("Trace ids: 2"));
            Assert.That(summary, Does.Contain("Widest trace: 205 (2 paths)"));
            Assert.That(summary, Does.Not.Contain("201"));
            Assert.That(exportPayload.schemaVersion, Is.EqualTo(5));
            Assert.That(exportPayload.tracePathCount, Is.EqualTo(2));
            Assert.That(exportText, Does.Not.Contain("contextVolume"));
            Assert.That(exportText, Does.Not.Contain("busiestContext"));
            Assert.That(exportText, Does.Not.Contain("Busiest context:"));
            Assert.That(exportText, Does.Not.Contain("busiestContextShare"));
            Assert.That(exportText, Does.Not.Contain("Busiest context share"));
            Assert.That(
                exportPayload.tracePaths[0].recentTraceIds,
                Is.EqualTo(new long[] { 101, 205 })
            );
            Assert.That(exportPayload.tracePaths[1].recentTraceIds, Is.EqualTo(new long[] { 205 }));
            Assert.That(exportText, Does.Contain("\"recentTraceIds\""));
            Assert.That(exportText, Does.Not.Contain("Root/Delta"));
        }

        [Test]
        public void BuildGraphUiRendersWidestTraceSummaryUsesTraceIdTieBreaker()
        {
            string summary = RenderTracePathsSummary(
                new FlowGraphTracePath(
                    "ScoreChanged",
                    "source: { Id = 42 }",
                    "component:beta",
                    "Root/Beta",
                    "Broadcast",
                    recentTracedDeliveryCount: 1,
                    traceIds: new long[] { 202 }
                ),
                new FlowGraphTracePath(
                    "ScoreChanged",
                    "source: { Id = 42 }",
                    "component:gamma",
                    "Root/Gamma",
                    "Broadcast",
                    recentTracedDeliveryCount: 1,
                    traceIds: new long[] { 101 }
                )
            );

            Assert.That(summary, Does.Contain("Widest trace: 101 (1 path)"));
        }

        [Test]
        public void BuildGraphUiRendersTracePathContextVolumeSummary()
        {
            FlowGraphSnapshot snapshot = new(
                new[]
                {
                    new FlowGraphComponentNode(
                        "component:alpha",
                        "Root/Alpha",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 2,
                        localMessageCount: 2
                    ),
                    new FlowGraphComponentNode(
                        "component:beta",
                        "Root/Beta",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 3,
                        localMessageCount: 3
                    ),
                    new FlowGraphComponentNode(
                        "component:gamma",
                        "Root/Gamma",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 1,
                        localMessageCount: 1
                    ),
                },
                new[]
                {
                    new FlowGraphMessageNode("InventoryChanged", 1, 2),
                    new FlowGraphMessageNode("ScoreChanged", 2, 4),
                },
                new[]
                {
                    new FlowGraphEdge(
                        "InventoryChanged",
                        "component:alpha",
                        "Root/Alpha",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 2,
                        recentTracedDeliveryCount: 2
                    ),
                    new FlowGraphEdge(
                        "ScoreChanged",
                        "component:beta",
                        "Root/Beta",
                        "Broadcast",
                        registrationCount: 1,
                        callCount: 3,
                        recentTracedDeliveryCount: 3
                    ),
                    new FlowGraphEdge(
                        "ScoreChanged",
                        "component:gamma",
                        "Root/Gamma",
                        "Broadcast",
                        registrationCount: 1,
                        callCount: 1,
                        recentTracedDeliveryCount: 1
                    ),
                },
                new[]
                {
                    new FlowGraphTracePath(
                        "InventoryChanged",
                        "source: { Id = 42 }",
                        "component:alpha",
                        "Root/Alpha",
                        "Untargeted",
                        recentTracedDeliveryCount: 2
                    ),
                    new FlowGraphTracePath(
                        "ScoreChanged",
                        "source: { Id = 42 }",
                        "component:beta",
                        "Root/Beta",
                        "Broadcast",
                        recentTracedDeliveryCount: 3
                    ),
                    new FlowGraphTracePath(
                        "ScoreChanged",
                        "source: { Id = 7 }",
                        "component:gamma",
                        "Root/Gamma",
                        "Broadcast",
                        recentTracedDeliveryCount: 1
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot, new FlowGraphViewState("42"));

            VisualElement tracePaths = root.Q<VisualElement>(
                DxMessagingFlowGraphWindow.TracePathsName
            );
            string summary = tracePaths
                .Q<Label>(DxMessagingFlowGraphWindow.TracePathsSummaryLabelName)
                .text;

            Assert.That(summary, Does.Contain("2 traced paths"));
            Assert.That(summary, Does.Contain("Deliveries: 5"));
            Assert.That(summary, Does.Contain("Contexts: 1"));
            Assert.That(summary, Does.Contain("Busiest context: source: { Id = 42 } (5)"));
            Assert.That(
                summary,
                Does.Contain("Busiest context share: source: { Id = 42 } | Share: 5/5 (100%)")
            );
            Assert.That(
                summary,
                Does.Contain(
                    "Busiest trace message: ScoreChanged (3 deliveries) | Share: 3/5 (60%)"
                )
            );
            Assert.That(
                summary,
                Does.Contain("Busiest target: Root/Beta (3 deliveries) | Share: 3/5 (60%)")
            );
            Assert.That(
                summary,
                Does.Contain(
                    "Busiest path: ScoreChanged -> Root/Beta (Broadcast, source: { Id = 42 }, 3 deliveries)"
                )
            );
            Assert.That(summary, Does.Contain("Busiest path share: 3/5 (60%)"));
        }

        [Test]
        public void BuildGraphUiRendersTracePathBusiestMessageSummaryAggregatesMessageDeliveries()
        {
            string summary = RenderTracePathsSummary(
                new FlowGraphTracePath(
                    "AlphaMessage",
                    "source: { Id = 7 }",
                    "component:alpha",
                    "Root/Alpha",
                    "Broadcast",
                    recentTracedDeliveryCount: 2
                ),
                new FlowGraphTracePath(
                    "AlphaMessage",
                    "source: { Id = 9 }",
                    "component:beta",
                    "Root/Beta",
                    "Untargeted",
                    recentTracedDeliveryCount: 2
                ),
                new FlowGraphTracePath(
                    "BetaMessage",
                    "source: { Id = 11 }",
                    "component:gamma",
                    "Root/Gamma",
                    "Broadcast",
                    recentTracedDeliveryCount: 3
                )
            );

            Assert.That(
                summary,
                Does.Contain(
                    "Busiest trace message: AlphaMessage (4 deliveries) | Share: 4/7 (57%)"
                )
            );
            Assert.That(
                summary,
                Does.Contain(
                    "Busiest path: BetaMessage -> Root/Gamma (Broadcast, source: { Id = 11 }, 3 deliveries)"
                )
            );
        }

        [Test]
        public void BuildGraphUiRendersTracePathBusiestContextShareSummary()
        {
            string summary = RenderTracePathsSummary(
                new FlowGraphTracePath(
                    "AlphaMessage",
                    "source: { Id = 42 }",
                    "component:alpha",
                    "Root/Alpha",
                    "Broadcast",
                    recentTracedDeliveryCount: 3
                ),
                new FlowGraphTracePath(
                    "BetaMessage",
                    "source: { Id = 42 }",
                    "component:beta",
                    "Root/Beta",
                    "Untargeted",
                    recentTracedDeliveryCount: 2
                ),
                new FlowGraphTracePath(
                    "GammaMessage",
                    string.Empty,
                    "component:gamma",
                    "Root/Gamma",
                    "Broadcast",
                    recentTracedDeliveryCount: 2
                )
            );

            Assert.That(
                summary,
                Does.Contain("Busiest context share: source: { Id = 42 } | Share: 5/7 (71%)")
            );
        }

        [Test]
        public void BuildGraphUiRendersTracePathBusiestContextShareNormalizesContextAndUsesTieBreaker()
        {
            string summary = RenderTracePathsSummary(
                new FlowGraphTracePath(
                    "AlphaMessage",
                    string.Empty,
                    "component:alpha",
                    "Root/Alpha",
                    "Broadcast",
                    recentTracedDeliveryCount: 2
                ),
                new FlowGraphTracePath(
                    "BetaMessage",
                    "   ",
                    "component:beta",
                    "Root/Beta",
                    "Untargeted",
                    recentTracedDeliveryCount: 2
                ),
                new FlowGraphTracePath(
                    "GammaMessage",
                    "source: { Id = 9 }",
                    "component:gamma",
                    "Root/Gamma",
                    "Broadcast",
                    recentTracedDeliveryCount: 4
                )
            );

            Assert.That(summary, Does.Contain("Busiest context: <none> (4)"));
            Assert.That(summary, Does.Contain("Busiest context share: <none> | Share: 4/8 (50%)"));
        }

        [Test]
        public void BuildGraphUiRendersTracePathBusiestContextShareHandlesZeroDeliveries()
        {
            string summary = RenderTracePathsSummary(
                new FlowGraphTracePath(
                    "AlphaMessage",
                    "source: { Id = 42 }",
                    "component:alpha",
                    "Root/Alpha",
                    "Broadcast",
                    recentTracedDeliveryCount: 0
                )
            );

            Assert.That(summary, Does.Contain("Busiest context share: none"));
        }

        [Test]
        public void BuildGraphUiRendersTracePathBusiestMessageSummaryUsesMessageNameTieBreaker()
        {
            string summary = RenderTracePathsSummary(
                new FlowGraphTracePath(
                    "BetaMessage",
                    "source: { Id = 7 }",
                    "component:beta",
                    "Root/Beta",
                    "Broadcast",
                    recentTracedDeliveryCount: 3
                ),
                new FlowGraphTracePath(
                    "AlphaMessage",
                    "source: { Id = 9 }",
                    "component:alpha",
                    "Root/Alpha",
                    "Broadcast",
                    recentTracedDeliveryCount: 3
                )
            );

            Assert.That(
                summary,
                Does.Contain("Busiest trace message: AlphaMessage (3 deliveries)")
            );
        }

        [Test]
        public void BuildGraphUiRendersTracePathBusiestTargetSummaryUsesTargetPathTieBreaker()
        {
            string summary = RenderTracePathsSummary(
                new FlowGraphTracePath(
                    "SharedMessage",
                    "source: { Id = 9 }",
                    "component:beta",
                    "Root/Beta",
                    "Broadcast",
                    recentTracedDeliveryCount: 3
                ),
                new FlowGraphTracePath(
                    "SharedMessage",
                    "source: { Id = 7 }",
                    "component:alpha",
                    "Root/Alpha",
                    "Broadcast",
                    recentTracedDeliveryCount: 3
                )
            );

            Assert.That(summary, Does.Contain("Busiest target: Root/Alpha (3 deliveries)"));
        }

        [Test]
        public void BuildGraphUiRendersTracePathBusiestTargetSummaryFromVisibleTracePaths()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "VisibleMessage",
                        "source: { Id = 7 }",
                        "component:beta",
                        "Root/Beta",
                        "Broadcast",
                        recentTracedDeliveryCount: 3
                    ),
                    new FlowGraphTracePath(
                        "HiddenMessage",
                        "source: { Id = 9 }",
                        "component:gamma",
                        "Root/Gamma",
                        "Broadcast",
                        recentTracedDeliveryCount: 10
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot, new FlowGraphViewState("Beta"));

            string summary = root.Q<VisualElement>(DxMessagingFlowGraphWindow.TracePathsName)
                .Q<Label>(DxMessagingFlowGraphWindow.TracePathsSummaryLabelName)
                .text;

            Assert.That(summary, Does.Contain("1 traced path"));
            Assert.That(summary, Does.Contain("Busiest target: Root/Beta (3 deliveries)"));
            Assert.That(summary, Does.Not.Contain("Root/Gamma"));
        }

        [Test]
        public void BuildGraphUiRendersTracePathBusiestTargetSummaryAggregatesTargetDeliveries()
        {
            string summary = RenderTracePathsSummary(
                new FlowGraphTracePath(
                    "SharedMessage",
                    "source: { Id = 7 }",
                    "component:alpha",
                    "Root/Alpha",
                    "Broadcast",
                    recentTracedDeliveryCount: 2
                ),
                new FlowGraphTracePath(
                    "OtherMessage",
                    "source: { Id = 9 }",
                    "component:alpha",
                    "Root/Alpha",
                    "Untargeted",
                    recentTracedDeliveryCount: 2
                ),
                new FlowGraphTracePath(
                    "LargeSinglePathMessage",
                    "source: { Id = 11 }",
                    "component:beta",
                    "Root/Beta",
                    "Broadcast",
                    recentTracedDeliveryCount: 3
                )
            );

            Assert.That(
                summary,
                Does.Contain("Busiest target: Root/Alpha (4 deliveries) | Share: 4/7 (57%)")
            );
        }

        [Test]
        public void BuildGraphUiRendersTracePathBusiestPathSummaryUsesNameTieBreaker()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "BetaMessage",
                        "source: { Id = 9 }",
                        "component:beta",
                        "Root/Beta",
                        "Broadcast",
                        recentTracedDeliveryCount: 3
                    ),
                    new FlowGraphTracePath(
                        "AlphaMessage",
                        "source: { Id = 7 }",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        recentTracedDeliveryCount: 3
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            VisualElement tracePaths = root.Q<VisualElement>(
                DxMessagingFlowGraphWindow.TracePathsName
            );
            string summary = tracePaths
                .Q<Label>(DxMessagingFlowGraphWindow.TracePathsSummaryLabelName)
                .text;

            Assert.That(
                summary,
                Does.Contain(
                    "Busiest path: AlphaMessage -> Root/Alpha (Broadcast, source: { Id = 7 }, 3 deliveries)"
                )
            );
        }

        [Test]
        public void BuildGraphUiRendersTracePathBusiestPathSummaryUsesTargetPathTieBreaker()
        {
            string summary = RenderTracePathsSummary(
                new FlowGraphTracePath(
                    "SharedMessage",
                    "source: { Id = 9 }",
                    "component:beta",
                    "Root/Beta",
                    "Broadcast",
                    recentTracedDeliveryCount: 3
                ),
                new FlowGraphTracePath(
                    "SharedMessage",
                    "source: { Id = 9 }",
                    "component:alpha",
                    "Root/Alpha",
                    "Broadcast",
                    recentTracedDeliveryCount: 3
                )
            );

            Assert.That(
                summary,
                Does.Contain(
                    "Busiest path: SharedMessage -> Root/Alpha (Broadcast, source: { Id = 9 }, 3 deliveries)"
                )
            );
        }

        [Test]
        public void BuildGraphUiRendersTracePathBusiestPathSummaryUsesRegistrationKindTieBreaker()
        {
            string summary = RenderTracePathsSummary(
                new FlowGraphTracePath(
                    "SharedMessage",
                    "source: { Id = 9 }",
                    "component:alpha",
                    "Root/Alpha",
                    "Targeted",
                    recentTracedDeliveryCount: 3
                ),
                new FlowGraphTracePath(
                    "SharedMessage",
                    "source: { Id = 9 }",
                    "component:alpha",
                    "Root/Alpha",
                    "Broadcast",
                    recentTracedDeliveryCount: 3
                )
            );

            Assert.That(
                summary,
                Does.Contain(
                    "Busiest path: SharedMessage -> Root/Alpha (Broadcast, source: { Id = 9 }, 3 deliveries)"
                )
            );
        }

        [Test]
        public void BuildGraphUiRendersTracePathBusiestPathSummaryUsesContextTieBreaker()
        {
            string summary = RenderTracePathsSummary(
                new FlowGraphTracePath(
                    "SharedMessage",
                    "source: { Id = 9 }",
                    "component:alpha",
                    "Root/Alpha",
                    "Broadcast",
                    recentTracedDeliveryCount: 3
                ),
                new FlowGraphTracePath(
                    "SharedMessage",
                    "source: { Id = 7 }",
                    "component:alpha",
                    "Root/Alpha",
                    "Broadcast",
                    recentTracedDeliveryCount: 3
                )
            );

            Assert.That(
                summary,
                Does.Contain(
                    "Busiest path: SharedMessage -> Root/Alpha (Broadcast, source: { Id = 7 }, 3 deliveries)"
                )
            );
        }

        [Test]
        public void BuildGraphUiRendersTracePathBusiestPathShareHandlesZeroDeliveries()
        {
            string summary = RenderTracePathsSummary(
                new FlowGraphTracePath(
                    "SharedMessage",
                    "source: { Id = 7 }",
                    "component:alpha",
                    "Root/Alpha",
                    "Broadcast",
                    recentTracedDeliveryCount: 0
                )
            );

            Assert.That(summary, Does.Contain("Busiest path share: none"));
        }

        [Test]
        public void BuildGraphUiRendersMessageDetailsFromVisibleFilteredEdges()
        {
            FlowGraphSnapshot snapshot = CreateSharedMessageSnapshot();
            FlowGraphMessageNode message = snapshot.MessageNodes[0];
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(
                root,
                snapshot,
                new FlowGraphViewState(
                    "Alpha",
                    DxMessagingFlowGraphWindow.CreateMessageSelectionKey(message)
                )
            );

            string details = root.Q<Label>(DxMessagingFlowGraphWindow.DetailsBodyLabelName).text;

            Assert.That(details, Does.Contain("Visible registrations: 1 | Calls: 4"));
            Assert.That(details, Does.Contain("Listener components: 1"));
            Assert.That(details, Does.Contain("Visible call share: 4/4 (100%)"));
            Assert.That(details, Does.Contain("Visible traced share: 0/0 (n/a)"));
            Assert.That(details, Does.Contain("Busiest traced route: none"));
            Assert.That(details, Does.Contain("Busiest traced target: none"));
            Assert.That(details, Does.Contain("Busiest target: none"));
            Assert.That(details, Does.Contain("Busiest listener: Root/Alpha (4 calls)"));
            Assert.That(details, Does.Not.Contain("Root/Beta"));
        }

        [Test]
        public void BuildGraphUiRendersSelectedComponentRouteHealthSummary()
        {
            FlowGraphSnapshot snapshot = CreateSelectedDetailsRouteHealthSnapshot();
            FlowGraphComponentNode component = snapshot.ComponentNodes[0];
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(
                root,
                snapshot,
                new FlowGraphViewState(
                    selectedItemKey: DxMessagingFlowGraphWindow.CreateComponentSelectionKey(
                        component
                    )
                )
            );

            string details = root.Q<Label>(DxMessagingFlowGraphWindow.DetailsBodyLabelName).text;

            Assert.That(details, Does.Contain("Recent traced routes: 1/2 | No-call routes: 1"));
            Assert.That(
                details,
                Does.Contain(
                    "Busiest traced route: InventoryChanged -> Root/Alpha (Untargeted) | Share: 2/2 (100%)"
                )
            );
        }

        [Test]
        public void BuildGraphUiRendersSelectedMessageRouteHealthSummary()
        {
            FlowGraphSnapshot snapshot = CreateSelectedDetailsRouteHealthSnapshot();
            FlowGraphMessageNode message = snapshot.MessageNodes[0];
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(
                root,
                snapshot,
                new FlowGraphViewState(
                    selectedItemKey: DxMessagingFlowGraphWindow.CreateMessageSelectionKey(message)
                )
            );

            string details = root.Q<Label>(DxMessagingFlowGraphWindow.DetailsBodyLabelName).text;

            Assert.That(details, Does.Contain("Recent traced routes: 2/3 | No-call routes: 1"));
            Assert.That(
                details,
                Does.Contain(
                    "Busiest traced route: InventoryChanged -> Root/Beta (Broadcast) | Share: 5/7 (71%)"
                )
            );
        }

        [Test]
        public void BuildGraphUiRendersSelectedMessageRouteHealthSummaryFromVisibleFilteredEdges()
        {
            FlowGraphSnapshot snapshot = CreateSelectedDetailsRouteHealthSnapshot();
            FlowGraphMessageNode message = snapshot.MessageNodes[0];
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(
                root,
                snapshot,
                new FlowGraphViewState(
                    "Alpha",
                    DxMessagingFlowGraphWindow.CreateMessageSelectionKey(message)
                )
            );

            string details = root.Q<Label>(DxMessagingFlowGraphWindow.DetailsBodyLabelName).text;

            Assert.That(details, Does.Contain("Recent traced routes: 1/2 | No-call routes: 1"));
            Assert.That(
                details,
                Does.Contain(
                    "Busiest traced route: InventoryChanged -> Root/Alpha (Untargeted) | Share: 2/2 (100%)"
                )
            );
            Assert.That(details, Does.Not.Contain("Recent traced routes: 2/3"));
            Assert.That(details, Does.Not.Contain("Root/Beta"));
        }

        [Test]
        public void BuildGraphUiRendersSelectedComponentTraceContextDeliveryBreakdown()
        {
            FlowGraphSnapshot snapshot = new(
                new[]
                {
                    new FlowGraphComponentNode(
                        "component:alpha",
                        "Root/Alpha",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 2,
                        callCount: 7,
                        localMessageCount: 7
                    ),
                    new FlowGraphComponentNode(
                        "component:beta",
                        "Root/Beta",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 4,
                        localMessageCount: 4
                    ),
                },
                new[]
                {
                    new FlowGraphMessageNode("ScoreChanged", 2, 7),
                    new FlowGraphMessageNode("InventoryChanged", 1, 4),
                },
                new[]
                {
                    new FlowGraphEdge(
                        "ScoreChanged",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        registrationCount: 1,
                        callCount: 5,
                        recentTracedDeliveryCount: 6
                    ),
                    new FlowGraphEdge(
                        "InventoryChanged",
                        "component:alpha",
                        "Root/Alpha",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 2,
                        recentTracedDeliveryCount: 2
                    ),
                    new FlowGraphEdge(
                        "ScoreChanged",
                        "component:beta",
                        "Root/Beta",
                        "Broadcast",
                        registrationCount: 1,
                        callCount: 4,
                        recentTracedDeliveryCount: 6
                    ),
                },
                new[]
                {
                    new FlowGraphTracePath(
                        "ScoreChanged",
                        "source: { Id = 42 }",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        recentTracedDeliveryCount: 3,
                        traceIds: new long[] { 205, 101 }
                    ),
                    new FlowGraphTracePath(
                        "InventoryChanged",
                        string.Empty,
                        "component:alpha",
                        "Root/Alpha",
                        "Untargeted",
                        recentTracedDeliveryCount: 2,
                        traceIds: new long[] { 205 }
                    ),
                    new FlowGraphTracePath(
                        "ScoreChanged",
                        "source: { Id = 42 }",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        recentTracedDeliveryCount: 2,
                        traceIds: new long[] { 101 }
                    ),
                    new FlowGraphTracePath(
                        "ScoreChanged",
                        "source: { Id = 99 }",
                        "component:beta",
                        "Root/Beta",
                        "Broadcast",
                        recentTracedDeliveryCount: 4,
                        traceIds: new long[] { 301, 302 }
                    ),
                },
                Array.Empty<string>()
            );
            FlowGraphComponentNode component = snapshot.ComponentNodes[0];
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(
                root,
                snapshot,
                new FlowGraphViewState(
                    selectedItemKey: DxMessagingFlowGraphWindow.CreateComponentSelectionKey(
                        component
                    )
                )
            );

            string details = root.Q<Label>(DxMessagingFlowGraphWindow.DetailsBodyLabelName).text;

            Assert.That(details, Does.Contain("Recent trace paths: 3"));
            Assert.That(details, Does.Contain("Traced deliveries: 7"));
            Assert.That(details, Does.Contain("Visible traced share: 8/14 (57%)"));
            Assert.That(
                details,
                Does.Contain("Busiest traced message: ScoreChanged | Share: 6/8 (75%)")
            );
            Assert.That(
                details,
                Does.Contain(
                    "Busiest trace message: ScoreChanged (5 deliveries) | Share: 5/7 (71%)"
                )
            );
            Assert.That(details, Does.Contain("Trace ids: 2 | Widest trace: 101 (2 paths)"));
            Assert.That(
                details,
                Does.Contain("Recent trace contexts: <none>, source: { Id = 42 }")
            );
            Assert.That(
                details,
                Does.Contain("Contexts: 2 | Busiest context: source: { Id = 42 } (5)")
            );
            Assert.That(
                details,
                Does.Contain("Trace context deliveries: source: { Id = 42 } (5), <none> (2)")
            );
            Assert.That(
                details,
                Does.Contain("Busiest context share: source: { Id = 42 } | Share: 5/7 (71%)")
            );
            Assert.That(
                details,
                Does.Contain(
                    "Busiest path: ScoreChanged -> Root/Alpha (Broadcast, source: { Id = 42 }, 3 deliveries)"
                )
            );
            Assert.That(details, Does.Contain("Busiest path share: 3/7 (43%)"));
            Assert.That(details, Does.Not.Contain("source: { Id = 99 }"));
        }

        [Test]
        public void BuildGraphUiRendersSelectedComponentTraceIdBreadthFromVisibleFilteredTracePaths()
        {
            FlowGraphSnapshot snapshot = new(
                new[]
                {
                    new FlowGraphComponentNode(
                        "component:alpha",
                        "Root/Alpha",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 3,
                        localMessageCount: 3
                    ),
                },
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[]
                {
                    new FlowGraphTracePath(
                        "ScoreChanged",
                        "source: { Id = 101 }",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        recentTracedDeliveryCount: 3,
                        traceIds: new long[] { 101 }
                    ),
                    new FlowGraphTracePath(
                        "ScoreChanged",
                        "source: { Id = 301 }",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        recentTracedDeliveryCount: 5,
                        traceIds: new long[] { 301 }
                    ),
                    new FlowGraphTracePath(
                        "ScoreChanged",
                        "source: { Id = 302 }",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        recentTracedDeliveryCount: 4,
                        traceIds: new long[] { 301 }
                    ),
                },
                Array.Empty<string>()
            );
            FlowGraphComponentNode component = snapshot.ComponentNodes[0];
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(
                root,
                snapshot,
                new FlowGraphViewState(
                    "101",
                    DxMessagingFlowGraphWindow.CreateComponentSelectionKey(component)
                )
            );

            string details = root.Q<Label>(DxMessagingFlowGraphWindow.DetailsBodyLabelName).text;

            Assert.That(details, Does.Contain("Recent trace paths: 1"));
            Assert.That(
                details,
                Does.Contain("Busiest trace message: ScoreChanged (3 deliveries)")
            );
            Assert.That(details, Does.Contain("Trace ids: 1 | Widest trace: 101 (1 path)"));
            Assert.That(details, Does.Not.Contain("Widest trace: 301 (2 paths)"));
            Assert.That(details, Does.Not.Contain("source: { Id = 301 }"));
            Assert.That(details, Does.Not.Contain("source: { Id = 302 }"));
        }

        [Test]
        public void BuildGraphUiRendersSelectedMessageTraceTargetFromVisibleFilteredTracePaths()
        {
            FlowGraphSnapshot snapshot = new(
                new[]
                {
                    new FlowGraphComponentNode(
                        "component:alpha",
                        "Root/Alpha",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 5,
                        localMessageCount: 5
                    ),
                    new FlowGraphComponentNode(
                        "component:beta",
                        "Root/Beta",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 8,
                        localMessageCount: 8
                    ),
                },
                new[] { new FlowGraphMessageNode("ScoreChanged", 2, 13) },
                new[]
                {
                    new FlowGraphEdge(
                        "ScoreChanged",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        registrationCount: 1,
                        callCount: 5,
                        recentTracedDeliveryCount: 5
                    ),
                    new FlowGraphEdge(
                        "ScoreChanged",
                        "component:beta",
                        "Root/Beta",
                        "Broadcast",
                        registrationCount: 1,
                        callCount: 8,
                        recentTracedDeliveryCount: 8
                    ),
                },
                new[]
                {
                    new FlowGraphTracePath(
                        "ScoreChanged",
                        "source: { Id = 41 }",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        recentTracedDeliveryCount: 2,
                        traceIds: new long[] { 101 }
                    ),
                    new FlowGraphTracePath(
                        "ScoreChanged",
                        "source: { Id = 42 }",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        recentTracedDeliveryCount: 3,
                        traceIds: new long[] { 102 }
                    ),
                    new FlowGraphTracePath(
                        "ScoreChanged",
                        "source: { Id = 99 }",
                        "component:beta",
                        "Root/Beta",
                        "Broadcast",
                        recentTracedDeliveryCount: 8,
                        traceIds: new long[] { 301 }
                    ),
                },
                Array.Empty<string>()
            );
            FlowGraphMessageNode message = snapshot.MessageNodes[0];
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(
                root,
                snapshot,
                new FlowGraphViewState(
                    "Id = 4",
                    DxMessagingFlowGraphWindow.CreateMessageSelectionKey(message)
                )
            );

            string details = root.Q<Label>(DxMessagingFlowGraphWindow.DetailsBodyLabelName).text;

            Assert.That(
                details,
                Does.Contain("Recent trace contexts: source: { Id = 41 }, source: { Id = 42 }")
            );
            Assert.That(details, Does.Contain("Trace-path deliveries: 5"));
            Assert.That(
                details,
                Does.Contain("Busiest target: Root/Alpha (5 deliveries) | Share: 5/5 (100%)")
            );
            Assert.That(details, Does.Not.Contain("Root/Beta (8 deliveries)"));
            Assert.That(details, Does.Not.Contain("source: { Id = 99 }"));
        }

        [Test]
        public void BuildGraphUiRendersSelectedMessageTraceContextDeliveryBreakdown()
        {
            FlowGraphSnapshot snapshot = new(
                new[]
                {
                    new FlowGraphComponentNode(
                        "component:alpha",
                        "Root/Alpha",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 5,
                        localMessageCount: 5
                    ),
                    new FlowGraphComponentNode(
                        "component:beta",
                        "Root/Beta",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 2,
                        localMessageCount: 2
                    ),
                    new FlowGraphComponentNode(
                        "component:gamma",
                        "Root/Gamma",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 4,
                        localMessageCount: 4
                    ),
                },
                new[]
                {
                    new FlowGraphMessageNode("ScoreChanged", 2, 7),
                    new FlowGraphMessageNode("InventoryChanged", 1, 4),
                },
                new[]
                {
                    new FlowGraphEdge(
                        "ScoreChanged",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        registrationCount: 1,
                        callCount: 5,
                        recentTracedDeliveryCount: 6
                    ),
                    new FlowGraphEdge(
                        "ScoreChanged",
                        "component:beta",
                        "Root/Beta",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 2,
                        recentTracedDeliveryCount: 2
                    ),
                    new FlowGraphEdge(
                        "InventoryChanged",
                        "component:gamma",
                        "Root/Gamma",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 4,
                        recentTracedDeliveryCount: 6
                    ),
                },
                new[]
                {
                    new FlowGraphTracePath(
                        "ScoreChanged",
                        "source: { Id = 42 }",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        recentTracedDeliveryCount: 3,
                        traceIds: new long[] { 205, 101 }
                    ),
                    new FlowGraphTracePath(
                        "ScoreChanged",
                        string.Empty,
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        recentTracedDeliveryCount: 2,
                        traceIds: new long[] { 205 }
                    ),
                    new FlowGraphTracePath(
                        "ScoreChanged",
                        "source: { Id = 42 }",
                        "component:beta",
                        "Root/Beta",
                        "Untargeted",
                        recentTracedDeliveryCount: 2,
                        traceIds: new long[] { 101 }
                    ),
                    new FlowGraphTracePath(
                        "InventoryChanged",
                        "source: { Id = 99 }",
                        "component:gamma",
                        "Root/Gamma",
                        "Untargeted",
                        recentTracedDeliveryCount: 4,
                        traceIds: new long[] { 301 }
                    ),
                },
                Array.Empty<string>()
            );
            FlowGraphMessageNode message = snapshot.MessageNodes[0];
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(
                root,
                snapshot,
                new FlowGraphViewState(
                    selectedItemKey: DxMessagingFlowGraphWindow.CreateMessageSelectionKey(message)
                )
            );

            string details = root.Q<Label>(DxMessagingFlowGraphWindow.DetailsBodyLabelName).text;

            Assert.That(
                details,
                Does.Contain("Recent trace contexts: <none>, source: { Id = 42 }")
            );
            Assert.That(details, Does.Contain("Trace-path deliveries: 7"));
            Assert.That(
                details,
                Does.Contain("Contexts: 2 | Busiest context: source: { Id = 42 } (5)")
            );
            Assert.That(details, Does.Contain("Visible traced share: 8/14 (57%)"));
            Assert.That(
                details,
                Does.Contain("Busiest traced target: Root/Alpha | Share: 6/8 (75%)")
            );
            Assert.That(
                details,
                Does.Contain("Busiest target: Root/Alpha (5 deliveries) | Share: 5/7 (71%)")
            );
            Assert.That(details, Does.Contain("Trace ids: 2 | Widest trace: 101 (2 paths)"));
            Assert.That(
                details,
                Does.Contain("Trace context deliveries: source: { Id = 42 } (5), <none> (2)")
            );
            Assert.That(
                details,
                Does.Contain("Busiest context share: source: { Id = 42 } | Share: 5/7 (71%)")
            );
            Assert.That(
                details,
                Does.Contain(
                    "Busiest path: ScoreChanged -> Root/Alpha (Broadcast, source: { Id = 42 }, 3 deliveries)"
                )
            );
            Assert.That(details, Does.Contain("Busiest path share: 3/7 (43%)"));
            Assert.That(details, Does.Not.Contain("source: { Id = 99 }"));
        }

        [Test]
        public void BuildGraphUiRendersSelectedRouteTraceContextDeliveryBreakdown()
        {
            FlowGraphSnapshot snapshot = new(
                new[]
                {
                    new FlowGraphComponentNode(
                        "component:beta",
                        "Root/Beta",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 4,
                        localMessageCount: 4
                    ),
                },
                new[] { new FlowGraphMessageNode("ScoreChanged", 2, 5) },
                new[]
                {
                    new FlowGraphEdge(
                        "ScoreChanged",
                        "component:beta",
                        "Root/Beta",
                        "Broadcast",
                        registrationCount: 1,
                        callCount: 4,
                        recentTracedDeliveryCount: 6
                    ),
                    new FlowGraphEdge(
                        "ScoreChanged",
                        "component:beta",
                        "Root/Beta",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 1,
                        recentTracedDeliveryCount: 2
                    ),
                },
                new[]
                {
                    new FlowGraphTracePath(
                        "ScoreChanged",
                        "source: { Id = 42 }",
                        "component:beta",
                        "Root/Beta",
                        "Broadcast",
                        recentTracedDeliveryCount: 3,
                        traceIds: new long[] { 205, 101 }
                    ),
                    new FlowGraphTracePath(
                        "ScoreChanged",
                        "source: { Id = 7 }",
                        "component:beta",
                        "Root/Beta",
                        "Broadcast",
                        recentTracedDeliveryCount: 1,
                        traceIds: new long[] { 205 }
                    ),
                    new FlowGraphTracePath(
                        "ScoreChanged",
                        string.Empty,
                        "component:beta",
                        "Root/Beta",
                        "Broadcast",
                        recentTracedDeliveryCount: 2,
                        traceIds: new long[] { 101 }
                    ),
                    new FlowGraphTracePath(
                        "ScoreChanged",
                        "source: { Id = 99 }",
                        "component:beta",
                        "Root/Beta",
                        "Untargeted",
                        recentTracedDeliveryCount: 1,
                        traceIds: new long[] { 301 }
                    ),
                },
                Array.Empty<string>()
            );
            FlowGraphEdge edge = snapshot.Edges[0];
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(
                root,
                snapshot,
                new FlowGraphViewState(
                    selectedItemKey: DxMessagingFlowGraphWindow.CreateEdgeSelectionKey(edge)
                )
            );

            string details = root.Q<Label>(DxMessagingFlowGraphWindow.DetailsBodyLabelName).text;

            Assert.That(
                details,
                Does.Contain("Contexts: <none>, source: { Id = 42 }, source: { Id = 7 }")
            );
            Assert.That(
                details,
                Does.Contain("Contexts: 3 | Busiest context: source: { Id = 42 } (3)")
            );
            Assert.That(details, Does.Contain("Trace ids: 2 | Widest trace: 101 (2 paths)"));
            Assert.That(
                details,
                Does.Contain(
                    "Trace context deliveries: source: { Id = 42 } (3), <none> (2), source: { Id = 7 } (1)"
                )
            );
            Assert.That(
                details,
                Does.Contain("Busiest context share: source: { Id = 42 } | Share: 3/6 (50%)")
            );
            Assert.That(details, Does.Contain("Visible traced share: 6/8 (75%)"));
            Assert.That(
                details,
                Does.Contain(
                    "Busiest path: ScoreChanged -> Root/Beta (Broadcast, source: { Id = 42 }, 3 deliveries)"
                )
            );
            Assert.That(details, Does.Contain("Busiest path share: 3/6 (50%)"));
            Assert.That(details, Does.Not.Contain("source: { Id = 99 }"));
        }

        [Test]
        public void BuildGraphUiRendersSelectedRouteTracedShareHandlesZeroVisibleDeliveries()
        {
            FlowGraphSnapshot snapshot = CreateTwoEdgeSnapshot();
            FlowGraphEdge edge = snapshot.Edges[0];
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(
                root,
                snapshot,
                new FlowGraphViewState(
                    selectedItemKey: DxMessagingFlowGraphWindow.CreateEdgeSelectionKey(edge)
                )
            );

            string details = root.Q<Label>(DxMessagingFlowGraphWindow.DetailsBodyLabelName).text;

            Assert.That(details, Does.Contain("Visible traced share: 0/0 (n/a)"));
            Assert.That(details, Does.Contain("Contexts: 0 | Busiest context: none"));
            Assert.That(details, Does.Contain("Busiest context share: none"));
        }

        [Test]
        public void BuildGraphUiRendersRouteMapFromVisibleEdges()
        {
            FlowGraphSnapshot snapshot = CreateTwoEdgeSnapshot();
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot, new FlowGraphViewState("Beta"));

            VisualElement routeMap = root.Q<VisualElement>(DxMessagingFlowGraphWindow.RouteMapName);
            Assert.That(routeMap, Is.Not.Null);

            List<VisualElement> routeRows = routeMap
                .Query<VisualElement>(className: DxMessagingFlowGraphWindow.RouteMapRouteClassName)
                .ToList();
            Assert.That(routeRows.Count, Is.EqualTo(1));
            Assert.That(
                routeRows[0].Q<Label>(DxMessagingFlowGraphWindow.RouteMapMessageLabelName).text,
                Does.Contain("ScoreChanged")
            );
            Assert.That(
                routeRows[0].Q<Label>(DxMessagingFlowGraphWindow.RouteMapTargetLabelName).text,
                Does.Contain("Root/Beta")
            );
            Assert.That(
                routeRows[0].Q<Label>(DxMessagingFlowGraphWindow.RouteMapSummaryLabelName).text,
                Does.Contain("Share: 2/2 (100%)")
            );
            Assert.That(
                routeMap.Q<Label>(DxMessagingFlowGraphWindow.RouteMapSummaryLabelName).text,
                Does.Contain("1 visible route | 1 message | 1 listener")
            );
            Assert.That(
                routeMap.Q<Label>(DxMessagingFlowGraphWindow.RouteMapSummaryLabelName).text,
                Does.Contain("Route kinds: Untargeted 1")
            );
            Assert.That(
                routeMap.Q<Label>(DxMessagingFlowGraphWindow.RouteMapSummaryLabelName).text,
                Does.Contain(
                    "Hottest route: ScoreChanged -> Root/Beta (Untargeted) | Share: 2/2 (100%)"
                )
            );
            Assert.That(
                routeMap.Q<Label>(DxMessagingFlowGraphWindow.RouteMapSummaryLabelName).text,
                Does.Contain("Widest message: ScoreChanged (1 target component, 2 calls)")
            );
            Assert.That(
                routeMap.Q<Label>(DxMessagingFlowGraphWindow.RouteMapSummaryLabelName).text,
                Does.Contain("Most-routed target: Root/Beta (1 route, 2 calls)")
            );
            Assert.That(
                routeRows[0].Q<Label>(DxMessagingFlowGraphWindow.RouteMapMessageLabelName).text,
                Does.Not.Contain("InventoryChanged")
            );
        }

        [Test]
        public void BuildGraphUiRendersRouteMapHottestRouteSummary()
        {
            FlowGraphSnapshot snapshot = CreateTwoEdgeSnapshot();
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            VisualElement routeMap = root.Q<VisualElement>(DxMessagingFlowGraphWindow.RouteMapName);
            string summary = routeMap
                .Q<Label>(DxMessagingFlowGraphWindow.RouteMapSummaryLabelName)
                .text;

            Assert.That(
                summary,
                Does.Contain(
                    "Hottest route: InventoryChanged -> Root/Alpha (Untargeted) | Share: 4/6 (67%)"
                )
            );
        }

        [Test]
        public void BuildGraphUiRendersRouteMapRegistrationKindMixSummary()
        {
            FlowGraphSnapshot snapshot = CreateMixedRouteKindSnapshot();

            string summary = RenderRouteMapSummary(snapshot);

            Assert.That(
                summary,
                Does.Contain("Route kinds: Broadcast 2, Targeted 1, Untargeted 1")
            );
        }

        [Test]
        public void BuildGraphUiRendersRouteMapRegistrationKindMixFromVisibleRegistrationKinds()
        {
            FlowGraphSnapshot snapshot = CreateMixedRouteKindSnapshot();
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(
                root,
                snapshot,
                new FlowGraphViewState("Broadcast")
            );

            VisualElement routeMap = root.Q<VisualElement>(DxMessagingFlowGraphWindow.RouteMapName);
            string summary = routeMap
                .Q<Label>(DxMessagingFlowGraphWindow.RouteMapSummaryLabelName)
                .text;
            List<VisualElement> routeRows = routeMap
                .Query<VisualElement>(className: DxMessagingFlowGraphWindow.RouteMapRouteClassName)
                .ToList();

            Assert.That(routeRows.Count, Is.EqualTo(2));
            Assert.That(summary, Does.Contain("2 visible routes"));
            Assert.That(summary, Does.Contain("Route kinds: Broadcast 2"));
            Assert.That(summary, Does.Not.Contain("Targeted 1"));
            Assert.That(summary, Does.Not.Contain("Untargeted 1"));
        }

        [Test]
        public void BuildGraphUiRendersRouteMapMostRoutedTargetSummary()
        {
            FlowGraphSnapshot snapshot = CreateMostRoutedTargetSnapshot();

            string summary = RenderRouteMapSummary(snapshot);

            Assert.That(
                summary,
                Does.Contain("Most-routed target: Root/Alpha (2 routes, 2 calls)")
            );
        }

        [Test]
        public void BuildGraphUiRendersRouteMapMostRoutedTargetSummaryUsesCallTieBreaker()
        {
            FlowGraphSnapshot snapshot = CreateMixedRouteKindSnapshot();

            string summary = RenderRouteMapSummary(snapshot);

            Assert.That(
                summary,
                Does.Contain("Most-routed target: Root/Alpha (2 routes, 6 calls)")
            );
        }

        [Test]
        public void BuildGraphUiRendersRouteMapMostRoutedTargetSummaryUsesPathTieBreaker()
        {
            FlowGraphSnapshot snapshot = CreateMostRoutedTargetPathTieSnapshot();

            string summary = RenderRouteMapSummary(snapshot);

            Assert.That(summary, Does.Contain("Most-routed target: Root/Alpha (1 route, 2 calls)"));
        }

        [Test]
        public void BuildGraphUiRendersRouteMapInactiveRoutedTargetSummary()
        {
            FlowGraphSnapshot snapshot = CreateInactiveRoutedTargetSnapshot();

            string summary = RenderRouteMapSummary(snapshot);

            Assert.That(summary, Does.Contain("Inactive routed targets: 1/2"));
        }

        [Test]
        public void BuildGraphUiRendersRouteMapInactiveRoutedTargetSummaryFromVisibleEdges()
        {
            FlowGraphSnapshot snapshot = CreateInactiveRoutedTargetSnapshot();
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot, new FlowGraphViewState("Beta"));

            VisualElement routeMap = root.Q<VisualElement>(DxMessagingFlowGraphWindow.RouteMapName);
            string summary = routeMap
                .Q<Label>(DxMessagingFlowGraphWindow.RouteMapSummaryLabelName)
                .text;

            Assert.That(summary, Does.Contain("1 visible route"));
            Assert.That(summary, Does.Contain("Inactive routed targets: 1/1"));
            Assert.That(summary, Does.Not.Contain("Inactive routed targets: 1/2"));
        }

        [Test]
        public void BuildGraphUiRendersRouteMapRecentTracedRouteCoverageSummary()
        {
            FlowGraphSnapshot snapshot = CreateRecentTracedRouteCoverageSnapshot();

            string summary = RenderRouteMapSummary(snapshot);

            Assert.That(summary, Does.Contain("Recent traced routes: 1/2"));
            Assert.That(
                summary,
                Does.Contain(
                    "Busiest traced route: InventoryChanged -> Root/Alpha (Untargeted) | Share: 3/3 (100%)"
                )
            );
            Assert.That(
                summary,
                Does.Contain("Busiest traced message: InventoryChanged | Share: 3/3 (100%)")
            );
            Assert.That(
                summary,
                Does.Contain("Busiest traced target: Root/Alpha | Share: 3/3 (100%)")
            );
        }

        [Test]
        public void BuildGraphUiRendersRouteMapRecentTracedRouteCoverageSummaryFromVisibleEdges()
        {
            FlowGraphSnapshot snapshot = CreateRecentTracedRouteCoverageSnapshot();
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot, new FlowGraphViewState("Beta"));

            VisualElement routeMap = root.Q<VisualElement>(DxMessagingFlowGraphWindow.RouteMapName);
            string summary = routeMap
                .Q<Label>(DxMessagingFlowGraphWindow.RouteMapSummaryLabelName)
                .text;

            Assert.That(summary, Does.Contain("1 visible route"));
            Assert.That(summary, Does.Contain("Recent traced routes: 0/1"));
            Assert.That(summary, Does.Contain("Busiest traced route: none"));
            Assert.That(summary, Does.Contain("Busiest traced message: none"));
            Assert.That(summary, Does.Contain("Busiest traced target: none"));
            Assert.That(summary, Does.Not.Contain("Recent traced routes: 1/2"));
            Assert.That(
                summary,
                Does.Not.Contain("Busiest traced route: InventoryChanged -> Root/Alpha")
            );
        }

        [Test]
        public void BuildGraphUiRendersRouteMapBusiestTracedMessageAggregatesVisibleEdges()
        {
            FlowGraphSnapshot baseSnapshot = CreateTwoEdgeSnapshot();
            FlowGraphSnapshot snapshot = new(
                baseSnapshot.ComponentNodes,
                new[]
                {
                    new FlowGraphMessageNode("AlphaMessage", 2, 5, recentTracedDeliveryCount: 5),
                    new FlowGraphMessageNode("BetaMessage", 1, 4, recentTracedDeliveryCount: 4),
                },
                new[]
                {
                    new FlowGraphEdge(
                        "AlphaMessage",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        registrationCount: 1,
                        callCount: 2,
                        recentTracedDeliveryCount: 2
                    ),
                    new FlowGraphEdge(
                        "AlphaMessage",
                        "component:beta",
                        "Root/Beta",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 6,
                        recentTracedDeliveryCount: 3
                    ),
                    new FlowGraphEdge(
                        "BetaMessage",
                        "component:beta",
                        "Root/Beta",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 5,
                        recentTracedDeliveryCount: 0
                    ),
                    new FlowGraphEdge(
                        "BetaMessage",
                        "component:alpha",
                        "Root/Alpha",
                        "Targeted",
                        registrationCount: 1,
                        callCount: 4,
                        recentTracedDeliveryCount: 4
                    ),
                },
                Array.Empty<string>()
            );

            string summary = RenderRouteMapSummary(snapshot);

            Assert.That(
                summary,
                Does.Contain("Most-routed target: Root/Beta (2 routes, 11 calls)")
            );
            Assert.That(
                summary,
                Does.Contain(
                    "Busiest traced route: BetaMessage -> Root/Alpha (Targeted) | Share: 4/9 (44%)"
                )
            );
            Assert.That(
                summary,
                Does.Contain("Busiest traced message: AlphaMessage | Share: 5/9 (56%)")
            );
            Assert.That(
                summary,
                Does.Contain("Busiest traced target: Root/Alpha | Share: 6/9 (67%)")
            );
        }

        [Test]
        public void BuildGraphUiRendersRouteMapBusiestTracedTargetUsesPathTieBreaker()
        {
            FlowGraphSnapshot baseSnapshot = CreateTwoEdgeSnapshot();
            FlowGraphSnapshot snapshot = new(
                baseSnapshot.ComponentNodes,
                new[] { new FlowGraphMessageNode("SharedMessage", 2, 2) },
                new[]
                {
                    new FlowGraphEdge(
                        "SharedMessage",
                        "component:beta",
                        "Root/Beta",
                        "Broadcast",
                        registrationCount: 1,
                        callCount: 1,
                        recentTracedDeliveryCount: 3
                    ),
                    new FlowGraphEdge(
                        "SharedMessage",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        registrationCount: 1,
                        callCount: 1,
                        recentTracedDeliveryCount: 3
                    ),
                },
                Array.Empty<string>()
            );

            string summary = RenderRouteMapSummary(snapshot);

            Assert.That(
                summary,
                Does.Contain("Busiest traced target: Root/Alpha | Share: 3/6 (50%)")
            );
        }

        [Test]
        public void BuildGraphUiRendersRouteMapBusiestTracedRouteUsesMessageNameTieBreaker()
        {
            FlowGraphSnapshot baseSnapshot = CreateTwoEdgeSnapshot();
            FlowGraphSnapshot snapshot = new(
                baseSnapshot.ComponentNodes,
                new[]
                {
                    new FlowGraphMessageNode("AlphaMessage", 1, 1, recentTracedDeliveryCount: 3),
                    new FlowGraphMessageNode("BetaMessage", 1, 1, recentTracedDeliveryCount: 3),
                },
                new[]
                {
                    new FlowGraphEdge(
                        "BetaMessage",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        registrationCount: 1,
                        callCount: 1,
                        recentTracedDeliveryCount: 3
                    ),
                    new FlowGraphEdge(
                        "AlphaMessage",
                        "component:beta",
                        "Root/Beta",
                        "Broadcast",
                        registrationCount: 1,
                        callCount: 1,
                        recentTracedDeliveryCount: 3
                    ),
                },
                Array.Empty<string>()
            );

            string summary = RenderRouteMapSummary(snapshot);

            Assert.That(
                summary,
                Does.Contain(
                    "Busiest traced route: AlphaMessage -> Root/Beta (Broadcast) | Share: 3/6 (50%)"
                )
            );
            Assert.That(
                summary,
                Does.Contain("Busiest traced message: AlphaMessage | Share: 3/6 (50%)")
            );
        }

        [Test]
        public void BuildGraphUiRendersRouteMapBusiestTracedRouteUsesTargetPathTieBreaker()
        {
            FlowGraphSnapshot baseSnapshot = CreateTwoEdgeSnapshot();
            FlowGraphSnapshot snapshot = new(
                baseSnapshot.ComponentNodes,
                new[]
                {
                    new FlowGraphMessageNode("SharedMessage", 2, 2, recentTracedDeliveryCount: 6),
                },
                new[]
                {
                    new FlowGraphEdge(
                        "SharedMessage",
                        "component:beta",
                        "Root/Beta",
                        "Broadcast",
                        registrationCount: 1,
                        callCount: 1,
                        recentTracedDeliveryCount: 3
                    ),
                    new FlowGraphEdge(
                        "SharedMessage",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        registrationCount: 1,
                        callCount: 1,
                        recentTracedDeliveryCount: 3
                    ),
                },
                Array.Empty<string>()
            );

            string summary = RenderRouteMapSummary(snapshot);

            Assert.That(
                summary,
                Does.Contain(
                    "Busiest traced route: SharedMessage -> Root/Alpha (Broadcast) | Share: 3/6 (50%)"
                )
            );
        }

        [Test]
        public void BuildGraphUiRendersRouteMapBusiestTracedRouteUsesRegistrationKindTieBreaker()
        {
            FlowGraphSnapshot baseSnapshot = CreateTwoEdgeSnapshot();
            FlowGraphSnapshot snapshot = new(
                baseSnapshot.ComponentNodes,
                new[]
                {
                    new FlowGraphMessageNode("SharedMessage", 2, 2, recentTracedDeliveryCount: 6),
                },
                new[]
                {
                    new FlowGraphEdge(
                        "SharedMessage",
                        "component:alpha",
                        "Root/Alpha",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 1,
                        recentTracedDeliveryCount: 3
                    ),
                    new FlowGraphEdge(
                        "SharedMessage",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        registrationCount: 1,
                        callCount: 1,
                        recentTracedDeliveryCount: 3
                    ),
                },
                Array.Empty<string>()
            );

            string summary = RenderRouteMapSummary(snapshot);

            Assert.That(
                summary,
                Does.Contain(
                    "Busiest traced route: SharedMessage -> Root/Alpha (Broadcast) | Share: 3/6 (50%)"
                )
            );
        }

        [Test]
        public void BuildGraphUiRendersRouteMapWidestMessageSummary()
        {
            FlowGraphSnapshot snapshot = CreateSharedMessageSnapshot();
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            VisualElement routeMap = root.Q<VisualElement>(DxMessagingFlowGraphWindow.RouteMapName);
            string summary = routeMap
                .Q<Label>(DxMessagingFlowGraphWindow.RouteMapSummaryLabelName)
                .text;

            Assert.That(
                summary,
                Does.Contain("Widest message: SharedMessage (2 target components, 6 calls)")
            );
        }

        [Test]
        public void BuildGraphUiRendersRouteMapWidestMessageSummaryCountsDistinctTargetComponents()
        {
            FlowGraphSnapshot baseSnapshot = CreateTwoEdgeSnapshot();
            FlowGraphSnapshot snapshot = new(
                baseSnapshot.ComponentNodes,
                new[]
                {
                    new FlowGraphMessageNode("BroaderMessage", 2, 2),
                    new FlowGraphMessageNode("DuplicateTargetMessage", 2, 6),
                },
                new[]
                {
                    new FlowGraphEdge(
                        "DuplicateTargetMessage",
                        "component:alpha",
                        "Root/Alpha",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 4
                    ),
                    new FlowGraphEdge(
                        "DuplicateTargetMessage",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        registrationCount: 1,
                        callCount: 2
                    ),
                    new FlowGraphEdge(
                        "BroaderMessage",
                        "component:alpha",
                        "Root/Alpha",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 1
                    ),
                    new FlowGraphEdge(
                        "BroaderMessage",
                        "component:beta",
                        "Root/Beta",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 1
                    ),
                },
                Array.Empty<string>()
            );

            string summary = RenderRouteMapSummary(snapshot);

            Assert.That(
                summary,
                Does.Contain("Widest message: BroaderMessage (2 target components, 2 calls)")
            );
        }

        [Test]
        public void BuildGraphUiRendersRouteMapWidestMessageSummaryUsesCallCountTieBreaker()
        {
            FlowGraphSnapshot baseSnapshot = CreateTwoEdgeSnapshot();
            FlowGraphSnapshot snapshot = new(
                baseSnapshot.ComponentNodes,
                new[]
                {
                    new FlowGraphMessageNode("AlphaMessage", 2, 2),
                    new FlowGraphMessageNode("BetaMessage", 2, 6),
                },
                new[]
                {
                    new FlowGraphEdge(
                        "AlphaMessage",
                        "component:alpha",
                        "Root/Alpha",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 1
                    ),
                    new FlowGraphEdge(
                        "AlphaMessage",
                        "component:beta",
                        "Root/Beta",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 1
                    ),
                    new FlowGraphEdge(
                        "BetaMessage",
                        "component:alpha",
                        "Root/Alpha",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 4
                    ),
                    new FlowGraphEdge(
                        "BetaMessage",
                        "component:beta",
                        "Root/Beta",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 2
                    ),
                },
                Array.Empty<string>()
            );

            string summary = RenderRouteMapSummary(snapshot);

            Assert.That(
                summary,
                Does.Contain("Widest message: BetaMessage (2 target components, 6 calls)")
            );
        }

        [Test]
        public void BuildGraphUiRendersRouteMapWidestMessageSummaryUsesNameTieBreaker()
        {
            FlowGraphSnapshot baseSnapshot = CreateTwoEdgeSnapshot();
            FlowGraphSnapshot snapshot = new(
                baseSnapshot.ComponentNodes,
                new[]
                {
                    new FlowGraphMessageNode("AlphaMessage", 2, 4),
                    new FlowGraphMessageNode("BetaMessage", 2, 4),
                },
                new[]
                {
                    new FlowGraphEdge(
                        "BetaMessage",
                        "component:beta",
                        "Root/Beta",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 2
                    ),
                    new FlowGraphEdge(
                        "AlphaMessage",
                        "component:beta",
                        "Root/Beta",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 2
                    ),
                    new FlowGraphEdge(
                        "BetaMessage",
                        "component:alpha",
                        "Root/Alpha",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 2
                    ),
                    new FlowGraphEdge(
                        "AlphaMessage",
                        "component:alpha",
                        "Root/Alpha",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 2
                    ),
                },
                Array.Empty<string>()
            );

            string summary = RenderRouteMapSummary(snapshot);

            Assert.That(
                summary,
                Does.Contain("Widest message: AlphaMessage (2 target components, 4 calls)")
            );
        }

        [Test]
        public void BuildGraphUiRendersNoWidestMessageWhenNoRoutesAreVisible()
        {
            FlowGraphSnapshot baseSnapshot = CreateTwoEdgeSnapshot();
            FlowGraphSnapshot snapshot = new(
                baseSnapshot.ComponentNodes,
                baseSnapshot.MessageNodes,
                Array.Empty<FlowGraphEdge>(),
                Array.Empty<string>()
            );

            string summary = RenderRouteMapSummary(snapshot);

            Assert.That(summary, Does.Contain("Widest message: none"));
            Assert.That(summary, Does.Contain("Route kinds: none"));
            Assert.That(summary, Does.Contain("Most-routed target: none"));
            Assert.That(summary, Does.Contain("Inactive routed targets: none"));
            Assert.That(summary, Does.Contain("Recent traced routes: none"));
            Assert.That(summary, Does.Contain("Busiest traced route: none"));
            Assert.That(summary, Does.Contain("Busiest traced message: none"));
            Assert.That(summary, Does.Contain("Busiest traced target: none"));
            Assert.That(summary, Does.Contain("Busiest trace message: none"));
            Assert.That(summary, Does.Contain("Busiest target: none"));
            Assert.That(summary, Does.Contain("Busiest path: none"));
            Assert.That(summary, Does.Contain("Busiest context share: none"));
            Assert.That(summary, Does.Contain("Busiest path share: none"));
        }

        [Test]
        public void BuildGraphUiRendersNoHottestRouteWhenVisibleCallsAreZero()
        {
            FlowGraphSnapshot baseSnapshot = CreateTwoEdgeSnapshot();
            FlowGraphSnapshot snapshot = new(
                baseSnapshot.ComponentNodes,
                baseSnapshot.MessageNodes,
                new[]
                {
                    new FlowGraphEdge(
                        "InventoryChanged",
                        "component:alpha",
                        "Root/Alpha",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 0
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            VisualElement routeMap = root.Q<VisualElement>(DxMessagingFlowGraphWindow.RouteMapName);
            string summary = routeMap
                .Q<Label>(DxMessagingFlowGraphWindow.RouteMapSummaryLabelName)
                .text;

            Assert.That(summary, Does.Contain("Calls: 0"));
            Assert.That(summary, Does.Contain("Hottest route: none"));
            Assert.That(summary, Does.Not.Contain("InventoryChanged -> Root/Alpha"));
        }

        [Test]
        public void BuildGraphUiRendersRouteMapNoCallRouteSummary()
        {
            FlowGraphSnapshot baseSnapshot = CreateTwoEdgeSnapshot();
            FlowGraphSnapshot snapshot = new(
                baseSnapshot.ComponentNodes,
                baseSnapshot.MessageNodes,
                new[]
                {
                    new FlowGraphEdge(
                        "InventoryChanged",
                        "component:alpha",
                        "Root/Alpha",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 0
                    ),
                    new FlowGraphEdge(
                        "ScoreChanged",
                        "component:beta",
                        "Root/Beta",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 2
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            VisualElement routeMap = root.Q<VisualElement>(DxMessagingFlowGraphWindow.RouteMapName);
            string summary = routeMap
                .Q<Label>(DxMessagingFlowGraphWindow.RouteMapSummaryLabelName)
                .text;

            Assert.That(summary, Does.Contain("No-call routes: 1"));
            Assert.That(
                summary,
                Does.Contain("Hottest route: ScoreChanged -> Root/Beta (Untargeted)")
            );
        }

        [Test]
        public void BuildGraphUiRendersRouteMapTraceContextVolumeSummary()
        {
            FlowGraphSnapshot baseSnapshot = CreateTwoEdgeSnapshot();
            FlowGraphSnapshot snapshot = new(
                baseSnapshot.ComponentNodes,
                baseSnapshot.MessageNodes,
                new[]
                {
                    new FlowGraphEdge(
                        "InventoryChanged",
                        "component:alpha",
                        "Root/Alpha",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 4,
                        recentTracedDeliveryCount: 2
                    ),
                    new FlowGraphEdge(
                        "ScoreChanged",
                        "component:beta",
                        "Root/Beta",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 2,
                        recentTracedDeliveryCount: 7
                    ),
                },
                new[]
                {
                    new FlowGraphTracePath(
                        "InventoryChanged",
                        "source: { Id = 42 }",
                        "component:alpha",
                        "Root/Alpha",
                        "Untargeted",
                        recentTracedDeliveryCount: 2
                    ),
                    new FlowGraphTracePath(
                        "ScoreChanged",
                        "source: { Id = 42 }",
                        "component:beta",
                        "Root/Beta",
                        "Untargeted",
                        recentTracedDeliveryCount: 3
                    ),
                    new FlowGraphTracePath(
                        "ScoreChanged",
                        "source: { Id = 99 }",
                        "component:beta",
                        "Root/Beta",
                        "Untargeted",
                        recentTracedDeliveryCount: 4
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot, new FlowGraphViewState("42"));

            VisualElement routeMap = root.Q<VisualElement>(DxMessagingFlowGraphWindow.RouteMapName);
            string summary = routeMap
                .Q<Label>(DxMessagingFlowGraphWindow.RouteMapSummaryLabelName)
                .text;

            Assert.That(summary, Does.Contain("2 visible routes"));
            Assert.That(summary, Does.Contain("Recent traced: 9"));
            Assert.That(
                summary,
                Does.Contain(
                    "Busiest traced route: ScoreChanged -> Root/Beta (Untargeted) | Share: 7/9 (78%)"
                )
            );
            Assert.That(summary, Does.Contain("Contexts: 1"));
            Assert.That(summary, Does.Contain("Busiest context: source: { Id = 42 } (5)"));
            Assert.That(
                summary,
                Does.Contain("Busiest context share: source: { Id = 42 } | Share: 5/5 (100%)")
            );
            Assert.That(
                summary,
                Does.Contain(
                    "Busiest trace message: ScoreChanged (3 deliveries) | Share: 3/5 (60%)"
                )
            );
            Assert.That(
                summary,
                Does.Contain("Busiest target: Root/Beta (3 deliveries) | Share: 3/5 (60%)")
            );
            Assert.That(
                summary,
                Does.Contain(
                    "Busiest path: ScoreChanged -> Root/Beta (Untargeted, source: { Id = 42 }, 3 deliveries)"
                )
            );
            Assert.That(summary, Does.Contain("Busiest path share: 3/5 (60%)"));
            Assert.That(summary, Does.Not.Contain("source: { Id = 99 }"));
        }

        [Test]
        public void BuildGraphUiRendersRouteMapTraceIdBreadthSummaryFromVisibleTracePaths()
        {
            FlowGraphSnapshot snapshot = new(
                new[]
                {
                    new FlowGraphComponentNode(
                        "component:beta",
                        "Root/Beta",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 3,
                        localMessageCount: 3
                    ),
                    new FlowGraphComponentNode(
                        "component:gamma",
                        "Root/Gamma",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 1,
                        localMessageCount: 1
                    ),
                    new FlowGraphComponentNode(
                        "component:delta",
                        "Root/Delta",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 4,
                        localMessageCount: 4
                    ),
                },
                Array.Empty<FlowGraphMessageNode>(),
                new[]
                {
                    new FlowGraphEdge(
                        "ScoreChanged",
                        "component:beta",
                        "Root/Beta",
                        "Broadcast",
                        registrationCount: 1,
                        callCount: 3,
                        recentTracedDeliveryCount: 3
                    ),
                    new FlowGraphEdge(
                        "ScoreChanged",
                        "component:gamma",
                        "Root/Gamma",
                        "Broadcast",
                        registrationCount: 1,
                        callCount: 1,
                        recentTracedDeliveryCount: 1
                    ),
                    new FlowGraphEdge(
                        "InventoryChanged",
                        "component:delta",
                        "Root/Delta",
                        "Broadcast",
                        registrationCount: 1,
                        callCount: 4,
                        recentTracedDeliveryCount: 4
                    ),
                },
                new[]
                {
                    new FlowGraphTracePath(
                        "ScoreChanged",
                        "source: { Id = 42 }",
                        "component:beta",
                        "Root/Beta",
                        "Broadcast",
                        recentTracedDeliveryCount: 3,
                        traceIds: new long[] { 205, 101 }
                    ),
                    new FlowGraphTracePath(
                        "ScoreChanged",
                        "source: { Id = 42 }",
                        "component:gamma",
                        "Root/Gamma",
                        "Broadcast",
                        recentTracedDeliveryCount: 1,
                        traceIds: new long[] { 205 }
                    ),
                    new FlowGraphTracePath(
                        "InventoryChanged",
                        "source: { Id = 7 }",
                        "component:delta",
                        "Root/Delta",
                        "Broadcast",
                        recentTracedDeliveryCount: 4,
                        traceIds: new long[] { 301, 302 }
                    ),
                    new FlowGraphTracePath(
                        "InventoryChanged",
                        "source: { Id = 8 }",
                        "component:epsilon",
                        "Root/Epsilon",
                        "Broadcast",
                        recentTracedDeliveryCount: 2,
                        traceIds: new long[] { 301 }
                    ),
                    new FlowGraphTracePath(
                        "InventoryChanged",
                        "source: { Id = 9 }",
                        "component:zeta",
                        "Root/Zeta",
                        "Broadcast",
                        recentTracedDeliveryCount: 1,
                        traceIds: new long[] { 301 }
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(
                root,
                snapshot,
                new FlowGraphViewState("Score")
            );

            string summary = root.Q<VisualElement>(DxMessagingFlowGraphWindow.RouteMapName)
                .Q<Label>(DxMessagingFlowGraphWindow.RouteMapSummaryLabelName)
                .text;

            Assert.That(summary, Does.Contain("2 visible routes"));
            Assert.That(summary, Does.Contain("Trace ids: 2"));
            Assert.That(summary, Does.Contain("Widest trace: 205 (2 paths)"));
            Assert.That(summary, Does.Not.Contain("302"));
            Assert.That(summary, Does.Not.Contain("Widest trace: 301 (3 paths)"));
            Assert.That(summary, Does.Not.Contain("Root/Delta"));
        }

        [Test]
        public void BuildGraphUiWiresRouteMapSelectionCallback()
        {
            FlowGraphSnapshot snapshot = CreateTwoEdgeSnapshot();
            EditorWindow window = CreateTrackedEditorWindow();
            string observedSelectionKey = null;

            try
            {
                EditorWindowTestUtility.ShowWindow(window);
                VisualElement root = window.rootVisualElement;
                Action<string> onSelectionChanged = null;
                onSelectionChanged = selectedItemKey =>
                {
                    observedSelectionKey = selectedItemKey;
                    DxMessagingFlowGraphWindow.RefreshGraphContent(
                        root,
                        snapshot,
                        new FlowGraphViewState(selectedItemKey: selectedItemKey),
                        onSelectionChanged: onSelectionChanged
                    );
                };
                DxMessagingFlowGraphWindow.BuildGraphUi(
                    root,
                    snapshot,
                    FlowGraphViewState.Default,
                    onSelectionChanged: onSelectionChanged
                );

                VisualElement routeMap = root.Q<VisualElement>(
                    DxMessagingFlowGraphWindow.RouteMapName
                );
                VisualElement routeRow = routeMap
                    .Query<VisualElement>(
                        className: DxMessagingFlowGraphWindow.RouteMapRouteClassName
                    )
                    .ToList()[1];
                TextField filter = root.Q<TextField>(DxMessagingFlowGraphWindow.FilterFieldName);
                ScrollView content = root.Q<ScrollView>(DxMessagingFlowGraphWindow.ContentName);
                int childCountBeforeSelection = root.childCount;

                using (ClickEvent click = ClickEvent.GetPooled())
                {
                    click.target = routeRow;
                    routeRow.SendEvent(click);
                }

                Assert.That(
                    observedSelectionKey,
                    Is.EqualTo(DxMessagingFlowGraphWindow.CreateEdgeSelectionKey(snapshot.Edges[1]))
                );
                Assert.That(root.childCount, Is.EqualTo(childCountBeforeSelection));
                Assert.That(
                    root.Q<TextField>(DxMessagingFlowGraphWindow.FilterFieldName),
                    Is.SameAs(filter)
                );
                Assert.That(
                    root.Q<ScrollView>(DxMessagingFlowGraphWindow.ContentName),
                    Is.SameAs(content)
                );
                Assert.That(
                    root.Q<Label>(DxMessagingFlowGraphWindow.DetailsTitleLabelName).text,
                    Does.Contain("ScoreChanged -> Root/Beta")
                );
                Assert.That(
                    root.Q<VisualElement>(DxMessagingFlowGraphWindow.RouteMapName)
                        .Query<VisualElement>(
                            className: DxMessagingFlowGraphWindow.RouteMapRouteClassName
                        )
                        .ToList()[1]
                        .ClassListContains(DxMessagingFlowGraphWindow.SelectedRowClassName),
                    Is.True
                );
            }
            finally
            {
                EditorWindowTestUtility.CloseWindow(window);
            }
        }

        [Test]
        public void BuildGraphUiWiresSelectionCallbackWithoutRebuildingControls()
        {
            FlowGraphSnapshot snapshot = CreateTwoEdgeSnapshot();
            EditorWindow window = CreateTrackedEditorWindow();
            string observedSelectionKey = null;

            try
            {
                EditorWindowTestUtility.ShowWindow(window);
                VisualElement root = window.rootVisualElement;
                Action<string> onSelectionChanged = null;
                onSelectionChanged = selectedItemKey =>
                {
                    observedSelectionKey = selectedItemKey;
                    DxMessagingFlowGraphWindow.RefreshGraphContent(
                        root,
                        snapshot,
                        new FlowGraphViewState(selectedItemKey: selectedItemKey),
                        onSelectionChanged: onSelectionChanged
                    );
                };
                DxMessagingFlowGraphWindow.BuildGraphUi(
                    root,
                    snapshot,
                    FlowGraphViewState.Default,
                    onSelectionChanged: onSelectionChanged
                );

                TextField filter = root.Q<TextField>(DxMessagingFlowGraphWindow.FilterFieldName);
                ScrollView content = root.Q<ScrollView>(DxMessagingFlowGraphWindow.ContentName);
                List<VisualElement> messages = root.Query<VisualElement>(
                        className: DxMessagingFlowGraphWindow.MessageNodeClassName
                    )
                    .ToList();
                int childCountBeforeSelection = root.childCount;

                using (ClickEvent click = ClickEvent.GetPooled())
                {
                    click.target = messages[1];
                    messages[1].SendEvent(click);
                }

                Assert.That(
                    observedSelectionKey,
                    Is.EqualTo(
                        DxMessagingFlowGraphWindow.CreateMessageSelectionKey(
                            snapshot.MessageNodes[1]
                        )
                    )
                );
                Assert.That(root.childCount, Is.EqualTo(childCountBeforeSelection));
                Assert.That(
                    root.Q<TextField>(DxMessagingFlowGraphWindow.FilterFieldName),
                    Is.SameAs(filter)
                );
                Assert.That(
                    root.Q<ScrollView>(DxMessagingFlowGraphWindow.ContentName),
                    Is.SameAs(content)
                );
                Assert.That(
                    root.Q<Label>(DxMessagingFlowGraphWindow.DetailsTitleLabelName).text,
                    Does.Contain("ScoreChanged")
                );
            }
            finally
            {
                EditorWindowTestUtility.CloseWindow(window);
            }
        }

        [Test]
        public void BuildGraphUiRendersNoFilteredMatchesStateAndDisablesExport()
        {
            FlowGraphSnapshot snapshot = CreateTwoEdgeSnapshot();
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(
                root,
                snapshot,
                new FlowGraphViewState("missing"),
                onCopyExport: _ => { }
            );

            Assert.That(
                root.Query<VisualElement>(
                        className: DxMessagingFlowGraphWindow.ComponentNodeClassName
                    )
                    .ToList(),
                Is.Empty
            );
            Label emptyBody = root.Q<Label>(DxMessagingFlowGraphWindow.EmptyStateLabelName);
            Assert.That(emptyBody, Is.Not.Null);
            Assert.That(emptyBody.text, Does.Contain("No graph items match"));
            Label emptyTitle = root.Q<Label>(DxMessagingFlowGraphWindow.EmptyStateTitleLabelName);
            Assert.That(emptyTitle, Is.Not.Null);
            Assert.That(emptyTitle.text, Is.EqualTo("No matches"));
            Assert.That(
                root.Q<Button>(DxMessagingFlowGraphWindow.ExportButtonName).enabledSelf,
                Is.False
            );
        }

        [Test]
        public void BuildGraphUiExportsWarningOnlyFilteredResults()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                new[] { "Root/Listener: serialized provider missing" }
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(
                root,
                snapshot,
                new FlowGraphViewState("provider"),
                onCopyExport: _ => { }
            );

            Assert.That(root.Q<Label>(DxMessagingFlowGraphWindow.EmptyStateLabelName), Is.Null);
            Assert.That(
                root.Q<Label>(DxMessagingFlowGraphWindow.WarningLabelName).text,
                Does.Contain("serialized provider missing")
            );
            Assert.That(
                root.Q<Label>(DxMessagingFlowGraphWindow.WarningLabelName)
                    .ClassListContains(DxMessagingEditorTheme.AdmonitionClassName),
                Is.True
            );
            Assert.That(
                root.Q<Label>(DxMessagingFlowGraphWindow.WarningLabelName)
                    .ClassListContains(DxMessagingEditorTheme.WarningClassName),
                Is.True
            );
            AssertCompleteBorder(
                root.Q<Label>(DxMessagingFlowGraphWindow.WarningLabelName),
                DxMessagingEditorPalette.Amber
            );
            Assert.That(
                root.Q<Button>(DxMessagingFlowGraphWindow.ExportButtonName).enabledSelf,
                Is.True
            );

            string exportText = DxMessagingFlowGraphWindow.CreateExportText(snapshot, "provider");
            Assert.That(exportText, Does.Contain("\"componentCount\": 0"));
            Assert.That(exportText, Does.Contain("serialized provider missing"));
        }

        [Test]
        public void BuildGraphUiWiresFilterCallbackAndUpdatesExportAvailability()
        {
            FlowGraphSnapshot snapshot = CreateTwoEdgeSnapshot();
            EditorWindow window = CreateTrackedEditorWindow();
            string observedFilter = null;

            try
            {
                EditorWindowTestUtility.ShowWindow(window);
                VisualElement root = window.rootVisualElement;
                DxMessagingFlowGraphWindow.BuildGraphUi(
                    root,
                    snapshot,
                    FlowGraphViewState.Default,
                    filterText =>
                    {
                        observedFilter = filterText;
                        DxMessagingFlowGraphWindow.RefreshGraphContent(
                            root,
                            snapshot,
                            new FlowGraphViewState(filterText),
                            _ => { }
                        );
                    },
                    onCopyExport: _ => { }
                );

                TextField filter = root.Q<TextField>(DxMessagingFlowGraphWindow.FilterFieldName);
                Button export = root.Q<Button>(DxMessagingFlowGraphWindow.ExportButtonName);
                ScrollView content = root.Q<ScrollView>(DxMessagingFlowGraphWindow.ContentName);
                int childCountBeforeFilterChange = root.childCount;

                Assert.That(export.enabledSelf, Is.True);

                filter.value = "missing";

                Assert.That(observedFilter, Is.EqualTo("missing"));
                Assert.That(root.childCount, Is.EqualTo(childCountBeforeFilterChange));
                Assert.That(
                    root.Q<TextField>(DxMessagingFlowGraphWindow.FilterFieldName),
                    Is.SameAs(filter)
                );
                Assert.That(
                    root.Q<ScrollView>(DxMessagingFlowGraphWindow.ContentName),
                    Is.SameAs(content)
                );
                Assert.That(export.enabledSelf, Is.False);

                filter.value = "Beta";

                Assert.That(observedFilter, Is.EqualTo("Beta"));
                Assert.That(root.childCount, Is.EqualTo(childCountBeforeFilterChange));
                Assert.That(
                    root.Q<TextField>(DxMessagingFlowGraphWindow.FilterFieldName),
                    Is.SameAs(filter)
                );
                Assert.That(
                    root.Q<ScrollView>(DxMessagingFlowGraphWindow.ContentName),
                    Is.SameAs(content)
                );
                Assert.That(export.enabledSelf, Is.True);

                string exportText = DxMessagingFlowGraphWindow.CreateExportText(
                    snapshot,
                    filter.value
                );

                Assert.That(exportText, Does.Contain("\"edgeCount\": 1"));
                Assert.That(exportText, Does.Contain("Root/Beta"));
                Assert.That(exportText, Does.Not.Contain("Root/Alpha"));
            }
            finally
            {
                EditorWindowTestUtility.CloseWindow(window);
            }
        }

        [Test]
        public void CreateExportTextFiltersAndEscapesJsonValues()
        {
            FlowGraphSnapshot snapshot = new(
                new[]
                {
                    new FlowGraphComponentNode(
                        "component:quote",
                        "Root/Quote\"Node",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 1,
                        localMessageCount: 0
                    ),
                    new FlowGraphComponentNode(
                        "component:plain",
                        "Root/Plain",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 1,
                        localMessageCount: 0
                    ),
                },
                new[]
                {
                    new FlowGraphMessageNode("Quote\"Message", 1, 1),
                    new FlowGraphMessageNode("PlainMessage", 1, 1),
                },
                new[]
                {
                    new FlowGraphEdge(
                        "Quote\"Message",
                        "component:quote",
                        "Root/Quote\"Node",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 1
                    ),
                    new FlowGraphEdge(
                        "PlainMessage",
                        "component:plain",
                        "Root/Plain",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 1
                    ),
                },
                new[] { "Quote warning\\line\nnext\t\u0001" }
            );

            string exportText = DxMessagingFlowGraphWindow.CreateExportText(snapshot, "Quote");
            FlowGraphExportPayload exportPayload = JsonUtility.FromJson<FlowGraphExportPayload>(
                exportText
            );

            Assert.That(exportText, Does.Contain("\"componentCount\": 1"));
            Assert.That(exportText, Does.Contain("\"edgeCount\": 1"));
            Assert.That(exportText, Does.Contain("Quote\\\"Message"));
            Assert.That(exportText, Does.Contain("Quote warning\\\\line\\nnext\\t\\u0001"));
            Assert.That(exportText, Does.Not.Contain("PlainMessage"));
            Assert.That(exportText, Does.Not.Contain("\u0001"));
            Assert.That(exportPayload.componentCount, Is.EqualTo(1));
            Assert.That(exportPayload.messageCount, Is.EqualTo(1));
            Assert.That(exportPayload.edgeCount, Is.EqualTo(1));
            Assert.That(exportPayload.messages, Has.Length.EqualTo(1));
            Assert.That(exportPayload.messages[0].messageType, Is.EqualTo("Quote\"Message"));
        }

        [Test]
        public void BuildGraphUiRendersEmptyState()
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            Label emptyBody = root.Q<Label>(DxMessagingFlowGraphWindow.EmptyStateLabelName);
            Assert.That(emptyBody, Is.Not.Null);
            Assert.That(emptyBody.text, Does.Contain("No MessagingComponent registrations"));
            Assert.That(
                emptyBody.ClassListContains(DxMessagingEditorTheme.EmptyBodyClassName),
                Is.True
            );
            Assert.That(
                emptyBody.parent.ClassListContains(DxMessagingEditorTheme.EmptyClassName),
                Is.True
            );
            Label emptyTitle = root.Q<Label>(DxMessagingFlowGraphWindow.EmptyStateTitleLabelName);
            Assert.That(emptyTitle, Is.Not.Null);
            Assert.That(emptyTitle.text, Is.EqualTo("No registrations"));
            Assert.That(
                root.Query<VisualElement>(className: DxMessagingFlowGraphWindow.EdgeRowClassName)
                    .ToList(),
                Is.Empty
            );
        }

        [Test]
        public void CaptureSnapshotBuildsRegistrationEdgesFromMessagingComponents()
        {
            GameObject host = CreateTrackedObject("FlowGraphHost");
            MessagingComponent messagingComponent = host.AddComponent<MessagingComponent>();
            TestListener listener = host.AddComponent<TestListener>();
            MessageBus messageBus = MessageHandler.MessageBus as MessageBus;
            Assert.That(messageBus, Is.Not.Null);
            int initialRegistrationCount = CountMessageBusRegistrations(messageBus);

            MessageRegistrationToken token = messagingComponent.Create(listener);
            token.DiagnosticMode = true;
            token.RegisterUntargeted<FlowGraphMessage>(listener.OnFlowGraphMessage);
            token.Enable();

            messageBus.DiagnosticsMode = true;
            messageBus._emissionBuffer.Clear();

            FlowGraphMessage message = default;
            MessageHandler.MessageBus.UntargetedBroadcast(ref message);

            FlowGraphSnapshot snapshot = DxMessagingFlowGraphWindow.CaptureSnapshot(
                new[] { messagingComponent }
            );

            Assert.That(snapshot.ComponentNodes.Count, Is.EqualTo(1));
            Assert.That(snapshot.MessageNodes.Count, Is.EqualTo(1));
            Assert.That(snapshot.Edges.Count, Is.EqualTo(1));
            Assert.That(snapshot.Warnings, Is.Empty);
            Assert.That(snapshot.ComponentNodes[0].HierarchyPath, Is.EqualTo("FlowGraphHost"));
            Assert.That(snapshot.ComponentNodes[0].ListenerCount, Is.EqualTo(1));
            Assert.That(snapshot.ComponentNodes[0].RegistrationCount, Is.EqualTo(1));
            Assert.That(snapshot.ComponentNodes[0].CallCount, Is.EqualTo(1));
            Assert.That(snapshot.ComponentNodes[0].LocalMessageCount, Is.EqualTo(1));
            Assert.That(
                snapshot.MessageNodes[0].MessageTypeName,
                Does.Contain(nameof(FlowGraphMessage))
            );
            Assert.That(
                snapshot.MessageNodes[0].MessageTypeName,
                Does.Contain("WallstopStudios.DxMessaging.Tests.Editor")
            );
            Assert.That(snapshot.MessageNodes[0].RegistrationCount, Is.EqualTo(1));
            Assert.That(snapshot.MessageNodes[0].CallCount, Is.EqualTo(1));
            Assert.That(snapshot.MessageNodes[0].RecentGlobalEmissionCount, Is.EqualTo(1));
            Assert.That(snapshot.MessageNodes[0].RecentLocalMessageCount, Is.EqualTo(1));
            Assert.That(snapshot.MessageNodes[0].RecentTracedDeliveryCount, Is.EqualTo(1));
            Assert.That(snapshot.Edges[0].MessageTypeName, Does.Contain(nameof(FlowGraphMessage)));
            Assert.That(snapshot.Edges[0].TargetComponentPath, Is.EqualTo("FlowGraphHost"));
            Assert.That(snapshot.Edges[0].RegistrationTypeName, Does.Contain("Untargeted"));
            Assert.That(snapshot.Edges[0].RegistrationCount, Is.EqualTo(1));
            Assert.That(snapshot.Edges[0].CallCount, Is.EqualTo(1));
            Assert.That(snapshot.Edges[0].RecentTracedDeliveryCount, Is.EqualTo(1));

            messagingComponent.EditorResetRuntimeState();
            Assert.That(
                CountMessageBusRegistrations(messageBus),
                Is.EqualTo(initialRegistrationCount)
            );
        }

        [Test]
        public void CaptureSnapshotCountsRecentTracedDeliveriesPerRegistrationHandle()
        {
            GameObject host = CreateTrackedObject("FlowGraphTraceHandleHost");
            MessagingComponent messagingComponent = host.AddComponent<MessagingComponent>();
            TestListener listener = host.AddComponent<TestListener>();
            MessageBus messageBus = MessageHandler.MessageBus as MessageBus;
            Assert.That(messageBus, Is.Not.Null);

            MessageRegistrationToken token = messagingComponent.Create(listener);
            token.DiagnosticMode = true;
            token.RegisterUntargeted<FlowGraphMessage>(listener.OnFlowGraphMessage);
            token.RegisterUntargetedPostProcessor<FlowGraphMessage>(
                listener.PostProcessFlowGraphMessage
            );
            token.Enable();

            messageBus.DiagnosticsMode = true;
            messageBus._emissionBuffer.Clear();

            FlowGraphMessage message = default;
            MessageHandler.MessageBus.UntargetedBroadcast(ref message);

            FlowGraphSnapshot snapshot = DxMessagingFlowGraphWindow.CaptureSnapshot(
                new[] { messagingComponent }
            );

            FlowGraphEdge untargetedEdge = snapshot.Edges.Single(edge =>
                edge.RegistrationTypeName == "Untargeted"
            );
            FlowGraphEdge postProcessorEdge = snapshot.Edges.Single(edge =>
                edge.RegistrationTypeName == "UntargetedPostProcessor"
            );

            Assert.That(snapshot.ComponentNodes[0].LocalMessageCount, Is.EqualTo(2));
            Assert.That(snapshot.MessageNodes[0].RecentLocalMessageCount, Is.EqualTo(2));
            Assert.That(snapshot.MessageNodes[0].RecentTracedDeliveryCount, Is.EqualTo(2));
            Assert.That(untargetedEdge.CallCount, Is.EqualTo(1));
            Assert.That(untargetedEdge.RecentTracedDeliveryCount, Is.EqualTo(1));
            Assert.That(postProcessorEdge.CallCount, Is.EqualTo(1));
            Assert.That(postProcessorEdge.RecentTracedDeliveryCount, Is.EqualTo(1));

            messagingComponent.EditorResetRuntimeState();
        }

        [Test]
        public void CaptureSnapshotAttributesGlobalAcceptAllTracesToConcreteMessageNodes()
        {
            GameObject host = CreateTrackedObject("FlowGraphGlobalAcceptAllTraceHost");
            MessagingComponent messagingComponent = host.AddComponent<MessagingComponent>();
            TestListener listener = host.AddComponent<TestListener>();
            MessageBus messageBus = MessageHandler.MessageBus as MessageBus;
            Assert.That(messageBus, Is.Not.Null);

            MessageRegistrationToken token = messagingComponent.Create(listener);
            token.DiagnosticMode = true;
            token.RegisterGlobalAcceptAll(
                listener.OnGlobalUntargeted,
                listener.OnGlobalTargeted,
                listener.OnGlobalBroadcast
            );
            token.Enable();

            messageBus.DiagnosticsMode = true;
            messageBus._emissionBuffer.Clear();

            FlowGraphMessage message = default;
            MessageHandler.MessageBus.UntargetedBroadcast(ref message);

            FlowGraphSnapshot snapshot = DxMessagingFlowGraphWindow.CaptureSnapshot(
                new[] { messagingComponent }
            );

            FlowGraphMessageNode concreteNode = snapshot.MessageNodes.Single(node =>
                node.MessageTypeName.Contains(nameof(FlowGraphMessage), StringComparison.Ordinal)
            );
            FlowGraphMessageNode catchAllNode = snapshot.MessageNodes.Single(node =>
                node.MessageTypeName.Contains("DxMessaging.Core.IMessage", StringComparison.Ordinal)
            );
            FlowGraphEdge catchAllEdge = snapshot.Edges.Single(edge =>
                edge.RegistrationTypeName == "GlobalAcceptAll"
            );

            Assert.That(concreteNode.RecentLocalMessageCount, Is.EqualTo(1));
            Assert.That(concreteNode.RecentTracedDeliveryCount, Is.EqualTo(1));
            Assert.That(catchAllNode.RegistrationCount, Is.EqualTo(1));
            Assert.That(catchAllNode.CallCount, Is.EqualTo(1));
            Assert.That(
                catchAllNode.RecentTracedDeliveryCount,
                Is.EqualTo(0),
                "The IMessage registration node is a catch-all route, not the concrete delivered message."
            );
            Assert.That(catchAllEdge.RecentTracedDeliveryCount, Is.EqualTo(1));

            messagingComponent.EditorResetRuntimeState();
        }

        [Test]
        public void CaptureSnapshotBuildsRecentTracePathsFromJoinedDiagnostics()
        {
            GameObject host = CreateTrackedObject("FlowGraphTracePathHost");
            MessagingComponent messagingComponent = host.AddComponent<MessagingComponent>();
            TestListener listener = host.AddComponent<TestListener>();
            MessageBus messageBus = MessageHandler.MessageBus as MessageBus;
            Assert.That(messageBus, Is.Not.Null);

            InstanceId source = new(998877);
            MessageRegistrationToken token = messagingComponent.Create(listener);
            token.DiagnosticMode = true;
            token.RegisterBroadcast<FlowGraphBroadcastMessage>(
                source,
                listener.OnFlowGraphBroadcast
            );
            token.Enable();

            messageBus.DiagnosticsMode = true;
            messageBus._emissionBuffer.Clear();

            FlowGraphBroadcastMessage message = default;
            MessageHandler.MessageBus.SourcedBroadcast(ref source, ref message);

            FlowGraphSnapshot snapshot = DxMessagingFlowGraphWindow.CaptureSnapshot(
                new[] { messagingComponent }
            );

            Assert.That(snapshot.TracePaths.Count, Is.EqualTo(1));
            Assert.That(
                snapshot.TracePaths[0].MessageTypeName,
                Does.Contain(nameof(FlowGraphBroadcastMessage))
            );
            Assert.That(snapshot.TracePaths[0].Context, Does.Contain("998877"));
            Assert.That(
                snapshot.TracePaths[0].TargetComponentPath,
                Is.EqualTo("FlowGraphTracePathHost")
            );
            Assert.That(snapshot.TracePaths[0].RegistrationTypeName, Is.EqualTo("Broadcast"));
            Assert.That(snapshot.TracePaths[0].RecentTracedDeliveryCount, Is.EqualTo(1));
            Assert.That(snapshot.TracePaths[0].RecentTraceIdCount, Is.EqualTo(1));

            messagingComponent.EditorResetRuntimeState();
        }

        [Test]
        public void CaptureSnapshotKeepsTracePathEvidenceScopedToTokenDeliveryRecord()
        {
            GameObject host = CreateTrackedObject("FlowGraphCustomBusTracePathHost");
            MessagingComponent messagingComponent = host.AddComponent<MessagingComponent>();
            TestListener listener = host.AddComponent<TestListener>();
            MessageBus defaultBus = MessageHandler.MessageBus as MessageBus;
            Assert.That(defaultBus, Is.Not.Null);

            MessageBus customBus = new();
            messagingComponent.Configure(customBus, MessageBusRebindMode.PreserveRegistrations);
            InstanceId source = new(13579);
            MessageRegistrationToken token = messagingComponent.Create(listener);
            token.DiagnosticMode = true;
            token.RegisterBroadcast<FlowGraphBroadcastMessage>(
                source,
                listener.OnFlowGraphBroadcast
            );
            token.Enable();

            defaultBus.DiagnosticsMode = true;
            defaultBus._emissionBuffer.Clear();
            defaultBus._emissionBuffer.Add(
                new MessageEmissionData(new EvidenceOnlyFlowGraphMessage(), traceId: 1)
            );
            customBus.DiagnosticsMode = true;

            FlowGraphBroadcastMessage message = default;
            customBus.SourcedBroadcast(ref source, ref message);

            FlowGraphSnapshot snapshot = DxMessagingFlowGraphWindow.CaptureSnapshot(
                new[] { messagingComponent }
            );

            Assert.That(snapshot.TracePaths.Count, Is.EqualTo(1));
            Assert.That(
                snapshot.TracePaths[0].MessageTypeName,
                Does.Contain(nameof(FlowGraphBroadcastMessage))
            );
            Assert.That(snapshot.TracePaths[0].MessageTypeName, Does.Not.Contain("EvidenceOnly"));
            Assert.That(snapshot.TracePaths[0].Context, Does.Contain("13579"));

            messagingComponent.EditorResetRuntimeState();
            defaultBus._emissionBuffer.Clear();
        }

        [Test]
        public void CaptureSnapshotBuildsEvidenceOnlyMessageNodesFromGlobalHistory()
        {
            GameObject host = CreateTrackedObject("FlowGraphEvidenceOnlyHost");
            MessagingComponent messagingComponent = host.AddComponent<MessagingComponent>();
            MessageBus messageBus = MessageHandler.MessageBus as MessageBus;
            Assert.That(messageBus, Is.Not.Null);

            messageBus.DiagnosticsMode = true;
            messageBus._emissionBuffer.Clear();
            messageBus._emissionBuffer.Add(
                new MessageEmissionData(new EvidenceOnlyFlowGraphMessage())
            );

            FlowGraphSnapshot snapshot = DxMessagingFlowGraphWindow.CaptureSnapshot(
                new[] { messagingComponent }
            );

            Assert.That(snapshot.ComponentNodes.Count, Is.EqualTo(1));
            Assert.That(snapshot.MessageNodes.Count, Is.EqualTo(1));
            Assert.That(snapshot.Edges, Is.Empty);
            Assert.That(
                snapshot.MessageNodes[0].MessageTypeName,
                Does.Contain(nameof(EvidenceOnlyFlowGraphMessage))
            );
            Assert.That(snapshot.MessageNodes[0].RegistrationCount, Is.EqualTo(0));
            Assert.That(snapshot.MessageNodes[0].CallCount, Is.EqualTo(0));
            Assert.That(snapshot.MessageNodes[0].RecentGlobalEmissionCount, Is.EqualTo(1));
            Assert.That(snapshot.MessageNodes[0].RecentLocalMessageCount, Is.EqualTo(0));
        }

        [Test]
        public void CaptureSnapshotDoesNotResolveSerializedProviders()
        {
            ThrowingScriptableMessageBusProvider provider = CreateTrackedObject(
                ScriptableObject.CreateInstance<ThrowingScriptableMessageBusProvider>()
            );
            GameObject host = CreateTrackedObject("FlowGraphSerializedProviderHost");
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

            FlowGraphSnapshot snapshot = DxMessagingFlowGraphWindow.CaptureSnapshot(
                new[] { messagingComponent }
            );

            Assert.That(snapshot.ComponentNodes.Count, Is.EqualTo(1));
            Assert.That(provider.ResolveCount, Is.EqualTo(0));
            Assert.That(snapshot.Warnings, Is.Empty);
        }

        [Test]
        public void CaptureSnapshotFindsSceneComponentsAndSkipsPersistentAssets()
        {
            string suffix = Guid.NewGuid().ToString("N");
            string sceneName = "FlowGraphSceneComponentHost-" + suffix;
            string prefabName = "FlowGraphPrefabComponentHost-" + suffix;
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

                FlowGraphSnapshot snapshot = DxMessagingFlowGraphWindow.CaptureSnapshot();

                Assert.That(
                    snapshot.ComponentNodes.Any(component => component.HierarchyPath == sceneName),
                    Is.True
                );
                Assert.That(
                    snapshot.ComponentNodes.Any(component => component.HierarchyPath == prefabName),
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
        public void CaptureSnapshotSkipsPreviewSceneComponents()
        {
            string suffix = Guid.NewGuid().ToString("N");
            string sceneName = "FlowGraphSceneHost-" + suffix;
            string previewName = "FlowGraphPreviewHost-" + suffix;
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

                FlowGraphSnapshot snapshot = DxMessagingFlowGraphWindow.CaptureSnapshot();

                Assert.That(
                    snapshot.ComponentNodes.Any(component => component.HierarchyPath == sceneName),
                    Is.True
                );
                Assert.That(
                    snapshot.ComponentNodes.Any(component =>
                        component.HierarchyPath == previewName
                    ),
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

        private static int CountMessageBusRegistrations(IMessageBus messageBus)
        {
            return messageBus.RegisteredUntargeted
                + messageBus.RegisteredTargeted
                + messageBus.RegisteredBroadcast
                + messageBus.RegisteredInterceptors
                + messageBus.RegisteredPostProcessors
                + messageBus.RegisteredGlobalAcceptAll;
        }

        private static void AssertColor(Color actual, Color expected)
        {
            Assert.That(actual.r, Is.EqualTo(expected.r).Within(0.001f));
            Assert.That(actual.g, Is.EqualTo(expected.g).Within(0.001f));
            Assert.That(actual.b, Is.EqualTo(expected.b).Within(0.001f));
            Assert.That(actual.a, Is.EqualTo(expected.a).Within(0.001f));
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

        private static void AssertRouteKindBadge(Label label, string expectedKind)
        {
            Assert.That(label, Is.Not.Null);
            Assert.That(label.text, Is.EqualTo(expectedKind));
            Assert.That(
                label.ClassListContains(DxMessagingEditorTheme.TypeBadgeClassName),
                Is.True
            );
            Assert.That(label.ClassListContains(ExpectedTypeBadgeClass(expectedKind)), Is.True);
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
                    return DxMessagingEditorTheme.TypeBadgeClassName;
            }
        }

        private static string RenderRouteMapSummary(FlowGraphSnapshot snapshot)
        {
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            return root.Q<VisualElement>(DxMessagingFlowGraphWindow.RouteMapName)
                .Q<Label>(DxMessagingFlowGraphWindow.RouteMapSummaryLabelName)
                .text;
        }

        private static string RenderTracePathsSummary(params FlowGraphTracePath[] tracePaths)
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                tracePaths,
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            return root.Q<VisualElement>(DxMessagingFlowGraphWindow.TracePathsName)
                .Q<Label>(DxMessagingFlowGraphWindow.TracePathsSummaryLabelName)
                .text;
        }

        private static string RenderVisibleFlowCorridorsSummary(
            params FlowGraphTracePath[] tracePaths
        )
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                tracePaths,
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            return root.Q<VisualElement>(DxMessagingFlowGraphWindow.VisibleFlowCorridorsName)
                    ?.Q<Label>(DxMessagingFlowGraphWindow.VisibleFlowCorridorsSummaryLabelName)
                    ?.text
                ?? string.Empty;
        }

        private static string RenderVisibleContextLanesSummary(
            params FlowGraphTracePath[] tracePaths
        )
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                tracePaths,
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            return root.Q<VisualElement>(ContextLanesName)
                    ?.Q<Label>(ContextLanesSummaryLabelName)
                    ?.text
                ?? string.Empty;
        }

        private static string RenderVisibleTraceMessageLanesSummary(
            params FlowGraphTracePath[] tracePaths
        )
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                tracePaths,
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            return root.Q<VisualElement>(TraceMessageLanesName)
                    ?.Q<Label>(TraceMessageLanesSummaryLabelName)
                    ?.text
                ?? string.Empty;
        }

        private static string RenderVisibleTraceTargetLanesSummary(
            params FlowGraphTracePath[] tracePaths
        )
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                tracePaths,
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            return root.Q<VisualElement>(TraceTargetLanesName)
                    ?.Q<Label>(TraceTargetLanesSummaryLabelName)
                    ?.text
                ?? string.Empty;
        }

        private static string RenderVisibleTraceRouteKindLanesSummary(
            params FlowGraphTracePath[] tracePaths
        )
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                tracePaths,
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            return root.Q<VisualElement>(TraceRouteKindLanesName)
                    ?.Q<Label>(TraceRouteKindLanesSummaryLabelName)
                    ?.text
                ?? string.Empty;
        }

        private static string RenderVisibleTraceIdLanesSummary(
            params FlowGraphTracePath[] tracePaths
        )
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                Array.Empty<FlowGraphEdge>(),
                tracePaths,
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            return root.Q<VisualElement>(TraceIdLanesName)
                    ?.Q<Label>(TraceIdLanesSummaryLabelName)
                    ?.text
                ?? string.Empty;
        }

        private static string RenderVisibleMessageLanesSummary(params FlowGraphEdge[] edges)
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                edges,
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            return root.Q<VisualElement>(MessageLanesName)
                    ?.Q<Label>(MessageLanesSummaryLabelName)
                    ?.text
                ?? string.Empty;
        }

        private static string RenderVisibleTargetLanesSummary(params FlowGraphEdge[] edges)
        {
            FlowGraphSnapshot snapshot = new(
                Array.Empty<FlowGraphComponentNode>(),
                Array.Empty<FlowGraphMessageNode>(),
                edges,
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            return root.Q<VisualElement>(TargetLanesName)
                    ?.Q<Label>(TargetLanesSummaryLabelName)
                    ?.text
                ?? string.Empty;
        }

        private static FlowGraphSnapshot CreateTwoEdgeSnapshot()
        {
            return new FlowGraphSnapshot(
                new[]
                {
                    new FlowGraphComponentNode(
                        "component:alpha",
                        "Root/Alpha",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 4,
                        localMessageCount: 1
                    ),
                    new FlowGraphComponentNode(
                        "component:beta",
                        "Root/Beta",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 2,
                        localMessageCount: 0
                    ),
                },
                new[]
                {
                    new FlowGraphMessageNode("InventoryChanged", 1, 4),
                    new FlowGraphMessageNode("ScoreChanged", 1, 2),
                },
                new[]
                {
                    new FlowGraphEdge(
                        "InventoryChanged",
                        "component:alpha",
                        "Root/Alpha",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 4
                    ),
                    new FlowGraphEdge(
                        "ScoreChanged",
                        "component:beta",
                        "Root/Beta",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 2
                    ),
                },
                Array.Empty<string>()
            );
        }

        private static FlowGraphSnapshot CreateSharedMessageSnapshot()
        {
            return new FlowGraphSnapshot(
                new[]
                {
                    new FlowGraphComponentNode(
                        "component:alpha",
                        "Root/Alpha",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 4,
                        localMessageCount: 1
                    ),
                    new FlowGraphComponentNode(
                        "component:beta",
                        "Root/Beta",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 2,
                        localMessageCount: 0
                    ),
                },
                new[] { new FlowGraphMessageNode("SharedMessage", 2, 6) },
                new[]
                {
                    new FlowGraphEdge(
                        "SharedMessage",
                        "component:alpha",
                        "Root/Alpha",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 4
                    ),
                    new FlowGraphEdge(
                        "SharedMessage",
                        "component:beta",
                        "Root/Beta",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 2
                    ),
                },
                Array.Empty<string>()
            );
        }

        private static FlowGraphSnapshot CreateMixedRouteKindSnapshot()
        {
            return new FlowGraphSnapshot(
                new[]
                {
                    new FlowGraphComponentNode(
                        "component:alpha",
                        "Root/Alpha",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 2,
                        registrationCount: 2,
                        callCount: 6,
                        localMessageCount: 1
                    ),
                    new FlowGraphComponentNode(
                        "component:beta",
                        "Root/Beta",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 2,
                        registrationCount: 2,
                        callCount: 4,
                        localMessageCount: 0
                    ),
                },
                new[]
                {
                    new FlowGraphMessageNode("InventoryChanged", 2, 7),
                    new FlowGraphMessageNode("ScoreChanged", 2, 3),
                },
                new[]
                {
                    new FlowGraphEdge(
                        "InventoryChanged",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        registrationCount: 1,
                        callCount: 4
                    ),
                    new FlowGraphEdge(
                        "InventoryChanged",
                        "component:beta",
                        "Root/Beta",
                        "Broadcast",
                        registrationCount: 1,
                        callCount: 3
                    ),
                    new FlowGraphEdge(
                        "ScoreChanged",
                        "component:alpha",
                        "Root/Alpha",
                        "Targeted",
                        registrationCount: 1,
                        callCount: 2
                    ),
                    new FlowGraphEdge(
                        "ScoreChanged",
                        "component:beta",
                        "Root/Beta",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 1
                    ),
                },
                Array.Empty<string>()
            );
        }

        private static FlowGraphSnapshot CreateMostRoutedTargetSnapshot()
        {
            return new FlowGraphSnapshot(
                new[]
                {
                    new FlowGraphComponentNode(
                        "component:alpha",
                        "Root/Alpha",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 2,
                        registrationCount: 2,
                        callCount: 2,
                        localMessageCount: 1
                    ),
                    new FlowGraphComponentNode(
                        "component:beta",
                        "Root/Beta",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 9,
                        localMessageCount: 0
                    ),
                },
                new[]
                {
                    new FlowGraphMessageNode("InventoryChanged", 2, 10),
                    new FlowGraphMessageNode("ScoreChanged", 1, 1),
                },
                new[]
                {
                    new FlowGraphEdge(
                        "InventoryChanged",
                        "component:beta",
                        "Root/Beta",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 9
                    ),
                    new FlowGraphEdge(
                        "InventoryChanged",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        registrationCount: 1,
                        callCount: 1
                    ),
                    new FlowGraphEdge(
                        "ScoreChanged",
                        "component:alpha",
                        "Root/Alpha",
                        "Targeted",
                        registrationCount: 1,
                        callCount: 1
                    ),
                },
                Array.Empty<string>()
            );
        }

        private static FlowGraphSnapshot CreateMostRoutedTargetPathTieSnapshot()
        {
            return new FlowGraphSnapshot(
                new[]
                {
                    new FlowGraphComponentNode(
                        "component:beta",
                        "Root/Beta",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 2,
                        localMessageCount: 0
                    ),
                    new FlowGraphComponentNode(
                        "component:alpha",
                        "Root/Alpha",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 2,
                        localMessageCount: 1
                    ),
                },
                new[] { new FlowGraphMessageNode("InventoryChanged", 2, 4) },
                new[]
                {
                    new FlowGraphEdge(
                        "InventoryChanged",
                        "component:beta",
                        "Root/Beta",
                        "Broadcast",
                        registrationCount: 1,
                        callCount: 2
                    ),
                    new FlowGraphEdge(
                        "InventoryChanged",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        registrationCount: 1,
                        callCount: 2
                    ),
                },
                Array.Empty<string>()
            );
        }

        private static FlowGraphSnapshot CreateInactiveRoutedTargetSnapshot()
        {
            return new FlowGraphSnapshot(
                new[]
                {
                    new FlowGraphComponentNode(
                        "component:alpha",
                        "Root/Alpha",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 1,
                        localMessageCount: 0
                    ),
                    new FlowGraphComponentNode(
                        "component:beta",
                        "Root/Beta",
                        "MessagingComponent",
                        activeInHierarchy: false,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 2,
                        localMessageCount: 0
                    ),
                    new FlowGraphComponentNode(
                        "component:inactive-orphan",
                        "Root/InactiveOrphan",
                        "MessagingComponent",
                        activeInHierarchy: false,
                        listenerCount: 0,
                        registrationCount: 0,
                        callCount: 0,
                        localMessageCount: 0
                    ),
                },
                new[] { new FlowGraphMessageNode("InventoryChanged", 2, 3) },
                new[]
                {
                    new FlowGraphEdge(
                        "InventoryChanged",
                        "component:alpha",
                        "Root/Alpha",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 1
                    ),
                    new FlowGraphEdge(
                        "InventoryChanged",
                        "component:beta",
                        "Root/Beta",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 2
                    ),
                },
                Array.Empty<string>()
            );
        }

        private static FlowGraphSnapshot CreateRecentTracedRouteCoverageSnapshot()
        {
            return new FlowGraphSnapshot(
                new[]
                {
                    new FlowGraphComponentNode(
                        "component:alpha",
                        "Root/Alpha",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 4,
                        localMessageCount: 0
                    ),
                    new FlowGraphComponentNode(
                        "component:beta",
                        "Root/Beta",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 2,
                        localMessageCount: 0
                    ),
                },
                new[] { new FlowGraphMessageNode("InventoryChanged", 2, 6) },
                new[]
                {
                    new FlowGraphEdge(
                        "InventoryChanged",
                        "component:alpha",
                        "Root/Alpha",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 4,
                        recentTracedDeliveryCount: 3
                    ),
                    new FlowGraphEdge(
                        "InventoryChanged",
                        "component:beta",
                        "Root/Beta",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 2,
                        recentTracedDeliveryCount: 0
                    ),
                },
                Array.Empty<string>()
            );
        }

        private static FlowGraphSnapshot CreateSelectedDetailsRouteHealthSnapshot()
        {
            return new FlowGraphSnapshot(
                new[]
                {
                    new FlowGraphComponentNode(
                        "component:alpha",
                        "Root/Alpha",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 2,
                        callCount: 3,
                        localMessageCount: 0
                    ),
                    new FlowGraphComponentNode(
                        "component:beta",
                        "Root/Beta",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 5,
                        localMessageCount: 0
                    ),
                },
                new[] { new FlowGraphMessageNode("InventoryChanged", 3, 8) },
                new[]
                {
                    new FlowGraphEdge(
                        "InventoryChanged",
                        "component:alpha",
                        "Root/Alpha",
                        "Untargeted",
                        registrationCount: 1,
                        callCount: 3,
                        recentTracedDeliveryCount: 2
                    ),
                    new FlowGraphEdge(
                        "InventoryChanged",
                        "component:alpha",
                        "Root/Alpha",
                        "Broadcast",
                        registrationCount: 1,
                        callCount: 0,
                        recentTracedDeliveryCount: 0
                    ),
                    new FlowGraphEdge(
                        "InventoryChanged",
                        "component:beta",
                        "Root/Beta",
                        "Broadcast",
                        registrationCount: 1,
                        callCount: 5,
                        recentTracedDeliveryCount: 5
                    ),
                },
                Array.Empty<string>()
            );
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

        private readonly struct FlowGraphMessage : IUntargetedMessage { }

        private readonly struct FlowGraphBroadcastMessage : IBroadcastMessage { }

        private readonly struct EvidenceOnlyFlowGraphMessage : IUntargetedMessage { }

        [Serializable]
        private sealed class FlowGraphExportPayload
        {
            public int schemaVersion;
            public string captureMode;
            public string traceSemantics;
            public int componentCount;
            public int messageCount;
            public int edgeCount;
            public int tracePathCount;
            public FlowGraphExportMessage[] messages;
            public FlowGraphExportTracePath[] tracePaths;
        }

        [Serializable]
        private sealed class FlowGraphExportMessage
        {
            public string messageType;
            public int registrationCount;
            public int callCount;
            public int recentGlobalEmissionCount;
            public int recentLocalMessageCount;
            public int recentTracedDeliveryCount;
        }

        [Serializable]
        private sealed class FlowGraphExportTracePath
        {
            public string messageType;
            public string context;
            public string targetComponentId;
            public string targetComponentPath;
            public string registrationType;
            public int recentTracedDeliveryCount;
            public int recentTraceIdCount;
            public long[] recentTraceIds;
        }

        private sealed class TestListener : MonoBehaviour
        {
            public void OnFlowGraphMessage(ref FlowGraphMessage message) { }

            public void OnFlowGraphBroadcast(ref FlowGraphBroadcastMessage message) { }

            public void PostProcessFlowGraphMessage(ref FlowGraphMessage message) { }

            public void OnGlobalUntargeted(IUntargetedMessage message) { }

            public void OnGlobalTargeted(InstanceId target, ITargetedMessage message) { }

            public void OnGlobalBroadcast(InstanceId source, IBroadcastMessage message) { }
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
