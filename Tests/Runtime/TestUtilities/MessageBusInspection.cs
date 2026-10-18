using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("WallstopStudios.DxMessaging.Tests.Editor")]
[assembly: InternalsVisibleTo("WallstopStudios.DxMessaging.Tests.Editor.Allocations")]
[assembly: InternalsVisibleTo("WallstopStudios.DxMessaging.Tests.00.Runtime.Benchmarks")]

namespace DxMessaging.Tests
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Reflection;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Pooling;

    /// <summary>
    /// Cold inspection of real bus storage. Keep these allocating queries outside measured regions.
    /// </summary>
    internal static partial class MessageBusInspection
    {
        private const BindingFlags Members =
            BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.DeclaredOnly;

        public static object ReadField(object owner, string name)
        {
            Type type = owner as Type ?? owner.GetType();
            for (Type current = type; current != null; current = current.BaseType)
            {
                FieldInfo field = current.GetField(name, Members);
                if (field != null)
                {
                    return field.GetValue(owner is Type ? null : owner);
                }
            }
            throw new MissingFieldException(type.FullName, name);
        }

        public static object ReadProperty(object owner, string name)
        {
            Type type = owner as Type ?? owner.GetType();
            for (Type current = type; current != null; current = current.BaseType)
            {
                PropertyInfo property = current.GetProperty(name, Members);
                if (property != null)
                {
                    return property.GetValue(owner is Type ? null : owner);
                }
            }
            throw new MissingMemberException(type.FullName, name);
        }

        public static object RecycledHandlerCache(MessageBus bus) =>
            ReadField(bus, "_recycledEmptyHandlerCache");

        public static int RecycledHandlerCacheHighWater(MessageBus bus)
        {
            object cache = RecycledHandlerCache(bus);
            return cache == null ? 0 : (int)ReadField(cache, "highWaterDistinctHandlers");
        }

        public static CollectionPoolDiagnostics ContextMapPoolDiagnostics()
        {
            object pool = ReadProperty(typeof(MessageBus), "ContextHandlerByTargetDicts");
            return (CollectionPoolDiagnostics)
                pool.GetType().GetMethod("Snapshot").Invoke(pool, null);
        }

        public static int CountLiveRegistrations(MessageBus bus)
        {
            int count = 0;
            foreach (IEnumerable sink in (Array)ReadField(bus, "_scalarSinks"))
            {
                if (sink == null)
                {
                    continue;
                }
                foreach (object priorities in sink)
                {
                    count += CountPriorityRegistrations(priorities);
                }
            }
            foreach (IEnumerable sink in (Array)ReadField(bus, "_contextSinks"))
            {
                foreach (object contexts in sink)
                {
                    if (contexts == null)
                    {
                        continue;
                    }
                    foreach (object priorities in (Array)ReadField(contexts, "_values"))
                    {
                        count += CountPriorityRegistrations(priorities);
                    }
                }
            }
            object global = ReadField(bus, "_globalSlots");
            foreach (int references in ((IDictionary)ReadField(global, "sharedHandlers")).Values)
            {
                count += references;
            }
            foreach (
                Dictionary<int, int> priorities in (
                    (IDictionary)ReadField(bus, "_uniqueInterceptorsAndPriorities")
                ).Values
            )
            {
                foreach (int references in priorities.Values)
                {
                    count += references;
                }
            }
            return count;
        }

        private static int CountPriorityRegistrations(object priorities)
        {
            if (priorities == null)
            {
                return 0;
            }
            int count = 0;
            foreach (object handlers in ((IDictionary)ReadField(priorities, "handlers")).Values)
            {
                if (handlers == null)
                {
                    continue;
                }
                foreach (int references in ((IDictionary)ReadField(handlers, "handlers")).Values)
                {
                    count += references;
                }
            }
            return count;
        }

        public static bool HasDispatchState<TMessage>(
            MessageBus bus,
            RegistrationMethod method,
            InstanceId context
        )
            where TMessage : IMessage
        {
            bool contextual =
                method == RegistrationMethod.Targeted
                || method == RegistrationMethod.Broadcast
                || method == RegistrationMethod.TargetedPostProcessor
                || method == RegistrationMethod.BroadcastPostProcessor;
            object sink = typeof(MessageBus)
                .GetMethod(
                    contextual ? "ContextSinkForMethod" : "ScalarSinkForMethod",
                    BindingFlags.Instance | BindingFlags.NonPublic
                )
                .Invoke(bus, new object[] { method });
            MethodInfo lookup = Array.Find(
                sink.GetType().GetMethods(),
                candidate => candidate.Name == "TryGetValue" && candidate.IsGenericMethodDefinition
            );
            object[] arguments = { null };
            if (!(bool)lookup.MakeGenericMethod(typeof(TMessage)).Invoke(sink, arguments))
            {
                return false;
            }
            object priorities = arguments[0];
            if (contextual)
            {
                MethodInfo contextLookup = priorities
                    .GetType()
                    .GetMethod(
                        "TryGetValue",
                        BindingFlags.Instance | BindingFlags.NonPublic,
                        null,
                        new[]
                        {
                            typeof(InstanceId),
                            priorities.GetType().BaseType.GetGenericArguments()[0].MakeByRefType(),
                        },
                        null
                    );
                object[] contextArguments = { context, null };
                if (!(bool)contextLookup.Invoke(priorities, contextArguments))
                {
                    return false;
                }
                priorities = contextArguments[1];
            }
            return ReadField(priorities, "dispatchState") != null;
        }
    }
}
