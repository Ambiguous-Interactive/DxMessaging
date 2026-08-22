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
        internal const string ActiveFilterTokenWrapRowName =
            "dxmessaging-monitor-active-filter-token-row";
        internal const string ActiveFilterTokenClassName =
            "dxmessaging-monitor-active-filter-token";
        internal const string LanePillWrapRowName = "dxmessaging-monitor-lane-pill-row";
        internal const string ActiveFilterClearButtonName =
            "dxmessaging-monitor-active-filter-clear";
        internal const string RefreshButtonName = "dxmessaging-monitor-refresh";
        internal const string ExportButtonName = "dxmessaging-monitor-export";
        internal const string LiveButtonName = "dxmessaging-monitor-live-mode";
        internal const string ModeBadgeLabelName = "dxmessaging-monitor-mode";
        internal const string ModeHintLabelName = "dxmessaging-monitor-mode-hint";
        internal const string ContentContainerName = "dxmessaging-monitor-content";
        internal const string MessageSectionName = "dxmessaging-monitor-message-section";
        internal const string EmptyStateLabelName = "dxmessaging-monitor-empty";
        internal const string EmptyStateTitleLabelName = "dxmessaging-monitor-empty-title";
        internal const string RouteKindFilterRowName = "dxmessaging-monitor-route-kinds";
        internal const string UntargetedChipName = "dxmessaging-monitor-chip-untargeted";
        internal const string TargetedChipName = "dxmessaging-monitor-chip-targeted";
        internal const string BroadcastChipName = "dxmessaging-monitor-chip-broadcast";
        internal const string ListHeaderName = "dxmessaging-monitor-list-header";
        internal const string ListName = "dxmessaging-monitor-list";
        internal const string MessageTypeLabelName = "dxmessaging-monitor-message-type";
        internal const string RouteKindLabelName = "dxmessaging-monitor-route-kind";
        internal const string ContextLabelName = "dxmessaging-monitor-context";
        internal const string TraceLabelName = "dxmessaging-monitor-trace";
        internal const string DetailsPaneName = "dxmessaging-monitor-details";
        internal const string DetailsTypeLabelName = "dxmessaging-monitor-details-type";
        internal const string DetailsContextLabelName = "dxmessaging-monitor-details-context";
        internal const string DetailsStackFoldoutName = "dxmessaging-monitor-details-stack-foldout";
        internal const string DetailsStackFirstFrameLabelName =
            "dxmessaging-monitor-details-stack-first-frame";
        internal const string DetailsTypeRowName = "dxmessaging-monitor-details-type-row";
        internal const string DetailsTypeValueLabelName = "dxmessaging-monitor-details-type-value";
        internal const string DetailsContextRowName = "dxmessaging-monitor-details-context-row";
        internal const string DetailsStackFrameRowClassName =
            "dxmessaging-monitor-details-stack-frame";
        internal const string DetailsPaneResizerName = "dxmessaging-monitor-details-resizer";
        internal const string ComponentResizerName = "dxmessaging-monitor-component-resizer";
        internal const string BreakdownFoldoutName = "dxmessaging-monitor-breakdown";
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
        internal const string VisibleContextLaneFilterButtonName =
            "dxmessaging-monitor-context-lane-filter";
        internal const string ComponentPanelName = "dxmessaging-monitor-components";
        internal const string ComponentFoldoutName = "dxmessaging-monitor-components-foldout";
        internal const string ComponentScrollViewName = "dxmessaging-monitor-component-scroll";
        internal const string ComponentRowClassName = "dxmessaging-monitor-component-row";
        internal const string ComponentNameLabelName = "dxmessaging-monitor-component-name";
        internal const string ComponentSummaryLabelName = "dxmessaging-monitor-component-summary";
        internal const string ComponentProviderLabelName = "dxmessaging-monitor-component-provider";
        internal const string ComponentWarningLabelName = "dxmessaging-monitor-component-warning";
        internal const string ComponentEmptyStateLabelName = "dxmessaging-monitor-component-empty";

        private const string Title = "Message Monitor";

        /// <summary>
        /// Badge text for the buffered-history mode, alongside the one-line explanation of what
        /// the rows in that mode stand for. Issue #344 reported that a reader cannot tell whether
        /// the Monitor is streaming or deduplicating, so both modes now say so on the surface
        /// itself rather than leaving it to the menu the window was opened from.
        /// </summary>
        internal const string SnapshotModeBadgeText = "SNAPSHOT";

        internal const string SnapshotModeHintText =
            "Buffered bus history as of the last Refresh. One row per emission, newest first, nothing merged.";

        internal const string DetailsPaneHeightPreferenceKey =
            "WallstopStudios.DxMessaging.MessageMonitor.DetailsPaneHeight";

        /// <summary>
        /// How the log and the detail pane divide a window that can be dragged down to its 320 px
        /// minimum.
        /// </summary>
        /// <remarks>
        /// The log is the section that gives space back first, because it scrolls: shrinking it
        /// costs a reader nothing they cannot scroll to. It stops at two rows so it still reads as
        /// a log. The detail pane keeps its natural height until it would take more than
        /// <see cref="DetailsMaxHeightPercent"/> of the section, at which point it is capped and its
        /// own body scrolls; below the log's floor it keeps shrinking rather than leaving the
        /// window, down to its own header. Issue #344 reported the previous arrangement -- where
        /// nothing gave way -- as content rendered off screen.
        /// </remarks>
        private const int MessageListMinHeight = 56;

        private const int DetailsMaxHeightPercent = 45;

        internal const int DetailsPaneMinHeight = 80;

        internal const int DetailsPaneResizeMaxHeight = 900;

        /// <summary>
        /// Floor for a disclosure section, sized to its own toggle header. A <see cref="Foldout"/>
        /// that shrinks has to keep at least the row a reader clicks to open it: without this floor
        /// the section is squeezed to a few pixels and its header, not its body, is what spills out
        /// of the window. Compression below the floor goes to the scrolling lists inside instead.
        /// </summary>
        private const int FoldoutHeaderMinHeight = 22;

        private const int LanePillScrollMaxHeight = 96;

        /// <summary>
        /// Bounds for the panels a reader can drag. The floors keep a dragged panel from
        /// collapsing to nothing, and the ceilings are generous rather than tight: the point of
        /// #344's resize request is that the shipped caps were chosen for a window the reader
        /// does not have.
        /// </summary>
        internal const int ComponentPanelMinHeight = 60;

        internal const int ComponentPanelResizeMaxHeight = 720;

        /// <summary>
        /// How often live mode drains the bus emission buffer. Fast enough that the log reads as
        /// live, slow enough that a busy scene batches many emissions into one rebuild.
        /// </summary>
        private const long LivePollIntervalMilliseconds = 250;

        [SerializeField]
        private bool _liveMode;

        [SerializeField]
        private float _detailsPaneHeight;

        private MessageMonitorViewState _viewState = MessageMonitorViewState.Default;
        private MessageMonitorLiveRecorder _liveRecorder;
        private MessageMonitorLiveViewState _liveViewState = MessageMonitorLiveViewState.Default;
        private IVisualElementScheduledItem _livePump;
        private long _renderedLiveRevision = -1;

        private MessageMonitorLiveRecorder LiveRecorder =>
            // The recorder is deliberately not serialized: a domain reload wipes the bus emission
            // buffer's contents from under it, so carrying a stale log across one would show rows
            // that no longer correspond to anything the bus still knows about.
            _liveRecorder ??= new MessageMonitorLiveRecorder();

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

        private void OnEnable()
        {
            if (_detailsPaneHeight <= 0f)
            {
                _detailsPaneHeight = EditorPrefs.GetFloat(DetailsPaneHeightPreferenceKey, 0f);
            }
            EditorApplication.playModeStateChanged += HandlePlayModeStateChanged;
            DxMessagingEditorSourceLinks.MessageSourceIndexChanged -=
                HandleMessageSourceIndexChanged;
            DxMessagingEditorSourceLinks.MessageSourceIndexChanged +=
                HandleMessageSourceIndexChanged;
        }

        private void OnDisable()
        {
            EditorApplication.playModeStateChanged -= HandlePlayModeStateChanged;
            DxMessagingEditorSourceLinks.MessageSourceIndexChanged -=
                HandleMessageSourceIndexChanged;
            StopLivePump();
            if (_detailsPaneHeight > 0f)
            {
                EditorPrefs.SetFloat(DetailsPaneHeightPreferenceKey, _detailsPaneHeight);
            }
        }

        /// <summary>
        /// Source resolution is lazy: the first lookup for an assembly starts a background index
        /// and answers "not yet", so a detail pane built before that index drains carries no
        /// source link. Without this the link only ever appeared if the reader happened to select
        /// a second row afterwards, which is not the path anyone takes. Re-render the selection
        /// when the index completes, exactly as the Flow Graph does.
        /// </summary>
        private void HandleMessageSourceIndexChanged()
        {
            if (this == null)
            {
                return;
            }

            if (_liveMode)
            {
                // The live body is re-rendered on a 250ms timer anyway, so rebuilding it here
                // costs nothing and clears the detail pane's own memoization, which would
                // otherwise keep showing the linkless pane for as long as the row stays selected.
                RenderLiveBody();
                return;
            }

            // Snapshot mode re-renders ONLY the detail pane. A full refresh would rebuild the log
            // and take the reader's scroll position with it -- the same loss a selection change
            // deliberately avoids -- for the sake of a link in a pane beside it.
            _ = TryRerenderDetails(rootVisualElement);
        }

        /// <summary>
        /// A play-mode transition starts a new session, so the previous session's rows go. Whether
        /// the cursor goes with them depends on whether the bus restarted its dispatch counter,
        /// which the recorder cannot always infer from the sequence alone.
        /// </summary>
        /// <remarks>
        /// <para>
        /// If the bus reset, every record it now holds belongs to the new run and the cursor has to
        /// rewind so all of them are drained. If it did not reset (the counter simply kept running),
        /// the cursor already sits exactly on the boundary between the two sessions, so keeping it
        /// drops the old session and keeps everything after it.
        /// </para>
        /// <para>
        /// Either way the cursor must not jump forward to the bus's current counter: <c>Entered*</c>
        /// fires after <c>Awake</c>, <c>OnEnable</c> and <c>Start</c> have already emitted, so
        /// stepping over what is buffered would permanently lose the startup traffic. That is also
        /// why this handles <c>Entered*</c> rather than <c>Exiting*</c>: on <c>Exiting*</c> the old
        /// session is still live and still filling the buffer.
        /// </para>
        /// </remarks>
        private void HandlePlayModeStateChanged(PlayModeStateChange change)
        {
            if (
                change != PlayModeStateChange.EnteredEditMode
                && change != PlayModeStateChange.EnteredPlayMode
            )
            {
                return;
            }

            if (
                MessageHandler.MessageBus is MessageBus messageBus
                && messageBus.EmissionId < LiveRecorder.Cursor
            )
            {
                LiveRecorder.ResetForNewBusRun();
            }
            else
            {
                LiveRecorder.Clear();
            }
            if (_liveMode)
            {
                _liveViewState = _liveViewState.WithSelectedTraceId(
                    MessageMonitorLiveViewState.FollowNewest
                );
                Refresh();
            }
        }

        private void Refresh()
        {
            if (_liveMode)
            {
                RefreshLive();
                return;
            }

            StopLivePump();
            RefreshSnapshot();
        }

        private void RefreshSnapshot()
        {
            MessageMonitorSnapshot snapshot = MessageHandler.MessageBus is MessageBus messageBus
                ? CaptureSnapshot(messageBus)
                : MessageMonitorSnapshot.Unavailable(
                    "The active global bus is not the default DxMessaging MessageBus."
                );
            IReadOnlyList<ComponentMonitorEntry> components = CaptureComponentSnapshots();
            BuildMonitorUi(
                rootVisualElement,
                snapshot,
                _viewState,
                // The surface has already re-rendered itself; the window only has to remember what
                // it is now showing so the next Refresh rebuilds into the same state.
                viewState => _viewState = viewState,
                Refresh,
                exportText => EditorGUIUtility.systemCopyBuffer = exportText,
                components,
                EnterLiveMode,
                _detailsPaneHeight,
                RememberDetailsPaneHeight
            );
        }

        private void EnterLiveMode()
        {
            _liveMode = true;
            _liveViewState = _liveViewState.WithSelectedTraceId(
                MessageMonitorLiveViewState.FollowNewest
            );
            Refresh();
        }

        private void ExitLiveMode()
        {
            _liveMode = false;
            Refresh();
        }

        private void RefreshLive()
        {
            // Drain before the first render so switching into live mode shows whatever the bus is
            // already holding instead of an empty log that fills a poll later.
            DrainLiveRecorder();
            _renderedLiveRevision = LiveRecorder.Revision;
            rootVisualElement.Clear();
            rootVisualElement.Add(
                DxMessagingMessageMonitorLiveView.Create(
                    LiveRecorder,
                    _liveViewState,
                    IsBusDiagnosticsEnabled(),
                    new MessageMonitorLiveViewCallbacks
                    {
                        OnRecordingChanged = recording =>
                        {
                            LiveRecorder.Recording = recording;
                            RenderLiveBody();
                        },
                        OnClear = () =>
                        {
                            LiveRecorder.Clear();
                            _liveViewState = _liveViewState.WithSelectedTraceId(
                                MessageMonitorLiveViewState.FollowNewest
                            );
                            RenderLiveBody();
                        },
                        OnStateChanged = state =>
                        {
                            _liveViewState = state;
                            RenderLiveBody();
                        },
                        OnExitLiveMode = ExitLiveMode,
                        InitialDetailsPaneHeight = _detailsPaneHeight,
                        OnDetailsPaneHeightChanged = RememberDetailsPaneHeight,
                    }
                )
            );
            StartLivePump();
        }

        /// <summary>
        /// Re-renders only the live body, leaving the toolbar (and the focus and caret of the
        /// filter field the user may be typing into) alone.
        /// </summary>
        private void RenderLiveBody()
        {
            VisualElement body = rootVisualElement.Q<VisualElement>(
                DxMessagingMessageMonitorLiveView.BodyName
            );
            if (body == null)
            {
                Refresh();
                return;
            }

            _renderedLiveRevision = LiveRecorder.Revision;
            DxMessagingMessageMonitorLiveView.RenderBody(
                body,
                LiveRecorder,
                _liveViewState,
                IsBusDiagnosticsEnabled(),
                new MessageMonitorLiveViewCallbacks
                {
                    OnStateChanged = state =>
                    {
                        _liveViewState = state;
                        RenderLiveBody();
                    },
                    InitialDetailsPaneHeight = _detailsPaneHeight,
                    OnDetailsPaneHeightChanged = RememberDetailsPaneHeight,
                }
            );
        }

        private void RememberDetailsPaneHeight(float height)
        {
            _detailsPaneHeight = height;
        }

        private void StartLivePump()
        {
            _livePump ??= rootVisualElement
                .schedule.Execute(PumpLive)
                .Every(LivePollIntervalMilliseconds);
            _livePump.Resume();
        }

        private void StopLivePump()
        {
            _livePump?.Pause();
        }

        /// <summary>
        /// One poll: drain whatever the bus has added, and rebuild the body only if that actually
        /// changed the log, so an idle scene costs one buffer scan per interval and no layout.
        /// </summary>
        private void PumpLive()
        {
            if (!_liveMode)
            {
                StopLivePump();
                return;
            }

            DrainLiveRecorder();
            if (_renderedLiveRevision == LiveRecorder.Revision)
            {
                return;
            }

            RenderLiveBody();
        }

        private void DrainLiveRecorder()
        {
            if (MessageHandler.MessageBus is not MessageBus messageBus)
            {
                return;
            }

            // A paused recorder discards the whole capture, and its cursor stops advancing, so it
            // has to be checked before the idle comparison rather than through it.
            if (!LiveRecorder.Recording)
            {
                return;
            }

            long busCursor = messageBus.EmissionId;
            if (busCursor < LiveRecorder.Cursor)
            {
                // The bus restarted its dispatch counter. Rebasing here rather than leaving it to
                // Ingest matters because Ingest can only see a restart through the records in the
                // buffer, and a reset empties that buffer: the log would keep showing the previous
                // run until the new one happened to emit something. The rewind goes to the start of
                // the run, not to busCursor, so anything the new run has already buffered is drained
                // rather than stepped over.
                LiveRecorder.ResetForNewBusRun();
            }
            else if (busCursor == LiveRecorder.Cursor)
            {
                // Capturing a snapshot rebuilds an entry for every record in the bus buffer, so an
                // idle scene should not pay for it four times a second. An exact match means
                // nothing has been emitted since the last drain.
                return;
            }

            LiveRecorder.Ingest(CaptureSnapshot(messageBus).Entries);
        }

        private static bool IsBusDiagnosticsEnabled()
        {
            return MessageHandler.MessageBus is MessageBus messageBus && messageBus.DiagnosticsMode;
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

        /// <summary>
        /// Builds the whole snapshot Monitor: a toolbar naming the mode, a control row, the
        /// taxonomy filter chips, and a content area holding the log, the selected-entry details
        /// and the component section.
        /// </summary>
        /// <remarks>
        /// Every control mutates one <see cref="MonitorUi"/> and re-renders through it, so the
        /// surface behaves identically whether or not a host supplied
        /// <paramref name="onViewStateChanged"/>. A host that does supply one is told the complete
        /// next state and is expected to store it rather than re-render, because the surface has
        /// already updated itself in place.
        /// </remarks>
        internal static void BuildMonitorUi(
            VisualElement root,
            MessageMonitorSnapshot snapshot,
            MessageMonitorViewState viewState,
            Action<MessageMonitorViewState> onViewStateChanged = null,
            Action onRefresh = null,
            Action<string> onCopyExport = null,
            IReadOnlyList<ComponentMonitorEntry> componentEntries = null,
            Action onEnterLiveMode = null,
            float initialDetailsPaneHeight = 0f,
            Action<float> onDetailsPaneHeightChanged = null
        )
        {
            if (root == null)
            {
                throw new ArgumentNullException(nameof(root));
            }

            root.Clear();
            DxMessagingEditorTheme.ApplyWindow(root);
            root.AddToClassList(RootClassName);
            root.style.flexDirection = FlexDirection.Column;

            MonitorUi ui = new()
            {
                Snapshot = snapshot,
                Components = componentEntries ?? Array.Empty<ComponentMonitorEntry>(),
                State = viewState,
                OnViewStateChanged = onViewStateChanged,
                OnCopyExport = onCopyExport,
                DetailsPaneHeight = initialDetailsPaneHeight,
                OnDetailsPaneHeightChanged = onDetailsPaneHeightChanged,
            };

            // The surface keeps a handle to its own state so a background source index that
            // completes later can re-render the one part that shows a link, instead of
            // rebuilding the window and taking the reader's place in the log with it.
            root.userData = ui;

            root.Add(CreateToolbar(ui, onEnterLiveMode));

            VisualElement content = new() { name = ContentContainerName };
            content.style.flexGrow = 1;
            // Growing is not enough: UI Toolkit defaults flex-shrink to 0, so a block that only
            // grows keeps its content height and pushes its siblings off the window as soon as an
            // expanded disclosure makes that content taller than the space available.
            content.style.flexShrink = 1;
            content.style.minHeight = 0;
            ui.Content = content;

            if (!snapshot.Available)
            {
                root.Add(content);
                AddEmptyState(content, "Monitor unavailable", snapshot.UnavailableReason);
                content.Add(CreateComponentSection(ui));
                return;
            }

            root.Add(CreateControlRow(ui, onRefresh, onEnterLiveMode));
            root.Add(CreateRouteKindFilterRow(ui));
            root.Add(content);
            RenderContent(ui);
        }

        /// <summary>
        /// The elements a rendered snapshot Monitor updates in place, plus the state they render.
        /// </summary>
        private sealed class MonitorUi
        {
            internal MessageMonitorSnapshot Snapshot { get; set; }

            internal IReadOnlyList<ComponentMonitorEntry> Components { get; set; }

            internal MessageMonitorViewState State { get; set; }

            internal Label Status { get; set; }

            internal TextField Filter { get; set; }

            internal Button Export { get; set; }

            internal VisualElement ActiveFilter { get; set; }

            internal VisualElement Content { get; set; }

            internal Toggle UntargetedChip { get; set; }

            internal Toggle TargetedChip { get; set; }

            internal Toggle BroadcastChip { get; set; }

            /// <summary>The scrolling log, or null while the message section is an empty state.</summary>
            internal ScrollView List { get; set; }

            /// <summary>Holds the detail pane so a new selection can replace only that.</summary>
            internal VisualElement DetailsSlot { get; set; }

            /// <summary>
            /// Whether each disclosure is open. A filter or chip change rebuilds the content, and a
            /// rebuilt <see cref="Foldout"/> starts closed, so without remembering this a reader
            /// who opened Breakdown and then typed would watch it snap shut under them -- the same
            /// loss of context a selection change deliberately avoids.
            /// </summary>
            internal bool BreakdownExpanded { get; set; }

            internal bool ComponentsExpanded { get; set; }

            /// <summary>
            /// Heights a reader dragged, or 0 while a section is still at its shipped cap. Same
            /// reason the two flags above exist: `RenderContent` rebuilds these sections on every
            /// filter keystroke and chip toggle, so a dragged height that is not carried here is
            /// undone by the next character typed.
            /// </summary>
            internal float ComponentPanelHeight { get; set; }

            internal float DetailsPaneHeight { get; set; }

            internal Action<float> OnDetailsPaneHeightChanged { get; set; }

            internal Action<MessageMonitorViewState> OnViewStateChanged { get; set; }

            internal Action<string> OnCopyExport { get; set; }

            internal IReadOnlyList<MessageMonitorEntry> FilteredEntries()
            {
                return FilterEntries(Snapshot.Entries, State);
            }
        }

        /// <summary>
        /// Adopts <paramref name="next"/> as what the Monitor shows: syncs the controls that do not
        /// already agree with it, re-renders what changed, and tells the host.
        /// </summary>
        /// <remarks>
        /// A selection change re-renders only the selected row's wash and the detail pane. Rebuilding
        /// the whole log for it would throw a reader who had scrolled into older rows back to the top
        /// on the very click they used to look at one.
        /// </remarks>
        private static void ApplyState(MonitorUi ui, MessageMonitorViewState next)
        {
            bool sameRowSet =
                string.Equals(ui.State.FilterText, next.FilterText, StringComparison.Ordinal)
                && ui.State.ShowUntargeted == next.ShowUntargeted
                && ui.State.ShowTargeted == next.ShowTargeted
                && ui.State.ShowBroadcast == next.ShowBroadcast;

            ui.State = next;
            if (
                ui.Filter != null
                && !string.Equals(ui.Filter.value, next.FilterText, StringComparison.Ordinal)
            )
            {
                ui.Filter.SetValueWithoutNotify(next.FilterText);
            }
            ui.UntargetedChip?.SetValueWithoutNotify(next.ShowUntargeted);
            ui.TargetedChip?.SetValueWithoutNotify(next.ShowTargeted);
            ui.BroadcastChip?.SetValueWithoutNotify(next.ShowBroadcast);

            if (sameRowSet && ui.List != null)
            {
                RenderSelection(ui);
            }
            else
            {
                RenderContent(ui);
            }
            ui.OnViewStateChanged?.Invoke(next);
        }

        /// <summary>
        /// Moves the selection within the log the reader is already looking at: repaints the row
        /// washes and swaps the detail pane, leaving the list and its scroll position alone.
        /// </summary>
        /// <summary>
        /// Re-renders only the detail pane of an already-built surface. Source links resolve
        /// lazily, so a link can become available minutes after the pane was drawn; this is how
        /// it arrives without a full rebuild. Returns false when <paramref name="root"/> is not
        /// a built Monitor surface, which is the normal case for the live view.
        /// </summary>
        internal static bool TryRerenderDetails(VisualElement root)
        {
            if (root?.userData is not MonitorUi ui || ui.DetailsSlot == null || ui.List == null)
            {
                return false;
            }

            RenderSelection(ui);
            return true;
        }

        private static void RenderSelection(MonitorUi ui)
        {
            IReadOnlyList<MessageMonitorEntry> rows = ui.FilteredEntries();
            if (rows.Count == 0)
            {
                RenderContent(ui);
                return;
            }

            int selectedEntryIndex = ClampSelectedIndex(ui.State.SelectedEntryIndex, rows.Count);
            for (int index = 0; index < ui.List.childCount; index++)
            {
                VisualElement row = ui.List[index];
                if (index == selectedEntryIndex)
                {
                    row.style.backgroundColor = DxMessagingEditorPalette.SelectedWash;
                }
                else
                {
                    row.style.backgroundColor = StyleKeyword.Null;
                }
            }

            ui.DetailsSlot.Clear();
            ui.DetailsSlot.Add(CreateDetailsPane(rows[selectedEntryIndex]));
        }

        /// <summary>
        /// Re-renders everything that depends on the view state: the status line, the export
        /// button, the active-filter strip, the chip counts, and the content area.
        /// </summary>
        private static void RenderContent(MonitorUi ui)
        {
            IReadOnlyList<MessageMonitorEntry> filteredEntries = ui.FilteredEntries();
            if (ui.Status != null)
            {
                string statusText = CreateStatusText(ui.Snapshot, filteredEntries.Count);
                ui.Status.text = statusText;
                // The line is cut off with an ellipsis on a narrow window, so the tooltip is where
                // the counts stay readable.
                ui.Status.tooltip = statusText;
            }
            SetExportButtonEnabled(ui, filteredEntries.Count);
            UpdateActiveFilterSummary(
                ui.ActiveFilter,
                ui.State.FilterText,
                () => ApplyState(ui, ui.State.WithFilterText(string.Empty))
            );
            UpdateRouteKindChipText(ui);

            if (ui.Content == null || !ui.Snapshot.Available)
            {
                return;
            }

            ui.Content.Clear();
            ui.List = null;
            ui.DetailsSlot = null;
            VisualElement messageSection = new() { name = MessageSectionName };
            messageSection.style.flexGrow = 1;
            messageSection.style.flexShrink = 1;
            messageSection.style.minHeight = 0;
            ui.Content.Add(messageSection);
            RenderMessageSection(ui, messageSection, filteredEntries);
            ui.Content.Add(CreateComponentSection(ui));
        }

        private static VisualElement CreateToolbar(MonitorUi ui, Action onEnterLiveMode)
        {
            VisualElement toolbar = new();
            toolbar.AddToClassList(ToolbarClassName);
            toolbar.AddToClassList(DxMessagingEditorTheme.ToolbarClassName);
            toolbar.style.flexDirection = FlexDirection.Row;
            toolbar.style.alignItems = Align.Center;

            // Every element in this row shrinks except the mode badge, which is the one thing that
            // must stay legible at any width. UI Toolkit defaults flex-shrink to 0, so a row of
            // defaults pushes its last child out of a narrow window instead of tightening.
            Label title = new(Title);
            title.style.fontSize = 16;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginRight = 8;
            title.style.flexShrink = 1;
            title.style.overflow = Overflow.Hidden;
            title.style.textOverflow = TextOverflow.Ellipsis;
            title.style.whiteSpace = WhiteSpace.NoWrap;
            toolbar.Add(title);

            // The badge that names the mode is also the control that changes it. Issue #344
            // reported not being able to leave live mode: the two modes offered their switch in
            // different places, so a reader who found one had no reason to look where the other
            // put it. Whichever mode is showing, the switch is on the word that names it.
            Label mode = new(SnapshotModeBadgeText)
            {
                name = ModeBadgeLabelName,
                tooltip = SnapshotModeHintText,
            };
            mode.AddToClassList(DxMessagingEditorTheme.TypeBadgeClassName);
            mode.AddToClassList(DxMessagingEditorTheme.TypeBadgeGlobalObserverClassName);
            mode.style.flexShrink = 0;
            if (onEnterLiveMode != null)
            {
                DxMessagingEditorSourceLinks.MakeActivatable(
                    mode,
                    "Switch to the live log, which drains new emissions as they happen.",
                    onEnterLiveMode,
                    addLinkClass: false
                );
            }
            toolbar.Add(mode);

            string statusText = CreateStatusText(
                ui.Snapshot,
                FilterEntries(ui.Snapshot.Entries, ui.State).Count
            );
            Label status = new(statusText) { name = StatusLabelName, tooltip = statusText };
            status.style.flexGrow = 1;
            status.style.flexShrink = 1;
            status.style.overflow = Overflow.Hidden;
            status.style.textOverflow = TextOverflow.Ellipsis;
            status.style.whiteSpace = WhiteSpace.NoWrap;
            status.style.unityTextAlign = TextAnchor.MiddleRight;
            ui.Status = status;
            toolbar.Add(status);
            return toolbar;
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

        private static void RenderMessageSection(
            MonitorUi ui,
            VisualElement messageSection,
            IReadOnlyList<MessageMonitorEntry> filteredEntries
        )
        {
            messageSection.Clear();

            if (!ui.Snapshot.DiagnosticsEnabled)
            {
                AddEmptyState(
                    messageSection,
                    "Diagnostics are Off",
                    "Enable diagnostics to collect message history."
                );
                return;
            }

            if (ui.Snapshot.Entries.Count == 0)
            {
                AddEmptyState(
                    messageSection,
                    "No messages yet",
                    "Diagnostics are On. No messages have been recorded yet."
                );
                return;
            }

            if (filteredEntries.Count == 0)
            {
                AddEmptyState(
                    messageSection,
                    "No matches",
                    "No messages match the current filter."
                );
                return;
            }

            messageSection.Add(CreateBreakdownFoldout(ui, filteredEntries));
            messageSection.Add(CreateListHeader());

            ScrollView list = new(ScrollViewMode.Vertical) { name = ListName };
            list.style.flexGrow = 1;
            list.style.flexShrink = 1;
            // flex-basis 0, not "auto". A hundred buffered rows make the log's content height
            // enormous, and shrinking is distributed in proportion to basis, so an auto basis lets
            // the log claim nearly all of the space and starve the sections beside it -- the
            // Component Diagnostics body ended up with a few pixels even on a 900x620 window.
            // Sizing it from the space left over instead makes the log the flexible one.
            list.style.flexBasis = 0;
            list.style.minHeight = MessageListMinHeight;
            int selectedEntryIndex = ClampSelectedIndex(
                ui.State.SelectedEntryIndex,
                filteredEntries.Count
            );
            for (int i = 0; i < filteredEntries.Count; i++)
            {
                int entryIndex = i;
                list.Add(
                    CreateRow(
                        filteredEntries[i],
                        i,
                        i == selectedEntryIndex,
                        () => ApplyState(ui, ui.State.WithSelectedEntryIndex(entryIndex))
                    )
                );
            }
            messageSection.Add(list);
            ui.List = list;

            VisualElement detailsSlot = new();
            // The detail pane is the one resizable lower area. Its handle sits above it, so an
            // upward drag grows the pane and gives the scrolling log less room. It remains
            // shrinkable at short window heights so the existing no-overflow contract wins over
            // a remembered size from a taller layout.
            detailsSlot.style.flexShrink = 1;
            if (ui.DetailsPaneHeight <= 0f)
            {
                detailsSlot.style.maxHeight = Length.Percent(DetailsMaxHeightPercent);
            }
            messageSection.Add(
                DxMessagingEditorTheme.CreateResizeHandle(
                    detailsSlot,
                    DetailsPaneMinHeight,
                    DetailsPaneResizeMaxHeight,
                    DetailsPaneResizerName,
                    ui.DetailsPaneHeight,
                    height =>
                    {
                        ui.DetailsPaneHeight = height;
                        ui.OnDetailsPaneHeightChanged?.Invoke(height);
                    },
                    growsUpward: true,
                    allowTargetShrink: true
                )
            );
            detailsSlot.Add(CreateDetailsPane(filteredEntries[selectedEntryIndex]));
            messageSection.Add(detailsSlot);
            ui.DetailsSlot = detailsSlot;
        }

        private static VisualElement CreateListHeader()
        {
            VisualElement header = new() { name = ListHeaderName };
            header.AddToClassList(DxMessagingEditorTheme.ListHeaderClassName);
            header.Add(CreateHeaderColumn("ROUTE", DxMessagingEditorTheme.ColumnTypeClassName));
            header.Add(
                CreateHeaderColumn("MESSAGE", DxMessagingEditorTheme.ColumnMessageClassName)
            );
            header.Add(CreateHeaderColumn("CONTEXT", DxMessagingEditorTheme.ColumnRouteClassName));
            header.Add(CreateHeaderColumn("#", DxMessagingEditorTheme.ColumnCountClassName));
            return header;
        }

        private static Label CreateHeaderColumn(string text, string columnClassName)
        {
            Label column = new(text);
            column.AddToClassList(columnClassName);
            return column;
        }

        /// <summary>
        /// The taxonomy filter chips, each naming its route kind and carrying how many of the
        /// currently matching entries it stands for. Issue #344 reported that the row colors were
        /// unexplained and that red read as a failure, so the legend and the filter are the same
        /// control rather than two.
        /// </summary>
        private static VisualElement CreateRouteKindFilterRow(MonitorUi ui)
        {
            VisualElement row = new() { name = RouteKindFilterRowName };
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            // No wrapping. Unity 2021.3 does not resolve a wrapping row's height from the lines it
            // wraps onto, so a second line lands outside this row and on top of the log below it -
            // the same defect #435 reported in live mode. Every control here gives space back
            // instead: the chips shrink to their floor and clip, and the hint already ends in
            // an ellipsis when it runs out of room.
            row.style.paddingLeft = 8;
            row.style.paddingRight = 8;
            row.style.paddingBottom = 6;

            ui.UntargetedChip = CreateRouteKindChip(
                UntargetedChipName,
                DxMessagingEditorPalette.UntargetedKind,
                ui.State.ShowUntargeted
            );
            ui.TargetedChip = CreateRouteKindChip(
                TargetedChipName,
                DxMessagingEditorPalette.TargetedKind,
                ui.State.ShowTargeted
            );
            ui.BroadcastChip = CreateRouteKindChip(
                BroadcastChipName,
                DxMessagingEditorPalette.BroadcastKind,
                ui.State.ShowBroadcast
            );

            void RaiseRouteKindsChanged()
            {
                ApplyState(
                    ui,
                    ui.State.WithRouteKinds(
                        ui.UntargetedChip.value,
                        ui.TargetedChip.value,
                        ui.BroadcastChip.value
                    )
                );
            }

            ui.UntargetedChip.RegisterValueChangedCallback(_ => RaiseRouteKindsChanged());
            ui.TargetedChip.RegisterValueChangedCallback(_ => RaiseRouteKindsChanged());
            ui.BroadcastChip.RegisterValueChangedCallback(_ => RaiseRouteKindsChanged());

            row.Add(ui.UntargetedChip);
            row.Add(ui.TargetedChip);
            row.Add(ui.BroadcastChip);

            // One line, always. Wrapping this sentence turns the chip row into a block several
            // times its height on a narrow window, which takes the space the log needs; the full
            // text stays on the badge tooltip and here.
            Label hint = new(SnapshotModeHintText)
            {
                name = ModeHintLabelName,
                tooltip = SnapshotModeHintText,
            };
            hint.AddToClassList(DxMessagingEditorTheme.CardLabelClassName);
            hint.style.marginBottom = 0;
            hint.style.marginLeft = 8;
            hint.style.flexShrink = 1;
            hint.style.whiteSpace = WhiteSpace.NoWrap;
            hint.style.overflow = Overflow.Hidden;
            hint.style.textOverflow = TextOverflow.Ellipsis;
            row.Add(hint);

            UpdateRouteKindChipText(ui);
            return row;
        }

        private static Toggle CreateRouteKindChip(string name, string routeKind, bool value)
        {
            Toggle chip = new() { name = name, value = value };
            DxMessagingEditorTheme.AddRouteKindChipClasses(chip, routeKind);
            chip.AddToClassList(DxMessagingEditorTheme.ChipWideClassName);
            chip.AddToClassList(DxMessagingEditorTheme.FilterClassName);

            // `.dx-chip` is a fixed letter box and a Toggle reserves a field-label column, both of
            // which would clip a named chip. The live Monitor collapses the same two pieces the
            // same way; neither belongs in the shared stylesheet, which the design system owns.
            VisualElement checkmark = chip.Q(className: "unity-toggle__checkmark");
            if (checkmark != null)
            {
                checkmark.style.display = DisplayStyle.None;
            }

            VisualElement label = chip.Q(className: "unity-base-field__label");
            if (label != null)
            {
                label.style.minWidth = 0;
                label.style.width = 0;
            }

            return chip;
        }

        /// <summary>
        /// Refreshes each chip's count and tooltip. The count is over the entries that pass the
        /// text filter, so a chip says how many rows it would add back rather than how many exist
        /// somewhere in the buffer.
        /// </summary>
        private static void UpdateRouteKindChipText(MonitorUi ui)
        {
            if (ui.UntargetedChip == null)
            {
                return;
            }

            IReadOnlyList<MessageMonitorEntry> textMatches = FilterEntries(
                ui.Snapshot.Entries,
                ui.State.FilterText
            );
            SetRouteKindChipText(
                ui.UntargetedChip,
                DxMessagingEditorPalette.UntargetedKind,
                textMatches,
                "no target: every registered receiver sees it"
            );
            SetRouteKindChipText(
                ui.TargetedChip,
                DxMessagingEditorPalette.TargetedKind,
                textMatches,
                "sent to one target object"
            );
            SetRouteKindChipText(
                ui.BroadcastChip,
                DxMessagingEditorPalette.BroadcastKind,
                textMatches,
                "sent from one source object"
            );
        }

        private static void SetRouteKindChipText(
            Toggle chip,
            string routeKind,
            IReadOnlyList<MessageMonitorEntry> entries,
            string meaning
        )
        {
            int count = 0;
            for (int index = 0; index < entries.Count; index++)
            {
                if (
                    string.Equals(
                        DxMessagingEditorPalette.NormalizeRouteKind(entries[index].RouteKind),
                        routeKind,
                        StringComparison.Ordinal
                    )
                )
                {
                    count++;
                }
            }

            chip.text = $"{routeKind} {count}";
            chip.tooltip =
                $"{routeKind} messages are {meaning}. This color marks them in every row; click to show or hide them.";
        }

        internal static IReadOnlyList<MessageMonitorEntry> FilterEntries(
            IReadOnlyList<MessageMonitorEntry> entries,
            MessageMonitorViewState viewState
        )
        {
            if (entries == null)
            {
                throw new ArgumentNullException(nameof(entries));
            }

            bool everyRouteKind =
                viewState.ShowUntargeted && viewState.ShowTargeted && viewState.ShowBroadcast;
            if (everyRouteKind && string.IsNullOrWhiteSpace(viewState.FilterText))
            {
                return entries;
            }

            return entries
                .Where(entry =>
                    viewState.ShowsRouteKind(entry.RouteKind) && entry.Matches(viewState.FilterText)
                )
                .ToArray();
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

        /// <summary>
        /// The collapsed-by-default breakdown of what the visible log contains: one clickable pill
        /// per message type and per context, each applying the filter that isolates it.
        /// </summary>
        /// <remarks>
        /// Issue #344 reported the previous always-expanded lane panels as an unreadable wall of
        /// aggregate text that also squeezed the log itself out of the window. The same lane data
        /// is now behind a disclosure, and each lane is the filter button rather than a paragraph
        /// next to one; the full context and message lists moved into the pill tooltips.
        /// </remarks>
        private static VisualElement CreateBreakdownFoldout(
            MonitorUi ui,
            IReadOnlyList<MessageMonitorEntry> filteredEntries
        )
        {
            MessageMonitorTypeLane[] typeLanes = BuildVisibleMessageTypeLanes(filteredEntries);
            MessageMonitorContextLane[] contextLanes = BuildVisibleContextLanes(filteredEntries);

            Foldout breakdown = new()
            {
                name = BreakdownFoldoutName,
                text =
                    $"Breakdown - {FormatCount(typeLanes.Length, "message type")}, {FormatCount(contextLanes.Length, "context")}",
                value = ui.BreakdownExpanded,
            };
            breakdown.tooltip =
                "Group the visible messages by type and by context. Every entry is a filter: click one to isolate it.";
            breakdown.RegisterValueChangedCallback(changed =>
                ui.BreakdownExpanded = changed.newValue
            );
            // Expanded, this is the tallest thing in the section, so it has to give space back like
            // everything else; its lane lists scroll, so shrinking costs nothing unreachable.
            breakdown.style.flexShrink = 1;
            breakdown.style.minHeight = FoldoutHeaderMinHeight;
            breakdown.style.marginBottom = 6;

            int typeLaneEntries = typeLanes.Sum(lane => lane.EntryCount);
            int contextLaneEntries = contextLanes.Sum(lane => lane.EntryCount);
            breakdown.Add(
                CreateLanePanel(
                    VisibleMessageTypeLanesName,
                    VisibleMessageTypeLanesSummaryLabelName,
                    VisibleMessageTypeLaneScrollViewName,
                    "Message types",
                    CreateVisibleMessageTypeLanesSummaryText(typeLanes),
                    typeLanes.Select(lane => CreateMessageTypeLanePill(ui, lane, typeLaneEntries))
                )
            );
            breakdown.Add(
                CreateLanePanel(
                    VisibleContextLanesName,
                    VisibleContextLanesSummaryLabelName,
                    VisibleContextLaneScrollViewName,
                    "Contexts",
                    CreateVisibleContextLanesSummaryText(contextLanes),
                    contextLanes.Select(lane => CreateContextLanePill(ui, lane, contextLaneEntries))
                )
            );
            return breakdown;
        }

        /// <summary>
        /// A wrapping row for content inside a <see cref="ScrollView"/>.
        ///
        /// The row belongs to this window rather than being the scroll view's own content
        /// container. Unity sizes that container to the viewport, so a wrapping content container
        /// keeps only the first screenful of lines: the rest are clipped and the scroller reports
        /// nothing to scroll to. An owned row is sized by its own lines, which the scroll view
        /// then scrolls (GitHub #440).
        /// </summary>
        private static VisualElement CreateScrollableWrapRow(string name)
        {
            VisualElement row = new() { name = name };
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexShrink = 0;
            DxMessagingEditorTheme.ApplyContentSizedWrap(row);
            return row;
        }

        private static VisualElement CreateLanePanel(
            string panelName,
            string summaryLabelName,
            string scrollViewName,
            string title,
            string summaryText,
            IEnumerable<VisualElement> pills
        )
        {
            VisualElement lanesRoot = new() { name = panelName };
            DxMessagingEditorTheme.ApplyCompleteBorder(
                lanesRoot,
                DxMessagingEditorPalette.BorderPanel
            );
            lanesRoot.style.flexShrink = 1;
            lanesRoot.style.minHeight = 0;
            lanesRoot.style.marginBottom = 6;
            lanesRoot.style.paddingTop = 6;
            lanesRoot.style.paddingRight = 6;
            lanesRoot.style.paddingBottom = 6;
            lanesRoot.style.paddingLeft = 6;

            Label titleLabel = new(title);
            titleLabel.AddToClassList(DxMessagingEditorTheme.CardLabelClassName);
            titleLabel.style.marginBottom = 2;
            lanesRoot.Add(titleLabel);

            Label summary = new(summaryText) { name = summaryLabelName };
            summary.AddToClassList(DxMessagingEditorTheme.KeyValueValueClassName);
            summary.style.whiteSpace = WhiteSpace.Normal;
            lanesRoot.Add(summary);

            ScrollView laneRows = new(ScrollViewMode.Vertical) { name = scrollViewName };
            laneRows.style.maxHeight = LanePillScrollMaxHeight;
            laneRows.style.flexShrink = 1;
            laneRows.style.minHeight = 0;
            laneRows.style.marginTop = 4;
            VisualElement laneRowsContent = CreateScrollableWrapRow(LanePillWrapRowName);
            laneRows.Add(laneRowsContent);
            lanesRoot.Add(laneRows);

            foreach (VisualElement pill in pills)
            {
                laneRowsContent.Add(pill);
            }

            return lanesRoot;
        }

        private static VisualElement CreateMessageTypeLanePill(
            MonitorUi ui,
            MessageMonitorTypeLane lane,
            int totalEntries
        )
        {
            return CreateLanePill(
                ui,
                VisibleMessageTypeLaneRowClassName,
                VisibleMessageTypeLaneFilterButtonName,
                VisibleMessageTypeLaneTypeLabelName,
                VisibleMessageTypeLaneSummaryLabelName,
                DxMessagingEditorPalette.Amber,
                lane.MessageTypeName,
                lane.EntryCount,
                totalEntries,
                $"Entries: {lane.EntryCount} | Contexts: {lane.ContextCount} | Share: {CreateEntryShareText(lane.EntryCount, totalEntries)}\nContexts: {lane.ContextsText}",
                CreateMessageTypeLaneFilterText(lane.MessageTypeName)
            );
        }

        private static VisualElement CreateContextLanePill(
            MonitorUi ui,
            MessageMonitorContextLane lane,
            int totalEntries
        )
        {
            return CreateLanePill(
                ui,
                VisibleContextLaneRowClassName,
                VisibleContextLaneFilterButtonName,
                VisibleContextLaneContextLabelName,
                VisibleContextLaneSummaryLabelName,
                DxMessagingEditorPalette.AmberSoft,
                lane.ContextText,
                lane.EntryCount,
                totalEntries,
                $"Entries: {lane.EntryCount} | Message types: {lane.MessageTypeCount} | Share: {CreateEntryShareText(lane.EntryCount, totalEntries)}\nMessages: {lane.MessageTypesText}",
                CreateContextLaneFilterText(lane.ContextText)
            );
        }

        /// <summary>
        /// One lane, rendered as a filter button. The wrapper carries the lane class so a caller
        /// can find the lane and read its labels; the button inside it is what a click lands on.
        /// </summary>
        /// <remarks>
        /// The pill shows only the lane's name and its share of the visible log. The counts it is
        /// derived from, and the full list of contexts or message types behind it, stay in the
        /// tooltip: on a busy scene there is one pill per message type and per context, and
        /// spelling all of that out is what made the previous lane panels unreadable.
        /// </remarks>
        private static VisualElement CreateLanePill(
            MonitorUi ui,
            string laneClassName,
            string buttonName,
            string nameLabelName,
            string summaryLabelName,
            Color borderColor,
            string laneName,
            int entryCount,
            int totalEntries,
            string tooltipText,
            string filterText
        )
        {
            VisualElement lane = new();
            lane.AddToClassList(laneClassName);
            lane.style.flexShrink = 0;
            lane.style.marginRight = 6;
            lane.style.marginBottom = 4;

            Button pill = new() { name = buttonName };
            pill.AddToClassList(DxMessagingEditorTheme.ButtonGhostClassName);
            DxMessagingEditorTheme.ApplyCompleteBorder(pill, borderColor);
            pill.style.flexDirection = FlexDirection.Row;
            pill.style.alignItems = Align.Center;
            pill.style.marginTop = 0;
            pill.style.marginRight = 0;
            pill.style.marginBottom = 0;
            pill.style.marginLeft = 0;
            pill.style.paddingTop = 4;
            pill.style.paddingRight = 8;
            pill.style.paddingBottom = 4;
            pill.style.paddingLeft = 8;
            pill.tooltip = $"{tooltipText}\nClick to filter to {filterText}";
            pill.SetEnabled(!string.IsNullOrWhiteSpace(filterText));
            pill.RegisterCallback<ClickEvent>(_ =>
                ApplyState(ui, ui.State.WithFilterText(filterText))
            );

            Label nameLabel = new(laneName) { name = nameLabelName };
            nameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            nameLabel.style.marginRight = 6;
            pill.Add(nameLabel);

            Label summaryLabel = new(CreateLanePillSummaryText(entryCount, totalEntries))
            {
                name = summaryLabelName,
            };
            summaryLabel.AddToClassList(DxMessagingEditorTheme.PriorityClassName);
            pill.Add(summaryLabel);

            lane.Add(pill);
            return lane;
        }

        /// <summary>
        /// The compact count-and-share text on a lane pill, for example <c>9 - 50%</c>. The share
        /// is dropped when there is nothing to be a share of.
        /// </summary>
        internal static string CreateLanePillSummaryText(int entryCount, int totalEntries)
        {
            string count = entryCount.ToString(CultureInfo.InvariantCulture);
            if (totalEntries <= 0)
            {
                return count;
            }

            int percent = (int)
                Math.Round((double)entryCount / totalEntries * 100, MidpointRounding.AwayFromZero);
            return $"{count} - {percent.ToString(CultureInfo.InvariantCulture)}%";
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
            MonitorUi ui,
            Action onRefresh,
            Action onEnterLiveMode
        )
        {
            VisualElement controls = new();
            controls.style.flexShrink = 0;
            controls.style.paddingTop = 6;
            controls.style.paddingRight = 8;
            controls.style.paddingLeft = 8;

            VisualElement filterRow = new();
            filterRow.style.flexDirection = FlexDirection.Row;
            filterRow.style.alignItems = Align.Center;
            controls.Add(filterRow);

            TextField filter = new("Filter") { name = FilterFieldName };
            filter.AddToClassList(DxMessagingEditorTheme.SearchClassName);
            filter.SetValueWithoutNotify(ui.State.FilterText);
            filter.tooltip =
                "Use plain text, or field filters such as type:, message:, context:, and stack:.";
            filter.style.flexGrow = 1;
            filter.style.marginRight = 8;
            filter.RegisterValueChangedCallback(evt =>
                ApplyState(ui, ui.State.WithFilterText(evt.newValue))
            );
            ui.Filter = filter;
            filterRow.Add(filter);

            // Buttons are wired through ClickEvent rather than the Button(Action) constructor, the
            // same as the rest of this package's editor UI: Button(Action) installs a Clickable that
            // only answers pointer down/up, so neither a test nor a script can drive it.
            Button refresh = new()
            {
                name = RefreshButtonName,
                text = "Refresh",
                tooltip = "Re-read the bus emission buffer. Nothing arrives between refreshes.",
            };
            refresh.RegisterCallback<ClickEvent>(_ => onRefresh?.Invoke());
            refresh.AddToClassList(DxMessagingEditorTheme.ToolButtonClassName);
            refresh.SetEnabled(onRefresh != null);
            refresh.style.marginRight = 6;
            refresh.style.flexShrink = 0;
            filterRow.Add(refresh);

            Button export = new()
            {
                name = ExportButtonName,
                text = "Copy JSON",
                tooltip = "Copy the currently visible entries to the clipboard as JSON.",
            };
            export.RegisterCallback<ClickEvent>(_ =>
                ui.OnCopyExport?.Invoke(CreateExportText(ui.Snapshot, ui.FilteredEntries()))
            );
            export.AddToClassList(DxMessagingEditorTheme.ToolButtonClassName);
            export.style.flexShrink = 0;
            ui.Export = export;
            filterRow.Add(export);

            Button live = new()
            {
                name = LiveButtonName,
                text = "Live",
                tooltip = "Switch to the live log, which drains new emissions as they happen.",
            };
            live.RegisterCallback<ClickEvent>(_ => onEnterLiveMode?.Invoke());
            live.AddToClassList(DxMessagingEditorTheme.ToolButtonClassName);
            live.SetEnabled(onEnterLiveMode != null);
            live.style.marginLeft = 6;
            live.style.flexShrink = 0;
            filterRow.Add(live);

            ui.ActiveFilter = CreateActiveFilterSummary(
                ui.State.FilterText,
                () => ApplyState(ui, ui.State.WithFilterText(string.Empty))
            );
            controls.Add(ui.ActiveFilter);

            SetExportButtonEnabled(ui, ui.FilteredEntries().Count);
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
            VisualElement tokenRow = CreateScrollableWrapRow(ActiveFilterTokenWrapRowName);
            tokenScroll.Add(tokenRow);
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
                tokenRow.Add(tokenLabel);
            }
        }

        private static void SetExportButtonEnabled(MonitorUi ui, int visibleEntryCount)
        {
            ui.Export?.SetEnabled(
                ui.OnCopyExport != null && ui.Snapshot.DiagnosticsEnabled && visibleEntryCount > 0
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
#if UNITY_6000_5_OR_NEWER
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

        private static void AddEmptyState(VisualElement root, string title, string body)
        {
            VisualElement empty = DxMessagingEditorTheme.CreateEmptyState(
                title,
                body,
                bodyName: EmptyStateLabelName,
                titleName: EmptyStateTitleLabelName
            );
            empty.style.marginTop = 8;
            root.Add(empty);
        }

        /// <summary>
        /// One log row: a fixed-height columnar row carrying the route kind, the message type, the
        /// context and the dispatch id.
        /// </summary>
        /// <remarks>
        /// The stack trace is deliberately absent. Issue #344 reported that rendering it on every
        /// row buried the log under call stacks; it belongs to whichever row is selected, which is
        /// where <see cref="CreateDetailsPane"/> puts it, behind a collapsed disclosure.
        /// </remarks>
        private static VisualElement CreateRow(
            MessageMonitorEntry entry,
            int entryIndex,
            bool selected,
            Action onSelected
        )
        {
            VisualElement row = new();
            row.AddToClassList(RowClassName);
            row.AddToClassList(DxMessagingEditorTheme.RowClassName);
            if (entryIndex % 2 == 1)
            {
                row.AddToClassList(DxMessagingEditorTheme.RowAlternateClassName);
            }
            if (selected)
            {
                row.style.backgroundColor = DxMessagingEditorPalette.SelectedWash;
            }
            if (onSelected != null)
            {
                row.RegisterCallback<ClickEvent>(_ => onSelected.Invoke());
            }

            string routeKind = DxMessagingEditorPalette.NormalizeRouteKind(entry.RouteKind);
            VisualElement route = new();
            route.AddToClassList(DxMessagingEditorTheme.RowTypeClassName);
            VisualElement dot = new();
            DxMessagingEditorTheme.AddRouteKindDotClasses(dot, routeKind);
            route.Add(dot);
            Label kind = new(string.IsNullOrEmpty(routeKind) ? "Other" : routeKind)
            {
                name = RouteKindLabelName,
            };
            route.Add(kind);
            row.Add(route);

            Label type = new(entry.MessageTypeName)
            {
                name = MessageTypeLabelName,
                tooltip = entry.MessageTypeDisplayPath,
            };
            type.AddToClassList(DxMessagingEditorTheme.RowMessageClassName);
            row.Add(type);

            Label context = new(entry.ContextText)
            {
                name = ContextLabelName,
                tooltip = entry.ContextText,
            };
            context.AddToClassList(DxMessagingEditorTheme.RowRouteClassName);
            row.Add(context);

            Label trace = new(CreateTraceText(entry)) { name = TraceLabelName };
            trace.AddToClassList(DxMessagingEditorTheme.RowCountClassName);
            row.Add(trace);

            return row;
        }

        /// <summary>
        /// The dispatch-id column. An entry built without a bus trace carries 0, which is not a
        /// dispatch id, so it shows nothing rather than a number that means "missing".
        /// </summary>
        internal static string CreateTraceText(MessageMonitorEntry entry)
        {
            return entry.TraceId <= 0
                ? string.Empty
                : "#" + entry.TraceId.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Component diagnostics, behind a disclosure that starts closed. The panel is a reference
        /// surface rather than something a reader watches, and issue #344 reported it as one of the
        /// blocks that pushed the log off the bottom of the window.
        /// </summary>
        private static VisualElement CreateComponentSection(MonitorUi ui)
        {
            Foldout foldout = new()
            {
                name = ComponentFoldoutName,
                text = $"Component Diagnostics ({ui.Components.Count})",
                value = ui.ComponentsExpanded,
            };
            foldout.tooltip =
                "Registration state of every MessagingComponent in the loaded scenes.";
            foldout.RegisterValueChangedCallback(changed =>
                ui.ComponentsExpanded = changed.newValue
            );
            // Shrinks, with a floor at its own header. A zero-shrink item still claims its full
            // preferred height, so a populated, expanded panel would push past the bottom of a short
            // window no matter how much the log beside it gave up. The floor is what keeps the
            // squeeze off the row a reader clicks, and the rows inside scroll.
            foldout.style.flexShrink = 1;
            foldout.style.minHeight = FoldoutHeaderMinHeight;
            foldout.style.paddingLeft = 8;
            foldout.style.paddingRight = 8;
            foldout.contentContainer.style.flexShrink = 1;
            foldout.contentContainer.style.minHeight = 0;
            // Expanded on a window with no room for it, the body is clipped to whatever the section
            // was given rather than drawn past the bottom edge. The rows inside stay reachable
            // because they live in their own scroll view, which shrinks with it.
            foldout.contentContainer.style.overflow = Overflow.Hidden;
            foldout.Add(CreateComponentPanel(ui.Components, ui));
            return foldout;
        }

        private static VisualElement CreateComponentPanel(
            IReadOnlyList<ComponentMonitorEntry> componentEntries,
            MonitorUi ui = null
        )
        {
            VisualElement panel = new() { name = ComponentPanelName };
            DxMessagingEditorTheme.ApplyCompleteBorder(panel, DxMessagingEditorPalette.BorderPanel);
            panel.style.flexShrink = 1;
            panel.style.minHeight = 0;
            panel.style.overflow = Overflow.Hidden;
            panel.style.paddingTop = 8;
            panel.style.paddingRight = 8;
            panel.style.paddingBottom = 8;
            panel.style.paddingLeft = 8;

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
            componentScroll.style.flexShrink = 1;
            componentScroll.style.minHeight = 0;
            componentScroll.style.marginTop = 2;
            panel.Add(componentScroll);

            foreach (ComponentMonitorEntry componentEntry in componentEntries)
            {
                componentScroll.Add(CreateComponentRow(componentEntry));
            }

            panel.Add(
                DxMessagingEditorTheme.CreateResizeHandle(
                    componentScroll,
                    ComponentPanelMinHeight,
                    ComponentPanelResizeMaxHeight,
                    ComponentResizerName,
                    ui?.ComponentPanelHeight ?? 0f,
                    height =>
                    {
                        if (ui != null)
                        {
                            ui.ComponentPanelHeight = height;
                        }
                    }
                )
            );

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

        /// <summary>
        /// The selected entry, rendered as the design system's detail pane: a header naming the
        /// route kind and message type, the emission's fields, and the stack trace behind a
        /// disclosure that starts closed.
        /// </summary>
        private static VisualElement CreateDetailsPane(MessageMonitorEntry entry)
        {
            VisualElement details = new() { name = DetailsPaneName };
            details.AddToClassList(DxMessagingEditorTheme.DetailClassName);
            // The pane gives space back when the window is short, and its body scrolls rather than
            // spilling out of the bottom, so a 320 px window still shows the log above it.
            details.style.flexGrow = 1;
            details.style.flexShrink = 1;
            details.style.minHeight = 0;
            details.style.overflow = Overflow.Hidden;
            DxMessagingEditorTheme.ApplyCompleteBorder(
                details,
                DxMessagingEditorPalette.BorderPanel
            );

            VisualElement head = new();
            head.AddToClassList(DxMessagingEditorTheme.DetailHeadClassName);
            string routeKind = DxMessagingEditorPalette.NormalizeRouteKind(entry.RouteKind);
            Label badge = new(string.IsNullOrEmpty(routeKind) ? "Other" : routeKind);
            DxMessagingEditorTheme.AddRouteKindTypeBadgeClasses(badge, routeKind);
            head.Add(badge);
            Label type = new(entry.MessageTypeName)
            {
                name = DetailsTypeLabelName,
                tooltip = entry.MessageTypeDisplayPath,
            };
            type.AddToClassList(DxMessagingEditorTheme.DetailTitleClassName);
            head.Add(type);
            Label trace = new(CreateTraceText(entry));
            trace.AddToClassList(DxMessagingEditorTheme.DetailFrameClassName);
            trace.style.flexGrow = 1;
            trace.style.unityTextAlign = TextAnchor.MiddleRight;
            head.Add(trace);
            head.style.flexShrink = 0;
            details.Add(head);

            ScrollView body = new(ScrollViewMode.Vertical);
            body.style.flexGrow = 1;
            body.style.flexShrink = 1;
            body.style.minHeight = 0;
            details.Add(body);

            VisualElement card = new();
            card.AddToClassList(DxMessagingEditorTheme.CardClassName);
            Label cardLabel = new("EMISSION");
            cardLabel.AddToClassList(DxMessagingEditorTheme.CardLabelClassName);
            card.Add(cardLabel);
            card.Add(CreateTypeDetailRow(entry));
            card.Add(CreateContextDetailRow(entry, DetailsContextLabelName));
            body.Add(card);

            body.Add(
                CreateStackTraceSection(
                    entry,
                    DetailsStackFoldoutName,
                    DetailsStackFirstFrameLabelName
                )
            );

            return details;
        }

        /// <summary>
        /// The message type, plus an "Open source" link when its declaring file can be found.
        /// Issue #344 asked for the type to take a reader to source; the resolver behind the
        /// link is the same one the Flow Graph uses, so a type that opens in one window opens
        /// in the other.
        /// </summary>
        internal static VisualElement CreateTypeDetailRow(MessageMonitorEntry entry)
        {
            VisualElement row = CreateKeyValue(
                "Type",
                entry.MessageTypeDisplayPath,
                DetailsTypeValueLabelName
            );
            row.name = DetailsTypeRowName;
            if (
                DxMessagingEditorSourceLinks.TryResolveSourceForAssemblyQualifiedName(
                    entry.MessageTypeIdentity,
                    out DxMessagingEditorSourceLinks.SourceLocation location
                )
                && AssetDatabase.LoadMainAssetAtPath(location.AssetPath) != null
            )
            {
                row.Add(
                    DxMessagingEditorSourceLinks.CreateSourceLinkButton(
                        "Open source",
                        location,
                        includeLocationInText: false
                    )
                );
            }
            return row;
        }

        /// <summary>
        /// The context, made clickable when the object it named is still alive so the reader
        /// lands on it in the Hierarchy with the Inspector already showing it. A context whose
        /// object is gone -- the normal case for a log that outlives its scene -- stays readable
        /// but inert rather than offering a link that would do nothing.
        /// </summary>
        internal static VisualElement CreateContextDetailRow(
            MessageMonitorEntry entry,
            string valueName
        )
        {
            VisualElement row = CreateKeyValue("Context", entry.ContextText, valueName);
            row.name = DetailsContextRowName;
            InstanceId? context = entry.Context;
            if (DxMessagingEditorSourceLinks.FindContextObject(context) == null)
            {
                return row;
            }

            // `Q<T>(null)` matches ANY descendant and would return the key label, so an unnamed
            // value falls back to the row rather than linking the wrong element.
            VisualElement value = string.IsNullOrEmpty(valueName)
                ? null
                : row.Q<VisualElement>(valueName);
            DxMessagingEditorSourceLinks.MakeActivatable(
                value ?? row,
                "Select and ping this object in the Hierarchy.",
                () => DxMessagingEditorSourceLinks.TryRevealContext(context)
            );
            return row;
        }

        /// <summary>
        /// The captured call stack as one row per frame, each with its own "Open" link when the
        /// frame names a file and line. Unity's own capture frames are dropped: issue #344
        /// reported them as noise at the top of every trace, and they are -- they describe the
        /// act of taking the stack, never the code that emitted.
        /// </summary>
        internal static VisualElement CreateStackTraceSection(
            MessageMonitorEntry entry,
            string foldoutName,
            string labelName
        )
        {
            IReadOnlyList<string> frames = DxMessagingEditorSourceLinks.ReadCallSiteFrames(
                entry.StackTrace
            );
            bool captured = frames.Count > 0;
            // Three distinct facts, and calling any of them by another's name would be a lie:
            // a trace holding only Unity's capture frames WAS captured; an empty trace while
            // capture is off is the opt-in setting, not a missing call site; an empty trace while
            // capture is ON is a record written before it was turned on (or built by hand).
            bool captureFramesOnly = !captured && !string.IsNullOrWhiteSpace(entry.StackTrace);
            bool captureDisabled =
                !captured && !captureFramesOnly && !DxMessagingEmissionCaptureNotice.CaptureEnabled;
            string emptyText;
            string emptyBody;
            if (captureFramesOnly)
            {
                emptyText = "Stack trace (no caller frames)";
                emptyBody = "Stack trace: captured, but every frame was Unity's own stack capture.";
            }
            else if (captureDisabled)
            {
                emptyText = $"Stack trace ({DxMessagingEmissionCaptureNotice.DisabledSummary})";
                emptyBody = DxMessagingEmissionCaptureNotice.DisabledExplanation;
            }
            else
            {
                emptyText = "Stack trace (not captured)";
                emptyBody = "Stack trace: not captured";
            }
            Foldout stackFoldout = new()
            {
                name = foldoutName,
                text = captured ? $"Stack trace ({frames.Count})" : emptyText,
                value = false,
                tooltip =
                    "The call stack the emission was recorded from, one row per frame. Collapsed "
                    + "by default because a stack per row is what buries the log. Unity's own "
                    + "stack-capture frames are left out.",
            };
            VisualElement stackFrames = new();

            if (!captured)
            {
                if (captureDisabled)
                {
                    // The switch travels with the explanation: an empty pane that only says
                    // "off" still leaves the user hunting through project settings.
                    stackFrames.Add(
                        DxMessagingEmissionCaptureNotice.CreateDisabledNotice(labelName)
                    );
                }
                else
                {
                    Label empty = new(emptyBody) { name = labelName };
                    empty.style.whiteSpace = WhiteSpace.Normal;
                    stackFrames.Add(empty);
                }

                stackFoldout.Add(stackFrames);
                // Opened by default ONLY in the capture-off state: the header alone cannot carry
                // the reason plus the fix, and a collapsed foldout is exactly how the setting
                // stayed invisible. A real trace stays collapsed so it cannot bury the log.
                stackFoldout.value = captureDisabled;
                return stackFoldout;
            }

            bool first = true;
            foreach (string frame in frames)
            {
                VisualElement frameRow = new();
                frameRow.AddToClassList(DetailsStackFrameRowClassName);
                frameRow.style.flexDirection = FlexDirection.Row;
                frameRow.style.alignItems = Align.FlexStart;
                frameRow.style.flexShrink = 0;

                Label frameLabel = new(frame) { tooltip = frame };
                // The first surviving frame is the emitting call site, so it reads as the answer
                // and the frames beneath it read as the path that led there.
                if (first)
                {
                    frameLabel.name = labelName;
                    frameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
                    first = false;
                }
                frameLabel.AddToClassList(DxMessagingEditorTheme.KeyValueValueClassName);
                frameLabel.style.whiteSpace = WhiteSpace.Normal;
                frameLabel.style.flexGrow = 1;
                frameLabel.style.flexShrink = 1;
                frameLabel.style.marginTop = 0;
                frameLabel.style.marginBottom = 0;
                frameRow.Add(frameLabel);

                if (
                    DxMessagingEditorSourceLinks.TryParseSourceLocation(
                        frame,
                        out DxMessagingEditorSourceLinks.SourceLocation frameLocation
                    )
                    && AssetDatabase.LoadMainAssetAtPath(frameLocation.AssetPath) != null
                )
                {
                    Button open = DxMessagingEditorSourceLinks.CreateSourceLinkButton(
                        "Open",
                        frameLocation,
                        includeLocationInText: false
                    );
                    open.AddToClassList(DxMessagingEditorTheme.DetailStackLinkClassName);
                    frameRow.Add(open);
                }
                stackFrames.Add(frameRow);
            }

            stackFoldout.Add(stackFrames);
            return stackFoldout;
        }

        internal static VisualElement CreateKeyValue(
            string key,
            string value,
            string valueName = null
        )
        {
            VisualElement pair = new();
            pair.AddToClassList(DxMessagingEditorTheme.KeyValueClassName);
            Label keyLabel = new(key);
            keyLabel.AddToClassList(DxMessagingEditorTheme.KeyValueKeyClassName);
            pair.Add(keyLabel);
            Label valueLabel = new(value) { tooltip = value };
            if (!string.IsNullOrEmpty(valueName))
            {
                valueLabel.name = valueName;
            }
            valueLabel.AddToClassList(DxMessagingEditorTheme.KeyValueValueClassName);
            pair.Add(valueLabel);
            return pair;
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

    /// <summary>
    /// What the snapshot Monitor is showing: the free-text filter, the selected row, and which
    /// route kinds the taxonomy chips are letting through.
    /// </summary>
    /// <remarks>
    /// The chip state is stored as what is <em>hidden</em> so that <c>default</c> is an unfiltered
    /// log, matching <see cref="MessageMonitorLiveViewState"/>. A struct's default value is
    /// reachable without going through its constructor, and a default that hid every route kind
    /// would silently render an empty log at each of those.
    /// </remarks>
    internal readonly struct MessageMonitorViewState
    {
        internal static MessageMonitorViewState Default { get; } = new();

        private readonly string _filterText;
        private readonly bool _hideUntargeted;
        private readonly bool _hideTargeted;
        private readonly bool _hideBroadcast;

        internal MessageMonitorViewState(
            string filterText = "",
            int selectedEntryIndex = 0,
            bool showUntargeted = true,
            bool showTargeted = true,
            bool showBroadcast = true
        )
        {
            _filterText = filterText;
            SelectedEntryIndex = selectedEntryIndex;
            _hideUntargeted = !showUntargeted;
            _hideTargeted = !showTargeted;
            _hideBroadcast = !showBroadcast;
        }

        /// <summary>
        /// Never null, including on <c>default</c>. A struct's default value is reachable without
        /// going through its constructor, so normalizing on read rather than on construction is
        /// what makes <c>default</c> and <c>new(...)</c> compare equal.
        /// </summary>
        internal string FilterText => _filterText ?? string.Empty;

        internal int SelectedEntryIndex { get; }

        internal bool ShowUntargeted => !_hideUntargeted;

        internal bool ShowTargeted => !_hideTargeted;

        internal bool ShowBroadcast => !_hideBroadcast;

        internal bool ShowsRouteKind(string routeKind)
        {
            return DxMessagingEditorPalette.ShowsRouteKind(
                routeKind,
                ShowUntargeted,
                ShowTargeted,
                ShowBroadcast
            );
        }

        internal MessageMonitorViewState WithFilterText(string filterText)
        {
            return new MessageMonitorViewState(
                filterText,
                selectedEntryIndex: 0,
                ShowUntargeted,
                ShowTargeted,
                ShowBroadcast
            );
        }

        internal MessageMonitorViewState WithSelectedEntryIndex(int selectedEntryIndex)
        {
            return new MessageMonitorViewState(
                FilterText,
                selectedEntryIndex,
                ShowUntargeted,
                ShowTargeted,
                ShowBroadcast
            );
        }

        /// <summary>
        /// The same filter with one taxonomy chip flipped. Changing what the log shows drops the
        /// selection back to the newest row, because the old index pointed into a different row
        /// set.
        /// </summary>
        internal MessageMonitorViewState WithRouteKinds(
            bool showUntargeted,
            bool showTargeted,
            bool showBroadcast
        )
        {
            return new MessageMonitorViewState(
                FilterText,
                selectedEntryIndex: 0,
                showUntargeted,
                showTargeted,
                showBroadcast
            );
        }
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
            string routeKind = null,
            long traceId = 0,
            InstanceId? context = null
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
            TraceId = traceId;
            Context = context;
        }

        internal string MessageTypeName { get; }

        internal string MessageTypeIdentity { get; }

        internal string MessageTypeDisplayPath { get; }

        internal string ContextText { get; }

        internal string StackTrace { get; }

        internal string RouteKind { get; }

        /// <summary>
        /// The bus-assigned dispatch sequence number this entry came from, or 0 when the emission
        /// carried no trace (records built by the public <see cref="MessageEmissionData"/>
        /// constructor). Bus-side records start at 1 and increase monotonically, which is what
        /// <see cref="MessageMonitorLiveRecorder"/> uses to poll for new emissions without
        /// re-ingesting ones it already holds.
        /// </summary>
        internal long TraceId { get; }

        /// <summary>
        /// The package-standard identity of the context this emission was routed through.
        /// Object-backed <see cref="InstanceId"/> values preserve a selectable editor reference;
        /// Unity's destroyed-object null semantics make the reference stop resolving after its
        /// native scene object is gone.
        /// </summary>
        internal InstanceId? Context { get; }

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
                CreateRouteKind(messageType),
                emission.traceId,
                emission.context
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
