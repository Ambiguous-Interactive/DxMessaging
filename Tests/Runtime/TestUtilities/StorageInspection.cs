namespace DxMessaging.Tests
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Reflection;
    using DxMessaging.Core;
    using DxMessaging.Core.DataStructure;
    using DxMessaging.Core.Helper;
    using DxMessaging.Core.Internal;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.MessageBus.Internal;
    using DxMessaging.Core.Messages;

    internal static partial class MessageBusInspection
    {
        private static object CacheValue<TMessage>(object cache)
            where TMessage : IMessage
        {
            Array values = (Array)ReadField(cache, "_values");
            int index = MessageHelperIndexer<TMessage>.SequentialId;
            return 0 <= index && index < values.Length ? values.GetValue(index) : null;
        }

        private static object ScalarPriorities<TMessage>(MessageBus bus)
            where TMessage : IMessage =>
            CacheValue<TMessage>(
                ((Array)ReadField(bus, "_scalarSinks")).GetValue(
                    BusSinkIndex.UntargetedHandleDefault
                )
            );

        private static object TargetMap<TMessage>(MessageBus bus)
            where TMessage : IMessage =>
            CacheValue<TMessage>(
                ((Array)ReadField(bus, "_contextSinks")).GetValue(
                    BusContextIndex.TargetedHandleDefault
                )
            );

        internal static bool TryObserveUntargetedPriorityStorageForBenchmark<TMessage>(
            this MessageBus bus,
            out PriorityStorageObservation observation
        )
            where TMessage : IUntargetedMessage
        {
            object priorities = ScalarPriorities<TMessage>(bus);
            if (priorities == null)
            {
                observation = default;
                return false;
            }
            object handlers = ReadField(priorities, "handlers");
            observation = new PriorityStorageObservation(
                ((IDictionary)handlers).Count,
                (int)
                    handlers
                        .GetType()
                        .GetMethod("EnsureCapacity")
                        .Invoke(handlers, new object[] { 0 }),
                (int)ReadProperty(ReadField(priorities, "order"), "Capacity")
            );
            return true;
        }

        internal static bool TryObserveUntargetedFlatSnapshotStorageForBenchmark<TMessage>(
            this MessageBus bus,
            out FlatSnapshotStorageObservation observation
        )
            where TMessage : IUntargetedMessage
        {
            object priorities = ScalarPriorities<TMessage>(bus);
            object state = priorities == null ? null : ReadField(priorities, "dispatchState");
            object snapshot = state == null ? null : ReadField(state, "active");
            object flat = snapshot == null ? null : ReadField(snapshot, "flat");
            if (flat == null)
            {
                observation = default;
                return false;
            }
            object pool = ReadField(flat.GetType(), "HolderPool");
            object gate = ReadField(flat.GetType(), "HolderPoolLock");
            int pooled;
            lock (gate)
            {
                pooled = (int)ReadProperty(pool, "Count");
            }
            observation = new FlatSnapshotStorageObservation(
                (int)ReadField(flat, "count"),
                ((Array)ReadField(flat, "entries")).Length,
                pooled
            );
            return true;
        }

        internal static bool TryObserveFastPriorityHandlerStorage<TMessage>(
            this MessageHandler handler,
            IMessageBus bus,
            int slotIndex,
            int priority,
            out HandlerCacheStorageObservation observation
        )
            where TMessage : IMessage
        {
            Dictionary<int, MessageCache<object>> byBus =
                (Dictionary<int, MessageCache<object>>)
                    ReadField(handler, "_handlersByTypeByMessageBus");
            if (
                !byBus.TryGetValue(
                    bus.RegisteredGlobalSequentialIndex,
                    out MessageCache<object> byType
                ) || !byType.TryGetValue<TMessage>(out object raw)
            )
            {
                observation = default;
                return false;
            }
            MessageHandler.TypedHandler<TMessage> typed =
                (MessageHandler.TypedHandler<TMessage>)raw;
            TypedSlot<TMessage> slot = typed._slots[slotIndex];
            if (
                slot == null
                || !slot.byPriority.TryGetValue(priority, out IHandlerActionCache erased)
                || erased
                    is not MessageHandler.HandlerActionCache<MessageHandler.FastHandler<TMessage>> cache
            )
            {
                observation = default;
                return false;
            }
            observation = new HandlerCacheStorageObservation(
                slot.byPriority.Count,
                0,
                slot.byPriority.EnsureCapacity(0),
                slot.orderedPriorities.Capacity,
                true,
                cache.entries.Count,
                EntryStorageInspection.InlineCapacity(cache.entries),
                EntryStorageInspection.MapCapacity(cache.entries),
                EntryStorageInspection.OrderCapacity(cache.entries),
                EntryStorageInspection.UsesSpillStorage(cache.entries)
            );
            return true;
        }

        internal static bool TryObserveTargetedHandleMapStorageForBenchmark<TMessage>(
            this MessageBus bus,
            out int entries,
            out int capacity
        )
            where TMessage : IMessage
        {
            object map = TargetMap<TMessage>(bus);
            entries = map == null ? 0 : (int)ReadProperty(map, "Count");
            capacity = map == null ? 0 : ((Array)ReadField(map, "_values")).Length;
            return map != null;
        }

        internal static bool TryObserveTargetedHandleMapTopologyForBenchmark<TMessage>(
            this MessageBus bus,
            InstanceId target,
            out IntKeyMapTopologyObservation observation
        )
            where TMessage : IMessage
        {
            object map = TargetMap<TMessage>(bus);
            observation = map == null ? default : MapInspection.Observe(map, target.Id);
            return map != null;
        }

        internal static bool TryObserveTargetedHandleMapLongestClusterForBenchmark<TMessage>(
            this MessageBus bus,
            out int longestCluster
        )
            where TMessage : IMessage
        {
            object map = TargetMap<TMessage>(bus);
            longestCluster = map == null ? 0 : MapInspection.LongestCluster(map);
            return map != null;
        }

        internal static object CreateContextMapSeedForBenchmark() => new object();

        internal static object CreatePopulatedContextMapForBenchmark(InstanceId[] keys, object seed)
        {
            if (keys == null)
            {
                throw new ArgumentNullException(nameof(keys));
            }
            IntKeyMap<object> map = new();
            for (int index = 0; index < keys.Length; ++index)
            {
                map[keys[index].Id] = seed;
            }
            return map;
        }

        internal static void ObserveContextMapForBenchmark(
            object opaqueMap,
            out int count,
            out int capacity
        )
        {
            IntKeyMap<object> map = (IntKeyMap<object>)opaqueMap;
            count = map.Count;
            capacity = MapInspection.Capacity(map);
        }
    }
}
