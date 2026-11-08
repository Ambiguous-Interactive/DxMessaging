namespace DxMessaging.Tests.Runtime.MemoryReclaim
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Core.Pooling;
    using DxMessaging.Tests.Runtime;
    using NUnit.Framework;
    using Bus = DxMessaging.Core.MessageBus.MessageBus;

    /// <summary>Keeps empty dispatch storage scheduled until an in-flight lease ends.</summary>
    /// <remarks>
    /// 2026-11-07: An ordinary trim must retain an empty cache protected by an active
    /// snapshot even after its idle age has elapsed. A later ordinary trim must reclaim
    /// it without requiring new registration, force, or private-state mutation.
    /// </remarks>
    [TestFixture]
    [Category("MemoryReclaim")]
    public sealed class InFlightIdleSweepTests
    {
        private static readonly InstanceId Context = new InstanceId(9517);

        private static IEnumerable<TestCaseData> Cases()
        {
            foreach (
                MessageScenario scenario in MessageScenarios.WithAndWithoutPostProcessorIncludingWithoutContext
            )
            {
                foreach (bool fast in new[] { false, true })
                {
                    if (
                        scenario.Kind == MessageKind.Untargeted
                        && scenario.UsePostProcessor
                        && !fast
                    )
                    {
                        continue;
                    }
                    foreach (bool force in new[] { false, true })
                    {
                        foreach (bool aged in new[] { false, true })
                        {
                            foreach (bool nested in new[] { false, true })
                            {
                                yield return new TestCaseData(
                                    scenario,
                                    fast,
                                    force,
                                    aged,
                                    nested
                                ).SetName(
                                    $"InFlightIdleSweep_{scenario}_fast{fast}_force{force}_aged{aged}_nested{nested}"
                                );
                            }
                        }
                    }
                }
            }
        }

        [TestCaseSource(nameof(Cases))]
        public void OrdinaryTrimReclaimsEmptySlotsAfterTheActiveLeaseEnds(
            MessageScenario scenario,
            bool fast,
            bool force,
            bool aged,
            bool nested
        )
        {
            string label = $"{scenario},fast={fast},force={force},aged={aged},nested={nested}";
            Bus bus = Bus.CreateForInternalUse(
                new ConstantClock(),
                idleEvictionTicks: 0,
                idleEvictionEnabled: false,
                trimApiEnabled: true
            );
            MessageHandler handler = new MessageHandler(new InstanceId(9518), bus)
            {
                active = true,
            };
            using LeakWatcher watch = LeakWatcher.WatchWithSlots(
                bus,
                label: label,
                handler: handler
            );
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            MessageRegistrationToken outer = MessageRegistrationToken.Create(handler, bus);
            MessageRegistrationToken fresh = MessageRegistrationToken.Create(handler, bus);
            bool armed = false;
            int calls = 0;
            int freshCalls = 0;
            int inFlightSlots = -1;
            try
            {
                Register(
                    token,
                    scenario,
                    fast,
                    () =>
                    {
                        ++calls;
                        if (!armed)
                        {
                            return;
                        }
                        armed = false;
                        token.Disable();
                        if (aged)
                        {
                            Tick(bus);
                        }
                        bus.Trim(force);
                        inFlightSlots = bus.OccupiedTypeSlots;
                        TestContext.WriteLine(
                            $"{label}: in-flight slots={inFlightSlots}, depth={MessageBusInspection.ReadField(bus, "_dispatchDepth")}, dirtyTypes={ReadDirtyTypeCount(bus)}"
                        );
                    },
                    priority: 3
                );
                outer.RegisterUntargeted<OuterMessage>(_ => Emit(bus, scenario.Kind));
                token.Enable();
                Emit(bus, scenario.Kind);
                Assert.That(calls, Is.EqualTo(1), label + ": warm control delivers exactly once");
                armed = true;
                if (nested)
                {
                    outer.Enable();
                    OuterMessage outerMessage = default;
                    bus.UntargetedBroadcast(ref outerMessage);
                    outer.Disable();
                }
                else
                {
                    Emit(bus, scenario.Kind);
                }
                Assert.That(calls, Is.EqualTo(2), label + ": armed callback delivers exactly once");
                Assert.That(
                    inFlightSlots,
                    Is.GreaterThan(0),
                    label + ": active snapshot keeps storage alive in flight"
                );
                Assert.That(
                    watch.Snapshot,
                    Is.Zero,
                    label + ": both tokens have no live registrations after dispatch"
                );
                Tick(bus);
                IMessageBus.TrimResult result = bus.Trim(force: false);
                TestContext.WriteLine(
                    $"{label}: after lease slots={bus.OccupiedTypeSlots}, targetSlots={bus.OccupiedTargetSlots}, typeEvictions={result.TypeSlotsEvicted}, dirtyTypes={ReadDirtyTypeCount(bus)}"
                );
                Assert.That(
                    bus.OccupiedTypeSlots,
                    Is.Zero,
                    label + ": later ordinary trim must reclaim every empty candidate"
                );
                Assert.That(
                    bus.OccupiedTargetSlots,
                    Is.Zero,
                    label + ": later ordinary trim must reclaim every empty context"
                );
                Register(fresh, scenario, fast, () => ++freshCalls, priority: 3);
                fresh.Enable();
                token.Disable();
                Emit(bus, scenario.Kind);
                Assert.That(
                    freshCalls,
                    Is.EqualTo(1),
                    label + ": stale disabled token cannot consume a fresh callback"
                );
                Assert.That(calls, Is.EqualTo(2), label + ": retired callback remains retired");
            }
            finally
            {
                fresh.Dispose();
                outer.Dispose();
                token.Dispose();
                bus.Trim(force: true);
                handler.active = false;
            }
        }

        private static int ReadDirtyTypeCount(Bus bus) =>
            (
                (System.Collections.ICollection)MessageBusInspection.ReadField(bus, "_dirtyTypes")
            ).Count;

        private static void Tick(Bus bus)
        {
            TickMessage message = default;
            bus.UntargetedBroadcast(ref message);
        }

        private sealed class ConstantClock : IDxMessagingClock
        {
            public double NowSeconds => 0d;
        }

        private readonly struct TickMessage : IUntargetedMessage
        {
            public Type MessageType => typeof(TickMessage);
        }

        private readonly struct OuterMessage : IUntargetedMessage
        {
            public Type MessageType => typeof(OuterMessage);
        }

        private static MessageRegistrationHandle Register(
            MessageRegistrationToken token,
            MessageScenario scenario,
            bool useFast,
            Action record,
            int priority
        )
        {
            Action<ProbeMessage> scalar = _ => record();
            Action<InstanceId, ProbeMessage> context = (_, __) => record();
            MessageHandler.FastHandler<ProbeMessage> fast = (in ProbeMessage _) => record();
            MessageHandler.FastHandlerWithContext<ProbeMessage> fastContext = (
                in InstanceId _,
                in ProbeMessage __
            ) => record();
            return (scenario.Kind, scenario.UsePostProcessor, !useFast) switch
            {
                (MessageKind.Untargeted, false, true) => token.RegisterUntargeted(
                    scalar,
                    priority: priority
                ),
                (MessageKind.Untargeted, false, false) => token.RegisterUntargeted(
                    fast,
                    priority: priority
                ),
                (MessageKind.Untargeted, true, _) => token.RegisterUntargetedPostProcessor(
                    fast,
                    priority: priority
                ),
                (MessageKind.Targeted, false, true) => token.RegisterTargeted(
                    Context,
                    scalar,
                    priority: priority
                ),
                (MessageKind.Targeted, false, false) => token.RegisterTargeted(
                    Context,
                    fast,
                    priority: priority
                ),
                (MessageKind.Targeted, true, true) => token.RegisterTargetedPostProcessor(
                    Context,
                    scalar,
                    priority: priority
                ),
                (MessageKind.Targeted, true, false) => token.RegisterTargetedPostProcessor(
                    Context,
                    fast,
                    priority: priority
                ),
                (MessageKind.Broadcast, false, true) => token.RegisterBroadcast(
                    Context,
                    scalar,
                    priority: priority
                ),
                (MessageKind.Broadcast, false, false) => token.RegisterBroadcast(
                    Context,
                    fast,
                    priority: priority
                ),
                (MessageKind.Broadcast, true, true) => token.RegisterBroadcastPostProcessor(
                    Context,
                    scalar,
                    priority: priority
                ),
                (MessageKind.Broadcast, true, false) => token.RegisterBroadcastPostProcessor(
                    Context,
                    fast,
                    priority: priority
                ),
                (MessageKind.TargetedWithoutTargeting, false, true) =>
                    token.RegisterTargetedWithoutTargeting(context, priority: priority),
                (MessageKind.TargetedWithoutTargeting, false, false) =>
                    token.RegisterTargetedWithoutTargeting(fastContext, priority: priority),
                (MessageKind.TargetedWithoutTargeting, true, true) =>
                    token.RegisterTargetedWithoutTargetingPostProcessor(
                        context,
                        priority: priority
                    ),
                (MessageKind.TargetedWithoutTargeting, true, false) =>
                    token.RegisterTargetedWithoutTargetingPostProcessor(
                        fastContext,
                        priority: priority
                    ),
                (MessageKind.BroadcastWithoutSource, false, true) =>
                    token.RegisterBroadcastWithoutSource(context, priority: priority),
                (MessageKind.BroadcastWithoutSource, false, false) =>
                    token.RegisterBroadcastWithoutSource(fastContext, priority: priority),
                (MessageKind.BroadcastWithoutSource, true, true) =>
                    token.RegisterBroadcastWithoutSourcePostProcessor(context, priority: priority),
                (MessageKind.BroadcastWithoutSource, true, false) =>
                    token.RegisterBroadcastWithoutSourcePostProcessor(
                        fastContext,
                        priority: priority
                    ),
                _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
            };
        }

        private static void Emit(Bus bus, MessageKind kind)
        {
            ProbeMessage message = default;
            InstanceId context = Context;
            switch (kind)
            {
                case MessageKind.Untargeted:
                    bus.UntargetedBroadcast(ref message);
                    break;
                case MessageKind.Targeted:
                case MessageKind.TargetedWithoutTargeting:
                    bus.TargetedBroadcast(ref context, ref message);
                    break;
                case MessageKind.Broadcast:
                case MessageKind.BroadcastWithoutSource:
                    bus.SourcedBroadcast(ref context, ref message);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        private readonly struct ProbeMessage
            : IUntargetedMessage,
                ITargetedMessage,
                IBroadcastMessage
        {
            public Type MessageType => typeof(ProbeMessage);
        }
    }
}
