namespace DxMessaging.Core.MessageBus.Internal
{
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Runtime.CompilerServices;
    using DxMessaging.Core;

    /// <summary>
    /// Per-bus global accept-all slot. Replaces the legacy non-generic
    /// <c>HandlerCache</c> previously declared in <see cref="MessageBus"/> --
    /// the slot that holds the "subscribe to every emit" handlers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This slot models global accept-all handlers as one shared handler set
    /// (<see cref="sharedHandlers"/> / <see cref="sharedCache"/>) and three
    /// separate per-kind dispatch state fields
    /// (<see cref="untargetedDispatchState"/>,
    /// <see cref="targetedDispatchState"/>,
    /// <see cref="broadcastDispatchState"/>). The discrete fields keep the
    /// per-emission slot select branch-free under JIT monomorphization,
    /// avoiding the dictionary lookup the legacy non-generic
    /// <c>HandlerCache</c> imposed.
    /// </para>
    /// </remarks>
    internal sealed class BusGlobalSlot : IEvictableSlot
    {
        /// <summary>
        /// Live global handlers, keyed by handler with insertion order tracked
        /// via the integer payload. Mirrors the legacy non-generic
        /// <c>HandlerCache.handlers</c> field.
        /// </summary>
        public readonly Dictionary<MessageHandler, int> sharedHandlers = new();

        /// <summary>
        /// Reserved for global-slot snapshot iteration. Mirrors the legacy
        /// non-generic <c>HandlerCache.cache</c> field, which was likewise
        /// allocated for parity but never populated or read by any dispatch path.
        /// Cleared by <see cref="Clear"/> and <see cref="Reset"/> as part of the
        /// slot lifecycle.
        /// </summary>
        public readonly List<MessageHandler> sharedCache = new();

        /// <summary>Monotonic version counter for the slot's structural state.</summary>
        public long version;

        /// <summary>
        /// Reserved legacy snapshot-version counter. Active global snapshots
        /// use the per-kind dispatch state fields instead.
        /// </summary>
        public long lastSeenVersion = -1;

        /// <summary>
        /// Reserved legacy snapshot-emission counter. Active global snapshots
        /// use the per-kind dispatch state fields instead.
        /// </summary>
        public long lastSeenEmissionId = -1;

        /// <summary>
        /// Bus tick counter value at the most recent register / deregister /
        /// emit that touched this slot. Maintained by the sweep touch hook;
        /// preserved across <see cref="Clear"/> and <see cref="Reset"/>.
        /// </summary>
        public long lastTouchTicks;

        /// <summary>
        /// <para>
        /// Live-handler counter that mirrors <c>sharedHandlers.Count</c> at
        /// every stable observation point. Maintained by the bus at the
        /// register / deregister sites for <c>RegisterGlobalAcceptAll</c> so
        /// <see cref="IsEmpty"/> is a single integer compare rather than a
        /// dictionary-count read.
        /// </para>
        /// <para>
        /// The invariant is <c>liveCount == sharedHandlers.Count</c>: only the
        /// per-handler refcount's <c>0 -&gt; 1</c> transition (newly-inserted
        /// handler) increments <see cref="liveCount"/>, and only the
        /// <c>1 -&gt; 0</c> transition (final removal of a handler) decrements
        /// it. Re-registering an already-present handler (refcount
        /// <c>n -&gt; n+1</c> for <c>n &gt;= 1</c>) leaves the counter alone,
        /// matching the dictionary's behaviour. Over-deregistration is a
        /// no-op for both fields. <c>DEBUG</c> builds verify the invariant
        /// after every register / deregister via
        /// <c>MessageBus.DebugAssertGlobalLiveCount</c> and
        /// <see cref="DebugAssertLiveCountInvariant"/>.
        /// </para>
        /// </summary>
        public int liveCount;

        /// <summary>
        /// Dispatch state for the Untargeted-global emission path. One of the
        /// three discrete per-kind fields. Separate slots over a per-kind
        /// dictionary keep the per-emission
        /// select branch-free under JIT monomorphization. Lazy alloc on first
        /// Stage/Acquire; null after Reset().
        /// </summary>
        public MessageBus.DispatchState untargetedDispatchState;

        /// <summary>
        /// Dispatch state for the Targeted-global emission path. Sibling of
        /// <see cref="untargetedDispatchState"/>; same lifetime semantics.
        /// </summary>
        public MessageBus.DispatchState targetedDispatchState;

        /// <summary>
        /// Dispatch state for the Broadcast-global emission path. Sibling of
        /// <see cref="untargetedDispatchState"/>; same lifetime semantics.
        /// </summary>
        public MessageBus.DispatchState broadcastDispatchState;

        /// <inheritdoc />
        public long LastTouchTicks
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => lastTouchTicks;
        }

        /// <inheritdoc />
        public bool IsEmpty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => liveCount == 0;
        }

        /// <inheritdoc />
        public long Version
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => version;
        }

        /// <summary>
        /// Full-reset semantic that mirrors the legacy non-generic
        /// <c>HandlerCache.Clear()</c> body. Clears
        /// <see cref="sharedHandlers"/> and <see cref="sharedCache"/>
        /// and resets the dispatch-snapshot counters. Resets
        /// <see cref="version"/> to <c>0</c>; this is NOT monotonic and is
        /// intended only for the bus-wide <c>MessageBus.ResetState()</c> code
        /// path. Use <see cref="Reset"/> for sweep-driven slot reclamation.
        /// </summary>
        public void Clear()
        {
            sharedHandlers.Clear();
            sharedCache.Clear();
            untargetedDispatchState?.Reset();
            untargetedDispatchState = null;
            targetedDispatchState?.Reset();
            targetedDispatchState = null;
            broadcastDispatchState?.Reset();
            broadcastDispatchState = null;
            version = 0;
            lastSeenVersion = -1;
            lastSeenEmissionId = -1;
            liveCount = 0;
        }

        /// <summary>
        /// Eviction-driven reset. Clears all structural state without touching
        /// <see cref="version"/>, then bumps <see cref="version"/> as the LAST
        /// step so any captured dispatch closure that observed the prior
        /// version detects invalidation. <see cref="lastTouchTicks"/> is
        /// intentionally preserved.
        /// </summary>
        public void Reset()
        {
            // Inline the structural-clear body of Clear(); do NOT call Clear()
            // because that resets version=0 and would break the monotonic
            // invariant the eviction layer depends on.
            sharedHandlers.Clear();
            sharedCache.Clear();
            untargetedDispatchState?.Reset();
            untargetedDispatchState = null;
            targetedDispatchState?.Reset();
            targetedDispatchState = null;
            broadcastDispatchState?.Reset();
            broadcastDispatchState = null;
            lastSeenVersion = -1;
            lastSeenEmissionId = -1;
            liveCount = 0;
            unchecked
            {
                ++version;
            }
        }

        /// <summary>
        /// Defensive <c>DEBUG</c>-only assertion that <see cref="liveCount"/>
        /// equals <c>sharedHandlers.Count</c>. Provided so contract tests can
        /// pin the invariant without exposing private bus state. Stripped in
        /// Release builds via <see cref="ConditionalAttribute"/>.
        /// </summary>
        [Conditional("DEBUG")]
        internal void DebugAssertLiveCountInvariant()
        {
            Debug.Assert(
                liveCount == sharedHandlers.Count,
                "BusGlobalSlot.liveCount must mirror sharedHandlers.Count at every "
                    + "stable observation point."
            );
        }
    }
}
