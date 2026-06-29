namespace DxMessaging.Tests.Editor.Contract
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using DxMessaging.Core;
    using DxMessaging.Core.Helper;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.MessageBus.Internal;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;

    /// <summary>
    /// Contract guardrails for <see cref="MessageBus"/> cache storage fields that
    /// must stay registered with the memory-reclamation sweep table.
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    public sealed class MessageBusInvariantTests
    {
        private static readonly InstanceId HandlerOwnerA = new InstanceId(0x4D42_4901);
        private static readonly InstanceId HandlerOwnerB = new InstanceId(0x4D42_4902);
        private static readonly InstanceId Target = new InstanceId(0x4D42_4903);

        private const BindingFlags DeclaredInstanceFields =
            BindingFlags.Instance
            | BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.DeclaredOnly;

        [Test]
        public void MessageCacheFieldCountMatchesExpected()
        {
            FieldInfo[] fields = GetMessageCacheStorageFields();

            Assert.AreEqual(
                MessageBus.ExpectedMessageCacheFieldCount,
                fields.Length,
                "MessageBus MessageCache storage field count changed. If the new cache owns "
                    + "per-message-type state, register it in MessageBus.SweepableTypeCaches and "
                    + "then update ExpectedMessageCacheFieldCount."
            );
        }

        [Test]
        public void MessageCacheArrayFieldsCountAsStorageFields()
        {
            string[] fieldNames = GetMessageCacheStorageFields()
                .Select(field => field.Name)
                .ToArray();

            CollectionAssert.Contains(fieldNames, "_scalarSinks");
            CollectionAssert.Contains(fieldNames, "_contextSinks");
        }

        [Test]
        public void EveryMessageCacheFieldHasSweepableRegistryEntry()
        {
            string[] fieldNames = GetMessageCacheStorageFields()
                .Select(field => field.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            string[] registeredNames = MessageBus
                .SweepableTypeCaches.Select(sweepable => sweepable.StorageFieldName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            CollectionAssert.AreEqual(
                fieldNames,
                registeredNames,
                "Every MessageBus MessageCache storage field must have exactly one sweep registry row."
            );
        }

        [Test]
        public void SweepableRegistryDoesNotContainUnknownFields()
        {
            HashSet<string> fieldNames = new(
                GetMessageCacheStorageFields().Select(field => field.Name),
                StringComparer.Ordinal
            );

            foreach (ISweepable sweepable in MessageBus.SweepableTypeCaches)
            {
                Assert.IsTrue(
                    fieldNames.Contains(sweepable.StorageFieldName),
                    $"Sweep registry row '{sweepable.StorageFieldName}' does not match a MessageBus MessageCache field."
                );
            }
        }

        [Test]
        public void AotBridgeRegistrationHooksKeepGeneratedReflectionContract()
        {
            AssertAotBridgeHook(
                "RegisterAotUntargetedBridge",
                typeof(AotHookUntargetedMessage),
                (Action<IMessageBus, IUntargetedMessage>)((_, _) => { })
            );
            AssertAotBridgeHook(
                "RegisterAotTargetedBridge",
                typeof(AotHookTargetedMessage),
                (Action<IMessageBus, InstanceId, ITargetedMessage>)((_, _, _) => { })
            );
            AssertAotBridgeHook(
                "RegisterAotSourcedBridge",
                typeof(AotHookBroadcastMessage),
                (Action<IMessageBus, InstanceId, IBroadcastMessage>)((_, _, _) => { })
            );
        }

        [Test]
        public void SweepableRegistryFieldNamesAreUnique()
        {
            string[] duplicateNames = MessageBus
                .SweepableTypeCaches.GroupBy(
                    sweepable => sweepable.StorageFieldName,
                    StringComparer.Ordinal
                )
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToArray();

            CollectionAssert.IsEmpty(
                duplicateNames,
                "Sweep registry rows must be one-to-one with MessageBus MessageCache storage fields."
            );
        }

        [Test]
        public void SweepableRegistryFieldTypesMatchDeclaredFields()
        {
            Dictionary<string, FieldInfo> fieldsByName = GetMessageCacheStorageFields()
                .ToDictionary(field => field.Name, StringComparer.Ordinal);

            foreach (ISweepable sweepable in MessageBus.SweepableTypeCaches)
            {
                Assert.IsTrue(
                    fieldsByName.TryGetValue(sweepable.StorageFieldName, out FieldInfo field),
                    $"Sweep registry row '{sweepable.StorageFieldName}' does not match a MessageBus MessageCache field."
                );
                Assert.AreEqual(
                    field.FieldType,
                    sweepable.StorageFieldType,
                    $"Sweep registry row '{sweepable.StorageFieldName}' has a stale field type."
                );
            }
        }

        [Test]
        public void ContextSweepCountsRemovedTypeAndTargetSlots()
        {
            MessageBus bus = new MessageBus();
            MessageHandler handler = new MessageHandler(HandlerOwnerA, bus) { active = true };
            try
            {
                MessageBusRegistration deregister = bus.RegisterTargeted<TargetedProbeMessage>(
                    Target,
                    handler
                );
                bus.Deregister<TargetedProbeMessage>(in deregister);

                IMessageBus.TrimResult result = bus.Trim(force: true);

                Assert.AreEqual(
                    1,
                    result.TargetSlotsEvicted,
                    "Context sweep should report the reclaimed target slot."
                );
                Assert.AreEqual(
                    1,
                    result.TypeSlotsEvicted,
                    "Removing the final target dictionary also removes the context-backed type slot."
                );
                Assert.AreEqual(0, bus.OccupiedTypeSlots);
                Assert.AreEqual(0, bus.OccupiedTargetSlots);
            }
            finally
            {
                handler.active = false;
            }
        }

        [Test]
        public void InterceptorMessageCachesAreSweptThroughRegistry()
        {
            MessageBus bus = new MessageBus();
            IMessageBus.UntargetedInterceptor<InterceptorProbeMessage> interceptor = (
                ref InterceptorProbeMessage _
            ) => true;

            MessageBusRegistration deregister = bus.RegisterUntargetedInterceptor(interceptor);
            Assert.AreEqual(1, bus.RegisteredInterceptors);
            Assert.GreaterOrEqual(bus.OccupiedTypeSlots, 1);

            bus.Deregister<InterceptorProbeMessage>(in deregister);
            Assert.AreEqual(0, bus.RegisteredInterceptors);

            ISweepable sweepable = MessageBus.SweepableTypeCaches.Single(row =>
                row.StorageFieldName == "_untargetedInterceptsByType"
            );
            int evicted = sweepable.Sweep(bus, force: true);

            Assert.AreEqual(1, evicted);
            Assert.AreEqual(0, bus.OccupiedTypeSlots);
        }

        [Test]
        public void TrimDuringActiveDispatchAfterDeregisterPreservesSnapshot()
        {
            MessageBus bus = new MessageBus();
            MessageHandler first = new MessageHandler(HandlerOwnerA, bus) { active = true };
            MessageHandler second = new MessageHandler(HandlerOwnerB, bus) { active = true };
            Action firstDeregister = null;
            Action secondDeregister = null;
            int secondCalls = 0;
            try
            {
                firstDeregister =
                    first.RegisterUntargetedMessageHandler<UntargetedDispatchProbeMessage>(
                        input =>
                        {
                            firstDeregister();
                            secondDeregister();
                            _ = bus.Trim(force: true);
                        },
                        input =>
                        {
                            firstDeregister();
                            secondDeregister();
                            _ = bus.Trim(force: true);
                        },
                        priority: 0,
                        messageBus: bus
                    );
                secondDeregister =
                    second.RegisterUntargetedMessageHandler<UntargetedDispatchProbeMessage>(
                        _ => secondCalls++,
                        _ => secondCalls++,
                        priority: 0,
                        messageBus: bus
                    );

                UntargetedDispatchProbeMessage message = new UntargetedDispatchProbeMessage();
                bus.UntargetedBroadcast(ref message);

                Assert.AreEqual(
                    1,
                    secondCalls,
                    "Trim must not release the active dispatch snapshot while the current emission is still iterating it."
                );

                _ = bus.Trim(force: true);
                Assert.AreEqual(0, bus.OccupiedTypeSlots);
            }
            finally
            {
                first.active = false;
                second.active = false;
            }
        }

        [Test]
        public void StaleInterceptorDeregisterAfterSweepCannotRemoveReRegisteredInterceptor()
        {
            MessageBus bus = new MessageBus();
            IMessageBus.UntargetedInterceptor<InterceptorProbeMessage> interceptor = (
                ref InterceptorProbeMessage _
            ) => true;

            MessageBusRegistration staleDeregister = bus.RegisterUntargetedInterceptor(interceptor);
            bus.Deregister<InterceptorProbeMessage>(in staleDeregister);
            _ = bus.Trim(force: true);

            _ = bus.RegisterUntargetedInterceptor(interceptor);
            bus.Deregister<InterceptorProbeMessage>(in staleDeregister);

            Assert.AreEqual(
                1,
                bus.RegisteredInterceptors,
                "A stale deregister closure from an evicted interceptor cache must not remove a later registration."
            );
        }

        [Test]
        public void StaleGlobalAcceptAllDeregisterAfterSweepCannotRemoveReRegisteredHandler()
        {
            MessageBus bus = new MessageBus();
            MessageHandler handler = new MessageHandler(new InstanceId(0x4D42_0001), bus)
            {
                active = true,
            };
            try
            {
                MessageBusRegistration staleDeregister = bus.RegisterGlobalAcceptAll(handler);
                bus.Deregister<IMessage>(in staleDeregister);
                _ = bus.Trim(force: true);

                MessageBusRegistration currentDeregister = bus.RegisterGlobalAcceptAll(handler);
                bus.Deregister<IMessage>(in staleDeregister);

                Assert.AreEqual(
                    1,
                    bus.RegisteredGlobalAcceptAll,
                    "A stale GlobalAcceptAll deregister closure from an evicted global slot must not remove a later registration."
                );

                bus.Deregister<IMessage>(in currentDeregister);
                Assert.AreEqual(0, bus.RegisteredGlobalAcceptAll);
            }
            finally
            {
                handler.active = false;
            }
        }

        [Test]
        public void StaleScalarDeregisterAfterSweepDoesNotWriteDiagnosticsLog()
        {
            MessageBus bus = new MessageBus();
            bus.Log.Enabled = true;
            MessageHandler handler = new MessageHandler(HandlerOwnerA, bus) { active = true };
            try
            {
                MessageBusRegistration staleDeregister =
                    bus.RegisterUntargeted<UntargetedDispatchProbeMessage>(handler);
                bus.Deregister<UntargetedDispatchProbeMessage>(in staleDeregister);
                Assert.AreEqual(2, bus.Log.Registrations.Count);

                _ = bus.Trim(force: true);
                bus.Deregister<UntargetedDispatchProbeMessage>(in staleDeregister);

                Assert.AreEqual(
                    2,
                    bus.Log.Registrations.Count,
                    "A stale scalar deregister closure after sweep must be silent in diagnostics."
                );
            }
            finally
            {
                handler.active = false;
            }
        }

        [Test]
        public void StaleContextDeregisterAfterSweepDoesNotWriteDiagnosticsLog()
        {
            MessageBus bus = new MessageBus();
            bus.Log.Enabled = true;
            MessageHandler handler = new MessageHandler(HandlerOwnerA, bus) { active = true };
            try
            {
                MessageBusRegistration staleDeregister = bus.RegisterTargeted<TargetedProbeMessage>(
                    Target,
                    handler
                );
                bus.Deregister<TargetedProbeMessage>(in staleDeregister);
                Assert.AreEqual(2, bus.Log.Registrations.Count);

                _ = bus.Trim(force: true);
                bus.Deregister<TargetedProbeMessage>(in staleDeregister);

                Assert.AreEqual(
                    2,
                    bus.Log.Registrations.Count,
                    "A stale context deregister closure after sweep must be silent in diagnostics."
                );
            }
            finally
            {
                handler.active = false;
            }
        }

        [Test]
        public void TrimDuringInFlightDispatchAfterDeregisteringCurrentSlotDefersSnapshotRelease()
        {
            MessageBus bus = new MessageBus();
            MessageHandler firstHandler = new MessageHandler(new InstanceId(0x4D42_0002), bus)
            {
                active = true,
            };
            MessageHandler secondHandler = new MessageHandler(new InstanceId(0x4D42_0003), bus)
            {
                active = true,
            };
            List<string> calls = new List<string>();
            IMessageBus.TrimResult inFlightTrimResult = default;
            Action firstDeregister = null;
            Action secondDeregister = null;
            try
            {
                Action<DispatchTrimProbeMessage> first = _ =>
                {
                    calls.Add("first");
                    firstDeregister();
                    secondDeregister();
                    inFlightTrimResult = bus.Trim(force: true);
                };
                Action<DispatchTrimProbeMessage> second = _ =>
                {
                    calls.Add("second");
                };

                firstDeregister = firstHandler.RegisterUntargetedMessageHandler(
                    first,
                    first,
                    priority: 0,
                    messageBus: bus
                );
                secondDeregister = secondHandler.RegisterUntargetedMessageHandler(
                    second,
                    second,
                    priority: 1,
                    messageBus: bus
                );

                DispatchTrimProbeMessage message = new DispatchTrimProbeMessage();
                bus.UntargetedBroadcast(ref message);

                CollectionAssert.AreEqual(new[] { "first", "second" }, calls);
                Assert.AreEqual(
                    0,
                    inFlightTrimResult.TypeSlotsEvicted,
                    "Trim must not release the active dispatch snapshot while the dispatch loop is still reading it."
                );
            }
            finally
            {
                firstHandler.active = false;
                secondHandler.active = false;
            }
        }

        private static FieldInfo[] GetMessageCacheStorageFields()
        {
            return typeof(MessageBus)
                .GetFields(DeclaredInstanceFields)
                .Where(field => IsMessageCacheStorageField(field.FieldType))
                .OrderBy(field => field.Name, StringComparer.Ordinal)
                .ToArray();
        }

        private static bool IsMessageCacheStorageField(Type fieldType)
        {
            if (IsClosedMessageCache(fieldType))
            {
                return true;
            }

            return fieldType.IsArray
                && fieldType.GetArrayRank() == 1
                && IsClosedMessageCache(fieldType.GetElementType());
        }

        private static void AssertAotBridgeHook(
            string methodName,
            Type messageType,
            Delegate bridge
        )
        {
            MethodInfo method = typeof(MessageBus).GetMethod(
                methodName,
                BindingFlags.Static | BindingFlags.NonPublic
            );

            Assert.IsNotNull(method, $"{methodName} must remain available for generated code.");
            Assert.AreEqual(typeof(void), method.ReturnType, methodName);

            ParameterInfo[] parameters = method.GetParameters();
            Assert.AreEqual(2, parameters.Length, methodName);
            Assert.AreEqual(typeof(Type), parameters[0].ParameterType, methodName);
            Assert.AreEqual(typeof(Delegate), parameters[1].ParameterType, methodName);
            Assert.DoesNotThrow(
                () => method.Invoke(null, new object[] { messageType, bridge }),
                $"{methodName} must accept the generated delegate shape."
            );
        }

        private static bool IsClosedMessageCache(Type type)
        {
            return type != null
                && type.IsGenericType
                && type.GetGenericTypeDefinition() == typeof(MessageCache<>);
        }

        private readonly struct AotHookUntargetedMessage : IUntargetedMessage { }

        private readonly struct AotHookTargetedMessage : ITargetedMessage { }

        private readonly struct AotHookBroadcastMessage : IBroadcastMessage { }

        private readonly struct InterceptorProbeMessage : IUntargetedMessage { }

        private readonly struct DispatchTrimProbeMessage : IUntargetedMessage { }

        private readonly struct TargetedProbeMessage : ITargetedMessage { }

        private readonly struct UntargetedDispatchProbeMessage : IUntargetedMessage { }
    }
}
