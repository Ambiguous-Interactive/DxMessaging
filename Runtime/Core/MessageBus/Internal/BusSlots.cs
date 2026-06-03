namespace DxMessaging.Core.MessageBus.Internal
{
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Runtime.CompilerServices;
    using DxMessaging.Core;
    using DxMessaging.Core.Internal;
    using DxMessaging.Core.Pooling;

    /// <summary>
    /// Per-priority leaf storage for a single dispatch slot. Mirrors the
    /// per-priority bucket previously held inside the legacy nested
    /// <c>HandlerCache</c> type previously declared in <see cref="MessageBus"/>:
    /// a handler set keyed by <see cref="MessageHandler"/> with insertion-order
    /// tracking via the integer payload (priority slot index), plus a flat
    /// cache list for snapshot-friendly iteration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="BusPriorityBucket"/> is intentionally NOT an
    /// <see cref="IEvictableSlot"/>: it is only ever owned by a
    /// <see cref="BusSinkSlot"/> and is reclaimed transitively when its parent
    /// is reset. Eviction never targets a bucket directly.
    /// </para>
    /// <para>
    /// The <see cref="version"/> / <see cref="lastSeenVersion"/> /
    /// <see cref="lastSeenEmissionId"/> triple is the same staged-dispatch
    /// snapshot mechanism used by the legacy <c>HandlerCache</c> -- structural
    /// mutations bump <see cref="version"/>, dispatchers compare against
    /// <see cref="lastSeenVersion"/> to decide whether to re-snapshot.
    /// </para>
    /// </remarks>
    internal sealed class BusPriorityBucket
    {
        /// <summary>
        /// Live handlers in this priority bucket. Value is the priority slot
        /// index used by the staged dispatch snapshot pattern (matches the
        /// legacy <c>HandlerCache.handlers</c> layout).
        /// </summary>
        public readonly Dictionary<MessageHandler, int> handlers = new();

        /// <summary>
        /// Flat snapshot-friendly cache of <see cref="handlers"/> keys; rebuilt
        /// lazily by the dispatcher when <see cref="version"/> changes.
        /// </summary>
        public readonly List<MessageHandler> cache = new();

        /// <summary>Monotonic version counter for the bucket contents.</summary>
        public long version;

        /// <summary>
        /// The <see cref="version"/> value observed by the most recent
        /// dispatcher snapshot. Used to decide whether <see cref="cache"/> needs
        /// to be re-materialized before the next dispatch.
        /// </summary>
        public long lastSeenVersion = -1;

        /// <summary>
        /// The bus emission id of the most recent dispatch that consumed this
        /// bucket. Used by the staged dispatch staleness check.
        /// </summary>
        public long lastSeenEmissionId = -1;

        /// <summary>
        /// Clear all bucket state. Mirrors the legacy
        /// <c>HandlerCache.Clear()</c> body -- empties handlers and cache and
        /// resets the dispatch-snapshot counters. Resets <see cref="version"/>
        /// to <c>0</c>; this is the legacy "full reset" semantic and is NOT
        /// monotonic. Eviction-driven reset semantics live on the parent
        /// <see cref="BusSinkSlot.Reset"/>, which preserves monotonicity by
        /// bumping the parent slot's version after clearing buckets.
        /// </summary>
        public void Clear()
        {
            handlers.Clear();
            cache.Clear();
            version = 0;
            lastSeenVersion = -1;
            lastSeenEmissionId = -1;
        }
    }

    /// <summary>
    /// Per-message-type, per-context dispatch slot. Replaces the inner
    /// <c>HandlerCache&lt;int, HandlerCache&gt;</c> type previously declared in
    /// <see cref="MessageBus"/>. Holds the priority-keyed map of
    /// <see cref="BusPriorityBucket"/>s and the flat ordered-priority list used
    /// by the staged dispatch snapshot pattern.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In the new layout each <see cref="BusSinkSlot"/> belongs to either the
    /// scalar slot grid (<c>WithoutContext</c> variants -- no
    /// <see cref="InstanceId"/> hash on the hot path) or the inner map of a
    /// <see cref="BusContextSlot"/> (variants that carry an
    /// <see cref="InstanceId"/> recipient or source).
    /// </para>
    /// <para>
    /// The <see cref="dispatchState"/> field carries the staged Stage/Acquire
    /// snapshot for this slot.
    /// </para>
    /// </remarks>
    internal sealed class BusSinkSlot : IEvictableSlot
    {
        /// <summary>
        /// Per-priority handler buckets, keyed by priority value.
        /// </summary>
        public readonly Dictionary<int, BusPriorityBucket> handlersByPriority = new();

        /// <summary>
        /// Insertion-ordered list of priority keys present in
        /// <see cref="handlersByPriority"/>. Mirrors the legacy
        /// <c>HandlerCache.order</c> field.
        /// </summary>
        public readonly List<int> orderedPriorities = new();

        /// <summary>
        /// Flat snapshot-friendly cache of <see cref="handlersByPriority"/>
        /// entries; rebuilt lazily by the dispatcher when <see cref="version"/>
        /// changes. Mirrors the legacy <c>HandlerCache.cache</c> field.
        /// </summary>
        public readonly List<KeyValuePair<int, BusPriorityBucket>> cache = new();

        /// <summary>Monotonic version counter for the slot's structural state.</summary>
        public long version;

        /// <summary>
        /// The <see cref="version"/> value observed by the most recent
        /// dispatcher snapshot. Used to decide whether <see cref="cache"/>
        /// needs to be re-materialized before the next dispatch.
        /// </summary>
        public long lastSeenVersion = -1;

        /// <summary>
        /// The bus emission id of the most recent dispatch that consumed this
        /// slot. Used by the staged dispatch staleness check.
        /// </summary>
        public long lastSeenEmissionId = -1;

        /// <summary>
        /// Bus tick counter value at the most recent register / deregister /
        /// emit that touched this slot. Maintained by the sweep touch hook;
        /// preserved across <see cref="Clear"/> and <see cref="Reset"/> so the
        /// sweep can distinguish never-touched slots from freshly-reset slots.
        /// </summary>
        public long lastTouchTicks;

        /// <summary>
        /// <para>
        /// Reserved live-handler counter intended to mirror the unique
        /// (<see cref="MessageHandler"/>, priority) pair count across every
        /// entry in <see cref="handlersByPriority"/>, so <see cref="IsEmpty"/>
        /// becomes a single integer compare rather than a walk over priority
        /// buckets. This counter is reserved until <see cref="BusSinkSlot"/> is
        /// used as the typed-sink storage type. <see cref="IsEmpty"/> currently
        /// returns <c>true</c> at all times because no writer increments
        /// <see cref="liveCount"/>.
        /// </para>
        /// <para>
        /// Intended transitions once wired: re-registration of an existing
        /// pair will be a no-op on this counter; only newly-inserted pairs
        /// will increment it, and only the final removal of a pair will
        /// decrement it.
        /// </para>
        /// </summary>
        public int liveCount;

        /// <summary>
        /// Per-slot dispatch state for the staged Stage/Acquire snapshot
        /// pattern. Single field per slot -- the previous per-slot-key
        /// dictionary keyed by dispatch slot is unnecessary once
        /// each slot maps 1:1 to a <see cref="SlotKey"/>. Lazy alloc on
        /// first Stage/Acquire; null after Reset(). This field is
        /// forward-compatible plumbing. Wiring lands when
        /// <see cref="BusSinkSlot"/> becomes the storage type backing the
        /// typed-sink hot dispatch path (replacing the current legacy
        /// <c>HandlerCache&lt;int, HandlerCache&gt;</c> generic outer +
        /// non-generic inner pair). The intermediate phases that retire
        /// the legacy category enum and split the Handle-phase variants
        /// do NOT touch this field; they continue working through the
        /// legacy storage type's <c>dispatchState</c>.
        /// </summary>
        public MessageBus.DispatchState dispatchState;

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
        /// Full-reset semantic that mirrors the legacy
        /// <c>HandlerCache&lt;TKey, TValue&gt;.Clear()</c> body. Clears all
        /// priority buckets and the outer maps, and resets the
        /// staged-dispatch snapshot counters.
        /// Resets <see cref="version"/> to <c>0</c>; this is NOT monotonic and
        /// is intended only for the bus-wide
        /// <c>MessageBus.ResetState()</c> code path. Use <see cref="Reset"/>
        /// for sweep-driven slot reclamation.
        /// </summary>
        public void Clear()
        {
            foreach (BusPriorityBucket bucket in handlersByPriority.Values)
            {
                bucket.Clear();
            }
            handlersByPriority.Clear();
            orderedPriorities.Clear();
            cache.Clear();
            dispatchState?.Reset();
            dispatchState = null;
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
        /// intentionally preserved so the sweep can distinguish freshly-reset
        /// slots from never-touched ones.
        /// </summary>
        public void Reset()
        {
            // Inline the structural-clear body of Clear(); do NOT call Clear()
            // because that resets version=0 and would break the monotonic
            // invariant the eviction layer depends on: stale deregister
            // closures captured before reset must observe a strictly larger
            // version after reset and skip their work.
            foreach (BusPriorityBucket bucket in handlersByPriority.Values)
            {
                bucket.Clear();
            }
            handlersByPriority.Clear();
            orderedPriorities.Clear();
            cache.Clear();
            dispatchState?.Reset();
            dispatchState = null;
            lastSeenVersion = -1;
            lastSeenEmissionId = -1;
            liveCount = 0;
            unchecked
            {
                ++version;
            }
        }
    }

    /// <summary>
    /// Per-message-type context-bound dispatch slot. Owns the
    /// <see cref="InstanceId"/>-keyed map of inner <see cref="BusSinkSlot"/>s
    /// for one message type's targeted or broadcast variants. Replaces the
    /// outer per-message-type dictionaries previously held in the
    /// targeted/broadcast sink fields on <see cref="MessageBus"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The inner map is rented from
    /// <see cref="DxPools.InstanceIdDicts"/>. The pool stores
    /// <c>Dictionary&lt;InstanceId, object&gt;</c> -- generic-erased to share a
    /// single pool across every message-type instantiation. Each value is a
    /// <see cref="BusSinkSlot"/>, accessed via
    /// <see cref="DxUnsafe.As{T}(object)"/>; the class is sealed and only inserted
    /// from this type's own methods, so the cast cannot encounter a foreign
    /// runtime type. <c>DEBUG</c> builds verify the invariant at every
    /// cast site.
    /// </para>
    /// <para>
    /// The map is left null until first registration so empty slots cost only
    /// the field set itself. <see cref="Clear"/> empties the map in place but
    /// does NOT return it to the pool; <see cref="Reset"/> returns the map to
    /// the pool and nulls the field.
    /// </para>
    /// </remarks>
    internal sealed class BusContextSlot : IEvictableSlot
    {
        /// <summary>
        /// Inner per-context map. Null until first registration. Values are
        /// <see cref="BusSinkSlot"/> instances stored as <see cref="object"/>
        /// so the underlying dictionary can be pooled in the shared
        /// <see cref="DxPools.InstanceIdDicts"/> pool.
        /// </summary>
        public Dictionary<InstanceId, object> byContext;

        /// <summary>Monotonic version counter for the slot's structural state.</summary>
        public long version;

        /// <summary>
        /// Bus tick counter value at the most recent register / deregister /
        /// emit that touched this slot. Maintained by the sweep touch hook;
        /// preserved across <see cref="Clear"/> and <see cref="Reset"/>.
        /// </summary>
        public long lastTouchTicks;

        /// <summary>
        /// <para>
        /// Reserved live-context counter intended to mirror the count of
        /// <see cref="InstanceId"/> keys in <see cref="byContext"/> that
        /// currently retain at least one live handler, so <see cref="IsEmpty"/>
        /// becomes a single integer compare rather than a recursive walk over
        /// the inner per-context slots. This counter is reserved until
        /// <see cref="BusContextSlot"/> is used as the typed-sink storage type.
        /// <see cref="IsEmpty"/> currently returns <c>true</c> at all times
        /// because no writer increments <see cref="liveCount"/>.
        /// </para>
        /// <para>
        /// Intended transitions once wired: the bus will increment by 1 when
        /// a context goes from zero handlers to one, and decrement by 1 when
        /// a context drops back to zero handlers (and is removed via
        /// <see cref="RemoveContext"/>); registering or deregistering inside
        /// an already-live context will not adjust this counter.
        /// (<see cref="BusSinkSlot.liveCount"/> is the per-context handler
        /// count; this is the per-slot context count.)
        /// </para>
        /// </summary>
        public int liveCount;

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
        /// Look up the inner <see cref="BusSinkSlot"/> for the supplied
        /// context. Returns <c>false</c> when <see cref="byContext"/> is null
        /// or the context is not present.
        /// </summary>
        /// <param name="context">The <see cref="InstanceId"/> context key.</param>
        /// <param name="slot">
        /// The inner slot when present; <c>null</c> otherwise.
        /// </param>
        /// <returns><c>true</c> when a slot was found.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetSlot(InstanceId context, out BusSinkSlot slot)
        {
            Dictionary<InstanceId, object> map = byContext;
            if (map == null)
            {
                slot = null;
                return false;
            }
            if (!map.TryGetValue(context, out object boxed))
            {
                slot = null;
                return false;
            }
            DebugAssertSlot(boxed);
            slot = DxUnsafe.As<BusSinkSlot>(boxed);
            return true;
        }

        /// <summary>
        /// Look up or create the inner <see cref="BusSinkSlot"/> for the
        /// supplied context. Lazily rents the inner map from
        /// <see cref="DxPools.InstanceIdDicts"/> on first use.
        /// </summary>
        /// <param name="context">The <see cref="InstanceId"/> context key.</param>
        /// <returns>
        /// The existing or freshly-allocated <see cref="BusSinkSlot"/> for
        /// <paramref name="context"/>.
        /// </returns>
        public BusSinkSlot GetOrAddSlot(InstanceId context)
        {
            Dictionary<InstanceId, object> map = byContext;
            if (map == null)
            {
                map = DxPools.InstanceIdDicts.Rent();
                byContext = map;
            }
            if (map.TryGetValue(context, out object boxed))
            {
                DebugAssertSlot(boxed);
                return DxUnsafe.As<BusSinkSlot>(boxed);
            }
            BusSinkSlot slot = new BusSinkSlot();
            map[context] = slot;
            return slot;
        }

        /// <summary>
        /// Remove the inner slot for the supplied context, if present. Returns
        /// <c>true</c> when an entry was removed. The removed inner slot is NOT
        /// reset by this method -- callers wanting to reclaim it should call
        /// <see cref="BusSinkSlot.Reset"/> on the returned reference before
        /// dropping it.
        /// </summary>
        /// <param name="context">The <see cref="InstanceId"/> context key.</param>
        /// <returns>
        /// <c>true</c> when the context was present in <see cref="byContext"/>.
        /// </returns>
        /// <remarks>
        /// The caller (the bus) is responsible for adjusting
        /// <see cref="liveCount"/> after a successful removal. This method
        /// intentionally does not touch <see cref="liveCount"/> so the bus can
        /// decide the right semantic at the call site (<see cref="InstanceId"/>
        /// keys vs handler sum -- see the <see cref="liveCount"/> field
        /// docstring).
        /// </remarks>
        public bool RemoveContext(InstanceId context)
        {
            Dictionary<InstanceId, object> map = byContext;
            if (map == null)
            {
                return false;
            }
            return map.Remove(context);
        }

        /// <summary>
        /// Full-reset semantic. Recursively clears every inner
        /// <see cref="BusSinkSlot"/> in place via
        /// <see cref="BusSinkSlot.Clear"/> (deeper than the legacy
        /// <c>HandlerCache&lt;TKey, TValue&gt;.Clear()</c> body, which relied
        /// on GC of dropped entries) and empties the outer map without
        /// returning it to the pool. Resets <see cref="version"/> to <c>0</c>;
        /// this is NOT
        /// monotonic and is intended only for the bus-wide
        /// <c>MessageBus.ResetState()</c> code path. Use <see cref="Reset"/>
        /// for sweep-driven slot reclamation.
        /// </summary>
        public void Clear()
        {
            Dictionary<InstanceId, object> map = byContext;
            if (map != null)
            {
                foreach (object boxed in map.Values)
                {
                    if (boxed == null)
                    {
                        continue;
                    }
                    DebugAssertSlot(boxed);
                    DxUnsafe.As<BusSinkSlot>(boxed).Clear();
                }
                map.Clear();
            }
            version = 0;
            liveCount = 0;
        }

        /// <summary>
        /// Eviction-driven reset. Walks every inner <see cref="BusSinkSlot"/>
        /// and calls <see cref="BusSinkSlot.Reset"/> on each. Inner pooled
        /// state must be drained BEFORE the outer map is recycled. Then returns
        /// <see cref="byContext"/> to the
        /// shared <see cref="DxPools.InstanceIdDicts"/> pool and nulls the
        /// field. Bumps <see cref="version"/> as the LAST step so any captured
        /// dispatch closure that observed the prior version detects
        /// invalidation. <see cref="lastTouchTicks"/> is intentionally
        /// preserved.
        /// </summary>
        public void Reset()
        {
            Dictionary<InstanceId, object> map = byContext;
            if (map != null)
            {
                foreach (object boxed in map.Values)
                {
                    if (boxed == null)
                    {
                        continue;
                    }
                    DebugAssertSlot(boxed);
                    DxUnsafe.As<BusSinkSlot>(boxed).Reset();
                }
                // Pool's onRecycled callback clears the dictionary before re-use.
                DxPools.InstanceIdDicts.Return(map);
                byContext = null;
            }
            liveCount = 0;
            unchecked
            {
                ++version;
            }
        }

        [Conditional("DEBUG")]
        private static void DebugAssertSlot(object boxed)
        {
            Debug.Assert(
                boxed is BusSinkSlot,
                "BusContextSlot.byContext must only contain BusSinkSlot values; "
                    + "DxUnsafe.As<BusSinkSlot> would otherwise produce undefined behavior."
            );
        }
    }

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
        /// Reserved counter intended to record the <see cref="version"/> value
        /// observed by the most recent dispatcher snapshot. Allocated for parity
        /// with the per-cache <see cref="BusSinkSlot.lastSeenVersion"/> contract.
        /// </summary>
        public long lastSeenVersion = -1;

        /// <summary>
        /// Reserved counter intended to record the bus emission id of the
        /// most recent dispatch that consumed this slot. Allocated for parity
        /// with the per-cache <see cref="BusSinkSlot.lastSeenEmissionId"/>
        /// contract.
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
