namespace DxMessaging.Editor.CustomEditors
{
#if UNITY_EDITOR
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.Diagnostics;
    using DxMessaging.Editor;
    using DxMessaging.Unity;
    using UnityEngine.UIElements;

    /// <summary>
    /// Builds the themed "Message subscriptions" section shown under a
    /// <see cref="MessageAwareComponent"/> inspector body.
    /// </summary>
    /// <remarks>
    /// The section renders the component's live <see cref="MessageRegistrationToken"/> registrations
    /// through the design system's <c>.dx-inspector</c> / <c>.dx-sub*</c> classes. It reads only
    /// registration metadata the token already keeps, so it costs nothing at runtime and nothing in
    /// the editor until an inspector is actually showing.
    /// </remarks>
    internal static class MessageAwareComponentSubscriptionsView
    {
        internal const string RootName = "dxmessaging-inspector-subscriptions";
        internal const string TitleLabelName = "dxmessaging-inspector-subscriptions-title";
        internal const string MetaLabelName = "dxmessaging-inspector-subscriptions-meta";
        internal const string RowsName = "dxmessaging-inspector-subscriptions-rows";
        internal const string EmptyBodyName = "dxmessaging-inspector-subscriptions-empty-body";

        internal const string RootClassName = "dx-inspector";
        internal const string HeadClassName = "dx-inspector__head";
        internal const string TitleClassName = "dx-inspector__title";
        internal const string MetaClassName = "dx-inspector__meta";
        internal const string RowClassName = "dx-sub";
        internal const string RowNameClassName = "dx-sub__name";
        internal const string RowMetaClassName = "dx-sub__meta";
        internal const string RowLiveClassName = "dx-sub__live";
        internal const string RowIdleClassName = "dx-sub__idle";

        internal const string Title = "Message subscriptions";

        internal static VisualElement Create(MessageAwareComponentSubscriptionsState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            VisualElement root = new() { name = RootName };
            DxMessagingEditorTheme.Apply(root);
            root.AddToClassList(RootClassName);
            DxMessagingEditorTheme.ApplyCompleteBorder(root, DxMessagingEditorPalette.BorderPanel);

            VisualElement head = new();
            head.AddToClassList(HeadClassName);
            Label title = new(Title) { name = TitleLabelName };
            title.AddToClassList(TitleClassName);
            head.Add(title);
            Label meta = new(CreateSummaryText(state)) { name = MetaLabelName };
            meta.AddToClassList(MetaClassName);
            meta.style.flexGrow = 1;
            meta.style.unityTextAlign = UnityEngine.TextAnchor.MiddleRight;
            head.Add(meta);
            root.Add(head);

            VisualElement rows = new() { name = RowsName };
            root.Add(rows);

            if (state.Rows.Count == 0)
            {
                rows.Add(
                    DxMessagingEditorTheme.CreateEmptyState(
                        title: null,
                        body: CreateEmptyBodyText(state),
                        bodyName: EmptyBodyName
                    )
                );
                return root;
            }

            foreach (MessageAwareComponentSubscriptionRow row in state.Rows)
            {
                rows.Add(CreateRow(row));
            }

            return root;
        }

        internal static string CreateSummaryText(MessageAwareComponentSubscriptionsState state)
        {
            if (!state.HasToken)
            {
                return "No token";
            }

            string count =
                state.Rows.Count == 1 ? "1 registration" : $"{state.Rows.Count} registrations";
            return state.TokenEnabled ? $"Listening | {count}" : $"Disabled | {count}";
        }

        internal static string CreateEmptyBodyText(MessageAwareComponentSubscriptionsState state)
        {
            return state.HasToken
                ? "This component has a registration token but has registered no handlers."
                : "Registrations are created in Awake, so they appear once the component is running in Play mode.";
        }

        internal static string CreateRowMetaText(MessageAwareComponentSubscriptionRow row)
        {
            string calls =
                row.CallCount == MessageAwareComponentSubscriptionRow.UnknownCallCount ? "calls n/a"
                : row.CallCount == 1 ? "1 call"
                : $"{row.CallCount} calls";
            return $"{row.RegistrationTypeName} | priority {row.Priority} | {calls}";
        }

        private static VisualElement CreateRow(MessageAwareComponentSubscriptionRow row)
        {
            VisualElement element = new();
            element.AddToClassList(RowClassName);

            Label name = new(row.MessageTypeName);
            name.AddToClassList(RowNameClassName);
            element.Add(name);

            Label meta = new(CreateRowMetaText(row));
            meta.AddToClassList(RowMetaClassName);
            element.Add(meta);

            VisualElement dot = new();
            dot.AddToClassList(row.IsLive ? RowLiveClassName : RowIdleClassName);
            element.Add(dot);

            return element;
        }
    }

    /// <summary>
    /// One registration row rendered by <see cref="MessageAwareComponentSubscriptionsView"/>.
    /// </summary>
    internal readonly struct MessageAwareComponentSubscriptionRow
    {
        /// <summary>
        /// <see cref="CallCount"/> when diagnostics are not recording. Distinct from zero, which
        /// asserts that the handler genuinely never ran.
        /// </summary>
        internal const int UnknownCallCount = -1;

        internal MessageAwareComponentSubscriptionRow(
            string messageTypeName,
            string registrationTypeName,
            int priority,
            int callCount,
            bool isLive
        )
        {
            MessageTypeName = messageTypeName;
            RegistrationTypeName = registrationTypeName;
            Priority = priority;
            CallCount = callCount;
            IsLive = isLive;
        }

        internal string MessageTypeName { get; }

        /// <summary>
        /// The exact <see cref="MessageRegistrationType"/> name, which is finer-grained than the
        /// Untargeted/Targeted/Broadcast route kind: it distinguishes handlers from post-processors
        /// and interceptors, which is the distinction a reader of this section needs.
        /// </summary>
        internal string RegistrationTypeName { get; }

        internal int Priority { get; }

        /// <summary>
        /// Observed invocations, or <see cref="UnknownCallCount"/> when diagnostics are not
        /// recording them.
        /// </summary>
        internal int CallCount { get; }

        /// <summary>True while the registration is subscribed on the bus.</summary>
        internal bool IsLive { get; }
    }

    /// <summary>
    /// Snapshot of a component's registrations, captured without touching any GUI API so the
    /// decision and formatting logic stays testable without an editor panel.
    /// </summary>
    internal sealed class MessageAwareComponentSubscriptionsState
    {
        private static readonly MessageAwareComponentSubscriptionRow[] NoRows =
            Array.Empty<MessageAwareComponentSubscriptionRow>();

        internal static readonly MessageAwareComponentSubscriptionsState None = new(
            hasToken: false,
            tokenEnabled: false,
            diagnosticsEnabled: false,
            rows: NoRows
        );

        internal MessageAwareComponentSubscriptionsState(
            bool hasToken,
            bool tokenEnabled,
            bool diagnosticsEnabled,
            IReadOnlyList<MessageAwareComponentSubscriptionRow> rows
        )
        {
            HasToken = hasToken;
            TokenEnabled = tokenEnabled;
            DiagnosticsEnabled = diagnosticsEnabled;
            Rows = rows ?? throw new ArgumentNullException(nameof(rows));
        }

        internal bool HasToken { get; }

        internal bool TokenEnabled { get; }

        internal bool DiagnosticsEnabled { get; }

        internal IReadOnlyList<MessageAwareComponentSubscriptionRow> Rows { get; }

        /// <summary>
        /// Cheap change signal, so a polling inspector only rebuilds the row list when something
        /// it renders actually moved.
        /// </summary>
        /// <remarks>
        /// Every rendered field folds in, including each row's identity. Folding only the row count
        /// and the call counts would miss a registration swapped for a different one: with
        /// diagnostics off every count is <see cref="MessageAwareComponentSubscriptionRow.UnknownCallCount"/>,
        /// so a same-size replacement would leave the poller showing stale rows.
        /// </remarks>
        internal long Revision
        {
            get
            {
                long revision = HasToken ? 1 : 0;
                revision = (revision * 31) + (TokenEnabled ? 1 : 0);
                revision = (revision * 31) + Rows.Count;
                foreach (MessageAwareComponentSubscriptionRow row in Rows)
                {
                    revision = (revision * 31) + row.CallCount;
                    revision = (revision * 31) + row.Priority;
                    revision = (revision * 31) + (row.IsLive ? 1 : 0);
                    revision =
                        (revision * 31) + StringComparer.Ordinal.GetHashCode(row.MessageTypeName);
                    revision =
                        (revision * 31)
                        + StringComparer.Ordinal.GetHashCode(row.RegistrationTypeName);
                }
                return revision;
            }
        }

        internal static MessageAwareComponentSubscriptionsState Capture(
            MessageAwareComponent component
        )
        {
            MessageRegistrationToken token = component == null ? null : component.Token;
            if (token == null)
            {
                return None;
            }

            // Reading _callCounts materializes the lazy diagnostics dictionary, so only touch it
            // when the token is actually recording. With diagnostics off the count is unknown
            // rather than zero, and the row says so instead of implying nothing ever fired.
            bool diagnosticsEnabled = token.DiagnosticMode;
            int ResolveCallCount(MessageRegistrationHandle handle)
            {
                if (!diagnosticsEnabled)
                {
                    return UnknownCallCount;
                }

                return token._callCounts.TryGetValue(handle, out int callCount) ? callCount : 0;
            }

            List<MessageAwareComponentSubscriptionRow> rows = new();
            foreach (
                KeyValuePair<
                    MessageRegistrationHandle,
                    MessageRegistrationMetadata
                > entry in token._metadata
            )
            {
                MessageRegistrationMetadata metadata = entry.Value;
                rows.Add(
                    new MessageAwareComponentSubscriptionRow(
                        metadata.type == null ? "<unknown>" : metadata.type.Name,
                        metadata.registrationType.ToString(),
                        metadata.priority,
                        ResolveCallCount(entry.Key),
                        token.Enabled
                    )
                );
            }

            rows.Sort(CompareRows);
            return new MessageAwareComponentSubscriptionsState(
                hasToken: true,
                tokenEnabled: token.Enabled,
                diagnosticsEnabled: diagnosticsEnabled,
                rows: rows
            );
        }

        private static int CompareRows(
            MessageAwareComponentSubscriptionRow left,
            MessageAwareComponentSubscriptionRow right
        )
        {
            int comparison = string.CompareOrdinal(left.MessageTypeName, right.MessageTypeName);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = string.CompareOrdinal(
                left.RegistrationTypeName,
                right.RegistrationTypeName
            );
            return comparison != 0 ? comparison : left.Priority.CompareTo(right.Priority);
        }
    }
#endif
}
