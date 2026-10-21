namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Runtime;
    using NUnit.Framework;

    /// <summary>Preserves callback identity and order across inline and spilled storage.</summary>
    /// <remarks>
    /// 2026-10-21: Ordered handler storage uses delegates as keys. Receiver equality
    /// and hashing must not run while comparing those keys. Bulk removal, duplicate
    /// ownership and recycled staging slots must preserve the surviving callbacks.
    /// </remarks>
    [TestFixture]
    public sealed class OrderedHandlerEntryLifecycleTests
    {
        private static readonly InstanceId Context = new InstanceId(986);

        private static IEnumerable<TestCaseData> Cases()
        {
            foreach (
                MessageScenario scenario in MessageScenarios.WithAndWithoutPostProcessorIncludingWithoutContext
            )
            {
                foreach (bool action in new[] { false, true })
                {
                    if (
                        scenario.Kind == MessageKind.Untargeted
                        && scenario.UsePostProcessor
                        && action
                    )
                    {
                        continue;
                    }
                    foreach (TestCaseData testCase in StorageCases(scenario, action, false))
                    {
                        yield return testCase;
                    }
                }
            }
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            {
                foreach (bool action in new[] { false, true })
                {
                    foreach (TestCaseData testCase in StorageCases(scenario, action, true))
                    {
                        yield return testCase;
                    }
                }
            }
        }

        private static IEnumerable<TestCaseData> StorageCases(
            MessageScenario scenario,
            bool action,
            bool global
        )
        {
            foreach (int count in new[] { 2, 70 })
            {
                foreach (bool reverse in new[] { false, true })
                {
                    foreach (bool duplicate in new[] { false, true })
                    {
                        yield return new TestCaseData(
                            scenario,
                            action,
                            global,
                            count,
                            reverse,
                            duplicate
                        ).SetName(
                            $"OrderedHandlerEntries_{scenario.Kind}_Post{scenario.UsePostProcessor}_Action{action}_Global{global}_Count{count}_Reverse{reverse}_Duplicate{duplicate}"
                        );
                    }
                }
            }
        }

        [TestCaseSource(nameof(Cases))]
        public void DelegateIdentityAndRegistrationOrderSurviveRemovalAndSlotReuse(
            MessageScenario scenario,
            bool action,
            bool global,
            int count,
            bool reverse,
            bool duplicate
        )
        {
            string label =
                $"{scenario},action={action},global={global},count={count},reverse={reverse},duplicate={duplicate}";
            bool previousDebug = MessagingDebug.enabled;
            MessagingDebug.enabled = false;
            MessageBus bus = new MessageBus { DiagnosticsMode = false };
            MessageHandler handler = new MessageHandler(new InstanceId(987), bus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            token.DiagnosticMode = false;
            using LeakWatcher leaks = new LeakWatcher(bus, label: label);
            List<int> observed = new List<int>();
            List<int> expected = new List<int>();
            Receiver[] receivers = new Receiver[count];
            MessageRegistrationHandle[] first = new MessageRegistrationHandle[count];
            MessageRegistrationHandle[] second = duplicate
                ? new MessageRegistrationHandle[count]
                : first;
            List<MessageRegistrationHandle> oldHandles = new List<MessageRegistrationHandle>();
            HashSet<int> oldSlots = new HashSet<int>();
            HashSet<long> oldIdentities = new HashSet<long>();
            try
            {
                token.Enable();
                for (int i = 0; i < count; i++)
                {
                    receivers[i] = new Receiver(i, observed);
                    first[i] = Register(token, scenario, action, global, receivers[i]);
                    oldHandles.Add(first[i]);
                    oldSlots.Add(first[i].Slot);
                    oldIdentities.Add(first[i].Id);
                    expected.Add(i);
                }
                if (duplicate)
                {
                    for (int i = 0; i < count; i++)
                    {
                        second[i] = Register(token, scenario, action, global, receivers[i]);
                        oldHandles.Add(second[i]);
                        oldSlots.Add(second[i].Slot);
                        oldIdentities.Add(second[i].Id);
                    }
                }
                Assert.That(
                    token._metadata.Count,
                    Is.EqualTo(count * (duplicate ? 2 : 1)),
                    label + ": all staged identities retained"
                );
                Assert.That(
                    MessageBusInspection.CountLiveRegistrations(bus),
                    Is.EqualTo(count * (duplicate ? 2 : 1)),
                    label + ": actual reference-counted registrations"
                );
                Emit(bus, scenario);
                Assert.That(
                    observed,
                    Is.EqualTo(expected),
                    label + ": first registration order and one callback per distinct delegate"
                );
                observed.Clear();
                if (duplicate)
                {
                    Remove(token, first, reverse);
                    Assert.That(
                        token._metadata.Count,
                        Is.EqualTo(count),
                        label + ": duplicate owners remain staged"
                    );
                    Assert.That(
                        MessageBusInspection.CountLiveRegistrations(bus),
                        Is.EqualTo(count),
                        label + ": one reference per distinct callback remains"
                    );
                    Emit(bus, scenario);
                    Assert.That(
                        observed,
                        Is.EqualTo(expected),
                        label + ": duplicate removal preserves original callback order"
                    );
                    observed.Clear();
                }
                Remove(token, second, reverse);
                Assert.That(
                    token._metadata.Count,
                    Is.Zero,
                    label + ": bulk removal clears staging"
                );
                Assert.That(
                    MessageBusInspection.CountLiveRegistrations(bus),
                    Is.Zero,
                    label + ": bulk removal clears actual registrations"
                );
                Emit(bus, scenario);
                Assert.That(observed, Is.Empty, label + ": empty storage stays silent");
                for (int i = 0; i < count; i++)
                {
                    MessageRegistrationHandle fresh = Register(
                        token,
                        scenario,
                        action,
                        global,
                        receivers[i]
                    );
                    Assert.That(
                        oldSlots.Contains(fresh.Slot),
                        Is.True,
                        label + $": actual old arena slot reused for receiver{i}"
                    );
                    Assert.That(
                        oldIdentities.Contains(fresh.Id),
                        Is.False,
                        label + $": reused arena slot has fresh identity for receiver{i}"
                    );
                }
                foreach (MessageRegistrationHandle stale in oldHandles)
                {
                    token.RemoveRegistration(stale);
                }
                Assert.That(
                    token._metadata.Count,
                    Is.EqualTo(count),
                    label + ": stale handles cannot remove reused slots"
                );
                Assert.That(
                    MessageBusInspection.CountLiveRegistrations(bus),
                    Is.EqualTo(count),
                    label + ": stale handles preserve actual fresh registrations"
                );
                Emit(bus, scenario);
                Assert.That(
                    observed,
                    Is.EqualTo(expected),
                    label + ": fresh insertion order survives stale cleanup"
                );
                int expectedCalls = duplicate ? 3 : 2;
                foreach (Receiver receiver in receivers)
                {
                    Assert.That(
                        receiver.Calls,
                        Is.EqualTo(expectedCalls),
                        label + $": exact callback count for receiver{receiver.Id}"
                    );
                }
                token.Dispose();
                Assert.That(
                    token._metadata.Count,
                    Is.Zero,
                    label + ": disposal clears fresh staging"
                );
                Assert.That(token._callCounts.Count, Is.Zero, label + ": diagnostics remain empty");
                Assert.That(
                    token._emissionBuffer.Count,
                    Is.Zero,
                    label + ": no diagnostic history retained"
                );
                Assert.That(
                    MessageBusInspection.CountLiveRegistrations(bus),
                    Is.Zero,
                    label + ": disposal clears fresh registrations"
                );
                foreach (Receiver receiver in receivers)
                {
                    Assert.That(
                        receiver.EqualsCalls,
                        Is.Zero,
                        label
                            + $": delegate identity must not invoke receiver equality for receiver{receiver.Id}"
                    );
                    Assert.That(
                        receiver.HashCalls,
                        Is.Zero,
                        label
                            + $": delegate identity must not invoke receiver hashing for receiver{receiver.Id}"
                    );
                }
            }
            finally
            {
                token.Dispose();
                handler.active = false;
                _ = bus.Trim(force: true);
                MessagingDebug.enabled = previousDebug;
            }
        }

        private static void Remove(
            MessageRegistrationToken token,
            MessageRegistrationHandle[] handles,
            bool reverse
        )
        {
            for (int i = 0; i < handles.Length; i++)
            {
                token.RemoveRegistration(handles[reverse ? handles.Length - 1 - i : i]);
            }
        }

        private static MessageRegistrationHandle Register(
            MessageRegistrationToken token,
            MessageScenario scenario,
            bool action,
            bool global,
            Receiver receiver
        )
        {
            if (global)
            {
                return action
                    ? token.RegisterGlobalAcceptAll(
                        receiver.ReceiveUntargeted,
                        receiver.ReceiveTargeted,
                        receiver.ReceiveBroadcast
                    )
                    : token.RegisterGlobalAcceptAll(
                        receiver.ReceiveUntargetedFast,
                        receiver.ReceiveTargetedFast,
                        receiver.ReceiveBroadcastFast
                    );
            }
            return (scenario.Kind, scenario.UsePostProcessor, action) switch
            {
                (MessageKind.Untargeted, false, true) => token.RegisterUntargeted<ProbeMessage>(
                    receiver.Receive
                ),
                (MessageKind.Untargeted, false, false) => token.RegisterUntargeted<ProbeMessage>(
                    receiver.ReceiveFast
                ),
                (MessageKind.Untargeted, true, _) =>
                    token.RegisterUntargetedPostProcessor<ProbeMessage>(receiver.ReceiveFast),
                (MessageKind.Targeted, false, true) => token.RegisterTargeted<ProbeMessage>(
                    Context,
                    receiver.Receive
                ),
                (MessageKind.Targeted, false, false) => token.RegisterTargeted<ProbeMessage>(
                    Context,
                    receiver.ReceiveFast
                ),
                (MessageKind.Targeted, true, true) =>
                    token.RegisterTargetedPostProcessor<ProbeMessage>(Context, receiver.Receive),
                (MessageKind.Targeted, true, false) =>
                    token.RegisterTargetedPostProcessor<ProbeMessage>(
                        Context,
                        receiver.ReceiveFast
                    ),
                (MessageKind.Broadcast, false, true) => token.RegisterBroadcast<ProbeMessage>(
                    Context,
                    receiver.Receive
                ),
                (MessageKind.Broadcast, false, false) => token.RegisterBroadcast<ProbeMessage>(
                    Context,
                    receiver.ReceiveFast
                ),
                (MessageKind.Broadcast, true, true) =>
                    token.RegisterBroadcastPostProcessor<ProbeMessage>(Context, receiver.Receive),
                (MessageKind.Broadcast, true, false) =>
                    token.RegisterBroadcastPostProcessor<ProbeMessage>(
                        Context,
                        receiver.ReceiveFast
                    ),
                (MessageKind.TargetedWithoutTargeting, false, true) =>
                    token.RegisterTargetedWithoutTargeting<ProbeMessage>(receiver.ReceiveContext),
                (MessageKind.TargetedWithoutTargeting, false, false) =>
                    token.RegisterTargetedWithoutTargeting<ProbeMessage>(
                        receiver.ReceiveContextFast
                    ),
                (MessageKind.TargetedWithoutTargeting, true, true) =>
                    token.RegisterTargetedWithoutTargetingPostProcessor<ProbeMessage>(
                        receiver.ReceiveContext
                    ),
                (MessageKind.TargetedWithoutTargeting, true, false) =>
                    token.RegisterTargetedWithoutTargetingPostProcessor<ProbeMessage>(
                        receiver.ReceiveContextFast
                    ),
                (MessageKind.BroadcastWithoutSource, false, true) =>
                    token.RegisterBroadcastWithoutSource<ProbeMessage>(receiver.ReceiveContext),
                (MessageKind.BroadcastWithoutSource, false, false) =>
                    token.RegisterBroadcastWithoutSource<ProbeMessage>(receiver.ReceiveContextFast),
                (MessageKind.BroadcastWithoutSource, true, true) =>
                    token.RegisterBroadcastWithoutSourcePostProcessor<ProbeMessage>(
                        receiver.ReceiveContext
                    ),
                (MessageKind.BroadcastWithoutSource, true, false) =>
                    token.RegisterBroadcastWithoutSourcePostProcessor<ProbeMessage>(
                        receiver.ReceiveContextFast
                    ),
                _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
            };
        }

        private static void Emit(MessageBus bus, MessageScenario scenario)
        {
            ProbeMessage message = default;
            InstanceId context = Context;
            switch (scenario.Kind)
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
                    throw new ArgumentOutOfRangeException(nameof(scenario));
            }
        }

        private sealed class Receiver
        {
            public readonly int Id;
            private readonly List<int> _order;
            public int Calls;
            public int EqualsCalls;
            public int HashCalls;

            public Receiver(int id, List<int> order)
            {
                Id = id;
                _order = order;
            }

            public override bool Equals(object other)
            {
                EqualsCalls++;
                return ReferenceEquals(this, other);
            }

            public override int GetHashCode()
            {
                HashCalls++;
                return 0;
            }

            private void Record()
            {
                Calls++;
                _order.Add(Id);
            }

            public void Receive(ProbeMessage message) => Record();

            public void ReceiveFast(in ProbeMessage message) => Record();

            public void ReceiveContext(InstanceId context, ProbeMessage message) => Record();

            public void ReceiveContextFast(in InstanceId context, in ProbeMessage message) =>
                Record();

            public void ReceiveUntargeted(IUntargetedMessage message) => Record();

            public void ReceiveUntargetedFast(in IUntargetedMessage message) => Record();

            public void ReceiveTargeted(InstanceId context, ITargetedMessage message) => Record();

            public void ReceiveTargetedFast(in InstanceId context, in ITargetedMessage message) =>
                Record();

            public void ReceiveBroadcast(InstanceId context, IBroadcastMessage message) => Record();

            public void ReceiveBroadcastFast(in InstanceId context, in IBroadcastMessage message) =>
                Record();
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
