namespace DxMessaging.Tests
{
    internal readonly struct FlatSnapshotStorageObservation
    {
        internal FlatSnapshotStorageObservation(
            int entryCount,
            int arrayCapacity,
            int emptyHolderPoolCount
        )
        {
            EntryCount = entryCount;
            ArrayCapacity = arrayCapacity;
            EmptyHolderPoolCount = emptyHolderPoolCount;
        }

        internal int EntryCount { get; }

        internal int ArrayCapacity { get; }

        /// <summary>
        /// Released holder objects currently pooled with empty entry arrays. This does not
        /// measure arrays or retained bytes; ArrayPool memory retention requires an external
        /// memory measurement.
        /// </summary>
        internal int EmptyHolderPoolCount { get; }
    }

    internal readonly struct PriorityStorageObservation
    {
        internal PriorityStorageObservation(int entries, int mapCapacity, int orderCapacity)
        {
            Entries = entries;
            MapCapacity = mapCapacity;
            OrderCapacity = orderCapacity;
        }

        internal int Entries { get; }

        internal int MapCapacity { get; }

        internal int OrderCapacity { get; }
    }

    internal readonly struct ContextMapPoolBenchmarkObservation
    {
        internal ContextMapPoolBenchmarkObservation(
            object poolIdentity,
            object storageIdentity,
            bool useLru,
            int maxRetained
        )
        {
            PoolIdentity = poolIdentity;
            StorageIdentity = storageIdentity;
            UseLru = useLru;
            MaxRetained = maxRetained;
        }

        internal object PoolIdentity { get; }

        internal object StorageIdentity { get; }

        internal bool UseLru { get; }

        internal int MaxRetained { get; }
    }

    internal readonly struct HandlerCacheStorageObservation
    {
        internal HandlerCacheStorageObservation(
            int priorityEntries,
            int priorityInlineCapacity,
            int priorityMapCapacity,
            int priorityOrderCapacity,
            bool priorityUsesSpillStorage,
            int handlerEntries,
            int handlerInlineCapacity,
            int handlerMapCapacity,
            int handlerOrderCapacity,
            bool handlerUsesSpillStorage
        )
        {
            PriorityEntries = priorityEntries;
            PriorityInlineCapacity = priorityInlineCapacity;
            PriorityMapCapacity = priorityMapCapacity;
            PriorityOrderCapacity = priorityOrderCapacity;
            PriorityUsesSpillStorage = priorityUsesSpillStorage;
            HandlerEntries = handlerEntries;
            HandlerInlineCapacity = handlerInlineCapacity;
            HandlerMapCapacity = handlerMapCapacity;
            HandlerOrderCapacity = handlerOrderCapacity;
            HandlerUsesSpillStorage = handlerUsesSpillStorage;
        }

        internal int PriorityEntries { get; }

        internal int PriorityInlineCapacity { get; }

        internal int PriorityMapCapacity { get; }

        internal int PriorityOrderCapacity { get; }

        internal bool PriorityUsesSpillStorage { get; }

        internal int HandlerEntries { get; }

        internal int HandlerInlineCapacity { get; }

        internal int HandlerMapCapacity { get; }

        internal int HandlerOrderCapacity { get; }

        internal bool HandlerUsesSpillStorage { get; }
    }

    internal readonly struct IntKeyMapTopologyObservation
    {
        internal IntKeyMapTopologyObservation(
            bool found,
            int slotIndex,
            int lookupProbes,
            int deletionScans,
            int deletionMoves
        )
        {
            Found = found;
            SlotIndex = slotIndex;
            LookupProbes = lookupProbes;
            DeletionScans = deletionScans;
            DeletionMoves = deletionMoves;
        }

        internal bool Found { get; }

        internal int SlotIndex { get; }

        internal int LookupProbes { get; }

        internal int DeletionScans { get; }

        internal int DeletionMoves { get; }
    }
}
