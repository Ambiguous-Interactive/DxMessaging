#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Editor.Allocations
{
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Pooling;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;

    [Category("PerfGate")]
    public sealed class EmitGateClockReadIsRare
    {
        [Test]
        public void EmitsSampleClockNoMoreThanOncePerSixteenEmits(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.AllKindsIncludingWithoutContext)
            )]
                MessageScenario scenario
        )
        {
            const int emits = 10_000;
            CountingClock probeClock = new();
            MessageBus bus = MessageBus.CreateForInternalUse(
                probeClock,
                idleEvictionTicks: 0,
                evictionTickIntervalSeconds: 60,
                idleEvictionEnabled: true,
                trimApiEnabled: true
            );
            MessageHandler handler = new(new InstanceId(0x6501), bus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            token.Enable();

            try
            {
                RegisterNoOpHandler(scenario, token, handler.owner);
                for (int index = 0; index < emits; index++)
                {
                    Emit(scenario, bus, handler.owner);
                }

                int expectedMaximumClockReads =
                    (emits + MessageBus.SweepGateSampleSize - 1) / MessageBus.SweepGateSampleSize
                    + 1;
                Assert.LessOrEqual(
                    probeClock.ReadCount,
                    expectedMaximumClockReads,
                    "[{0}] the idle-sweep gate should sample wall-clock time rather than reading it on every emit. Reads={1}, Emits={2}, SampleSize={3}.",
                    scenario.Kind,
                    probeClock.ReadCount,
                    emits,
                    MessageBus.SweepGateSampleSize
                );
            }
            finally
            {
                token.UnregisterAll();
                token.Dispose();
            }
        }

        [Test]
        public void DisabledIdleEvictionDoesNotReadClockAfterConstruction(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.AllKindsIncludingWithoutContext)
            )]
                MessageScenario scenario
        )
        {
            const int emits = MessageBus.SweepGateSampleSize * 2;
            CountingClock probeClock = new();
            MessageBus bus = MessageBus.CreateForInternalUse(
                probeClock,
                idleEvictionTicks: 0,
                evictionTickIntervalSeconds: 0,
                idleEvictionEnabled: false,
                trimApiEnabled: true
            );
            MessageHandler handler = new(new InstanceId(0x6502), bus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            token.Enable();

            try
            {
                RegisterNoOpHandler(scenario, token, handler.owner);
                long readsAfterConstruction = probeClock.ReadCount;
                for (int index = 0; index < emits; index++)
                {
                    Emit(scenario, bus, handler.owner);
                }

                Assert.AreEqual(
                    readsAfterConstruction,
                    probeClock.ReadCount,
                    "[{0}] disabled idle eviction should return before the sampled wall-clock gate. Reads={1}, Emits={2}.",
                    scenario.Kind,
                    probeClock.ReadCount,
                    emits
                );
            }
            finally
            {
                token.UnregisterAll();
                token.Dispose();
            }
        }

        [Test]
        public void EmitGateFirstSamplesClockOnSixteenthEmit()
        {
            CountingClock probeClock = new();
            MessageBus bus = MessageBus.CreateForInternalUse(
                probeClock,
                idleEvictionTicks: 0,
                evictionTickIntervalSeconds: 60,
                idleEvictionEnabled: true,
                trimApiEnabled: true
            );

            long readsAfterConstruction = probeClock.ReadCount;
            SimpleUntargetedMessage message = new();
            for (int emitIndex = 1; emitIndex < MessageBus.SweepGateSampleSize; emitIndex++)
            {
                bus.UntargetedBroadcast(ref message);
                Assert.AreEqual(
                    readsAfterConstruction,
                    probeClock.ReadCount,
                    "Emit {0} should not sample wall-clock time before the sweep gate reaches its sample window.",
                    emitIndex
                );
            }

            bus.UntargetedBroadcast(ref message);

            Assert.AreEqual(
                readsAfterConstruction + 1,
                probeClock.ReadCount,
                "The sixteenth emit should be the first emit-side sampled wall-clock read."
            );
        }

        [Test]
        public void SampledIdleSweepAddsOnlyOneSweepClockRead()
        {
            CountingClock probeClock = new();
            MessageBus bus = MessageBus.CreateForInternalUse(
                probeClock,
                idleEvictionTicks: 0,
                evictionTickIntervalSeconds: 0,
                idleEvictionEnabled: true,
                trimApiEnabled: true
            );
            MessageHandler handler = new(new InstanceId(0x6503), bus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            token.Enable();

            try
            {
                MessageRegistrationHandle handle =
                    token.RegisterUntargeted<SimpleUntargetedMessage>(NoOpUntargeted);
                token.RemoveRegistration(handle);
                SimpleUntargetedMessage message = new();
                for (int index = 0; index <= MessageBus.SweepGateSampleSize; index++)
                {
                    bus.UntargetedBroadcast(ref message);
                }

                Assert.AreEqual(0, bus.OccupiedTypeSlots);
                Assert.LessOrEqual(
                    probeClock.ReadCount,
                    4,
                    "A real sampled idle sweep should read the clock once for construction, at most twice for sampled cadence checks when the first sample fires before the dirty slot ages, and once to stamp the completed sweep. Reads={0}.",
                    probeClock.ReadCount
                );
            }
            finally
            {
                token.UnregisterAll();
                token.Dispose();
            }
        }

        [Test]
        public void MutationChurnDoesNotDriveEmitClockSampling()
        {
            const int emits = MessageBus.SweepGateSampleSize * 8;
            CountingClock probeClock = new();
            MessageBus bus = MessageBus.CreateForInternalUse(
                probeClock,
                idleEvictionTicks: 0,
                evictionTickIntervalSeconds: 60,
                idleEvictionEnabled: true,
                trimApiEnabled: true
            );
            MessageHandler handler = new(new InstanceId(0x6504), bus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            token.Enable();

            try
            {
                _ = token.RegisterUntargeted<SimpleUntargetedMessage>(NoOpUntargeted);
                for (int emitIndex = 0; emitIndex < emits; emitIndex++)
                {
                    for (
                        int mutationIndex = 0;
                        mutationIndex < MessageBus.SweepGateSampleSize - 1;
                        mutationIndex++
                    )
                    {
                        _ = token.RegisterUntargeted<ClassUntargetedMessage>(NoOpClassUntargeted);
                    }

                    SimpleUntargetedMessage message = new();
                    bus.UntargetedBroadcast(ref message);
                }

                int expectedMaximumClockReads = emits / MessageBus.SweepGateSampleSize + 1;
                Assert.LessOrEqual(
                    probeClock.ReadCount,
                    expectedMaximumClockReads,
                    "Registration churn between emits must not make the idle-sweep gate sample wall-clock time more often than the emit cadence. Reads={0}, Emits={1}, SampleSize={2}.",
                    probeClock.ReadCount,
                    emits,
                    MessageBus.SweepGateSampleSize
                );
            }
            finally
            {
                token.UnregisterAll();
                token.Dispose();
            }
        }

        private static void RegisterNoOpHandler(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId context
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    _ = token.RegisterUntargeted<SimpleUntargetedMessage>(NoOpUntargeted);
                    return;
                }
                case MessageKind.Targeted:
                {
                    _ = token.RegisterTargeted<SimpleTargetedMessage>(context, NoOpTargeted);
                    return;
                }
                case MessageKind.TargetedWithoutTargeting:
                {
                    _ = token.RegisterTargetedWithoutTargeting<SimpleTargetedMessage>(
                        NoOpTargetedWithoutTargeting
                    );
                    return;
                }
                case MessageKind.Broadcast:
                {
                    _ = token.RegisterBroadcast<SimpleBroadcastMessage>(context, NoOpBroadcast);
                    return;
                }
                case MessageKind.BroadcastWithoutSource:
                {
                    _ = token.RegisterBroadcastWithoutSource<SimpleBroadcastMessage>(
                        NoOpBroadcastWithoutSource
                    );
                    return;
                }
                default:
                {
                    throw new System.ArgumentOutOfRangeException(
                        nameof(scenario),
                        scenario.Kind,
                        "Unsupported message kind."
                    );
                }
            }
        }

        private static void Emit(MessageScenario scenario, MessageBus bus, InstanceId context)
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    SimpleUntargetedMessage message = new();
                    bus.UntargetedBroadcast(ref message);
                    return;
                }
                case MessageKind.Targeted:
                case MessageKind.TargetedWithoutTargeting:
                {
                    SimpleTargetedMessage message = new();
                    bus.TargetedBroadcast(ref context, ref message);
                    return;
                }
                case MessageKind.Broadcast:
                case MessageKind.BroadcastWithoutSource:
                {
                    SimpleBroadcastMessage message = new();
                    bus.SourcedBroadcast(ref context, ref message);
                    return;
                }
                default:
                {
                    throw new System.ArgumentOutOfRangeException(
                        nameof(scenario),
                        scenario.Kind,
                        "Unsupported message kind."
                    );
                }
            }
        }

        private static void NoOpUntargeted(in SimpleUntargetedMessage message) { }

        private static void NoOpTargeted(in SimpleTargetedMessage message) { }

        private static void NoOpBroadcast(in SimpleBroadcastMessage message) { }

        private static void NoOpClassUntargeted(in ClassUntargetedMessage message) { }

        private static void NoOpTargetedWithoutTargeting(
            in InstanceId target,
            in SimpleTargetedMessage message
        ) { }

        private static void NoOpBroadcastWithoutSource(
            in InstanceId source,
            in SimpleBroadcastMessage message
        ) { }

        private sealed class CountingClock : IDxMessagingClock
        {
            public long ReadCount { get; private set; }

            public double NowSeconds
            {
                get
                {
                    ReadCount++;
                    return 1d;
                }
            }
        }
    }
}
#endif
