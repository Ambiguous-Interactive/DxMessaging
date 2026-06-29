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
    /// Contract guardrails for the <see cref="BusGlobalSlot.liveCount"/>
    /// invariant wired for memory reclamation. The
    /// counter must remain in lockstep with
    /// <c>BusGlobalSlot.sharedHandlers.Count</c> across every register and
    /// deregister flow exercised through
    /// <see cref="IMessageBus.RegisterGlobalAcceptAll"/>, so that
    /// <see cref="BusGlobalSlot.IsEmpty"/> can be a single integer compare
    /// without losing fidelity for re-registration / partial-deregistration
    /// / over-deregistration cases.
    /// </summary>
    /// <remarks>
    /// The bus's <c>_globalSlots</c> field is <c>private readonly</c>; the
    /// fixture reads it via reflection so the slot's
    /// <see cref="BusGlobalSlot.IsEmpty"/> getter can be exercised directly
    /// in addition to the public
    /// <see cref="IMessageBus.RegisteredGlobalAcceptAll"/> count
    /// (<c>InternalsVisibleTo</c> on the runtime asmdef makes
    /// <see cref="BusGlobalSlot"/> itself reachable). Reflection is also
    /// used so the contract holds even when the public getter contract is
    /// later refactored.
    /// </remarks>
    [TestFixture]
    [Category("Contract")]
    public sealed class BusGlobalSlotLiveCountTests
    {
        private static readonly InstanceId HandlerOwnerA = new InstanceId(0x4E61_5841);
        private static readonly InstanceId HandlerOwnerB = new InstanceId(0x4E61_5842);

        private static readonly FieldInfo GlobalSlotsField = typeof(MessageBus).GetField(
            "_globalSlots",
            BindingFlags.Instance | BindingFlags.NonPublic
        );

        [Test]
        public void LiveCountStartsAtZero()
        {
            MessageBus bus = new MessageBus();
            Assert.AreEqual(0, bus.RegisteredGlobalAcceptAll);
            BusGlobalSlot slot = ReadGlobalSlot(bus);
            Assert.AreEqual(0, slot.liveCount);
            Assert.IsTrue(slot.IsEmpty);
            slot.DebugAssertLiveCountInvariant();
        }

        [Test]
        public void LiveCountIncrementsOnFirstRegistration()
        {
            MessageBus bus = new MessageBus();
            MessageHandler handler = new MessageHandler(HandlerOwnerA, bus) { active = true };
            try
            {
                _ = bus.RegisterGlobalAcceptAll(handler);

                Assert.AreEqual(1, bus.RegisteredGlobalAcceptAll);
                BusGlobalSlot slot = ReadGlobalSlot(bus);
                Assert.AreEqual(1, slot.liveCount);
                Assert.IsFalse(slot.IsEmpty);
                slot.DebugAssertLiveCountInvariant();
            }
            finally
            {
                handler.active = false;
            }
        }

        [Test]
        public void LiveCountStaysOneOnDuplicateRegistrationFromSameHandler()
        {
            MessageBus bus = new MessageBus();
            MessageHandler handler = new MessageHandler(HandlerOwnerA, bus) { active = true };
            try
            {
                _ = bus.RegisterGlobalAcceptAll(handler);
                _ = bus.RegisterGlobalAcceptAll(handler);

                // The dictionary refcount goes 0 -> 1 -> 2; only the 0 -> 1
                // transition advances liveCount.
                Assert.AreEqual(1, bus.RegisteredGlobalAcceptAll);
                BusGlobalSlot slot = ReadGlobalSlot(bus);
                Assert.AreEqual(1, slot.liveCount);
                Assert.IsFalse(slot.IsEmpty);
                slot.DebugAssertLiveCountInvariant();
            }
            finally
            {
                handler.active = false;
            }
        }

        [Test]
        public void LiveCountIncrementsForDistinctHandlers()
        {
            MessageBus bus = new MessageBus();
            MessageHandler handlerA = new MessageHandler(HandlerOwnerA, bus) { active = true };
            MessageHandler handlerB = new MessageHandler(HandlerOwnerB, bus) { active = true };
            try
            {
                _ = bus.RegisterGlobalAcceptAll(handlerA);
                _ = bus.RegisterGlobalAcceptAll(handlerB);

                Assert.AreEqual(2, bus.RegisteredGlobalAcceptAll);
                BusGlobalSlot slot = ReadGlobalSlot(bus);
                Assert.AreEqual(2, slot.liveCount);
                Assert.IsFalse(slot.IsEmpty);
                slot.DebugAssertLiveCountInvariant();
            }
            finally
            {
                handlerA.active = false;
                handlerB.active = false;
            }
        }

        [Test]
        public void LiveCountDecrementsOnFinalDeregistration()
        {
            MessageBus bus = new MessageBus();
            MessageHandler handler = new MessageHandler(HandlerOwnerA, bus) { active = true };
            try
            {
                MessageBusRegistration dereg = bus.RegisterGlobalAcceptAll(handler);
                Assert.AreEqual(1, bus.RegisteredGlobalAcceptAll);

                bus.Deregister<IMessage>(in dereg);

                Assert.AreEqual(0, bus.RegisteredGlobalAcceptAll);
                BusGlobalSlot slot = ReadGlobalSlot(bus);
                Assert.AreEqual(0, slot.liveCount);
                Assert.IsTrue(slot.IsEmpty);
                slot.DebugAssertLiveCountInvariant();
            }
            finally
            {
                handler.active = false;
            }
        }

        [Test]
        public void LiveCountStaysAfterPartialDeregistration()
        {
            MessageBus bus = new MessageBus();
            MessageHandler handler = new MessageHandler(HandlerOwnerA, bus) { active = true };
            try
            {
                MessageBusRegistration dereg1 = bus.RegisterGlobalAcceptAll(handler);
                _ = bus.RegisterGlobalAcceptAll(handler);

                // Refcount: 0 -> 1 -> 2; liveCount went 0 -> 1, stays 1.
                Assert.AreEqual(1, bus.RegisteredGlobalAcceptAll);

                bus.Deregister<IMessage>(in dereg1);

                // Refcount: 2 -> 1; the dictionary entry is still present, so
                // liveCount must stay 1 (only the final 1 -> 0 transition
                // decrements it).
                Assert.AreEqual(1, bus.RegisteredGlobalAcceptAll);
                BusGlobalSlot slot = ReadGlobalSlot(bus);
                Assert.AreEqual(1, slot.liveCount);
                Assert.IsFalse(slot.IsEmpty);
                slot.DebugAssertLiveCountInvariant();
            }
            finally
            {
                handler.active = false;
            }
        }

        [Test]
        public void LiveCountUnchangedOnOverDeregistration()
        {
            MessageBus bus = new MessageBus();
            MessageHandler handler = new MessageHandler(HandlerOwnerA, bus) { active = true };
            Action<LogLevel, string> previousLog = MessagingDebug.LogFunction;
            try
            {
                MessagingDebug.LogFunction = (_, _) => { };

                MessageBusRegistration dereg = bus.RegisterGlobalAcceptAll(handler);
                bus.Deregister<IMessage>(in dereg);

                Assert.AreEqual(0, bus.RegisteredGlobalAcceptAll);
                BusGlobalSlot slot = ReadGlobalSlot(bus);
                Assert.AreEqual(0, slot.liveCount);
                slot.DebugAssertLiveCountInvariant();

                // Second invocation is over-deregistration: the early-exit
                // branch must NOT decrement liveCount.
                bus.Deregister<IMessage>(in dereg);

                Assert.AreEqual(0, bus.RegisteredGlobalAcceptAll);
                slot = ReadGlobalSlot(bus);
                Assert.AreEqual(0, slot.liveCount);
                Assert.IsTrue(slot.IsEmpty);
                slot.DebugAssertLiveCountInvariant();
            }
            finally
            {
                MessagingDebug.LogFunction = previousLog;
                handler.active = false;
            }
        }

        [Test]
        public void IsEmptyTracksLiveCount()
        {
            MessageBus bus = new MessageBus();
            MessageHandler handler = new MessageHandler(HandlerOwnerA, bus) { active = true };
            try
            {
                BusGlobalSlot slot = ReadGlobalSlot(bus);
                Assert.IsTrue(slot.IsEmpty, "Fresh slot must report IsEmpty.");

                MessageBusRegistration dereg = bus.RegisterGlobalAcceptAll(handler);
                slot = ReadGlobalSlot(bus);
                Assert.IsFalse(
                    slot.IsEmpty,
                    "After registration liveCount > 0, IsEmpty must be false."
                );
                slot.DebugAssertLiveCountInvariant();

                bus.Deregister<IMessage>(in dereg);
                slot = ReadGlobalSlot(bus);
                Assert.IsTrue(
                    slot.IsEmpty,
                    "After final deregistration liveCount == 0, IsEmpty must be true."
                );
                slot.DebugAssertLiveCountInvariant();
            }
            finally
            {
                handler.active = false;
            }
        }

        private static BusGlobalSlot ReadGlobalSlot(MessageBus bus)
        {
            Assert.IsNotNull(
                GlobalSlotsField,
                "Could not locate the private '_globalSlots' field on MessageBus via reflection."
            );
            object value = GlobalSlotsField.GetValue(bus);
            Assert.IsInstanceOf<BusGlobalSlot>(value);
            return (BusGlobalSlot)value;
        }
    }
}
