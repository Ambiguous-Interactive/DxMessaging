namespace DxMessaging.Editor.CustomEditors
{
#if UNITY_EDITOR
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Runtime.CompilerServices;
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
        internal const string RowPriorityLabelName = "dxmessaging-inspector-subscriptions-priority";
        internal const string RowStatusName = "dxmessaging-inspector-subscriptions-status";

        internal const string RootClassName = "dx-inspector";
        internal const string HeadClassName = "dx-inspector__head";
        internal const string TitleClassName = "dx-inspector__title";
        internal const string MetaClassName = "dx-inspector__meta";
        internal const string RowClassName = "dx-sub";
        internal const string RowNameClassName = "dx-sub__name";
        internal const string RowMetaClassName = "dx-sub__meta";
        internal const string RowLiveClassName = "dx-sub__live";
        internal const string RowIdleClassName = "dx-sub__idle";
        internal const string RowMixedClassName = "dx-sub__mixed";
        internal const string PriorityClassName = "dx-prio";

        internal const string Title = "Message subscriptions";

        /// <summary>
        /// Mirrors the horizontal inset of <c>.dx-inspector__head</c> in
        /// <c>Editor/Theme/DxMessagingTheme.uss</c>. Kept here rather than added to <c>.dx-sub</c>
        /// so the migrated stylesheet stays identical to the design-system spec.
        /// </summary>
        internal const int HeadHorizontalPadding = 11;

        internal const int BodyVerticalPadding = 5;

        /// <summary>
        /// Gap between the priority badge and the meta text that follows it. <c>.dx-prio</c> carries
        /// its own padding but no outer margin, so without this the badge and the registration type
        /// touch.
        /// </summary>
        internal const int PriorityBadgeMarginRight = 6;

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

            // `.dx-sub` pads vertically only, and `.dx-inspector` clips to a rounded corner, so the
            // body needs its own horizontal inset: without it a row name sits flush against the
            // border and the trailing status dot clips on the corner radius. Matches the
            // `.dx-inspector__head` inset so the head and the rows line up.
            VisualElement rows = new() { name = RowsName };
            rows.style.paddingLeft = HeadHorizontalPadding;
            rows.style.paddingRight = HeadHorizontalPadding;
            rows.style.paddingTop = BodyVerticalPadding;
            rows.style.paddingBottom = BodyVerticalPadding;
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
            if (state.IsAggregate)
            {
                if (state.TokenCount == 0)
                {
                    return $"{state.SelectionCount} selected | No tokens";
                }

                string patterns =
                    state.Rows.Count == 1 ? "1 pattern" : $"{state.Rows.Count} patterns";
                if (state.TokenCount < state.SelectionCount)
                {
                    string tokens =
                        state.TokenCount == 1 ? "1 token" : $"{state.TokenCount} tokens";
                    return $"{state.SelectionCount} selected | {tokens} | {patterns}";
                }
                return $"{state.SelectionCount} selected | {patterns}";
            }

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
            if (state.IsAggregate)
            {
                if (state.TokenCount == 0)
                {
                    return $"The {state.SelectionCount} selected components do not have registration tokens yet.";
                }
                if (state.TokenCount < state.SelectionCount)
                {
                    string tokens =
                        state.TokenCount == 1
                            ? "1 selected component has a registration token"
                            : $"{state.TokenCount} selected components have registration tokens";
                    int missingTokenCount = state.SelectionCount - state.TokenCount;
                    string missing =
                        missingTokenCount == 1
                            ? "the other selected component does not"
                            : $"the other {missingTokenCount} do not";
                    return $"{tokens}; {missing}, and no registered handlers were found.";
                }

                return $"The {state.SelectionCount} selected components have registration tokens but no registered handlers.";
            }

            return state.HasToken
                ? "This component has a registration token but has registered no handlers."
                : "Registrations are created in Awake, so they appear once the component is running in Play mode.";
        }

        internal static string CreateRowMetaText(MessageAwareComponentSubscriptionRow row)
        {
            if (row.IsAggregate)
            {
                string status = row.Liveness switch
                {
                    MessageAwareComponentSubscriptionLiveness.Live => "enabled",
                    MessageAwareComponentSubscriptionLiveness.Mixed => "mixed",
                    _ => "disabled",
                };
                return $"{row.RegistrationTypeName} | {row.SelectedComponentCount} of {row.SelectionCount} selected | {status}";
            }

            string calls =
                row.CallCount == MessageAwareComponentSubscriptionRow.UnknownCallCount ? "calls n/a"
                : row.CallCount == 1 ? "1 call"
                : $"{row.CallCount} calls";
            return $"{row.RegistrationTypeName} | {calls}";
        }

        /// <summary>
        /// The priority badge's text. Priority decides dispatch order within a message type, so it is
        /// the one number on the row worth finding without reading a sentence; the <c>P</c> is what
        /// keeps a bare integer from reading as a count.
        /// </summary>
        internal static string CreatePriorityText(MessageAwareComponentSubscriptionRow row)
        {
            return "P" + row.Priority.ToString(CultureInfo.InvariantCulture);
        }

        private static VisualElement CreateRow(MessageAwareComponentSubscriptionRow row)
        {
            VisualElement element = new();
            element.AddToClassList(RowClassName);

            Label name = new(row.MessageTypeName)
            {
                tooltip = row.MessageType?.AssemblyQualifiedName ?? "Unknown message type",
            };
            name.AddToClassList(RowNameClassName);
            element.Add(name);

            Label priority = new(CreatePriorityText(row)) { name = RowPriorityLabelName };
            priority.AddToClassList(PriorityClassName);
            priority.tooltip = "Registration priority; lower runs earlier.";
            priority.style.marginRight = PriorityBadgeMarginRight;
            element.Add(priority);

            Label meta = new(CreateRowMetaText(row));
            meta.AddToClassList(RowMetaClassName);
            element.Add(meta);

            VisualElement dot = new() { name = RowStatusName };
            switch (row.Liveness)
            {
                case MessageAwareComponentSubscriptionLiveness.Live:
                    dot.AddToClassList(RowLiveClassName);
                    dot.tooltip = row.IsAggregate
                        ? "Enabled on every selected component carrying this registration."
                        : "This registration is enabled.";
                    break;
                case MessageAwareComponentSubscriptionLiveness.Mixed:
                    dot.AddToClassList(RowMixedClassName);
                    dot.tooltip =
                        "Enabled state differs across the selected components carrying this registration.";
                    break;
                default:
                    dot.AddToClassList(RowIdleClassName);
                    dot.tooltip = row.IsAggregate
                        ? "Disabled on every selected component carrying this registration."
                        : "This registration is disabled.";
                    break;
            }
            element.Add(dot);

            return element;
        }
    }

    internal enum MessageAwareComponentSubscriptionLiveness
    {
        Idle,
        Live,
        Mixed,
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
            Type messageType,
            string messageTypeName,
            string registrationTypeName,
            int priority,
            int callCount,
            MessageAwareComponentSubscriptionLiveness liveness,
            int selectedComponentCount,
            int selectionCount
        )
        {
            MessageType = messageType;
            MessageTypeName = messageTypeName;
            RegistrationTypeName = registrationTypeName;
            Priority = priority;
            CallCount = callCount;
            Liveness = liveness;
            SelectedComponentCount = selectedComponentCount;
            SelectionCount = selectionCount;
        }

        internal Type MessageType { get; }

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

        internal MessageAwareComponentSubscriptionLiveness Liveness { get; }

        /// <summary>True while every represented registration is subscribed on its bus.</summary>
        internal bool IsLive => Liveness == MessageAwareComponentSubscriptionLiveness.Live;

        internal int SelectedComponentCount { get; }

        internal int SelectionCount { get; }

        internal bool IsAggregate => SelectionCount > 1;
    }

    /// <summary>
    /// Snapshot of a component's registrations, captured without touching any GUI API so the
    /// decision and formatting logic stays testable without an editor panel.
    /// </summary>
    internal sealed class MessageAwareComponentSubscriptionsState
    {
        private static readonly MessageAwareComponentSubscriptionRow[] NoRows =
            Array.Empty<MessageAwareComponentSubscriptionRow>();

        private static readonly MessageAwareComponentSubscriptionsState EmptySelection = new(
            hasToken: false,
            tokenEnabled: false,
            diagnosticsEnabled: false,
            rows: NoRows,
            isAggregate: false,
            selectionCount: 0,
            tokenCount: 0
        );

        internal static readonly MessageAwareComponentSubscriptionsState None = new(
            hasToken: false,
            tokenEnabled: false,
            diagnosticsEnabled: false,
            rows: NoRows,
            isAggregate: false,
            selectionCount: 1,
            tokenCount: 0
        );

        internal MessageAwareComponentSubscriptionsState(
            bool hasToken,
            bool tokenEnabled,
            bool diagnosticsEnabled,
            IReadOnlyList<MessageAwareComponentSubscriptionRow> rows,
            bool isAggregate,
            int selectionCount,
            int tokenCount
        )
        {
            HasToken = hasToken;
            TokenEnabled = tokenEnabled;
            DiagnosticsEnabled = diagnosticsEnabled;
            Rows = rows ?? throw new ArgumentNullException(nameof(rows));
            IsAggregate = isAggregate;
            SelectionCount = selectionCount;
            TokenCount = tokenCount;
        }

        internal bool HasToken { get; }

        internal bool TokenEnabled { get; }

        internal bool DiagnosticsEnabled { get; }

        internal IReadOnlyList<MessageAwareComponentSubscriptionRow> Rows { get; }

        internal bool IsAggregate { get; }

        internal int SelectionCount { get; }

        internal int TokenCount { get; }

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
                revision = (revision * 31) + (IsAggregate ? 1 : 0);
                revision = (revision * 31) + SelectionCount;
                revision = (revision * 31) + TokenCount;
                revision = (revision * 31) + Rows.Count;
                foreach (MessageAwareComponentSubscriptionRow row in Rows)
                {
                    revision = (revision * 31) + row.CallCount;
                    revision = (revision * 31) + row.Priority;
                    revision = (revision * 31) + (int)row.Liveness;
                    revision = (revision * 31) + row.SelectedComponentCount;
                    revision = (revision * 31) + row.SelectionCount;
                    revision = (revision * 31) + (row.MessageType?.GetHashCode() ?? 0);
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
                    return MessageAwareComponentSubscriptionRow.UnknownCallCount;
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
                        metadata.type,
                        metadata.type == null ? "<unknown>" : metadata.type.Name,
                        metadata.registrationType.ToString(),
                        metadata.priority,
                        ResolveCallCount(entry.Key),
                        token.Enabled
                            ? MessageAwareComponentSubscriptionLiveness.Live
                            : MessageAwareComponentSubscriptionLiveness.Idle,
                        selectedComponentCount: 1,
                        selectionCount: 1
                    )
                );
            }

            rows.Sort(CompareRows);
            return new MessageAwareComponentSubscriptionsState(
                hasToken: true,
                tokenEnabled: token.Enabled,
                diagnosticsEnabled: diagnosticsEnabled,
                rows: rows,
                isAggregate: false,
                selectionCount: 1,
                tokenCount: 1
            );
        }

        internal static MessageAwareComponentSubscriptionsState Capture(
            IReadOnlyList<MessageAwareComponent> components
        )
        {
            if (components == null)
            {
                throw new ArgumentNullException(nameof(components));
            }

            int liveComponentCount = 0;
            MessageAwareComponent onlyLiveComponent = null;
            for (int componentIndex = 0; componentIndex < components.Count; componentIndex++)
            {
                MessageAwareComponent component = components[componentIndex];
                if (component == null)
                {
                    continue;
                }

                liveComponentCount++;
                onlyLiveComponent = component;
            }
            if (liveComponentCount == 0)
            {
                return EmptySelection;
            }
            if (liveComponentCount == 1)
            {
                return Capture(onlyLiveComponent);
            }

            Dictionary<MessageAwareComponentSubscriptionKey, AggregateRow> aggregateRows = new();
            int tokenCount = 0;
            for (int componentIndex = 0; componentIndex < components.Count; componentIndex++)
            {
                MessageAwareComponent component = components[componentIndex];
                MessageRegistrationToken token = component == null ? null : component.Token;
                if (token == null)
                {
                    continue;
                }

                tokenCount++;
                HashSet<MessageAwareComponentSubscriptionKey> seenForComponent = new();
                foreach (
                    KeyValuePair<
                        MessageRegistrationHandle,
                        MessageRegistrationMetadata
                    > entry in token._metadata
                )
                {
                    MessageRegistrationMetadata metadata = entry.Value;
                    MessageAwareComponentSubscriptionKey key = new(
                        metadata.type,
                        metadata.registrationType,
                        metadata.priority
                    );
                    if (!seenForComponent.Add(key))
                    {
                        continue;
                    }

                    if (!aggregateRows.TryGetValue(key, out AggregateRow aggregate))
                    {
                        aggregate = new AggregateRow(key);
                        aggregateRows.Add(key, aggregate);
                    }

                    aggregate.SelectedComponentCount++;
                    if (token.Enabled)
                    {
                        aggregate.EnabledComponentCount++;
                    }
                }
            }

            List<MessageAwareComponentSubscriptionRow> rows = new(aggregateRows.Count);
            foreach (AggregateRow aggregate in aggregateRows.Values)
            {
                MessageAwareComponentSubscriptionLiveness liveness =
                    aggregate.EnabledComponentCount == 0
                        ? MessageAwareComponentSubscriptionLiveness.Idle
                    : aggregate.EnabledComponentCount == aggregate.SelectedComponentCount
                        ? MessageAwareComponentSubscriptionLiveness.Live
                    : MessageAwareComponentSubscriptionLiveness.Mixed;
                rows.Add(
                    new MessageAwareComponentSubscriptionRow(
                        aggregate.Key.MessageType,
                        aggregate.Key.MessageType == null
                            ? "<unknown>"
                            : aggregate.Key.MessageType.Name,
                        aggregate.Key.RegistrationType.ToString(),
                        aggregate.Key.Priority,
                        MessageAwareComponentSubscriptionRow.UnknownCallCount,
                        liveness,
                        aggregate.SelectedComponentCount,
                        liveComponentCount
                    )
                );
            }

            rows.Sort(CompareRows);
            return new MessageAwareComponentSubscriptionsState(
                hasToken: tokenCount > 0,
                tokenEnabled: false,
                diagnosticsEnabled: false,
                rows: rows,
                isAggregate: true,
                selectionCount: liveComponentCount,
                tokenCount: tokenCount
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
                left.MessageType?.AssemblyQualifiedName,
                right.MessageType?.AssemblyQualifiedName
            );
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

        private sealed class AggregateRow
        {
            internal AggregateRow(MessageAwareComponentSubscriptionKey key)
            {
                Key = key;
            }

            internal MessageAwareComponentSubscriptionKey Key { get; }

            internal int SelectedComponentCount { get; set; }

            internal int EnabledComponentCount { get; set; }
        }

        private readonly struct MessageAwareComponentSubscriptionKey
            : IEquatable<MessageAwareComponentSubscriptionKey>
        {
            internal MessageAwareComponentSubscriptionKey(
                Type messageType,
                MessageRegistrationType registrationType,
                int priority
            )
            {
                MessageType = messageType;
                RegistrationType = registrationType;
                Priority = priority;
            }

            internal Type MessageType { get; }

            internal MessageRegistrationType RegistrationType { get; }

            internal int Priority { get; }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool Equals(MessageAwareComponentSubscriptionKey other)
            {
                return ReferenceEquals(MessageType, other.MessageType)
                    && RegistrationType == other.RegistrationType
                    && Priority == other.Priority;
            }

            public override bool Equals(object obj)
            {
                return obj is MessageAwareComponentSubscriptionKey other && Equals(other);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = (hash * 31) + (MessageType?.GetHashCode() ?? 0);
                    hash = (hash * 31) + (int)RegistrationType;
                    hash = (hash * 31) + Priority;
                    return hash;
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool operator ==(
                MessageAwareComponentSubscriptionKey left,
                MessageAwareComponentSubscriptionKey right
            )
            {
                return left.Equals(right);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool operator !=(
                MessageAwareComponentSubscriptionKey left,
                MessageAwareComponentSubscriptionKey right
            )
            {
                return !left.Equals(right);
            }
        }
    }
#endif
}
