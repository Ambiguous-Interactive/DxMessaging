#if UNITY_EDITOR
namespace DxMessaging.Editor.Windows
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using DxMessaging.Editor;
    using UnityEngine;
    using UnityEngine.UIElements;

    /// <summary>
    /// Which route kinds the live log is showing, alongside the free-text filter and the selected
    /// row. Kept free of any GUI type so the filtering and formatting rules stay unit-testable.
    /// </summary>
    /// <remarks>
    /// The chip state is stored as what is <em>hidden</em> so that <c>default</c> is an unfiltered
    /// log. A struct's default value is reachable without going through its constructor -- an
    /// uninitialized field, an array element, an omitted optional parameter -- and a default that
    /// hid every route kind would silently render an empty log at each of those.
    /// </remarks>
    internal readonly struct MessageMonitorLiveViewState
    {
        internal static MessageMonitorLiveViewState Default { get; } = new();

        private readonly string _filterText;
        private readonly bool _hideUntargeted;
        private readonly bool _hideTargeted;
        private readonly bool _hideBroadcast;

        internal MessageMonitorLiveViewState(
            string filterText = "",
            bool showUntargeted = true,
            bool showTargeted = true,
            bool showBroadcast = true,
            long selectedTraceId = FollowNewest
        )
        {
            _filterText = filterText;
            _hideUntargeted = !showUntargeted;
            _hideTargeted = !showTargeted;
            _hideBroadcast = !showBroadcast;
            SelectedTraceId = selectedTraceId;
        }

        /// <summary>
        /// <see cref="SelectedTraceId"/> value meaning "no row is pinned", so the detail pane
        /// follows whatever is newest. Also what a row that has aged out of the log falls back to.
        /// </summary>
        internal const long FollowNewest = 0;

        /// <summary>Never null, including on <c>default</c>, so it can be bound straight to a field.</summary>
        internal string FilterText => _filterText ?? string.Empty;

        internal bool ShowUntargeted => !_hideUntargeted;

        internal bool ShowTargeted => !_hideTargeted;

        internal bool ShowBroadcast => !_hideBroadcast;

        /// <summary>
        /// <see cref="MessageMonitorLiveEntry.FirstTraceId"/> of the pinned row, or
        /// <see cref="FollowNewest"/>.
        /// </summary>
        /// <remarks>
        /// The selection is keyed by dispatch id rather than by list position because the log is
        /// newest-first: every poll that appends a row shifts every index down, so an index would
        /// quietly repoint the detail pane at a different emission while recording continues.
        /// </remarks>
        internal long SelectedTraceId { get; }

        internal MessageMonitorLiveViewState WithSelectedTraceId(long selectedTraceId)
        {
            return new MessageMonitorLiveViewState(
                FilterText,
                ShowUntargeted,
                ShowTargeted,
                ShowBroadcast,
                selectedTraceId
            );
        }

        /// <summary>
        /// True when this row's route kind is switched on. A row whose route kind is neither
        /// untargeted, targeted nor broadcast (unrecognized or missing) is never hidden by the
        /// chips, so the chips can only ever hide rows they can also bring back.
        /// </summary>
        internal bool ShowsRouteKind(string routeKind)
        {
            return DxMessagingEditorPalette.ShowsRouteKind(
                routeKind,
                ShowUntargeted,
                ShowTargeted,
                ShowBroadcast
            );
        }
    }

    /// <summary>
    /// One live-log footer statistic: a number and the noun it counts.
    /// </summary>
    internal readonly struct MessageMonitorLiveFooterStat
    {
        internal MessageMonitorLiveFooterStat(string number, string label)
        {
            Number = number;
            Label = label;
        }

        /// <summary>Rendered as the highlighted <c>.dx-footer__num</c>.</summary>
        internal string Number { get; }

        internal string Label { get; }
    }

    /// <summary>
    /// Callbacks the live Monitor view raises. Every one is optional so the view can be built and
    /// asserted on without a hosting window.
    /// </summary>
    internal sealed class MessageMonitorLiveViewCallbacks
    {
        /// <summary>Raised by the record toggle with the requested recording state.</summary>
        internal Action<bool> OnRecordingChanged { get; set; }

        /// <summary>Raised by the Clear button.</summary>
        internal Action OnClear { get; set; }

        /// <summary>
        /// Raised whenever the filter text, a taxonomy chip or the row selection changes, with the
        /// complete next state. The host is expected to store it and re-render the body.
        /// </summary>
        internal Action<MessageMonitorLiveViewState> OnStateChanged { get; set; }

        /// <summary>Raised by the Snapshot button to leave live mode.</summary>
        internal Action OnExitLiveMode { get; set; }
    }

    /// <summary>
    /// Renders the live Message Monitor: a columnar, continuously drained log of bus emissions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the design system's recorder surface -- the <c>.dx-record</c> toggle, the taxonomy
    /// <c>.dx-chip</c> filters, the <c>.dx-list-header</c> / <c>.dx-col-*</c> / <c>.dx-row__*</c>
    /// columns, the <c>.dx-detail</c> / <c>.dx-kv</c> detail pane and the <c>.dx-footer</c> stats --
    /// rendered over the real <see cref="MessageMonitorLiveRecorder"/> log. It is additive: the
    /// snapshot Monitor keeps its card and lane layout, and the two modes swap in the same window.
    /// </para>
    /// <para>
    /// The toolbar is built once and the body re-rendered on its own, so typing in the filter does
    /// not rebuild (and unfocus) the field the user is typing into.
    /// </para>
    /// </remarks>
    internal static class DxMessagingMessageMonitorLiveView
    {
        internal const string RootName = "dxmessaging-monitor-live";
        internal const string ToolbarName = "dxmessaging-monitor-live-toolbar";
        internal const string BodyName = "dxmessaging-monitor-live-body";
        internal const string RecordToggleName = "dxmessaging-monitor-live-record";
        internal const string ClearButtonName = "dxmessaging-monitor-live-clear";
        internal const string SnapshotButtonName = "dxmessaging-monitor-live-snapshot";
        internal const string FilterFieldName = "dxmessaging-monitor-live-filter";
        internal const string UntargetedChipName = "dxmessaging-monitor-live-chip-untargeted";
        internal const string TargetedChipName = "dxmessaging-monitor-live-chip-targeted";
        internal const string BroadcastChipName = "dxmessaging-monitor-live-chip-broadcast";
        internal const string ListHeaderName = "dxmessaging-monitor-live-header";
        internal const string ListName = "dxmessaging-monitor-live-list";
        internal const string RowClassName = "dxmessaging-monitor-live-row";
        internal const string RowTimeLabelName = "dxmessaging-monitor-live-row-time";
        internal const string RowRouteLabelName = "dxmessaging-monitor-live-row-route";
        internal const string RowMessageLabelName = "dxmessaging-monitor-live-row-message";
        internal const string RowContextLabelName = "dxmessaging-monitor-live-row-context";
        internal const string RowCountLabelName = "dxmessaging-monitor-live-row-count";
        internal const string DetailName = "dxmessaging-monitor-live-detail";
        internal const string DetailTitleLabelName = "dxmessaging-monitor-live-detail-title";
        internal const string DetailFrameLabelName = "dxmessaging-monitor-live-detail-frame";
        internal const string DetailStackFoldoutName =
            "dxmessaging-monitor-live-detail-stack-foldout";
        internal const string DetailStackLabelName = "dxmessaging-monitor-live-detail-stack";
        internal const string FooterName = "dxmessaging-monitor-live-footer";
        internal const string EmptyBodyName = "dxmessaging-monitor-live-empty";
        internal const string EmptyTitleName = "dxmessaging-monitor-live-empty-title";
        internal const string GapNoticeName = "dxmessaging-monitor-live-gap";
        internal const string GapNoticeTitleName = "dxmessaging-monitor-live-gap-title";
        internal const string GapNoticeBodyName = "dxmessaging-monitor-live-gap-body";
        internal const string ModeBadgeLabelName = "dxmessaging-monitor-live-mode-badge";
        internal const string ModeHintLabelName = "dxmessaging-monitor-live-mode-hint";

        internal const string Title = "Message Monitor (Live)";

        /// <summary>
        /// Badge text for the streaming mode, alongside the one-line explanation of what a row in
        /// that mode stands for. Issue #344 reported that a reader cannot tell whether the Monitor
        /// is streaming or deduplicating; the live log does both, and says so here.
        /// </summary>
        internal const string LiveModeBadgeText = "LIVE";

        internal const string LiveModeHintText =
            "Streaming. Repeats of the same message and context merge into one row, and the N column counts them.";

        /// <summary>
        /// Row height for the virtualized list, matching the <c>.dx-row</c> height in
        /// <c>Editor/Theme/DxMessagingTheme.uss</c>. <see cref="ListView"/> needs the height as a
        /// number to size its viewport before any row is laid out, so it cannot read it from USS.
        /// </summary>
        internal const int RowHeight = 28;

        /// <summary>
        /// Horizontal size of a taxonomy chip's letter box, mirroring <c>.dx-chip</c>. The chip is
        /// a <see cref="Toggle"/> so it can carry the <c>:checked</c> state the stylesheet keys its
        /// on/off opacity off, and a Toggle reserves a field-label column that would push the
        /// letter out of that box; these two inline rules reclaim it without editing the stylesheet
        /// away from its design-system source.
        /// </summary>
        private const int ChipLabelWidth = 0;

        private const int DetailStackTraceMaxHeight = 120;

        // The body renders into four fixed slots so a poll can update each independently. Named
        // only so the UI Toolkit debugger reads clearly; nothing queries them.
        private const string ListSlotName = "dxmessaging-monitor-live-list-slot";
        private const string DetailSlotName = "dxmessaging-monitor-live-detail-slot";
        private const string FooterSlotName = "dxmessaging-monitor-live-footer-slot";

        /// <summary>
        /// What the body is currently showing, and the slots it shows it in.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This hangs off the body element's <see cref="VisualElement.userData"/> so
        /// <see cref="RenderBody"/> stays a static entry point a polling host can call without
        /// holding a view object, and so a re-render can update the list and the detail pane in place
        /// instead of clearing the body (issue #303).
        /// </para>
        /// <para>
        /// The row list is the identity the <see cref="ListView"/> is bound to for the life of that
        /// list, and the view state and callbacks are what the row binding reads, so a refresh never
        /// renders against a row set or reports to a host that has since been replaced.
        /// </para>
        /// </remarks>
        private sealed class LiveBodyState
        {
            internal VisualElement ListSlot { get; set; }

            internal VisualElement DetailSlot { get; set; }

            internal VisualElement FooterSlot { get; set; }

            /// <summary>The live list, or null while the log is showing an empty state.</summary>
            internal ListView List { get; set; }

            /// <summary>The list's item source, refilled in place on every render.</summary>
            internal List<MessageMonitorLiveEntry> Rows { get; } = new();

            /// <summary>
            /// Position of the pinned row, or -1 before the first row set so the first render always
            /// pushes a selection.
            /// </summary>
            internal int SelectedIndex { get; set; } = -1;

            internal MessageMonitorLiveViewState ViewState { get; set; }

            internal MessageMonitorLiveViewCallbacks Callbacks { get; set; }

            private long _detailFirstTraceId;
            private long _detailLastTraceId;
            private int _detailCount;

            /// <summary>
            /// True when the detail pane already describes <paramref name="row"/>. The dispatch range
            /// and the count are the whole of what the pane renders that can change while a row stays
            /// pinned: a coalesced row keeps its first dispatch id and grows the other two.
            /// </summary>
            internal bool DetailShows(MessageMonitorLiveEntry row)
            {
                return _detailFirstTraceId == row.FirstTraceId
                    && _detailLastTraceId == row.LastTraceId
                    && _detailCount == row.Count;
            }

            internal void RememberDetail(MessageMonitorLiveEntry row)
            {
                _detailFirstTraceId = row.FirstTraceId;
                _detailLastTraceId = row.LastTraceId;
                _detailCount = row.Count;
            }

            /// <summary>
            /// Empties the log slot. The list goes with it rather than being kept hidden, so
            /// <c>Q&lt;ListView&gt;</c> stays an honest answer to "is the log showing rows".
            /// </summary>
            internal void DropList()
            {
                ListSlot.Clear();
                List = null;
                Rows.Clear();
                SelectedIndex = -1;
            }

            internal void DropDetail()
            {
                DetailSlot.Clear();
                _detailFirstTraceId = 0;
                _detailLastTraceId = 0;
                _detailCount = 0;
            }
        }

        /// <summary>
        /// Builds the whole live view: a persistent toolbar plus a body rendered by
        /// <see cref="RenderBody"/>.
        /// </summary>
        internal static VisualElement Create(
            MessageMonitorLiveRecorder recorder,
            MessageMonitorLiveViewState viewState,
            bool diagnosticsEnabled,
            MessageMonitorLiveViewCallbacks callbacks = null
        )
        {
            if (recorder == null)
            {
                throw new ArgumentNullException(nameof(recorder));
            }

            callbacks ??= new MessageMonitorLiveViewCallbacks();

            VisualElement root = new() { name = RootName };
            DxMessagingEditorTheme.ApplyWindow(root);
            root.style.flexGrow = 1;
            root.Add(CreateToolbar(recorder, viewState, callbacks));

            VisualElement body = new() { name = BodyName };
            body.style.flexGrow = 1;
            root.Add(body);
            RenderBody(body, recorder, viewState, diagnosticsEnabled, callbacks);
            return root;
        }

        /// <summary>
        /// Fills the body container with the column header, the log list (or an empty state), the
        /// detail pane for the selected row, and the footer stats. Safe to call repeatedly, and
        /// cheap enough to call on every poll: it updates the four slots in place and never touches
        /// the toolbar.
        /// </summary>
        /// <remarks>
        /// The list and the detail pane are updated rather than replaced, because both hold a scroll
        /// position. A rebuilt <see cref="ListView"/> starts at the top, so before this a reader who
        /// had scrolled into older rows was thrown back to the newest row on every poll -- up to four
        /// times a second on a busy scene (issue #303). The header, the empty state and the footer
        /// hold nothing a reader can lose, so they are rebuilt.
        /// </remarks>
        internal static void RenderBody(
            VisualElement body,
            MessageMonitorLiveRecorder recorder,
            MessageMonitorLiveViewState viewState,
            bool diagnosticsEnabled,
            MessageMonitorLiveViewCallbacks callbacks = null
        )
        {
            if (body == null)
            {
                throw new ArgumentNullException(nameof(body));
            }
            if (recorder == null)
            {
                throw new ArgumentNullException(nameof(recorder));
            }

            LiveBodyState state = EnsureBodyState(body);
            state.ViewState = viewState;
            state.Callbacks = callbacks ?? new MessageMonitorLiveViewCallbacks();

            List<MessageMonitorLiveEntry> rows = FilterRows(recorder.Entries, viewState);
            if (rows.Count == 0)
            {
                state.DropList();
                state.ListSlot.Add(CreateEmptyState(recorder, diagnosticsEnabled));
                state.DropDetail();
            }
            else
            {
                int selectedIndex = ResolveSelectedIndex(rows, viewState.SelectedTraceId);
                UpdateList(state, rows, selectedIndex);
                UpdateDetail(state, rows[selectedIndex]);
            }

            state.FooterSlot.Clear();
            if (recorder.MissedCount > 0)
            {
                state.FooterSlot.Add(CreateGapNotice(recorder.MissedCount));
            }
            state.FooterSlot.Add(CreateFooter(recorder, rows.Count));
        }

        /// <summary>
        /// The body of the notice shown when the bus overwrote emissions before the recorder drained
        /// them. Both causes are named because the fix differs: pausing is the reader's own doing, a
        /// buffer too small for the emission rate is a setting.
        /// </summary>
        internal static string CreateGapNoticeBodyText(long missedCount)
        {
            string emissions = missedCount == 1 ? "1 emission" : $"{missedCount} emissions";
            return $"The bus overwrote {emissions} before this recorder drained them, so rows are "
                + "missing. Either recording was paused long enough for the bus buffer to wrap, or "
                + "that buffer is too small for the emission rate.";
        }

        /// <summary>
        /// Rows matching both the taxonomy chips and the free-text filter, newest first. The text
        /// filter is the snapshot Monitor's own typed query, so <c>type:</c>, <c>message:</c>,
        /// <c>context:</c> and <c>stack:</c> behave identically in both modes.
        /// </summary>
        internal static List<MessageMonitorLiveEntry> FilterRows(
            IReadOnlyList<MessageMonitorLiveEntry> entries,
            MessageMonitorLiveViewState viewState
        )
        {
            if (entries == null)
            {
                throw new ArgumentNullException(nameof(entries));
            }

            List<MessageMonitorLiveEntry> rows = new(entries.Count);
            for (int index = entries.Count - 1; index >= 0; index--)
            {
                MessageMonitorLiveEntry row = entries[index];
                if (
                    viewState.ShowsRouteKind(row.Entry.RouteKind)
                    && row.Entry.Matches(viewState.FilterText)
                )
                {
                    rows.Add(row);
                }
            }

            return rows;
        }

        /// <summary>
        /// Position of the pinned row in the current filtered list, or 0 (the newest row) when
        /// nothing is pinned or the pinned row has been filtered out or aged out of the log.
        /// </summary>
        internal static int ResolveSelectedIndex(
            IReadOnlyList<MessageMonitorLiveEntry> rows,
            long selectedTraceId
        )
        {
            if (rows == null)
            {
                throw new ArgumentNullException(nameof(rows));
            }

            if (selectedTraceId == MessageMonitorLiveViewState.FollowNewest || rows.Count == 0)
            {
                return 0;
            }

            for (int index = 0; index < rows.Count; index++)
            {
                if (rows[index].FirstTraceId == selectedTraceId)
                {
                    return index;
                }
            }

            return 0;
        }

        /// <summary>
        /// Formats a recorder observation time as <c>m:ss.fff</c> since the recorder started, short
        /// enough for the fixed-width time column and precise enough to read a burst.
        /// </summary>
        internal static string FormatObservedSeconds(double observedSeconds)
        {
            double clamped = observedSeconds < 0 ? 0 : observedSeconds;
            int minutes = (int)(clamped / 60);
            double seconds = clamped - (minutes * 60);
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00.000}", minutes, seconds);
        }

        /// <summary>
        /// The count column text. A row that stands for a single emission shows nothing, so the
        /// column only draws attention when emissions actually coalesced.
        /// </summary>
        internal static string FormatCount(int count)
        {
            return count <= 1 ? string.Empty : count.ToString(CultureInfo.InvariantCulture);
        }

        internal static string CreateTraceRangeText(MessageMonitorLiveEntry row)
        {
            string first = row.FirstTraceId.ToString(CultureInfo.InvariantCulture);
            return row.FirstTraceId == row.LastTraceId
                ? $"#{first}"
                : $"#{first}-#{row.LastTraceId.ToString(CultureInfo.InvariantCulture)}";
        }

        internal static string CreateEmptyTitleText(
            MessageMonitorLiveRecorder recorder,
            bool diagnosticsEnabled
        )
        {
            if (recorder == null)
            {
                throw new ArgumentNullException(nameof(recorder));
            }

            if (!diagnosticsEnabled)
            {
                return "Diagnostics are Off";
            }

            if (recorder.Entries.Count > 0)
            {
                return "No matches";
            }

            return recorder.Recording ? "Waiting for messages" : "Recording is paused";
        }

        internal static string CreateEmptyBodyText(
            MessageMonitorLiveRecorder recorder,
            bool diagnosticsEnabled
        )
        {
            if (recorder == null)
            {
                throw new ArgumentNullException(nameof(recorder));
            }

            if (!diagnosticsEnabled)
            {
                return "The live log drains the bus emission buffer, which only fills while diagnostics are on.";
            }

            if (recorder.Entries.Count > 0)
            {
                return "No recorded messages match the current filter.";
            }

            return recorder.Recording
                ? "Emit a message and it appears here within a poll."
                : "Recording is paused, so nothing is being drained. The bus keeps buffering meanwhile.";
        }

        /// <summary>
        /// The footer stats in display order: how many rows the current filter shows, how much of
        /// the retained window is used, how many emissions have been drained, and how many the bus
        /// overwrote before they could be.
        /// </summary>
        internal static IReadOnlyList<MessageMonitorLiveFooterStat> CreateFooterStats(
            MessageMonitorLiveRecorder recorder,
            int shownCount
        )
        {
            if (recorder == null)
            {
                throw new ArgumentNullException(nameof(recorder));
            }

            return new[]
            {
                new MessageMonitorLiveFooterStat(
                    shownCount.ToString(CultureInfo.InvariantCulture),
                    "shown"
                ),
                new MessageMonitorLiveFooterStat(
                    $"{recorder.Entries.Count.ToString(CultureInfo.InvariantCulture)}/{recorder.Capacity.ToString(CultureInfo.InvariantCulture)}",
                    "buffered"
                ),
                new MessageMonitorLiveFooterStat(
                    recorder.ObservedCount.ToString(CultureInfo.InvariantCulture),
                    "recorded"
                ),
                new MessageMonitorLiveFooterStat(
                    recorder.MissedCount.ToString(CultureInfo.InvariantCulture),
                    "missed"
                ),
            };
        }

        /// <summary>
        /// Builds one log row. Exposed so tests exercise the same factory the list binds, without
        /// depending on when the virtualized list realizes a row.
        /// </summary>
        internal static VisualElement CreateRow(
            MessageMonitorLiveEntry row,
            int rowIndex,
            bool selected
        )
        {
            VisualElement element = new();
            element.AddToClassList(RowClassName);
            element.AddToClassList(DxMessagingEditorTheme.RowClassName);
            if (rowIndex % 2 == 1)
            {
                element.AddToClassList(DxMessagingEditorTheme.RowAlternateClassName);
            }
            if (selected)
            {
                element.style.backgroundColor = DxMessagingEditorPalette.SelectedWash;
            }

            Label time = new(FormatObservedSeconds(row.FirstObservedSeconds))
            {
                name = RowTimeLabelName,
            };
            time.AddToClassList(DxMessagingEditorTheme.RowTimeClassName);
            element.Add(time);

            string routeKind = DxMessagingEditorPalette.NormalizeRouteKind(row.Entry.RouteKind);
            string routeKindText = string.IsNullOrEmpty(routeKind) ? "Other" : routeKind;
            VisualElement route = new();
            route.AddToClassList(DxMessagingEditorTheme.RowTypeClassName);
            VisualElement dot = new();
            DxMessagingEditorTheme.AddRouteKindDotClasses(dot, routeKind);
            route.Add(dot);
            route.Add(new Label(routeKindText) { name = RowRouteLabelName });
            element.Add(route);

            Label message = new(row.Entry.MessageTypeName) { name = RowMessageLabelName };
            message.AddToClassList(DxMessagingEditorTheme.RowMessageClassName);
            element.Add(message);

            Label context = new(row.Entry.ContextText) { name = RowContextLabelName };
            context.AddToClassList(DxMessagingEditorTheme.RowRouteClassName);
            element.Add(context);

            Label count = new(FormatCount(row.Count)) { name = RowCountLabelName };
            count.AddToClassList(DxMessagingEditorTheme.RowCountClassName);
            element.Add(count);

            return element;
        }

        private static VisualElement CreateToolbar(
            MessageMonitorLiveRecorder recorder,
            MessageMonitorLiveViewState viewState,
            MessageMonitorLiveViewCallbacks callbacks
        )
        {
            VisualElement toolbar = new() { name = ToolbarName };
            toolbar.AddToClassList(DxMessagingEditorTheme.ToolbarClassName);
            toolbar.style.flexDirection = FlexDirection.Row;
            toolbar.style.alignItems = Align.Center;
            toolbar.style.flexWrap = Wrap.Wrap;

            Label mode = new(LiveModeBadgeText)
            {
                name = ModeBadgeLabelName,
                tooltip = LiveModeHintText,
            };
            mode.AddToClassList(DxMessagingEditorTheme.TypeBadgeClassName);
            mode.AddToClassList(DxMessagingEditorTheme.TypeBadgeGlobalObserverClassName);
            mode.style.marginRight = 6;
            toolbar.Add(mode);

            Toggle record = new("Record") { name = RecordToggleName, value = recorder.Recording };
            record.AddToClassList(DxMessagingEditorTheme.RecordClassName);
            record.tooltip =
                "Drain new emissions into the log. The bus keeps buffering while this is off.";
            record.RegisterValueChangedCallback(changed =>
                callbacks.OnRecordingChanged?.Invoke(changed.newValue)
            );
            toolbar.Add(record);

            // The toolbar runs four unrelated groups together -- what is being recorded, what is
            // shown, what is searched, and what the window does next. The design system's rule
            // separates them so the row reads as groups rather than one run of controls.
            toolbar.Add(CreateSeparator());

            // The chips name their route kind rather than abbreviating it to a letter: issue #344
            // reported the toolbar toggles as unlabelled, and a named chip is also the only legend
            // the row colors have.
            Toggle untargeted = CreateChip(
                UntargetedChipName,
                DxMessagingEditorPalette.UntargetedKind,
                viewState.ShowUntargeted
            );
            Toggle targeted = CreateChip(
                TargetedChipName,
                DxMessagingEditorPalette.TargetedKind,
                viewState.ShowTargeted
            );
            Toggle broadcast = CreateChip(
                BroadcastChipName,
                DxMessagingEditorPalette.BroadcastKind,
                viewState.ShowBroadcast
            );
            TextField filter = new() { name = FilterFieldName, value = viewState.FilterText };
            filter.AddToClassList(DxMessagingEditorTheme.SearchClassName);
            filter.tooltip =
                "Filter the log. Supports type:, message:, context: and stack: prefixes.";

            // Every control reads the live value of every other control, so a change to one never
            // writes back a stale copy of the others. Changing a filter also drops the selection
            // back to the newest row, because the old index pointed into a different row set.
            void RaiseStateChanged()
            {
                callbacks.OnStateChanged?.Invoke(
                    new MessageMonitorLiveViewState(
                        filter.value,
                        untargeted.value,
                        targeted.value,
                        broadcast.value
                    )
                );
            }

            untargeted.RegisterValueChangedCallback(_ => RaiseStateChanged());
            targeted.RegisterValueChangedCallback(_ => RaiseStateChanged());
            broadcast.RegisterValueChangedCallback(_ => RaiseStateChanged());
            filter.RegisterValueChangedCallback(_ => RaiseStateChanged());

            toolbar.Add(untargeted);
            toolbar.Add(targeted);
            toolbar.Add(broadcast);
            toolbar.Add(CreateSeparator());
            toolbar.Add(filter);
            toolbar.Add(CreateSeparator());

            // Buttons are wired through ClickEvent rather than the Button(Action) constructor, the
            // same as the rest of this package's editor UI: it is the event a real click produces
            // and the one a test can synthesize.
            Button clear = new()
            {
                name = ClearButtonName,
                text = "Clear",
                tooltip = "Empty the log and reset its statistics.",
            };
            clear.RegisterCallback<ClickEvent>(_ => callbacks.OnClear?.Invoke());
            clear.AddToClassList(DxMessagingEditorTheme.ButtonGhostClassName);
            toolbar.Add(clear);

            Button snapshot = new()
            {
                name = SnapshotButtonName,
                text = "Snapshot",
                tooltip = "Switch back to the snapshot Monitor.",
            };
            snapshot.RegisterCallback<ClickEvent>(_ => callbacks.OnExitLiveMode?.Invoke());
            snapshot.AddToClassList(DxMessagingEditorTheme.ToolButtonClassName);
            toolbar.Add(snapshot);

            return toolbar;
        }

        private static Toggle CreateChip(string name, string routeKind, bool value)
        {
            Toggle chip = new()
            {
                name = name,
                text = routeKind,
                value = value,
            };
            DxMessagingEditorTheme.AddRouteKindChipClasses(chip, routeKind);
            chip.AddToClassList(DxMessagingEditorTheme.ChipWideClassName);
            chip.AddToClassList(DxMessagingEditorTheme.FilterClassName);
            chip.tooltip =
                $"This color marks {routeKind} messages in every row. Click to show or hide them.";

            // `.dx-record` hides its checkmark from the stylesheet; `.dx-chip` carries no such rule
            // in the migrated sheet, and the chip's own letter is its state, so the checkmark and
            // the empty field-label column are collapsed here instead.
            VisualElement checkmark = chip.Q(className: "unity-toggle__checkmark");
            if (checkmark != null)
            {
                checkmark.style.display = DisplayStyle.None;
            }

            VisualElement label = chip.Q(className: "unity-base-field__label");
            if (label != null)
            {
                label.style.minWidth = ChipLabelWidth;
                label.style.width = ChipLabelWidth;
            }

            return chip;
        }

        private static VisualElement CreateListHeader()
        {
            VisualElement header = new() { name = ListHeaderName };
            header.AddToClassList(DxMessagingEditorTheme.ListHeaderClassName);
            header.Add(CreateHeaderColumn("TIME", DxMessagingEditorTheme.ColumnTimeClassName));
            header.Add(CreateHeaderColumn("ROUTE", DxMessagingEditorTheme.ColumnTypeClassName));
            header.Add(
                CreateHeaderColumn("MESSAGE", DxMessagingEditorTheme.ColumnMessageClassName)
            );
            header.Add(CreateHeaderColumn("CONTEXT", DxMessagingEditorTheme.ColumnRouteClassName));
            header.Add(CreateHeaderColumn("N", DxMessagingEditorTheme.ColumnCountClassName));
            return header;
        }

        private static Label CreateHeaderColumn(string text, string columnClassName)
        {
            Label column = new(text);
            column.AddToClassList(columnClassName);
            return column;
        }

        /// <summary>
        /// Points the list at the current row set without replacing it, so the reader keeps their
        /// place in the log. The list is built on the first non-empty render and after the log has
        /// been empty, where there is no scroll position left to preserve anyway.
        /// </summary>
        private static void UpdateList(
            LiveBodyState state,
            List<MessageMonitorLiveEntry> rows,
            int selectedIndex
        )
        {
            bool selectionChanged = state.SelectedIndex != selectedIndex;
            state.SelectedIndex = selectedIndex;

            // Refilling the list the view was built around, rather than assigning a new one, keeps
            // this a data refresh: reassigning `itemsSource` is a collection reset.
            state.Rows.Clear();
            state.Rows.AddRange(rows);

            if (state.List == null)
            {
                state.ListSlot.Clear();
                state.List = CreateList(state);
                state.ListSlot.Add(state.List);
                selectionChanged = true;
            }
            else
            {
                state.List.RefreshItems();
            }

            // Only when it moved: the selection is also what the row factory reads to draw the
            // selected wash, so an unchanged pin needs no work and no allocation per poll.
            if (selectionChanged)
            {
                state.List.SetSelectionWithoutNotify(new[] { selectedIndex });
            }
        }

        private static ListView CreateList(LiveBodyState state)
        {
            ListView list = new()
            {
                name = ListName,
                itemsSource = state.Rows,
                fixedItemHeight = RowHeight,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
                selectionType = SelectionType.Single,
                makeItem = static () => new VisualElement(),
            };

            // Selection is raised from the row's own click rather than the list's selection event:
            // the event was renamed across the supported editor range (onSelectionChange ->
            // selectionChanged), and the row click carries the index directly.
            //
            // Everything the binding needs is read off the shared state at bind time rather than
            // captured here, so a refreshed list renders the current rows and reports clicks to the
            // host's current callbacks instead of the ones it was first built with.
            list.bindItem = (element, index) =>
            {
                element.Clear();
                MessageMonitorLiveEntry entry = state.Rows[index];
                VisualElement row = CreateRow(entry, index, index == state.SelectedIndex);
                row.RegisterCallback<ClickEvent>(_ =>
                    state.Callbacks.OnStateChanged?.Invoke(
                        state.ViewState.WithSelectedTraceId(entry.FirstTraceId)
                    )
                );
                element.Add(row);
            };
            list.style.flexGrow = 1;
            return list;
        }

        /// <summary>
        /// Rebuilds the detail pane only when it would show something different. The pane carries a
        /// scrollable stack trace, so rebuilding it while the selected row is unchanged would scroll
        /// a reader back to the top of that trace on every poll.
        /// </summary>
        private static void UpdateDetail(LiveBodyState state, MessageMonitorLiveEntry row)
        {
            if (state.DetailSlot.childCount > 0 && state.DetailShows(row))
            {
                return;
            }

            state.DetailSlot.Clear();
            state.DetailSlot.Add(CreateDetail(row));
            state.RememberDetail(row);
        }

        /// <summary>
        /// Builds the body's four slots on first render and hands back the state they are tracked
        /// in. A body that already carries state keeps every element it has.
        /// </summary>
        private static LiveBodyState EnsureBodyState(VisualElement body)
        {
            if (body.userData is LiveBodyState existing)
            {
                return existing;
            }

            body.Clear();
            LiveBodyState state = new();
            body.Add(CreateListHeader());

            state.ListSlot = new VisualElement { name = ListSlotName };
            state.ListSlot.style.flexGrow = 1;
            body.Add(state.ListSlot);

            state.DetailSlot = new VisualElement { name = DetailSlotName };
            body.Add(state.DetailSlot);

            state.FooterSlot = new VisualElement { name = FooterSlotName };
            body.Add(state.FooterSlot);

            body.userData = state;
            return state;
        }

        private static VisualElement CreateEmptyState(
            MessageMonitorLiveRecorder recorder,
            bool diagnosticsEnabled
        )
        {
            VisualElement empty = DxMessagingEditorTheme.CreateEmptyState(
                CreateEmptyTitleText(recorder, diagnosticsEnabled),
                CreateEmptyBodyText(recorder, diagnosticsEnabled),
                bodyName: EmptyBodyName,
                titleName: EmptyTitleName
            );
            empty.style.flexGrow = 1;
            return empty;
        }

        private static VisualElement CreateDetail(MessageMonitorLiveEntry row)
        {
            VisualElement detail = new() { name = DetailName };
            detail.AddToClassList(DxMessagingEditorTheme.DetailClassName);
            DxMessagingEditorTheme.ApplyCompleteBorder(
                detail,
                DxMessagingEditorPalette.BorderPanel
            );

            VisualElement head = new();
            head.AddToClassList(DxMessagingEditorTheme.DetailHeadClassName);
            string routeKind = DxMessagingEditorPalette.NormalizeRouteKind(row.Entry.RouteKind);
            Label badge = new(string.IsNullOrEmpty(routeKind) ? "Other" : routeKind);
            DxMessagingEditorTheme.AddRouteKindTypeBadgeClasses(badge, routeKind);
            head.Add(badge);
            Label title = new(row.Entry.MessageTypeName)
            {
                name = DetailTitleLabelName,
                tooltip = row.Entry.MessageTypeDisplayPath,
            };
            title.AddToClassList(DxMessagingEditorTheme.DetailTitleClassName);
            head.Add(title);
            Label frame = new(CreateTraceRangeText(row)) { name = DetailFrameLabelName };
            frame.AddToClassList(DxMessagingEditorTheme.DetailFrameClassName);
            frame.style.flexGrow = 1;
            frame.style.unityTextAlign = TextAnchor.MiddleRight;
            head.Add(frame);
            detail.Add(head);

            VisualElement card = new();
            card.AddToClassList(DxMessagingEditorTheme.CardClassName);
            Label cardLabel = new("EMISSION");
            cardLabel.AddToClassList(DxMessagingEditorTheme.CardLabelClassName);
            card.Add(cardLabel);
            card.Add(CreateKeyValue("Type", row.Entry.MessageTypeDisplayPath));
            card.Add(CreateKeyValue("Context", row.Entry.ContextText));
            card.Add(CreateKeyValue("Count", row.Count.ToString(CultureInfo.InvariantCulture)));
            card.Add(CreateKeyValue("Dispatch", CreateTraceRangeText(row)));
            card.Add(CreateKeyValue("Observed", CreateObservedRangeText(row)));
            detail.Add(card);

            bool captured = !string.IsNullOrWhiteSpace(row.Entry.StackTrace);
            Foldout stackFoldout = new()
            {
                name = DetailStackFoldoutName,
                text = captured ? "Stack trace" : "Stack trace (not captured)",
                value = false,
                tooltip =
                    "The call stack the emission was recorded from. Collapsed by default so the log stays readable.",
            };
            ScrollView stackScroll = new(ScrollViewMode.Vertical);
            stackScroll.style.maxHeight = DetailStackTraceMaxHeight;
            Label stack = new(captured ? row.Entry.StackTrace : "Stack trace: not captured")
            {
                name = DetailStackLabelName,
            };
            stack.style.whiteSpace = WhiteSpace.Normal;
            stackScroll.Add(stack);
            stackFoldout.Add(stackScroll);
            detail.Add(stackFoldout);

            return detail;
        }

        private static string CreateObservedRangeText(MessageMonitorLiveEntry row)
        {
            string first = FormatObservedSeconds(row.FirstObservedSeconds);
            // ReSharper disable once CompareOfFloatsByEqualityOperator -- both readings come from
            // the same clock, and a coalesced row copies the earlier reading verbatim.
            return row.FirstObservedSeconds == row.LastObservedSeconds
                ? first
                : $"{first} - {FormatObservedSeconds(row.LastObservedSeconds)}";
        }

        private static VisualElement CreateKeyValue(string key, string value)
        {
            VisualElement pair = new();
            pair.AddToClassList(DxMessagingEditorTheme.KeyValueClassName);
            Label keyLabel = new(key);
            keyLabel.AddToClassList(DxMessagingEditorTheme.KeyValueKeyClassName);
            pair.Add(keyLabel);
            Label valueLabel = new(value);
            valueLabel.AddToClassList(DxMessagingEditorTheme.KeyValueValueClassName);
            pair.Add(valueLabel);
            return pair;
        }

        /// <summary>
        /// The danger-severity notice for a lossy log. A hole in the log is not a caution: every
        /// count and every gap the reader is looking at is wrong until they know about it, which is
        /// why this reads louder than the warning admonitions elsewhere in the package.
        /// </summary>
        private static VisualElement CreateGapNotice(long missedCount)
        {
            VisualElement notice = new() { name = GapNoticeName };
            notice.AddToClassList(DxMessagingEditorTheme.AdmonitionClassName);
            notice.AddToClassList(DxMessagingEditorTheme.DangerClassName);
            DxMessagingEditorTheme.ApplyCompleteBorder(notice, DxMessagingEditorPalette.Targeted);

            Label title = new("The log has gaps") { name = GapNoticeTitleName };
            title.AddToClassList(DxMessagingEditorTheme.AdmonitionTitleClassName);
            notice.Add(title);

            // Deliberately carries no class, matching the package's other admonitions:
            // `.dx-admonition` owns the padding and the body is plain copy. `.dx-empty__body` would
            // have been the convenient class to reach for and is the wrong one -- it caps width at
            // 260px and centers, which is right for an empty state and wrong for a full-width notice.
            Label body = new(CreateGapNoticeBodyText(missedCount)) { name = GapNoticeBodyName };
            body.style.whiteSpace = WhiteSpace.Normal;
            notice.Add(body);

            return notice;
        }

        private static VisualElement CreateSeparator()
        {
            VisualElement separator = new();
            separator.AddToClassList(DxMessagingEditorTheme.SeparatorClassName);
            return separator;
        }

        private static VisualElement CreateFooter(
            MessageMonitorLiveRecorder recorder,
            int shownCount
        )
        {
            VisualElement footer = new() { name = FooterName };
            footer.AddToClassList(DxMessagingEditorTheme.FooterClassName);
            foreach (MessageMonitorLiveFooterStat stat in CreateFooterStats(recorder, shownCount))
            {
                VisualElement statElement = new();
                statElement.AddToClassList(DxMessagingEditorTheme.FooterStatClassName);
                Label number = new(stat.Number);
                number.AddToClassList(DxMessagingEditorTheme.FooterNumberClassName);
                statElement.Add(number);
                statElement.Add(new Label(stat.Label));
                footer.Add(statElement);
            }

            // The stats say how many rows there are; this says what a row is. Without it the count
            // column reads as a mystery on a log that silently merges repeats.
            Label hint = new(LiveModeHintText) { name = ModeHintLabelName };
            hint.style.flexGrow = 1;
            hint.style.flexShrink = 1;
            hint.style.unityTextAlign = TextAnchor.MiddleRight;
            footer.Add(hint);

            return footer;
        }
    }
}
#endif
