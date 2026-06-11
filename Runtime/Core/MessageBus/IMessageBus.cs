namespace DxMessaging.Core.MessageBus
{
    using System;
    using System.Threading;
    using Core;
    using Extensions;
    using Messages;
#if UNITY_2021_3_OR_NEWER
    using UnityEngine;
#endif

    /// <summary>
    /// Contract for a general-purpose message bus handling registration and dispatch.
    /// </summary>
    /// <remarks>
    /// <para>Concepts:</para>
    /// <list type="bullet">
    /// <item><description><b>Handlers</b>: register to receive messages of specific types.</description></item>
    /// <item><description><b>Interceptors</b>: mutate or cancel messages before handlers run.</description></item>
    /// <item><description><b>Post-processors</b>: observe messages after handlers complete.</description></item>
    /// <item><description><b>Priority</b>: most registration APIs accept a <c>priority</c> parameter (lower runs first).</description></item>
    /// </list>
    /// <para>Message categories:</para>
    /// <list type="bullet">
    /// <item><description><see cref="Messages.IUntargetedMessage"/> -- Global notifications.</description></item>
    /// <item><description><see cref="Messages.ITargetedMessage"/> -- Directed at a specific <see cref="InstanceId"/>.</description></item>
    /// <item><description><see cref="Messages.IBroadcastMessage"/> -- Emitted from a source for any listener.</description></item>
    /// </list>
    /// <para>
    /// Contract note: emitting any message on a bus with no registered handlers must
    /// be a silent no-op. Implementations must NOT throw, log, or otherwise surface
    /// the empty-bus state to the caller. The
    /// <c>LifecycleEdgeCasesTests.EmitOnEmptyBusIsSilentNoOp</c> test pins this
    /// contract for every message kind.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Unity: register then send a targeted message
    /// var token = messagingComponent.Create(this);
    /// token.RegisterComponentTargeted&lt;TookDamage&gt;(this, (ref TookDamage m) =&gt; OnDamage(m));
    /// token.Enable();
    ///
    /// // Later, somewhere else
    /// DxMessaging.Core.InstanceId me = GetComponent&lt;UnityEngine.Component&gt;();
    /// var msg = new TookDamage(5);
    /// DxMessaging.Core.MessageHandler.MessageBus.TargetedBroadcast(ref me, ref msg);
    /// </code>
    /// </example>
    public interface IMessageBus
    {
        /// <summary>
        /// Default diagnostics mode for newly created buses and tokens.
        /// </summary>
        static DiagnosticsTarget GlobalDiagnosticsTargets { get; set; } = DiagnosticsTarget.Off;

        [Obsolete("Use GlobalDiagnosticsTargets instead.")]
        static bool GlobalDiagnosticsMode
        {
            get => GlobalDiagnosticsTargets != DiagnosticsTarget.Off;
            set => GlobalDiagnosticsTargets = value ? DiagnosticsTarget.All : DiagnosticsTarget.Off;
        }

        long EmissionId { get; }

        /// <summary>
        /// Reclaim empty message slots and pooled collections owned by this bus.
        /// </summary>
        /// <remarks>
        /// Non-Unity and headless hosts must call this periodically when they need
        /// deterministic reclamation. The automatic PlayerLoop sweep hook is only
        /// installed on Unity 2021.3 or newer player/editor hosts.
        /// </remarks>
        /// <param name="force">
        /// When true, ignores idle-age thresholds and drains shared pools to zero.
        /// When false, only slots past the configured idle threshold are eligible.
        /// </param>
        /// <returns>Counts describing what was reclaimed.</returns>
        TrimResult Trim(bool force = false);

        /// <summary>
        /// Default buffer size for message emission history.
        /// </summary>
        /// <remarks>
        /// This constant is used as the default value for <see cref="GlobalMessageBufferSize"/>
        /// and should be referenced by any settings classes to avoid configuration drift.
        /// </remarks>
        const int DefaultMessageBufferSize = 100;

        /// <summary>
        /// Default ring buffer size for emission history when diagnostics are enabled.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Controls the maximum number of <see cref="Diagnostics.MessageEmissionData"/> records retained
        /// per <see cref="MessageBus"/> instance when <see cref="DiagnosticsMode"/> is active.
        /// </para>
        /// <para>
        /// The default value is <see cref="DefaultMessageBufferSize"/> (100). Setting this to 0 will
        /// cause emissions to be silently discarded (no history is retained).
        /// </para>
        /// </remarks>
        static int GlobalMessageBufferSize { get; set; } = DefaultMessageBufferSize;

        internal static int GlobalSequentialIndex = -1;

        protected static int GenerateNewGlobalSequentialIndex() =>
            Interlocked.Increment(ref GlobalSequentialIndex);

        internal static bool ShouldEnableDiagnostics()
        {
#if UNITY_2021_3_OR_NEWER
            return ShouldEnableDiagnostics(GlobalDiagnosticsTargets, Application.isEditor);
#else
            return ShouldEnableDiagnostics(GlobalDiagnosticsTargets, isEditor: false);
#endif
        }

        internal static bool ShouldEnableDiagnostics(DiagnosticsTarget targets, bool isEditor)
        {
            if (targets == DiagnosticsTarget.Off)
            {
                return false;
            }

            return isEditor
                ? targets.HasFlagNoAlloc(DiagnosticsTarget.Editor)
                : targets.HasFlagNoAlloc(DiagnosticsTarget.Runtime);
        }

        /// <summary>
        /// Whether diagnostics are recorded for this bus instance.
        /// </summary>
        bool DiagnosticsMode { get; }

        int RegisteredGlobalSequentialIndex { get; }

        /// <summary>
        /// Number of currently occupied per-message-type slots on this bus.
        /// </summary>
        int OccupiedTypeSlots { get; }

        /// <summary>
        /// Number of currently occupied per-context target/source slots on this bus.
        /// </summary>
        int OccupiedTargetSlots { get; }

        /// <summary>
        /// Result returned by <see cref="Trim"/>.
        /// </summary>
        readonly struct TrimResult : IEquatable<TrimResult>
        {
            public TrimResult(
                int typeSlotsEvicted,
                int targetSlotsEvicted,
                int pooledCollectionsEvicted,
                int liveTypeSlotsRemaining
            )
            {
                TypeSlotsEvicted = typeSlotsEvicted;
                TargetSlotsEvicted = targetSlotsEvicted;
                PooledCollectionsEvicted = pooledCollectionsEvicted;
                LiveTypeSlotsRemaining = liveTypeSlotsRemaining;
            }

            /// <summary>Number of typed-handler slots reset.</summary>
            public int TypeSlotsEvicted { get; }

            /// <summary>Number of bus target/source context entries removed.</summary>
            public int TargetSlotsEvicted { get; }

            /// <summary>Number of pooled collections dropped from shared pools.</summary>
            public int PooledCollectionsEvicted { get; }

            /// <summary>Number of occupied type slots remaining after trim.</summary>
            public int LiveTypeSlotsRemaining { get; }

            public bool Equals(TrimResult other) =>
                TypeSlotsEvicted == other.TypeSlotsEvicted
                && TargetSlotsEvicted == other.TargetSlotsEvicted
                && PooledCollectionsEvicted == other.PooledCollectionsEvicted
                && LiveTypeSlotsRemaining == other.LiveTypeSlotsRemaining;

            public override bool Equals(object obj) => obj is TrimResult other && Equals(other);

            public override int GetHashCode() =>
                (
                    TypeSlotsEvicted,
                    TargetSlotsEvicted,
                    PooledCollectionsEvicted,
                    LiveTypeSlotsRemaining
                ).GetHashCode();

            public static bool operator ==(TrimResult left, TrimResult right) => left.Equals(right);

            public static bool operator !=(TrimResult left, TrimResult right) =>
                !left.Equals(right);

            public override string ToString() =>
                $"TrimResult(TypeSlotsEvicted={TypeSlotsEvicted}, TargetSlotsEvicted={TargetSlotsEvicted}, PooledCollectionsEvicted={PooledCollectionsEvicted}, LiveTypeSlotsRemaining={LiveTypeSlotsRemaining})";
        }

        int RegisteredBroadcast { get; }

        int RegisteredTargeted { get; }

        int RegisteredUntargeted { get; }

        /// <summary>
        /// Total number of registered interceptors across all message kinds.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Counts the unique <c>(interceptor delegate, priority)</c> pairs registered via
        /// <see cref="RegisterUntargetedInterceptor{T}"/>, <see cref="RegisterTargetedInterceptor{T}"/>,
        /// and <see cref="RegisterBroadcastInterceptor{T}"/>. Test infrastructure such as
        /// <c>LeakWatcher</c> reads this counter to detect interceptor handles that escape
        /// a watched region. Single-thread contract: read on the same thread that drives dispatch.
        /// </para>
        /// <para>
        /// Aggregated by walking the per-kind interceptor caches; the call is O(n) in the
        /// number of message types known to the bus. Snapshot at the start of a tracked
        /// region rather than reading in a hot loop.
        /// </para>
        /// </remarks>
        int RegisteredInterceptors { get; }

        /// <summary>
        /// Total number of registered post-processors across all message kinds.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Sums per-message-type post-processor handler counts across the untargeted,
        /// targeted, targeted-without-targeting, broadcast, and broadcast-without-source
        /// post-processor sinks. Test infrastructure such as <c>LeakWatcher</c> reads this
        /// counter to detect post-processor handles that escape a watched region. Same
        /// single-thread contract as the other counters.
        /// </para>
        /// <para>
        /// Aggregated on each read; prefer snapshotting at region boundaries instead of
        /// polling every frame.
        /// </para>
        /// </remarks>
        int RegisteredPostProcessors { get; }

        /// <summary>
        /// Number of registered global accept-all handlers on this bus.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Counts the distinct <see cref="MessageHandler"/> instances registered via
        /// <see cref="RegisterGlobalAcceptAll"/>. Test infrastructure such as
        /// <c>LeakWatcher</c> reads this counter to detect global accept-all handles
        /// that escape a watched region. Same single-thread contract as the other
        /// counters.
        /// </para>
        /// </remarks>
        int RegisteredGlobalAcceptAll { get; }

        /// <summary>
        /// Interceptor delegate for untargeted messages to transform or cancel them.
        /// </summary>
        /// <typeparam name="TMessage">Specific type of message.</typeparam>
        /// <param name="message">Message to consider.</param>
        /// <returns>True if the message should be processed, false if it should be skipped.</returns>
        delegate bool UntargetedInterceptor<TMessage>(ref TMessage message)
            where TMessage : IUntargetedMessage;

        /// <summary>
        /// Interceptor delegate for targeted messages to transform or cancel them.
        /// </summary>
        /// <typeparam name="TMessage">Specific type of message.</typeparam>
        /// <param name="target">Target of the message.</param>
        /// <param name="message">Message to consider.</param>
        /// <returns>True if the message should be processed, false if it should be skipped.</returns>
        delegate bool TargetedInterceptor<TMessage>(ref InstanceId target, ref TMessage message)
            where TMessage : ITargetedMessage;

        /// <summary>
        /// Interceptor delegate for broadcast messages to transform or cancel them.
        /// </summary>
        /// <typeparam name="TMessage">Specific type of message.</typeparam>
        /// <param name="source">Source of the message.</param>
        /// <param name="message">Message to consider.</param>
        /// <returns>True if the message should be processed, false if it should be skipped.</returns>
        delegate bool BroadcastInterceptor<TMessage>(ref InstanceId source, ref TMessage message)
            where TMessage : IBroadcastMessage;

        /// <summary>
        /// The registration log of all messaging registrations for this MessageBus.
        /// </summary>
        RegistrationLog Log { get; }

        /// <summary>
        /// Registers the specified MessageHandler to receive UntargetedMessages of the specified type.
        /// </summary>
        /// <typeparam name="T">Specific type of UntargetedMessages to register for.</typeparam>
        /// <param name="messageHandler">MessageHandler to register to accept UntargetedMessages of the specified type.</param>
        /// <param name="priority">Priority at which to run; lower runs earlier.</param>
        /// <returns>The deregistration action. Invoke when the handler no longer wants to receive messages.</returns>
        Action RegisterUntargeted<T>(MessageHandler messageHandler, int priority = 0)
            where T : IUntargetedMessage;

        /// <summary>
        /// Registers the specified MessageHandler to receive TargetedMessages of the specified type for the specified target.
        /// </summary>
        /// <typeparam name="T">Specific type of TargetedMessages to register for.</typeparam>
        /// <param name="target">Target of messages to listen for.</param>
        /// <param name="messageHandler">MessageHandler to register the TargetedMessages of the specified type.</param>
        /// <param name="priority">Priority at which to run; lower runs earlier.</param>
        /// <returns>The deregistration action. Invoke when the handler no longer wants to receive the messages.</returns>
        /// <remarks>
        /// To preserve frozen dispatch snapshots during in-flight emissions, the per-MessageHandler
        /// typed-cache for <c>(target, priority)</c> is NOT removed when the last registration at
        /// that pair is deregistered. Empty entries persist for the lifetime of the owning
        /// MessageHandler. For typical Unity usage with a small fixed set of priorities and a
        /// bounded set of long-lived target ids the residual footprint is trivial; code that
        /// registers per-ephemeral-target (e.g. a global service that listens to messages targeted
        /// at every spawned GameObject) should prefer <see cref="RegisterTargetedWithoutTargeting{T}"/>
        /// or recycle MessageHandlers to avoid unbounded inner-dictionary growth.
        /// </remarks>
        Action RegisterTargeted<T>(
            InstanceId target,
            MessageHandler messageHandler,
            int priority = 0
        )
            where T : ITargetedMessage;

        /// <summary>
        /// Registers the specified MessageHandler to receive TargetedMessages of the specified type.
        /// This registration IGNORES the targeting of the TargetedMessage.
        /// </summary>
        /// <typeparam name="T">Specific type of TargetedMessages to register for.</typeparam>
        /// <param name="messageHandler">MessageHandler to register to accept all TargetedMessages of the specified type.</param>
        /// <param name="priority">Priority at which to run; lower runs earlier.</param>
        /// <returns>The deregistration action. Invoke when the handler no longer wants to receive messages.</returns>
        Action RegisterTargetedWithoutTargeting<T>(MessageHandler messageHandler, int priority = 0)
            where T : ITargetedMessage;

        /// <summary>
        /// Registers the specified MessageHandler to receive BroadcastMessages of the specified type from the provided source.
        /// </summary>
        /// <typeparam name="T">Type of the BroadcastMessage to register.</typeparam>
        /// <param name="source">InstanceId of the source for BroadcastMessages to listen to.</param>
        /// <param name="messageHandler">MessageHandler to register to accept BroadcastMessages.</param>
        /// <param name="priority"></param>
        /// <returns>The deregistration action. Should be invoked when the handler no longer wants to receive messages.</returns>
        /// <remarks>
        /// To preserve frozen dispatch snapshots during in-flight emissions, the per-MessageHandler
        /// typed-cache for <c>(source, priority)</c> is NOT removed when the last registration at
        /// that pair is deregistered. Empty entries persist for the lifetime of the owning
        /// MessageHandler. For typical Unity usage with a small fixed set of priorities and a
        /// bounded set of long-lived source ids the residual footprint is trivial; code that
        /// registers per-ephemeral-source (e.g. a global service that listens to broadcasts from
        /// every spawned GameObject) should prefer <see cref="RegisterSourcedBroadcastWithoutSource{T}"/>
        /// or recycle MessageHandlers to avoid unbounded inner-dictionary growth.
        /// </remarks>
        Action RegisterSourcedBroadcast<T>(
            InstanceId source,
            MessageHandler messageHandler,
            int priority = 0
        )
            where T : IBroadcastMessage;

        /// <summary>
        /// Registers the specified MessageHandler to receive BroadcastMessages of the specified type from ALL sources.
        /// This registration IGNORES the source of the BroadcastMessage.
        /// </summary>
        /// <typeparam name="T">Type of the BroadcastMessage to register.</typeparam>
        /// <param name="messageHandler">MessageHandler to register to accept BroadcastMessages.</param>
        /// <param name="priority"></param>
        /// <returns>The deregistration action. Should be invoked when the handler no longer wants to receive messages.</returns>
        Action RegisterSourcedBroadcastWithoutSource<T>(
            MessageHandler messageHandler,
            int priority = 0
        )
            where T : IBroadcastMessage;

        /// <summary>
        /// Registers the specified MessageHandler to receive ALL messages.
        /// It doesn't matter if the message is Targeted or Untargeted, this MessageHandler will be invoked for it.
        /// </summary>
        /// <param name="messageHandler">MessageHandler to register to accept all messages.</param>
        /// <returns>The deregistration action. Should be invoked when the handler no longer wants to receive messages.</returns>
        Action RegisterGlobalAcceptAll(MessageHandler messageHandler);

        /// <summary>
        /// Registers the specified MessageHandler and transformer function as an interceptor for Messages of type T.
        /// Whenever messages of that type are sent, interceptors will be ran in order by priority and then order of registration within
        /// that priority, transforming that message into new, mutated types.
        /// If any interceptor returns false, message handling is immediately stopped.
        /// The message at the end of the transformations will be then sent to registered message handlers.
        /// </summary>
        /// <note>
        /// Transformer function can return false to "cancel" the message being sent.
        /// </note>
        /// <typeparam name="T">Type of message to intercept.</typeparam>
        /// <param name="interceptor">Transformation function to run on messages of the chosen type.</param>
        /// <param name="priority">Priority of the interceptor to run at. Handlers run in order of priority from low -> high.</param>
        /// <note>
        ///     The transform function takes:
        ///         param1: Current message instance by reference
        ///     And returns: true if message handling should continue, false if message handling should be stopped.
        /// </note>
        /// <remarks>
        /// Interceptor delegates registered with this method are retained for the bus lifetime.
        /// Calling the returned deregistration action removes the interceptor from dispatch, but
        /// the delegate reference may remain until the final deregistration of that interceptor.
        /// </remarks>
        /// <returns>The deregistration action. Should be invoked when the handler no longer wants to intercept messages.</returns>
        Action RegisterUntargetedInterceptor<T>(
            UntargetedInterceptor<T> interceptor,
            int priority = 0
        )
            where T : IUntargetedMessage;

        /// <summary>
        /// Registers the specified MessageHandler and transformer function as an interceptor for Messages of type T.
        /// Whenever messages of that type are sent, interceptors will be ran in order by priority and then order of registration within
        /// that priority, transforming that message into new, mutated types.
        /// If any interceptor returns false, message handling is immediately stopped.
        /// The message at the end of the transformations will be then sent to registered message handlers.
        /// </summary>
        /// <note>
        /// Transformer function can return false to "cancel" the message being sent.
        /// </note>
        /// <typeparam name="T">Type of message to intercept.</typeparam>
        /// <param name="interceptor">Transformation function to run on messages of the chosen type.</param>
        /// <param name="priority">Priority of the interceptor to run at. Handlers run in order of priority from low -> high.</param>
        /// <note>
        ///     The transform function takes:
        ///         param1: Current message instance by reference
        ///     And returns: true if message handling should continue, false if message handling should be stopped.
        /// </note>
        /// <remarks>
        /// Interceptor delegates registered with this method are retained for the bus lifetime.
        /// Calling the returned deregistration action removes the interceptor from dispatch, but
        /// the delegate reference may remain until the final deregistration of that interceptor.
        /// </remarks>
        /// <returns>The deregistration action. Should be invoked when the handler no longer wants to intercept messages.</returns>
        Action RegisterTargetedInterceptor<T>(TargetedInterceptor<T> interceptor, int priority = 0)
            where T : ITargetedMessage;

        /// <summary>
        /// Registers the specified MessageHandler and transformer function as an interceptor for Messages of type T.
        /// Whenever messages of that type are sent, interceptors will be ran in order by priority and then order of registration within
        /// that priority, transforming that message into new, mutated types.
        /// If any interceptor returns false, message handling is immediately stopped.
        /// The message at the end of the transformations will be then sent to registered message handlers.
        /// </summary>
        /// <note>
        /// Transformer function can return false to "cancel" the message being sent.
        /// </note>
        /// <typeparam name="T">Type of message to intercept.</typeparam>
        /// <param name="interceptor">Transformation function to run on messages of the chosen type.</param>
        /// <param name="priority">Priority of the interceptor to run at. Handlers run in order of priority from low -> high.</param>
        /// <note>
        ///     The transform function takes:
        ///         param1: Current message instance by reference
        ///     And returns: true if message handling should continue, false if message handling should be stopped.
        /// </note>
        /// <remarks>
        /// Interceptor delegates registered with this method are retained for the bus lifetime.
        /// Calling the returned deregistration action removes the interceptor from dispatch, but
        /// the delegate reference may remain until the final deregistration of that interceptor.
        /// </remarks>
        /// <returns>The deregistration action. Should be invoked when the handler no longer wants to intercept messages.</returns>
        Action RegisterBroadcastInterceptor<T>(
            BroadcastInterceptor<T> interceptor,
            int priority = 0
        )
            where T : IBroadcastMessage;

        /// <summary>
        /// Registers the provided MessageHandler to post-process Untargeted messages of the given type.
        /// (This will run after all handlers run for the provided message).
        /// </summary>
        /// <typeparam name="T">Type of UntargetedMessage to post-process.</typeparam>
        /// <param name="messageHandler">MessageHandler to post-process messages for.</param>
        /// <param name="priority">Priority of the interceptor to run at. Handlers run in order of priority from low -> high.</param>
        /// <returns>The deregistration action. Should be invoked when the handler no longer wants to post-process messages.</returns>
        Action RegisterUntargetedPostProcessor<T>(MessageHandler messageHandler, int priority = 0)
            where T : IUntargetedMessage;

        /// <summary>
        /// Registers the provided MessageHandler to post-process Targeted messages of the given type.
        /// (This will run after all handlers run for the provided message).
        /// </summary>
        /// <typeparam name="T">Type of TargetedMessage to post-process.</typeparam>
        /// <param name="target">Target of messages to listen for.</param>
        /// <param name="messageHandler">MessageHandler to post-process messages for.</param>
        /// <param name="priority">Priority of the interceptor to run at. Handlers run in order of priority from low -> high.</param>
        /// <returns>The deregistration action. Should be invoked when the handler no longer wants to post-process messages.</returns>
        Action RegisterTargetedPostProcessor<T>(
            InstanceId target,
            MessageHandler messageHandler,
            int priority = 0
        )
            where T : ITargetedMessage;

        /// <summary>
        /// Registers the provided MessageHandler to post-process Targeted messages of the given type for all targets.
        /// (This will run after all handlers run for the provided message).
        /// </summary>
        /// <typeparam name="T">Type of TargetedMessage to post-process.</typeparam>
        /// <param name="messageHandler">MessageHandler to post-process messages for.</param>
        /// <param name="priority">Priority of the interceptor to run at. Handlers run in order of priority from low -> high.</param>
        /// <returns>The deregistration action. Should be invoked when the handler no longer wants to post-process messages.</returns>
        Action RegisterTargetedWithoutTargetingPostProcessor<T>(
            MessageHandler messageHandler,
            int priority = 0
        )
            where T : ITargetedMessage;

        /// <summary>
        /// Registers the provided MessageHandler to post-process Targeted messages of the given type.
        /// (This will run after all handlers run for the provided message).
        /// </summary>
        /// <typeparam name="T">Type of TargetedMessage to post-process.</typeparam>
        /// <param name="source">Source of messages to listen for.</param>
        /// <param name="messageHandler">MessageHandler to post-process messages for.</param>
        /// <param name="priority">Priority of the interceptor to run at. Handlers run in order of priority from low -> high.</param>
        /// <returns>The deregistration action. Should be invoked when the handler no longer wants to post-process messages.</returns>
        Action RegisterBroadcastPostProcessor<T>(
            InstanceId source,
            MessageHandler messageHandler,
            int priority = 0
        )
            where T : IBroadcastMessage;

        /// <summary>
        /// Registers the provided MessageHandler to post-process Targeted messages of the given type for all sources.
        /// (This will run after all handlers run for the provided message).
        /// </summary>
        /// <typeparam name="T">Type of TargetedMessage to post-process.</typeparam>
        /// <param name="messageHandler">MessageHandler to post-process messages for.</param>
        /// <param name="priority">Priority of the interceptor to run at. Handlers run in order of priority from low -> high.</param>
        /// <returns>The deregistration action. Should be invoked when the handler no longer wants to post-process messages.</returns>
        Action RegisterBroadcastWithoutSourcePostProcessor<T>(
            MessageHandler messageHandler,
            int priority = 0
        )
            where T : IBroadcastMessage;

        /// <summary>
        /// Broadcasts an Untargeted message to all listeners registered to this bus.
        /// </summary>
        /// <param name="typedMessage">Message to broadcast.</param>
        void UntypedUntargetedBroadcast(IUntargetedMessage typedMessage);

        /// <summary>
        /// Broadcasts a fast Untargeted message to all listeners registered to this bus.
        /// </summary>
        /// <param name="typedMessage">Message to broadcast.</param>
        void UntargetedBroadcast<TMessage>(ref TMessage typedMessage)
            where TMessage : IUntargetedMessage;

        /// <summary>
        /// Broadcasts a TargetedMessage to all listeners registered to this bus.
        /// </summary>
        /// <param name="target">Target to send the message to.</param>
        /// <param name="typedMessage">Message to broadcast.</param>
        void UntypedTargetedBroadcast(InstanceId target, ITargetedMessage typedMessage);

        /// <summary>
        /// Broadcasts a fast TargetedMessage to all listeners registered to this bus.
        /// </summary>
        /// <param name="target">Target to send the message to.</param>
        /// <param name="typedMessage">Message to broadcast.</param>
        void TargetedBroadcast<TMessage>(ref InstanceId target, ref TMessage typedMessage)
            where TMessage : ITargetedMessage;

        /// <summary>
        /// Broadcasts a BroadcastMessage to all listeners registered to this bus.
        /// </summary>
        /// <param name="source">Source of the message.</param>
        /// <param name="typedMessage">Message to broadcast.</param>
        void UntypedSourcedBroadcast(InstanceId source, IBroadcastMessage typedMessage);

        /// <summary>
        /// Broadcasts a fast BroadcastMessage to all listeners registered to this bus.
        /// </summary>
        /// <param name="source">Source of the message.</param>
        /// <param name="typedMessage">Message to broadcast.</param>
        void SourcedBroadcast<TMessage>(ref InstanceId source, ref TMessage typedMessage)
            where TMessage : IBroadcastMessage;
    }
}
