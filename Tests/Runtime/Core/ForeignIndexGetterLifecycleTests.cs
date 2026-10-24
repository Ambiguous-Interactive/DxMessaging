namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Runtime;
    using NUnit.Framework;

    /// <summary>Required foreign cache-index callbacks precede bus resource acquisition.</summary>
    [TestFixture]
#if !UNITY_2021_3_OR_NEWER
    [NonParallelizable]
#endif
    public sealed class ForeignIndexGetterLifecycleTests
    {
        private static readonly InstanceId Context = new InstanceId(978);

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
                    foreach (TestCaseData item in BoundaryCases(scenario, action, 0))
                    {
                        yield return item;
                    }
                }
            }
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            {
                foreach (bool action in new[] { false, true })
                {
                    foreach (TestCaseData item in BoundaryCases(scenario, action, 2))
                    {
                        yield return item;
                    }
                }
                foreach (TestCaseData item in BoundaryCases(scenario, false, 1))
                {
                    yield return item;
                }
            }
        }

        private static IEnumerable<TestCaseData> BoundaryCases(
            MessageScenario scenario,
            bool action,
            int family
        )
        {
            foreach (string phase in new[] { "Immediate", "Replay", "Retarget" })
            foreach (bool diagnostics in new[] { false, true })
            foreach (string boundary in new[] { "Getter", "RegistrarBefore", "RegistrarAfter" })
            foreach (string effect in new[] { "None", "Throw", "Trim", "Reset" })
            {
                if (boundary == "RegistrarBefore" && effect != "Throw")
                {
                    continue;
                }
                if (boundary == "RegistrarAfter" && effect != "Trim" && effect != "Reset")
                {
                    continue;
                }
                yield return new TestCaseData(
                    scenario,
                    action,
                    family,
                    phase,
                    diagnostics,
                    boundary,
                    effect
                ).SetName(
                    $"ForeignIndex_{scenario.Kind}_Post{scenario.UsePostProcessor}_Action{action}_Family{family}_{phase}_Diagnostics{diagnostics}_{boundary}_{effect}"
                );
            }
        }

        [TestCaseSource(nameof(Cases))]
        public void RequiredIndexBoundaryPreservesRegistrationOwnership(
            MessageScenario scenario,
            bool action,
            int family,
            string phase,
            bool diagnostics,
            string boundary,
            string effect
        )
        {
            string label =
                $"[{scenario}, action={action}, family={family}, phase={phase}, diagnostics={diagnostics}, boundary={boundary}, effect={effect}]";
            bool savedDebug = MessagingDebug.enabled;
            MessagingDebug.enabled = false;
            MessageBus oldInner = new MessageBus { DiagnosticsMode = diagnostics };
            MessageBus newInner = new MessageBus { DiagnosticsMode = diagnostics };
            FaultBus oldBus = new FaultBus(oldInner, diagnostics);
            FaultBus newBus = new FaultBus(newInner, diagnostics);
            MessageHandler handler = new MessageHandler(new InstanceId(979), oldBus)
            {
                active = true,
            };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, oldBus);
            token.DiagnosticMode = diagnostics;
            using LeakWatcher oldLeaks = new LeakWatcher(oldInner);
            using LeakWatcher newLeaks = new LeakWatcher(newInner);
            Receiver receiver = new Receiver();
            FaultBus target = phase == "Retarget" ? newBus : oldBus;
            bool fails = effect == "Throw" && (boundary == "RegistrarBefore" || family != 1);
            try
            {
                if (phase == "Immediate")
                {
                    token.Enable();
                }
                else
                {
                    _ = Stage(token, scenario, action, family, receiver);
                    if (phase == "Retarget")
                    {
                        token.Enable();
                        Emit(oldInner, scenario);
                        Assert.That(
                            receiver.Calls,
                            Is.EqualTo(1),
                            label + ": initial old callback."
                        );
                        receiver.Calls = 0;
                    }
                }
                target.Boundary = boundary;
                target.Effect = effect;
                target.Armed = true;
                Exception observed = null;
                try
                {
                    if (phase == "Immediate")
                    {
                        _ = Stage(token, scenario, action, family, receiver);
                    }
                    else if (phase == "Replay")
                    {
                        token.Enable();
                    }
                    else
                    {
                        token.RetargetMessageBus(newBus, MessageBusRebindMode.RebindActive);
                    }
                }
                catch (Exception exception)
                {
                    observed = exception;
                }
                finally
                {
                    oldBus.Armed = false;
                    newBus.Armed = false;
                }

                Assert.That(
                    observed,
                    fails ? Is.SameAs(target.Marker) : Is.Null,
                    label + ": original fault identity."
                );
                Assert.That(
                    target.BoundaryCalls,
                    Is.EqualTo(boundary == "Getter" && family == 1 ? 0 : 1),
                    label + ": boundary multiplicity."
                );
                Assert.That(
                    target.CompletedRegistrations,
                    Is.EqualTo(fails ? 0 : 1),
                    label + ": acquisition follows required throwing boundaries."
                );
                Assert.That(
                    token._metadata.Count,
                    Is.EqualTo(fails && phase == "Immediate" ? 0 : 1),
                    label + ": staged ownership."
                );
                Assert.That(
                    token.Enabled,
                    Is.EqualTo(!fails || phase != "Replay"),
                    label + ": lifecycle enabled state."
                );
                MessageBus liveBus = phase == "Retarget" && !fails ? newInner : oldInner;
                bool cancelled = boundary == "RegistrarAfter" && effect == "Reset";
                int expectedLive = fails && phase != "Retarget" || cancelled ? 0 : 1;
                Assert.That(
                    MessageBusInspection.CountLiveRegistrations(liveBus),
                    Is.EqualTo(expectedLive),
                    label + ": live membership."
                );
                MessageBus otherBus = ReferenceEquals(liveBus, oldInner) ? newInner : oldInner;
                Assert.That(
                    MessageBusInspection.CountLiveRegistrations(otherBus),
                    Is.Zero,
                    label + ": no stranded peer membership."
                );
                Emit(oldInner, scenario);
                Emit(newInner, scenario);
                Assert.That(receiver.Calls, Is.EqualTo(expectedLive), label + ": actual delivery.");
                token.Disable();
                Assert.That(token.Enabled, Is.False, label + ": disabled.");
                Assert.That(
                    MessageBusInspection.CountLiveRegistrations(oldInner)
                        + MessageBusInspection.CountLiveRegistrations(newInner),
                    Is.Zero,
                    label + ": disabled bus ownership."
                );
                if (token._metadata.Count == 0)
                {
                    _ = Stage(token, scenario, action, family, receiver);
                }
                token.Enable();
                receiver.Calls = 0;
                Emit(oldInner, scenario);
                Emit(newInner, scenario);
                Assert.That(receiver.Calls, Is.EqualTo(1), label + ": later replay delivers once.");
                token.Dispose();
                Assert.That(token.Enabled, Is.False, label + ": disposed.");
                Assert.That(token._metadata.Count, Is.Zero, label + ": disposed metadata.");
                Assert.That(token._callCounts.Count, Is.Zero, label + ": disposed call counts.");
                Assert.That(token._emissionBuffer.Count, Is.Zero, label + ": disposed history.");
                Assert.That(
                    MessageBusInspection.CountLiveRegistrations(oldInner)
                        + MessageBusInspection.CountLiveRegistrations(newInner),
                    Is.Zero,
                    label + ": disposed bus ownership."
                );
            }
            finally
            {
                oldBus.Armed = false;
                newBus.Armed = false;
                try
                {
                    token.Dispose();
                    CleanupObservedHandle(oldInner, oldBus, family);
                    CleanupObservedHandle(newInner, newBus, family);
                    handler.active = false;
                    oldInner.Trim(force: true);
                    newInner.Trim(force: true);
                }
                finally
                {
                    MessagingDebug.enabled = savedDebug;
                }
            }
        }

        private static void CleanupObservedHandle(MessageBus bus, FaultBus facade, int family)
        {
            if (MessageBusInspection.CountLiveRegistrations(bus) == 0)
            {
                return;
            }
            if (family == 2)
            {
                bus.Deregister<IMessage>(in facade.LastRegistration);
            }
            else
            {
                bus.Deregister<ProbeMessage>(in facade.LastRegistration);
            }
        }

        private static MessageRegistrationHandle Stage(
            MessageRegistrationToken token,
            MessageScenario scenario,
            bool action,
            int family,
            Receiver receiver
        )
        {
            if (family != 1)
            {
                return Register(token, scenario, action, family == 2, receiver);
            }
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                    return token.RegisterUntargetedInterceptor<ProbeMessage>(
                        (ref ProbeMessage message) =>
                        {
                            receiver.ReceiveFast(in message);
                            return true;
                        }
                    );
                case MessageKind.Targeted:
                    return token.RegisterTargetedInterceptor<ProbeMessage>(
                        (ref InstanceId context, ref ProbeMessage message) =>
                        {
                            receiver.ReceiveFast(in message);
                            return true;
                        }
                    );
                case MessageKind.Broadcast:
                    return token.RegisterBroadcastInterceptor<ProbeMessage>(
                        (ref InstanceId context, ref ProbeMessage message) =>
                        {
                            receiver.ReceiveFast(in message);
                            return true;
                        }
                    );
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario));
            }
        }

        private sealed class FaultBus : DelegatingMessageBus
        {
            private readonly MessageBus _actual;
            private readonly bool _diagnostics;
            public readonly Exception Marker = new InvalidOperationException(
                "Required index boundary marker."
            );
            public string Boundary,
                Effect;
            public bool Armed;
            public int BoundaryCalls,
                CompletedRegistrations;
            public MessageBusRegistration LastRegistration;

            public FaultBus(MessageBus actual, bool diagnostics)
                : base(actual)
            {
                _actual = actual;
                _diagnostics = diagnostics;
            }

            public override int RegisteredGlobalSequentialIndex
            {
                get
                {
                    Fire("Getter");
                    return base.RegisteredGlobalSequentialIndex;
                }
            }

            private void Fire(string boundary)
            {
                if (!Armed || Boundary != boundary)
                {
                    return;
                }
                Armed = false;
                ++BoundaryCalls;
                if (Effect == "Throw")
                {
                    throw Marker;
                }
                if (Effect == "Trim")
                {
                    _actual.Trim(force: true);
                }
                if (Effect == "Reset")
                {
                    _actual.ResetState();
                    _actual.DiagnosticsMode = _diagnostics;
                }
            }

            private MessageBusRegistration After(MessageBusRegistration registration)
            {
                ++CompletedRegistrations;
                LastRegistration = registration;
                Fire("RegistrarAfter");
                return registration;
            }

            public override MessageBusRegistration RegisterUntargeted<T>(
                MessageHandler handler,
                int priority = 0
            )
            {
                Fire("RegistrarBefore");
                return After(base.RegisterUntargeted<T>(handler, priority));
            }

            public override MessageBusRegistration RegisterUntargetedPostProcessor<T>(
                MessageHandler handler,
                int priority = 0
            )
            {
                Fire("RegistrarBefore");
                return After(base.RegisterUntargetedPostProcessor<T>(handler, priority));
            }

            public override MessageBusRegistration RegisterTargeted<T>(
                InstanceId context,
                MessageHandler handler,
                int priority = 0
            )
            {
                Fire("RegistrarBefore");
                return After(base.RegisterTargeted<T>(context, handler, priority));
            }

            public override MessageBusRegistration RegisterTargetedPostProcessor<T>(
                InstanceId context,
                MessageHandler handler,
                int priority = 0
            )
            {
                Fire("RegistrarBefore");
                return After(base.RegisterTargetedPostProcessor<T>(context, handler, priority));
            }

            public override MessageBusRegistration RegisterTargetedWithoutTargeting<T>(
                MessageHandler handler,
                int priority = 0
            )
            {
                Fire("RegistrarBefore");
                return After(base.RegisterTargetedWithoutTargeting<T>(handler, priority));
            }

            public override MessageBusRegistration RegisterTargetedWithoutTargetingPostProcessor<T>(
                MessageHandler handler,
                int priority = 0
            )
            {
                Fire("RegistrarBefore");
                return After(
                    base.RegisterTargetedWithoutTargetingPostProcessor<T>(handler, priority)
                );
            }

            public override MessageBusRegistration RegisterSourcedBroadcast<T>(
                InstanceId context,
                MessageHandler handler,
                int priority = 0
            )
            {
                Fire("RegistrarBefore");
                return After(base.RegisterSourcedBroadcast<T>(context, handler, priority));
            }

            public override MessageBusRegistration RegisterBroadcastPostProcessor<T>(
                InstanceId context,
                MessageHandler handler,
                int priority = 0
            )
            {
                Fire("RegistrarBefore");
                return After(base.RegisterBroadcastPostProcessor<T>(context, handler, priority));
            }

            public override MessageBusRegistration RegisterSourcedBroadcastWithoutSource<T>(
                MessageHandler handler,
                int priority = 0
            )
            {
                Fire("RegistrarBefore");
                return After(base.RegisterSourcedBroadcastWithoutSource<T>(handler, priority));
            }

            public override MessageBusRegistration RegisterBroadcastWithoutSourcePostProcessor<T>(
                MessageHandler handler,
                int priority = 0
            )
            {
                Fire("RegistrarBefore");
                return After(
                    base.RegisterBroadcastWithoutSourcePostProcessor<T>(handler, priority)
                );
            }

            public override MessageBusRegistration RegisterGlobalAcceptAll(MessageHandler handler)
            {
                Fire("RegistrarBefore");
                return After(base.RegisterGlobalAcceptAll(handler));
            }

            public override MessageBusRegistration RegisterUntargetedInterceptor<T>(
                IMessageBus.UntargetedInterceptor<T> interceptor,
                int priority = 0
            )
            {
                Fire("RegistrarBefore");
                return After(base.RegisterUntargetedInterceptor(interceptor, priority));
            }

            public override MessageBusRegistration RegisterTargetedInterceptor<T>(
                IMessageBus.TargetedInterceptor<T> interceptor,
                int priority = 0
            )
            {
                Fire("RegistrarBefore");
                return After(base.RegisterTargetedInterceptor(interceptor, priority));
            }

            public override MessageBusRegistration RegisterBroadcastInterceptor<T>(
                IMessageBus.BroadcastInterceptor<T> interceptor,
                int priority = 0
            )
            {
                Fire("RegistrarBefore");
                return After(base.RegisterBroadcastInterceptor(interceptor, priority));
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
