namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Runtime;
    using NUnit.Framework;

    /// <summary>Foreign emission getters must not interrupt registration bookkeeping.</summary>
    /// <remarks>
    /// 2026-10-21: A touch stamp called a foreign getter before live count publication.
    /// Forced trim then reclaimed in-progress registrations. Lifecycle work must leave
    /// delivery counters to the bus, without invoking unrelated getter side effects.
    /// </remarks>
    [TestFixture]
#if !UNITY_2021_3_OR_NEWER
    [NonParallelizable]
#endif
    public sealed class ForeignTouchGetterLifecycleTests
    {
        private static readonly InstanceId Context = new InstanceId(990);

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
                    foreach (TestCaseData testCase in LifecycleCases(scenario, action, false))
                    {
                        yield return testCase;
                    }
                }
            }
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            {
                foreach (bool action in new[] { false, true })
                {
                    foreach (TestCaseData testCase in LifecycleCases(scenario, action, true))
                    {
                        yield return testCase;
                    }
                }
            }
        }

        private static IEnumerable<TestCaseData> LifecycleCases(
            MessageScenario scenario,
            bool action,
            bool global
        )
        {
            foreach (string phase in new[] { "Register", "Replay", "Disable" })
            {
                foreach (string effect in new[] { "None", "Trim", "Reset", "Throw" })
                {
                    foreach (bool diagnostics in new[] { false, true })
                    {
                        yield return new TestCaseData(
                            scenario,
                            action,
                            global,
                            phase,
                            effect,
                            diagnostics
                        ).SetName(
                            $"ForeignTouchGetter_{scenario.Kind}_Post{scenario.UsePostProcessor}_Action{action}_Global{global}_{phase}_{effect}_Diagnostics{diagnostics}"
                        );
                    }
                }
            }
        }

        [TestCaseSource(nameof(Cases))]
        public void LifecyclePreservesRegistrationWhenForeignGetterHasSideEffects(
            MessageScenario scenario,
            bool action,
            bool global,
            string phase,
            string effect,
            bool diagnostics
        )
        {
            bool previousDebug = MessagingDebug.enabled;
            MessagingDebug.enabled = false;
            MessageBus actual = new MessageBus { DiagnosticsMode = diagnostics };
            GetterBus facade = new GetterBus(actual);
            MessageHandler handler = new MessageHandler(new InstanceId(991), facade)
            {
                active = true,
            };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, facade);
            token.DiagnosticMode = diagnostics;
            using LeakWatcher leaks = new LeakWatcher(actual);
            Receiver receiver = new Receiver();
            MessageRegistrationHandle handle = default;
            try
            {
                if (phase == "Register")
                {
                    token.Enable();
                }
                else
                {
                    handle = Register(token, scenario, action, global, receiver);
                    token.Enable();
                    Emit(actual, scenario);
                    Assert.That(receiver.Calls, Is.EqualTo(1), "warm callback");
                    receiver.Calls = 0;
                    if (phase == "Replay")
                    {
                        token.Disable();
                        Assert.That(token.Enabled, Is.False, "disabled before replay");
                        Assert.That(
                            MessageBusInspection.CountLiveRegistrations(actual),
                            Is.Zero,
                            "warm cleanup"
                        );
                    }
                }
                facade.Effect = effect;
                Exception failure = null;
                try
                {
                    if (phase == "Register")
                    {
                        handle = Register(token, scenario, action, global, receiver);
                    }
                    else if (phase == "Replay")
                    {
                        token.Enable();
                    }
                    else
                    {
                        token.Disable();
                    }
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    facade.Effect = "None";
                }
                Assert.That(failure, Is.Null, "foreign getter must not interrupt lifecycle work");
                Assert.That(token.Enabled, Is.EqualTo(phase != "Disable"), "lifecycle state");
                Assert.That(token._metadata.Count, Is.EqualTo(1), "staged ownership survives");
                Assert.That(
                    MessageBusInspection.CountLiveRegistrations(actual),
                    Is.EqualTo(phase == "Disable" ? 0 : 1),
                    "actual bus membership"
                );
                Emit(actual, scenario);
                Assert.That(
                    receiver.Calls,
                    Is.EqualTo(phase == "Disable" ? 0 : 1),
                    "live callback after lifecycle operation"
                );
                if (phase == "Disable")
                {
                    token.Enable();
                    Emit(actual, scenario);
                    Assert.That(receiver.Calls, Is.EqualTo(1), "disabled staging replays");
                }
                actual.Trim(force: true);
                Emit(actual, scenario);
                Assert.That(receiver.Calls, Is.EqualTo(2), "forced trim preserves live callback");
                token.RemoveRegistration(handle);
                Assert.That(token._metadata.Count, Is.Zero, "handle consumes staged ownership");
                Assert.That(
                    MessageBusInspection.CountLiveRegistrations(actual),
                    Is.Zero,
                    "handle consumes actual membership"
                );
                Emit(actual, scenario);
                Assert.That(receiver.Calls, Is.EqualTo(2), "removed callback stays silent");
                token.Dispose();
                Assert.That(token._metadata.Count, Is.Zero, "disposed metadata");
                Assert.That(token._callCounts.Count, Is.Zero, "disposed call counts");
                Assert.That(token._emissionBuffer.Count, Is.Zero, "disposed emission history");
            }
            finally
            {
                facade.Effect = "None";
                try
                {
                    token.Dispose();
                }
                finally
                {
                    handler.active = false;
                    actual.Trim(force: true);
                    MessagingDebug.enabled = previousDebug;
                }
            }
        }

        private sealed class GetterBus : DelegatingMessageBus
        {
            private readonly MessageBus _actual;
            public string Effect = "None";

            public GetterBus(MessageBus actual)
                : base(actual)
            {
                _actual = actual;
            }

            public override long EmissionId
            {
                get
                {
                    string effect = Effect;
                    Effect = "None";
                    if (effect == "Trim")
                    {
                        _actual.Trim(force: true);
                    }
                    else if (effect == "Reset")
                    {
                        DxMessagingStaticState.Reset();
                    }
                    else if (effect == "Throw")
                    {
                        throw new InvalidOperationException("foreign emission getter");
                    }
                    return _actual.EmissionId;
                }
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
