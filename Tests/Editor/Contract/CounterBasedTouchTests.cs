namespace DxMessaging.Tests.Editor.Contract
{
    using System;
    using System.Reflection;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.MessageBus.Internal;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;

    /// <summary>
    /// Reflection-based contract tests for counter-based slot touch wiring.
    /// These intentionally pin likely private/internal names so implementation
    /// drift fails with focused messages.
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    public sealed class CounterBasedTouchTests
    {
        private static readonly InstanceId HandlerOwner = new InstanceId(0x5044_1001);

        private static readonly string[] TickMemberNames =
        {
            "_tickCounter",
            "_touchTicks",
            "_currentTouchTicks",
            "_currentTouchTick",
            "_slotTouchTicks",
            "_tick",
            "_ticks",
            "_currentTick",
            "TickCounter",
            "TouchTicks",
            "CurrentTouchTicks",
            "CurrentTouchTick",
            "Tick",
            "Ticks",
            "CurrentTick",
        };

        private readonly struct ProbeMessage : IUntargetedMessage<ProbeMessage> { }

        private readonly struct TargetedProbeMessage : ITargetedMessage<TargetedProbeMessage> { }

        [Test]
        public void TickStartsAtZero()
        {
            MessageBus bus = new MessageBus();

            Assert.AreEqual(0, ReadBusTick(bus));
        }

        [Test]
        public void TypedRegisterUpdatesTouchedSlotLastTouchTicks()
        {
            MessageBus bus = new MessageBus();
            MessageHandler handler = new MessageHandler(HandlerOwner, bus) { active = true };
            try
            {
                Action<ProbeMessage> callback = _ => { };
                Action deregistration = handler.RegisterUntargetedMessageHandler(
                    callback,
                    callback,
                    priority: 17,
                    messageBus: bus
                );

                IEvictableSlot slot = ReadTypedSlot<ProbeMessage>(
                    handler,
                    bus,
                    "UntargetedHandleDefault"
                );
                long tick = ReadBusTick(bus);

                Assert.Greater(tick, 0);
                Assert.AreEqual(tick, slot.LastTouchTicks);

                deregistration();
            }
            finally
            {
                handler.active = false;
            }
        }

        [Test]
        public void GlobalRegisterUpdatesTouchedSlotLastTouchTicks()
        {
            MessageBus bus = new MessageBus();
            MessageHandler handler = new MessageHandler(HandlerOwner, bus) { active = true };
            try
            {
                MessageBusRegistration deregistration = bus.RegisterGlobalAcceptAll(handler);
                IEvictableSlot slot = ReadGlobalSlot(bus);
                long tick = ReadBusTick(bus);

                Assert.Greater(tick, 0);
                Assert.AreEqual(tick, slot.LastTouchTicks);

                bus.Deregister<IMessage>(in deregistration);
            }
            finally
            {
                handler.active = false;
            }
        }

        [Test]
        public void TypedDeregisterUpdatesTouchedSlotLastTouchTicks()
        {
            MessageBus bus = new MessageBus();
            MessageHandler handler = new MessageHandler(HandlerOwner, bus) { active = true };
            try
            {
                Action<ProbeMessage> callback = _ => { };
                Action deregistration = handler.RegisterUntargetedMessageHandler(
                    callback,
                    callback,
                    priority: 17,
                    messageBus: bus
                );
                IEvictableSlot slot = ReadTypedSlot<ProbeMessage>(
                    handler,
                    bus,
                    "UntargetedHandleDefault"
                );
                long registerTick = ReadBusTick(bus);

                deregistration();
                long deregisterTick = ReadBusTick(bus);

                Assert.Greater(deregisterTick, registerTick);
                Assert.AreEqual(deregisterTick, slot.LastTouchTicks);
            }
            finally
            {
                handler.active = false;
            }
        }

        [Test]
        public void GlobalDeregisterUpdatesTouchedSlotLastTouchTicks()
        {
            MessageBus bus = new MessageBus();
            MessageHandler handler = new MessageHandler(HandlerOwner, bus) { active = true };
            try
            {
                MessageBusRegistration deregistration = bus.RegisterGlobalAcceptAll(handler);
                IEvictableSlot slot = ReadGlobalSlot(bus);
                long registerTick = ReadBusTick(bus);

                bus.Deregister<IMessage>(in deregistration);
                long deregisterTick = ReadBusTick(bus);

                Assert.Greater(deregisterTick, registerTick);
                Assert.AreEqual(deregisterTick, slot.LastTouchTicks);
            }
            finally
            {
                handler.active = false;
            }
        }

        [Test]
        public void StaleGlobalDeregisterAfterResetStateDoesNotTouchSlot()
        {
            MessageBus bus = new MessageBus();
            MessageHandler handler = new MessageHandler(HandlerOwner, bus) { active = true };
            try
            {
                MessageBusRegistration staleDeregistration = bus.RegisterGlobalAcceptAll(handler);
                IEvictableSlot slot = ReadGlobalSlot(bus);

                InvokeResetState(bus);
                long tickAfterReset = ReadBusTick(bus);
                long touchAfterReset = slot.LastTouchTicks;

                bus.Deregister<IMessage>(in staleDeregistration);

                Assert.AreEqual(tickAfterReset, ReadBusTick(bus));
                Assert.AreEqual(touchAfterReset, slot.LastTouchTicks);
            }
            finally
            {
                handler.active = false;
            }
        }

        [Test]
        public void StaleTypedDeregisterAfterResetStateDoesNotTouchSlot()
        {
            MessageBus bus = new MessageBus();
            MessageHandler handler = new MessageHandler(HandlerOwner, bus) { active = true };
            try
            {
                int staleCallCount = 0;
                int currentCallCount = 0;
                Action<ProbeMessage> callback = _ => { };
                Action<ProbeMessage> staleCallback = _ =>
                {
                    staleCallCount++;
                };
                Action<ProbeMessage> currentCallback = _ =>
                {
                    currentCallCount++;
                };
                Action staleDeregistration = handler.RegisterUntargetedMessageHandler(
                    callback,
                    staleCallback,
                    priority: 17,
                    messageBus: bus
                );
                IEvictableSlot slot = ReadTypedSlot<ProbeMessage>(
                    handler,
                    bus,
                    "UntargetedHandleDefault"
                );

                InvokeResetState(bus);
                long tickAfterReset = ReadBusTick(bus);
                long touchAfterReset = slot.LastTouchTicks;

                staleDeregistration();

                Assert.AreEqual(tickAfterReset, ReadBusTick(bus));
                Assert.AreEqual(touchAfterReset, slot.LastTouchTicks);
                Assert.IsTrue(slot.IsEmpty);

                _ = handler.RegisterUntargetedMessageHandler(
                    currentCallback,
                    currentCallback,
                    priority: 17,
                    messageBus: bus
                );
                ProbeMessage message = new ProbeMessage();
                bus.UntargetedBroadcast(ref message);

                Assert.AreEqual(0, staleCallCount);
                Assert.AreEqual(1, currentCallCount);
            }
            finally
            {
                handler.active = false;
            }
        }

        [Test]
        public void StaleTargetedContextDeregisterAfterResetStateDoesNotTouchSlot()
        {
            MessageBus bus = new MessageBus();
            MessageHandler handler = new MessageHandler(HandlerOwner, bus) { active = true };
            InstanceId target = new InstanceId(0x5044_2002);
            try
            {
                int staleCallCount = 0;
                int currentCallCount = 0;
                Action<TargetedProbeMessage> callback = _ => { };
                Action<TargetedProbeMessage> staleCallback = _ =>
                {
                    staleCallCount++;
                };
                Action<TargetedProbeMessage> currentCallback = _ =>
                {
                    currentCallCount++;
                };
                Action staleDeregistration = handler.RegisterTargetedMessageHandler(
                    target,
                    callback,
                    staleCallback,
                    priority: 17,
                    messageBus: bus
                );
                IEvictableSlot slot = ReadTypedSlot<TargetedProbeMessage>(
                    handler,
                    bus,
                    "TargetedHandleDefault"
                );

                InvokeResetState(bus);
                long tickAfterReset = ReadBusTick(bus);
                long touchAfterReset = slot.LastTouchTicks;

                staleDeregistration();

                Assert.AreEqual(tickAfterReset, ReadBusTick(bus));
                Assert.AreEqual(touchAfterReset, slot.LastTouchTicks);
                Assert.IsTrue(slot.IsEmpty);

                _ = handler.RegisterTargetedMessageHandler(
                    target,
                    currentCallback,
                    currentCallback,
                    priority: 17,
                    messageBus: bus
                );
                TargetedProbeMessage message = new TargetedProbeMessage();
                bus.TargetedBroadcast(ref target, ref message);

                Assert.AreEqual(0, staleCallCount);
                Assert.AreEqual(1, currentCallCount);
            }
            finally
            {
                handler.active = false;
            }
        }

        [Test]
        public void StaleTypedGlobalDeregisterAfterResetStateDoesNotTouchSlot()
        {
            MessageBus bus = new MessageBus();
            MessageHandler handler = new MessageHandler(HandlerOwner, bus) { active = true };
            try
            {
                int staleCallCount = 0;
                int currentCallCount = 0;
                Action<IUntargetedMessage> originalUntargeted = _ => { };
                Action<IUntargetedMessage> staleUntargeted = _ =>
                {
                    staleCallCount++;
                };
                Action<IUntargetedMessage> currentUntargeted = _ =>
                {
                    currentCallCount++;
                };
                Action<InstanceId, ITargetedMessage> targeted = (_, _) => { };
                Action<InstanceId, IBroadcastMessage> broadcast = (_, _) => { };
                Action staleDeregistration = handler.RegisterGlobalAcceptAll(
                    originalUntargeted,
                    staleUntargeted,
                    targeted,
                    targeted,
                    broadcast,
                    broadcast,
                    bus
                );
                IEvictableSlot slot = ReadTypedGlobalSlot(handler, bus, "UntargetedDefault");

                InvokeResetState(bus);
                long tickAfterReset = ReadBusTick(bus);
                long touchAfterReset = slot.LastTouchTicks;

                staleDeregistration();

                Assert.AreEqual(tickAfterReset, ReadBusTick(bus));
                Assert.AreEqual(touchAfterReset, slot.LastTouchTicks);
                Assert.IsTrue(slot.IsEmpty);

                _ = handler.RegisterGlobalAcceptAll(
                    currentUntargeted,
                    currentUntargeted,
                    targeted,
                    targeted,
                    broadcast,
                    broadcast,
                    bus
                );
                ProbeMessage message = new ProbeMessage();
                bus.UntargetedBroadcast(ref message);

                Assert.AreEqual(0, staleCallCount);
                Assert.AreEqual(1, currentCallCount);
            }
            finally
            {
                handler.active = false;
            }
        }

        [Test]
        public void TypedGlobalDeregisterUsesAdvancedBusTick()
        {
            MessageBus bus = new MessageBus();
            MessageHandler handler = new MessageHandler(HandlerOwner, bus) { active = true };
            try
            {
                Action<IUntargetedMessage> untargeted = _ => { };
                Action<InstanceId, ITargetedMessage> targeted = (_, _) => { };
                Action<InstanceId, IBroadcastMessage> broadcast = (_, _) => { };
                Action deregistration = handler.RegisterGlobalAcceptAll(
                    untargeted,
                    untargeted,
                    targeted,
                    targeted,
                    broadcast,
                    broadcast,
                    bus
                );
                IEvictableSlot slot = ReadTypedGlobalSlot(handler, bus, "UntargetedDefault");
                long registerTick = ReadBusTick(bus);

                deregistration();
                long deregisterTick = ReadBusTick(bus);

                Assert.Greater(deregisterTick, registerTick);
                Assert.AreEqual(deregisterTick, slot.LastTouchTicks);
            }
            finally
            {
                handler.active = false;
            }
        }

        [Test]
        public void UntypedUntargetedBroadcastIncrementsOnce()
        {
            MessageBus bus = new MessageBus();
            ProbeMessage message = new ProbeMessage();
            long before = ReadBusTick(bus);

            bus.UntypedUntargetedBroadcast(message);

            long after = ReadBusTick(bus);
            Assert.AreEqual(before + 1, after);
        }

        private static long ReadBusTick(MessageBus bus)
        {
            Type busType = typeof(MessageBus);
            const BindingFlags Flags =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            foreach (string name in TickMemberNames)
            {
                FieldInfo field = busType.GetField(name, Flags);
                if (field != null && IsLongLike(field.FieldType))
                {
                    return Convert.ToInt64(field.GetValue(bus));
                }

                PropertyInfo property = busType.GetProperty(name, Flags);
                if (
                    property != null
                    && property.GetIndexParameters().Length == 0
                    && property.GetMethod != null
                    && IsLongLike(property.PropertyType)
                )
                {
                    return Convert.ToInt64(property.GetValue(bus));
                }
            }

            Assert.Fail(
                "Could not locate the MessageBus touch tick counter. Tried: "
                    + string.Join(", ", TickMemberNames)
                    + ". Update CounterBasedTouchTests.TickMemberNames if the implementation uses a different name."
            );
            return 0;
        }

        private static bool IsLongLike(Type type)
        {
            return type == typeof(long)
                || type == typeof(ulong)
                || type == typeof(int)
                || type == typeof(uint);
        }

        private static IEvictableSlot ReadGlobalSlot(MessageBus bus)
        {
            FieldInfo field = typeof(MessageBus).GetField(
                "_globalSlots",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            Assert.IsNotNull(field, "MessageBus must retain private field '_globalSlots'.");
            object value = field.GetValue(bus);
            Assert.IsInstanceOf<IEvictableSlot>(value);
            return (IEvictableSlot)value;
        }

        private static IEvictableSlot ReadTypedSlot<TMessage>(
            MessageHandler handler,
            IMessageBus bus,
            string typedSlotIndexName
        )
            where TMessage : IMessage
        {
            object typedHandler = ReadTypedHandler<TMessage>(handler, bus);
            Array slots = ReadArrayField(typedHandler, "_slots");
            int index = ReadIndexConstant(
                "DxMessaging.Core.Internal.TypedSlotIndex",
                typedSlotIndexName
            );
            object slot = slots.GetValue(index);
            Assert.IsInstanceOf<IEvictableSlot>(slot);
            return (IEvictableSlot)slot;
        }

        private static IEvictableSlot ReadTypedGlobalSlot(
            MessageHandler handler,
            IMessageBus bus,
            string typedGlobalSlotIndexName
        )
        {
            object typedHandler = ReadTypedHandler<IMessage>(handler, bus);
            Array slots = ReadArrayField(typedHandler, "_globalSlots");
            int index = ReadIndexConstant(
                "DxMessaging.Core.Internal.TypedGlobalSlotIndex",
                typedGlobalSlotIndexName
            );
            object slot = slots.GetValue(index);
            Assert.IsInstanceOf<IEvictableSlot>(slot);
            return (IEvictableSlot)slot;
        }

        private static object ReadTypedHandler<TMessage>(MessageHandler handler, IMessageBus bus)
            where TMessage : IMessage
        {
            Assert.Less(
                bus.RegisteredGlobalSequentialIndex,
                handler._handlersByTypeByMessageBus.Count
            );
            bool exists = handler
                ._handlersByTypeByMessageBus[bus.RegisteredGlobalSequentialIndex]
                .TryGetValue<TMessage>(out object typedHandler);
            Assert.IsTrue(exists, "Typed handler for " + typeof(TMessage).Name + " must exist.");
            Assert.IsNotNull(typedHandler);
            return typedHandler;
        }

        private static Array ReadArrayField(object owner, string name)
        {
            FieldInfo field = owner
                .GetType()
                .GetField(
                    name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                );
            Assert.IsNotNull(field, owner.GetType().Name + " must declare field '" + name + "'.");
            object value = field.GetValue(owner);
            Assert.IsNotNull(value, owner.GetType().Name + "." + name + " must be non-null.");
            return (Array)value;
        }

        private static int ReadIndexConstant(string fullTypeName, string name)
        {
            Type type = typeof(MessageHandler).Assembly.GetType(fullTypeName);
            Assert.IsNotNull(type, "Could not locate index type " + fullTypeName + ".");
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(field, fullTypeName + "." + name + " must exist.");
            return (int)field.GetRawConstantValue();
        }

        private static void InvokeResetState(MessageBus bus)
        {
            MethodInfo method = typeof(MessageBus).GetMethod(
                "ResetState",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            Assert.IsNotNull(method, "MessageBus.ResetState must exist for stale dereg tests.");
            method.Invoke(bus, Array.Empty<object>());
        }
    }
}
