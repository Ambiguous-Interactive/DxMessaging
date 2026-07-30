namespace DxMessaging.Core.Pooling
{
    /// <summary>
    /// Aggregate snapshot of every <see cref="DxPools"/> pool. Returned by
    /// <see cref="DxPools.DescribeAll"/>.
    /// </summary>
    internal readonly struct PoolDiagnosticsSnapshot
    {
        /// <summary><c>Dictionary&lt;int, object&gt;</c> context-slot pool diagnostics.</summary>
        public readonly CollectionPoolDiagnostics ContextSlotDicts;

        /// <summary><c>List&lt;int&gt;</c> dirty-context-ID pool diagnostics.</summary>
        public readonly CollectionPoolDiagnostics ContextIdLists;

        /// <summary><c>HashSet&lt;int&gt;</c> dirty-context-ID pool diagnostics.</summary>
        public readonly CollectionPoolDiagnostics ContextIdSets;

        /// <summary><c>List&lt;object&gt;</c> pool diagnostics.</summary>
        public readonly CollectionPoolDiagnostics ObjectLists;

        /// <summary><c>Stack&lt;object&gt;</c> pool diagnostics.</summary>
        public readonly CollectionPoolDiagnostics ObjectStacks;

        /// <summary><c>HashSet&lt;int&gt;</c> pool diagnostics.</summary>
        public readonly CollectionPoolDiagnostics IntSets;

        /// <summary>Typed handler <c>context ID -&gt; priority-cache</c> dictionary pool diagnostics.</summary>
        public readonly CollectionPoolDiagnostics TypedHandlerContextDicts;

        /// <summary>Typed handler priority-cache dictionary pool diagnostics.</summary>
        public readonly CollectionPoolDiagnostics TypedHandlerPriorityDicts;

        internal PoolDiagnosticsSnapshot(
            CollectionPoolDiagnostics contextSlotDicts,
            CollectionPoolDiagnostics contextIdLists,
            CollectionPoolDiagnostics contextIdSets,
            CollectionPoolDiagnostics objectLists,
            CollectionPoolDiagnostics objectStacks,
            CollectionPoolDiagnostics intSets,
            CollectionPoolDiagnostics typedHandlerContextDicts,
            CollectionPoolDiagnostics typedHandlerPriorityDicts
        )
        {
            ContextSlotDicts = contextSlotDicts;
            ContextIdLists = contextIdLists;
            ContextIdSets = contextIdSets;
            ObjectLists = objectLists;
            ObjectStacks = objectStacks;
            IntSets = intSets;
            TypedHandlerContextDicts = typedHandlerContextDicts;
            TypedHandlerPriorityDicts = typedHandlerPriorityDicts;
        }
    }
}
