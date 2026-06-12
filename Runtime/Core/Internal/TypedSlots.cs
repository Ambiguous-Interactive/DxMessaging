namespace DxMessaging.Core.Internal
{
    using System.Collections.Generic;
    using System.Runtime.CompilerServices;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus.Internal;
    using DxMessaging.Core.Pooling;

    /// <summary>
    /// Non-generic erasure interface over the per-delegate-shape
    /// <c>HandlerActionCache&lt;TDelegate&gt;</c> type currently nested in
    /// <see cref="MessageHandler"/>. Exposes only the metadata the staged
    /// dispatch + eviction layers need so the typed-handler-side slot grid
    /// (<see cref="TypedSlot{T}"/>, <see cref="TypedGlobalSlot"/>) can hold
    /// caches polymorphically across the four typed delegate shapes
    /// (<c>Action&lt;T&gt;</c>, <c>FastHandler&lt;T&gt;</c>,
    /// <c>Action&lt;InstanceId, T&gt;</c>,
    /// <c>FastHandlerWithContext&lt;T&gt;</c>) plus the six global
    /// non-generic shapes (3 kinds {<c>IUntargetedMessage</c>,
    /// <c>ITargetedMessage</c>, <c>IBroadcastMessage</c>} times 2 variants
    /// {<c>Default</c>, <c>Fast</c>}), 10 distinct shapes total.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>HandlerActionCache&lt;TDelegate&gt;</c> implements this interface
    /// explicitly; the explicit form keeps the public field
    /// shape on the nested cache type unchanged, so the public dispatch
    /// surface picks up no new members from the interface retrofit.
    /// </para>
    /// <para>
    /// Deliberately a thin, marker-style surface: only the five members that
    /// staged dispatch (<see cref="Version"/>, <see cref="LastSeenVersion"/>,
    /// <see cref="LastSeenEmissionId"/>) and eviction
    /// (<see cref="IsEmpty"/>, <see cref="Reset"/>) require. The
    /// <c>entries</c> dictionary and <c>cache</c> list are NOT exposed
    /// because their generic shape is the very thing this interface erases;
    /// dispatchers that need the typed cache down-cast at the call site.
    /// </para>
    /// </remarks>
    internal interface IHandlerActionCache
    {
        /// <summary>
        /// Strictly monotonic version counter for the cache's structural
        /// state. Mirrors <c>HandlerActionCache&lt;TDelegate&gt;.version</c>.
        /// Read-only on this surface; bumped internally by the cache's own
        /// register / deregister sites and by <see cref="Reset"/>.
        /// </summary>
        long Version { get; }

        /// <summary>
        /// The <see cref="Version"/> value observed by the most recent
        /// dispatcher snapshot. Mirrors
        /// <c>HandlerActionCache&lt;TDelegate&gt;.lastSeenVersion</c> and is
        /// mutated by the staged dispatch path to detect when the flat cache
        /// list needs to be re-materialised.
        /// </summary>
        long LastSeenVersion { get; set; }

        /// <summary>
        /// The bus emission id of the most recent dispatch that consumed
        /// this cache. Mirrors
        /// <c>HandlerActionCache&lt;TDelegate&gt;.lastSeenEmissionId</c>.
        /// Used by the staged dispatch staleness check. Implementations use
        /// an invalid sentinel before the first dispatch so emission id 0
        /// still materializes an initial snapshot.
        /// </summary>
        long LastSeenEmissionId { get; set; }

        /// <summary>
        /// True iff the cache currently retains zero entries. Cheap (single
        /// integer compare against <c>entries.Count</c>); used by the
        /// eviction sweep so empty caches can be reclaimed without walking
        /// inner state.
        /// </summary>
        bool IsEmpty { get; }

        /// <summary>
        /// Eviction-driven full clear. Empties the entries dictionary and
        /// the flat cache list, resets <see cref="LastSeenVersion"/> /
        /// <see cref="LastSeenEmissionId"/>,
        /// and bumps <see cref="Version"/> as the LAST step so any captured
        /// dispatch closure that observed the prior version detects
        /// invalidation. Idempotent.
        /// </summary>
        void Reset();
    }

    /// <summary>
    /// Non-generic sweep surface for <c>MessageHandler.TypedHandler&lt;T&gt;</c>.
    /// The owning <see cref="MessageHandler"/> stores typed handlers in a
    /// <c>MessageCache&lt;object&gt;</c>, so external reclamation code needs an
    /// erased entry point that can reset empty typed slots without reflection.
    /// </summary>
    internal interface ITypedHandlerSlotSweeper
    {
        /// <summary>
        /// Message type index for the owning typed-handler wrapper.
        /// </summary>
        int MessageTypeIndex { get; }

        /// <summary>
        /// True when the last sweep found no live typed slots worth
        /// retaining and the owning <c>MessageCache</c> entry can be
        /// removed.
        /// </summary>
        bool MarkedForOuterRemoval { get; }

        /// <summary>
        /// Resets every empty typed or typed-global slot and removes it from
        /// the handler's slot arrays.
        /// </summary>
        /// <returns>Number of slots reset.</returns>
        int ResetEmptySlotsForSweep();

        /// <summary>
        /// Resets every typed or typed-global slot and removes it from the
        /// handler's slot arrays.
        /// </summary>
        /// <returns>Number of slots reset.</returns>
        int ResetAllSlotsForBusReset();

        /// <summary>
        /// Counts empty typed or typed-global slots still occupying memory and
        /// eligible for a sweep reset.
        /// </summary>
        /// <returns>Number of empty slots still allocated.</returns>
        int CountEmptySlotsForSweep();
    }

    /// <summary>
    /// Per-message-type, per-<see cref="SlotKey"/> dispatch slot on the
    /// typed-handler side. Mirrors the role of
    /// <see cref="BusSinkSlot"/> on the bus side: holds a priority-keyed map
    /// of <see cref="IHandlerActionCache"/>s plus the snapshot-friendly
    /// ordered-priority list, and tracks the staged-dispatch / eviction
    /// counters.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This type is sealed because the current slot layout uses one
    /// type-erased cache shape for every delegate variant. A future
    /// delegate-specific fast path can promote the type to <c>abstract</c>
    /// together with its concrete subclasses, without affecting the current
    /// storage contract.
    /// </para>
    /// <para>
    /// The context requirement is represented as a readonly field
    /// (<see cref="requiresContext"/>) set via the constructor. The field is
    /// <c>true</c> for
    /// slots whose <see cref="SlotKey"/> resolves to a
    /// <see cref="DispatchVariant"/> that carries an
    /// <see cref="InstanceId"/> recipient or source (Targeted / Broadcast,
    /// excluding the <c>WithoutContext</c> variants), and <c>false</c>
    /// otherwise.
    /// </para>
    /// <para>
    /// <see cref="TypedHandler{T}"/> routes storage through this slot;
    /// the legacy named fields were deleted and the
    /// <c>_slots[<see cref="TypedSlotIndex.Length"/>]</c> array the
    /// storage owner.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">
    /// The strongly-typed message contract this slot's parent
    /// <see cref="TypedHandler{T}"/> binds to. The slot itself does not
    /// reference <typeparamref name="T"/> directly today (the type-erased
    /// <see cref="IHandlerActionCache"/> handles the per-delegate generic
    /// shapes) -- the parameter is carried so typed storage can add a
    /// concrete cache reference here without an additional
    /// generic re-parameterization.
    /// </typeparam>
    internal sealed class TypedSlot<T> : IEvictableSlot
        where T : IMessage
    {
        /// <summary>
        /// Per-priority handler caches keyed by priority value.
        /// <see cref="TypedHandler{T}"/> routes non-context storage through
        /// this slot.
        /// </summary>
        public readonly Dictionary<int, IHandlerActionCache> byPriority = new();

        /// <summary>
        /// Insertion-ordered list of priority keys present in
        /// <see cref="byPriority"/>. Mirrors the legacy ordered-priority
        /// list used by the staged dispatch snapshot pattern.
        /// </summary>
        public readonly List<int> orderedPriorities = new();

        /// <summary>Monotonic version counter for the slot's structural state.</summary>
        public long version;

        /// <summary>
        /// The <see cref="version"/> value observed by the most recent
        /// dispatcher snapshot. Used to decide whether the cache list needs
        /// to be re-materialised before the next dispatch.
        /// </summary>
        public long lastSeenVersion = -1;

        /// <summary>
        /// The bus emission id of the most recent dispatch that consumed
        /// this slot. Used by the staged dispatch staleness check.
        /// </summary>
        public long lastSeenEmissionId = -1;

        /// <summary>
        /// Bus tick counter value at the most recent register / deregister /
        /// emit that touched this slot. Maintained by the sweep touch hook;
        /// preserved across <see cref="Clear"/> and <see cref="Reset"/>
        /// so the sweep can distinguish freshly-reset slots from
        /// never-touched slots.
        /// </summary>
        public long lastTouchTicks;

        /// <summary>
        /// Reserved live-handler counter intended to mirror the unique
        /// (handler, priority) pair count across every entry in
        /// <see cref="byPriority"/>; for context-bound slots, the SUM of
        /// (handler, priority) pair counts across every (InstanceId,
        /// priority) leaf in <see cref="byContext"/> -- matching
        /// <see cref="BusSinkSlot.liveCount"/> semantics so eviction logic
        /// does not diverge between bus-side and handler-side.
        /// <see cref="IsEmpty"/> is a single integer compare. The typed
        /// handler maintains this counter on first-registration and
        /// final-deregistration transitions so <see cref="IsEmpty"/>
        /// reflects whether the slot still owns live handlers.
        /// </summary>
        public int liveCount;

        /// <summary>
        /// True iff this slot's <see cref="SlotKey"/> resolves to a
        /// dispatch variant that carries an <see cref="InstanceId"/>
        /// recipient or source (the non-<c>WithoutContext</c> Targeted and
        /// Broadcast variants). When <c>true</c>, context-bound storage uses
        /// <see cref="byContext"/>; when <c>false</c>, storage flows through
        /// <see cref="byPriority"/> directly.
        /// </summary>
        /// <remarks>
        /// Kept as a readonly field because <see cref="TypedSlot{T}"/> is
        /// sealed (see class remarks).
        /// </remarks>
        public readonly bool requiresContext;

        /// <summary>
        /// Inner per-context map for context-bound slots. Null unless
        /// <see cref="requiresContext"/> is <c>true</c> AND at least one
        /// context has been registered.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Lifetime semantic: <see cref="Clear"/> and <see cref="Reset"/>
        /// return the outer context dictionary and every inner priority
        /// dictionary to <see cref="DxPools"/> before nulling the field.
        /// </para>
        /// <para>
        /// Unlike the bus-side <see cref="BusContextSlot.byContext"/>, which
        /// is rented from <c>DxPools.InstanceIdDicts</c> as a
        /// <c>Dictionary&lt;InstanceId, object&gt;</c> (boxed
        /// <see cref="BusSinkSlot"/>) for cross-message-type pool sharing,
        /// the typed-handler-side equivalent here is a strongly-typed
        /// <c>Dictionary&lt;InstanceId, Dictionary&lt;int, IHandlerActionCache&gt;&gt;</c>.
        /// Both the outer context dictionary and the inner priority
        /// dictionaries are rented from typed-handler-specific
        /// <see cref="DxPools"/> pools.
        /// </para>
        /// <para>
        /// Shape: <c>InstanceId -&gt; (priority -&gt; IHandlerActionCache)</c>,
        /// with the leaf cache type-erased to <see cref="IHandlerActionCache"/>.
        /// The inner dictionary is keyed by priority, matching the legacy
        /// <c>Dictionary&lt;InstanceId, Dictionary&lt;int, HandlerActionCache&lt;TDelegate&gt;&gt;&gt;</c>
        /// layout on <c>MessageHandler.TypedHandler&lt;T&gt;</c>. The flat
        /// 3-level shape was chosen over the alternatives (extend
        /// <see cref="IHandlerActionCache"/> with per-priority buckets, or
        /// recurse with <c>Dictionary&lt;InstanceId, TypedSlot&lt;T&gt;&gt;</c>)
        /// because it preserves the legacy storage layout exactly and keeps
        /// call sites aligned with the existing shape. The monotonic-version
        /// drain contract on
        /// <see cref="Reset"/> requires every inner cache to be drained through
        /// <see cref="IHandlerActionCache.Reset"/> before the outer
        /// container is cleared.
        /// </para>
        /// </remarks>
        public Dictionary<InstanceId, Dictionary<int, IHandlerActionCache>> byContext;

        /// <summary>
        /// Constructs a <see cref="TypedSlot{T}"/> with the supplied
        /// context-binding flag. All other fields take their default
        /// initial values.
        /// </summary>
        /// <param name="requiresContext">
        /// Value for <see cref="TypedSlot{T}.requiresContext"/>; see that
        /// field's remarks for the semantic.
        /// </param>
        public TypedSlot(bool requiresContext)
        {
            this.requiresContext = requiresContext;
        }

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
        /// Full-reset semantic. Empties <see cref="byPriority"/> and
        /// <see cref="orderedPriorities"/>, nulls out
        /// <see cref="byContext"/>, and resets the staged-dispatch
        /// counters. Resets <see cref="version"/> to <c>0</c>; this is NOT
        /// monotonic and is intended only for the typed-handler analog of
        /// <c>MessageBus.ResetState()</c> if and when that code path is
        /// wired up. Use <see cref="Reset"/> for sweep-driven slot
        /// reclamation.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Mirrors <see cref="BusSinkSlot.Clear"/>. <see cref="byContext"/>
        /// returns <see cref="byContext"/> and its inner priority
        /// dictionaries to <see cref="DxPools"/>.
        /// </para>
        /// <para>
        /// Unlike <see cref="BusSinkSlot.Clear"/> which drains inner buckets,
        /// this <see cref="Clear"/> drops references only -- <see cref="Clear"/>
        /// is intended for the typed-handler analog of
        /// <c>MessageBus.ResetState()</c> where the entire
        /// <see cref="MessageHandler"/> graph is being torn down, so per-cache
        /// drain would be redundant work. <see cref="Reset"/> (eviction-driven)
        /// DOES drain inner caches via <see cref="IHandlerActionCache.Reset"/>
        /// because outer-version invalidation alone is insufficient when the
        /// slot is being re-used after sweep.
        /// </para>
        /// </remarks>
        public void Clear()
        {
            byPriority.Clear();
            orderedPriorities.Clear();
            ReturnContextDictionaries();
            byContext = null;
            version = 0;
            lastSeenVersion = -1;
            lastSeenEmissionId = -1;
            liveCount = 0;
        }

        /// <summary>
        /// Eviction-driven reset. Drains every inner
        /// <see cref="IHandlerActionCache"/> held by <see cref="byPriority"/>
        /// and <see cref="byContext"/> through
        /// <see cref="IHandlerActionCache.Reset"/> first, then clears the
        /// outer containers, then bumps <see cref="version"/> as the LAST
        /// step so any captured dispatch closure that observed the prior
        /// version detects invalidation.
        /// <see cref="lastTouchTicks"/> is intentionally preserved so the
        /// sweep can distinguish freshly-reset slots from never-touched
        /// ones.
        /// </summary>
        /// <remarks>
        /// Drain order is load-bearing: inner caches must be reset (and their
        /// own monotonic versions bumped)
        /// BEFORE the outer container is cleared, so any captured dispatch
        /// closure observing an inner cache detects invalidation regardless
        /// of whether the outer reference is still reachable. The outer
        /// <see cref="version"/> bump is the LAST statement in the method
        /// for the same reason at the slot level.
        /// </remarks>
        public void Reset()
        {
            // Inline the structural-clear body of Clear(); do NOT call
            // Clear() because that resets version=0 and would break the
            // monotonic invariant the eviction layer depends on: stale
            // deregister closures captured before reset must observe a
            // strictly larger version after reset and skip their work.
            // Per-cache drain BEFORE the structural clear: every
            // IHandlerActionCache.Reset() bumps its own version internally,
            // so closures captured against the inner cache also detect
            // invalidation -- not just closures captured against the slot.
            foreach (KeyValuePair<int, IHandlerActionCache> kv in byPriority)
            {
                kv.Value?.Reset();
            }
            if (byContext != null)
            {
                foreach (
                    KeyValuePair<InstanceId, Dictionary<int, IHandlerActionCache>> ctx in byContext
                )
                {
                    if (ctx.Value == null)
                    {
                        continue;
                    }
                    foreach (KeyValuePair<int, IHandlerActionCache> kv in ctx.Value)
                    {
                        kv.Value?.Reset();
                    }
                }
            }
            byPriority.Clear();
            orderedPriorities.Clear();
            ReturnContextDictionaries();
            byContext = null;
            lastSeenVersion = -1;
            lastSeenEmissionId = -1;
            liveCount = 0;
            unchecked
            {
                ++version;
            }
        }

        private void ReturnContextDictionaries()
        {
            if (byContext == null)
            {
                return;
            }

            foreach (
                KeyValuePair<InstanceId, Dictionary<int, IHandlerActionCache>> ctx in byContext
            )
            {
                DxPools.TypedHandlerPriorityDicts.Return(ctx.Value);
            }
            DxPools.TypedHandlerContextDicts.Return(byContext);
        }
    }

    /// <summary>
    /// Per-message-type accept-all slot on the typed-handler side. Mirrors
    /// the role of <see cref="BusGlobalSlot"/> on the bus side: holds a
    /// single type-erased cache for the slot's
    /// (<see cref="DispatchKind"/>, <see cref="DispatchVariant"/>)
    /// coordinate and the staged-dispatch / eviction counters.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The typed handler holds an array of 6 <see cref="TypedGlobalSlot"/>
    /// entries. The per-(<see cref="DispatchKind"/>,
    /// <see cref="DispatchVariant"/>) indexing scheme maps the six global
    /// flavours (3 kinds {<c>Untargeted</c>, <c>Targeted</c>,
    /// <c>Broadcast</c>} times 2 variants {<c>Default</c>, <c>Fast</c>}) to
    /// array slots and is committed in <see cref="TypedGlobalSlotIndex"/>
    /// (sibling file in the same folder). This type defines the per-slot
    /// shape; the index file owns the layout decision.
    /// <see cref="TypedHandler{T}"/> routes global storage through this type.
    /// </para>
    /// <para>
    /// Non-generic by design: the typed handler's six legacy global
    /// fields each carry a different non-generic delegate shape
    /// (<c>Action&lt;IUntargetedMessage&gt;</c>,
    /// <c>FastHandler&lt;IUntargetedMessage&gt;</c>,
    /// <c>Action&lt;InstanceId, ITargetedMessage&gt;</c>, etc.), and the
    /// bus-side <see cref="BusGlobalSlot"/> mirrors this. The single
    /// <see cref="cache"/> field holds an erased
    /// <see cref="IHandlerActionCache"/> whose concrete generic shape is
    /// determined by the slot's coordinate; dispatchers down-cast at the
    /// call site.
    /// </para>
    /// <para>
    /// Single <see cref="cache"/> field intentionally -- not three like
    /// <see cref="BusGlobalSlot"/>'s
    /// <c>untargetedDispatchState</c> / <c>targetedDispatchState</c> /
    /// <c>broadcastDispatchState</c> trio. The typed-handler-side global
    /// array is the per-kind-and-variant fan-out (six slots), so each
    /// slot already corresponds to a single kind+variant coordinate and
    /// holds exactly one cache.
    /// </para>
    /// </remarks>
    internal sealed class TypedGlobalSlot : IEvictableSlot
    {
        /// <summary>
        /// Type-erased handler cache for this slot's
        /// (<see cref="DispatchKind"/>, <see cref="DispatchVariant"/>)
        /// coordinate. Lazy alloc on first registration; nulled by
        /// <see cref="Clear"/> and <see cref="Reset"/>.
        /// </summary>
        public IHandlerActionCache cache;

        /// <summary>Monotonic version counter for the slot's structural state.</summary>
        public long version;

        /// <summary>
        /// The <see cref="version"/> value observed by the most recent
        /// dispatcher snapshot.
        /// </summary>
        public long lastSeenVersion = -1;

        /// <summary>
        /// The bus emission id of the most recent dispatch that consumed
        /// this slot.
        /// </summary>
        public long lastSeenEmissionId = -1;

        /// <summary>
        /// Bus tick counter value at the most recent register / deregister /
        /// emit that touched this slot. Maintained by the sweep touch hook;
        /// preserved across <see cref="Clear"/> and
        /// <see cref="Reset"/>.
        /// </summary>
        public long lastTouchTicks;

        /// <summary>
        /// Reserved live-handler counter intended to mirror the entry count
        /// of <see cref="cache"/> at every stable observation point so
        /// <see cref="IsEmpty"/> is a single integer compare rather than a
        /// dispatch through the type-erased
        /// <see cref="IHandlerActionCache.IsEmpty"/> property. The typed
        /// handler maintains this counter on first-registration and
        /// final-deregistration transitions so <see cref="IsEmpty"/>
        /// reflects whether the slot still owns live global handlers.
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
        /// Full-reset semantic. Nulls out <see cref="cache"/> and resets
        /// the staged-dispatch counters. Resets <see cref="version"/> to
        /// <c>0</c>; this is NOT monotonic and is intended only for the
        /// typed-handler analog of <c>MessageBus.ResetState()</c> if and
        /// when that code path is wired up. Use <see cref="Reset"/> for
        /// sweep-driven slot reclamation.
        /// </summary>
        public void Clear()
        {
            cache = null;
            version = 0;
            lastSeenVersion = -1;
            lastSeenEmissionId = -1;
            liveCount = 0;
        }

        /// <summary>
        /// Eviction-driven reset. Drains <see cref="cache"/> through
        /// <see cref="IHandlerActionCache.Reset"/> first, then nulls the
        /// reference, then bumps <see cref="version"/> as the LAST step so
        /// any captured dispatch closure that observed the prior version
        /// detects invalidation.
        /// <see cref="lastTouchTicks"/> is intentionally preserved.
        /// </summary>
        /// <remarks>
        /// Drain order is load-bearing: the inner cache's own monotonic
        /// version is bumped BEFORE the slot
        /// drops the reference, so closures captured against the inner
        /// cache also detect invalidation. The outer <see cref="version"/>
        /// bump is the LAST statement in the method for the same reason at
        /// the slot level.
        /// </remarks>
        public void Reset()
        {
            // Inline the structural-clear body of Clear(); do NOT call
            // Clear() because that resets version=0 and would break the
            // monotonic invariant the eviction layer depends on.
            cache?.Reset();
            cache = null;
            lastSeenVersion = -1;
            lastSeenEmissionId = -1;
            liveCount = 0;
            unchecked
            {
                ++version;
            }
        }
    }
}
