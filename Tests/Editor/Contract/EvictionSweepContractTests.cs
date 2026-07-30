namespace DxMessaging.Tests.Editor.Contract
{
    using System;
    using System.Reflection;
    using DxMessaging.Core;
    using DxMessaging.Core.Internal;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.MessageBus.Internal;
    using DxMessaging.Core.Messages;
    using DxMessaging.Core.Pooling;
    using NUnit.Framework;
#if UNITY_2021_3_OR_NEWER
    using DxMessaging.Core.Configuration;
    using UnityEngine;
    using UnityEngine.LowLevel;
    using UnityEngine.PlayerLoop;
#endif

    [TestFixture]
    [Category("Contract")]
    public sealed class EvictionSweepContractTests
    {
        private static readonly InstanceId HandlerOwner = new InstanceId(0x5044_4201);

        private readonly struct ProbeMessage : IUntargetedMessage<ProbeMessage> { }

        private readonly struct OtherProbeMessage : IUntargetedMessage<OtherProbeMessage> { }

        private readonly struct TargetedProbeMessage : ITargetedMessage<TargetedProbeMessage> { }

        private readonly struct BroadcastProbeMessage : IBroadcastMessage<BroadcastProbeMessage> { }

        private sealed class ThrowingIntComparer : System.Collections.Generic.IEqualityComparer<int>
        {
            public bool Equals(int x, int y)
            {
                return x == y;
            }

            public int GetHashCode(int obj)
            {
                throw new InvalidOperationException("Injected owner insertion failure.");
            }
        }

        [Test]
        public void ForceSweepResetsDirtyEmptyTypedSlotsAndTrimsPools()
        {
            ManualClock clock = new ManualClock(10d);
            MessageBus bus = MessageBus.CreateForInternalUse(clock, idleEvictionTicks: 0);
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
                deregistration();
                object typedHandler = ReadTypedHandler<ProbeMessage>(handler, bus);
                Array slots = ReadArrayField(typedHandler, "_slots");
                int slotIndex = TypedSlotIndex.UntargetedHandleDefault;
                Assert.IsInstanceOf<IEvictableSlot>(slots.GetValue(slotIndex));

                System.Collections.Generic.List<object> pooledList = DxPools.ObjectLists.Rent();
                DxPools.ObjectLists.Return(pooledList);
                Assert.Greater(DxPools.DescribeAll().ObjectLists.Cached, 0);

                clock.SetTo(12d);
                IMessageBus.TrimResult result = bus.Trim(force: true);

                Assert.IsNull(slots.GetValue(slotIndex));
                Assert.GreaterOrEqual(result.TypeSlotsEvicted, 1);
                Assert.Greater(result.PooledCollectionsEvicted, 0);
                Assert.AreEqual(0, DxPools.DescribeAll().ObjectLists.Cached);
                Assert.AreEqual(12d, ReadDoubleField(bus, "_lastSweepSeconds"));
                Assert.AreEqual(0, ReadCollectionCount(bus, "_dirtyTypes"));
                Assert.AreEqual(0, ReadCollectionCount(bus, "_dirtyTargets"));
                Assert.AreEqual(0, ReadCollectionCount(bus, "_dirtyHandlers"));
            }
            finally
            {
                handler.active = false;
            }
        }

        [Test]
        public void NonForceSweepRetainsFreshDirtyTypedSlotsUntilIdle()
        {
            ManualClock clock = new ManualClock();
            MessageBus bus = MessageBus.CreateForInternalUse(clock, idleEvictionTicks: 1);
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
                deregistration();
                object typedHandler = ReadTypedHandler<ProbeMessage>(handler, bus);
                Array slots = ReadArrayField(typedHandler, "_slots");
                int slotIndex = TypedSlotIndex.UntargetedHandleDefault;

                IMessageBus.TrimResult freshResult = bus.Trim(force: false);

                Assert.IsInstanceOf<IEvictableSlot>(slots.GetValue(slotIndex));
                Assert.AreEqual(0, freshResult.TypeSlotsEvicted);
                Assert.AreEqual(1, ReadCollectionCount(bus, "_dirtyTypes"));
                Assert.AreEqual(1, ReadCollectionCount(bus, "_dirtyHandlers"));

                OtherProbeMessage other = new OtherProbeMessage();
                bus.UntargetedBroadcast(ref other);
                bus.UntargetedBroadcast(ref other);
                IMessageBus.TrimResult idleResult = bus.Trim(force: false);

                Assert.IsNull(slots.GetValue(slotIndex));
                Assert.GreaterOrEqual(idleResult.TypeSlotsEvicted, 1);
                Assert.AreEqual(0, ReadCollectionCount(bus, "_dirtyTypes"));
                Assert.AreEqual(0, ReadCollectionCount(bus, "_dirtyHandlers"));
            }
            finally
            {
                handler.active = false;
            }
        }

        [Test]
        public void ForceSweepPreservesActiveTypedRegistration()
        {
            MessageBus bus = MessageBus.CreateForInternalUse(
                new ManualClock(),
                idleEvictionTicks: 0
            );
            MessageHandler handler = new MessageHandler(HandlerOwner, bus) { active = true };
            try
            {
                int calls = 0;
                Action<ProbeMessage> callback = _ =>
                {
                    calls++;
                };
                _ = handler.RegisterUntargetedMessageHandler(
                    callback,
                    callback,
                    priority: 17,
                    messageBus: bus
                );

                _ = bus.Trim(force: true);
                ProbeMessage message = new ProbeMessage();
                bus.UntargetedBroadcast(ref message);

                Assert.AreEqual(1, calls);
            }
            finally
            {
                handler.active = false;
            }
        }

        [Test]
        public void StaleDeregisterAfterEmptySweepDoesNotRemoveReRegisteredHandler()
        {
            MessageBus bus = MessageBus.CreateForInternalUse(
                new ManualClock(),
                idleEvictionTicks: 0
            );
            MessageHandler handler = new MessageHandler(HandlerOwner, bus) { active = true };
            try
            {
                int staleCalls = 0;
                int currentCalls = 0;
                Action<ProbeMessage> original = _ => { };
                Action<ProbeMessage> stale = _ =>
                {
                    staleCalls++;
                };
                Action<ProbeMessage> current = _ =>
                {
                    currentCalls++;
                };

                Action staleDeregistration = handler.RegisterUntargetedMessageHandler(
                    original,
                    stale,
                    priority: 17,
                    messageBus: bus
                );
                staleDeregistration();
                _ = bus.Trim(force: true);

                _ = handler.RegisterUntargetedMessageHandler(
                    current,
                    current,
                    priority: 17,
                    messageBus: bus
                );
                staleDeregistration();

                ProbeMessage message = new ProbeMessage();
                bus.UntargetedBroadcast(ref message);

                Assert.AreEqual(0, staleCalls);
                Assert.AreEqual(1, currentCalls);
            }
            finally
            {
                handler.active = false;
            }
        }

        [Test]
        public void TrimReclaimsBusSlotAfterDispatchThenDeregister()
        {
            MessageBus bus = MessageBus.CreateForInternalUse(
                new ManualClock(),
                idleEvictionTicks: 0
            );
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
                ProbeMessage message = new ProbeMessage();
                bus.UntargetedBroadcast(ref message);

                deregistration();

                Assert.GreaterOrEqual(
                    bus.OccupiedTypeSlots,
                    1,
                    "Final deregistration must leave empty bus-side slots reachable for Trim."
                );

                IMessageBus.TrimResult result = bus.Trim(force: true);

                Assert.GreaterOrEqual(result.TypeSlotsEvicted, 1);
                Assert.AreEqual(0, bus.OccupiedTypeSlots);
                Assert.AreEqual(0, result.LiveTypeSlotsRemaining);
            }
            finally
            {
                handler.active = false;
            }
        }

        [Test]
        public void ForceTrimOnFreshBusDoesNotReportGlobalSlotEviction()
        {
            MessageBus bus = MessageBus.CreateForInternalUse(
                new ManualClock(),
                idleEvictionTicks: 0
            );

            IMessageBus.TrimResult result = bus.Trim(force: true);

            Assert.AreEqual(0, result.TypeSlotsEvicted);
            Assert.AreEqual(0, result.TargetSlotsEvicted);
            Assert.AreEqual(0, bus.OccupiedTypeSlots);
            Assert.AreEqual(0, bus.OccupiedTargetSlots);
        }

        [Test]
        public void StaleScalarDeregisterAfterSweepDoesNotLogOverDeregistration()
        {
            MessageBus bus = MessageBus.CreateForInternalUse(
                new ManualClock(),
                idleEvictionTicks: 0
            );
            MessageHandler handler = new MessageHandler(HandlerOwner, bus) { active = true };
            Action<LogLevel, string> previousLog = MessagingDebug.LogFunction;
            bool previousEnabled = MessagingDebug.enabled;
            System.Collections.Generic.List<string> logs =
                new System.Collections.Generic.List<string>();
            try
            {
                Action<ProbeMessage> callback = _ => { };
                Action deregistration = handler.RegisterUntargetedMessageHandler(
                    callback,
                    callback,
                    priority: 17,
                    messageBus: bus
                );
                deregistration();
                _ = bus.Trim(force: true);

                MessagingDebug.enabled = true;
                MessagingDebug.LogFunction = (_, message) => logs.Add(message);
                deregistration();

                Assert.AreEqual(0, logs.Count);
            }
            finally
            {
                MessagingDebug.enabled = previousEnabled;
                MessagingDebug.LogFunction = previousLog;
                handler.active = false;
            }
        }

        [Test]
        public void StaleContextDeregisterAfterSweepDoesNotLogOverDeregistration()
        {
            MessageBus bus = MessageBus.CreateForInternalUse(
                new ManualClock(),
                idleEvictionTicks: 0
            );
            MessageHandler handler = new MessageHandler(HandlerOwner, bus) { active = true };
            Action<LogLevel, string> previousLog = MessagingDebug.LogFunction;
            bool previousEnabled = MessagingDebug.enabled;
            System.Collections.Generic.List<string> logs =
                new System.Collections.Generic.List<string>();
            try
            {
                InstanceId context = new InstanceId(0x5044_4203);
                Action<TargetedProbeMessage> callback = _ => { };
                Action deregistration = handler.RegisterTargetedMessageHandler(
                    context,
                    callback,
                    callback,
                    priority: 17,
                    messageBus: bus
                );
                deregistration();
                _ = bus.Trim(force: true);

                MessagingDebug.enabled = true;
                MessagingDebug.LogFunction = (_, message) => logs.Add(message);
                deregistration();

                Assert.AreEqual(0, logs.Count);
            }
            finally
            {
                MessagingDebug.enabled = previousEnabled;
                MessagingDebug.LogFunction = previousLog;
                handler.active = false;
            }
        }

        [Test]
        public void EmitTimeSweepReclaimsIdleDirtySlotsWhenCadenceHasElapsed()
        {
            ManualClock clock = new ManualClock();
            MessageBus bus = MessageBus.CreateForInternalUse(
                clock,
                idleEvictionTicks: 0,
                evictionTickIntervalSeconds: 0d,
                idleEvictionEnabled: true,
                trimApiEnabled: true
            );
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
                deregistration();
                Assert.GreaterOrEqual(bus.OccupiedTypeSlots, 1);

                OtherProbeMessage other = new OtherProbeMessage();
                EmitUntargetedSweepSampleWindow(bus, ref other);

                Assert.AreEqual(0, bus.OccupiedTypeSlots);
                Assert.AreEqual(0, ReadCollectionCount(bus, "_dirtyTypes"));
                Assert.AreEqual(0, ReadCollectionCount(bus, "_dirtyHandlers"));
            }
            finally
            {
                handler.active = false;
            }
        }

        [Test]
        public void EmitTimeSweepWaitsForCadenceInterval()
        {
            ManualClock clock = new ManualClock();
            MessageBus bus = MessageBus.CreateForInternalUse(
                clock,
                idleEvictionTicks: 0,
                evictionTickIntervalSeconds: 10d,
                idleEvictionEnabled: true,
                trimApiEnabled: true
            );
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
                deregistration();
                OtherProbeMessage other = new OtherProbeMessage();

                clock.SetTo(9d);
                EmitUntargetedSweepSampleWindow(bus, ref other);

                Assert.GreaterOrEqual(bus.OccupiedTypeSlots, 1);

                clock.SetTo(10d);
                EmitUntargetedSweepSampleWindow(bus, ref other);

                Assert.AreEqual(0, bus.OccupiedTypeSlots);
                Assert.AreEqual(10d, ReadDoubleField(bus, "_lastSweepSeconds"));
            }
            finally
            {
                handler.active = false;
            }
        }

        [Test]
        public void EmitTimeSweepPreservesActiveRegistration()
        {
            MessageBus bus = MessageBus.CreateForInternalUse(
                new ManualClock(),
                idleEvictionTicks: 0,
                evictionTickIntervalSeconds: 0d,
                idleEvictionEnabled: true,
                trimApiEnabled: true
            );
            MessageHandler handler = new MessageHandler(HandlerOwner, bus) { active = true };
            try
            {
                int calls = 0;
                Action<ProbeMessage> callback = _ =>
                {
                    calls++;
                };
                _ = handler.RegisterUntargetedMessageHandler(
                    callback,
                    callback,
                    priority: 17,
                    messageBus: bus
                );

                ProbeMessage message = new ProbeMessage();
                EmitUntargetedSweepSampleWindow(bus, ref message);

                Assert.AreEqual(SweepSampleWindowEmits, calls);
                Assert.GreaterOrEqual(bus.OccupiedTypeSlots, 1);
            }
            finally
            {
                handler.active = false;
            }
        }

        [Test]
        public void DisabledIdleEvictionPreventsEmitTimeSweep()
        {
            MessageBus bus = MessageBus.CreateForInternalUse(
                new ManualClock(),
                idleEvictionTicks: 0,
                evictionTickIntervalSeconds: 0d,
                idleEvictionEnabled: false,
                trimApiEnabled: true
            );
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
                deregistration();

                OtherProbeMessage other = new OtherProbeMessage();
                EmitUntargetedSweepSampleWindow(bus, ref other);

                Assert.GreaterOrEqual(bus.OccupiedTypeSlots, 1);
                Assert.AreEqual(1, ReadCollectionCount(bus, "_dirtyTypes"));
                Assert.AreEqual(1, ReadCollectionCount(bus, "_dirtyHandlers"));
            }
            finally
            {
                handler.active = false;
            }
        }

        [Test]
        public void TrimReturnsDefaultAndLeavesStateWhenTrimApiIsDisabled()
        {
            MessageBus bus = MessageBus.CreateForInternalUse(
                new ManualClock(),
                idleEvictionTicks: 0,
                evictionTickIntervalSeconds: 0d,
                idleEvictionEnabled: true,
                trimApiEnabled: false
            );
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
                deregistration();
                System.Collections.Generic.List<object> pooledList = DxPools.ObjectLists.Rent();
                DxPools.ObjectLists.Return(pooledList);
                int cachedBefore = DxPools.DescribeAll().ObjectLists.Cached;

                IMessageBus.TrimResult result = bus.Trim(force: true);

                Assert.AreEqual(0, result.TypeSlotsEvicted);
                Assert.AreEqual(0, result.TargetSlotsEvicted);
                Assert.AreEqual(0, result.PooledCollectionsEvicted);
                Assert.AreEqual(0, result.LiveTypeSlotsRemaining);
                Assert.GreaterOrEqual(bus.OccupiedTypeSlots, 1);
                Assert.AreEqual(cachedBefore, DxPools.DescribeAll().ObjectLists.Cached);
            }
            finally
            {
                handler.active = false;
            }
        }

        [Test]
        public void LifoCollectionPoolIgnoresDuplicateReturns()
        {
            CollectionPool<System.Collections.Generic.List<object>> pool =
                new CollectionPool<System.Collections.Generic.List<object>>(
                    maxRetained: 2,
                    useLru: false,
                    factory: () => new System.Collections.Generic.List<object>(),
                    onRecycled: list => list.Clear()
                );
            System.Collections.Generic.List<object> item =
                new System.Collections.Generic.List<object>();

            pool.Return(item);
            pool.Return(item);

            Assert.AreEqual(1, pool.Count);
            Assert.AreSame(item, pool.Rent());
            Assert.AreNotSame(item, pool.Rent());
        }

        [Test]
        public void RentAndAddReturnsValueWhenOwnerInsertionThrows()
        {
            CollectionPool<System.Collections.Generic.List<object>> pool =
                new CollectionPool<System.Collections.Generic.List<object>>(
                    maxRetained: 1,
                    useLru: true,
                    factory: () => new System.Collections.Generic.List<object>(),
                    onRecycled: list => list.Clear()
                );
            System.Collections.Generic.List<object> expected =
                new System.Collections.Generic.List<object>();
            pool.Return(expected);
            System.Collections.Generic.Dictionary<
                int,
                System.Collections.Generic.List<object>
            > owner = new System.Collections.Generic.Dictionary<
                int,
                System.Collections.Generic.List<object>
            >(new ThrowingIntComparer());

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
                pool.RentAndAdd(owner, 17)
            );

            Assert.AreEqual("Injected owner insertion failure.", exception.Message);
            Assert.AreEqual(0, owner.Count);
            Assert.AreEqual(1, pool.Count);
            Assert.AreSame(expected, pool.Rent());
        }

        [Test]
        public void EmitTimeSweepRunsForTargetedAndBroadcastTypedEntryPoints()
        {
            ManualClock clock = new ManualClock();
            MessageBus bus = MessageBus.CreateForInternalUse(
                clock,
                idleEvictionTicks: 0,
                evictionTickIntervalSeconds: 0d,
                idleEvictionEnabled: true,
                trimApiEnabled: true
            );
            MessageHandler handler = new MessageHandler(HandlerOwner, bus) { active = true };
            try
            {
                InstanceId context = new InstanceId(0x5044_4202);
                Action<TargetedProbeMessage> targetedCallback = _ => { };
                Action<BroadcastProbeMessage> broadcastCallback = _ => { };
                Action deregisterTargeted = handler.RegisterTargetedMessageHandler(
                    context,
                    targetedCallback,
                    targetedCallback,
                    priority: 17,
                    messageBus: bus
                );
                Action deregisterBroadcast = handler.RegisterSourcedBroadcastMessageHandler(
                    context,
                    broadcastCallback,
                    broadcastCallback,
                    priority: 17,
                    messageBus: bus
                );
                deregisterTargeted();
                deregisterBroadcast();
                Assert.GreaterOrEqual(bus.OccupiedTargetSlots, 2);

                TargetedProbeMessage targeted = new TargetedProbeMessage();
                EmitTargetedSweepSampleWindow(bus, ref context, ref targeted);

                Assert.LessOrEqual(bus.OccupiedTargetSlots, 1);

                BroadcastProbeMessage broadcast = new BroadcastProbeMessage();
                EmitSourcedSweepSampleWindow(bus, ref context, ref broadcast);

                Assert.AreEqual(0, bus.OccupiedTargetSlots);
            }
            finally
            {
                handler.active = false;
            }
        }

#if UNITY_2021_3_OR_NEWER
        [Test]
        public void RuntimeSettingsHotReloadUpdatesSweepGatesAndPoolCaps()
        {
            ManualClock clock = new ManualClock();
            MessageBus bus = MessageBus.CreateForInternalUse(clock);
            DxMessagingRuntimeSettings settings =
                ScriptableObject.CreateInstance<DxMessagingRuntimeSettings>();
            try
            {
                System.Collections.Generic.List<object> pooledList = DxPools.ObjectLists.Rent();
                DxPools.ObjectLists.Return(pooledList);
                Assert.Greater(DxPools.DescribeAll().ObjectLists.Cached, 0);

                settings._idleEvictionSeconds = 7f;
                settings._evictionTickIntervalSeconds = 3f;
                settings._evictionEnabled = false;
                settings._enableTrimApi = false;
                settings._bufferMaxDistinctEntries = 0;
                settings._bufferUseLruEviction = false;

                DxMessagingRuntimeSettings.RaiseSettingsChanged(settings);

                Assert.AreEqual(7L, ReadLongField(bus, "_idleEvictionTicks"));
                Assert.AreEqual(3d, ReadDoubleField(bus, "_evictionTickIntervalSeconds"));
                Assert.IsFalse(ReadBoolField(bus, "_idleEvictionEnabled"));
                Assert.IsFalse(ReadBoolField(bus, "_trimApiEnabled"));
                Assert.AreEqual(0, DxPools.ObjectLists.MaxRetained);
                Assert.IsFalse(DxPools.ObjectLists.UseLru);
                Assert.AreEqual(0, DxPools.DescribeAll().ObjectLists.Cached);

                DxMessagingRuntimeSettingsProvider.ResetForTests();
                using (DxMessagingRuntimeSettingsProvider.Override(settings)) { }

                Assert.AreEqual(30L, ReadLongField(bus, "_idleEvictionTicks"));
                Assert.AreEqual(5d, ReadDoubleField(bus, "_evictionTickIntervalSeconds"));
                Assert.IsTrue(ReadBoolField(bus, "_idleEvictionEnabled"));
                Assert.IsTrue(ReadBoolField(bus, "_trimApiEnabled"));
                Assert.AreEqual(
                    DxMessagingRuntimeSettings.DefaultBufferMaxDistinctEntries,
                    DxPools.ObjectLists.MaxRetained
                );
                Assert.IsTrue(DxPools.ObjectLists.UseLru);
            }
            finally
            {
                DxMessagingRuntimeSettingsProvider.ResetForTests();
                DxMessagingRuntimeSettings.RaiseSettingsChanged(
                    DxMessagingRuntimeSettingsProvider.Current
                );
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void PlayerLoopHookInstallsOnceUnderUpdate()
        {
            PlayerLoopSystem root = new PlayerLoopSystem
            {
                type = typeof(EvictionSweepContractTests),
                subSystemList = new[] { new PlayerLoopSystem { type = typeof(Update) } },
            };

            bool firstInstall = EvictionPlayerLoopHook.InstallInto(ref root);
            bool secondInstall = EvictionPlayerLoopHook.InstallInto(ref root);

            Assert.IsTrue(firstInstall);
            Assert.IsFalse(secondInstall);
            Assert.IsTrue(EvictionPlayerLoopHook.ContainsHook(root));
            Assert.AreEqual(1, CountPlayerLoopHook(root));
        }

        [Test]
        public void PlayerLoopSweepAgesIdleCandidatesWithoutEmit()
        {
            ManualClock clock = new ManualClock();
            MessageBus bus = MessageBus.CreateForInternalUse(
                clock,
                idleEvictionTicks: 0,
                evictionTickIntervalSeconds: 0d,
                idleEvictionEnabled: true,
                trimApiEnabled: true
            );
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
                deregistration();

                Assert.GreaterOrEqual(bus.OccupiedTypeSlots, 1);

                MessageBus.SweepIdleBusesFromPlayerLoop();

                Assert.AreEqual(0, bus.OccupiedTypeSlots);
            }
            finally
            {
                handler.active = false;
            }
        }
#endif

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

        private static void EmitUntargetedSweepSampleWindow<TMessage>(
            MessageBus bus,
            ref TMessage message
        )
            where TMessage : IUntargetedMessage
        {
            for (int i = 0; i < SweepSampleWindowEmits; i++)
            {
                bus.UntargetedBroadcast(ref message);
            }
        }

        private static void EmitTargetedSweepSampleWindow<TMessage>(
            MessageBus bus,
            ref InstanceId target,
            ref TMessage message
        )
            where TMessage : ITargetedMessage
        {
            for (int i = 0; i < SweepSampleWindowEmits; i++)
            {
                bus.TargetedBroadcast(ref target, ref message);
            }
        }

        private static void EmitSourcedSweepSampleWindow<TMessage>(
            MessageBus bus,
            ref InstanceId source,
            ref TMessage message
        )
            where TMessage : IBroadcastMessage
        {
            for (int i = 0; i < SweepSampleWindowEmits; i++)
            {
                bus.SourcedBroadcast(ref source, ref message);
            }
        }

        private const int SweepSampleWindowEmits = MessageBus.SweepGateSampleSize + 1;

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

        private static double ReadDoubleField(MessageBus bus, string name)
        {
            FieldInfo field = typeof(MessageBus).GetField(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            Assert.IsNotNull(field, "MessageBus must declare field '" + name + "'.");
            return (double)field.GetValue(bus);
        }

        private static long ReadLongField(MessageBus bus, string name)
        {
            FieldInfo field = typeof(MessageBus).GetField(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            Assert.IsNotNull(field, "MessageBus must declare field '" + name + "'.");
            return (long)field.GetValue(bus);
        }

        private static bool ReadBoolField(MessageBus bus, string name)
        {
            FieldInfo field = typeof(MessageBus).GetField(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            Assert.IsNotNull(field, "MessageBus must declare field '" + name + "'.");
            return (bool)field.GetValue(bus);
        }

        private static int ReadCollectionCount(MessageBus bus, string name)
        {
            FieldInfo field = typeof(MessageBus).GetField(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            Assert.IsNotNull(field, "MessageBus must declare field '" + name + "'.");
            object value = field.GetValue(bus);
            PropertyInfo count = value.GetType().GetProperty("Count");
            Assert.IsNotNull(count, name + " must expose Count.");
            return (int)count.GetValue(value);
        }

#if UNITY_2021_3_OR_NEWER
        private static int CountPlayerLoopHook(PlayerLoopSystem system)
        {
            int count = system.type == typeof(EvictionPlayerLoopHook) ? 1 : 0;
            PlayerLoopSystem[] subsystems = system.subSystemList;
            if (subsystems == null)
            {
                return count;
            }

            for (int i = 0; i < subsystems.Length; ++i)
            {
                count += CountPlayerLoopHook(subsystems[i]);
            }

            return count;
        }
#endif

        private sealed class ManualClock : IDxMessagingClock
        {
            private double _now;

            public ManualClock(double nowSeconds = 0d)
            {
                _now = nowSeconds;
            }

            public double NowSeconds => _now;

            public void SetTo(double nowSeconds)
            {
                _now = nowSeconds;
            }
        }
    }
}
