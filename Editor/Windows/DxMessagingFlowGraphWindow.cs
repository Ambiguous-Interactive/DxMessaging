#if UNITY_EDITOR
namespace DxMessaging.Editor.Windows
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using Core;
    using Core.Diagnostics;
    using Core.Messages;
    using DxMessaging.Editor;
    using DxMessaging.Editor.Testing;
    using DxMessaging.Unity;
    using UnityEditor;
    using UnityEditor.Compilation;
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
        internal const string EmptyStateTitleLabelName = "dxmessaging-flow-graph-empty-title";
        internal const string GraphCanvasName = "dxmessaging-flow-graph-canvas";
        internal const string GraphEdgeLayerName = "dxmessaging-flow-graph-edge-layer";
        internal const string GraphZoomOutButtonName = "dxmessaging-flow-graph-zoom-out";
        internal const string GraphFitButtonName = "dxmessaging-flow-graph-fit";
        internal const string GraphZoomInButtonName = "dxmessaging-flow-graph-zoom-in";
        internal const string GraphZoomLabelName = "dxmessaging-flow-graph-zoom-label";
        internal const string GraphLegendName = "dxmessaging-flow-graph-legend";
        internal const string GraphInteractionHintName = "dxmessaging-flow-graph-interaction-hint";
        internal const string GraphZoomControlsName = "dxmessaging-flow-graph-zoom-controls";
        internal const string GraphMessageNodeClassName = "dxmessaging-flow-graph-canvas-message";
        internal const string GraphReceiverNodeClassName = "dxmessaging-flow-graph-canvas-receiver";
        internal const string GraphConnectionClassName = "dxmessaging-flow-graph-canvas-connection";
        internal const string AnalysisFoldoutName = "dxmessaging-flow-graph-analysis";
        internal const string RouteMapName = "dxmessaging-flow-graph-route-map";
        internal const string RouteMapRouteClassName = "dxmessaging-flow-graph-route-map-route";
        internal const string RouteMapMessageLabelName = "dxmessaging-flow-graph-route-map-message";
        internal const string RouteMapRouteKindLabelName =
            "dxmessaging-flow-graph-route-map-route-kind";
        internal const string RouteMapTargetLabelName = "dxmessaging-flow-graph-route-map-target";
        internal const string RouteMapSummaryLabelName = "dxmessaging-flow-graph-route-map-summary";
        internal const string RouteMapOverviewLabelName =
            "dxmessaging-flow-graph-route-map-overview";
        internal const string RouteMapInsightsFoldoutName =
            "dxmessaging-flow-graph-route-map-insights";
        internal const string RouteMapMoreRoutesFoldoutName =
            "dxmessaging-flow-graph-route-map-more";
        internal const string TraceActivityFoldoutName = "dxmessaging-flow-graph-trace-activity";
        internal const string TopologyFoldoutName = "dxmessaging-flow-graph-topology";
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
        internal const string EdgeRouteKindLabelName = "dxmessaging-flow-graph-edge-route-kind";
        internal const string DetailsPaneName = "dxmessaging-flow-graph-details";
        internal const string DetailsTitleLabelName = "dxmessaging-flow-graph-details-title";
        internal const string DetailsSectionClassName = "dxmessaging-flow-graph-details-section";
        internal const string DetailsMetricClassName = "dxmessaging-flow-graph-details-metric";
        internal const string DetailsMessageTypeRowClassName =
            "dxmessaging-flow-graph-details-message-type-row";
        internal const string DetailsHierarchyRowClassName =
            "dxmessaging-flow-graph-details-hierarchy-row";
        internal const string DetailsHierarchyTrailClassName =
            "dxmessaging-flow-graph-details-hierarchy-trail";
        internal const string DetailsHierarchySegmentClassName =
            "dxmessaging-flow-graph-details-hierarchy-segment";
        internal const string DetailsSourceRowClassName =
            "dxmessaging-flow-graph-details-source-row";
        internal const string DetailsRelationshipClassName =
            "dxmessaging-flow-graph-details-relationship";
        internal const string DetailsRouteRowClassName = "dxmessaging-flow-graph-details-route-row";
        internal const string DetailsRoutesFoldoutName = "dxmessaging-flow-graph-details-routes";
        internal const string DetailsRoutesOverflowFoldoutName =
            "dxmessaging-flow-graph-details-routes-overflow";
        internal const string DetailsRelationshipMessageLinkName =
            "dxmessaging-flow-graph-details-relationship-message";
        internal const string DetailsRelationshipReceiverLinkName =
            "dxmessaging-flow-graph-details-relationship-receiver";
        internal const string DetailsTechnicalFoldoutName =
            "dxmessaging-flow-graph-details-technical";
        internal const string DetailsEvidenceFoldoutName =
            "dxmessaging-flow-graph-details-evidence";
        internal const string DetailsOverflowFoldoutName =
            "dxmessaging-flow-graph-details-overflow";
        internal const string DetailsCopyDiagnosticsButtonName =
            "dxmessaging-flow-graph-details-copy-diagnostics";
        internal const string GraphNodeMetricClassName = "dxmessaging-flow-graph-node-metric";
        internal const string WarningLabelName = "dxmessaging-flow-graph-warning";
        internal const string GlobalObserverMessageName = "ANY MESSAGE";

        private const string Title = "Message Flow Graph";
        private const int VisibleRouteLimit = 8;
        private const float GraphNodeWidth = 270f;
        private const float GraphNodeHeight = 132f;
        private const float GraphNodeGap = 42f;
        private const float GraphCanvasHeight = 520f;
        private const float GraphMinimumZoom = 0.2f;
        private const float GraphMaximumZoom = 2f;
        private const float GraphRouteHitRadius = 10f;
        private const int VisibleDetailsRowLimit = 8;
        private const int ExportSchemaVersion = 6;
        private const string ExportCaptureMode = "registration-topology-with-recent-diagnostics";
        private const string ExportTraceSemantics =
            "traceId is emitted by concrete MessageBus dispatch and copied to token delivery records when diagnostics are enabled; edge traced counts are registration-handle exact, trace paths are built from token delivery records to avoid cross-bus trace-id collisions, recentTraceIdCount counts distinct trace ids observed for each trace path, and recentTraceIds lists those positive trace ids for cross-path breadth analysis.";

        private sealed class FlowGraphFoldoutState
        {
            internal bool AnalysisExpanded;
            internal bool RouteInsightsExpanded;
            internal bool MoreRoutesExpanded;
            internal bool TraceActivityExpanded;
            internal bool TopologyExpanded;
            internal bool DetailsEvidenceExpanded;
            internal bool DetailsTechnicalExpanded;
            internal bool DetailsOverflowExpanded;
            internal bool DetailsRoutesExpanded;
            internal bool DetailsRoutesOverflowExpanded;
            internal FlowGraphCanvasState CanvasState { get; } = new();
        }

        private sealed class DetailSelectionData
        {
            internal DetailSelectionData(string selectionKey, string focusRestorationId)
            {
                SelectionKey = selectionKey;
                FocusRestorationId = focusRestorationId;
            }

            internal string SelectionKey { get; }

            internal string FocusRestorationId { get; }
        }

        private readonly struct GraphConnectionDescriptor
        {
            internal GraphConnectionDescriptor(
                string messageTypeName,
                string targetComponentId,
                string targetComponentPath,
                string routeKind,
                string context,
                int contextId,
                IReadOnlyList<string> recentEmissionSites,
                int activityCount,
                string selectionKey,
                bool traceOnly
            )
            {
                MessageTypeName = messageTypeName ?? string.Empty;
                TargetComponentId = targetComponentId ?? string.Empty;
                TargetComponentPath = targetComponentPath ?? string.Empty;
                RouteKind = routeKind ?? string.Empty;
                Context = context ?? string.Empty;
                ContextId = contextId;
                RecentEmissionSites = recentEmissionSites ?? Array.Empty<string>();
                ActivityCount = activityCount;
                SelectionKey = selectionKey ?? string.Empty;
                TraceOnly = traceOnly;
            }

            internal string MessageTypeName { get; }

            internal string TargetComponentId { get; }

            internal string TargetComponentPath { get; }

            internal string RouteKind { get; }

            internal string Context { get; }

            internal int ContextId { get; }

            internal IReadOnlyList<string> RecentEmissionSites { get; }

            internal int ActivityCount { get; }

            internal string SelectionKey { get; }

            internal bool TraceOnly { get; }
        }

        private readonly struct GraphNodeMetric
        {
            internal GraphNodeMetric(string label, string value)
            {
                Label = label ?? string.Empty;
                Value = value ?? string.Empty;
            }

            internal string Label { get; }

            internal string Value { get; }
        }

        private readonly struct CapturedEdgeEmission
        {
            internal CapturedEdgeEmission(string componentId, MessageEmissionData emission)
            {
                ComponentId = componentId ?? string.Empty;
                Emission = emission;
            }

            internal string ComponentId { get; }

            internal MessageEmissionData Emission { get; }
        }

        private readonly struct EdgeEmissionKey : IEquatable<EdgeEmissionKey>
        {
            internal EdgeEmissionKey(string componentId, MessageRegistrationHandle handle)
            {
                ComponentId = componentId ?? string.Empty;
                Handle = handle;
            }

            private string ComponentId { get; }

            private MessageRegistrationHandle Handle { get; }

            public bool Equals(EdgeEmissionKey other)
            {
                return string.Equals(ComponentId, other.ComponentId, StringComparison.Ordinal)
                    && Handle == other.Handle;
            }

            public override bool Equals(object obj)
            {
                return obj is EdgeEmissionKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (StringComparer.Ordinal.GetHashCode(ComponentId) * 397)
                        ^ Handle.GetHashCode();
                }
            }
        }

        internal sealed class FlowGraphCanvasState
        {
            internal bool Initialized;
            internal string LayoutSignature = string.Empty;
            internal Vector2 Pan;
            internal float Zoom = 1f;
        }

        internal readonly struct GraphCurveDescriptor
        {
            internal GraphCurveDescriptor(
                Vector2 start,
                Vector2 end,
                float curveOffset,
                Color color,
                bool selected,
                string selectionKey = "",
                bool dimmed = false
            )
            {
                Start = start;
                End = end;
                CurveOffset = curveOffset;
                Color = dimmed ? new Color(color.r, color.g, color.b, color.a * 0.18f) : color;
                Selected = selected;
                SelectionKey = selectionKey ?? string.Empty;
            }

            internal Vector2 Start { get; }

            internal Vector2 End { get; }

            internal float CurveOffset { get; }

            internal Color Color { get; }

            internal bool Selected { get; }

            internal string SelectionKey { get; }

            internal Vector2 Evaluate(float t)
            {
                Vector2 controlOffset = new((End.x - Start.x) * 0.42f, CurveOffset);
                Vector2 firstControl = Start + controlOffset;
                Vector2 secondControl = End - new Vector2(controlOffset.x, -CurveOffset);
                float inverse = 1f - t;
                return inverse * inverse * inverse * Start
                    + 3f * inverse * inverse * t * firstControl
                    + 3f * inverse * t * t * secondControl
                    + t * t * t * End;
            }
        }

        private sealed class FlowGraphEdgeLayer : VisualElement
        {
            private const int SegmentCount = 28;
            private readonly IReadOnlyList<GraphCurveDescriptor> _curves;

            internal FlowGraphEdgeLayer(
                IReadOnlyList<GraphCurveDescriptor> curves,
                Action<string> onSelectionChanged
            )
            {
                _curves = OrderGraphCurvesForRendering(
                    curves ?? throw new ArgumentNullException(nameof(curves))
                );
                name = GraphEdgeLayerName;
                pickingMode =
                    onSelectionChanged == null ? PickingMode.Ignore : PickingMode.Position;
                generateVisualContent += DrawConnections;
                if (onSelectionChanged != null)
                {
                    RegisterCallback<MouseDownEvent>(evt =>
                    {
                        if (evt.button != 0)
                        {
                            return;
                        }

                        string selectionKey = FindGraphRouteAtPoint(
                            _curves,
                            evt.localMousePosition,
                            CalculateLocalGraphRouteHitRadius(
                                worldTransform.MultiplyVector(Vector3.right).magnitude
                            )
                        );
                        if (string.IsNullOrWhiteSpace(selectionKey))
                        {
                            return;
                        }

                        evt.StopPropagation();
                        onSelectionChanged.Invoke(selectionKey);
                    });
                }
            }

            internal IReadOnlyList<GraphCurveDescriptor> Curves => _curves;

            private void DrawConnections(MeshGenerationContext context)
            {
                foreach (GraphCurveDescriptor curve in _curves)
                {
                    DrawConnection(context, curve);
                }
            }

            private static void DrawConnection(
                MeshGenerationContext context,
                GraphCurveDescriptor curve
            )
            {
                const int arrowVertexCount = 3;
                const int arrowIndexCount = 3;
                MeshWriteData mesh = context.Allocate(
                    SegmentCount * 8 + arrowVertexCount,
                    SegmentCount * 12 + arrowIndexCount
                );
                float width = curve.Selected ? 4f : 2.5f;
                ushort vertexIndex = 0;
                Vector2 previous = curve.Evaluate(0f);
                for (int segment = 1; segment <= SegmentCount; segment++)
                {
                    Vector2 next = curve.Evaluate(segment / (float)SegmentCount);
                    AddSegment(
                        mesh,
                        previous,
                        next,
                        width + 3f,
                        CreateGraphFeatherColor(curve.Color),
                        ref vertexIndex
                    );
                    previous = next;
                }

                previous = curve.Evaluate(0f);
                for (int segment = 1; segment <= SegmentCount; segment++)
                {
                    Vector2 next = curve.Evaluate(segment / (float)SegmentCount);
                    AddSegment(mesh, previous, next, width, curve.Color, ref vertexIndex);
                    previous = next;
                }

                AddArrow(mesh, curve, curve.Color, ref vertexIndex);
            }

            private static void AddSegment(
                MeshWriteData mesh,
                Vector2 start,
                Vector2 end,
                float width,
                Color color,
                ref ushort vertexIndex
            )
            {
                Vector2 direction = NormalizeGraphDirection(end - start);
                Vector2 normal = new(-direction.y, direction.x);
                normal *= width * 0.5f;
                ushort startIndex = vertexIndex;
                mesh.SetNextVertex(CreateGraphVertex(start + normal, color));
                mesh.SetNextVertex(CreateGraphVertex(start - normal, color));
                mesh.SetNextVertex(CreateGraphVertex(end - normal, color));
                mesh.SetNextVertex(CreateGraphVertex(end + normal, color));
                vertexIndex += 4;
                mesh.SetNextIndex(startIndex);
                mesh.SetNextIndex((ushort)(startIndex + 1));
                mesh.SetNextIndex((ushort)(startIndex + 2));
                mesh.SetNextIndex(startIndex);
                mesh.SetNextIndex((ushort)(startIndex + 2));
                mesh.SetNextIndex((ushort)(startIndex + 3));
            }

            private static void AddArrow(
                MeshWriteData mesh,
                GraphCurveDescriptor curve,
                Color color,
                ref ushort vertexIndex
            )
            {
                Vector2 tip = curve.End;
                Vector2 direction = NormalizeGraphDirection(curve.End - curve.Evaluate(0.96f));
                Vector2 normal = new(-direction.y, direction.x);
                Vector2 baseCenter = tip - direction * 11f;
                ushort startIndex = vertexIndex;
                mesh.SetNextVertex(CreateGraphVertex(tip, color));
                mesh.SetNextVertex(CreateGraphVertex(baseCenter + normal * 5f, color));
                mesh.SetNextVertex(CreateGraphVertex(baseCenter - normal * 5f, color));
                vertexIndex += 3;
                mesh.SetNextIndex(startIndex);
                mesh.SetNextIndex((ushort)(startIndex + 1));
                mesh.SetNextIndex((ushort)(startIndex + 2));
            }

            private static Vertex CreateGraphVertex(Vector2 position, Color color)
            {
                return new Vertex
                {
                    position = new Vector3(position.x, position.y, Vertex.nearZ),
                    tint = color,
                };
            }
        }

        internal static Color CreateGraphFeatherColor(Color color)
        {
            return new Color(color.r, color.g, color.b, color.a * 0.22f);
        }

        internal static Vector2 NormalizeGraphDirection(Vector2 direction)
        {
            return direction.sqrMagnitude <= Mathf.Epsilon ? Vector2.right : direction.normalized;
        }

        internal static string FindGraphRouteAtPoint(
            IReadOnlyList<GraphCurveDescriptor> curves,
            Vector2 point,
            float hitRadius
        )
        {
            if (curves == null || curves.Count == 0 || hitRadius <= 0f)
            {
                return string.Empty;
            }

            const int sampleCount = 28;
            float nearestSquaredDistance = hitRadius * hitRadius;
            string nearestSelectionKey = string.Empty;
            for (int curveIndex = curves.Count - 1; curveIndex >= 0; curveIndex--)
            {
                GraphCurveDescriptor curve = curves[curveIndex];
                if (string.IsNullOrWhiteSpace(curve.SelectionKey))
                {
                    continue;
                }

                Vector2 previous = curve.Evaluate(0f);
                for (int sample = 1; sample <= sampleCount; sample++)
                {
                    Vector2 next = curve.Evaluate(sample / (float)sampleCount);
                    float squaredDistance = DistanceToGraphSegmentSquared(point, previous, next);
                    if (squaredDistance < nearestSquaredDistance)
                    {
                        nearestSquaredDistance = squaredDistance;
                        nearestSelectionKey = curve.SelectionKey;
                    }
                    previous = next;
                }
            }
            return nearestSelectionKey;
        }

        internal static IReadOnlyList<GraphCurveDescriptor> OrderGraphCurvesForRendering(
            IReadOnlyList<GraphCurveDescriptor> curves
        )
        {
            return curves == null
                ? Array.Empty<GraphCurveDescriptor>()
                : curves.OrderBy(curve => curve.Selected ? 1 : 0).ToArray();
        }

        internal static float CalculateLocalGraphRouteHitRadius(float worldScale)
        {
            return worldScale <= Mathf.Epsilon
                ? GraphRouteHitRadius
                : GraphRouteHitRadius / worldScale;
        }

        private static float DistanceToGraphSegmentSquared(
            Vector2 point,
            Vector2 segmentStart,
            Vector2 segmentEnd
        )
        {
            Vector2 segment = segmentEnd - segmentStart;
            float lengthSquared = segment.sqrMagnitude;
            if (lengthSquared <= Mathf.Epsilon)
            {
                return (point - segmentStart).sqrMagnitude;
            }

            float position = Mathf.Clamp01(
                Vector2.Dot(point - segmentStart, segment) / lengthSquared
            );
            Vector2 closest = segmentStart + segment * position;
            return (point - closest).sqrMagnitude;
        }

        private string _filterText = string.Empty;
        private string _selectedItemKey = string.Empty;
        private FlowGraphSnapshot _currentSnapshot = FlowGraphSnapshot.Empty;

        [MenuItem("Tools/Wallstop Studios/DxMessaging/Flow Graph")]
        public static void Open()
        {
            DxMessagingFlowGraphWindow window = GetWindow<DxMessagingFlowGraphWindow>();
            window.titleContent = new GUIContent(Title, DxMessagingEditorTheme.LoadIcon());
            window.minSize = new Vector2(520, 360);
            window.Refresh();
        }

        private void OnEnable()
        {
            DxMessagingEditorSourceLinks.MessageSourceIndexChanged -=
                HandleMessageSourceIndexChanged;
            DxMessagingEditorSourceLinks.MessageSourceIndexChanged +=
                HandleMessageSourceIndexChanged;
        }

        private void OnDisable()
        {
            DxMessagingEditorSourceLinks.MessageSourceIndexChanged -=
                HandleMessageSourceIndexChanged;
        }

        private void CreateGUI()
        {
            titleContent = new GUIContent(Title, DxMessagingEditorTheme.LoadIcon());
            Refresh();
        }

        private void Refresh()
        {
            DxMessagingEditorSourceLinks.AllowIncompleteMessageSourceIndexRetries();
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

        private void HandleMessageSourceIndexChanged()
        {
            if (string.IsNullOrWhiteSpace(_selectedItemKey))
            {
                return;
            }

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
            MessagingComponent[] components = FindMessagingComponentsInLoadedScenes();
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
            List<CapturedEdgeEmission> capturedEdgeEmissions = new();
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
                        CaptureEdgeEmissionEvidence(
                            capturedEdgeEmissions,
                            componentId,
                            listener.EmissionHistory
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

            AddEdgeEmissionEvidence(edgeBuilders.Values, capturedEdgeEmissions);

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
                    .ThenBy(edge => edge.Context, StringComparer.Ordinal)
                    .ThenBy(edge => edge.ContextId)
                    .ToArray(),
                tracePathBuilders
                    .Values.Select(builder => builder.Build())
                    .OrderBy(path => path.MessageTypeName, StringComparer.Ordinal)
                    .ThenBy(path => path.Context, StringComparer.Ordinal)
                    .ThenBy(path => path.ContextId)
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
            DxMessagingEditorTheme.ApplyWindow(root);
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
            toolbar.AddToClassList(DxMessagingEditorTheme.ToolbarClassName);
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

            FlowGraphFoldoutState foldoutState =
                content.userData as FlowGraphFoldoutState ?? new FlowGraphFoldoutState();
            VisualElement focusedElement =
                content.panel?.focusController.focusedElement as VisualElement;
            string focusedSelectionKey = GetSelectionKey(focusedElement);
            string focusedDetailRestorationId = GetDetailFocusRestorationId(focusedElement);
            bool focusedGraphControl =
                focusedElement != null
                && (
                    focusedElement.ClassListContains(GraphMessageNodeClassName)
                    || focusedElement.ClassListContains(GraphReceiverNodeClassName)
                    || focusedElement.ClassListContains(GraphConnectionClassName)
                );
            bool focusedDetailLink =
                focusedElement != null
                && focusedElement.ClassListContains(DxMessagingEditorTheme.DetailLinkClassName);
            bool restoreFocus =
                !string.IsNullOrWhiteSpace(focusedSelectionKey)
                && (focusedGraphControl || focusedDetailLink);
            bool restoreDetailLinkFocus =
                focusedDetailLink
                && !string.Equals(
                    viewState.SelectedItemKey,
                    focusedSelectionKey,
                    StringComparison.Ordinal
                );
            Foldout existingAnalysis = content.Q<Foldout>(AnalysisFoldoutName);
            Foldout existingRouteInsights = content.Q<Foldout>(RouteMapInsightsFoldoutName);
            Foldout existingMoreRoutes = content.Q<Foldout>(RouteMapMoreRoutesFoldoutName);
            Foldout existingTraceActivity = content.Q<Foldout>(TraceActivityFoldoutName);
            Foldout existingTopology = content.Q<Foldout>(TopologyFoldoutName);
            Foldout existingDetailsEvidence = content.Q<Foldout>(DetailsEvidenceFoldoutName);
            Foldout existingDetailsTechnical = content.Q<Foldout>(DetailsTechnicalFoldoutName);
            Foldout existingDetailsOverflow = content.Q<Foldout>(DetailsOverflowFoldoutName);
            Foldout existingDetailsRoutes = content.Q<Foldout>(DetailsRoutesFoldoutName);
            Foldout existingDetailsRoutesOverflow = content.Q<Foldout>(
                DetailsRoutesOverflowFoldoutName
            );
            if (existingAnalysis != null)
            {
                foldoutState.AnalysisExpanded = existingAnalysis.value;
            }
            if (existingRouteInsights != null)
            {
                foldoutState.RouteInsightsExpanded = existingRouteInsights.value;
            }
            if (existingMoreRoutes != null)
            {
                foldoutState.MoreRoutesExpanded = existingMoreRoutes.value;
            }
            if (existingTraceActivity != null)
            {
                foldoutState.TraceActivityExpanded = existingTraceActivity.value;
            }
            if (existingTopology != null)
            {
                foldoutState.TopologyExpanded = existingTopology.value;
            }
            if (existingDetailsEvidence != null)
            {
                foldoutState.DetailsEvidenceExpanded = existingDetailsEvidence.value;
            }
            if (existingDetailsTechnical != null)
            {
                foldoutState.DetailsTechnicalExpanded = existingDetailsTechnical.value;
            }
            if (existingDetailsOverflow != null)
            {
                foldoutState.DetailsOverflowExpanded = existingDetailsOverflow.value;
            }
            if (existingDetailsRoutes != null)
            {
                foldoutState.DetailsRoutesExpanded = existingDetailsRoutes.value;
            }
            if (existingDetailsRoutesOverflow != null)
            {
                foldoutState.DetailsRoutesOverflowExpanded = existingDetailsRoutesOverflow.value;
            }
            content.userData = foldoutState;
            content.Clear();
            bool hasGraphItems =
                visibleSnapshot.ComponentNodes.Count > 0
                || visibleSnapshot.MessageNodes.Count > 0
                || visibleSnapshot.Edges.Count > 0
                || visibleSnapshot.TracePaths.Count > 0;
            bool hasWarnings = visibleSnapshot.Warnings.Count > 0;
            bool hasObservedObjects =
                snapshot.ComponentNodes.Count > 0 || snapshot.MessageNodes.Count > 0;
            bool hasCapturedRoutes = snapshot.Edges.Count > 0 || snapshot.TracePaths.Count > 0;

            if (!hasGraphItems && !hasWarnings)
            {
                bool noRegistrations =
                    snapshot.ComponentNodes.Count == 0
                    && snapshot.MessageNodes.Count == 0
                    && snapshot.Edges.Count == 0
                    && snapshot.TracePaths.Count == 0
                    && snapshot.Warnings.Count == 0;
                string emptyTitle = noRegistrations ? "No registrations" : "No matches";
                string emptyText = noRegistrations
                    ? "No MessagingComponent registrations are loaded in open scenes."
                    : "No graph items match the current filter.";
                VisualElement empty = DxMessagingEditorTheme.CreateEmptyState(
                    emptyTitle,
                    emptyText,
                    bodyName: EmptyStateLabelName,
                    titleName: EmptyStateTitleLabelName
                );
                content.Add(empty);
            }
            else if (hasObservedObjects && !hasCapturedRoutes)
            {
                VisualElement empty = DxMessagingEditorTheme.CreateEmptyState(
                    "No live routes",
                    "MessagingComponents or recent messages are visible, but no live registration routes were captured. Enter Play mode (or restart it if already playing), make sure listeners are enabled, then click Refresh.",
                    bodyName: EmptyStateLabelName,
                    titleName: EmptyStateTitleLabelName
                );
                content.Add(empty);
            }
            else if (hasGraphItems)
            {
                FlowGraphSelectedItem selectedItem = ResolveSelectedItem(
                    visibleSnapshot,
                    viewState.SelectedItemKey
                );

                content.Add(
                    CreateGraphCanvas(
                        visibleSnapshot,
                        selectedItem.Key,
                        onSelectionChanged,
                        foldoutState.CanvasState
                    )
                );

                if (selectedItem.HasValue)
                {
                    content.Add(
                        CreateDetailsPane(
                            selectedItem,
                            snapshot,
                            visibleSnapshot,
                            foldoutState,
                            onSelectionChanged
                        )
                    );
                }

                Foldout analysis = CreateCollapsedFoldout(
                    AnalysisFoldoutName,
                    "Analysis and Raw Data"
                );
                analysis.value = foldoutState.AnalysisExpanded;

                VisualElement routeMap = CreateRouteMap(
                    visibleSnapshot,
                    selectedItem.Key,
                    onSelectionChanged
                );
                routeMap.Q<Foldout>(RouteMapInsightsFoldoutName).value =
                    foldoutState.RouteInsightsExpanded;
                Foldout moreRoutes = routeMap.Q<Foldout>(RouteMapMoreRoutesFoldoutName);
                if (moreRoutes != null)
                {
                    moreRoutes.value |= foldoutState.MoreRoutesExpanded;
                }
                analysis.Add(routeMap);

                if (visibleSnapshot.Edges.Count > 0)
                {
                    analysis.Add(CreateVisibleMessageLanes(visibleSnapshot));
                    analysis.Add(CreateVisibleTargetLanes(visibleSnapshot));
                }

                if (visibleSnapshot.TracePaths.Count > 0)
                {
                    Foldout traceActivity = CreateCollapsedFoldout(
                        TraceActivityFoldoutName,
                        "Trace Activity"
                    );
                    traceActivity.value = foldoutState.TraceActivityExpanded;
                    traceActivity.Add(CreateVisibleFlowCorridors(visibleSnapshot));
                    traceActivity.Add(CreateVisibleTraceRouteKindLanes(visibleSnapshot));
                    traceActivity.Add(CreateVisibleTraceIdLanes(visibleSnapshot));
                    traceActivity.Add(CreateVisibleTraceMessageLanes(visibleSnapshot));
                    traceActivity.Add(CreateVisibleTraceTargetLanes(visibleSnapshot));
                    traceActivity.Add(CreateVisibleContextLanes(visibleSnapshot));
                    traceActivity.Add(CreateTracePaths(visibleSnapshot));
                    analysis.Add(traceActivity);
                }

                Foldout topology = CreateCollapsedFoldout(TopologyFoldoutName, "Topology Details");
                topology.value = foldoutState.TopologyExpanded;
                topology.Add(CreateSectionTitle("Components"));
                foreach (FlowGraphComponentNode component in visibleSnapshot.ComponentNodes)
                {
                    topology.Add(
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

                topology.Add(CreateSectionTitle("Message Types"));
                foreach (FlowGraphMessageNode message in visibleSnapshot.MessageNodes)
                {
                    topology.Add(
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

                topology.Add(CreateSectionTitle("Registration Edges"));
                foreach (FlowGraphEdge edge in visibleSnapshot.Edges)
                {
                    topology.Add(
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
                analysis.Add(topology);
                content.Add(analysis);
            }

            foreach (string warning in visibleSnapshot.Warnings)
            {
                Label warningLabel = new(warning) { name = WarningLabelName };
                warningLabel.AddToClassList(DxMessagingEditorTheme.AdmonitionClassName);
                warningLabel.AddToClassList(DxMessagingEditorTheme.WarningClassName);
                DxMessagingEditorTheme.ApplyCompleteBorder(
                    warningLabel,
                    DxMessagingEditorPalette.Amber
                );
                warningLabel.style.marginTop = 8;
                warningLabel.style.paddingTop = 8;
                warningLabel.style.paddingRight = 8;
                warningLabel.style.paddingBottom = 8;
                warningLabel.style.paddingLeft = 8;
                warningLabel.style.whiteSpace = WhiteSpace.Normal;
                content.Add(warningLabel);
            }

            if (restoreFocus)
            {
                List<VisualElement> focusCandidates = content
                    .Query<VisualElement>()
                    .ToList()
                    .Where(element =>
                        element.focusable
                        && string.Equals(
                            GetSelectionKey(element),
                            focusedSelectionKey,
                            StringComparison.Ordinal
                        )
                        && (
                            restoreDetailLinkFocus
                                ? element.ClassListContains(
                                    DxMessagingEditorTheme.DetailLinkClassName
                                )
                                    && string.Equals(
                                        GetDetailFocusRestorationId(element),
                                        focusedDetailRestorationId,
                                        StringComparison.Ordinal
                                    )
                                : element.ClassListContains(GraphMessageNodeClassName)
                                    || element.ClassListContains(GraphReceiverNodeClassName)
                                    || element.ClassListContains(GraphConnectionClassName)
                        )
                    )
                    .ToList();
                VisualElement selectedControl = focusCandidates.FirstOrDefault();
                selectedControl?.Focus();
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
                AppendJsonProperty(
                    builder,
                    "messageKind",
                    message.MessageKindName,
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
                    .AppendLine(",");
                AppendJsonStringArray(
                    builder,
                    indentSize: 6,
                    "recentEmissionSites",
                    message.RecentEmissionSites,
                    trailingComma: true
                );
                AppendJsonStringArray(
                    builder,
                    indentSize: 6,
                    "recentContexts",
                    message.RecentContexts,
                    trailingComma: false
                );
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
                AppendJsonProperty(builder, "context", edge.Context, trailingComma: true);
                builder.Append("      \"contextId\": ").Append(edge.ContextId).AppendLine(",");
                builder
                    .Append("      \"registrationCount\": ")
                    .Append(edge.RegistrationCount)
                    .AppendLine(",");
                builder.Append("      \"callCount\": ").Append(edge.CallCount).AppendLine(",");
                builder
                    .Append("      \"recentTracedDeliveryCount\": ")
                    .Append(edge.RecentTracedDeliveryCount)
                    .AppendLine(",");
                AppendJsonStringArray(
                    builder,
                    indentSize: 6,
                    "recentEmissionSites",
                    edge.RecentEmissionSites,
                    trailingComma: false
                );
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
                builder.Append("      \"contextId\": ").Append(path.ContextId).AppendLine(",");
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
                InstanceId? contextId = emission.context ?? registration.Metadata.context;
                string context = CreateTraceContextText(contextId);
                string registrationTypeName = registration.Metadata.registrationType.ToString();
                string key = string.Join(
                    "|",
                    messageTypeName,
                    context,
                    contextId?.Id.ToString(CultureInfo.InvariantCulture) ?? "0",
                    componentId,
                    registrationTypeName
                );
                if (!tracePathBuilders.TryGetValue(key, out TracePathBuilder builder))
                {
                    builder = new TracePathBuilder(
                        messageTypeName,
                        context,
                        contextId?.Id ?? 0,
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
            bool globalObserver =
                metadata.registrationType == MessageRegistrationType.GlobalAcceptAll;
            string messageKey = globalObserver
                ? "message:<global-observer>"
                : CreateMessageKey(metadata.type);
            string messageTypeName = globalObserver
                ? GlobalObserverMessageName
                : CreateMessageTypeName(metadata.type);
            if (!messageNodes.TryGetValue(messageKey, out MessageNodeBuilder messageBuilder))
            {
                messageBuilder = new MessageNodeBuilder(messageTypeName);
                messageNodes[messageKey] = messageBuilder;
            }

            messageBuilder.ObserveRegistrationType(metadata.registrationType);

            messageBuilder.RegistrationCount++;
            messageBuilder.CallCount += registration.CallCount;
            if (metadata.registrationType != MessageRegistrationType.GlobalAcceptAll)
            {
                messageBuilder.RecentTracedDeliveryCount += recentTracedDeliveryCount;
            }

            string registrationTypeName = metadata.registrationType.ToString();
            string context = CreateContextDisplayText(metadata.context);
            string contextId = metadata.context.HasValue
                ? metadata.context.Value.Id.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
            string edgeKey = string.Join(
                "|",
                messageKey,
                componentId,
                registrationTypeName,
                contextId
            );
            if (!edgeBuilders.TryGetValue(edgeKey, out EdgeBuilder edgeBuilder))
            {
                edgeBuilder = new EdgeBuilder(
                    messageTypeName,
                    componentId,
                    componentPath,
                    registrationTypeName,
                    context,
                    metadata.context
                );
                edgeBuilders[edgeKey] = edgeBuilder;
            }

            edgeBuilder.AddRegistrationHandle(registration.Handle);
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

                messageBuilder.ObserveEmission(emission);

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

        private static void CaptureEdgeEmissionEvidence(
            ICollection<CapturedEdgeEmission> capturedEmissions,
            string componentId,
            IEnumerable<MessageEmissionData> emissions
        )
        {
            if (capturedEmissions == null || emissions == null)
            {
                return;
            }

            foreach (MessageEmissionData emission in emissions)
            {
                if (emission.registrationHandle != default(MessageRegistrationHandle))
                {
                    capturedEmissions.Add(new CapturedEdgeEmission(componentId, emission));
                }
            }
        }

        private static void AddEdgeEmissionEvidence(
            IEnumerable<EdgeBuilder> edgeBuilders,
            IEnumerable<CapturedEdgeEmission> emissions
        )
        {
            Dictionary<EdgeEmissionKey, List<MessageEmissionData>> emissionsByRegistration = new();
            foreach (CapturedEdgeEmission capturedEmission in emissions)
            {
                MessageEmissionData emission = capturedEmission.Emission;
                EdgeEmissionKey key = new(
                    capturedEmission.ComponentId,
                    emission.registrationHandle
                );
                if (!emissionsByRegistration.TryGetValue(key, out List<MessageEmissionData> group))
                {
                    group = new List<MessageEmissionData>();
                    emissionsByRegistration[key] = group;
                }
                group.Add(emission);
            }

            foreach (EdgeBuilder edgeBuilder in edgeBuilders)
            {
                if (
                    string.Equals(
                        edgeBuilder.RegistrationTypeName,
                        MessageRegistrationType.GlobalAcceptAll.ToString(),
                        StringComparison.Ordinal
                    )
                )
                {
                    continue;
                }

                foreach (MessageRegistrationHandle handle in edgeBuilder.RegistrationHandles)
                {
                    EdgeEmissionKey key = new(edgeBuilder.TargetComponentId, handle);
                    if (
                        !emissionsByRegistration.TryGetValue(
                            key,
                            out List<MessageEmissionData> matchingEmissions
                        )
                    )
                    {
                        continue;
                    }

                    foreach (MessageEmissionData emission in matchingEmissions)
                    {
                        if (
                            !string.Equals(
                                edgeBuilder.MessageTypeName,
                                CreateMessageTypeName(emission.message?.MessageType),
                                StringComparison.Ordinal
                            )
                            || (
                                edgeBuilder.ContextId.HasValue
                                && (
                                    !emission.context.HasValue
                                    || edgeBuilder.ContextId.Value.Id != emission.context.Value.Id
                                )
                            )
                        )
                        {
                            continue;
                        }

                        edgeBuilder.ObserveEmission(emission);
                    }
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

            string normalizedRouteKind = DxMessagingEditorPalette.NormalizeRouteKind(
                edge.RegistrationTypeName
            );
            bool hasSpecificRegistrationContext =
                !string.IsNullOrWhiteSpace(edge.Context) && edge.Context != "<none>";
            bool routeUsesSpecificContext =
                normalizedRouteKind == DxMessagingEditorPalette.BroadcastKind
                || normalizedRouteKind == DxMessagingEditorPalette.TargetedKind;
            if (
                routeUsesSpecificContext
                && hasSpecificRegistrationContext
                && (
                    (edge.ContextId != 0 && path.ContextId != 0)
                        ? edge.ContextId != path.ContextId
                        : !string.Equals(edge.Context, path.Context, StringComparison.Ordinal)
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

        private static MessagingComponent[] FindMessagingComponentsInLoadedScenes()
        {
#if UNITY_2023_1_OR_NEWER
            // FindObjectsByType's two-argument (FindObjectsInactive, FindObjectsSortMode)
            // overload exists across all 2023.1+ editors, including every 6000.x. The
            // one-argument FindObjectsByType(FindObjectsInactive) convenience overload only
            // exists on some 6000.x patch releases (e.g. 6000.4, not 6000.3), so always pass
            // both arguments to stay portable across the whole 6000.x range.
            return UnityEngine.Object.FindObjectsByType<MessagingComponent>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None
            );
#else
            return UnityEngine.Object.FindObjectsOfType<MessagingComponent>(includeInactive: true);
#endif
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
            filter.AddToClassList(DxMessagingEditorTheme.SearchClassName);
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
            refresh.AddToClassList(DxMessagingEditorTheme.ToolButtonClassName);
            refresh.SetEnabled(onRefresh != null);
            refresh.style.marginRight = 6;
            controls.Add(refresh);

            export = new(() => onCopyExport?.Invoke(CreateExportText(snapshot, filter.value)))
            {
                name = ExportButtonName,
                text = "Copy JSON",
            };
            export.AddToClassList(DxMessagingEditorTheme.ToolButtonClassName);
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
            return CreateContextDisplayText(context);
        }

        private static string CreateContextDisplayText(InstanceId? context)
        {
            if (!context.HasValue)
            {
                return "<none>";
            }

            UnityEngine.Object contextObject = context.Value.Object;
            if (contextObject == null)
            {
                return "Instance " + context.Value.Id;
            }

            switch (contextObject)
            {
                case GameObject gameObject:
                    return GetHierarchyPath(gameObject.transform) + " (GameObject)";
                case Component component:
                    return $"{GetHierarchyPath(component.transform)} ({component.GetType().Name})";
                default:
                    return string.IsNullOrWhiteSpace(contextObject.name)
                        ? $"{contextObject.GetType().Name} ({context.Value.Id})"
                        : $"{contextObject.name} ({contextObject.GetType().Name})";
            }
        }

        private static string CreateMessageKindName(
            IMessage message,
            MessageRegistrationType registrationType = MessageRegistrationType.None
        )
        {
            if (registrationType == MessageRegistrationType.GlobalAcceptAll)
            {
                return "GLOBAL OBSERVER";
            }
            if (
                registrationType == MessageRegistrationType.Broadcast
                || registrationType == MessageRegistrationType.BroadcastPostProcessor
                || registrationType == MessageRegistrationType.BroadcastWithoutSource
                || registrationType == MessageRegistrationType.BroadcastWithoutSourcePostProcessor
                || registrationType == MessageRegistrationType.BroadcastInterceptor
                || message is IBroadcastMessage
            )
            {
                return "BROADCAST";
            }
            if (
                registrationType == MessageRegistrationType.Targeted
                || registrationType == MessageRegistrationType.TargetedPostProcessor
                || registrationType == MessageRegistrationType.TargetedWithoutTargeting
                || registrationType == MessageRegistrationType.TargetedWithoutTargetingPostProcessor
                || registrationType == MessageRegistrationType.TargetedInterceptor
                || message is ITargetedMessage
            )
            {
                return "TARGETED";
            }
            if (
                registrationType == MessageRegistrationType.Untargeted
                || registrationType == MessageRegistrationType.UntargetedPostProcessor
                || registrationType == MessageRegistrationType.UntargetedInterceptor
                || message is IUntargetedMessage
            )
            {
                return "GLOBAL";
            }

            return "MESSAGE";
        }

        private static Label CreateSectionTitle(string text)
        {
            Label title = new(text);
            title.AddToClassList(DxMessagingEditorTheme.CardLabelClassName);
            title.style.marginTop = 10;
            title.style.marginBottom = 4;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            return title;
        }

        private static Foldout CreateCollapsedFoldout(string name, string text)
        {
            Foldout foldout = new()
            {
                name = name,
                text = text,
                value = false,
            };
            foldout.style.marginTop = 8;
            return foldout;
        }

        private static VisualElement CreateGraphCanvas(
            FlowGraphVisibleSnapshot visibleSnapshot,
            string selectedItemKey,
            Action<string> onSelectionChanged,
            FlowGraphCanvasState canvasState
        )
        {
            VisualElement panel = new();
            DxMessagingEditorTheme.ApplyCompleteBorder(panel, DxMessagingEditorPalette.BorderPanel);
            panel.style.marginBottom = 8;
            panel.style.paddingTop = 8;
            panel.style.paddingRight = 8;
            panel.style.paddingBottom = 8;
            panel.style.paddingLeft = 8;

            VisualElement legend = new() { name = GraphLegendName };
            legend.style.flexDirection = FlexDirection.Row;
            legend.style.flexWrap = Wrap.Wrap;
            legend.style.alignItems = Align.Center;
            legend.style.marginBottom = 6;

            Label title = new("Live Route Graph");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginRight = 12;
            legend.Add(title);
            legend.Add(CreateGraphLegendBadge("MESSAGE", DxMessagingEditorPalette.AmberSoft));
            Label arrow = new("->");
            arrow.style.marginLeft = 6;
            arrow.style.marginRight = 6;
            legend.Add(arrow);
            legend.Add(CreateGraphLegendBadge("RECEIVER", DxMessagingEditorPalette.Amber));
            legend.Add(CreateGraphLegendBadge("BROADCAST", DxMessagingEditorPalette.Broadcast));
            legend.Add(CreateGraphLegendBadge("TARGETED", DxMessagingEditorPalette.Targeted));
            legend.Add(CreateGraphLegendBadge("GLOBAL", DxMessagingEditorPalette.Untargeted));

            Label controls = new("Click a route or node to inspect. Drag empty space to pan.")
            {
                name = GraphInteractionHintName,
            };
            controls.style.flexGrow = 1;
            controls.style.flexBasis = 260;
            controls.style.minWidth = 220;
            controls.style.marginTop = 4;
            controls.style.whiteSpace = WhiteSpace.Normal;
            controls.style.unityTextAlign = TextAnchor.MiddleRight;
            legend.Add(controls);
            VisualElement zoomControls = new() { name = GraphZoomControlsName };
            zoomControls.style.flexDirection = FlexDirection.Row;
            zoomControls.style.flexShrink = 0;
            zoomControls.style.alignItems = Align.Center;
            zoomControls.style.marginLeft = 8;
            zoomControls.style.marginTop = 4;
            Button zoomOut = CreateGraphControlButton(GraphZoomOutButtonName, "-", "Zoom out");
            Button fit = CreateGraphControlButton(GraphFitButtonName, "Fit", "Fit all routes");
            Button zoomIn = CreateGraphControlButton(GraphZoomInButtonName, "+", "Zoom in");
            Label zoomLabel = new("100%") { name = GraphZoomLabelName };
            zoomLabel.style.minWidth = 42;
            zoomLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            zoomControls.Add(zoomOut);
            zoomControls.Add(fit);
            zoomControls.Add(zoomIn);
            zoomControls.Add(zoomLabel);
            legend.Add(zoomControls);
            panel.Add(legend);

            GraphConnectionDescriptor[] connections = CreateGraphConnections(visibleSnapshot);
            string[] messageNames = visibleSnapshot
                .MessageNodes.Select(message => message.MessageTypeName)
                .Concat(connections.Select(connection => connection.MessageTypeName))
                .Where(messageTypeName => !string.IsNullOrWhiteSpace(messageTypeName))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(messageTypeName => messageTypeName, StringComparer.Ordinal)
                .ToArray();
            string[] receiverIds = visibleSnapshot
                .ComponentNodes.Select(component => component.Id)
                .Concat(connections.Select(connection => connection.TargetComponentId))
                .Where(componentId => !string.IsNullOrWhiteSpace(componentId))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            receiverIds = receiverIds
                .OrderBy(
                    componentId => CreateReceiverPath(visibleSnapshot, connections, componentId),
                    StringComparer.Ordinal
                )
                .ThenBy(componentId => componentId, StringComparer.Ordinal)
                .ToArray();
            OrderGraphLayersForReadability(ref messageNames, ref receiverIds, connections);

            int rowCount = Math.Max(messageNames.Length, receiverIds.Length);
            float graphHeight = Math.Max(
                GraphCanvasHeight,
                80f + rowCount * (GraphNodeHeight + GraphNodeGap)
            );
            const float contentWidth = 1100f;
            const float messageX = 60f;
            const float receiverX = 770f;
            Dictionary<string, Vector2> messagePositions = new(StringComparer.Ordinal);
            Dictionary<string, Vector2> receiverPositions = new(StringComparer.Ordinal);
            for (int index = 0; index < messageNames.Length; index++)
            {
                messagePositions[messageNames[index]] = new Vector2(
                    messageX,
                    CreateGraphNodeY(index, messageNames.Length, rowCount)
                );
            }
            for (int index = 0; index < receiverIds.Length; index++)
            {
                receiverPositions[receiverIds[index]] = new Vector2(
                    receiverX,
                    CreateGraphNodeY(index, receiverIds.Length, rowCount)
                );
            }
            Dictionary<string, int> orderedMessageIndexes = messageNames
                .Select((messageTypeName, index) => new { messageTypeName, index })
                .ToDictionary(
                    item => item.messageTypeName,
                    item => item.index,
                    StringComparer.Ordinal
                );
            Dictionary<string, int> orderedReceiverIndexes = receiverIds
                .Select((componentId, index) => new { componentId, index })
                .ToDictionary(item => item.componentId, item => item.index, StringComparer.Ordinal);
            Dictionary<string, GraphConnectionDescriptor[]> outgoingConnectionsByMessage =
                connections
                    .GroupBy(connection => connection.MessageTypeName, StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group =>
                            group
                                .OrderBy(candidate =>
                                    orderedReceiverIndexes.GetValueOrDefault(
                                        candidate.TargetComponentId,
                                        int.MaxValue
                                    )
                                )
                                .ThenBy(candidate => candidate.RouteKind, StringComparer.Ordinal)
                                .ThenBy(candidate => candidate.ContextId)
                                .ThenBy(candidate => candidate.Context, StringComparer.Ordinal)
                                .ToArray(),
                        StringComparer.Ordinal
                    );
            Dictionary<string, GraphConnectionDescriptor[]> incomingConnectionsByReceiver =
                connections
                    .GroupBy(connection => connection.TargetComponentId, StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group =>
                            group
                                .OrderBy(candidate =>
                                    orderedMessageIndexes.GetValueOrDefault(
                                        candidate.MessageTypeName,
                                        int.MaxValue
                                    )
                                )
                                .ThenBy(candidate => candidate.RouteKind, StringComparer.Ordinal)
                                .ThenBy(candidate => candidate.ContextId)
                                .ThenBy(candidate => candidate.Context, StringComparer.Ordinal)
                                .ToArray(),
                        StringComparer.Ordinal
                    );
            Dictionary<string, int> outgoingPortIndexes = new(StringComparer.Ordinal);
            foreach (GraphConnectionDescriptor[] outgoing in outgoingConnectionsByMessage.Values)
            {
                for (int index = 0; index < outgoing.Length; index++)
                {
                    outgoingPortIndexes[CreateGraphConnectionIdentity(outgoing[index])] = index;
                }
            }
            Dictionary<string, int> incomingPortIndexes = new(StringComparer.Ordinal);
            foreach (GraphConnectionDescriptor[] incoming in incomingConnectionsByReceiver.Values)
            {
                for (int index = 0; index < incoming.Length; index++)
                {
                    incomingPortIndexes[CreateGraphConnectionIdentity(incoming[index])] = index;
                }
            }

            string layoutSignature = CreateGraphLayoutSignature(
                messageNames,
                receiverIds,
                connections
            );
            if (
                !string.Equals(
                    canvasState.LayoutSignature,
                    layoutSignature,
                    StringComparison.Ordinal
                )
            )
            {
                canvasState.LayoutSignature = layoutSignature;
                canvasState.Initialized = false;
                canvasState.Pan = Vector2.zero;
                canvasState.Zoom = 1f;
            }

            VisualElement viewport = new() { name = GraphCanvasName, userData = canvasState };
            viewport.focusable = true;
            viewport.style.height = GraphCanvasHeight;
            viewport.style.flexShrink = 0;
            viewport.style.overflow = Overflow.Hidden;
            viewport.style.backgroundColor = EditorGUIUtility.isProSkin
                ? new Color(0.07f, 0.08f, 0.1f, 1f)
                : new Color(0.91f, 0.92f, 0.94f, 1f);
            DxMessagingEditorTheme.ApplyCompleteBorder(
                viewport,
                DxMessagingEditorPalette.BorderStrong
            );

            VisualElement graphContent = new();
            graphContent.style.position = Position.Absolute;
            graphContent.style.left = 0;
            graphContent.style.top = 0;
            graphContent.style.width = contentWidth;
            graphContent.style.height = graphHeight;
            viewport.Add(graphContent);

            List<GraphCurveDescriptor> curves = new();
            bool routeIsSelected = selectedItemKey.StartsWith("edge|", StringComparison.Ordinal);
            List<(
                GraphConnectionDescriptor connection,
                GraphCurveDescriptor curve,
                float position
            )> markers = new();
            foreach (
                IGrouping<string, GraphConnectionDescriptor> pair in connections.GroupBy(
                    connection => connection.MessageTypeName + "\n" + connection.TargetComponentId,
                    StringComparer.Ordinal
                )
            )
            {
                GraphConnectionDescriptor[] parallelConnections = pair.OrderBy(
                        connection => connection.RouteKind,
                        StringComparer.Ordinal
                    )
                    .ThenBy(connection => connection.ContextId)
                    .ThenBy(connection => connection.Context, StringComparer.Ordinal)
                    .ToArray();
                for (int index = 0; index < parallelConnections.Length; index++)
                {
                    GraphConnectionDescriptor connection = parallelConnections[index];
                    if (
                        !messagePositions.TryGetValue(
                            connection.MessageTypeName,
                            out Vector2 messagePosition
                        )
                        || !receiverPositions.TryGetValue(
                            connection.TargetComponentId,
                            out Vector2 receiverPosition
                        )
                    )
                    {
                        continue;
                    }

                    GraphConnectionDescriptor[] outgoingConnections = outgoingConnectionsByMessage[
                        connection.MessageTypeName
                    ];
                    GraphConnectionDescriptor[] incomingConnections = incomingConnectionsByReceiver[
                        connection.TargetComponentId
                    ];
                    string connectionIdentity = CreateGraphConnectionIdentity(connection);
                    float sourcePortOffset = CreateGraphPortOffset(
                        outgoingPortIndexes[connectionIdentity],
                        outgoingConnections.Length
                    );
                    float targetPortOffset = CreateGraphPortOffset(
                        incomingPortIndexes[connectionIdentity],
                        incomingConnections.Length
                    );
                    float parallelIndex = index - (parallelConnections.Length - 1) * 0.5f;
                    float curveOffset = parallelIndex * 8f;
                    Color edgeColor = connection.TraceOnly
                        ? DxMessagingEditorPalette.Trace
                        : DxMessagingEditorPalette.RouteKindColor(connection.RouteKind);
                    bool selected = string.Equals(
                        connection.SelectionKey,
                        selectedItemKey,
                        StringComparison.Ordinal
                    );
                    GraphCurveDescriptor curve = new(
                        new Vector2(
                            messagePosition.x + GraphNodeWidth + 7f,
                            messagePosition.y + GraphNodeHeight * 0.5f + sourcePortOffset
                        ),
                        new Vector2(
                            receiverPosition.x - 7f,
                            receiverPosition.y + GraphNodeHeight * 0.5f + targetPortOffset
                        ),
                        curveOffset,
                        edgeColor,
                        selected,
                        connection.SelectionKey,
                        dimmed: routeIsSelected && !selected
                    );
                    curves.Add(curve);
                    markers.Add(
                        (
                            connection,
                            curve,
                            CreateGraphMarkerPosition(
                                orderedMessageIndexes[connection.MessageTypeName],
                                messageNames.Length,
                                curveOffset
                            )
                        )
                    );
                }
            }

            FlowGraphEdgeLayer edgeLayer = new(curves, onSelectionChanged);
            edgeLayer.userData = edgeLayer.Curves;
            edgeLayer.style.position = Position.Absolute;
            edgeLayer.style.left = 0;
            edgeLayer.style.top = 0;
            edgeLayer.style.width = contentWidth;
            edgeLayer.style.height = graphHeight;
            graphContent.Add(edgeLayer);

            foreach (string messageTypeName in messageNames)
            {
                FlowGraphMessageNode? message = visibleSnapshot
                    .MessageNodes.Where(candidate =>
                        string.Equals(
                            candidate.MessageTypeName,
                            messageTypeName,
                            StringComparison.Ordinal
                        )
                    )
                    .Cast<FlowGraphMessageNode?>()
                    .FirstOrDefault();
                GraphConnectionDescriptor[] messageConnections = connections
                    .Where(connection =>
                        string.Equals(
                            connection.MessageTypeName,
                            messageTypeName,
                            StringComparison.Ordinal
                        )
                    )
                    .ToArray();
                int activityCount = messageConnections.Sum(connection => connection.ActivityCount);
                string messageKind = CreateGraphMessageKind(message, messageConnections);
                string selectionKey = message.HasValue
                    ? CreateMessageSelectionKey(message.Value)
                    : string.Empty;
                graphContent.Add(
                    CreateGraphNode(
                        messageKind,
                        CreateCompactGraphLabel(messageTypeName),
                        CreateGraphMessageMetrics(
                            message,
                            messageConnections,
                            messageKind,
                            activityCount
                        ),
                        CreateGraphMessageTooltip(message, messageTypeName, messageKind),
                        GraphMessageNodeClassName,
                        DxMessagingEditorPalette.AmberSoft,
                        messagePositions[messageTypeName],
                        outputPort: true,
                        string.Equals(selectionKey, selectedItemKey, StringComparison.Ordinal),
                        selectionKey,
                        onSelectionChanged
                    )
                );
            }

            foreach (string componentId in receiverIds)
            {
                FlowGraphComponentNode? component = visibleSnapshot
                    .ComponentNodes.Where(candidate =>
                        string.Equals(candidate.Id, componentId, StringComparison.Ordinal)
                    )
                    .Cast<FlowGraphComponentNode?>()
                    .FirstOrDefault();
                GraphConnectionDescriptor[] receiverConnections = connections
                    .Where(connection =>
                        string.Equals(
                            connection.TargetComponentId,
                            componentId,
                            StringComparison.Ordinal
                        )
                    )
                    .ToArray();
                string receiverPath = component.HasValue
                    ? component.Value.HierarchyPath
                    : receiverConnections
                        .Select(connection => connection.TargetComponentPath)
                        .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path))
                        ?? componentId;
                int activityCount = receiverConnections.Sum(connection => connection.ActivityCount);
                string stateText =
                    !component.HasValue || component.Value.ActiveInHierarchy
                        ? "active"
                        : "inactive";
                string selectionKey = component.HasValue
                    ? CreateComponentSelectionKey(component.Value)
                    : string.Empty;
                graphContent.Add(
                    CreateGraphNode(
                        "RECEIVER",
                        CreateCompactReceiverLabel(receiverPath),
                        new[]
                        {
                            new GraphNodeMetric(
                                "Routes",
                                receiverConnections.Length.ToString(CultureInfo.InvariantCulture)
                            ),
                            new GraphNodeMetric(
                                "Messages",
                                receiverConnections
                                    .Select(connection => connection.MessageTypeName)
                                    .Distinct(StringComparer.Ordinal)
                                    .Count()
                                    .ToString(CultureInfo.InvariantCulture)
                            ),
                            new GraphNodeMetric(
                                "Calls",
                                activityCount.ToString(CultureInfo.InvariantCulture)
                            ),
                            new GraphNodeMetric("State", stateText),
                        },
                        receiverPath,
                        GraphReceiverNodeClassName,
                        DxMessagingEditorPalette.Amber,
                        receiverPositions[componentId],
                        outputPort: false,
                        string.Equals(selectionKey, selectedItemKey, StringComparison.Ordinal),
                        selectionKey,
                        onSelectionChanged
                    )
                );
            }

            foreach (
                (
                    GraphConnectionDescriptor connection,
                    GraphCurveDescriptor curve,
                    float position
                ) in markers.OrderBy(marker => marker.curve.Selected ? 1 : 0)
            )
            {
                graphContent.Add(
                    CreateGraphConnectionMarker(connection, curve, position, onSelectionChanged)
                );
            }

            ConfigureGraphViewport(
                viewport,
                graphContent,
                canvasState,
                new Vector2(contentWidth, graphHeight),
                zoomOut,
                fit,
                zoomIn,
                zoomLabel
            );
            panel.Add(viewport);
            return panel;
        }

        private static Button CreateGraphControlButton(string name, string text, string tooltip)
        {
            Button button = new()
            {
                name = name,
                text = text,
                tooltip = tooltip,
            };
            button.AddToClassList(DxMessagingEditorTheme.ToolButtonClassName);
            button.style.minWidth = text.Length == 1 ? 26 : 38;
            button.style.marginLeft = 2;
            return button;
        }

        private static GraphConnectionDescriptor[] CreateGraphConnections(
            FlowGraphVisibleSnapshot visibleSnapshot
        )
        {
            if (visibleSnapshot.Edges.Count > 0)
            {
                return visibleSnapshot
                    .Edges.Select(edge => new GraphConnectionDescriptor(
                        edge.MessageTypeName,
                        edge.TargetComponentId,
                        edge.TargetComponentPath,
                        edge.RegistrationTypeName,
                        edge.Context,
                        edge.ContextId,
                        edge.RecentEmissionSites,
                        edge.CallCount,
                        CreateEdgeSelectionKey(edge),
                        traceOnly: false
                    ))
                    .ToArray();
            }

            return visibleSnapshot
                .TracePaths.GroupBy(path => new
                {
                    path.MessageTypeName,
                    path.TargetComponentId,
                    path.TargetComponentPath,
                    path.RegistrationTypeName,
                    path.Context,
                    path.ContextId,
                })
                .Select(group => new GraphConnectionDescriptor(
                    group.Key.MessageTypeName,
                    group.Key.TargetComponentId,
                    group.Key.TargetComponentPath,
                    group.Key.RegistrationTypeName,
                    group.Key.Context,
                    group.Key.ContextId,
                    Array.Empty<string>(),
                    group.Sum(path => path.RecentTracedDeliveryCount),
                    string.Empty,
                    traceOnly: true
                ))
                .OrderBy(connection => connection.MessageTypeName, StringComparer.Ordinal)
                .ThenBy(connection => connection.TargetComponentPath, StringComparer.Ordinal)
                .ThenBy(connection => connection.RouteKind, StringComparer.Ordinal)
                .ThenBy(connection => connection.Context, StringComparer.Ordinal)
                .ThenBy(connection => connection.ContextId)
                .ToArray();
        }

        private static void OrderGraphLayersForReadability(
            ref string[] messageNames,
            ref string[] receiverIds,
            IReadOnlyList<GraphConnectionDescriptor> connections
        )
        {
            const int sweepCount = 6;
            Dictionary<string, string[]> messagesByReceiver = connections
                .GroupBy(connection => connection.TargetComponentId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group =>
                        group
                            .Select(connection => connection.MessageTypeName)
                            .Distinct(StringComparer.Ordinal)
                            .ToArray(),
                    StringComparer.Ordinal
                );
            Dictionary<string, string[]> receiversByMessage = connections
                .GroupBy(connection => connection.MessageTypeName, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group =>
                        group
                            .Select(connection => connection.TargetComponentId)
                            .Distinct(StringComparer.Ordinal)
                            .ToArray(),
                    StringComparer.Ordinal
                );
            for (int sweep = 0; sweep < sweepCount; sweep++)
            {
                Dictionary<string, int> messageIndexes = CreateGraphOrderIndexes(messageNames);
                receiverIds = OrderGraphLayer(
                    receiverIds,
                    receiverId =>
                        messagesByReceiver
                            .GetValueOrDefault(receiverId, Array.Empty<string>())
                            .Select(messageName =>
                                messageIndexes.GetValueOrDefault(messageName, int.MaxValue)
                            )
                );

                Dictionary<string, int> receiverIndexes = CreateGraphOrderIndexes(receiverIds);
                messageNames = OrderGraphLayer(
                    messageNames,
                    messageName =>
                        receiversByMessage
                            .GetValueOrDefault(messageName, Array.Empty<string>())
                            .Select(receiverId =>
                                receiverIndexes.GetValueOrDefault(receiverId, int.MaxValue)
                            )
                );
            }
        }

        private static Dictionary<string, int> CreateGraphOrderIndexes(IReadOnlyList<string> values)
        {
            Dictionary<string, int> indexes = new(values.Count, StringComparer.Ordinal);
            for (int index = 0; index < values.Count; index++)
            {
                indexes[values[index]] = index;
            }
            return indexes;
        }

        private static string[] OrderGraphLayer(
            IReadOnlyList<string> values,
            Func<string, IEnumerable<int>> connectedIndexes
        )
        {
            Dictionary<string, int> previousIndexes = CreateGraphOrderIndexes(values);
            return values
                .OrderBy(value =>
                {
                    int[] indexes = connectedIndexes(value)
                        .Where(index => index != int.MaxValue)
                        .ToArray();
                    return indexes.Length == 0 ? double.MaxValue : indexes.Average();
                })
                .ThenBy(value => previousIndexes[value])
                .ThenBy(value => value, StringComparer.Ordinal)
                .ToArray();
        }

        private static string CreateGraphConnectionIdentity(GraphConnectionDescriptor connection)
        {
            return string.Join(
                "|",
                connection.MessageTypeName,
                connection.TargetComponentId,
                connection.RouteKind,
                connection.ContextId.ToString(CultureInfo.InvariantCulture)
            );
        }

        private static float CreateGraphPortOffset(int index, int count)
        {
            if (count <= 1)
            {
                return 0f;
            }

            const float usableHeightRatio = 0.64f;
            float halfRange = GraphNodeHeight * usableHeightRatio * 0.5f;
            return Mathf.Lerp(-halfRange, halfRange, index / (float)(count - 1));
        }

        private static VisualElement CreateGraphNode(
            string eyebrow,
            string title,
            IReadOnlyList<GraphNodeMetric> metrics,
            string tooltip,
            string className,
            Color accent,
            Vector2 position,
            bool outputPort,
            bool selected,
            string selectionKey,
            Action<string> onSelectionChanged
        )
        {
            VisualElement node = new() { tooltip = tooltip };
            node.AddToClassList(className);
            node.AddToClassList(DxMessagingEditorTheme.CardClassName);
            node.focusable = !string.IsNullOrWhiteSpace(selectionKey);
            node.userData = selectionKey;
            node.style.position = Position.Absolute;
            node.style.left = position.x;
            node.style.top = position.y;
            node.style.width = GraphNodeWidth;
            node.style.height = GraphNodeHeight;
            node.style.paddingTop = 8;
            node.style.paddingRight = 10;
            node.style.paddingBottom = 8;
            node.style.paddingLeft = 10;
            DxMessagingEditorTheme.ApplyCompleteBorder(node, accent);
            ConfigureGraphFocusIndicator(node, node, accent);
            if (selected)
            {
                node.AddToClassList(SelectedRowClassName);
                node.style.backgroundColor = DxMessagingEditorPalette.SelectedWash;
            }

            Label titleLabel = new(title);
            titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            titleLabel.style.fontSize = 13;
            titleLabel.style.maxWidth = GraphNodeWidth - 92f;
            titleLabel.style.whiteSpace = WhiteSpace.NoWrap;
            titleLabel.style.overflow = Overflow.Hidden;
            titleLabel.style.textOverflow = TextOverflow.Ellipsis;
            node.Add(titleLabel);

            Label eyebrowLabel = new(eyebrow);
            eyebrowLabel.style.position = Position.Absolute;
            eyebrowLabel.style.right = 10;
            eyebrowLabel.style.top = 9;
            eyebrowLabel.style.fontSize = 9;
            eyebrowLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            eyebrowLabel.style.color = accent;
            node.Add(eyebrowLabel);

            VisualElement metricsContainer = new();
            metricsContainer.style.marginTop = 8;
            foreach (GraphNodeMetric metric in metrics)
            {
                VisualElement metricRow = new();
                metricRow.AddToClassList(GraphNodeMetricClassName);
                metricRow.style.flexDirection = FlexDirection.Row;
                metricRow.style.marginBottom = 2;
                Label metricLabel = new(metric.Label);
                metricLabel.style.width = 72;
                metricLabel.style.fontSize = 10;
                metricLabel.style.opacity = 0.7f;
                metricRow.Add(metricLabel);
                Label metricValue = new(metric.Value);
                metricValue.style.flexGrow = 1;
                metricValue.style.fontSize = 11;
                metricValue.style.unityFontStyleAndWeight = FontStyle.Bold;
                metricValue.style.whiteSpace = WhiteSpace.NoWrap;
                metricValue.style.overflow = Overflow.Hidden;
                metricValue.style.textOverflow = TextOverflow.Ellipsis;
                metricRow.Add(metricValue);
                metricsContainer.Add(metricRow);
            }
            node.Add(metricsContainer);

            VisualElement port = new();
            port.pickingMode = PickingMode.Ignore;
            port.style.position = Position.Absolute;
            float portHeight = GraphNodeHeight * 0.64f;
            port.style.top = (GraphNodeHeight - portHeight) * 0.5f;
            if (outputPort)
            {
                port.style.right = -3f;
            }
            else
            {
                port.style.left = -3f;
            }
            port.style.width = 6;
            port.style.height = portHeight;
            port.style.borderTopLeftRadius = 3;
            port.style.borderTopRightRadius = 3;
            port.style.borderBottomLeftRadius = 3;
            port.style.borderBottomRightRadius = 3;
            port.style.backgroundColor = accent;
            DxMessagingEditorTheme.ApplyCompleteBorder(port, DxMessagingEditorPalette.BorderStrong);
            node.Add(port);

            node.RegisterCallback<MouseDownEvent>(evt => evt.StopPropagation());
            if (onSelectionChanged != null && !string.IsNullOrWhiteSpace(selectionKey))
            {
                node.RegisterCallback<ClickEvent>(evt =>
                {
                    evt.StopPropagation();
                    onSelectionChanged.Invoke(selectionKey);
                });
                node.RegisterCallback<KeyDownEvent>(evt =>
                {
                    if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.Space)
                    {
                        return;
                    }
                    evt.StopPropagation();
                    onSelectionChanged.Invoke(selectionKey);
                });
            }
            return node;
        }

        private static VisualElement CreateGraphConnectionMarker(
            GraphConnectionDescriptor connection,
            GraphCurveDescriptor curve,
            float markerPosition,
            Action<string> onSelectionChanged
        )
        {
            Vector2 midpoint = curve.Evaluate(markerPosition);
            const float hitSize = 30f;
            float glyphSize = curve.Selected ? 18f : 12f;
            VisualElement marker = new()
            {
                tooltip =
                    $"{CreateConnectionFlowText(connection)}\n{connection.RouteKind} | {connection.ActivityCount} {(connection.TraceOnly ? "deliveries" : "calls")}{CreateConnectionEmissionSiteText(connection)}",
            };
            marker.AddToClassList(GraphConnectionClassName);
            marker.focusable = !string.IsNullOrWhiteSpace(connection.SelectionKey);
            marker.userData = connection.SelectionKey;
            if (curve.Selected)
            {
                marker.AddToClassList(SelectedRowClassName);
            }
            marker.style.position = Position.Absolute;
            marker.style.left = midpoint.x - hitSize * 0.5f;
            marker.style.top = midpoint.y - hitSize * 0.5f;
            marker.style.width = hitSize;
            marker.style.height = hitSize;

            VisualElement glyph = new();
            glyph.pickingMode = PickingMode.Ignore;
            glyph.style.position = Position.Absolute;
            glyph.style.left = (hitSize - glyphSize) * 0.5f;
            glyph.style.top = (hitSize - glyphSize) * 0.5f;
            glyph.style.width = glyphSize;
            glyph.style.height = glyphSize;
            glyph.style.backgroundColor = curve.Color;
            ApplyGraphConnectionMarkerShape(glyph, connection.RouteKind, glyphSize);
            DxMessagingEditorTheme.ApplyCompleteBorder(
                glyph,
                curve.Selected
                    ? DxMessagingEditorPalette.AmberSoft
                    : DxMessagingEditorPalette.BorderStrong
            );
            marker.Add(glyph);
            ConfigureGraphFocusIndicator(
                marker,
                glyph,
                curve.Selected
                    ? DxMessagingEditorPalette.AmberSoft
                    : DxMessagingEditorPalette.BorderStrong
            );
            marker.RegisterCallback<MouseDownEvent>(evt => evt.StopPropagation());
            if (onSelectionChanged != null && !string.IsNullOrWhiteSpace(connection.SelectionKey))
            {
                string selectionKey = connection.SelectionKey;
                marker.RegisterCallback<ClickEvent>(evt =>
                {
                    evt.StopPropagation();
                    onSelectionChanged.Invoke(selectionKey);
                });
                marker.RegisterCallback<KeyDownEvent>(evt =>
                {
                    if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.Space)
                    {
                        return;
                    }
                    evt.StopPropagation();
                    onSelectionChanged.Invoke(selectionKey);
                });
            }
            return marker;
        }

        private static void ConfigureGraphFocusIndicator(
            VisualElement focusTarget,
            VisualElement borderTarget,
            Color restingColor
        )
        {
            focusTarget.RegisterCallback<FocusInEvent>(_ =>
                ApplyGraphFocusIndicator(borderTarget, restingColor, focused: true)
            );
            focusTarget.RegisterCallback<FocusOutEvent>(_ =>
                ApplyGraphFocusIndicator(borderTarget, restingColor, focused: false)
            );
        }

        internal static void ApplyGraphFocusIndicator(
            VisualElement element,
            Color restingColor,
            bool focused
        )
        {
            float width = focused ? 3f : DxMessagingEditorTheme.CompleteBorderWidth;
            DxMessagingEditorTheme.ApplyCompleteBorder(
                element,
                focused ? DxMessagingEditorPalette.AmberSoft : restingColor,
                width
            );
        }

        internal static float CreateGraphMarkerPosition(
            int sourceIndex,
            int sourceCount,
            float curveOffset
        )
        {
            float sourcePosition =
                sourceCount <= 1
                    ? 0.5f
                    : Mathf.Lerp(0.38f, 0.62f, sourceIndex / (sourceCount - 1f));
            return Mathf.Clamp(sourcePosition + curveOffset / 160f, 0.3f, 0.7f);
        }

        private static void ApplyGraphConnectionMarkerShape(
            VisualElement marker,
            string routeKind,
            float size
        )
        {
            string normalizedKind = DxMessagingEditorPalette.NormalizeRouteKind(routeKind);
            float radius =
                normalizedKind == DxMessagingEditorPalette.BroadcastKind ? size * 0.5f : 2f;
            marker.style.borderTopLeftRadius = radius;
            marker.style.borderTopRightRadius = radius;
            marker.style.borderBottomLeftRadius = radius;
            marker.style.borderBottomRightRadius = radius;
            if (normalizedKind == DxMessagingEditorPalette.TargetedKind)
            {
                marker.transform.rotation = Quaternion.Euler(0f, 0f, 45f);
            }
        }

        private static string CreateConnectionEmissionSiteText(GraphConnectionDescriptor connection)
        {
            return connection.RecentEmissionSites.Count == 0
                ? string.Empty
                : "\nEMITTED BY " + string.Join(", ", connection.RecentEmissionSites);
        }

        private static string CreateConnectionFlowText(GraphConnectionDescriptor connection)
        {
            string context =
                string.IsNullOrWhiteSpace(connection.Context) || connection.Context == "<none>"
                    ? "ANY"
                    : connection.Context;
            switch (DxMessagingEditorPalette.NormalizeRouteKind(connection.RouteKind))
            {
                case DxMessagingEditorPalette.BroadcastKind:
                    return $"{context} -> {connection.MessageTypeName} -> {connection.TargetComponentPath}";
                case DxMessagingEditorPalette.TargetedKind:
                    return $"{connection.MessageTypeName} -> {context} -> {connection.TargetComponentPath}";
                case DxMessagingEditorPalette.UntargetedKind:
                    return $"GLOBAL BUS -> {connection.MessageTypeName} -> {connection.TargetComponentPath}";
                default:
                    return string.Equals(
                        connection.RouteKind,
                        MessageRegistrationType.GlobalAcceptAll.ToString(),
                        StringComparison.Ordinal
                    )
                        ? $"ANY MESSAGE -> {connection.TargetComponentPath}"
                        : $"{connection.MessageTypeName} -> {connection.TargetComponentPath}";
            }
        }

        private static void ConfigureGraphViewport(
            VisualElement viewport,
            VisualElement graphContent,
            FlowGraphCanvasState canvasState,
            Vector2 contentSize,
            Button zoomOut,
            Button fit,
            Button zoomIn,
            Label zoomLabel
        )
        {
            Vector2 viewportSize = Vector2.zero;
            void ApplyTransform()
            {
                graphContent.transform.position = new Vector3(
                    canvasState.Pan.x,
                    canvasState.Pan.y,
                    0f
                );
                graphContent.transform.scale = new Vector3(canvasState.Zoom, canvasState.Zoom, 1f);
                zoomLabel.text = Mathf.RoundToInt(canvasState.Zoom * 100f) + "%";
            }

            void ZoomAround(Vector2 focus, float nextZoom)
            {
                float previousZoom = Math.Max(GraphMinimumZoom, canvasState.Zoom);
                Vector2 contentCenter = contentSize * 0.5f;
                Vector2 contentPoint =
                    (focus - contentCenter - canvasState.Pan) / previousZoom + contentCenter;
                canvasState.Zoom = Mathf.Clamp(nextZoom, GraphMinimumZoom, GraphMaximumZoom);
                canvasState.Pan =
                    focus - contentCenter - (contentPoint - contentCenter) * canvasState.Zoom;
                canvasState.Initialized = true;
                ApplyTransform();
            }

            void FrameGraph()
            {
                if (viewportSize.x <= Mathf.Epsilon || viewportSize.y <= Mathf.Epsilon)
                {
                    return;
                }

                canvasState.Zoom = CalculateGraphFrameScale(viewportSize, contentSize);
                canvasState.Pan = viewportSize * 0.5f - contentSize * 0.5f;
                canvasState.Initialized = true;
                ApplyTransform();
            }

            ApplyTransform();
            zoomOut.RegisterCallback<ClickEvent>(evt =>
            {
                evt.StopPropagation();
                ZoomAround(viewportSize * 0.5f, canvasState.Zoom / 1.25f);
            });
            fit.RegisterCallback<ClickEvent>(evt =>
            {
                evt.StopPropagation();
                FrameGraph();
            });
            zoomIn.RegisterCallback<ClickEvent>(evt =>
            {
                evt.StopPropagation();
                ZoomAround(viewportSize * 0.5f, canvasState.Zoom * 1.25f);
            });
            bool panning = false;
            Vector2 lastMousePosition = Vector2.zero;
            viewport.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.button != 0)
                {
                    return;
                }

                panning = true;
                lastMousePosition = evt.localMousePosition;
                viewport.CaptureMouse();
                evt.StopPropagation();
            });
            viewport.RegisterCallback<MouseMoveEvent>(evt =>
            {
                if (!panning || !viewport.HasMouseCapture())
                {
                    return;
                }

                Vector2 currentPosition = evt.localMousePosition;
                canvasState.Pan += currentPosition - lastMousePosition;
                lastMousePosition = currentPosition;
                ApplyTransform();
                evt.StopPropagation();
            });
            viewport.RegisterCallback<MouseUpEvent>(evt =>
            {
                if (!panning || evt.button != 0)
                {
                    return;
                }

                panning = false;
                if (viewport.HasMouseCapture())
                {
                    viewport.ReleaseMouse();
                }
                evt.StopPropagation();
            });
            viewport.RegisterCallback<WheelEvent>(evt =>
            {
                float zoomFactor = evt.delta.y > 0f ? 0.88f : 1.14f;
                ZoomAround(evt.localMousePosition, canvasState.Zoom * zoomFactor);
                evt.StopPropagation();
            });

            EventCallback<GeometryChangedEvent> frameCallback = evt =>
            {
                if (evt.newRect.width <= Mathf.Epsilon || evt.newRect.height <= Mathf.Epsilon)
                {
                    return;
                }

                viewportSize = evt.newRect.size;
                if (!canvasState.Initialized)
                {
                    FrameGraph();
                }
            };
            viewport.RegisterCallback(frameCallback);
        }

        internal static float CalculateGraphFrameScale(Vector2 viewportSize, Vector2 contentSize)
        {
            if (contentSize.x <= Mathf.Epsilon || contentSize.y <= Mathf.Epsilon)
            {
                return 1f;
            }

            float horizontalScale = Math.Max(0f, viewportSize.x - 32f) / contentSize.x;
            float verticalScale = Math.Max(0f, viewportSize.y - 32f) / contentSize.y;
            return Mathf.Clamp(Math.Min(horizontalScale, verticalScale), GraphMinimumZoom, 1f);
        }

        private static string CreateGraphLayoutSignature(
            IEnumerable<string> messageNames,
            IEnumerable<string> receiverIds,
            IEnumerable<GraphConnectionDescriptor> connections
        )
        {
            StringBuilder signature = new();
            foreach (string messageName in messageNames)
            {
                signature.Append("m|").Append(messageName).Append('\n');
            }
            foreach (string receiverId in receiverIds)
            {
                signature.Append("r|").Append(receiverId).Append('\n');
            }
            foreach (
                GraphConnectionDescriptor connection in connections
                    .OrderBy(candidate => candidate.MessageTypeName, StringComparer.Ordinal)
                    .ThenBy(candidate => candidate.TargetComponentId, StringComparer.Ordinal)
                    .ThenBy(candidate => candidate.RouteKind, StringComparer.Ordinal)
                    .ThenBy(candidate => candidate.ContextId)
            )
            {
                signature
                    .Append("e|")
                    .Append(connection.MessageTypeName)
                    .Append('|')
                    .Append(connection.TargetComponentId)
                    .Append('|')
                    .Append(connection.RouteKind)
                    .Append('|')
                    .Append(connection.ContextId)
                    .Append('\n');
            }
            return signature.ToString();
        }

        private static Label CreateGraphLegendBadge(string text, Color color)
        {
            Label badge = new(text);
            DxMessagingEditorTheme.ApplyCompleteBorder(badge, color);
            badge.style.paddingTop = 2;
            badge.style.paddingRight = 6;
            badge.style.paddingBottom = 2;
            badge.style.paddingLeft = 6;
            badge.style.unityFontStyleAndWeight = FontStyle.Bold;
            return badge;
        }

        private static string CreateReceiverPath(
            FlowGraphVisibleSnapshot visibleSnapshot,
            IReadOnlyList<GraphConnectionDescriptor> connections,
            string componentId
        )
        {
            foreach (FlowGraphComponentNode component in visibleSnapshot.ComponentNodes)
            {
                if (string.Equals(component.Id, componentId, StringComparison.Ordinal))
                {
                    return component.HierarchyPath;
                }
            }

            return connections
                    .Where(connection =>
                        string.Equals(
                            connection.TargetComponentId,
                            componentId,
                            StringComparison.Ordinal
                        )
                    )
                    .Select(connection => connection.TargetComponentPath)
                    .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path))
                ?? componentId;
        }

        private static float CreateGraphNodeY(int index, int count, int rowCount)
        {
            float centeredOffset = (rowCount - count) * (GraphNodeHeight + GraphNodeGap) * 0.5f;
            return 54f + centeredOffset + index * (GraphNodeHeight + GraphNodeGap);
        }

        private static string CreateCompactGraphLabel(string messageTypeName)
        {
            string typeName = messageTypeName ?? string.Empty;
            int assemblyStart = typeName.LastIndexOf(" [", StringComparison.Ordinal);
            if (assemblyStart >= 0)
            {
                typeName = typeName.Substring(0, assemblyStart);
            }
            int namespaceSeparator = Math.Max(typeName.LastIndexOf('.'), typeName.LastIndexOf('+'));
            return namespaceSeparator >= 0 && namespaceSeparator < typeName.Length - 1
                ? typeName.Substring(namespaceSeparator + 1)
                : typeName;
        }

        private static string CreateCompactReceiverLabel(string hierarchyPath)
        {
            string path = hierarchyPath ?? string.Empty;
            int separator = path.LastIndexOf('/');
            return separator >= 0 && separator < path.Length - 1
                ? path.Substring(separator + 1)
                : path;
        }

        private static VisualElement CreateRouteMap(
            FlowGraphVisibleSnapshot visibleSnapshot,
            string selectedItemKey,
            Action<string> onSelectionChanged
        )
        {
            VisualElement routeMap = new() { name = RouteMapName };
            DxMessagingEditorTheme.ApplyCompleteBorder(
                routeMap,
                DxMessagingEditorPalette.BorderPanel
            );
            routeMap.style.marginBottom = 4;
            routeMap.style.paddingTop = 8;
            routeMap.style.paddingRight = 8;
            routeMap.style.paddingBottom = 8;
            routeMap.style.paddingLeft = 8;

            Label title = new("Route Map");
            title.AddToClassList(DxMessagingEditorTheme.CardLabelClassName);
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            routeMap.Add(title);

            Label overview = new(CreateRouteMapOverviewText(visibleSnapshot))
            {
                name = RouteMapOverviewLabelName,
            };
            overview.style.marginTop = 2;
            overview.style.whiteSpace = WhiteSpace.Normal;
            routeMap.Add(overview);

            Foldout insights = CreateCollapsedFoldout(
                RouteMapInsightsFoldoutName,
                "Route Insights"
            );
            Label summary = new(CreateRouteMapSummaryText(visibleSnapshot))
            {
                name = RouteMapSummaryLabelName,
            };
            summary.style.marginTop = 2;
            summary.style.whiteSpace = WhiteSpace.Normal;
            insights.Add(summary);
            routeMap.Add(insights);

            if (visibleSnapshot.Edges.Count == 0)
            {
                Label empty = new("No visible registration routes.");
                empty.AddToClassList(DxMessagingEditorTheme.EmptyBodyClassName);
                empty.style.marginTop = 6;
                routeMap.Add(empty);
                return routeMap;
            }

            int totalVisibleCalls = SumVisibleCalls(visibleSnapshot);
            FlowGraphEdge[] orderedRoutes = OrderRoutesForDisplay(visibleSnapshot.Edges).ToArray();
            foreach (FlowGraphEdge edge in orderedRoutes.Take(VisibleRouteLimit))
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

            if (orderedRoutes.Length > VisibleRouteLimit)
            {
                FlowGraphEdge[] remainingRoutes = orderedRoutes.Skip(VisibleRouteLimit).ToArray();
                Foldout moreRoutes = CreateCollapsedFoldout(
                    RouteMapMoreRoutesFoldoutName,
                    FormatCount(remainingRoutes.Length, "more route")
                );
                moreRoutes.value = remainingRoutes.Any(edge =>
                    string.Equals(
                        CreateEdgeSelectionKey(edge),
                        selectedItemKey,
                        StringComparison.Ordinal
                    )
                );
                foreach (FlowGraphEdge edge in remainingRoutes)
                {
                    string selectionKey = CreateEdgeSelectionKey(edge);
                    moreRoutes.Add(
                        CreateRouteMapRow(
                            edge,
                            CreateCallShareText(edge.CallCount, totalVisibleCalls),
                            string.Equals(selectionKey, selectedItemKey, StringComparison.Ordinal),
                            onSelectionChanged
                        )
                    );
                }
                routeMap.Add(moreRoutes);
            }

            return routeMap;
        }

        private static IOrderedEnumerable<FlowGraphEdge> OrderRoutesForDisplay(
            IEnumerable<FlowGraphEdge> edges
        )
        {
            return edges
                .OrderBy(edge =>
                    string.Equals(
                        edge.RegistrationTypeName,
                        nameof(MessageRegistrationType.GlobalAcceptAll),
                        StringComparison.Ordinal
                    )
                )
                .ThenByDescending(edge => edge.CallCount)
                .ThenByDescending(edge => edge.RecentTracedDeliveryCount)
                .ThenBy(edge => edge.MessageTypeName, StringComparer.Ordinal)
                .ThenBy(edge => edge.TargetComponentPath, StringComparer.Ordinal)
                .ThenBy(edge => edge.TargetComponentId, StringComparer.Ordinal)
                .ThenBy(edge => edge.RegistrationTypeName, StringComparer.Ordinal);
        }

        private static VisualElement CreateVisibleMessageLanes(
            FlowGraphVisibleSnapshot visibleSnapshot
        )
        {
            FlowGraphMessageLane[] lanes = BuildVisibleMessageLanes(visibleSnapshot);
            VisualElement messageLanes = new() { name = VisibleMessageLanesName };
            DxMessagingEditorTheme.ApplyCompleteBorder(
                messageLanes,
                DxMessagingEditorPalette.BorderPanel
            );
            messageLanes.style.marginTop = 8;
            messageLanes.style.marginBottom = 4;
            messageLanes.style.paddingTop = 8;
            messageLanes.style.paddingRight = 8;
            messageLanes.style.paddingBottom = 8;
            messageLanes.style.paddingLeft = 8;

            Label title = new("Visible Message Lanes");
            title.AddToClassList(DxMessagingEditorTheme.CardLabelClassName);
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
            row.AddToClassList(DxMessagingEditorTheme.CardClassName);
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            DxMessagingEditorTheme.ApplyCompleteBorder(row, DxMessagingEditorPalette.Amber);
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
            DxMessagingEditorTheme.ApplyCompleteBorder(
                targetLanes,
                DxMessagingEditorPalette.BorderPanel
            );
            targetLanes.style.marginTop = 8;
            targetLanes.style.marginBottom = 4;
            targetLanes.style.paddingTop = 8;
            targetLanes.style.paddingRight = 8;
            targetLanes.style.paddingBottom = 8;
            targetLanes.style.paddingLeft = 8;

            Label title = new("Visible Target Lanes");
            title.AddToClassList(DxMessagingEditorTheme.CardLabelClassName);
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
            row.AddToClassList(DxMessagingEditorTheme.CardClassName);
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            DxMessagingEditorTheme.ApplyCompleteBorder(row, DxMessagingEditorPalette.AmberSoft);
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
            DxMessagingEditorTheme.ApplyCompleteBorder(
                flowCorridors,
                DxMessagingEditorPalette.BorderPanel
            );
            flowCorridors.style.marginTop = 8;
            flowCorridors.style.marginBottom = 4;
            flowCorridors.style.paddingTop = 8;
            flowCorridors.style.paddingRight = 8;
            flowCorridors.style.paddingBottom = 8;
            flowCorridors.style.paddingLeft = 8;

            Label title = new("Visible Flow Corridors");
            title.AddToClassList(DxMessagingEditorTheme.CardLabelClassName);
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
            row.AddToClassList(DxMessagingEditorTheme.CardClassName);
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            DxMessagingEditorTheme.ApplyCompleteBorder(row, DxMessagingEditorPalette.AmberSoft);
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
            DxMessagingEditorTheme.ApplyCompleteBorder(
                traceRouteKindLanes,
                DxMessagingEditorPalette.BorderStrong
            );
            traceRouteKindLanes.style.marginTop = 8;
            traceRouteKindLanes.style.marginBottom = 4;
            traceRouteKindLanes.style.paddingTop = 8;
            traceRouteKindLanes.style.paddingRight = 8;
            traceRouteKindLanes.style.paddingBottom = 8;
            traceRouteKindLanes.style.paddingLeft = 8;

            Label title = new("Visible Trace Route Kind Lanes");
            title.AddToClassList(DxMessagingEditorTheme.CardLabelClassName);
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
            row.AddToClassList(DxMessagingEditorTheme.CardClassName);
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            DxMessagingEditorTheme.ApplyCompleteBorder(
                row,
                DxMessagingEditorPalette.RouteKindColor(lane.RouteKind)
            );
            row.style.marginTop = 6;
            row.style.paddingTop = 7;
            row.style.paddingRight = 8;
            row.style.paddingBottom = 7;
            row.style.paddingLeft = 10;

            Label routeKind = new(lane.RouteKind)
            {
                name = VisibleTraceRouteKindLaneRouteKindLabelName,
            };
            DxMessagingEditorTheme.AddRouteKindTypeBadgeClasses(routeKind, lane.RouteKind);
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
            DxMessagingEditorTheme.ApplyCompleteBorder(
                traceIdLanes,
                DxMessagingEditorPalette.BorderStrong
            );
            traceIdLanes.style.marginTop = 8;
            traceIdLanes.style.marginBottom = 4;
            traceIdLanes.style.paddingTop = 8;
            traceIdLanes.style.paddingRight = 8;
            traceIdLanes.style.paddingBottom = 8;
            traceIdLanes.style.paddingLeft = 8;

            Label title = new("Visible Trace Id Lanes");
            title.AddToClassList(DxMessagingEditorTheme.CardLabelClassName);
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
            row.AddToClassList(DxMessagingEditorTheme.CardClassName);
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            DxMessagingEditorTheme.ApplyCompleteBorder(row, DxMessagingEditorPalette.Trace);
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
            DxMessagingEditorTheme.ApplyCompleteBorder(
                traceMessageLanes,
                DxMessagingEditorPalette.BorderStrong
            );
            traceMessageLanes.style.marginTop = 8;
            traceMessageLanes.style.marginBottom = 4;
            traceMessageLanes.style.paddingTop = 8;
            traceMessageLanes.style.paddingRight = 8;
            traceMessageLanes.style.paddingBottom = 8;
            traceMessageLanes.style.paddingLeft = 8;

            Label title = new("Visible Trace Message Lanes");
            title.AddToClassList(DxMessagingEditorTheme.CardLabelClassName);
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
            row.AddToClassList(DxMessagingEditorTheme.CardClassName);
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            DxMessagingEditorTheme.ApplyCompleteBorder(row, DxMessagingEditorPalette.TraceMessage);
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
            DxMessagingEditorTheme.ApplyCompleteBorder(
                traceTargetLanes,
                DxMessagingEditorPalette.BorderStrong
            );
            traceTargetLanes.style.marginTop = 8;
            traceTargetLanes.style.marginBottom = 4;
            traceTargetLanes.style.paddingTop = 8;
            traceTargetLanes.style.paddingRight = 8;
            traceTargetLanes.style.paddingBottom = 8;
            traceTargetLanes.style.paddingLeft = 8;

            Label title = new("Visible Trace Target Lanes");
            title.AddToClassList(DxMessagingEditorTheme.CardLabelClassName);
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
            row.AddToClassList(DxMessagingEditorTheme.CardClassName);
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            DxMessagingEditorTheme.ApplyCompleteBorder(row, DxMessagingEditorPalette.TraceTarget);
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
            DxMessagingEditorTheme.ApplyCompleteBorder(
                contextLanes,
                DxMessagingEditorPalette.BorderStrong
            );
            contextLanes.style.marginTop = 8;
            contextLanes.style.marginBottom = 4;
            contextLanes.style.paddingTop = 8;
            contextLanes.style.paddingRight = 8;
            contextLanes.style.paddingBottom = 8;
            contextLanes.style.paddingLeft = 8;

            Label title = new("Visible Trace Context Lanes");
            title.AddToClassList(DxMessagingEditorTheme.CardLabelClassName);
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
            row.AddToClassList(DxMessagingEditorTheme.CardClassName);
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            DxMessagingEditorTheme.ApplyCompleteBorder(row, DxMessagingEditorPalette.Amber);
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
            DxMessagingEditorTheme.ApplyCompleteBorder(
                tracePaths,
                DxMessagingEditorPalette.BorderPanel
            );
            tracePaths.style.marginTop = 8;
            tracePaths.style.marginBottom = 4;
            tracePaths.style.paddingTop = 8;
            tracePaths.style.paddingRight = 8;
            tracePaths.style.paddingBottom = 8;
            tracePaths.style.paddingLeft = 8;

            Label title = new("Recent Trace Paths");
            title.AddToClassList(DxMessagingEditorTheme.CardLabelClassName);
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
            row.AddToClassList(DxMessagingEditorTheme.CardClassName);
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            DxMessagingEditorTheme.ApplyCompleteBorder(row, DxMessagingEditorPalette.Amber);
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

        internal static string CreateRouteMapSummaryText(
            FlowGraphSnapshot snapshot,
            string filterText = ""
        )
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            return CreateRouteMapSummaryText(FilterSnapshot(snapshot, filterText));
        }

        private static string CreateRouteMapOverviewText(FlowGraphVisibleSnapshot visibleSnapshot)
        {
            int routeCount = visibleSnapshot.Edges.Count;
            int messageCount = CountDistinct(
                visibleSnapshot.Edges.Select(edge => edge.MessageTypeName)
            );
            int receiverCount = CountDistinct(
                visibleSnapshot.Edges.Select(edge => edge.TargetComponentId)
            );
            int totalVisibleCalls = SumVisibleCalls(visibleSnapshot);
            return $"{FormatCount(routeCount, "route")} | {FormatCount(messageCount, "message")} | {FormatCount(receiverCount, "receiver")} | {FormatCount(totalVisibleCalls, "call")}";
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
            TraceIdPathSummary widestTrace = FindWidestTrace(tracePaths);
            if (widestTrace.PathCount <= 0)
            {
                return "Widest trace: none";
            }

            return $"Widest trace: {widestTrace.TraceId} ({FormatCount(widestTrace.PathCount, "path")})";
        }

        private static TraceIdPathSummary FindWidestTrace(
            IEnumerable<FlowGraphTracePath> tracePaths
        )
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
            return widestTrace;
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
            row.AddToClassList(DxMessagingEditorTheme.CardClassName);
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            DxMessagingEditorTheme.ApplyCompleteBorder(
                row,
                DxMessagingEditorPalette.RouteKindColor(edge.RegistrationTypeName)
            );
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

            Label routeKind = CreateRouteKindBadge(
                edge.RegistrationTypeName,
                RouteMapRouteKindLabelName
            );
            routeKind.style.marginLeft = 8;
            row.Add(routeKind);

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
            return CreateComponentSelectionKey(component.Id);
        }

        internal static string CreateMessageSelectionKey(FlowGraphMessageNode message)
        {
            return CreateMessageSelectionKey(message.MessageTypeName);
        }

        private static string CreateComponentSelectionKey(string componentId)
        {
            return string.IsNullOrWhiteSpace(componentId)
                ? string.Empty
                : "component|" + componentId;
        }

        private static string CreateMessageSelectionKey(string messageTypeName)
        {
            return string.IsNullOrWhiteSpace(messageTypeName)
                ? string.Empty
                : "message|" + messageTypeName;
        }

        internal static string CreateEdgeSelectionKey(FlowGraphEdge edge)
        {
            return CreateEdgeSelectionKey(
                edge.MessageTypeName,
                edge.TargetComponentId,
                edge.RegistrationTypeName,
                edge.ContextId
            );
        }

        private static string CreateGraphMessageKind(
            FlowGraphMessageNode? message,
            IReadOnlyList<GraphConnectionDescriptor> connections
        )
        {
            return CreateVisibleMessageKind(
                message?.MessageKindName,
                connections.Select(connection => connection.RouteKind)
            );
        }

        private static string CreateVisibleMessageKind(
            string fallbackKind,
            IEnumerable<string> routeKinds
        )
        {
            string[] visibleRouteKinds = routeKinds?.ToArray() ?? Array.Empty<string>();
            if (
                visibleRouteKinds.Any(kind =>
                    string.Equals(
                        kind,
                        MessageRegistrationType.GlobalAcceptAll.ToString(),
                        StringComparison.Ordinal
                    )
                )
            )
            {
                return "GLOBAL OBSERVER";
            }

            string[] normalizedKinds = visibleRouteKinds
                .Select(DxMessagingEditorPalette.NormalizeRouteKind)
                .Where(kind => !string.IsNullOrWhiteSpace(kind) && kind != "none")
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (normalizedKinds.Length > 1)
            {
                return "MIXED";
            }
            if (normalizedKinds.Length == 1)
            {
                switch (normalizedKinds[0])
                {
                    case "Broadcast":
                        return "BROADCAST";
                    case "Targeted":
                        return "TARGETED";
                    case "Untargeted":
                        return "GLOBAL";
                }
            }

            if (!string.IsNullOrWhiteSpace(fallbackKind))
            {
                return fallbackKind;
            }

            return "MESSAGE";
        }

        private static string CreateVisibleMessageKind(
            FlowGraphMessageNode message,
            FlowGraphVisibleSnapshot visibleSnapshot
        )
        {
            return CreateVisibleMessageKind(
                message.MessageKindName,
                visibleSnapshot
                    .Edges.Where(edge =>
                        string.Equals(
                            edge.MessageTypeName,
                            message.MessageTypeName,
                            StringComparison.Ordinal
                        )
                    )
                    .Select(edge => edge.RegistrationTypeName)
            );
        }

        private static IReadOnlyList<GraphNodeMetric> CreateGraphMessageMetrics(
            FlowGraphMessageNode? message,
            IReadOnlyList<GraphConnectionDescriptor> connections,
            string messageKind,
            int activityCount
        )
        {
            int receiverCount = connections
                .Select(connection => connection.TargetComponentId)
                .Distinct(StringComparer.Ordinal)
                .Count();
            int emissionSiteCount = connections
                .SelectMany(connection => connection.RecentEmissionSites)
                .Distinct(StringComparer.Ordinal)
                .Count();
            string contextSummary = CreateGraphContextMetric(connections);
            switch (messageKind)
            {
                case "BROADCAST":
                    return new[]
                    {
                        new GraphNodeMetric("Sources", contextSummary),
                        new GraphNodeMetric(
                            "Receivers",
                            receiverCount.ToString(CultureInfo.InvariantCulture)
                        ),
                        new GraphNodeMetric(
                            "Calls",
                            activityCount.ToString(CultureInfo.InvariantCulture)
                        ),
                    };
                case "TARGETED":
                    return new[]
                    {
                        new GraphNodeMetric("Targets", contextSummary),
                        new GraphNodeMetric(
                            "Handlers",
                            receiverCount.ToString(CultureInfo.InvariantCulture)
                        ),
                        new GraphNodeMetric(
                            "Call sites",
                            emissionSiteCount.ToString(CultureInfo.InvariantCulture)
                        ),
                    };
                case "GLOBAL":
                    return new[]
                    {
                        new GraphNodeMetric("Scope", "Global bus"),
                        new GraphNodeMetric(
                            "Receivers",
                            receiverCount.ToString(CultureInfo.InvariantCulture)
                        ),
                        new GraphNodeMetric(
                            "Calls",
                            activityCount.ToString(CultureInfo.InvariantCulture)
                        ),
                    };
                case "GLOBAL OBSERVER":
                    return new[]
                    {
                        new GraphNodeMetric("Scope", "Any message"),
                        new GraphNodeMetric(
                            "Observers",
                            receiverCount.ToString(CultureInfo.InvariantCulture)
                        ),
                        new GraphNodeMetric(
                            "Calls",
                            activityCount.ToString(CultureInfo.InvariantCulture)
                        ),
                    };
                default:
                    return new[]
                    {
                        new GraphNodeMetric(
                            "Routes",
                            connections.Count.ToString(CultureInfo.InvariantCulture)
                        ),
                        new GraphNodeMetric(
                            "Receivers",
                            receiverCount.ToString(CultureInfo.InvariantCulture)
                        ),
                        new GraphNodeMetric(
                            "Calls",
                            activityCount.ToString(CultureInfo.InvariantCulture)
                        ),
                    };
            }
        }

        private static string CreateGraphMessageTooltip(
            FlowGraphMessageNode? message,
            string messageTypeName,
            string messageKind
        )
        {
            if (!message.HasValue)
            {
                return messageKind + ": " + messageTypeName;
            }

            return $"{messageKind}: {messageTypeName}\nObserved emit sites: {JoinDistinctOrNone(message.Value.RecentEmissionSites)}\nObserved contexts: {JoinDistinctOrNone(message.Value.RecentContexts)}";
        }

        private static string CreateGraphContextMetric(
            IEnumerable<GraphConnectionDescriptor> connections
        )
        {
            GraphConnectionDescriptor[] values = connections
                .Where(connection =>
                    !string.IsNullOrWhiteSpace(connection.Context) && connection.Context != "<none>"
                )
                .GroupBy(
                    connection =>
                        connection.ContextId == 0
                            ? "text:" + connection.Context
                            : "id:" + connection.ContextId.ToString(CultureInfo.InvariantCulture),
                    StringComparer.Ordinal
                )
                .Select(group => group.First())
                .OrderBy(connection => connection.Context, StringComparer.Ordinal)
                .ThenBy(connection => connection.ContextId)
                .ToArray();
            if (values.Length == 0)
            {
                return "ANY";
            }

            string first = CreateCompactReceiverLabel(values[0].Context);
            return values.Length == 1
                ? first
                : values.Length.ToString(CultureInfo.InvariantCulture) + " observed";
        }

        private static string CreateEdgeSelectionKey(
            string messageTypeName,
            string targetComponentId,
            string registrationTypeName,
            int contextId
        )
        {
            return string.Join(
                "|",
                "edge",
                messageTypeName ?? string.Empty,
                targetComponentId ?? string.Empty,
                registrationTypeName ?? string.Empty,
                contextId.ToString(CultureInfo.InvariantCulture)
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
            row.Add(
                new Label($"{message.MessageKindName}: {message.MessageTypeName}")
                {
                    name = NodeNameLabelName,
                }
            );
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
            row.AddToClassList(DxMessagingEditorTheme.CardClassName);
            ApplySelection(row, selected);
            if (onSelectionChanged != null)
            {
                string selectionKey = CreateEdgeSelectionKey(edge);
                row.RegisterCallback<ClickEvent>(_ => onSelectionChanged.Invoke(selectionKey));
            }
            DxMessagingEditorTheme.ApplyCompleteBorder(
                row,
                DxMessagingEditorPalette.RouteKindColor(edge.RegistrationTypeName)
            );
            row.style.marginTop = 6;
            row.style.paddingTop = 7;
            row.style.paddingRight = 8;
            row.style.paddingBottom = 7;
            row.style.paddingLeft = 10;

            Label label = new(CreateEdgeFlowText(edge)) { name = EdgeLabelName };
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            row.Add(label);

            Label routeKind = CreateRouteKindBadge(
                edge.RegistrationTypeName,
                EdgeRouteKindLabelName
            );
            routeKind.style.marginTop = 4;
            row.Add(routeKind);

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

        private static Label CreateRouteKindBadge(string routeKindText, string name)
        {
            string labelText = CreateRouteKindBadgeText(routeKindText);
            Label routeKind = new(labelText) { name = name };
            DxMessagingEditorTheme.AddRouteKindTypeBadgeClasses(routeKind, routeKindText);
            if (IsGlobalObserverRegistration(routeKindText))
            {
                routeKind.AddToClassList(DxMessagingEditorTheme.TypeBadgeGlobalObserverClassName);
            }
            routeKind.style.unityFontStyleAndWeight = FontStyle.Bold;
            routeKind.style.whiteSpace = WhiteSpace.Normal;
            return routeKind;
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
            row.AddToClassList(DxMessagingEditorTheme.CardClassName);
            DxMessagingEditorTheme.ApplyCompleteBorder(row, borderColor);
            row.style.marginTop = 6;
            row.style.paddingTop = 7;
            row.style.paddingRight = 8;
            row.style.paddingBottom = 7;
            row.style.paddingLeft = 10;
            return row;
        }

        private static VisualElement CreateDetailsPane(
            FlowGraphSelectedItem selectedItem,
            FlowGraphSnapshot snapshot,
            FlowGraphVisibleSnapshot visibleSnapshot,
            FlowGraphFoldoutState foldoutState,
            Action<string> onSelectionChanged
        )
        {
            VisualElement details = new() { name = DetailsPaneName };
            details.AddToClassList(DxMessagingEditorTheme.CardClassName);
            DxMessagingEditorTheme.ApplyCompleteBorder(
                details,
                DxMessagingEditorPalette.BorderPanel
            );
            details.style.marginTop = 10;
            details.style.paddingTop = 12;
            details.style.paddingRight = 12;
            details.style.paddingBottom = 12;
            details.style.paddingLeft = 12;

            VisualElement header = new();
            header.AddToClassList(DxMessagingEditorTheme.DetailHeadClassName);
            header.style.flexWrap = Wrap.Wrap;
            VisualElement heading = new();
            heading.style.flexGrow = 1;
            heading.style.flexShrink = 1;
            Label title = new(CreateDetailsTitle(selectedItem)) { name = DetailsTitleLabelName };
            title.AddToClassList(DxMessagingEditorTheme.DetailTitleClassName);
            title.tooltip = CreateDetailsTitleTooltip(selectedItem);
            title.style.whiteSpace = WhiteSpace.Normal;
            title.style.marginLeft = 0;
            heading.Add(title);
            foreach (string titleMetadata in CreateDetailsTitleMetadata(selectedItem))
            {
                Label metadata = new(titleMetadata);
                metadata.AddToClassList(DxMessagingEditorTheme.DetailFrameClassName);
                metadata.tooltip = CreateDetailsTitleTooltip(selectedItem);
                metadata.style.marginTop = 2;
                metadata.style.whiteSpace = WhiteSpace.Normal;
                heading.Add(metadata);
            }
            header.Add(heading);
            if (selectedItem.Kind == FlowGraphSelectionKind.Edge)
            {
                header.Add(CreateRouteKindBadge(selectedItem.Edge.RegistrationTypeName, null));
            }
            else if (selectedItem.Kind == FlowGraphSelectionKind.Message)
            {
                string visibleMessageKind = CreateVisibleMessageKind(
                    selectedItem.Message,
                    visibleSnapshot
                );
                Label messageKindBadge = CreateGraphLegendBadge(
                    visibleMessageKind,
                    DxMessagingEditorPalette.AmberSoft
                );
                if (string.Equals(visibleMessageKind, "GLOBAL OBSERVER", StringComparison.Ordinal))
                {
                    messageKindBadge.AddToClassList(DxMessagingEditorTheme.TypeBadgeClassName);
                    messageKindBadge.AddToClassList(
                        DxMessagingEditorTheme.TypeBadgeGlobalObserverClassName
                    );
                }
                header.Add(messageKindBadge);
            }
            string messageTypeName =
                selectedItem.Kind == FlowGraphSelectionKind.Message
                    ? selectedItem.Message.MessageTypeName
                : selectedItem.Kind == FlowGraphSelectionKind.Edge
                    ? selectedItem.Edge.MessageTypeName
                : string.Empty;
            if (
                DxMessagingEditorSourceLinks.TryResolveMessageSource(
                    messageTypeName,
                    out DxMessagingEditorSourceLinks.SourceLocation messageSource
                )
            )
            {
                header.Add(
                    DxMessagingEditorSourceLinks.CreateSourceLinkButton(
                        "Open message source",
                        messageSource,
                        includeLocationInText: false
                    )
                );
            }
            details.Add(header);

            switch (selectedItem.Kind)
            {
                case FlowGraphSelectionKind.Component:
                    AddComponentDetailsCards(
                        details,
                        selectedItem.Component,
                        visibleSnapshot,
                        foldoutState.DetailsOverflowExpanded
                    );
                    break;
                case FlowGraphSelectionKind.Message:
                    AddMessageDetailsCards(
                        details,
                        selectedItem.Message,
                        snapshot,
                        visibleSnapshot,
                        foldoutState,
                        onSelectionChanged
                    );
                    break;
                case FlowGraphSelectionKind.Edge:
                    AddEdgeDetailsCards(
                        details,
                        selectedItem.Edge,
                        visibleSnapshot,
                        foldoutState.DetailsOverflowExpanded
                    );
                    break;
            }

            Foldout evidence = details.Q<Foldout>(DetailsEvidenceFoldoutName);
            if (evidence != null)
            {
                evidence.value = foldoutState.DetailsEvidenceExpanded;
            }
            Foldout technical = new()
            {
                name = DetailsTechnicalFoldoutName,
                text = "Diagnostics summary",
                value = foldoutState.DetailsTechnicalExpanded,
            };
            technical.style.marginTop = 4;
            AddDetailsDiagnosticsSummary(
                technical,
                selectedItem,
                visibleSnapshot,
                onSelectionChanged
            );
            string diagnosticsText = CreateDetailsBody(selectedItem, visibleSnapshot);
            Button copyDiagnostics = new()
            {
                name = DetailsCopyDiagnosticsButtonName,
                text = "Copy diagnostics",
                userData = diagnosticsText,
            };
            copyDiagnostics.AddToClassList(DxMessagingEditorTheme.ButtonGhostClassName);
            copyDiagnostics.AddToClassList(DxMessagingEditorTheme.ToolButtonClassName);
            copyDiagnostics.style.alignSelf = Align.FlexStart;
            copyDiagnostics.RegisterCallback<ClickEvent>(evt =>
            {
                evt.StopPropagation();
                EditorGUIUtility.systemCopyBuffer = diagnosticsText;
            });
            technical.Add(copyDiagnostics);
            details.Add(technical);

            return details;
        }

        private static void AddComponentDetailsCards(
            VisualElement details,
            FlowGraphComponentNode component,
            FlowGraphVisibleSnapshot visibleSnapshot,
            bool overflowExpanded
        )
        {
            FlowGraphEdge[] inboundEdges = visibleSnapshot
                .Edges.Where(edge =>
                    string.Equals(edge.TargetComponentId, component.Id, StringComparison.Ordinal)
                )
                .ToArray();
            int totalCalls = SumVisibleCalls(visibleSnapshot);
            int totalTracedDeliveries = SumVisibleTracedDeliveries(visibleSnapshot);
            details.Add(
                CreateDetailsMetricSection(
                    "COMPONENT",
                    new GraphNodeMetric(
                        "State",
                        component.ActiveInHierarchy ? "Active" : "Inactive"
                    ),
                    new GraphNodeMetric(
                        "Listeners",
                        component.ListenerCount.ToString(CultureInfo.InvariantCulture)
                    ),
                    new GraphNodeMetric(
                        "Registrations",
                        component.RegistrationCount.ToString(CultureInfo.InvariantCulture)
                    ),
                    new GraphNodeMetric(
                        "Calls",
                        component.CallCount.ToString(CultureInfo.InvariantCulture)
                    ),
                    new GraphNodeMetric(
                        "Local messages",
                        component.LocalMessageCount.ToString(CultureInfo.InvariantCulture)
                    )
                )
            );
            VisualElement traffic = CreateDetailsSection("VISIBLE TRAFFIC");
            traffic.Add(CreateDetailsKeyValue("Type", component.ComponentTypeName));
            traffic.Add(CreateHierarchyDetailRow("Hierarchy", component.HierarchyPath));
            traffic.Add(
                CreateDetailsKeyValue(
                    "Inbound routes",
                    inboundEdges.Length.ToString(CultureInfo.InvariantCulture)
                )
            );
            traffic.Add(
                CreateDetailsKeyValue(
                    "Call share",
                    CreateCallShareText(inboundEdges.Sum(edge => edge.CallCount), totalCalls)
                )
            );
            traffic.Add(
                CreateDetailsKeyValue(
                    "Trace share",
                    CreateCallShareText(
                        inboundEdges.Sum(edge => edge.RecentTracedDeliveryCount),
                        totalTracedDeliveries
                    )
                )
            );
            Foldout evidence = CreateDetailsEvidenceFoldout();
            evidence.Add(traffic);
            evidence.Add(
                CreateMessageTypesSection(
                    "MESSAGE TYPES",
                    inboundEdges.Select(edge => edge.MessageTypeName),
                    overflowExpanded
                )
            );
            details.Add(evidence);
        }

        private static void AddMessageDetailsCards(
            VisualElement details,
            FlowGraphMessageNode message,
            FlowGraphSnapshot snapshot,
            FlowGraphVisibleSnapshot visibleSnapshot,
            FlowGraphFoldoutState foldoutState,
            Action<string> onSelectionChanged
        )
        {
            FlowGraphEdge[] messageEdges = SelectMessageEdges(message, visibleSnapshot);
            FlowGraphTracePath[] messageTracePaths = SelectMessageTracePaths(
                message,
                visibleSnapshot
            );
            string visibleMessageKind = CreateVisibleMessageKind(
                message.MessageKindName,
                messageEdges.Select(edge => edge.RegistrationTypeName)
            );
            int tracedDeliveryCount = IsGlobalObserverMessage(message)
                ? messageTracePaths.Sum(path => path.RecentTracedDeliveryCount)
                : message.RecentTracedDeliveryCount;
            details.Add(
                CreateDetailsMetricSection(
                    "MESSAGE",
                    new GraphNodeMetric("Kind", visibleMessageKind),
                    new GraphNodeMetric(
                        "Routes",
                        messageEdges.Length.ToString(CultureInfo.InvariantCulture)
                    ),
                    new GraphNodeMetric(
                        "Receivers",
                        CountDistinct(messageEdges.Select(edge => edge.TargetComponentId))
                            .ToString(CultureInfo.InvariantCulture)
                    ),
                    new GraphNodeMetric(
                        "Registrations",
                        messageEdges
                            .Sum(edge => edge.RegistrationCount)
                            .ToString(CultureInfo.InvariantCulture)
                    ),
                    new GraphNodeMetric(
                        "Calls",
                        messageEdges
                            .Sum(edge => edge.CallCount)
                            .ToString(CultureInfo.InvariantCulture)
                    ),
                    new GraphNodeMetric(
                        "Traced",
                        tracedDeliveryCount.ToString(CultureInfo.InvariantCulture)
                    )
                )
            );
            details.Add(
                CreateDetailsRouteRoster(
                    messageEdges,
                    onSelectionChanged,
                    foldoutState.DetailsRoutesExpanded,
                    foldoutState.DetailsRoutesOverflowExpanded
                )
            );
            VisualElement evidence = CreateDetailsSection("RECENT EVIDENCE");
            AddHierarchyDetailValues(
                evidence,
                "Contexts",
                message.RecentContexts,
                extractDescriptor: true,
                selectionKeyFactory: value =>
                    FindContextComponentSelectionKey(value, message, snapshot, visibleSnapshot),
                onSelectionChanged: onSelectionChanged
            );
            AddSourceDetailValues(evidence, "Emitted by", message.RecentEmissionSites);
            Foldout evidenceFoldout = CreateDetailsEvidenceFoldout();
            evidenceFoldout.Add(evidence);
            if (visibleMessageKind == "GLOBAL OBSERVER")
            {
                evidenceFoldout.Add(
                    CreateMessageTypesSection(
                        "OBSERVED TYPES",
                        visibleSnapshot
                            .TracePaths.Where(path =>
                                string.Equals(
                                    path.RegistrationTypeName,
                                    MessageRegistrationType.GlobalAcceptAll.ToString(),
                                    StringComparison.Ordinal
                                )
                            )
                            .Select(path => path.MessageTypeName),
                        foldoutState.DetailsOverflowExpanded
                    )
                );
            }
            details.Add(evidenceFoldout);
        }

        private static VisualElement CreateDetailsRouteRoster(
            IReadOnlyList<FlowGraphEdge> edges,
            Action<string> onSelectionChanged,
            bool expanded,
            bool overflowExpanded
        )
        {
            FlowGraphEdge[] orderedEdges = edges
                .OrderByDescending(edge => edge.CallCount)
                .ThenBy(edge => edge.TargetComponentPath, StringComparer.Ordinal)
                .ThenBy(edge => edge.RegistrationTypeName, StringComparer.Ordinal)
                .ThenBy(edge => edge.ContextId)
                .ToArray();
            if (orderedEdges.Length == 0)
            {
                VisualElement empty = CreateDetailsSection("ROUTE ROSTER");
                empty.Add(CreateDetailsKeyValue("Routes", "none"));
                return empty;
            }

            Foldout roster = new()
            {
                name = DetailsRoutesFoldoutName,
                text = $"Route roster ({orderedEdges.Length})",
                value = false,
            };
            roster.style.marginBottom = 8;
            bool populated = false;
            void Populate()
            {
                if (populated)
                {
                    return;
                }

                foreach (FlowGraphEdge edge in orderedEdges.Take(VisibleDetailsRowLimit))
                {
                    roster.Add(CreateDetailsRouteRow(edge, onSelectionChanged));
                }
                if (orderedEdges.Length > VisibleDetailsRowLimit)
                {
                    FlowGraphEdge[] overflowEdges = orderedEdges
                        .Skip(VisibleDetailsRowLimit)
                        .ToArray();
                    Foldout overflow = new()
                    {
                        name = DetailsRoutesOverflowFoldoutName,
                        text = $"{overflowEdges.Length} more routes",
                        value = false,
                    };
                    bool overflowPopulated = false;
                    void PopulateOverflow()
                    {
                        if (overflowPopulated)
                        {
                            return;
                        }

                        foreach (FlowGraphEdge edge in overflowEdges)
                        {
                            overflow.Add(CreateDetailsRouteRow(edge, onSelectionChanged));
                        }
                        overflowPopulated = true;
                    }
                    overflow.RegisterValueChangedCallback(evt =>
                    {
                        if (evt.newValue)
                        {
                            PopulateOverflow();
                        }
                    });
                    overflow.SetValueWithoutNotify(overflowExpanded);
                    if (overflowExpanded)
                    {
                        PopulateOverflow();
                    }
                    roster.Add(overflow);
                }
                populated = true;
            }

            roster.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue)
                {
                    Populate();
                }
            });
            roster.SetValueWithoutNotify(expanded);
            if (expanded)
            {
                Populate();
            }
            return roster;
        }

        private static VisualElement CreateDetailsRouteRow(
            FlowGraphEdge edge,
            Action<string> onSelectionChanged
        )
        {
            string normalizedContext = NormalizeTraceContext(edge.Context);
            string exactContext =
                edge.ContextId == 0
                    ? normalizedContext
                    : $"{normalizedContext}, context #{edge.ContextId}";
            VisualElement row = new()
            {
                tooltip =
                    $"{edge.MessageTypeName} -> {edge.TargetComponentPath} [{edge.TargetComponentId}] ({edge.RegistrationTypeName}, {exactContext})",
            };
            row.AddToClassList(DetailsRouteRowClassName);
            row.AddToClassList(DxMessagingEditorTheme.CardClassName);
            row.style.marginTop = 5;
            row.style.paddingTop = 7;
            row.style.paddingRight = 8;
            row.style.paddingBottom = 7;
            row.style.paddingLeft = 8;
            DxMessagingEditorTheme.ApplyCompleteBorder(
                row,
                DxMessagingEditorPalette.RouteKindColor(edge.RegistrationTypeName)
            );

            VisualElement summary = new();
            summary.style.flexDirection = FlexDirection.Row;
            summary.style.flexWrap = Wrap.Wrap;
            summary.style.alignItems = Align.Center;
            VisualElement receiver = CreateRelationshipIdentity("RECEIVER", string.Empty);
            receiver.style.flexGrow = 1;
            receiver.Add(CreateHierarchyTrail(edge.TargetComponentPath));
            summary.Add(receiver);
            Label routeKind = CreateRouteKindBadge(edge.RegistrationTypeName, name: null);
            routeKind.style.marginLeft = 8;
            summary.Add(routeKind);
            Label activity = new($"{edge.CallCount} calls");
            activity.AddToClassList(DxMessagingEditorTheme.DetailFrameClassName);
            activity.style.marginLeft = 8;
            summary.Add(activity);
            row.Add(summary);
            row.Add(CreateDetailsKeyValue("Receiver ID", edge.TargetComponentId));
            row.Add(CreateDetailsKeyValue("Registration", edge.RegistrationTypeName));
            if (!IsMissingHierarchyValue(normalizedContext))
            {
                row.Add(
                    CreateHierarchyDetailRow(
                        CreateRouteContextLabel(edge.RegistrationTypeName, plural: false),
                        normalizedContext,
                        extractDescriptor: true
                    )
                );
            }
            if (edge.ContextId != 0)
            {
                row.Add(
                    CreateDetailsKeyValue(
                        "Context ID",
                        edge.ContextId.ToString(CultureInfo.InvariantCulture)
                    )
                );
            }
            ApplyDetailsSelection(
                row,
                CreateEdgeSelectionKey(edge),
                onSelectionChanged,
                addDesignClass: true,
                focusRestorationId: "route:" + CreateEdgeSelectionKey(edge)
            );
            return row;
        }

        private static void AddDetailsDiagnosticsSummary(
            VisualElement diagnostics,
            FlowGraphSelectedItem selectedItem,
            FlowGraphVisibleSnapshot visibleSnapshot,
            Action<string> onSelectionChanged
        )
        {
            FlowGraphEdge[] edges;
            FlowGraphTracePath[] tracePaths;
            switch (selectedItem.Kind)
            {
                case FlowGraphSelectionKind.Component:
                    edges = visibleSnapshot
                        .Edges.Where(edge =>
                            string.Equals(
                                edge.TargetComponentId,
                                selectedItem.Component.Id,
                                StringComparison.Ordinal
                            )
                        )
                        .ToArray();
                    tracePaths = visibleSnapshot
                        .TracePaths.Where(path =>
                            string.Equals(
                                path.TargetComponentId,
                                selectedItem.Component.Id,
                                StringComparison.Ordinal
                            )
                        )
                        .ToArray();
                    break;
                case FlowGraphSelectionKind.Message:
                    edges = SelectMessageEdges(selectedItem.Message, visibleSnapshot);
                    tracePaths = SelectMessageTracePaths(selectedItem.Message, visibleSnapshot);
                    break;
                case FlowGraphSelectionKind.Edge:
                    edges = new[] { selectedItem.Edge };
                    tracePaths = visibleSnapshot
                        .TracePaths.Where(path => EdgeMatchesTracePath(selectedItem.Edge, path))
                        .ToArray();
                    break;
                default:
                    edges = Array.Empty<FlowGraphEdge>();
                    tracePaths = Array.Empty<FlowGraphTracePath>();
                    break;
            }
            bool showAggregateRelationships = selectedItem.Kind != FlowGraphSelectionKind.Edge;
            bool hasBusiestEdge = TryGetBusiestTracedEdge(edges, out FlowGraphEdge busiestEdge);
            bool hasBusiestTracePath = TryGetBusiestTracePath(
                tracePaths,
                out FlowGraphTracePath busiestTracePath
            );
            bool mergeBusiestRelationships =
                hasBusiestEdge
                && hasBusiestTracePath
                && RelationshipsMatch(busiestEdge, busiestTracePath);

            VisualElement routeHealth = CreateDetailsMetricSection(
                "ROUTE HEALTH",
                new GraphNodeMetric("Routes", edges.Length.ToString(CultureInfo.InvariantCulture)),
                new GraphNodeMetric(
                    "Called",
                    edges.Count(edge => edge.CallCount > 0).ToString(CultureInfo.InvariantCulture)
                ),
                new GraphNodeMetric(
                    "Traced",
                    edges
                        .Count(edge => edge.RecentTracedDeliveryCount > 0)
                        .ToString(CultureInfo.InvariantCulture)
                ),
                new GraphNodeMetric(
                    "No calls",
                    edges.Count(edge => edge.CallCount <= 0).ToString(CultureInfo.InvariantCulture)
                )
            );
            if (showAggregateRelationships && hasBusiestEdge)
            {
                routeHealth.Add(
                    CreateDetailsRelationship(
                        mergeBusiestRelationships
                            ? "BUSIEST ROUTE + TRACE PATH"
                            : "BUSIEST TRACED ROUTE",
                        busiestEdge.MessageTypeName,
                        busiestEdge.TargetComponentPath,
                        busiestEdge.TargetComponentId,
                        busiestEdge.RegistrationTypeName,
                        busiestEdge.Context,
                        busiestEdge.RecentTracedDeliveryCount,
                        edges.Sum(edge => edge.RecentTracedDeliveryCount),
                        mergeBusiestRelationships ? "route traces" : "traced",
                        mergeBusiestRelationships ? busiestTracePath.RecentTracedDeliveryCount : -1,
                        mergeBusiestRelationships
                            ? tracePaths.Sum(path => path.RecentTracedDeliveryCount)
                            : 0,
                        mergeBusiestRelationships ? "path deliveries" : null,
                        onSelectionChanged,
                        selectedItem.Key
                    )
                );
            }
            else if (showAggregateRelationships)
            {
                routeHealth.Add(CreateDetailsKeyValue("Busiest traced route", "none"));
            }
            diagnostics.Add(routeHealth);

            VisualElement traceCoverage = CreateDetailsMetricSection(
                "TRACE COVERAGE",
                new GraphNodeMetric(
                    "Paths",
                    tracePaths.Length.ToString(CultureInfo.InvariantCulture)
                ),
                new GraphNodeMetric(
                    "Deliveries",
                    tracePaths
                        .Sum(path => path.RecentTracedDeliveryCount)
                        .ToString(CultureInfo.InvariantCulture)
                ),
                new GraphNodeMetric(
                    "Trace ids",
                    CountDistinctTraceIds(tracePaths).ToString(CultureInfo.InvariantCulture)
                ),
                new GraphNodeMetric(
                    "Contexts",
                    tracePaths
                        .Select(path => NormalizeTraceContext(path.Context))
                        .Distinct(StringComparer.Ordinal)
                        .Count()
                        .ToString(CultureInfo.InvariantCulture)
                )
            );
            traceCoverage.Add(CreateWidestTraceDetail(tracePaths));
            if (showAggregateRelationships && hasBusiestTracePath && !mergeBusiestRelationships)
            {
                traceCoverage.Add(
                    CreateDetailsRelationship(
                        "BUSIEST TRACE PATH",
                        busiestTracePath.MessageTypeName,
                        busiestTracePath.TargetComponentPath,
                        busiestTracePath.TargetComponentId,
                        busiestTracePath.RegistrationTypeName,
                        busiestTracePath.Context,
                        busiestTracePath.RecentTracedDeliveryCount,
                        tracePaths.Sum(path => path.RecentTracedDeliveryCount),
                        "deliveries",
                        onSelectionChanged: onSelectionChanged,
                        currentSelectionKey: selectedItem.Key
                    )
                );
            }
            else if (showAggregateRelationships && !hasBusiestTracePath)
            {
                traceCoverage.Add(CreateDetailsKeyValue("Busiest trace path", "none"));
            }
            diagnostics.Add(traceCoverage);
        }

        private static FlowGraphEdge[] SelectMessageEdges(
            FlowGraphMessageNode message,
            FlowGraphVisibleSnapshot visibleSnapshot
        )
        {
            bool globalObserver = IsGlobalObserverMessage(message);
            return visibleSnapshot
                .Edges.Where(edge =>
                    globalObserver
                        ? IsGlobalObserverRegistration(edge.RegistrationTypeName)
                        : string.Equals(
                            edge.MessageTypeName,
                            message.MessageTypeName,
                            StringComparison.Ordinal
                        )
                )
                .ToArray();
        }

        private static FlowGraphTracePath[] SelectMessageTracePaths(
            FlowGraphMessageNode message,
            FlowGraphVisibleSnapshot visibleSnapshot
        )
        {
            bool globalObserver = IsGlobalObserverMessage(message);
            return visibleSnapshot
                .TracePaths.Where(path =>
                    globalObserver
                        ? IsGlobalObserverRegistration(path.RegistrationTypeName)
                        : string.Equals(
                            path.MessageTypeName,
                            message.MessageTypeName,
                            StringComparison.Ordinal
                        )
                )
                .ToArray();
        }

        private static bool IsGlobalObserverMessage(FlowGraphMessageNode message)
        {
            return string.Equals(
                message.MessageTypeName,
                GlobalObserverMessageName,
                StringComparison.Ordinal
            );
        }

        private static bool IsGlobalObserverRegistration(string registrationTypeName)
        {
            return string.Equals(
                registrationTypeName,
                MessageRegistrationType.GlobalAcceptAll.ToString(),
                StringComparison.Ordinal
            );
        }

        private static bool TryGetBusiestTracedEdge(
            IEnumerable<FlowGraphEdge> edges,
            out FlowGraphEdge busiestEdge
        )
        {
            busiestEdge = edges
                .Where(edge => edge.RecentTracedDeliveryCount > 0)
                .OrderByDescending(edge => edge.RecentTracedDeliveryCount)
                .ThenBy(edge => edge.MessageTypeName, StringComparer.Ordinal)
                .ThenBy(edge => edge.TargetComponentPath, StringComparer.Ordinal)
                .ThenBy(edge => edge.RegistrationTypeName, StringComparer.Ordinal)
                .FirstOrDefault();
            return busiestEdge.RecentTracedDeliveryCount > 0;
        }

        private static bool TryGetBusiestTracePath(
            IEnumerable<FlowGraphTracePath> tracePaths,
            out FlowGraphTracePath busiestPath
        )
        {
            busiestPath = tracePaths
                .Where(path => path.RecentTracedDeliveryCount > 0)
                .OrderByDescending(path => path.RecentTracedDeliveryCount)
                .ThenBy(path => path.MessageTypeName, StringComparer.Ordinal)
                .ThenBy(path => path.TargetComponentPath, StringComparer.Ordinal)
                .ThenBy(path => path.RegistrationTypeName, StringComparer.Ordinal)
                .ThenBy(path => NormalizeTraceContext(path.Context), StringComparer.Ordinal)
                .FirstOrDefault();
            return busiestPath.RecentTracedDeliveryCount > 0;
        }

        private static bool RelationshipsMatch(FlowGraphEdge edge, FlowGraphTracePath tracePath)
        {
            bool stableIdentityMatches =
                string.Equals(
                    edge.MessageTypeName,
                    tracePath.MessageTypeName,
                    StringComparison.Ordinal
                )
                && string.Equals(
                    edge.TargetComponentId,
                    tracePath.TargetComponentId,
                    StringComparison.Ordinal
                )
                && string.Equals(
                    edge.TargetComponentPath,
                    tracePath.TargetComponentPath,
                    StringComparison.Ordinal
                )
                && string.Equals(
                    edge.RegistrationTypeName,
                    tracePath.RegistrationTypeName,
                    StringComparison.Ordinal
                );
            if (!stableIdentityMatches)
            {
                return false;
            }
            if (edge.ContextId != 0 || tracePath.ContextId != 0)
            {
                return edge.ContextId == tracePath.ContextId;
            }
            return string.Equals(
                CreateHierarchyComparisonKey(edge.Context),
                CreateHierarchyComparisonKey(tracePath.Context),
                StringComparison.Ordinal
            );
        }

        private static string CreateHierarchyComparisonKey(string value)
        {
            string normalized = NormalizeTraceContext(value);
            SplitHierarchyDescriptor(
                normalized,
                extractDescriptor: true,
                out string hierarchyPath,
                out _
            );
            return hierarchyPath;
        }

        private static VisualElement CreateWidestTraceDetail(
            IEnumerable<FlowGraphTracePath> tracePaths
        )
        {
            TraceIdPathSummary widestTrace = FindWidestTrace(tracePaths);
            VisualElement row = new();
            row.AddToClassList(DxMessagingEditorTheme.KeyValueClassName);
            Label keyLabel = new("Widest trace");
            keyLabel.AddToClassList(DxMessagingEditorTheme.KeyValueKeyClassName);
            keyLabel.style.width = 110;
            row.Add(keyLabel);
            VisualElement values = new();
            values.style.flexDirection = FlexDirection.Row;
            values.style.flexWrap = Wrap.Wrap;
            values.style.flexGrow = 1;
            Label traceIdentity = new(
                widestTrace.PathCount <= 0 ? "none" : $"Trace #{widestTrace.TraceId}"
            );
            traceIdentity.AddToClassList(DxMessagingEditorTheme.KeyValueValueClassName);
            values.Add(traceIdentity);
            if (widestTrace.PathCount > 0)
            {
                Label pathCount = new(FormatCount(widestTrace.PathCount, "path"));
                pathCount.AddToClassList(DxMessagingEditorTheme.DetailFrameClassName);
                pathCount.style.marginLeft = 10;
                values.Add(pathCount);
            }
            row.Add(values);
            return row;
        }

        private static void AddEdgeDetailsCards(
            VisualElement details,
            FlowGraphEdge edge,
            FlowGraphVisibleSnapshot visibleSnapshot,
            bool overflowExpanded
        )
        {
            FlowGraphTracePath[] tracePaths = visibleSnapshot
                .TracePaths.Where(path => EdgeMatchesTracePath(edge, path))
                .ToArray();
            details.Add(CreateRoutePathSection(edge));
            details.Add(
                CreateDetailsMetricSection(
                    "ROUTE ACTIVITY",
                    new GraphNodeMetric(
                        "Registrations",
                        edge.RegistrationCount.ToString(CultureInfo.InvariantCulture)
                    ),
                    new GraphNodeMetric(
                        "Calls",
                        edge.CallCount.ToString(CultureInfo.InvariantCulture)
                    ),
                    new GraphNodeMetric(
                        "Traced",
                        edge.RecentTracedDeliveryCount.ToString(CultureInfo.InvariantCulture)
                    ),
                    new GraphNodeMetric(
                        "Call share",
                        CreateCallShareText(edge.CallCount, SumVisibleCalls(visibleSnapshot))
                    ),
                    new GraphNodeMetric(
                        "Trace share",
                        CreateCallShareText(
                            edge.RecentTracedDeliveryCount,
                            SumVisibleTracedDeliveries(visibleSnapshot)
                        )
                    )
                )
            );
            VisualElement evidence = CreateDetailsSection("EMISSION EVIDENCE");
            evidence.Add(
                CreateDetailsKeyValue(
                    "Route kind",
                    DxMessagingEditorPalette.NormalizeRouteKind(edge.RegistrationTypeName)
                )
            );
            evidence.Add(CreateDetailsKeyValue("Registration", edge.RegistrationTypeName));
            evidence.Add(
                CreateHierarchyDetailRow(
                    CreateRouteContextLabel(edge.RegistrationTypeName, plural: false),
                    CreateReadableRouteContext(edge),
                    extractDescriptor: true
                )
            );
            evidence.Add(
                CreateDetailsKeyValue(
                    "Evidence scope",
                    "Exact component registration delivery record"
                )
            );
            AddSourceDetailValues(evidence, "Matching call site", edge.RecentEmissionSites);

            VisualElement traces = CreateDetailsSection("TRACE EVIDENCE");
            traces.Add(
                CreateDetailsKeyValue(
                    "Paths",
                    tracePaths.Length.ToString(CultureInfo.InvariantCulture)
                )
            );
            traces.Add(
                CreateDetailsKeyValue(
                    "Deliveries",
                    tracePaths
                        .Sum(path => path.RecentTracedDeliveryCount)
                        .ToString(CultureInfo.InvariantCulture)
                )
            );
            AddHierarchyDetailValues(
                traces,
                CreateRouteContextLabel(edge.RegistrationTypeName, plural: true),
                tracePaths.Select(path => NormalizeTraceContext(path.Context)).ToArray(),
                extractDescriptor: true
            );
            traces.Add(
                CreateDetailsKeyValue(
                    "Trace ids",
                    CountDistinctTraceIds(tracePaths).ToString(CultureInfo.InvariantCulture)
                )
            );
            Foldout evidenceFoldout = CreateDetailsEvidenceFoldout();
            evidenceFoldout.Add(evidence);
            evidenceFoldout.Add(traces);
            if (IsGlobalObserverRegistration(edge.RegistrationTypeName))
            {
                evidenceFoldout.Add(
                    CreateMessageTypesSection(
                        "OBSERVED TYPES",
                        tracePaths.Select(path => path.MessageTypeName),
                        overflowExpanded
                    )
                );
            }
            details.Add(evidenceFoldout);
        }

        private static Foldout CreateDetailsEvidenceFoldout()
        {
            Foldout evidence = new()
            {
                name = DetailsEvidenceFoldoutName,
                text = "Evidence and source details",
                value = false,
            };
            evidence.style.marginBottom = 6;
            return evidence;
        }

        private static VisualElement CreateRoutePathSection(FlowGraphEdge edge)
        {
            VisualElement section = CreateDetailsSection("ROUTE");
            VisualElement flow = new();
            flow.style.flexDirection = FlexDirection.Row;
            flow.style.flexWrap = Wrap.Wrap;
            flow.style.alignItems = Align.Center;
            string kind = DxMessagingEditorPalette.NormalizeRouteKind(edge.RegistrationTypeName);
            string context = CreateReadableRouteContext(edge);
            if (kind == DxMessagingEditorPalette.BroadcastKind)
            {
                flow.Add(CreateRouteEndpoint("SOURCE", context));
                flow.Add(CreateRouteArrow());
                flow.Add(
                    CreateRouteEndpoint("MESSAGE", CreateCompactGraphLabel(edge.MessageTypeName))
                );
                flow.Add(CreateRouteArrow());
                flow.Add(CreateRouteEndpoint("RECEIVER", edge.TargetComponentPath));
            }
            else if (kind == DxMessagingEditorPalette.TargetedKind)
            {
                flow.Add(
                    CreateRouteEndpoint("MESSAGE", CreateCompactGraphLabel(edge.MessageTypeName))
                );
                flow.Add(CreateRouteArrow());
                flow.Add(CreateRouteEndpoint("TARGET", context));
                flow.Add(CreateRouteArrow());
                flow.Add(CreateRouteEndpoint("HANDLER", edge.TargetComponentPath));
            }
            else if (kind == DxMessagingEditorPalette.UntargetedKind)
            {
                flow.Add(CreateRouteEndpoint("SCOPE", "Global bus"));
                flow.Add(CreateRouteArrow());
                flow.Add(
                    CreateRouteEndpoint("MESSAGE", CreateCompactGraphLabel(edge.MessageTypeName))
                );
                flow.Add(CreateRouteArrow());
                flow.Add(CreateRouteEndpoint("RECEIVER", edge.TargetComponentPath));
            }
            else
            {
                flow.Add(CreateRouteEndpoint("SCOPE", GlobalObserverMessageName));
                flow.Add(CreateRouteArrow());
                flow.Add(CreateRouteEndpoint("GLOBAL OBSERVER", edge.TargetComponentPath));
            }
            section.Add(flow);
            return section;
        }

        private static VisualElement CreateRouteEndpoint(string label, string value)
        {
            VisualElement endpoint = new();
            endpoint.style.flexGrow = 1;
            endpoint.style.flexBasis = 150;
            endpoint.style.minWidth = 130;
            endpoint.style.marginTop = 2;
            endpoint.style.marginBottom = 2;
            endpoint.style.paddingTop = 7;
            endpoint.style.paddingRight = 8;
            endpoint.style.paddingBottom = 7;
            endpoint.style.paddingLeft = 8;
            DxMessagingEditorTheme.ApplyCompleteBorder(
                endpoint,
                DxMessagingEditorPalette.BorderPanel
            );
            Label endpointLabel = new(label);
            endpointLabel.AddToClassList(DxMessagingEditorTheme.CardLabelClassName);
            endpointLabel.style.marginBottom = 2;
            endpoint.Add(endpointLabel);
            Label endpointValue = new(value);
            endpointValue.style.unityFontStyleAndWeight = FontStyle.Bold;
            endpointValue.style.whiteSpace = WhiteSpace.Normal;
            endpoint.Add(endpointValue);
            return endpoint;
        }

        private static Label CreateRouteArrow()
        {
            Label arrow = new("->");
            arrow.style.marginLeft = 6;
            arrow.style.marginRight = 6;
            arrow.style.unityFontStyleAndWeight = FontStyle.Bold;
            return arrow;
        }

        private static VisualElement CreateDetailsMetricSection(
            string title,
            params GraphNodeMetric[] metrics
        )
        {
            VisualElement section = CreateDetailsSection(title);
            VisualElement grid = new();
            grid.style.flexDirection = FlexDirection.Row;
            grid.style.flexWrap = Wrap.Wrap;
            foreach (GraphNodeMetric metric in metrics)
            {
                VisualElement tile = new();
                tile.AddToClassList(DetailsMetricClassName);
                tile.style.flexGrow = 1;
                tile.style.flexBasis = 105;
                tile.style.minWidth = 92;
                tile.style.marginRight = 6;
                tile.style.marginBottom = 4;
                tile.style.paddingTop = 6;
                tile.style.paddingRight = 7;
                tile.style.paddingBottom = 6;
                tile.style.paddingLeft = 7;
                DxMessagingEditorTheme.ApplyCompleteBorder(
                    tile,
                    DxMessagingEditorPalette.BorderPanel
                );
                Label metricLabel = new(metric.Label);
                metricLabel.AddToClassList(DxMessagingEditorTheme.CardLabelClassName);
                metricLabel.style.marginBottom = 2;
                tile.Add(metricLabel);
                Label metricValue = new(metric.Value);
                metricValue.style.unityFontStyleAndWeight = FontStyle.Bold;
                metricValue.style.whiteSpace = WhiteSpace.Normal;
                tile.Add(metricValue);
                grid.Add(tile);
            }
            section.Add(grid);
            return section;
        }

        private static VisualElement CreateDetailsSection(string title)
        {
            VisualElement section = new();
            section.AddToClassList(DetailsSectionClassName);
            section.AddToClassList(DxMessagingEditorTheme.CardClassName);
            section.style.marginBottom = 8;
            Label label = new(title);
            label.AddToClassList(DxMessagingEditorTheme.CardLabelClassName);
            section.Add(label);
            return section;
        }

        private static VisualElement CreateDetailsKeyValue(string key, string value)
        {
            VisualElement pair = new();
            pair.AddToClassList(DxMessagingEditorTheme.KeyValueClassName);
            Label keyLabel = new(key);
            keyLabel.AddToClassList(DxMessagingEditorTheme.KeyValueKeyClassName);
            keyLabel.style.width = 110;
            keyLabel.style.whiteSpace = WhiteSpace.Normal;
            pair.Add(keyLabel);
            Label valueLabel = new(string.IsNullOrWhiteSpace(value) ? "none" : value);
            valueLabel.AddToClassList(DxMessagingEditorTheme.KeyValueValueClassName);
            valueLabel.style.whiteSpace = WhiteSpace.Normal;
            pair.Add(valueLabel);
            return pair;
        }

        private static void AddHierarchyDetailValues(
            VisualElement section,
            string firstLabel,
            IReadOnlyList<string> values,
            bool extractDescriptor = false,
            Func<string, string> selectionKeyFactory = null,
            Action<string> onSelectionChanged = null
        )
        {
            string[] distinctValues = values
                .Where(value => !IsMissingHierarchyValue(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (distinctValues.Length == 0)
            {
                section.Add(CreateDetailsKeyValue(firstLabel, "none captured"));
                return;
            }

            for (int index = 0; index < distinctValues.Length; index++)
            {
                section.Add(
                    CreateHierarchyDetailRow(
                        index == 0 ? firstLabel : string.Empty,
                        distinctValues[index],
                        extractDescriptor,
                        selectionKeyFactory?.Invoke(distinctValues[index]),
                        onSelectionChanged
                    )
                );
            }
        }

        private static VisualElement CreateHierarchyDetailRow(
            string key,
            string value,
            bool extractDescriptor = false,
            string selectionKey = null,
            Action<string> onSelectionChanged = null
        )
        {
            string exactValue = string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
            VisualElement row = new() { tooltip = exactValue };
            row.AddToClassList(DxMessagingEditorTheme.KeyValueClassName);
            row.AddToClassList(DetailsHierarchyRowClassName);
            row.style.alignItems = Align.FlexStart;
            Label keyLabel = new(key);
            keyLabel.AddToClassList(DxMessagingEditorTheme.KeyValueKeyClassName);
            keyLabel.style.width = 110;
            keyLabel.style.whiteSpace = WhiteSpace.Normal;
            row.Add(keyLabel);
            row.Add(
                CreateHierarchyTrail(
                    exactValue,
                    extractDescriptor,
                    selectionKey,
                    onSelectionChanged
                )
            );
            return row;
        }

        private static VisualElement CreateHierarchyTrail(
            string value,
            bool extractDescriptor = false,
            string selectionKey = null,
            Action<string> onSelectionChanged = null
        )
        {
            SplitHierarchyDescriptor(
                value,
                extractDescriptor,
                out string path,
                out string descriptor
            );

            string[] segments = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(segment => segment.Trim())
                .Where(segment => segment.Length > 0)
                .ToArray();
            if (segments.Length == 0)
            {
                segments = new[] { "none" };
            }
            if (segments.Length > 4)
            {
                segments = new[]
                {
                    segments[0],
                    "...",
                    segments[segments.Length - 2],
                    segments[segments.Length - 1],
                };
            }

            VisualElement trail = new()
            {
                tooltip = string.IsNullOrWhiteSpace(value) ? "none" : value.Trim(),
            };
            trail.AddToClassList(DetailsHierarchyTrailClassName);
            trail.style.flexDirection = FlexDirection.Row;
            trail.style.flexWrap = Wrap.Wrap;
            trail.style.alignItems = Align.Center;
            trail.style.flexGrow = 1;
            trail.style.flexShrink = 1;
            for (int index = 0; index < segments.Length; index++)
            {
                VisualElement segmentGroup = new();
                segmentGroup.AddToClassList(DetailsHierarchySegmentClassName);
                segmentGroup.style.flexDirection = FlexDirection.Row;
                segmentGroup.style.alignItems = Align.Center;
                segmentGroup.style.flexShrink = 1;
                if (index > 0)
                {
                    Label separator = new(">");
                    separator.AddToClassList(DxMessagingEditorTheme.DetailFrameClassName);
                    separator.style.marginLeft = 5;
                    separator.style.marginRight = 5;
                    separator.style.flexShrink = 0;
                    segmentGroup.Add(separator);
                }

                Label segment = new(segments[index]);
                segment.AddToClassList(DxMessagingEditorTheme.KeyValueValueClassName);
                segment.style.flexShrink = 1;
                segment.style.whiteSpace = WhiteSpace.Normal;
                segment.style.overflow = Overflow.Hidden;
                segment.style.textOverflow = TextOverflow.Ellipsis;
                if (index == segments.Length - 1)
                {
                    segment.style.unityFontStyleAndWeight = FontStyle.Bold;
                }
                else
                {
                    segment.style.opacity = 0.72f;
                }
                segmentGroup.Add(segment);
                trail.Add(segmentGroup);
            }
            if (!string.IsNullOrWhiteSpace(descriptor))
            {
                Label descriptorLabel = new(descriptor);
                descriptorLabel.AddToClassList(DxMessagingEditorTheme.PriorityClassName);
                descriptorLabel.style.marginLeft = 8;
                trail.Add(descriptorLabel);
            }
            ApplyDetailsSelection(
                trail,
                selectionKey,
                onSelectionChanged,
                addDesignClass: true,
                focusRestorationId: "hierarchy:" + value
            );
            return trail;
        }

        private static void SplitHierarchyDescriptor(
            string value,
            bool extractDescriptor,
            out string path,
            out string descriptor
        )
        {
            path = string.IsNullOrWhiteSpace(value) ? "none" : value.Trim();
            descriptor = string.Empty;
            if (!extractDescriptor)
            {
                return;
            }

            int descriptorStart = path.LastIndexOf(" (", StringComparison.Ordinal);
            if (descriptorStart <= 0 || !path.EndsWith(")", StringComparison.Ordinal))
            {
                return;
            }

            descriptor = path.Substring(descriptorStart + 2, path.Length - descriptorStart - 3);
            path = path.Substring(0, descriptorStart);
        }

        private static string FindContextComponentSelectionKey(
            string hierarchyValue,
            FlowGraphMessageNode message,
            FlowGraphSnapshot snapshot,
            FlowGraphVisibleSnapshot visibleSnapshot
        )
        {
            if (
                !message.RecentContextComponentIds.TryGetValue(
                    hierarchyValue,
                    out string componentId
                ) || string.IsNullOrWhiteSpace(componentId)
            )
            {
                return string.Empty;
            }

            FlowGraphComponentNode[] matches = snapshot
                .ComponentNodes.Where(component =>
                    string.Equals(component.Id, componentId, StringComparison.Ordinal)
                )
                .Take(2)
                .ToArray();
            if (matches.Length != 1)
            {
                return string.Empty;
            }

            return visibleSnapshot.ComponentNodes.Any(component =>
                string.Equals(component.Id, matches[0].Id, StringComparison.Ordinal)
            )
                ? CreateComponentSelectionKey(matches[0])
                : string.Empty;
        }

        private static string CreateRouteContextLabel(string registrationTypeName, bool plural)
        {
            string normalizedKind = DxMessagingEditorPalette.NormalizeRouteKind(
                registrationTypeName
            );
            if (
                string.Equals(
                    normalizedKind,
                    DxMessagingEditorPalette.TargetedKind,
                    StringComparison.Ordinal
                )
            )
            {
                return plural ? "Targets" : "Target";
            }
            if (
                string.Equals(
                    normalizedKind,
                    DxMessagingEditorPalette.BroadcastKind,
                    StringComparison.Ordinal
                )
            )
            {
                return plural ? "Sources" : "Source";
            }
            return "Scope";
        }

        private static VisualElement CreateDetailsRelationship(
            string label,
            string messageTypeName,
            string targetPath,
            string targetComponentId,
            string registrationTypeName,
            string context,
            int deliveryCount,
            int totalDeliveryCount,
            string activityLabel,
            int secondaryDeliveryCount = -1,
            int secondaryTotalDeliveryCount = 0,
            string secondaryActivityLabel = null,
            Action<string> onSelectionChanged = null,
            string currentSelectionKey = null
        )
        {
            string relationshipFocusId = string.Join(
                ":",
                "relationship",
                label,
                messageTypeName,
                targetComponentId,
                registrationTypeName,
                NormalizeTraceContext(context)
            );
            VisualElement relationship = new()
            {
                tooltip =
                    $"{messageTypeName} -> {targetPath} ({registrationTypeName}, {NormalizeTraceContext(context)})",
            };
            relationship.AddToClassList(DetailsRelationshipClassName);
            relationship.style.marginTop = 6;
            relationship.style.paddingTop = 8;
            relationship.style.paddingRight = 9;
            relationship.style.paddingBottom = 8;
            relationship.style.paddingLeft = 9;
            relationship.style.backgroundColor = DxMessagingEditorPalette.SelectedWash;
            relationship.style.borderTopLeftRadius = 6;
            relationship.style.borderTopRightRadius = 6;
            relationship.style.borderBottomLeftRadius = 6;
            relationship.style.borderBottomRightRadius = 6;
            DxMessagingEditorTheme.ApplyCompleteBorder(
                relationship,
                DxMessagingEditorPalette.RouteKindColor(registrationTypeName)
            );

            VisualElement header = new();
            header.style.flexDirection = FlexDirection.Row;
            header.style.flexWrap = Wrap.Wrap;
            header.style.alignItems = Align.Center;
            Label relationshipLabel = new(label);
            relationshipLabel.AddToClassList(DxMessagingEditorTheme.CardLabelClassName);
            relationshipLabel.style.flexGrow = 1;
            relationshipLabel.style.marginBottom = 0;
            header.Add(relationshipLabel);
            Label routeKind = CreateRouteKindBadge(registrationTypeName, name: null);
            routeKind.style.marginLeft = 6;
            header.Add(routeKind);
            relationship.Add(header);

            VisualElement flow = new();
            flow.style.flexDirection = FlexDirection.Row;
            flow.style.flexWrap = Wrap.Wrap;
            flow.style.alignItems = Align.Center;
            flow.style.marginTop = 6;
            flow.Add(
                CreateRelationshipIdentity(
                    "MESSAGE",
                    CreateCompactGraphLabel(messageTypeName),
                    CreateMessageTitleMetadata(messageTypeName),
                    DetailsRelationshipMessageLinkName,
                    CreateMessageSelectionKey(messageTypeName),
                    onSelectionChanged,
                    string.Equals(
                        currentSelectionKey,
                        CreateMessageSelectionKey(messageTypeName),
                        StringComparison.Ordinal
                    ),
                    focusRestorationId: relationshipFocusId + ":message",
                    exactTooltip: messageTypeName
                )
            );
            flow.Add(CreateRouteArrow());
            VisualElement target = CreateRelationshipIdentity(
                "RECEIVER",
                string.Empty,
                name: DetailsRelationshipReceiverLinkName,
                selectionKey: CreateComponentSelectionKey(targetComponentId),
                onSelectionChanged: onSelectionChanged,
                active: string.Equals(
                    currentSelectionKey,
                    CreateComponentSelectionKey(targetComponentId),
                    StringComparison.Ordinal
                ),
                focusRestorationId: relationshipFocusId + ":receiver",
                exactTooltip: $"{targetPath} [{targetComponentId}]"
            );
            target.Add(CreateHierarchyTrail(targetPath));
            flow.Add(target);
            relationship.Add(flow);

            string normalizedContext = NormalizeTraceContext(context);
            SplitHierarchyDescriptor(
                normalizedContext,
                extractDescriptor: true,
                out string contextPath,
                out _
            );
            if (
                !IsMissingHierarchyValue(normalizedContext)
                && !string.Equals(contextPath, targetPath, StringComparison.Ordinal)
            )
            {
                relationship.Add(
                    CreateHierarchyDetailRow(
                        CreateRouteContextLabel(registrationTypeName, plural: false),
                        normalizedContext,
                        extractDescriptor: true
                    )
                );
            }
            relationship.Add(
                CreateRelationshipActivity(deliveryCount, totalDeliveryCount, activityLabel)
            );
            if (secondaryDeliveryCount >= 0)
            {
                relationship.Add(
                    CreateRelationshipActivity(
                        secondaryDeliveryCount,
                        secondaryTotalDeliveryCount,
                        secondaryActivityLabel
                    )
                );
            }
            return relationship;
        }

        private static VisualElement CreateRelationshipActivity(
            int deliveryCount,
            int totalDeliveryCount,
            string activityLabel
        )
        {
            VisualElement activity = new();
            activity.style.flexDirection = FlexDirection.Row;
            activity.style.flexWrap = Wrap.Wrap;
            activity.style.marginTop = 5;
            Label activityCount = new($"{deliveryCount} {activityLabel}");
            activityCount.AddToClassList(DxMessagingEditorTheme.DetailFrameClassName);
            activity.Add(activityCount);
            Label activityShare = new(CreateCallShareText(deliveryCount, totalDeliveryCount));
            activityShare.AddToClassList(DxMessagingEditorTheme.DetailFrameClassName);
            activityShare.style.marginLeft = 10;
            activity.Add(activityShare);
            return activity;
        }

        private static bool IsMissingHierarchyValue(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                || string.Equals(value.Trim(), "none", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value.Trim(), "<none>", StringComparison.OrdinalIgnoreCase);
        }

        private static VisualElement CreateRelationshipIdentity(
            string label,
            string value,
            IReadOnlyList<string> metadata = null,
            string name = null,
            string selectionKey = null,
            Action<string> onSelectionChanged = null,
            bool active = false,
            string focusRestorationId = null,
            string exactTooltip = null
        )
        {
            VisualElement identity = new() { name = name, tooltip = exactTooltip };
            identity.style.flexGrow = 1;
            identity.style.flexBasis = 150;
            identity.style.minWidth = 130;
            Label identityLabel = new(label);
            identityLabel.AddToClassList(DxMessagingEditorTheme.CardLabelClassName);
            identityLabel.style.marginBottom = 2;
            identity.Add(identityLabel);
            if (!string.IsNullOrWhiteSpace(value))
            {
                Label identityValue = new(value);
                identityValue.AddToClassList(DxMessagingEditorTheme.KeyValueValueClassName);
                identityValue.style.unityFontStyleAndWeight = FontStyle.Bold;
                identity.Add(identityValue);
            }
            if (metadata != null)
            {
                for (int index = 0; index < metadata.Count; index++)
                {
                    if (string.IsNullOrWhiteSpace(metadata[index]))
                    {
                        continue;
                    }
                    Label identityMetadata = new(metadata[index]);
                    identityMetadata.AddToClassList(DxMessagingEditorTheme.DetailFrameClassName);
                    identityMetadata.style.whiteSpace = WhiteSpace.Normal;
                    identity.Add(identityMetadata);
                }
            }
            if (active)
            {
                identity.AddToClassList(DxMessagingEditorTheme.DetailActiveClassName);
            }
            else
            {
                ApplyDetailsSelection(
                    identity,
                    selectionKey,
                    onSelectionChanged,
                    addDesignClass: true,
                    focusRestorationId: focusRestorationId
                );
            }
            return identity;
        }

        private static void ApplyDetailsSelection(
            VisualElement element,
            string selectionKey,
            Action<string> onSelectionChanged,
            bool addDesignClass,
            string focusRestorationId
        )
        {
            if (
                element == null
                || string.IsNullOrWhiteSpace(selectionKey)
                || onSelectionChanged == null
            )
            {
                return;
            }

            if (addDesignClass)
            {
                element.AddToClassList(DxMessagingEditorTheme.DetailLinkClassName);
            }
            element.focusable = true;
            element.userData = new DetailSelectionData(selectionKey, focusRestorationId);
            element.pickingMode = PickingMode.Position;
            if (string.IsNullOrWhiteSpace(element.tooltip))
            {
                element.tooltip = "Select in the Flow Graph";
            }
            element.RegisterCallback<ClickEvent>(evt =>
            {
                evt.StopPropagation();
                onSelectionChanged.Invoke(selectionKey);
            });
            element.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.Space)
                {
                    return;
                }

                evt.StopPropagation();
                onSelectionChanged.Invoke(selectionKey);
            });
        }

        private static string GetSelectionKey(VisualElement element)
        {
            return element?.userData is DetailSelectionData detailSelection
                ? detailSelection.SelectionKey
                : element?.userData as string;
        }

        private static string GetDetailFocusRestorationId(VisualElement element)
        {
            return element?.userData is DetailSelectionData detailSelection
                ? detailSelection.FocusRestorationId
                : string.Empty;
        }

        private static VisualElement CreateMessageTypesSection(
            string title,
            IEnumerable<string> messageTypeNames,
            bool overflowExpanded
        )
        {
            VisualElement section = CreateDetailsSection(title);
            string[] distinctTypes = messageTypeNames
                .Where(typeName => !string.IsNullOrWhiteSpace(typeName))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(typeName => typeName, StringComparer.Ordinal)
                .ToArray();
            if (distinctTypes.Length == 0)
            {
                section.Add(CreateDetailsKeyValue("Message types", "none"));
                return section;
            }

            int visibleCount = Math.Min(VisibleDetailsRowLimit, distinctTypes.Length);
            for (int index = 0; index < visibleCount; index++)
            {
                section.Add(CreateMessageTypeRow(distinctTypes[index]));
            }
            if (distinctTypes.Length > visibleCount)
            {
                Foldout overflow = new()
                {
                    name = DetailsOverflowFoldoutName,
                    text = $"{distinctTypes.Length - visibleCount} more message types",
                    value = false,
                };
                bool populated = false;
                void PopulateOverflow()
                {
                    if (populated)
                    {
                        return;
                    }

                    for (int index = visibleCount; index < distinctTypes.Length; index++)
                    {
                        overflow.Add(CreateMessageTypeRow(distinctTypes[index]));
                    }
                    populated = true;
                }
                overflow.RegisterValueChangedCallback(evt =>
                {
                    if (evt.newValue)
                    {
                        PopulateOverflow();
                    }
                });
                overflow.SetValueWithoutNotify(overflowExpanded);
                if (overflowExpanded)
                {
                    PopulateOverflow();
                }
                section.Add(overflow);
            }
            return section;
        }

        private static VisualElement CreateMessageTypeRow(string messageTypeName)
        {
            VisualElement row = new();
            row.AddToClassList(DxMessagingEditorTheme.KeyValueClassName);
            row.AddToClassList(DetailsMessageTypeRowClassName);
            row.tooltip = messageTypeName;
            VisualElement identity = new();
            identity.style.flexGrow = 1;
            identity.style.flexShrink = 1;
            Label typeLabel = new(CreateCompactGraphLabel(messageTypeName));
            typeLabel.AddToClassList(DxMessagingEditorTheme.KeyValueValueClassName);
            typeLabel.style.whiteSpace = WhiteSpace.Normal;
            identity.Add(typeLabel);
            IReadOnlyList<string> metadata = CreateMessageTitleMetadata(messageTypeName);
            for (int index = 0; index < metadata.Count; index++)
            {
                Label metadataLabel = new(metadata[index]);
                metadataLabel.AddToClassList(DxMessagingEditorTheme.DetailFrameClassName);
                metadataLabel.style.whiteSpace = WhiteSpace.Normal;
                identity.Add(metadataLabel);
            }
            row.Add(identity);
            if (
                DxMessagingEditorSourceLinks.TryResolveMessageSource(
                    messageTypeName,
                    out DxMessagingEditorSourceLinks.SourceLocation sourceLocation
                )
            )
            {
                row.Add(
                    DxMessagingEditorSourceLinks.CreateSourceLinkButton(
                        "Open source",
                        sourceLocation,
                        includeLocationInText: false
                    )
                );
            }
            return row;
        }

        private static void AddSourceDetailValues(
            VisualElement section,
            string firstLabel,
            IReadOnlyList<string> values
        )
        {
            if (values.Count == 0)
            {
                section.Add(CreateDetailsKeyValue(firstLabel, "none captured"));
                return;
            }

            for (int index = 0; index < values.Count; index++)
            {
                string value = values[index];
                VisualElement row = new() { tooltip = value };
                row.AddToClassList(DxMessagingEditorTheme.KeyValueClassName);
                row.AddToClassList(DetailsSourceRowClassName);
                row.style.alignItems = Align.Center;
                Label keyLabel = new(index == 0 ? firstLabel : string.Empty);
                keyLabel.AddToClassList(DxMessagingEditorTheme.KeyValueKeyClassName);
                keyLabel.style.width = 110;
                keyLabel.style.whiteSpace = WhiteSpace.Normal;
                row.Add(keyLabel);
                VisualElement identity = new();
                identity.style.flexGrow = 1;
                identity.style.flexShrink = 1;
                Label symbol = new(DxMessagingEditorSourceLinks.CreateCompactCallSiteLabel(value));
                symbol.AddToClassList(DxMessagingEditorTheme.KeyValueValueClassName);
                symbol.style.unityFontStyleAndWeight = FontStyle.Bold;
                symbol.style.whiteSpace = WhiteSpace.Normal;
                identity.Add(symbol);
                bool hasLocation = DxMessagingEditorSourceLinks.TryParseSourceLocation(
                    value,
                    out DxMessagingEditorSourceLinks.SourceLocation location
                );
                Label file = new(
                    hasLocation
                        ? $"{Path.GetFileName(location.AssetPath)}:{location.Line}"
                        : "Captured call site"
                );
                file.AddToClassList(DxMessagingEditorTheme.DetailFrameClassName);
                file.style.marginTop = 1;
                file.style.whiteSpace = WhiteSpace.Normal;
                file.style.flexShrink = 1;
                identity.Add(file);
                row.Add(identity);
                if (hasLocation && AssetDatabase.LoadMainAssetAtPath(location.AssetPath) != null)
                {
                    row.Add(
                        DxMessagingEditorSourceLinks.CreateSourceLinkButton(
                            "Open call site",
                            location,
                            includeLocationInText: false
                        )
                    );
                }
                section.Add(row);
            }
        }

        private static string CreateDetailsTitle(FlowGraphSelectedItem selectedItem)
        {
            switch (selectedItem.Kind)
            {
                case FlowGraphSelectionKind.Component:
                    return CreateCompactReceiverLabel(selectedItem.Component.HierarchyPath);
                case FlowGraphSelectionKind.Message:
                    return CreateCompactGraphLabel(selectedItem.Message.MessageTypeName);
                case FlowGraphSelectionKind.Edge:
                    return $"{CreateCompactGraphLabel(selectedItem.Edge.MessageTypeName)} -> {CreateCompactReceiverLabel(selectedItem.Edge.TargetComponentPath)}";
                default:
                    return "Selection Details";
            }
        }

        private static IReadOnlyList<string> CreateDetailsTitleMetadata(
            FlowGraphSelectedItem selectedItem
        )
        {
            switch (selectedItem.Kind)
            {
                case FlowGraphSelectionKind.Component:
                    return new[] { selectedItem.Component.ComponentTypeName };
                case FlowGraphSelectionKind.Message:
                    return CreateMessageTitleMetadata(selectedItem.Message.MessageTypeName);
                case FlowGraphSelectionKind.Edge:
                    return CreateMessageTitleMetadata(selectedItem.Edge.MessageTypeName);
                default:
                    return Array.Empty<string>();
            }
        }

        private static IReadOnlyList<string> CreateMessageTitleMetadata(string messageTypeName)
        {
            DxMessagingEditorSourceLinks.CapturedTypeIdentity identity =
                DxMessagingEditorSourceLinks.ParseCapturedTypeIdentity(messageTypeName);
            string owner = CreateCapturedTypeOwner(messageTypeName);
            List<string> metadata = new();
            if (!string.IsNullOrWhiteSpace(owner))
            {
                metadata.Add(owner);
            }
            if (!string.IsNullOrWhiteSpace(identity.AssemblyName))
            {
                metadata.Add(identity.AssemblyName + " assembly");
            }
            return metadata;
        }

        private static string CreateCapturedTypeOwner(string messageTypeName)
        {
            DxMessagingEditorSourceLinks.CapturedTypeIdentity identity =
                DxMessagingEditorSourceLinks.ParseCapturedTypeIdentity(messageTypeName);
            int typeSeparator = Math.Max(
                identity.TypeName.LastIndexOf('.'),
                identity.TypeName.LastIndexOf('+')
            );
            return typeSeparator > 0 ? identity.TypeName.Substring(0, typeSeparator) : string.Empty;
        }

        private static string CreateDetailsTitleTooltip(FlowGraphSelectedItem selectedItem)
        {
            switch (selectedItem.Kind)
            {
                case FlowGraphSelectionKind.Component:
                    return $"{selectedItem.Component.HierarchyPath} ({selectedItem.Component.ComponentTypeName})";
                case FlowGraphSelectionKind.Message:
                    return selectedItem.Message.MessageTypeName;
                case FlowGraphSelectionKind.Edge:
                    return $"{selectedItem.Edge.MessageTypeName} -> {selectedItem.Edge.TargetComponentPath} ({selectedItem.Edge.RegistrationTypeName})";
                default:
                    return "Selection details";
            }
        }

        private static string CreateRouteKindBadgeText(string routeKindText)
        {
            if (
                string.Equals(
                    routeKindText,
                    MessageRegistrationType.GlobalAcceptAll.ToString(),
                    StringComparison.Ordinal
                )
            )
            {
                return "GLOBAL OBSERVER";
            }

            string normalizedKind = DxMessagingEditorPalette.NormalizeRouteKind(routeKindText);
            return string.IsNullOrWhiteSpace(normalizedKind)
                ? string.IsNullOrWhiteSpace(routeKindText)
                    ? "<unknown route kind>"
                    : routeKindText.Trim()
                : normalizedKind;
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
            FlowGraphEdge[] messageEdges = SelectMessageEdges(message, visibleSnapshot);
            FlowGraphTracePath[] tracePaths = SelectMessageTracePaths(message, visibleSnapshot);
            bool globalObserver = IsGlobalObserverMessage(message);
            int visibleRegistrationCount = messageEdges.Sum(edge => edge.RegistrationCount);
            int selectedCalls = messageEdges.Sum(edge => edge.CallCount);
            int totalCalls = SumVisibleCalls(visibleSnapshot);
            int selectedTracedDeliveries = globalObserver
                ? tracePaths.Sum(path => path.RecentTracedDeliveryCount)
                : messageEdges.Sum(edge => edge.RecentTracedDeliveryCount);
            int totalTracedDeliveries = SumVisibleTracedDeliveries(visibleSnapshot);
            string visibleMessageKind = CreateVisibleMessageKind(
                message.MessageKindName,
                messageEdges.Select(edge => edge.RegistrationTypeName)
            );
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
                .Append("Message kind: ")
                .Append(visibleMessageKind)
                .Append(" | Observed emit sites: ")
                .Append(message.RecentEmissionSites.Count)
                .Append(" | Observed contexts: ")
                .Append(message.RecentContexts.Count)
                .AppendLine();
            builder
                .Append("Emit sites: ")
                .Append(JoinDistinctOrNone(message.RecentEmissionSites))
                .AppendLine();
            builder
                .Append("Observed FROM/AT contexts: ")
                .Append(JoinDistinctOrNone(message.RecentContexts))
                .AppendLine();
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
                .Append(
                    globalObserver
                        ? tracePaths.Sum(path => path.RecentTracedDeliveryCount)
                        : message.RecentTracedDeliveryCount
                )
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
            if (visibleMessageKind == "GLOBAL OBSERVER")
            {
                builder
                    .Append("Concrete message types recently observed: ")
                    .Append(
                        JoinDistinctOrNone(
                            visibleSnapshot
                                .TracePaths.Where(path =>
                                    string.Equals(
                                        path.RegistrationTypeName,
                                        MessageRegistrationType.GlobalAcceptAll.ToString(),
                                        StringComparison.Ordinal
                                    )
                                )
                                .Select(path => path.MessageTypeName)
                        )
                    )
                    .AppendLine();
            }
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
                .TracePaths.Where(path => EdgeMatchesTracePath(edge, path))
                .ToArray();
            StringBuilder builder = new();
            builder.Append("Flow: ").Append(CreateEdgeFlowText(edge)).AppendLine();
            builder
                .Append("Target component: ")
                .Append(edge.TargetComponentPath)
                .Append(" | Target id: ")
                .Append(edge.TargetComponentId)
                .AppendLine();
            builder.Append("Route context: ").Append(edge.Context).AppendLine();
            builder
                .Append("Recent emit sites: ")
                .Append(JoinDistinctOrNone(edge.RecentEmissionSites))
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

        private static string CreateEdgeFlowText(FlowGraphEdge edge)
        {
            string context =
                string.IsNullOrWhiteSpace(edge.Context) || edge.Context == "<none>"
                    ? "ANY"
                    : edge.Context;
            switch (DxMessagingEditorPalette.NormalizeRouteKind(edge.RegistrationTypeName))
            {
                case DxMessagingEditorPalette.BroadcastKind:
                    return $"{edge.MessageTypeName} -> {edge.TargetComponentPath} | FROM {context} TO receiver";
                case DxMessagingEditorPalette.TargetedKind:
                    return $"{edge.MessageTypeName} -> {edge.TargetComponentPath} | AT {context}";
                case DxMessagingEditorPalette.UntargetedKind:
                    return $"{edge.MessageTypeName} -> {edge.TargetComponentPath} | GLOBAL";
                default:
                    return string.Equals(
                        edge.RegistrationTypeName,
                        MessageRegistrationType.GlobalAcceptAll.ToString(),
                        StringComparison.Ordinal
                    )
                        ? $"{edge.TargetComponentPath} observes {GlobalObserverMessageName} globally"
                        : $"{edge.MessageTypeName} -> {edge.TargetComponentPath}";
            }
        }

        private static string CreateReadableRouteContext(FlowGraphEdge edge)
        {
            if (!string.IsNullOrWhiteSpace(edge.Context) && edge.Context != "<none>")
            {
                return edge.Context;
            }

            switch (DxMessagingEditorPalette.NormalizeRouteKind(edge.RegistrationTypeName))
            {
                case DxMessagingEditorPalette.BroadcastKind:
                    return "Any source";
                case DxMessagingEditorPalette.TargetedKind:
                    return "Any target";
                case DxMessagingEditorPalette.UntargetedKind:
                    return "Global bus";
                default:
                    return string.Equals(
                        edge.RegistrationTypeName,
                        MessageRegistrationType.GlobalAcceptAll.ToString(),
                        StringComparison.Ordinal
                    )
                        ? GlobalObserverMessageName
                        : "Any context";
            }
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

        private static void AppendJsonStringArray(
            StringBuilder builder,
            int indentSize,
            string name,
            IReadOnlyList<string> values,
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

                builder.Append("\"").Append(EscapeJson(values[i])).Append("\"");
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

            internal string MessageKindName
            {
                get
                {
                    if (Kinds.Contains("GLOBAL OBSERVER"))
                    {
                        return "GLOBAL OBSERVER";
                    }
                    if (Kinds.Count > 1)
                    {
                        return "MIXED";
                    }
                    return Kinds.FirstOrDefault() ?? "MESSAGE";
                }
            }

            private HashSet<string> Kinds { get; } = new(StringComparer.Ordinal);

            private HashSet<string> EmissionSites { get; } = new(StringComparer.Ordinal);

            private HashSet<string> Contexts { get; } = new(StringComparer.Ordinal);

            private Dictionary<string, HashSet<int>> ContextIdsByDisplay { get; } =
                new(StringComparer.Ordinal);

            private Dictionary<int, string> ContextComponentIds { get; } = new();

            internal int RegistrationCount { get; set; }

            internal int CallCount { get; set; }

            internal int RecentGlobalEmissionCount { get; set; }

            internal int RecentLocalMessageCount { get; set; }

            internal int RecentTracedDeliveryCount { get; set; }

            internal void ObserveRegistrationType(MessageRegistrationType registrationType)
            {
                string kind = CreateMessageKindName(message: null, registrationType);
                if (kind != "MESSAGE")
                {
                    Kinds.Add(kind);
                }
            }

            internal void ObserveEmission(MessageEmissionData emission)
            {
                if (emission.message is IBroadcastMessage)
                {
                    Kinds.Add("BROADCAST");
                }
                if (emission.message is ITargetedMessage)
                {
                    Kinds.Add("TARGETED");
                }
                if (emission.message is IUntargetedMessage)
                {
                    Kinds.Add("GLOBAL");
                }
                EmissionSites.Add(
                    DxMessagingEditorSourceLinks.CreateEmissionSite(emission.stackTrace)
                );
                if (emission.context.HasValue)
                {
                    string contextDisplay = CreateContextDisplayText(emission.context);
                    Contexts.Add(contextDisplay);
                    if (!ContextIdsByDisplay.TryGetValue(contextDisplay, out HashSet<int> ids))
                    {
                        ids = new HashSet<int>();
                        ContextIdsByDisplay[contextDisplay] = ids;
                    }
                    int contextId = emission.context.Value.Id;
                    ids.Add(contextId);
                    string componentId = TryCreateContextComponentId(emission.context.Value);
                    if (!string.IsNullOrWhiteSpace(componentId))
                    {
                        ContextComponentIds[contextId] = componentId;
                    }
                }
            }

            internal FlowGraphMessageNode Build()
            {
                return new FlowGraphMessageNode(
                    MessageTypeName,
                    RegistrationCount,
                    CallCount,
                    RecentGlobalEmissionCount,
                    RecentLocalMessageCount,
                    RecentTracedDeliveryCount,
                    MessageKindName,
                    EmissionSites.OrderBy(site => site, StringComparer.Ordinal).ToArray(),
                    Contexts.OrderBy(context => context, StringComparer.Ordinal).ToArray(),
                    BuildContextComponentIds()
                );
            }

            private IReadOnlyDictionary<string, string> BuildContextComponentIds()
            {
                Dictionary<string, string> contextComponentIds = new(StringComparer.Ordinal);
                foreach (KeyValuePair<string, HashSet<int>> pair in ContextIdsByDisplay)
                {
                    if (pair.Value.Count != 1)
                    {
                        continue;
                    }

                    int contextId = pair.Value.First();
                    if (ContextComponentIds.TryGetValue(contextId, out string componentId))
                    {
                        contextComponentIds[pair.Key] = componentId;
                    }
                }
                return contextComponentIds;
            }
        }

        private static string TryCreateContextComponentId(InstanceId context)
        {
            UnityEngine.Object contextObject = context.Object;
            if (contextObject == null)
            {
                return string.Empty;
            }
            if (contextObject is MessagingComponent exactComponent)
            {
                return IsSceneComponent(exactComponent)
                    ? CreateComponentId(exactComponent)
                    : string.Empty;
            }

            GameObject contextGameObject = contextObject is GameObject gameObject
                ? gameObject
                : (contextObject as Component)?.gameObject;
            if (contextGameObject == null)
            {
                return string.Empty;
            }

            MessagingComponent[] candidates = contextGameObject
                .GetComponents<MessagingComponent>()
                .Where(IsSceneComponent)
                .Take(2)
                .ToArray();
            return candidates.Length == 1 ? CreateComponentId(candidates[0]) : string.Empty;
        }

        private sealed class EdgeBuilder
        {
            internal EdgeBuilder(
                string messageTypeName,
                string targetComponentId,
                string targetComponentPath,
                string registrationTypeName,
                string context,
                InstanceId? contextId
            )
            {
                MessageTypeName = messageTypeName ?? string.Empty;
                TargetComponentId = targetComponentId ?? string.Empty;
                TargetComponentPath = targetComponentPath ?? string.Empty;
                RegistrationTypeName = registrationTypeName ?? string.Empty;
                Context = context ?? string.Empty;
                ContextId = contextId;
            }

            internal string MessageTypeName { get; }

            internal string TargetComponentId { get; }

            internal string TargetComponentPath { get; }

            internal string RegistrationTypeName { get; }

            internal string Context { get; }

            internal InstanceId? ContextId { get; }

            internal int RegistrationCount { get; set; }

            internal int CallCount { get; set; }

            internal int RecentTracedDeliveryCount { get; set; }

            private HashSet<string> EmissionSites { get; } = new(StringComparer.Ordinal);

            internal HashSet<MessageRegistrationHandle> RegistrationHandles { get; } = new();

            internal void AddRegistrationHandle(MessageRegistrationHandle handle)
            {
                if (handle != default(MessageRegistrationHandle))
                {
                    RegistrationHandles.Add(handle);
                }
            }

            internal void ObserveEmission(MessageEmissionData emission)
            {
                EmissionSites.Add(
                    DxMessagingEditorSourceLinks.CreateEmissionSite(emission.stackTrace)
                );
            }

            internal FlowGraphEdge Build()
            {
                return new FlowGraphEdge(
                    MessageTypeName,
                    TargetComponentId,
                    TargetComponentPath,
                    RegistrationTypeName,
                    RegistrationCount,
                    CallCount,
                    RecentTracedDeliveryCount,
                    Context,
                    EmissionSites.OrderBy(site => site, StringComparer.Ordinal).ToArray(),
                    ContextId?.Id ?? 0
                );
            }
        }

        private sealed class TracePathBuilder
        {
            internal TracePathBuilder(
                string messageTypeName,
                string context,
                int contextId,
                string targetComponentId,
                string targetComponentPath,
                string registrationTypeName
            )
            {
                MessageTypeName = messageTypeName ?? string.Empty;
                Context = context ?? string.Empty;
                ContextId = contextId;
                TargetComponentId = targetComponentId ?? string.Empty;
                TargetComponentPath = targetComponentPath ?? string.Empty;
                RegistrationTypeName = registrationTypeName ?? string.Empty;
            }

            internal string MessageTypeName { get; }

            internal string Context { get; }

            internal int ContextId { get; }

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
                    TraceIds.OrderBy(traceId => traceId).ToArray(),
                    ContextId
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
            int recentTracedDeliveryCount = 0,
            string messageKindName = "MESSAGE",
            IReadOnlyList<string> recentEmissionSites = null,
            IReadOnlyList<string> recentContexts = null,
            IReadOnlyDictionary<string, string> recentContextComponentIds = null
        )
        {
            MessageTypeName = messageTypeName ?? string.Empty;
            RegistrationCount = registrationCount;
            CallCount = callCount;
            RecentGlobalEmissionCount = recentGlobalEmissionCount;
            RecentLocalMessageCount = recentLocalMessageCount;
            RecentTracedDeliveryCount = recentTracedDeliveryCount;
            MessageKindName = string.IsNullOrWhiteSpace(messageKindName)
                ? "MESSAGE"
                : messageKindName;
            RecentEmissionSites = recentEmissionSites ?? Array.Empty<string>();
            RecentContexts = recentContexts ?? Array.Empty<string>();
            RecentContextComponentIds =
                recentContextComponentIds ?? new Dictionary<string, string>();
        }

        internal string MessageTypeName { get; }

        internal int RegistrationCount { get; }

        internal int CallCount { get; }

        internal int RecentGlobalEmissionCount { get; }

        internal int RecentLocalMessageCount { get; }

        internal int RecentTracedDeliveryCount { get; }

        internal string MessageKindName { get; }

        internal IReadOnlyList<string> RecentEmissionSites { get; }

        internal IReadOnlyList<string> RecentContexts { get; }

        internal IReadOnlyDictionary<string, string> RecentContextComponentIds { get; }

        internal bool Matches(string filterText)
        {
            return ContainsText(MessageTypeName, filterText)
                || ContainsText(MessageKindName, filterText)
                || RecentEmissionSites.Any(site => ContainsText(site, filterText))
                || RecentContexts.Any(context => ContainsText(context, filterText));
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
            IReadOnlyList<long> traceIds = null,
            int contextId = 0
        )
        {
            MessageTypeName = messageTypeName ?? string.Empty;
            Context = context ?? string.Empty;
            ContextId = contextId;
            TargetComponentId = targetComponentId ?? string.Empty;
            TargetComponentPath = targetComponentPath ?? string.Empty;
            RegistrationTypeName = registrationTypeName ?? string.Empty;
            RecentTracedDeliveryCount = recentTracedDeliveryCount;
            TraceIds = NormalizeTraceIds(traceIds);
        }

        internal string MessageTypeName { get; }

        internal string Context { get; }

        internal int ContextId { get; }

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
            int recentTracedDeliveryCount = 0,
            string context = "",
            IReadOnlyList<string> recentEmissionSites = null,
            int contextId = 0
        )
        {
            MessageTypeName = messageTypeName ?? string.Empty;
            TargetComponentId = targetComponentId ?? string.Empty;
            TargetComponentPath = targetComponentPath ?? string.Empty;
            RegistrationTypeName = registrationTypeName ?? string.Empty;
            RegistrationCount = registrationCount;
            CallCount = callCount;
            RecentTracedDeliveryCount = recentTracedDeliveryCount;
            Context = context ?? string.Empty;
            RecentEmissionSites = recentEmissionSites ?? Array.Empty<string>();
            ContextId = contextId;
        }

        internal string MessageTypeName { get; }

        internal string TargetComponentId { get; }

        internal string TargetComponentPath { get; }

        internal string RegistrationTypeName { get; }

        internal int RegistrationCount { get; }

        internal int CallCount { get; }

        internal int RecentTracedDeliveryCount { get; }

        internal string Context { get; }

        internal IReadOnlyList<string> RecentEmissionSites { get; }

        internal int ContextId { get; }

        internal bool Matches(string filterText)
        {
            return ContainsText(MessageTypeName, filterText)
                || ContainsText(TargetComponentId, filterText)
                || ContainsText(TargetComponentPath, filterText)
                || ContainsText(RegistrationTypeName, filterText)
                || ContainsText(Context, filterText)
                || RecentEmissionSites.Any(site => ContainsText(site, filterText));
        }

        private static bool ContainsText(string value, string filterText)
        {
            return !string.IsNullOrEmpty(value)
                && value.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
#endif
