#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests
{
    using System;
    using System.Collections;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Core.Pooling;

    internal static partial class MessageBusInspection
    {
        internal static int ReflexiveMethodCacheCount(MessageBus bus) =>
            ((IDictionary)ReadField(bus, "_methodCache")).Count;

        internal static int ReflexiveMethodCacheCapacity(MessageBus bus)
        {
            object cache = ReadField(bus, "_methodCache");
            return (int)
                cache.GetType().GetMethod("EnsureCapacity").Invoke(cache, new object[] { 0 });
        }

        internal static bool IsReflexiveMethodCached(
            this MessageBus bus,
            Type componentType,
            MethodSignatureKey signature
        ) => ((IDictionary)ReadField(bus, "_methodCache")).Contains((componentType, signature));

        internal static bool HasRetainedReflexiveDispatchState(MessageBus bus) =>
            ReadField(bus, "_reflexiveDispatchState") != null;

        internal static int ReflexiveDispatchRetainedCapacity(MessageBus bus)
        {
            object state = ReadField(bus, "_reflexiveDispatchState");
            return state == null ? 0 : (int)ReadProperty(state, "RetainedCapacity");
        }

        internal static CollectionPoolDiagnostics ReflexiveDispatchPoolDiagnostics(MessageBus bus)
        {
            object pool = ReadField(bus, "_reflexiveDispatchStatePool");
            return pool == null
                ? default
                : (CollectionPoolDiagnostics)
                    pool.GetType().GetMethod("Snapshot").Invoke(pool, null);
        }

        internal static int ReflexiveDispatchPoolMaxRetained(MessageBus bus)
        {
            object pool = ReadField(bus, "_reflexiveDispatchStatePool");
            return pool == null
                ? (int)ReadField(bus, "_handlerCacheRetentionLimit")
                : (int)ReadProperty(pool, "MaxRetained");
        }

        internal static bool ReflexiveDispatchPoolUsesLru(MessageBus bus)
        {
            object pool =
                ReadField(bus, "_reflexiveDispatchStatePool")
                ?? ReadProperty(typeof(MessageBus), "ContextHandlerByTargetDicts");
            return (bool)ReadProperty(pool, "UseLru");
        }
    }
}
#endif
