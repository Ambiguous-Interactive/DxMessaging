namespace DxMessaging.Tests
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.Helper;
    using DxMessaging.Core.Internal;
    using DxMessaging.Core.MessageBus;

    internal static class EntryStorageInspection
    {
        internal static int InlineCapacity(object entries) =>
            (int)MessageBusInspection.ReadField(entries.GetType(), "PhysicalInlineCapacity");

        internal static int MapCapacity(object entries)
        {
            object map = MessageBusInspection.ReadField(entries, "_spillMap");
            return map == null
                ? 0
                : (int)map.GetType().GetMethod("EnsureCapacity").Invoke(map, new object[] { 0 });
        }

        internal static int OrderCapacity(object entries)
        {
            object order = MessageBusInspection.ReadField(entries, "_spillOrder");
            return order == null ? 0 : (int)MessageBusInspection.ReadProperty(order, "Capacity");
        }

        internal static int OrderSlotCount(object entries)
        {
            object order = MessageBusInspection.ReadField(entries, "_spillOrder");
            return order == null
                ? (int)MessageBusInspection.ReadField(entries, "_inlineCount")
                : ((ICollection)order).Count;
        }

        internal static int SkippedPrefixCount(object entries) =>
            (int)MessageBusInspection.ReadField(entries, "_spillOrderStart");

        internal static bool UsesSpillStorage(object entries) =>
            MessageBusInspection.ReadField(entries, "_spillMap") != null;

        internal static void GetRetainedStorageCounts(
            this MessageHandler handler,
            IMessageBus bus,
            out int contextKeys,
            out int priorityCaches
        )
        {
            contextKeys = 0;
            priorityCaches = 0;
            if (bus == null)
            {
                bus = (IMessageBus)
                    typeof(MessageHandler)
                        .GetMethod(
                            "ResolveMessageBus",
                            System.Reflection.BindingFlags.Instance
                                | System.Reflection.BindingFlags.NonPublic
                        )
                        .Invoke(handler, new object[] { null });
            }
            if (
                !handler._handlersByTypeByMessageBus.TryGetValue(
                    bus.RegisteredGlobalSequentialIndex,
                    out MessageCache<object> byType
                )
            )
            {
                return;
            }
            foreach (object typed in byType)
            {
                if (typed is not ITypedHandlerSlotSweeper)
                {
                    continue;
                }
                foreach (object slot in (Array)MessageBusInspection.ReadField(typed, "_slots"))
                {
                    if (slot == null)
                    {
                        continue;
                    }
                    priorityCaches += (
                        (IDictionary)MessageBusInspection.ReadField(slot, "byPriority")
                    ).Count;
                    IDictionary byContext = (IDictionary)
                        MessageBusInspection.ReadField(slot, "byContext");
                    if (byContext == null)
                    {
                        continue;
                    }
                    contextKeys += byContext.Count;
                    foreach (IDictionary priorities in byContext.Values)
                    {
                        priorityCaches += priorities.Count;
                    }
                }
            }
        }
    }
}
