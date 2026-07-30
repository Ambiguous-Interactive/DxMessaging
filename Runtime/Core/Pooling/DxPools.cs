namespace DxMessaging.Core.Pooling
{
    using System.Collections.Generic;
    using DxMessaging.Core.Internal;
#if UNITY_2021_3_OR_NEWER
    using Configuration;
#endif

    /// <summary>
    /// Static, single-threaded pool registry shared by the bus and handler
    /// subsystems. Instantiated lazily on first use; reconfigured by
    /// <see cref="Configure"/> when the runtime settings asset changes.
    /// </summary>
    /// <remarks>
    /// Each pool returns the per-entry recycle action that clears the entry
    /// before re-use, so callers do not need to remember to <c>Clear()</c>
    /// before <c>Return</c>. <c>OnEvicted</c> currently does nothing -- entries
    /// are dropped on cap overflow and the GC reclaims them.
    /// </remarks>
    internal static class DxPools
    {
        // Mirrors DxMessagingRuntimeSettings.DefaultBufferMaxDistinctEntries; updated by Configure().
        // Kept as a local constant so DxPools' field initializers don't depend on Unity types.
        private const int DefaultMaxRetained = 512;

        internal static readonly CollectionPool<Dictionary<InstanceId, object>> InstanceIdDicts =
            new(
                maxRetained: DefaultMaxRetained,
                useLru: true,
                factory: () => new Dictionary<InstanceId, object>(),
                onRecycled: dict => dict.Clear()
            );

        internal static readonly CollectionPool<List<InstanceId>> InstanceIdLists = new(
            maxRetained: DefaultMaxRetained,
            useLru: true,
            factory: () => new List<InstanceId>(),
            onRecycled: list => list.Clear()
        );

        internal static readonly CollectionPool<HashSet<InstanceId>> InstanceIdSets = new(
            maxRetained: DefaultMaxRetained,
            useLru: true,
            factory: () => new HashSet<InstanceId>(),
            onRecycled: set => set.Clear()
        );

        internal static readonly CollectionPool<List<object>> ObjectLists = new(
            maxRetained: DefaultMaxRetained,
            useLru: true,
            factory: () => new List<object>(),
            onRecycled: list => list.Clear()
        );

        internal static readonly CollectionPool<Stack<object>> ObjectStacks = new(
            maxRetained: DefaultMaxRetained,
            useLru: true,
            factory: () => new Stack<object>(),
            onRecycled: stack => stack.Clear()
        );

        internal static readonly CollectionPool<HashSet<int>> IntSets = new(
            maxRetained: DefaultMaxRetained,
            useLru: true,
            factory: () => new HashSet<int>(),
            onRecycled: set => set.Clear()
        );

        internal static readonly CollectionPool<
            Dictionary<InstanceId, Dictionary<int, IHandlerActionCache>>
        > TypedHandlerContextDicts = new(
            maxRetained: DefaultMaxRetained,
            useLru: true,
            factory: () => new Dictionary<InstanceId, Dictionary<int, IHandlerActionCache>>(),
            onRecycled: dict => dict.Clear()
        );

        internal static readonly CollectionPool<
            Dictionary<int, IHandlerActionCache>
        > TypedHandlerPriorityDicts = new(
            maxRetained: DefaultMaxRetained,
            useLru: true,
            factory: () => new Dictionary<int, IHandlerActionCache>(),
            onRecycled: dict => dict.Clear()
        );

        /// <summary>
        /// Trim every pool. <paramref name="force"/> drains pools to zero;
        /// otherwise each pool is trimmed to its current cap (a no-op unless the
        /// cap was lowered). Returns total evicted count.
        /// </summary>
        internal static int TrimAll(bool force)
        {
            int evicted = 0;
            evicted += InstanceIdDicts.Trim(force ? 0 : InstanceIdDicts.MaxRetained);
            evicted += InstanceIdLists.Trim(force ? 0 : InstanceIdLists.MaxRetained);
            evicted += InstanceIdSets.Trim(force ? 0 : InstanceIdSets.MaxRetained);
            evicted += ObjectLists.Trim(force ? 0 : ObjectLists.MaxRetained);
            evicted += ObjectStacks.Trim(force ? 0 : ObjectStacks.MaxRetained);
            evicted += IntSets.Trim(force ? 0 : IntSets.MaxRetained);
            evicted += TypedHandlerContextDicts.Trim(
                force ? 0 : TypedHandlerContextDicts.MaxRetained
            );
            evicted += TypedHandlerPriorityDicts.Trim(
                force ? 0 : TypedHandlerPriorityDicts.MaxRetained
            );
            return evicted;
        }

#if UNITY_2021_3_OR_NEWER
        /// <summary>
        /// Re-apply caps from the supplied settings. Lowering a cap immediately
        /// trims; raising a cap takes effect on subsequent returns.
        /// </summary>
        internal static void Configure(DxMessagingRuntimeSettings settings)
        {
            if (settings == null)
            {
                throw new System.ArgumentNullException(nameof(settings));
            }
            int cap = settings.BufferMaxDistinctEntries;
            bool useLru = settings.BufferUseLruEviction;
            InstanceIdDicts.UseLru = useLru;
            InstanceIdLists.UseLru = useLru;
            InstanceIdSets.UseLru = useLru;
            ObjectLists.UseLru = useLru;
            ObjectStacks.UseLru = useLru;
            IntSets.UseLru = useLru;
            TypedHandlerContextDicts.UseLru = useLru;
            TypedHandlerPriorityDicts.UseLru = useLru;
            InstanceIdDicts.MaxRetained = cap;
            InstanceIdLists.MaxRetained = cap;
            InstanceIdSets.MaxRetained = cap;
            ObjectLists.MaxRetained = cap;
            ObjectStacks.MaxRetained = cap;
            IntSets.MaxRetained = cap;
            TypedHandlerContextDicts.MaxRetained = cap;
            TypedHandlerPriorityDicts.MaxRetained = cap;
        }
#endif

        /// <summary>Aggregate snapshot of every pool's diagnostics.</summary>
        internal static PoolDiagnosticsSnapshot DescribeAll()
        {
            return new PoolDiagnosticsSnapshot(
                InstanceIdDicts.Snapshot(),
                InstanceIdLists.Snapshot(),
                InstanceIdSets.Snapshot(),
                ObjectLists.Snapshot(),
                ObjectStacks.Snapshot(),
                IntSets.Snapshot(),
                TypedHandlerContextDicts.Snapshot(),
                TypedHandlerPriorityDicts.Snapshot()
            );
        }
    }
}
