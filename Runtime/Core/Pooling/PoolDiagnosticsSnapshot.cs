namespace DxMessaging.Core.Pooling
{
    /// <summary>
    /// Aggregate snapshot of every <see cref="DxPools"/> pool. Returned by
    /// <see cref="DxPools.DescribeAll"/>. Pool names are stable, human-readable
    /// strings safe to log.
    /// </summary>
    internal readonly struct PoolDiagnosticsSnapshot
    {
        /// <summary><c>Dictionary&lt;InstanceId, object&gt;</c> pool diagnostics.</summary>
        public readonly CollectionPoolDiagnostics InstanceIdDicts;

        /// <summary><c>List&lt;InstanceId&gt;</c> pool diagnostics.</summary>
        public readonly CollectionPoolDiagnostics InstanceIdLists;

        /// <summary><c>HashSet&lt;InstanceId&gt;</c> pool diagnostics.</summary>
        public readonly CollectionPoolDiagnostics InstanceIdSets;

        /// <summary><c>List&lt;object&gt;</c> pool diagnostics.</summary>
        public readonly CollectionPoolDiagnostics ObjectLists;

        /// <summary><c>Stack&lt;object&gt;</c> pool diagnostics.</summary>
        public readonly CollectionPoolDiagnostics ObjectStacks;

        /// <summary><c>HashSet&lt;int&gt;</c> pool diagnostics.</summary>
        public readonly CollectionPoolDiagnostics IntSets;

        /// <summary>Typed handler <c>InstanceId -&gt; priority-cache</c> dictionary pool diagnostics.</summary>
        public readonly CollectionPoolDiagnostics TypedHandlerContextDicts;

        /// <summary>Typed handler priority-cache dictionary pool diagnostics.</summary>
        public readonly CollectionPoolDiagnostics TypedHandlerPriorityDicts;

        internal PoolDiagnosticsSnapshot(
            CollectionPoolDiagnostics instanceIdDicts,
            CollectionPoolDiagnostics instanceIdLists,
            CollectionPoolDiagnostics instanceIdSets,
            CollectionPoolDiagnostics objectLists,
            CollectionPoolDiagnostics objectStacks,
            CollectionPoolDiagnostics intSets,
            CollectionPoolDiagnostics typedHandlerContextDicts,
            CollectionPoolDiagnostics typedHandlerPriorityDicts
        )
        {
            InstanceIdDicts = instanceIdDicts;
            InstanceIdLists = instanceIdLists;
            InstanceIdSets = instanceIdSets;
            ObjectLists = objectLists;
            ObjectStacks = objectStacks;
            IntSets = intSets;
            TypedHandlerContextDicts = typedHandlerContextDicts;
            TypedHandlerPriorityDicts = typedHandlerPriorityDicts;
        }
    }
}
