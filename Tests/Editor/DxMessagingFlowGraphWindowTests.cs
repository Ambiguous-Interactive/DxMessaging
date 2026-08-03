#if UNITY_EDITOR && UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Editor
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using Core;
    using Core.Diagnostics;
    using Core.MessageBus;
    using Core.Messages;
    using DxMessaging.Editor;
    using DxMessaging.Editor.Testing;
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
            // The viewport-selection test keeps a shown host window open until teardown.
            // Unity resets LogAssert tolerance between the test body and teardown, so
            // re-enable the shared headless-only suppression before closing that window.
            EditorWindowTestUtility.SuppressHeadlessWindowRenderErrors();

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
            VisualElement graph = root.Q<VisualElement>(DxMessagingFlowGraphWindow.GraphCanvasName);
            Assert.That(graph, Is.Not.Null, "The primary surface should be a graph canvas.");
            Assert.That(
                graph
                    .Query<VisualElement>(
                        className: DxMessagingFlowGraphWindow.GraphMessageNodeClassName
                    )
                    .ToList(),
                Has.Count.EqualTo(1)
            );
            Assert.That(
                graph
                    .Query<VisualElement>(
                        className: DxMessagingFlowGraphWindow.GraphReceiverNodeClassName
                    )
                    .ToList(),
                Has.Count.EqualTo(1)
            );
            Assert.That(
                graph
                    .Query<VisualElement>(
                        className: DxMessagingFlowGraphWindow.GraphConnectionClassName
                    )
                    .ToList(),
                Has.Count.EqualTo(1)
            );
            VisualElement graphMessage = graph
                .Query<VisualElement>(
                    className: DxMessagingFlowGraphWindow.GraphMessageNodeClassName
                )
                .First();
            VisualElement graphReceiver = graph
                .Query<VisualElement>(
                    className: DxMessagingFlowGraphWindow.GraphReceiverNodeClassName
                )
                .First();
            Assert.That(
                graphMessage.style.left.value.value,
                Is.LessThan(graphReceiver.style.left.value.value),
                "Message nodes should be laid out to the left of receiver nodes."
            );
            AssertCompleteBorder(graphMessage, DxMessagingEditorPalette.AmberSoft);
            AssertCompleteBorder(graphReceiver, DxMessagingEditorPalette.Amber);
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
        public void BuildGraphUiLeadsWithInteractiveCanvasAndCollapsesTextAnalysis()
        {
            FlowGraphSnapshot snapshot = CreateTwoEdgeSnapshot();
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            Label overview = root.Q<Label>(DxMessagingFlowGraphWindow.RouteMapOverviewLabelName);
            Foldout routeInsights = root.Q<Foldout>(
                DxMessagingFlowGraphWindow.RouteMapInsightsFoldoutName
            );
            Foldout traceActivity = root.Q<Foldout>(
                DxMessagingFlowGraphWindow.TraceActivityFoldoutName
            );
            Foldout topology = root.Q<Foldout>(DxMessagingFlowGraphWindow.TopologyFoldoutName);
            Label detailsTitle = root.Q<Label>(DxMessagingFlowGraphWindow.DetailsTitleLabelName);
            VisualElement graph = root.Q<VisualElement>(DxMessagingFlowGraphWindow.GraphCanvasName);
            Foldout analysis = root.Q<Foldout>(DxMessagingFlowGraphWindow.AnalysisFoldoutName);

            Assert.That(
                graph,
                Is.Not.Null,
                "The primary surface should render a node-and-edge graph."
            );
            Assert.That(
                graph
                    .Query<VisualElement>(
                        className: DxMessagingFlowGraphWindow.GraphMessageNodeClassName
                    )
                    .ToList(),
                Has.Count.EqualTo(2),
                "Every visible message should be represented as a graph node."
            );
            Assert.That(
                graph
                    .Query<VisualElement>(
                        className: DxMessagingFlowGraphWindow.GraphReceiverNodeClassName
                    )
                    .ToList(),
                Has.Count.EqualTo(2),
                "Every visible receiver should be represented as a graph node."
            );
            Assert.That(
                graph
                    .Query<VisualElement>(
                        className: DxMessagingFlowGraphWindow.GraphConnectionClassName
                    )
                    .ToList(),
                Has.Count.EqualTo(2),
                "Every visible route should be represented as a graph connection."
            );
            Assert.That(
                analysis.value,
                Is.False,
                "Text reports should default collapsed beneath the graph."
            );
            Assert.That(
                overview,
                Is.Not.Null,
                "The collapsed analysis should retain the route overview for inspection."
            );
            Assert.That(
                overview.text,
                Is.EqualTo("2 routes | 2 messages | 2 receivers | 6 calls"),
                $"Unexpected visible overview text: {overview.text}"
            );
            Assert.That(
                routeInsights.value,
                Is.False,
                "Route insights should default collapsed so the route map remains scannable."
            );
            Assert.That(
                traceActivity,
                Is.Null,
                "Trace activity should be omitted when the capture has no trace paths."
            );
            Assert.That(
                topology.value,
                Is.False,
                "Raw topology should default collapsed so components, messages, and edges do not duplicate the route map."
            );
            Assert.That(
                detailsTitle,
                Is.Null,
                "The graph should wait for an intentional selection before opening diagnostics."
            );

            FlowGraphSnapshot tracedSnapshot = new(
                snapshot.ComponentNodes,
                snapshot.MessageNodes,
                snapshot.Edges,
                new[]
                {
                    new FlowGraphTracePath(
                        "InventoryChanged",
                        "<none>",
                        "component:alpha",
                        "Root/Alpha",
                        "Untargeted",
                        recentTracedDeliveryCount: 1
                    ),
                },
                snapshot.Warnings
            );
            VisualElement tracedRoot = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(tracedRoot, tracedSnapshot);

            Foldout tracedActivity = tracedRoot.Q<Foldout>(
                DxMessagingFlowGraphWindow.TraceActivityFoldoutName
            );
            Assert.That(
                tracedActivity.value,
                Is.False,
                "Trace activity should default collapsed so secondary analysis does not bury routes."
            );

            tracedActivity.value = true;
            Assert.That(
                DxMessagingFlowGraphWindow.RefreshGraphContent(
                    tracedRoot,
                    tracedSnapshot,
                    new FlowGraphViewState("missing")
                ),
                Is.True
            );
            Assert.That(
                tracedRoot.Q<Foldout>(DxMessagingFlowGraphWindow.TraceActivityFoldoutName),
                Is.Null
            );
            Assert.That(
                DxMessagingFlowGraphWindow.RefreshGraphContent(
                    tracedRoot,
                    tracedSnapshot,
                    FlowGraphViewState.Default
                ),
                Is.True
            );
            Assert.That(
                tracedRoot.Q<Foldout>(DxMessagingFlowGraphWindow.TraceActivityFoldoutName).value,
                Is.True,
                "Refreshing graph content should preserve an expanded trace section."
            );
        }

        [Test]
        public void BuildGraphUiExplainsMissingLiveRoutesWithoutRenderingZeroValueTopology()
        {
            FlowGraphSnapshot snapshot = new(
                new[]
                {
                    new FlowGraphComponentNode(
                        "component:listener",
                        "Root/Listener",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 0,
                        registrationCount: 0,
                        callCount: 0,
                        localMessageCount: 0
                    ),
                },
                new[]
                {
                    new FlowGraphMessageNode(
                        "ObservedMessage",
                        registrationCount: 0,
                        callCount: 0,
                        recentGlobalEmissionCount: 3,
                        recentLocalMessageCount: 0,
                        recentTracedDeliveryCount: 0
                    ),
                },
                Array.Empty<FlowGraphEdge>(),
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            Label emptyTitle = root.Q<Label>(DxMessagingFlowGraphWindow.EmptyStateTitleLabelName);
            Label emptyBody = root.Q<Label>(DxMessagingFlowGraphWindow.EmptyStateLabelName);

            Assert.That(
                emptyTitle.text,
                Is.EqualTo("No live routes"),
                $"Unexpected no-route title: {emptyTitle.text}"
            );
            Assert.That(
                emptyBody.text,
                Does.Contain("Enter Play mode"),
                $"The no-route guidance should name the next action, but was '{emptyBody.text}'."
            );
            Assert.That(
                root.Q<VisualElement>(DxMessagingFlowGraphWindow.RouteMapName),
                Is.Null,
                "A zero-route capture should not render a zero-value route summary."
            );
            Assert.That(
                root.Query<VisualElement>(
                        className: DxMessagingFlowGraphWindow.ComponentNodeClassName
                    )
                    .ToList(),
                Is.Empty,
                "A zero-route capture should not dump raw component rows below the guidance."
            );
        }

        [Test]
        public void BuildGraphUiPlacesEveryRouteOnCanvasAndKeepsTextOverflowCollapsed()
        {
            FlowGraphComponentNode[] components = Enumerable
                .Range(0, 10)
                .Select(index => new FlowGraphComponentNode(
                    $"component:{index}",
                    $"Root/Listener {index}",
                    "MessagingComponent",
                    activeInHierarchy: true,
                    listenerCount: 1,
                    registrationCount: 1,
                    callCount: index == 9 ? 100 : 9 - index,
                    localMessageCount: 0
                ))
                .ToArray();
            FlowGraphMessageNode[] messages = Enumerable
                .Range(0, 9)
                .Select(index => new FlowGraphMessageNode($"ConcreteMessage{index}", 1, 9 - index))
                .Append(new FlowGraphMessageNode("IMessage", 1, 100))
                .ToArray();
            FlowGraphEdge[] edges = Enumerable
                .Range(0, 9)
                .Select(index => new FlowGraphEdge(
                    $"ConcreteMessage{index}",
                    $"component:{index}",
                    $"Root/Listener {index}",
                    "Untargeted",
                    registrationCount: 1,
                    callCount: 9 - index
                ))
                .Append(
                    new FlowGraphEdge(
                        "IMessage",
                        "component:9",
                        "Root/Listener 9",
                        "GlobalAcceptAll",
                        registrationCount: 1,
                        callCount: 100
                    )
                )
                .ToArray();
            FlowGraphSnapshot snapshot = new(components, messages, edges, Array.Empty<string>());
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            VisualElement routeMap = root.Q<VisualElement>(DxMessagingFlowGraphWindow.RouteMapName);
            VisualElement graph = root.Q<VisualElement>(DxMessagingFlowGraphWindow.GraphCanvasName);
            Foldout analysis = root.Q<Foldout>(DxMessagingFlowGraphWindow.AnalysisFoldoutName);
            Foldout moreRoutes = root.Q<Foldout>(
                DxMessagingFlowGraphWindow.RouteMapMoreRoutesFoldoutName
            );
            int initiallyVisibleRouteCount = routeMap
                .Children()
                .Count(child =>
                    child.ClassListContains(DxMessagingFlowGraphWindow.RouteMapRouteClassName)
                );
            int overflowRouteCount = moreRoutes
                .Query<VisualElement>(className: DxMessagingFlowGraphWindow.RouteMapRouteClassName)
                .ToList()
                .Count;
            Label detailsTitle = root.Q<Label>(DxMessagingFlowGraphWindow.DetailsTitleLabelName);

            Assert.That(
                graph
                    .Query<VisualElement>(
                        className: DxMessagingFlowGraphWindow.GraphMessageNodeClassName
                    )
                    .ToList(),
                Has.Count.EqualTo(10),
                "Message types should remain navigable nodes instead of a text overflow list."
            );
            Assert.That(
                graph
                    .Query<VisualElement>(
                        className: DxMessagingFlowGraphWindow.GraphReceiverNodeClassName
                    )
                    .ToList(),
                Has.Count.EqualTo(10)
            );
            Assert.That(
                graph
                    .Query<VisualElement>(
                        className: DxMessagingFlowGraphWindow.GraphConnectionClassName
                    )
                    .ToList(),
                Has.Count.EqualTo(10),
                "The graph should retain all routes without a visible-route cap."
            );
            Assert.That(
                analysis.value,
                Is.False,
                "Route-row overflow should stay hidden until the secondary analysis is opened."
            );

            Assert.That(
                initiallyVisibleRouteCount,
                Is.EqualTo(8),
                $"Expected 8 initially visible routes, but found {initiallyVisibleRouteCount}."
            );
            Assert.That(
                overflowRouteCount,
                Is.EqualTo(2),
                $"Expected 2 overflow routes, but found {overflowRouteCount}."
            );
            Assert.That(moreRoutes.value, Is.False, "Overflow routes should default collapsed.");
            Assert.That(
                detailsTitle,
                Is.Null,
                "A dense graph should not auto-select a route and open diagnostics before user input."
            );

            analysis.value = true;
            root.Q<Foldout>(DxMessagingFlowGraphWindow.RouteMapInsightsFoldoutName).value = true;
            moreRoutes.value = true;
            root.Q<Foldout>(DxMessagingFlowGraphWindow.TopologyFoldoutName).value = true;

            Assert.That(
                DxMessagingFlowGraphWindow.RefreshGraphContent(
                    root,
                    snapshot,
                    new FlowGraphViewState("missing")
                ),
                Is.True
            );
            Assert.That(root.Q<VisualElement>(DxMessagingFlowGraphWindow.RouteMapName), Is.Null);
            Assert.That(
                DxMessagingFlowGraphWindow.RefreshGraphContent(
                    root,
                    snapshot,
                    FlowGraphViewState.Default
                ),
                Is.True
            );
            Assert.That(
                root.Q<Foldout>(DxMessagingFlowGraphWindow.AnalysisFoldoutName).value,
                Is.True
            );
            Assert.That(
                root.Q<Foldout>(DxMessagingFlowGraphWindow.RouteMapInsightsFoldoutName).value,
                Is.True
            );
            Assert.That(
                root.Q<Foldout>(DxMessagingFlowGraphWindow.RouteMapMoreRoutesFoldoutName).value,
                Is.True
            );
            Assert.That(
                root.Q<Foldout>(DxMessagingFlowGraphWindow.TopologyFoldoutName).value,
                Is.True
            );
        }

        [Test]
        public void GraphFrameScaleAllowsStressGraphOverviewBeforePanning()
        {
            float scale = DxMessagingFlowGraphWindow.CalculateGraphFrameScale(
                new Vector2(520f, 520f),
                new Vector2(1100f, 80f + 100f * (132f + 42f))
            );

            Assert.That(
                scale,
                Is.EqualTo(0.2f),
                $"A 100-row stress graph should support a useful overview before panning, but used {scale}."
            );
        }

        [Test]
        public void BuildGraphUiProvidesExplicitZoomControlsForStressGraphs()
        {
            const int nodeCount = 40;
            const int routesPerMessage = 10;
            FlowGraphComponentNode[] components = Enumerable
                .Range(0, nodeCount)
                .Select(index => new FlowGraphComponentNode(
                    $"component:{index}",
                    $"Stress/Receiver {index}",
                    "MessagingComponent",
                    activeInHierarchy: true,
                    listenerCount: 1,
                    registrationCount: 1,
                    callCount: index + 1,
                    localMessageCount: 0
                ))
                .ToArray();
            FlowGraphMessageNode[] messages = Enumerable
                .Range(0, nodeCount)
                .Select(index => new FlowGraphMessageNode($"Stress.Message{index}", 1, index + 1))
                .ToArray();
            FlowGraphEdge[] edges = Enumerable
                .Range(0, nodeCount)
                .SelectMany(messageIndex =>
                    Enumerable
                        .Range(0, routesPerMessage)
                        .Select(routeIndex =>
                        {
                            int componentIndex = (messageIndex * 7 + routeIndex * 3) % nodeCount;
                            string registrationTypeName = (routeIndex % 3) switch
                            {
                                0 => "Untargeted",
                                1 => "Targeted",
                                _ => "Broadcast",
                            };
                            return new FlowGraphEdge(
                                messages[messageIndex].MessageTypeName,
                                components[componentIndex].Id,
                                components[componentIndex].HierarchyPath,
                                registrationTypeName,
                                registrationCount: 1,
                                callCount: messageIndex + routeIndex + 1,
                                context: $"Stress/Context {routeIndex}",
                                contextId: routeIndex + 1
                            );
                        })
                )
                .ToArray();
            EditorWindow window = CreateTrackedEditorWindow();
            window.position = new Rect(80f, 80f, 1000f, 700f);
            EditorWindowTestUtility.ShowWindow(window);
            VisualElement root = window.rootVisualElement;
            string selectedRoute = string.Empty;

            DxMessagingFlowGraphWindow.BuildGraphUi(
                root,
                new FlowGraphSnapshot(components, messages, edges, Array.Empty<string>()),
                FlowGraphViewState.Default,
                onSelectionChanged: key => selectedRoute = key
            );

            Assert.That(
                root.Q<Button>(DxMessagingFlowGraphWindow.GraphZoomOutButtonName),
                Is.Not.Null,
                "Stress graphs need an explicit zoom-out control instead of requiring an undocumented wheel gesture."
            );
            Assert.That(
                root.Q<Button>(DxMessagingFlowGraphWindow.GraphFitButtonName),
                Is.Not.Null,
                "Stress graphs need a one-action fit control."
            );
            Assert.That(
                root.Q<Button>(DxMessagingFlowGraphWindow.GraphZoomInButtonName),
                Is.Not.Null,
                "A user who framed a stress graph needs an explicit way back to readable nodes."
            );
            VisualElement legend = root.Q<VisualElement>(
                DxMessagingFlowGraphWindow.GraphLegendName
            );
            Assert.That(
                legend.style.flexWrap.value,
                Is.EqualTo(Wrap.Wrap),
                "The legend and zoom controls must wrap instead of overflowing a constrained window."
            );
            Assert.That(
                root.Q<VisualElement>(
                    DxMessagingFlowGraphWindow.GraphZoomControlsName
                ).style.flexShrink.value,
                Is.EqualTo(0f),
                "Constrained layouts must keep the zoom controls usable rather than shrinking them to zero width."
            );
            Assert.That(
                root.Query<VisualElement>(
                        className: DxMessagingFlowGraphWindow.GraphConnectionClassName
                    )
                    .ToList()
                    .Count,
                Is.EqualTo(nodeCount * routesPerMessage),
                "A dense many-to-many stress graph must keep hundreds of crossing routes selectable instead of dropping overflow routes."
            );
            VisualElement edgeLayer = root.Q<VisualElement>(
                DxMessagingFlowGraphWindow.GraphEdgeLayerName
            );
            Assert.That(edgeLayer.panel, Is.Not.Null, "The stress edge layer must be attached.");
            Assert.That(
                edgeLayer.pickingMode,
                Is.EqualTo(PickingMode.Position),
                "Providing a route callback must make the complete edge layer interactive."
            );
            IReadOnlyList<DxMessagingFlowGraphWindow.GraphCurveDescriptor> curves =
                (IReadOnlyList<DxMessagingFlowGraphWindow.GraphCurveDescriptor>)edgeLayer.userData;
            Vector2 routePoint = curves[curves.Count - 1].Evaluate(0.2f);
            float hitRadius = DxMessagingFlowGraphWindow.CalculateLocalGraphRouteHitRadius(
                edgeLayer.worldTransform.MultiplyVector(Vector3.right).magnitude
            );
            string expectedRoute = DxMessagingFlowGraphWindow.FindGraphRouteAtPoint(
                curves,
                routePoint,
                hitRadius
            );
            Assert.That(expectedRoute, Is.Not.Empty);
            Event systemEvent = new()
            {
                type = EventType.MouseDown,
                button = 0,
                mousePosition = edgeLayer.LocalToWorld(routePoint),
            };
            using (MouseDownEvent mouseDown = MouseDownEvent.GetPooled(systemEvent))
            {
                mouseDown.target = edgeLayer;
                edgeLayer.SendEvent(mouseDown);
            }
            Assert.That(
                selectedRoute,
                Is.EqualTo(expectedRoute),
                "Clicking a non-marker segment in the attached 400-route graph must select the visible route."
            );
        }

        [Test]
        public void GraphFeatherColorPreservesRouteHueAtLowOpacity()
        {
            Color routeColor = new(0.2f, 0.4f, 0.8f, 0.75f);

            Color featherColor = DxMessagingFlowGraphWindow.CreateGraphFeatherColor(routeColor);

            Assert.That(featherColor.r, Is.EqualTo(routeColor.r));
            Assert.That(featherColor.g, Is.EqualTo(routeColor.g));
            Assert.That(featherColor.b, Is.EqualTo(routeColor.b));
            Assert.That(featherColor.a, Is.EqualTo(routeColor.a * 0.22f));
        }

        [Test]
        public void GraphRouteHitTestingSelectsTheSourceToDestinationCurve()
        {
            DxMessagingFlowGraphWindow.GraphCurveDescriptor first = new(
                new Vector2(10f, 20f),
                new Vector2(410f, 220f),
                curveOffset: 0f,
                Color.green,
                selected: false,
                selectionKey: "edge|first"
            );
            DxMessagingFlowGraphWindow.GraphCurveDescriptor second = new(
                new Vector2(10f, 220f),
                new Vector2(410f, 20f),
                curveOffset: 0f,
                Color.red,
                selected: false,
                selectionKey: "edge|second"
            );

            IReadOnlyList<DxMessagingFlowGraphWindow.GraphCurveDescriptor> unselectedRenderOrder =
                DxMessagingFlowGraphWindow.OrderGraphCurvesForRendering(new[] { first, second });
            Assert.That(
                unselectedRenderOrder.Select(curve => curve.SelectionKey),
                Is.EqualTo(new[] { "edge|first", "edge|second" }),
                "Rendering order must stay stable when no route is selected."
            );
            string selected = DxMessagingFlowGraphWindow.FindGraphRouteAtPoint(
                unselectedRenderOrder,
                first.Evaluate(0.25f),
                hitRadius: 10f
            );

            Assert.That(
                selected,
                Is.EqualTo("edge|first"),
                "Clicking the source-to-destination path must select that route, not require finding its midpoint glyph."
            );
            Assert.That(
                DxMessagingFlowGraphWindow.FindGraphRouteAtPoint(
                    unselectedRenderOrder,
                    first.Evaluate(0.5f),
                    hitRadius: 10f
                ),
                Is.EqualTo("edge|second"),
                "An exact crossing must select the later route drawn visibly on top."
            );
            DxMessagingFlowGraphWindow.GraphCurveDescriptor selectedFirst = new(
                first.Start,
                first.End,
                first.CurveOffset,
                first.Color,
                selected: true,
                selectionKey: first.SelectionKey
            );
            DxMessagingFlowGraphWindow.GraphCurveDescriptor dimmedSecond = new(
                second.Start,
                second.End,
                second.CurveOffset,
                second.Color,
                selected: false,
                selectionKey: second.SelectionKey,
                dimmed: true
            );
            IReadOnlyList<DxMessagingFlowGraphWindow.GraphCurveDescriptor> selectedRenderOrder =
                DxMessagingFlowGraphWindow.OrderGraphCurvesForRendering(
                    new[] { selectedFirst, dimmedSecond }
                );
            Assert.That(
                selectedRenderOrder[selectedRenderOrder.Count - 1].SelectionKey,
                Is.EqualTo("edge|first"),
                "The selected route must render after every dimmed path so it stays visibly on top."
            );
            Assert.That(
                DxMessagingFlowGraphWindow.FindGraphRouteAtPoint(
                    selectedRenderOrder,
                    selectedFirst.Evaluate(0.5f),
                    hitRadius: 10f
                ),
                Is.EqualTo("edge|first"),
                "At an exact crossing, hit testing must prefer the selected route rendered on top."
            );
            Assert.That(
                DxMessagingFlowGraphWindow.FindGraphRouteAtPoint(
                    new[] { first, second },
                    new Vector2(900f, 900f),
                    hitRadius: 10f
                ),
                Is.Empty,
                "Empty-canvas clicks must remain available for panning."
            );
            Assert.That(
                DxMessagingFlowGraphWindow.CalculateLocalGraphRouteHitRadius(0.2f),
                Is.EqualTo(50f),
                "The 20 percent overview must retain a 10-pixel screen-space route hit corridor."
            );
            Assert.That(
                DxMessagingFlowGraphWindow.CalculateLocalGraphRouteHitRadius(2f),
                Is.EqualTo(5f),
                "Zooming in must not make adjacent routes share an oversized hit corridor."
            );
        }

        [Test]
        public void BuildGraphUiCollapsesRouteEvidenceAndLinksMessageSource()
        {
            string messageTypeName = typeof(FlowGraphMessage).FullName;
            FlowGraphEdge edge = new(
                messageTypeName,
                "component:receiver",
                "Root/Receiver",
                "Untargeted",
                registrationCount: 1,
                callCount: 3,
                recentEmissionSites: new[] { "Game.Emit () (at Assets/Scripts/Game.cs:42)" }
            );
            FlowGraphSnapshot snapshot = new(
                new[]
                {
                    new FlowGraphComponentNode(
                        "component:receiver",
                        "Root/Receiver",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 3,
                        localMessageCount: 0
                    ),
                },
                new[] { new FlowGraphMessageNode(messageTypeName, 1, 3) },
                new[] { edge },
                Array.Empty<string>()
            );
            VisualElement root = new();

            FlowGraphViewState viewState = new(
                selectedItemKey: DxMessagingFlowGraphWindow.CreateEdgeSelectionKey(edge)
            );
            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot, viewState);
            Button messageSource = root.Query<Button>(
                    name: DxMessagingFlowGraphWindow.SourceLinkButtonName
                )
                .ToList()
                .SingleOrDefault(button =>
                    button.text.StartsWith("Open message source", StringComparison.Ordinal)
                );
            if (messageSource == null)
            {
                DxMessagingFlowGraphWindow.CompleteMessageSourceIndexesForTests();
                DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot, viewState);
                messageSource = root.Query<Button>(
                        name: DxMessagingFlowGraphWindow.SourceLinkButtonName
                    )
                    .ToList()
                    .SingleOrDefault(button =>
                        button.text.StartsWith("Open message source", StringComparison.Ordinal)
                    );
            }

            Foldout evidence = root.Q<Foldout>(
                DxMessagingFlowGraphWindow.DetailsEvidenceFoldoutName
            );
            Assert.That(
                evidence,
                Is.Not.Null,
                "Verbose emission and trace evidence should live behind one named disclosure."
            );
            Assert.That(
                evidence.value,
                Is.False,
                "Route evidence should start collapsed so selecting a route does not replace the graph with text."
            );
            Assert.That(
                messageSource,
                Is.Not.Null,
                "The background source index should refresh the selected message with an exact source link."
            );
            DxMessagingFlowGraphWindow.FlowGraphSourceLocation location =
                (DxMessagingFlowGraphWindow.FlowGraphSourceLocation)messageSource.userData;
            Assert.That(
                location.AssetPath,
                Does.EndWith("Tests/Editor/DxMessagingFlowGraphWindowTests.cs"),
                $"The selected message should resolve to its declaring script, but resolved {location.AssetPath}."
            );
            Assert.That(
                location.Line,
                Is.GreaterThan(0),
                "The message-source action should open the declaring line, not only the file."
            );
        }

        [Test]
        public void SourceLocationParserExtractsUnityCallSitePathAndLine()
        {
            bool parsed = DxMessagingFlowGraphWindow.TryParseSourceLocation(
                "Game.Emit () (at Assets/Scripts/Game.cs:42)",
                out DxMessagingFlowGraphWindow.FlowGraphSourceLocation location
            );

            Assert.That(parsed, Is.True, "A standard Unity call site should be linkable.");
            Assert.That(location.AssetPath, Is.EqualTo("Assets/Scripts/Game.cs"));
            Assert.That(location.Line, Is.EqualTo(42));
            Assert.That(
                DxMessagingFlowGraphWindow.TryParseSourceLocation(
                    "Game.Emit without a location",
                    out _
                ),
                Is.False,
                "Diagnostic text without a Unity source suffix must remain plain text."
            );
        }

        [Test]
        public void MessageSourceResolverDistinguishesNestingAndGenericArity()
        {
            (Type type, string declaration)[] cases =
            {
                (typeof(SourceLinkBeta.DuplicateSourceMessage), "DuplicateSourceMessage"),
                (typeof(SourceLinkBeta.GenericSourceMessage), "GenericSourceMessage :"),
                (
                    typeof(SourceLinkBeta.GenericSourceMessage<int>).GetGenericTypeDefinition(),
                    "GenericSourceMessage<T>"
                ),
            };
            foreach ((Type messageType, string expectedDeclaration) in cases)
            {
                string messageTypeName = messageType.FullName;
                FlowGraphEdge edge = new(
                    messageTypeName,
                    "component:source-link",
                    "Root/Source Link",
                    "Untargeted",
                    registrationCount: 1,
                    callCount: 1
                );
                FlowGraphSnapshot snapshot = new(
                    new[]
                    {
                        new FlowGraphComponentNode(
                            "component:source-link",
                            "Root/Source Link",
                            "MessagingComponent",
                            activeInHierarchy: true,
                            listenerCount: 1,
                            registrationCount: 1,
                            callCount: 1,
                            localMessageCount: 0
                        ),
                    },
                    new[] { new FlowGraphMessageNode(messageTypeName, 1, 1) },
                    new[] { edge },
                    Array.Empty<string>()
                );
                VisualElement root = new();

                FlowGraphViewState viewState = new(
                    selectedItemKey: DxMessagingFlowGraphWindow.CreateEdgeSelectionKey(edge)
                );
                DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot, viewState);
                Button messageSource = root.Query<Button>(
                        name: DxMessagingFlowGraphWindow.SourceLinkButtonName
                    )
                    .ToList()
                    .SingleOrDefault(button =>
                        button.text.StartsWith("Open message source", StringComparison.Ordinal)
                    );
                if (messageSource == null)
                {
                    DxMessagingFlowGraphWindow.CompleteMessageSourceIndexesForTests();
                    DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot, viewState);
                    messageSource = root.Query<Button>(
                            name: DxMessagingFlowGraphWindow.SourceLinkButtonName
                        )
                        .ToList()
                        .SingleOrDefault(button =>
                            button.text.StartsWith("Open message source", StringComparison.Ordinal)
                        );
                }

                Assert.That(
                    messageSource,
                    Is.Not.Null,
                    $"The background source index should resolve {messageTypeName}."
                );
                DxMessagingFlowGraphWindow.FlowGraphSourceLocation location =
                    (DxMessagingFlowGraphWindow.FlowGraphSourceLocation)messageSource.userData;
                string declaration = File.ReadAllLines(Path.GetFullPath(location.AssetPath))[
                    location.Line - 1
                ];
                Assert.That(
                    declaration,
                    Does.Contain(expectedDeclaration),
                    $"{messageTypeName} resolved to the wrong declaration at {location.AssetPath}:{location.Line}."
                );
                if (messageType == cases[0].type)
                {
                    Assert.That(
                        location.Line,
                        Is.GreaterThan(
                            Array.FindIndex(
                                File.ReadAllLines(Path.GetFullPath(location.AssetPath)),
                                line => line.Contains("class SourceLinkBeta")
                            ) + 1
                        ),
                        "The duplicate message must resolve inside SourceLinkBeta, not SourceLinkAlpha."
                    );
                }
            }
        }

        [Test]
        public void MessageSourceResolverReadsAssemblyFilesOffTheEditorThread()
        {
            DxMessagingFlowGraphWindow.ResetMessageSourceIndexesForTests();
            int editorThreadId = Thread.CurrentThread.ManagedThreadId;
            int readerThreadId = editorThreadId;
            using ManualResetEventSlim readerStarted = new(initialState: false);
            using ManualResetEventSlim releaseReader = new(initialState: false);
            int indexChangedCount = 0;
            try
            {
                DxMessagingFlowGraphWindow.MessageSourceIndexChanged += HandleIndexChanged;
                DxMessagingFlowGraphWindow.MessageSourceFileReader = _ =>
                {
                    readerThreadId = Thread.CurrentThread.ManagedThreadId;
                    readerStarted.Set();
                    releaseReader.Wait(TimeSpan.FromSeconds(5));
                    return Array.Empty<string>();
                };

                bool resolved = DxMessagingFlowGraphWindow.TryResolveMessageSource(
                    typeof(FlowGraphMessage).FullName,
                    out _
                );

                Assert.That(
                    resolved,
                    Is.False,
                    "A cold source lookup should queue an index build instead of blocking for a result."
                );
                Assert.That(
                    DxMessagingFlowGraphWindow.PendingMessageSourceIndexCount,
                    Is.EqualTo(1),
                    "A cold lookup should share one background index build for its compilation assembly."
                );
                Assert.That(
                    readerStarted.Wait(TimeSpan.FromSeconds(10)),
                    Is.True,
                    "The queued source index should begin reading without requiring another UI action."
                );
                Assert.That(
                    readerThreadId,
                    Is.Not.EqualTo(editorThreadId),
                    "Source files must be read on a worker thread, not Unity's editor thread."
                );

                releaseReader.Set();
                DxMessagingFlowGraphWindow.CompleteMessageSourceIndexesForTests();
                Assert.That(
                    DxMessagingFlowGraphWindow.PendingMessageSourceIndexCount,
                    Is.Zero,
                    "The background index should drain and leave no pending assembly work."
                );
                Assert.That(
                    indexChangedCount,
                    Is.EqualTo(1),
                    "Each completed assembly index should notify open Flow Graph windows immediately."
                );
            }
            finally
            {
                releaseReader.Set();
                DxMessagingFlowGraphWindow.CompleteMessageSourceIndexesForTests();
                DxMessagingFlowGraphWindow.MessageSourceIndexChanged -= HandleIndexChanged;
                DxMessagingFlowGraphWindow.ResetMessageSourceIndexesForTests();
            }

            void HandleIndexChanged()
            {
                indexChangedCount++;
            }
        }

        [Test]
        public void CompletedMessageSourceIndexDrainsWhileAnotherAssemblyIsPending()
        {
            DxMessagingFlowGraphWindow.ResetMessageSourceIndexesForTests();
            using ManualResetEventSlim gatedReaderStarted = new(initialState: false);
            using ManualResetEventSlim releaseGatedReader = new(initialState: false);
            int indexChangedCount = 0;
            try
            {
                DxMessagingFlowGraphWindow.MessageSourceIndexChanged += HandleIndexChanged;
                DxMessagingFlowGraphWindow.MessageSourceFileReader = sourceFile =>
                {
                    if (
                        sourceFile.EndsWith(
                            "DxMessagingFlowGraphWindowTests.cs",
                            StringComparison.Ordinal
                        )
                    )
                    {
                        gatedReaderStarted.Set();
                        releaseGatedReader.Wait(TimeSpan.FromMinutes(1));
                    }
                    return Array.Empty<string>();
                };

                Assert.That(
                    DxMessagingFlowGraphWindow.TryResolveMessageSource(
                        typeof(GlobalStringMessage).FullName,
                        out _
                    ),
                    Is.False
                );
                Assert.That(
                    DxMessagingFlowGraphWindow.TryResolveMessageSource(
                        typeof(FlowGraphMessage).FullName,
                        out _
                    ),
                    Is.False
                );
                Assert.That(
                    gatedReaderStarted.Wait(TimeSpan.FromSeconds(10)),
                    Is.True,
                    "The Editor-test assembly index should reach its intentionally gated source file."
                );
                Assert.That(
                    SpinWait.SpinUntil(
                        () => DxMessagingFlowGraphWindow.CompletedMessageSourceIndexCount > 0,
                        TimeSpan.FromSeconds(10)
                    ),
                    Is.True,
                    "The runtime assembly index should complete while the Editor-test index remains gated."
                );

                DxMessagingFlowGraphWindow.DrainMessageSourceIndexesForTests();

                Assert.That(
                    indexChangedCount,
                    Is.EqualTo(1),
                    "A completed assembly must notify immediately instead of waiting behind another pending build."
                );
                Assert.That(
                    DxMessagingFlowGraphWindow.PendingMessageSourceIndexCount,
                    Is.EqualTo(1),
                    "Only the intentionally gated assembly should remain pending after the completed batch drains."
                );

                releaseGatedReader.Set();
                DxMessagingFlowGraphWindow.CompleteMessageSourceIndexesForTests();
                Assert.That(indexChangedCount, Is.EqualTo(2));
            }
            finally
            {
                releaseGatedReader.Set();
                DxMessagingFlowGraphWindow.CompleteMessageSourceIndexesForTests();
                DxMessagingFlowGraphWindow.MessageSourceIndexChanged -= HandleIndexChanged;
                DxMessagingFlowGraphWindow.ResetMessageSourceIndexesForTests();
            }

            void HandleIndexChanged()
            {
                indexChangedCount++;
            }
        }

        [Test]
        public void MessageSourceResolverHonorsCapturedAssemblyIdentity()
        {
            DxMessagingFlowGraphWindow.ResetMessageSourceIndexesForTests();
            try
            {
                string typeName = typeof(FlowGraphMessage).FullName;
                Assert.That(
                    DxMessagingFlowGraphWindow.TryResolveMessageSource(
                        $"{typeName} [Not.The.Message.Assembly]",
                        out _
                    ),
                    Is.False,
                    "An assembly-qualified capture must not resolve a same-named type from another assembly."
                );
                Assert.That(
                    DxMessagingFlowGraphWindow.PendingMessageSourceIndexCount,
                    Is.Zero,
                    "A missing captured assembly should not queue an unrelated assembly index."
                );

                string assemblyName = typeof(FlowGraphMessage).Assembly.GetName().Name;
                Assert.That(
                    DxMessagingFlowGraphWindow.TryResolveMessageSource(
                        $"{typeName} [{assemblyName}]",
                        out _
                    ),
                    Is.False,
                    "The first lookup should queue the captured assembly index."
                );
                DxMessagingFlowGraphWindow.CompleteMessageSourceIndexesForTests();
                Assert.That(
                    DxMessagingFlowGraphWindow.TryResolveMessageSource(
                        $"{typeName} [{assemblyName}]",
                        out DxMessagingFlowGraphWindow.FlowGraphSourceLocation location
                    ),
                    Is.True,
                    "The exact captured assembly should resolve after its background index completes."
                );
                Assert.That(
                    location.AssetPath,
                    Does.EndWith("Tests/Editor/DxMessagingFlowGraphWindowTests.cs")
                );
            }
            finally
            {
                DxMessagingFlowGraphWindow.CompleteMessageSourceIndexesForTests();
                DxMessagingFlowGraphWindow.ResetMessageSourceIndexesForTests();
            }
        }

        [Test]
        public void MessageSourceResolverRetriesTransientFileReadFailures()
        {
            DxMessagingFlowGraphWindow.ResetMessageSourceIndexesForTests();
            try
            {
                string typeName = typeof(FlowGraphMessage).FullName;
                DxMessagingFlowGraphWindow.MessageSourceFileReader = _ =>
                    throw new IOException("Simulated transient source-file lock.");

                Assert.That(
                    DxMessagingFlowGraphWindow.TryResolveMessageSource(typeName, out _),
                    Is.False
                );
                DxMessagingFlowGraphWindow.CompleteMessageSourceIndexesForTests();

                DxMessagingFlowGraphWindow.MessageSourceFileReader = File.ReadAllLines;
                DxMessagingFlowGraphWindow.AllowIncompleteMessageSourceIndexRetries();
                Assert.That(
                    DxMessagingFlowGraphWindow.TryResolveMessageSource(typeName, out _),
                    Is.False,
                    "The retry should remain asynchronous."
                );
                Assert.That(
                    DxMessagingFlowGraphWindow.PendingMessageSourceIndexCount,
                    Is.EqualTo(1),
                    "A refresh should retry an incomplete assembly index after a transient read failure."
                );
                DxMessagingFlowGraphWindow.CompleteMessageSourceIndexesForTests();
                Assert.That(
                    DxMessagingFlowGraphWindow.TryResolveMessageSource(
                        typeName,
                        out DxMessagingFlowGraphWindow.FlowGraphSourceLocation location
                    ),
                    Is.True,
                    "A temporary read failure must not become a permanent negative source-link cache entry."
                );
                Assert.That(location.Line, Is.GreaterThan(0));
            }
            finally
            {
                DxMessagingFlowGraphWindow.CompleteMessageSourceIndexesForTests();
                DxMessagingFlowGraphWindow.ResetMessageSourceIndexesForTests();
            }
        }

        [Test]
        public void TypeDeclarationScannerSupportsRecordsAndIgnoresNonCodeText()
        {
            string[] lines =
            {
                "namespace Example.Messages",
                "{",
                "    /*",
                "    public record struct GenericMessage<T> : IMessage;",
                "    */",
                "    string decoy = @\"",
                "    }",
                "    public record struct GenericMessage<T> : IMessage;",
                "    \"\"still inside the literal\"\"",
                "    \";",
                "    public record struct GenericMessage<T> : IMessage;",
                "    public readonly struct SplitMessage<",
                "        TFirst,",
                "        TSecond",
                "    > : IMessage { }",
                "    public readonly struct AttributedMessage<[Marker(typeof(Dictionary<,>))] TFirst, TSecond> : IMessage { }",
                "    public class AttributeExpressionMessage<",
                "        [Marker(2 > 1, new[] { 1, 2 })] TFirst,",
                "        TSecond",
                "    >",
                "    {",
                "        public struct Nested : IMessage { }",
                "    }",
                "}",
            };

            Assert.That(
                DxMessagingFlowGraphWindow.FindTypeDeclarationLine(
                    lines,
                    "Example.Messages",
                    new[] { "GenericMessage`1" }
                ),
                Is.EqualTo(11),
                "The scanner must select the live generic record declaration, not matching text inside a block comment or multiline verbatim string."
            );
            Assert.That(
                DxMessagingFlowGraphWindow.FindTypeDeclarationLine(
                    lines,
                    "Example.Messages",
                    new[] { "SplitMessage`2" }
                ),
                Is.EqualTo(12),
                "The scanner must retain exact generic arity when a valid type parameter list spans lines."
            );
            Assert.That(
                DxMessagingFlowGraphWindow.FindTypeDeclarationLine(
                    lines,
                    "Example.Messages",
                    new[] { "AttributedMessage`2" }
                ),
                Is.EqualTo(16),
                "The scanner must ignore nested generic commas inside parameter attributes and count the balanced outer list."
            );
            Assert.That(
                DxMessagingFlowGraphWindow.FindTypeDeclarationLine(
                    lines,
                    "Example.Messages",
                    new[] { "AttributeExpressionMessage`2", "Nested" }
                ),
                Is.EqualTo(22),
                "Generic-parameter attribute operators and array braces must not close the outer list or consume its type scope."
            );
        }

        [Test]
        public void GraphMarkerPositionsSeparateCrossingRoutesBySourceLayer()
        {
            float first = DxMessagingFlowGraphWindow.CreateGraphMarkerPosition(0, 2, 0f);
            float second = DxMessagingFlowGraphWindow.CreateGraphMarkerPosition(1, 2, 0f);

            Assert.That(first, Is.EqualTo(0.38f).Within(0.001f));
            Assert.That(second, Is.EqualTo(0.62f).Within(0.001f));
            Assert.That(second - first, Is.GreaterThanOrEqualTo(0.2f));
        }

        [Test]
        public void GraphMeshNormalizesZeroLengthSegmentsToDefinedDirection()
        {
            Assert.That(
                DxMessagingFlowGraphWindow.NormalizeGraphDirection(Vector2.zero),
                Is.EqualTo(Vector2.right),
                "Degenerate sampled segments must still write their allocated mesh quad."
            );
            Assert.That(
                DxMessagingFlowGraphWindow.NormalizeGraphDirection(new Vector2(3f, 4f)),
                Is.EqualTo(new Vector2(0.6f, 0.8f))
            );
        }

        [Test]
        public void BuildGraphUiPreservesViewportWhenGraphConnectionIsSelected()
        {
            FlowGraphSnapshot snapshot = CreateTwoEdgeSnapshot();
            EditorWindow window = CreateTrackedEditorWindow();
            EditorWindowTestUtility.ShowWindow(window);
            VisualElement root = window.rootVisualElement;
            string selectedItemKey = string.Empty;
            Action<string> select = null;
            select = key =>
            {
                selectedItemKey = key;
                DxMessagingFlowGraphWindow.RefreshGraphContent(
                    root,
                    snapshot,
                    new FlowGraphViewState(selectedItemKey: key),
                    onSelectionChanged: select
                );
            };
            DxMessagingFlowGraphWindow.BuildGraphUi(
                root,
                snapshot,
                FlowGraphViewState.Default,
                onSelectionChanged: select
            );

            VisualElement graph = root.Q<VisualElement>(DxMessagingFlowGraphWindow.GraphCanvasName);
            DxMessagingFlowGraphWindow.FlowGraphCanvasState canvasState =
                (DxMessagingFlowGraphWindow.FlowGraphCanvasState)graph.userData;
            canvasState.Initialized = true;
            canvasState.Pan = new Vector2(123f, -47f);
            canvasState.Zoom = 0.42f;
            VisualElement unselectedConnection = graph
                .Query<VisualElement>(
                    className: DxMessagingFlowGraphWindow.GraphConnectionClassName
                )
                .ToList()
                .First();

            using (ClickEvent click = ClickEvent.GetPooled())
            {
                click.target = unselectedConnection;
                unselectedConnection.SendEvent(click);
            }

            VisualElement refreshedGraph = root.Q<VisualElement>(
                DxMessagingFlowGraphWindow.GraphCanvasName
            );
            DxMessagingFlowGraphWindow.FlowGraphCanvasState refreshedState =
                (DxMessagingFlowGraphWindow.FlowGraphCanvasState)refreshedGraph.userData;
            VisualElement selectedConnection = refreshedGraph
                .Query<VisualElement>(
                    className: DxMessagingFlowGraphWindow.GraphConnectionClassName
                )
                .ToList()
                .Single(connection =>
                    connection.ClassListContains(DxMessagingFlowGraphWindow.SelectedRowClassName)
                );
            List<VisualElement> orderedConnections = refreshedGraph
                .Query<VisualElement>(
                    className: DxMessagingFlowGraphWindow.GraphConnectionClassName
                )
                .ToList();

            Assert.That(selectedItemKey, Is.Not.Empty);
            Assert.That(refreshedState, Is.SameAs(canvasState));
            Assert.That(refreshedState.Pan, Is.EqualTo(new Vector2(123f, -47f)));
            Assert.That(refreshedState.Zoom, Is.EqualTo(0.42f));
            Assert.That(selectedConnection.style.width.value.value, Is.EqualTo(30f));
            Assert.That(
                selectedConnection.Children().Single().style.width.value.value,
                Is.EqualTo(18f)
            );
            Assert.That(
                orderedConnections[orderedConnections.Count - 1],
                Is.SameAs(selectedConnection),
                "The selected route marker must be the last sibling so dimmed markers cannot cover it."
            );
        }

        [Test]
        public void BuildGraphUiPreservesSelectedRouteAndViewportWhenContextDisplayChanges()
        {
            FlowGraphSnapshot initialSnapshot = CreateStableContextRouteSnapshot("Arena/Alpha");
            FlowGraphSnapshot refreshedSnapshot = CreateStableContextRouteSnapshot("Instance 4242");
            string selectionKey = DxMessagingFlowGraphWindow.CreateEdgeSelectionKey(
                initialSnapshot.Edges.Single(edge => edge.ContextId == 4242)
            );
            Assert.That(
                DxMessagingFlowGraphWindow.CreateEdgeSelectionKey(
                    refreshedSnapshot.Edges.Single(edge => edge.ContextId == 4242)
                ),
                Is.EqualTo(selectionKey),
                "A route's stable selection identity must not depend on its mutable context label."
            );
            Assert.That(
                initialSnapshot.Edges.Select(edge => edge.ContextId),
                Is.EqualTo(new[] { 4242, 5252 }),
                "The initial labels must place the selected route first."
            );
            Assert.That(
                refreshedSnapshot.Edges.Select(edge => edge.ContextId),
                Is.EqualTo(new[] { 5252, 4242 }),
                "The refreshed labels must reverse input order to exercise signature stability."
            );

            EditorWindow window = CreateTrackedEditorWindow();
            EditorWindowTestUtility.ShowWindow(window);
            VisualElement root = window.rootVisualElement;
            FlowGraphViewState viewState = new(selectedItemKey: selectionKey);
            DxMessagingFlowGraphWindow.BuildGraphUi(root, initialSnapshot, viewState);

            VisualElement graph = root.Q<VisualElement>(DxMessagingFlowGraphWindow.GraphCanvasName);
            DxMessagingFlowGraphWindow.FlowGraphCanvasState canvasState =
                (DxMessagingFlowGraphWindow.FlowGraphCanvasState)graph.userData;
            canvasState.Initialized = true;
            canvasState.Pan = new Vector2(91f, -23f);
            canvasState.Zoom = 0.9f;
            string layoutSignature = canvasState.LayoutSignature;

            DxMessagingFlowGraphWindow.RefreshGraphContent(root, refreshedSnapshot, viewState);

            VisualElement refreshedGraph = root.Q<VisualElement>(
                DxMessagingFlowGraphWindow.GraphCanvasName
            );
            DxMessagingFlowGraphWindow.FlowGraphCanvasState refreshedState =
                (DxMessagingFlowGraphWindow.FlowGraphCanvasState)refreshedGraph.userData;
            int selectedConnectionCount = refreshedGraph
                .Query<VisualElement>(
                    className: DxMessagingFlowGraphWindow.GraphConnectionClassName
                )
                .ToList()
                .Count(connection =>
                    connection.ClassListContains(DxMessagingFlowGraphWindow.SelectedRowClassName)
                );

            Assert.That(
                refreshedState,
                Is.SameAs(canvasState),
                "Refresh must reuse the existing canvas state."
            );
            Assert.That(
                refreshedState.LayoutSignature,
                Is.EqualTo(layoutSignature),
                "A display-only context change must not invalidate the graph layout."
            );
            Assert.That(
                refreshedState.Initialized,
                Is.True,
                "A display-only context change must not request automatic reframing."
            );
            Assert.That(
                refreshedState.Pan,
                Is.EqualTo(new Vector2(91f, -23f)),
                "A display-only context change must preserve the user's pan position."
            );
            Assert.That(
                refreshedState.Zoom,
                Is.EqualTo(0.9f),
                "A display-only context change must preserve the user's zoom level."
            );
            Assert.That(
                selectedConnectionCount,
                Is.EqualTo(1),
                "The same stable route must remain selected after its display label changes."
            );
            Assert.That(
                root.Q<Label>(DxMessagingFlowGraphWindow.DetailsBodyLabelName).text,
                Does.Contain("Route context: Instance 4242"),
                "The preserved selection must resolve to the refreshed route details."
            );
        }

        [Test]
        public void BuildGraphUiMakesBroadcastSourceAndReceiverDirectionExplicit()
        {
            FlowGraphMessageNode message = new(
                "Combat.DamageApplied",
                registrationCount: 1,
                callCount: 3,
                recentGlobalEmissionCount: 3,
                messageKindName: "BROADCAST",
                recentEmissionSites: new[] { "Archer.Fire" },
                recentContexts: new[] { "Arena/Archer" }
            );
            FlowGraphSnapshot snapshot = new(
                new[]
                {
                    new FlowGraphComponentNode(
                        "component:receiver",
                        "Arena/Health",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 3,
                        localMessageCount: 0
                    ),
                },
                new[] { message },
                new[]
                {
                    new FlowGraphEdge(
                        message.MessageTypeName,
                        "component:receiver",
                        "Arena/Health",
                        "Broadcast",
                        registrationCount: 1,
                        callCount: 3,
                        context: "Arena/Archer"
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            VisualElement messageNode = root.Q<VisualElement>(
                className: DxMessagingFlowGraphWindow.GraphMessageNodeClassName
            );
            VisualElement marker = root.Q<VisualElement>(
                className: DxMessagingFlowGraphWindow.GraphConnectionClassName
            );
            Assert.That(
                messageNode.Query<Label>().ToList().Select(label => label.text),
                Does.Contain("BROADCAST")
            );
            Assert.That(
                messageNode
                    .Query<VisualElement>(
                        className: DxMessagingFlowGraphWindow.GraphNodeMetricClassName
                    )
                    .ToList()
                    .Select(row =>
                        string.Join(": ", row.Query<Label>().ToList().Select(label => label.text))
                    ),
                Does.Contain("Sources: Archer")
            );
            Assert.That(marker.Query<Label>().ToList(), Is.Empty);
            Assert.That(
                marker.tooltip,
                Does.Contain("Arena/Archer -> Combat.DamageApplied -> Arena/Health")
            );
            Assert.That(marker.tooltip, Does.Not.Contain("FROM").And.Not.Contain(" TO "));
        }

        [Test]
        public void BuildGraphUiExposesTargetedEmitSiteWithoutInventingSenderObject()
        {
            FlowGraphMessageNode message = new(
                "Combat.ApplyKnockback",
                registrationCount: 1,
                callCount: 2,
                recentGlobalEmissionCount: 2,
                messageKindName: "TARGETED",
                recentEmissionSites: new[] { "CombatController.FireAtTarget" },
                recentContexts: new[] { "Arena/Target" }
            );
            FlowGraphSnapshot snapshot = new(
                new[]
                {
                    new FlowGraphComponentNode(
                        "component:target",
                        "Arena/Target/Receiver",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 2,
                        localMessageCount: 0
                    ),
                },
                new[] { message },
                new[]
                {
                    new FlowGraphEdge(
                        message.MessageTypeName,
                        "component:target",
                        "Arena/Target/Receiver",
                        "Targeted",
                        registrationCount: 1,
                        callCount: 2,
                        context: "Arena/Target",
                        recentEmissionSites: new[] { "CombatController.FireAtTarget" }
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(
                root,
                snapshot,
                new FlowGraphViewState(
                    selectedItemKey: DxMessagingFlowGraphWindow.CreateEdgeSelectionKey(
                        snapshot.Edges[0]
                    )
                )
            );

            VisualElement marker = root.Q<VisualElement>(
                className: DxMessagingFlowGraphWindow.GraphConnectionClassName
            );
            string details = root.Q<Label>(DxMessagingFlowGraphWindow.DetailsBodyLabelName).text;
            Assert.That(marker.Query<Label>().ToList(), Is.Empty);
            Assert.That(
                marker.tooltip,
                Does.Contain("Combat.ApplyKnockback -> Arena/Target -> Arena/Target/Receiver")
            );
            Assert.That(marker.tooltip, Does.Not.Contain("AT "));
            Assert.That(marker.tooltip, Does.Contain("EMITTED BY CombatController.FireAtTarget"));
            Assert.That(details, Does.Contain("Route context: Arena/Target"));
            Assert.That(details, Does.Contain("Recent emit sites: CombatController.FireAtTarget"));
            Assert.That(details, Does.Contain("CombatController.FireAtTarget"));
            Assert.That(
                root.Query<VisualElement>(
                        className: DxMessagingFlowGraphWindow.DetailsSectionClassName
                    )
                    .ToList()
                    .Count,
                Is.GreaterThanOrEqualTo(4)
            );
            Assert.That(
                root.Query<VisualElement>(
                        className: DxMessagingFlowGraphWindow.DetailsMetricClassName
                    )
                    .ToList()
                    .Count,
                Is.EqualTo(5)
            );
            Assert.That(
                root.Q<Foldout>(DxMessagingFlowGraphWindow.DetailsTechnicalFoldoutName).value,
                Is.False
            );
            string structuredDetails = string.Join(
                "\n",
                root.Query<VisualElement>(
                        className: DxMessagingFlowGraphWindow.DetailsSectionClassName
                    )
                    .ToList()
                    .SelectMany(section => section.Query<Label>().ToList())
                    .Select(label => label.text)
            );
            Assert.That(structuredDetails, Does.Contain("MESSAGE"));
            Assert.That(structuredDetails, Does.Contain("TARGET"));
            Assert.That(structuredDetails, Does.Contain("HANDLER"));
            Assert.That(structuredDetails, Does.Contain("EMISSION EVIDENCE"));
            Assert.That(structuredDetails, Does.Contain("CombatController.FireAtTarget"));
            FlowGraphExportPayload export = JsonUtility.FromJson<FlowGraphExportPayload>(
                DxMessagingFlowGraphWindow.CreateExportText(snapshot)
            );
            Assert.That(export.schemaVersion, Is.EqualTo(6));
            Assert.That(export.messages[0].messageKind, Is.EqualTo("TARGETED"));
            Assert.That(
                export.messages[0].recentEmissionSites,
                Does.Contain("CombatController.FireAtTarget")
            );
            Assert.That(export.edges[0].context, Is.EqualTo("Arena/Target"));
            Assert.That(
                export.edges[0].recentEmissionSites,
                Does.Contain("CombatController.FireAtTarget")
            );
        }

        [Test]
        public void BuildGraphUiOrdersBothLayersToAvoidSimpleCrossing()
        {
            FlowGraphSnapshot snapshot = new(
                new[]
                {
                    new FlowGraphComponentNode("x", "Root/X", "Receiver", true, 1, 1, 1, 0),
                    new FlowGraphComponentNode("y", "Root/Y", "Receiver", true, 1, 1, 1, 0),
                },
                new[] { new FlowGraphMessageNode("A", 1, 1), new FlowGraphMessageNode("B", 1, 1) },
                new[]
                {
                    new FlowGraphEdge("A", "y", "Root/Y", "Untargeted", 1, 1),
                    new FlowGraphEdge("B", "x", "Root/X", "Untargeted", 1, 1),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            Dictionary<string, VisualElement> messages = root.Query<VisualElement>(
                    className: DxMessagingFlowGraphWindow.GraphMessageNodeClassName
                )
                .ToList()
                .ToDictionary(node => node.Q<Label>().text, StringComparer.Ordinal);
            Dictionary<string, VisualElement> receivers = root.Query<VisualElement>(
                    className: DxMessagingFlowGraphWindow.GraphReceiverNodeClassName
                )
                .ToList()
                .ToDictionary(node => node.Q<Label>().text, StringComparer.Ordinal);
            Assert.That(
                messages["A"].style.top.value.value,
                Is.LessThan(messages["B"].style.top.value.value)
            );
            Assert.That(
                receivers["Y"].style.top.value.value,
                Is.LessThan(receivers["X"].style.top.value.value)
            );
        }

        [Test]
        public void BuildGraphUiUsesNamedNodeMetricsWithoutPlusCountShorthand()
        {
            FlowGraphMessageNode message = new(
                "Combat.DamageApplied",
                registrationCount: 2,
                callCount: 7,
                messageKindName: "BROADCAST",
                recentContexts: new[] { "Arena/Archer", "Arena/Mage" }
            );
            FlowGraphSnapshot snapshot = new(
                new[]
                {
                    new FlowGraphComponentNode(
                        "component:alpha-only",
                        "Arena/A",
                        "Receiver",
                        true,
                        1,
                        1,
                        3,
                        0
                    ),
                    new FlowGraphComponentNode(
                        "component:beta-only",
                        "Arena/B",
                        "Receiver",
                        true,
                        1,
                        1,
                        4,
                        0
                    ),
                },
                new[] { message },
                new[]
                {
                    new FlowGraphEdge(
                        message.MessageTypeName,
                        "component:alpha-only",
                        "Arena/A",
                        "Broadcast",
                        1,
                        3,
                        context: "Arena/Archer"
                    ),
                    new FlowGraphEdge(
                        message.MessageTypeName,
                        "component:beta-only",
                        "Arena/B",
                        "Broadcast",
                        1,
                        4,
                        context: "Arena/Mage"
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            VisualElement messageNode = root.Q<VisualElement>(
                className: DxMessagingFlowGraphWindow.GraphMessageNodeClassName
            );
            string metricText = string.Join(
                "\n",
                messageNode
                    .Query<VisualElement>(
                        className: DxMessagingFlowGraphWindow.GraphNodeMetricClassName
                    )
                    .ToList()
                    .SelectMany(row => row.Query<Label>().ToList())
                    .Select(label => label.text)
            );
            Assert.That(metricText, Does.Contain("Sources"));
            Assert.That(metricText, Does.Contain("2 observed"));
            Assert.That(metricText, Does.Contain("Receivers"));
            Assert.That(metricText, Does.Contain("Calls"));
            Assert.That(metricText, Does.Not.Contain("+"));
            Assert.That(messageNode.focusable, Is.True);
            Assert.That(
                root.Q<VisualElement>(
                    className: DxMessagingFlowGraphWindow.GraphConnectionClassName
                ).focusable,
                Is.True
            );
            VisualElement marker = root.Q<VisualElement>(
                className: DxMessagingFlowGraphWindow.GraphConnectionClassName
            );
            VisualElement glyph = marker.Children().Single();
            DxMessagingFlowGraphWindow.ApplyGraphFocusIndicator(
                glyph,
                DxMessagingEditorPalette.BorderStrong,
                focused: true
            );
            Assert.That(glyph.style.borderTopWidth.value, Is.EqualTo(3f));
            DxMessagingFlowGraphWindow.ApplyGraphFocusIndicator(
                glyph,
                DxMessagingEditorPalette.BorderStrong,
                focused: false
            );
            Assert.That(glyph.style.borderTopWidth.value, Is.EqualTo(1f));

            DxMessagingFlowGraphWindow.BuildGraphUi(
                root,
                snapshot,
                new FlowGraphViewState(filterText: "component:alpha-only")
            );
            messageNode = root.Q<VisualElement>(
                className: DxMessagingFlowGraphWindow.GraphMessageNodeClassName
            );
            Dictionary<string, string> filteredMetrics = messageNode
                .Query<VisualElement>(
                    className: DxMessagingFlowGraphWindow.GraphNodeMetricClassName
                )
                .ToList()
                .ToDictionary(
                    row => row.Query<Label>().First().text,
                    row => row.Query<Label>().Last().text,
                    StringComparer.Ordinal
                );
            Assert.That(filteredMetrics["Calls"], Is.EqualTo("3"));
        }

        [Test]
        public void BuildGraphUiUsesMixedMetricsUntilFilteringToOneRouteKind()
        {
            FlowGraphMessageNode message = new(
                "Combat.MultiRouteMessage",
                registrationCount: 2,
                callCount: 7,
                messageKindName: "BROADCAST"
            );
            FlowGraphSnapshot snapshot = new(
                new[]
                {
                    new FlowGraphComponentNode(
                        "component:target-only",
                        "Arena/Target",
                        "Receiver",
                        true,
                        1,
                        1,
                        3,
                        0
                    ),
                    new FlowGraphComponentNode(
                        "component:broadcast-only",
                        "Arena/Broadcast",
                        "Receiver",
                        true,
                        1,
                        1,
                        4,
                        0
                    ),
                },
                new[] { message },
                new[]
                {
                    new FlowGraphEdge(
                        message.MessageTypeName,
                        "component:target-only",
                        "Arena/Target",
                        "Targeted",
                        1,
                        3,
                        context: "Arena/Target",
                        recentEmissionSites: new[] { "TargetController.Send" }
                    ),
                    new FlowGraphEdge(
                        message.MessageTypeName,
                        "component:broadcast-only",
                        "Arena/Broadcast",
                        "Broadcast",
                        1,
                        4,
                        context: "Arena/Source",
                        recentEmissionSites: new[] { "BroadcastController.Send" }
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            VisualElement messageNode = root.Q<VisualElement>(
                className: DxMessagingFlowGraphWindow.GraphMessageNodeClassName
            );
            Assert.That(
                messageNode.Query<Label>().ToList().Select(label => label.text),
                Does.Contain("MIXED")
            );
            Assert.That(
                messageNode
                    .Query<VisualElement>(
                        className: DxMessagingFlowGraphWindow.GraphNodeMetricClassName
                    )
                    .ToList()
                    .Select(row => row.Query<Label>().ToList().First().text),
                Is.EquivalentTo(new[] { "Routes", "Receivers", "Calls" })
            );

            DxMessagingFlowGraphWindow.BuildGraphUi(
                root,
                snapshot,
                new FlowGraphViewState(
                    filterText: "component:target-only",
                    selectedItemKey: DxMessagingFlowGraphWindow.CreateMessageSelectionKey(message)
                )
            );
            messageNode = root.Q<VisualElement>(
                className: DxMessagingFlowGraphWindow.GraphMessageNodeClassName
            );
            Assert.That(
                messageNode.Query<Label>().ToList().Select(label => label.text),
                Does.Contain("TARGETED").And.Not.Contain("MIXED")
            );
            Assert.That(
                messageNode
                    .Query<VisualElement>(
                        className: DxMessagingFlowGraphWindow.GraphNodeMetricClassName
                    )
                    .ToList()
                    .Select(row => row.Query<Label>().ToList().First().text),
                Is.EquivalentTo(new[] { "Targets", "Handlers", "Call sites" })
            );
            Dictionary<string, string> targetedMetrics = messageNode
                .Query<VisualElement>(
                    className: DxMessagingFlowGraphWindow.GraphNodeMetricClassName
                )
                .ToList()
                .ToDictionary(
                    row => row.Query<Label>().ToList().First().text,
                    row => row.Query<Label>().ToList().Last().text,
                    StringComparer.Ordinal
                );
            Assert.That(targetedMetrics["Call sites"], Is.EqualTo("1"));
            string selectedDetails = string.Join(
                "\n",
                root.Q<VisualElement>(DxMessagingFlowGraphWindow.DetailsPaneName)
                    .Query<Label>()
                    .ToList()
                    .Select(label => label.text)
            );
            Assert.That(selectedDetails, Does.Contain("TARGETED"));
            Assert.That(selectedDetails, Does.Not.Contain("Message kind: MIXED"));
            Assert.That(
                root.Q<Label>(DxMessagingFlowGraphWindow.DetailsBodyLabelName).text,
                Does.Contain("Message kind: TARGETED")
            );
        }

        [Test]
        public void BuildGraphUiNamesGlobalAcceptAllAsObserverScopeInsteadOfIMessage()
        {
            FlowGraphMessageNode message = new(
                DxMessagingFlowGraphWindow.GlobalObserverMessageName,
                registrationCount: 1,
                callCount: 2,
                recentTracedDeliveryCount: 0,
                messageKindName: "GLOBAL OBSERVER"
            );
            FlowGraphSnapshot snapshot = new(
                new[]
                {
                    new FlowGraphComponentNode(
                        "component:observer",
                        "Root/Observer",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 1,
                        callCount: 2,
                        localMessageCount: 2
                    ),
                },
                new[] { message },
                new[]
                {
                    new FlowGraphEdge(
                        message.MessageTypeName,
                        "component:observer",
                        "Root/Observer",
                        "GlobalAcceptAll",
                        registrationCount: 1,
                        callCount: 2
                    ),
                },
                new[]
                {
                    new FlowGraphTracePath(
                        "Combat.DamageApplied",
                        "<none>",
                        "component:observer",
                        "Root/Observer",
                        "GlobalAcceptAll",
                        recentTracedDeliveryCount: 2
                    ),
                },
                Array.Empty<string>()
            );
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(
                root,
                snapshot,
                new FlowGraphViewState(
                    selectedItemKey: DxMessagingFlowGraphWindow.CreateMessageSelectionKey(message)
                )
            );

            VisualElement graphMessage = root.Q<VisualElement>(
                className: DxMessagingFlowGraphWindow.GraphMessageNodeClassName
            );
            string[] graphLabels = graphMessage
                .Query<Label>()
                .ToList()
                .Select(label => label.text)
                .ToArray();
            string details = root.Q<Label>(DxMessagingFlowGraphWindow.DetailsBodyLabelName).text;
            Assert.That(graphLabels, Does.Contain("GLOBAL OBSERVER"));
            Assert.That(graphLabels, Does.Contain("ANY MESSAGE"));
            Assert.That(string.Join("\n", graphLabels), Does.Not.Contain("IMessage"));
            Assert.That(details, Does.Contain("Combat.DamageApplied"));

            DxMessagingFlowGraphWindow.BuildGraphUi(
                root,
                snapshot,
                new FlowGraphViewState(
                    selectedItemKey: DxMessagingFlowGraphWindow.CreateEdgeSelectionKey(
                        snapshot.Edges[0]
                    )
                )
            );
            string structuredRouteDetails = string.Join(
                "\n",
                root.Query<VisualElement>(
                        className: DxMessagingFlowGraphWindow.DetailsSectionClassName
                    )
                    .ToList()
                    .SelectMany(section => section.Query<Label>().ToList())
                    .Select(label => label.text)
            );
            Assert.That(structuredRouteDetails, Does.Contain("GLOBAL OBSERVER"));
            Assert.That(structuredRouteDetails, Does.Contain("Combat.DamageApplied"));
            Assert.That(structuredRouteDetails, Does.Contain("2"));
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

            Dictionary<string, VisualElement> graphEdgesByKind = root.Q<VisualElement>(
                    DxMessagingFlowGraphWindow.GraphCanvasName
                )
                .Query<VisualElement>(
                    className: DxMessagingFlowGraphWindow.GraphConnectionClassName
                )
                .ToList()
                .ToDictionary(
                    edge =>
                        edge.tooltip.Contains("BroadcastPostProcessor") ? "Broadcast" : "Targeted",
                    StringComparer.Ordinal
                );
            Assert.That(graphEdgesByKind["Broadcast"].Query<Label>().ToList(), Is.Empty);
            Assert.That(graphEdgesByKind["Targeted"].Query<Label>().ToList(), Is.Empty);
            AssertColor(
                graphEdgesByKind["Targeted"].Children().Single().style.backgroundColor.value,
                DxMessagingEditorPalette.Targeted
            );
            AssertColor(
                graphEdgesByKind["Broadcast"].Children().Single().style.backgroundColor.value,
                DxMessagingEditorPalette.Broadcast
            );
            Assert.That(
                graphEdgesByKind["Targeted"].style.top.value.value,
                Is.Not.EqualTo(graphEdgesByKind["Broadcast"].style.top.value.value),
                "Parallel route kinds need distinct rendered geometry and hit targets."
            );

            Dictionary<string, VisualElement> edgesByKind = root.Query<VisualElement>(
                    className: DxMessagingFlowGraphWindow.EdgeRowClassName
                )
                .ToList()
                .ToDictionary(
                    row =>
                        row.Q<Label>(DxMessagingFlowGraphWindow.EdgeLabelName)
                            .text.Contains("| FROM")
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
        public void BuildGraphUiWaitsForSelectionBeforeRenderingDetails()
        {
            FlowGraphSnapshot snapshot = CreateTwoEdgeSnapshot();
            VisualElement root = new();

            DxMessagingFlowGraphWindow.BuildGraphUi(root, snapshot);

            VisualElement details = root.Q<VisualElement>(
                DxMessagingFlowGraphWindow.DetailsPaneName
            );
            Assert.That(
                details,
                Is.Null,
                "Diagnostics should remain closed until a route or node is intentionally selected."
            );

            DxMessagingFlowGraphWindow.BuildGraphUi(
                root,
                snapshot,
                new FlowGraphViewState(
                    selectedItemKey: DxMessagingFlowGraphWindow.CreateEdgeSelectionKey(
                        snapshot.Edges[0]
                    )
                )
            );

            details = root.Q<VisualElement>(DxMessagingFlowGraphWindow.DetailsPaneName);
            Assert.That(details, Is.Not.Null);
            Assert.That(
                details.Q<Label>(DxMessagingFlowGraphWindow.DetailsTitleLabelName).text,
                Does.Contain("InventoryChanged -> Root/Alpha")
            );
            Assert.That(
                details.Q<Label>(DxMessagingFlowGraphWindow.DetailsBodyLabelName).text,
                Does.Contain("Registration type: Untargeted")
            );
            Assert.That(
                details.Q<Label>(DxMessagingFlowGraphWindow.DetailsBodyLabelName).text,
                Does.Contain("Visible call share: 4/6 (67%)")
            );

            List<VisualElement> routes = root.Query<VisualElement>(
                    className: DxMessagingFlowGraphWindow.RouteMapRouteClassName
                )
                .ToList();
            Assert.That(
                routes[0].ClassListContains(DxMessagingFlowGraphWindow.SelectedRowClassName),
                Is.True
            );
            Assert.That(
                routes[1].ClassListContains(DxMessagingFlowGraphWindow.SelectedRowClassName),
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
            Assert.That(exportPayload.schemaVersion, Is.EqualTo(6));
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
            Assert.That(exportPayload.schemaVersion, Is.EqualTo(6));
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
            Assert.That(exportPayload.schemaVersion, Is.EqualTo(6));
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

            Assert.That(exportPayload.schemaVersion, Is.EqualTo(6));
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

            Assert.That(exportPayload.schemaVersion, Is.EqualTo(6));
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
                    new FlowGraphMessageNode(
                        DxMessagingFlowGraphWindow.GlobalObserverMessageName,
                        1,
                        1,
                        messageKindName: "GLOBAL OBSERVER"
                    ),
                },
                new[]
                {
                    new FlowGraphEdge(
                        DxMessagingFlowGraphWindow.GlobalObserverMessageName,
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

            Assert.That(exportPayload.schemaVersion, Is.EqualTo(6));
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

            Assert.That(exportPayload.schemaVersion, Is.EqualTo(6));
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

            Assert.That(exportPayload.schemaVersion, Is.EqualTo(6));
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

            Assert.That(exportPayload.schemaVersion, Is.EqualTo(6));
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

            Assert.That(exportPayload.schemaVersion, Is.EqualTo(6));
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

            Assert.That(exportPayload.schemaVersion, Is.EqualTo(6));
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
            Assert.That(exportPayload.schemaVersion, Is.EqualTo(6));
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
                root.Q<Foldout>(DxMessagingFlowGraphWindow.TopologyFoldoutName).value = true;
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
                Assert.That(
                    root.Q<Foldout>(DxMessagingFlowGraphWindow.TopologyFoldoutName).value,
                    Is.True,
                    "Selecting a topology row should not collapse the section around it."
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
                node.MessageTypeName == DxMessagingFlowGraphWindow.GlobalObserverMessageName
            );
            FlowGraphEdge catchAllEdge = snapshot.Edges.Single(edge =>
                edge.RegistrationTypeName == "GlobalAcceptAll"
            );

            Assert.That(concreteNode.RecentLocalMessageCount, Is.EqualTo(1));
            Assert.That(concreteNode.RecentTracedDeliveryCount, Is.EqualTo(1));
            Assert.That(catchAllNode.RegistrationCount, Is.EqualTo(1));
            Assert.That(catchAllNode.CallCount, Is.EqualTo(1));
            Assert.That(catchAllNode.MessageKindName, Is.EqualTo("GLOBAL OBSERVER"));
            Assert.That(
                catchAllNode.RecentTracedDeliveryCount,
                Is.EqualTo(0),
                "The ANY MESSAGE scope node is a catch-all route, not the concrete delivered message."
            );
            Assert.That(catchAllEdge.RecentTracedDeliveryCount, Is.EqualTo(1));
            Assert.That(catchAllEdge.MessageTypeName, Is.EqualTo("ANY MESSAGE"));

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
            FlowGraphEdge broadcastEdge = snapshot.Edges.Single(edge =>
                edge.RegistrationTypeName == "Broadcast"
            );
            FlowGraphMessageNode broadcastNode = snapshot.MessageNodes.Single(node =>
                node.MessageTypeName.Contains(
                    nameof(FlowGraphBroadcastMessage),
                    StringComparison.Ordinal
                )
            );
            Assert.That(broadcastEdge.Context, Does.Contain("998877"));
            Assert.That(
                string.Join("\n", broadcastEdge.RecentEmissionSites),
                Does.Contain(nameof(CaptureSnapshotBuildsRecentTracePathsFromJoinedDiagnostics))
            );
            Assert.That(broadcastNode.MessageKindName, Is.EqualTo("BROADCAST"));
            Assert.That(broadcastNode.RecentContexts.Single(), Does.Contain("998877"));
            Assert.That(
                string.Join("\n", broadcastNode.RecentEmissionSites),
                Does.Contain(nameof(CaptureSnapshotBuildsRecentTracePathsFromJoinedDiagnostics))
            );

            messagingComponent.EditorResetRuntimeState();
        }

        [Test]
        public void CaptureSnapshotKeepsTargetedEmitSitesScopedToTheirTargets()
        {
            GameObject host = CreateTrackedObject("FlowGraphTargetedEvidenceHost");
            MessagingComponent messagingComponent = host.AddComponent<MessagingComponent>();
            TestListener listener = host.AddComponent<TestListener>();
            MessageBus messageBus = MessageHandler.MessageBus as MessageBus;
            Assert.That(messageBus, Is.Not.Null);

            InstanceId firstTarget = host;
            InstanceId secondTarget = listener;
            MessageRegistrationToken token = messagingComponent.Create(listener);
            token.DiagnosticMode = true;
            token.RegisterTargeted<FlowGraphTargetedMessage>(
                firstTarget,
                listener.OnFlowGraphTargeted
            );
            token.RegisterTargeted<FlowGraphTargetedMessage>(
                secondTarget,
                listener.OnFlowGraphTargeted
            );
            token.Enable();

            messageBus.DiagnosticsMode = true;
            messageBus._emissionBuffer.Clear();
            FlowGraphTargetedMessage deliveredMessage = default;
            messageBus.TargetedBroadcast(ref firstTarget, ref deliveredMessage);
            messageBus.TargetedBroadcast(ref secondTarget, ref deliveredMessage);

            MessagingComponentInspectorState evidenceState =
                MessagingComponentEditorHarness.Capture(
                    messagingComponent,
                    resolveSerializedProviderBus: false
                );
            ListenerDiagnosticsView diagnosticsListener = evidenceState.Listeners.Single();
            MessageRegistrationHandle firstHandle = diagnosticsListener
                .Registrations.Single(registration =>
                    registration.Metadata.context?.Id == firstTarget.Id
                )
                .Handle;
            MessageRegistrationHandle secondHandle = diagnosticsListener
                .Registrations.Single(registration =>
                    registration.Metadata.context?.Id == secondTarget.Id
                )
                .Handle;
            MessageEmissionData firstDelivery = diagnosticsListener.EmissionHistory.Single(
                emission => emission.registrationHandle == firstHandle
            );
            MessageEmissionData secondDelivery = diagnosticsListener.EmissionHistory.Single(
                emission => emission.registrationHandle == secondHandle
            );

            FlowGraphSnapshot snapshot = DxMessagingFlowGraphWindow.CaptureSnapshot(
                new[] { messagingComponent }
            );

            FlowGraphEdge firstEdge = snapshot.Edges.Single(edge =>
                edge.Context.Contains("GameObject", StringComparison.Ordinal)
            );
            FlowGraphEdge secondEdge = snapshot.Edges.Single(edge =>
                edge.Context.Contains(nameof(TestListener), StringComparison.Ordinal)
            );
            string firstSites = string.Join("\n", firstEdge.RecentEmissionSites);
            string secondSites = string.Join("\n", secondEdge.RecentEmissionSites);
            string expectedFirstSite = DxMessagingFlowGraphWindow.CreateEmissionSite(
                firstDelivery.stackTrace
            );
            string expectedSecondSite = DxMessagingFlowGraphWindow.CreateEmissionSite(
                secondDelivery.stackTrace
            );
            Assert.That(firstSites, Does.Contain(expectedFirstSite), firstSites);
            Assert.That(firstSites, Does.Not.Contain(expectedSecondSite));
            Assert.That(secondSites, Does.Contain(expectedSecondSite));
            Assert.That(secondSites, Does.Not.Contain(expectedFirstSite));
            Assert.That(firstEdge.ContextId, Is.EqualTo(firstTarget.Id));
            Assert.That(secondEdge.ContextId, Is.EqualTo(secondTarget.Id));
            Assert.That(
                snapshot.TracePaths.Select(path => path.ContextId),
                Is.EquivalentTo(new[] { firstTarget.Id, secondTarget.Id })
            );
            FlowGraphExportPayload export = JsonUtility.FromJson<FlowGraphExportPayload>(
                DxMessagingFlowGraphWindow.CreateExportText(snapshot)
            );
            Assert.That(
                export.tracePaths.Select(path => path.contextId),
                Is.EquivalentTo(new[] { firstTarget.Id, secondTarget.Id })
            );
            Assert.That(
                DxMessagingFlowGraphWindow.CreateEdgeSelectionKey(firstEdge),
                Is.Not.EqualTo(DxMessagingFlowGraphWindow.CreateEdgeSelectionKey(secondEdge))
            );

            messagingComponent.EditorResetRuntimeState();
        }

        [Test]
        public void CaptureSnapshotKeepsRoutesWhenContextObjectWasDestroyed()
        {
            GameObject host = CreateTrackedObject("FlowGraphDestroyedContextHost");
            MessagingComponent messagingComponent = host.AddComponent<MessagingComponent>();
            TestListener listener = host.AddComponent<TestListener>();
            GameObject contextHost = CreateTrackedObject("FlowGraphDestroyedContext");
            InstanceId context = contextHost;
            int contextId = context.Id;
            MessageRegistrationToken token = messagingComponent.Create(listener);
            token.RegisterTargeted<FlowGraphTargetedMessage>(context, listener.OnFlowGraphTargeted);
            token.Enable();
            Object.DestroyImmediate(contextHost);

            FlowGraphSnapshot snapshot = null;
            Assert.DoesNotThrow(
                () =>
                    snapshot = DxMessagingFlowGraphWindow.CaptureSnapshot(
                        new[] { messagingComponent }
                    ),
                "Capture must not dereference a destroyed Unity context reference."
            );

            Assert.That(snapshot, Is.Not.Null, "Capture must return a snapshot after destruction.");
            Assert.That(
                snapshot.Warnings,
                Is.Empty,
                "A destroyed context must use its stable ID without producing a capture warning."
            );
            // Unity 2021.3's NUnit resolves Has.Count against the runtime array, which has no
            // public Count property. Read Count through the IReadOnlyList contract directly so
            // the regression runs on every supported editor version.
            Assert.That(
                snapshot.Edges.Count,
                Is.EqualTo(1),
                "The targeted registration must remain present after its context object is destroyed."
            );
            FlowGraphEdge edge = snapshot.Edges.Single();
            Assert.That(
                edge.Context,
                Is.EqualTo("Instance " + contextId),
                "Destroyed contexts must fall back to readable stable-ID text."
            );
            Assert.That(
                edge.ContextId,
                Is.EqualTo(contextId),
                "Destroyed contexts must retain their exact registration identity."
            );

            messagingComponent.EditorResetRuntimeState();
        }

        [Test]
        public void CaptureSnapshotClassifiesMultiKindMessageTypesAsMixed()
        {
            GameObject host = CreateTrackedObject("FlowGraphMixedKindHost");
            MessagingComponent messagingComponent = host.AddComponent<MessagingComponent>();
            TestListener listener = host.AddComponent<TestListener>();
            MessageRegistrationToken token = messagingComponent.Create(listener);
            InstanceId context = host;
            token.RegisterBroadcast<FlowGraphMixedMessage>(context, listener.OnFlowGraphMixed);
            token.RegisterTargeted<FlowGraphMixedMessage>(context, listener.OnFlowGraphMixed);
            token.Enable();

            FlowGraphSnapshot snapshot = DxMessagingFlowGraphWindow.CaptureSnapshot(
                new[] { messagingComponent }
            );

            Assert.That(snapshot.MessageNodes.Single().MessageKindName, Is.EqualTo("MIXED"));
            Assert.That(
                snapshot.Edges.Select(edge => edge.RegistrationTypeName),
                Is.EquivalentTo(new[] { "Broadcast", "Targeted" })
            );

            messagingComponent.EditorResetRuntimeState();
        }

        [Test]
        public void CaptureSnapshotDoesNotLeakEmitSitesAcrossComponentsOrBuses()
        {
            GameObject firstHost = CreateTrackedObject("FlowGraphDefaultBusHost");
            MessagingComponent firstComponent = firstHost.AddComponent<MessagingComponent>();
            TestListener firstListener = firstHost.AddComponent<TestListener>();
            GameObject secondHost = CreateTrackedObject("FlowGraphCustomBusHost");
            MessagingComponent secondComponent = secondHost.AddComponent<MessagingComponent>();
            TestListener secondListener = secondHost.AddComponent<TestListener>();
            MessageBus defaultBus = MessageHandler.MessageBus as MessageBus;
            Assert.That(defaultBus, Is.Not.Null);
            MessageBus customBus = new();
            secondComponent.Configure(customBus, MessageBusRebindMode.PreserveRegistrations);
            InstanceId sharedTarget = new(24680);

            MessageRegistrationToken firstToken = firstComponent.Create(firstListener);
            firstToken.DiagnosticMode = true;
            firstToken.RegisterTargeted<FlowGraphTargetedMessage>(
                sharedTarget,
                firstListener.OnFlowGraphTargeted
            );
            firstToken.Enable();
            MessageRegistrationToken secondToken = secondComponent.Create(secondListener);
            secondToken.DiagnosticMode = true;
            secondToken.RegisterTargeted<FlowGraphTargetedMessage>(
                sharedTarget,
                secondListener.OnFlowGraphTargeted
            );
            secondToken.Enable();

            defaultBus.DiagnosticsMode = true;
            customBus.DiagnosticsMode = true;
            FlowGraphTargetedMessage message = default;
            defaultBus.TargetedBroadcast(ref sharedTarget, ref message);

            FlowGraphSnapshot snapshot = DxMessagingFlowGraphWindow.CaptureSnapshot(
                new[] { firstComponent, secondComponent }
            );
            FlowGraphEdge deliveredEdge = snapshot.Edges.Single(edge =>
                edge.TargetComponentPath.Contains(firstHost.name, StringComparison.Ordinal)
            );
            FlowGraphEdge otherBusEdge = snapshot.Edges.Single(edge =>
                edge.TargetComponentPath.Contains(secondHost.name, StringComparison.Ordinal)
            );

            Assert.That(deliveredEdge.RecentEmissionSites, Is.Not.Empty);
            Assert.That(otherBusEdge.RecentEmissionSites, Is.Empty);
            Assert.That(deliveredEdge.ContextId, Is.EqualTo(sharedTarget.Id));
            Assert.That(otherBusEdge.ContextId, Is.EqualTo(sharedTarget.Id));

            firstComponent.EditorResetRuntimeState();
            secondComponent.EditorResetRuntimeState();
            defaultBus._emissionBuffer.Clear();
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
            Assert.That(snapshot.MessageNodes[0].MessageKindName, Is.EqualTo("GLOBAL"));
            Assert.That(snapshot.MessageNodes[0].RecentEmissionSites, Is.Not.Empty);
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
            return DxMessagingFlowGraphWindow.CreateRouteMapSummaryText(snapshot);
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

        private static FlowGraphSnapshot CreateStableContextRouteSnapshot(string context)
        {
            return new FlowGraphSnapshot(
                new[]
                {
                    new FlowGraphComponentNode(
                        "component:target",
                        "Arena/Receiver",
                        "MessagingComponent",
                        activeInHierarchy: true,
                        listenerCount: 1,
                        registrationCount: 2,
                        callCount: 6,
                        localMessageCount: 0
                    ),
                },
                new[] { new FlowGraphMessageNode("DamageApplied", 2, 6) },
                new[]
                {
                    new FlowGraphEdge(
                        "DamageApplied",
                        "component:target",
                        "Arena/Receiver",
                        "Targeted",
                        registrationCount: 1,
                        callCount: 3,
                        context: context,
                        contextId: 4242
                    ),
                    new FlowGraphEdge(
                        "DamageApplied",
                        "component:target",
                        "Arena/Receiver",
                        "Targeted",
                        registrationCount: 1,
                        callCount: 3,
                        context: "Arena/Zulu",
                        contextId: 5252
                    ),
                }
                    .OrderBy(edge => edge.Context, StringComparer.Ordinal)
                    .ThenBy(edge => edge.ContextId)
                    .ToArray(),
                Array.Empty<string>()
            );
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

        private readonly struct FlowGraphTargetedMessage : ITargetedMessage { }

        private readonly struct FlowGraphMixedMessage : IBroadcastMessage, ITargetedMessage { }

        private readonly struct EvidenceOnlyFlowGraphMessage : IUntargetedMessage { }

        private static class SourceLinkAlpha
        {
            internal readonly struct DuplicateSourceMessage : IUntargetedMessage { }
        }

        private static class SourceLinkBeta
        {
            internal readonly struct DuplicateSourceMessage : IUntargetedMessage { }

            internal readonly struct GenericSourceMessage : IUntargetedMessage { }

            internal readonly struct GenericSourceMessage<T> : IUntargetedMessage { }
        }

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
            public FlowGraphExportEdge[] edges;
            public FlowGraphExportTracePath[] tracePaths;
        }

        [Serializable]
        private sealed class FlowGraphExportMessage
        {
            public string messageType;
            public string messageKind;
            public int registrationCount;
            public int callCount;
            public int recentGlobalEmissionCount;
            public int recentLocalMessageCount;
            public int recentTracedDeliveryCount;
            public string[] recentEmissionSites;
            public string[] recentContexts;
        }

        [Serializable]
        private sealed class FlowGraphExportEdge
        {
            public string messageType;
            public string targetComponentId;
            public string targetComponentPath;
            public string registrationType;
            public string context;
            public int contextId;
            public int registrationCount;
            public int callCount;
            public int recentTracedDeliveryCount;
            public string[] recentEmissionSites;
        }

        [Serializable]
        private sealed class FlowGraphExportTracePath
        {
            public string messageType;
            public string context;
            public int contextId;
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

            public void OnFlowGraphTargeted(ref FlowGraphTargetedMessage message) { }

            public void OnFlowGraphMixed(ref FlowGraphMixedMessage message) { }

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
