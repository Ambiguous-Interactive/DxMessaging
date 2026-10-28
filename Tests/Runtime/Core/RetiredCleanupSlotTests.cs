namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using DxMessaging.Core;
    using DxMessaging.Core.Helper;
    using DxMessaging.Core.Internal;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Runtime;
    using NUnit.Framework;

    [TestFixture]
#if !UNITY_2021_3_OR_NEWER
    [NonParallelizable]
#endif
    public sealed class RetiredCleanupSlotTests
    {
        private static readonly InstanceId Context = new(478);

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
                    foreach (bool sameReceiver in new[] { false, true })
                    {
                        foreach (string phase in new[] { "None", "Before", "After" })
                        {
                            yield return new TestCaseData(
                                scenario,
                                action,
                                sameReceiver,
                                phase
                            ).SetName(
                                $"RetiredCleanup_{scenario}_Action{action}_SameReceiver{sameReceiver}_{phase}"
                            );
                        }
                    }
                }
            }
        }

        [TestCaseSource(nameof(Cases))]
        public void ForeignCleanupCannotScheduleWorkOnResetSlot(
            MessageScenario scenario,
            bool action,
            bool sameReceiver,
            string phase
        )
        {
            DxMessagingStaticState.Reset();
            HookBus bus = new(MessageHandler.MessageBus) { AfterForward = phase == "After" };
            MessageHandler handler = new(new InstanceId(479), bus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            Receiver first = new();
            Receiver survivor = sameReceiver ? first : new Receiver();
            bool pendingWasNullAtReset = false;
            long versionAtReset = -1;
            try
            {
                _ = Register(token, scenario, action, first);
                token.Enable();
                Emit(MessageHandler.MessageBus, scenario);
                Assert.That(first.Calls, Is.EqualTo(1), "Baseline callback must run exactly once.");
                first.Calls = 0;
                TypedSlot<ProbeMessage> captured = Slot(handler, bus);
                long beforeVersion = captured.version;
                bus.OnDeregistration = () =>
                {
                    if (phase != "None")
                    {
                        DxMessagingStaticState.Reset();
                        versionAtReset = captured.version;
                        pendingWasNullAtReset = captured.pendingContextCleanup == null;
                    }
                    _ = Register(token, scenario, action, survivor);
                };
                token.Disable();
                bool enabledAfterFirst = token.Enabled;
                bool capturedStillCurrent = ReferenceEquals(captured, Slot(handler, bus));
                object pendingAfterFirst = captured.pendingContextCleanup;
                Emit(MessageHandler.MessageBus, scenario);
                int originalAfterFirst = first.Calls;
                int survivorAfterFirst = survivor.Calls;
                first.Calls = 0;
                survivor.Calls = 0;
                token.Disable();
                bool enabledAfterSecond = token.Enabled;
                Emit(MessageHandler.MessageBus, scenario);
                Assert.That(enabledAfterFirst, Is.True, "Accepted new cleanup remains owned.");
                Assert.That(
                    enabledAfterSecond,
                    Is.False,
                    "The next pass consumes the new cleanup."
                );
                Assert.That(originalAfterFirst, Is.EqualTo(sameReceiver ? 1 : 0));
                Assert.That(survivorAfterFirst, Is.EqualTo(1));
                Assert.That(first.Calls, Is.Zero);
                Assert.That(survivor.Calls, Is.Zero);
                if (phase != "None")
                {
                    Assert.That(versionAtReset, Is.Not.EqualTo(beforeVersion));
                    Assert.That(
                        pendingWasNullAtReset,
                        Is.True,
                        "Reset returns the old pending set."
                    );
                    Assert.That(
                        capturedStillCurrent,
                        Is.False,
                        "The new registration uses a new slot."
                    );
                    Assert.That(
                        pendingAfterFirst,
                        Is.Null,
                        "A retired slot has no owner to process newly scheduled context work."
                    );
                }
            }
            finally
            {
                bus.OnDeregistration = null;
                token.Dispose();
                DxMessagingStaticState.Reset();
            }
        }

        private static TypedSlot<ProbeMessage> Slot(MessageHandler handler, IMessageBus bus)
        {
            FieldInfo field = typeof(MessageHandler).GetField(
                "_handlersByTypeByMessageBus",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            Dictionary<int, MessageCache<object>> byBus =
                (Dictionary<int, MessageCache<object>>)field.GetValue(handler);
            bool found = byBus[bus.RegisteredGlobalSequentialIndex]
                .TryGetValue<ProbeMessage>(out object raw);
            Assert.That(found, Is.True);
            MessageHandler.TypedHandler<ProbeMessage> typed =
                (MessageHandler.TypedHandler<ProbeMessage>)raw;
            TypedSlot<ProbeMessage> result = null;
            int count = 0;
            foreach (TypedSlot<ProbeMessage> slot in typed._slots)
            {
                if (slot != null)
                {
                    result = slot;
                    ++count;
                }
            }
            Assert.That(count, Is.EqualTo(1), "Each case creates one typed registration shape.");
            return result;
        }

        private sealed class HookBus : DelegatingMessageBus
        {
            internal bool AfterForward;
            internal Action OnDeregistration;

            internal HookBus(IMessageBus inner)
                : base(inner) { }

            public override void Deregister<T>(in MessageBusRegistration registration)
            {
                Action callback = OnDeregistration;
                OnDeregistration = null;
                if (!AfterForward)
                {
                    callback?.Invoke();
                }
                base.Deregister<T>(in registration);
                if (AfterForward)
                {
                    callback?.Invoke();
                }
            }
        }

        private static MessageRegistrationHandle Register(
            MessageRegistrationToken token,
            MessageScenario scenario,
            bool action,
            Receiver receiver
        )
        {
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

        private static void Emit(IMessageBus bus, MessageScenario scenario)
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
            public int Calls;

            private void Record()
            {
                Calls++;
            }

            public void Receive(ProbeMessage message) => Record();

            public void ReceiveFast(in ProbeMessage message) => Record();

            public void ReceiveContext(InstanceId context, ProbeMessage message) => Record();

            public void ReceiveContextFast(in InstanceId context, in ProbeMessage message) =>
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
