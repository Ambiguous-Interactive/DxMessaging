namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;

    /// <summary>Registration handles belong to the bus that produced them.</summary>
    /// <remarks>
    /// 2026-10-16: a foreign handle could remove an origin handler route or a receiver global
    /// registration because fresh buses shared reset generations. Copies retain the same owner.
    /// </remarks>
    [TestFixture]
#if !UNITY_2021_3_OR_NEWER
    [NonParallelizable]
#endif
    public sealed class ForeignBusRegistrationTests
    {
        private static readonly InstanceId Context = new InstanceId(702);

        [Test]
        public void ForeignHandlerHandlePreservesBothBuses(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.WithAndWithoutPostProcessorIncludingWithoutContext)
            )]
                MessageScenario scenario,
            [Values(false, true)] bool receiverHasRegistration,
            [Values(false, true)] bool diagnostics
        )
        {
            using DebugCapture capture = new DebugCapture(diagnostics);
            MessageBus origin = new MessageBus();
            MessageBus receiver = new MessageBus();
            using LeakWatcher originLeaks = new LeakWatcher(origin);
            using LeakWatcher receiverLeaks = new LeakWatcher(receiver);
            MessageHandler handler = new MessageHandler(new InstanceId(701), origin);
            MessageBusRegistration original = RegisterHandler(origin, handler, scenario);
            MessageBusRegistration other = receiverHasRegistration
                ? RegisterHandler(receiver, handler, scenario)
                : MessageBusRegistration.None;
            try
            {
                Assert.That(CountHandlers(origin, scenario), Is.EqualTo(1), scenario.ToString());
                Assert.That(
                    CountHandlers(receiver, scenario),
                    Is.EqualTo(receiverHasRegistration ? 1 : 0),
                    scenario.ToString()
                );
                capture.Messages.Clear();
                MessageBusRegistration copy = original;
                receiver.Deregister<ProbeMessage>(in copy);
                int originAfterForeign = CountHandlers(origin, scenario);
                int receiverAfterForeign = CountHandlers(receiver, scenario);
                int foreignLogs = capture.Messages.Count;
                origin.Deregister<ProbeMessage>(in original);
                original = MessageBusRegistration.None;
                AssertAll(() =>
                {
                    string label =
                        $"[{scenario}, receiver={receiverHasRegistration}, diagnostics={diagnostics}]";
                    Assert.That(
                        originAfterForeign,
                        Is.EqualTo(1),
                        label + " origin route changed."
                    );
                    Assert.That(
                        receiverAfterForeign,
                        Is.EqualTo(receiverHasRegistration ? 1 : 0),
                        label + " receiver route changed."
                    );
                    Assert.That(foreignLogs, Is.Zero, label + " foreign deregistration logged.");
                    Assert.That(
                        CountHandlers(origin, scenario),
                        Is.Zero,
                        label + " owner cleanup failed."
                    );
                    Assert.That(
                        CountHandlers(receiver, scenario),
                        Is.EqualTo(receiverHasRegistration ? 1 : 0),
                        label + " owner cleanup changed receiver."
                    );
                });
            }
            finally
            {
                origin.Deregister<ProbeMessage>(in original);
                receiver.Deregister<ProbeMessage>(in other);
                origin.Trim(force: true);
                receiver.Trim(force: true);
            }
        }

        [Test]
        public void ForeignGlobalHandlePreservesBothBuses(
            [Values(false, true)] bool receiverHasRegistration,
            [Values(false, true)] bool diagnostics
        )
        {
            using DebugCapture capture = new DebugCapture(diagnostics);
            MessageBus origin = new MessageBus();
            MessageBus receiver = new MessageBus();
            using LeakWatcher originLeaks = new LeakWatcher(origin);
            using LeakWatcher receiverLeaks = new LeakWatcher(receiver);
            MessageHandler handler = new MessageHandler(new InstanceId(701), origin);
            MessageBusRegistration original = origin.RegisterGlobalAcceptAll(handler);
            MessageBusRegistration other = receiverHasRegistration
                ? receiver.RegisterGlobalAcceptAll(handler)
                : MessageBusRegistration.None;
            try
            {
                capture.Messages.Clear();
                MessageBusRegistration copy = original;
                receiver.Deregister<ProbeMessage>(in copy);
                int originAfterForeign = origin.RegisteredGlobalAcceptAll;
                int receiverAfterForeign = receiver.RegisteredGlobalAcceptAll;
                int foreignLogs = capture.Messages.Count;
                origin.Deregister<ProbeMessage>(in original);
                original = MessageBusRegistration.None;
                AssertAll(() =>
                {
                    string label =
                        $"[receiver={receiverHasRegistration}, diagnostics={diagnostics}]";
                    Assert.That(
                        originAfterForeign,
                        Is.EqualTo(1),
                        label + " origin global route changed."
                    );
                    Assert.That(
                        receiverAfterForeign,
                        Is.EqualTo(receiverHasRegistration ? 1 : 0),
                        label + " receiver global route changed."
                    );
                    Assert.That(
                        foreignLogs,
                        Is.Zero,
                        label + " foreign global deregistration logged."
                    );
                    Assert.That(
                        origin.RegisteredGlobalAcceptAll,
                        Is.Zero,
                        label + " owner cleanup failed."
                    );
                    Assert.That(
                        receiver.RegisteredGlobalAcceptAll,
                        Is.EqualTo(receiverHasRegistration ? 1 : 0),
                        label + " owner cleanup changed receiver."
                    );
                });
            }
            finally
            {
                origin.Deregister<ProbeMessage>(in original);
                receiver.Deregister<ProbeMessage>(in other);
                origin.Trim(force: true);
                receiver.Trim(force: true);
            }
        }

        [Test]
        public void ForeignInterceptorHandlePreservesCallbacksAndOwnerCleanup(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(false, true)] bool receiverHasRegistration,
            [Values(false, true)] bool diagnostics
        )
        {
            using DebugCapture capture = new DebugCapture(diagnostics);
            MessageBus origin = new MessageBus();
            MessageBus receiver = new MessageBus();
            using LeakWatcher originLeaks = new LeakWatcher(origin);
            using LeakWatcher receiverLeaks = new LeakWatcher(receiver);
            int calls = 0;
            bool Intercept(ref ProbeMessage message)
            {
                calls++;
                return false;
            }
            bool InterceptContext(ref InstanceId context, ref ProbeMessage message)
            {
                calls++;
                return false;
            }
            IMessageBus.UntargetedInterceptor<ProbeMessage> untargeted = Intercept;
            IMessageBus.TargetedInterceptor<ProbeMessage> targeted = InterceptContext;
            IMessageBus.BroadcastInterceptor<ProbeMessage> broadcast = InterceptContext;
            MessageBusRegistration Register(MessageBus bus) =>
                scenario.Kind switch
                {
                    MessageKind.Untargeted => bus.RegisterUntargetedInterceptor(untargeted),
                    MessageKind.Targeted => bus.RegisterTargetedInterceptor(targeted),
                    MessageKind.Broadcast => bus.RegisterBroadcastInterceptor(broadcast),
                    _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
                };
            MessageBusRegistration original = Register(origin);
            MessageBusRegistration other = receiverHasRegistration
                ? Register(receiver)
                : MessageBusRegistration.None;
            try
            {
                capture.Messages.Clear();
                MessageBusRegistration copy = original;
                receiver.Deregister<ProbeMessage>(in copy);
                int foreignLogs = capture.Messages.Count;
                Emit(origin, scenario);
                if (receiverHasRegistration)
                {
                    Emit(receiver, scenario);
                }
                int callsAfterForeign = calls;
                int originAfterForeign = origin.RegisteredInterceptors;
                int receiverAfterForeign = receiver.RegisteredInterceptors;
                origin.Deregister<ProbeMessage>(in original);
                original = MessageBusRegistration.None;
                AssertAll(() =>
                {
                    string label =
                        $"[{scenario}, receiver={receiverHasRegistration}, diagnostics={diagnostics}]";
                    Assert.That(
                        originAfterForeign,
                        Is.EqualTo(1),
                        label + " origin interceptor changed."
                    );
                    Assert.That(
                        receiverAfterForeign,
                        Is.EqualTo(receiverHasRegistration ? 1 : 0),
                        label + " receiver interceptor changed."
                    );
                    Assert.That(
                        callsAfterForeign,
                        Is.EqualTo(receiverHasRegistration ? 2 : 1),
                        label + " callbacks changed."
                    );
                    Assert.That(
                        foreignLogs,
                        Is.Zero,
                        label + " foreign interceptor deregistration logged."
                    );
                    Assert.That(
                        origin.RegisteredInterceptors,
                        Is.Zero,
                        label + " owner cleanup failed."
                    );
                });
            }
            finally
            {
                origin.Deregister<ProbeMessage>(in original);
                receiver.Deregister<ProbeMessage>(in other);
                origin.Trim(force: true);
                receiver.Trim(force: true);
            }
        }

        [Test]
        public void ForeignHandleCannotConsumeTheOwnersLaterValidDeregistration(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(false, true)] bool receiverHasRegistration,
            [Values(false, true)] bool diagnostics
        )
        {
            using DebugCapture capture = new DebugCapture(diagnostics);
            MessageBus origin = new MessageBus();
            MessageBus receiver = new MessageBus();
            using LeakWatcher originLeaks = new LeakWatcher(origin);
            using LeakWatcher receiverLeaks = new LeakWatcher(receiver);
            MessageHandler handler = new MessageHandler(new InstanceId(701), origin)
            {
                active = true,
            };
            using MessageRegistrationToken token = MessageRegistrationToken.Create(handler, origin);
            using MessageRegistrationToken otherToken = MessageRegistrationToken.Create(
                handler,
                receiver
            );
            int originCalls = 0;
            int receiverCalls = 0;
            void ReceiveOrigin(in ProbeMessage message) => originCalls++;
            void ReceiveReceiver(in ProbeMessage message) => receiverCalls++;
            RegisterCallback(token, scenario, ReceiveOrigin);
            token.Enable();
            if (receiverHasRegistration)
            {
                RegisterCallback(otherToken, scenario, ReceiveReceiver);
                otherToken.Enable();
            }
            MessageBusRegistration original = RegisterHandler(origin, handler, scenario);
            try
            {
                Emit(origin, scenario);
                Assert.That(originCalls, Is.EqualTo(1), "Initial origin callback must be live.");
                originCalls = 0;
                MessageBusRegistration copy = original;
                receiver.Deregister<ProbeMessage>(in copy);
                origin.Deregister<ProbeMessage>(in original);
                original = MessageBusRegistration.None;
                Emit(origin, scenario);
                if (receiverHasRegistration)
                {
                    Emit(receiver, scenario);
                }
                AssertAll(() =>
                {
                    string label =
                        $"[{scenario}, receiver={receiverHasRegistration}, diagnostics={diagnostics}]";
                    Assert.That(
                        originCalls,
                        Is.EqualTo(1),
                        label
                            + " foreign handle consumed the owner registration and lost its peer callback."
                    );
                    Assert.That(
                        receiverCalls,
                        Is.EqualTo(receiverHasRegistration ? 1 : 0),
                        label + " receiver callback changed."
                    );
                });
            }
            finally
            {
                origin.Deregister<ProbeMessage>(in original);
                token.UnregisterAll();
                otherToken.UnregisterAll();
                origin.Trim(force: true);
                receiver.Trim(force: true);
                handler.active = false;
            }
        }

        [Test]
        public void GlobalHandlesFromDifferentBusesHaveDistinctValueIdentity()
        {
            using DebugCapture capture = new DebugCapture(false);
            MessageBus firstBus = new MessageBus();
            MessageBus secondBus = new MessageBus();
            using LeakWatcher firstLeaks = new LeakWatcher(firstBus);
            using LeakWatcher secondLeaks = new LeakWatcher(secondBus);
            MessageHandler handler = new MessageHandler(new InstanceId(701), firstBus);
            MessageBusRegistration first = firstBus.RegisterGlobalAcceptAll(handler);
            MessageBusRegistration second = secondBus.RegisterGlobalAcceptAll(handler);
            try
            {
                MessageBusRegistration copy = first;
                HashSet<MessageBusRegistration> handles = new HashSet<MessageBusRegistration>
                {
                    first,
                    copy,
                    second,
                };
                AssertAll(() =>
                {
                    Assert.That(
                        first,
                        Is.EqualTo(copy),
                        "A copy must retain registration identity."
                    );
                    Assert.That(
                        first.GetHashCode(),
                        Is.EqualTo(copy.GetHashCode()),
                        "Equal copies must have equal hashes."
                    );
                    Assert.That(
                        first,
                        Is.Not.EqualTo(second),
                        "Global registrations from different buses must have distinct identities."
                    );
                    Assert.That(
                        handles.Count,
                        Is.EqualTo(2),
                        "A handle set must retain both bus registrations and deduplicate only the copy."
                    );
                });
            }
            finally
            {
                firstBus.Deregister<ProbeMessage>(in first);
                secondBus.Deregister<ProbeMessage>(in second);
                firstBus.Trim(force: true);
                secondBus.Trim(force: true);
            }
        }

        [Test]
        public void EmptyAndExternalHandlesPreserveLiveRegistrations(
            [Values(false, true)] bool diagnostics
        )
        {
            using DebugCapture capture = new DebugCapture(diagnostics);
            MessageBus bus = new MessageBus();
            using LeakWatcher leaks = new LeakWatcher(bus);
            MessageHandler handler = new MessageHandler(new InstanceId(701), bus);
            MessageBusRegistration live = bus.RegisterUntargeted<ProbeMessage>(handler);
            try
            {
                MessageBusRegistration none = MessageBusRegistration.None;
                object state = new object();
                MessageBusRegistration external = new MessageBusRegistration(703, state);
                capture.Messages.Clear();
                bus.Deregister<ProbeMessage>(in none);
                bus.Deregister<ProbeMessage>(in external);
                AssertAll(() =>
                {
                    string label = $"[diagnostics={diagnostics}]";
                    Assert.That(
                        bus.RegisteredUntargeted,
                        Is.EqualTo(1),
                        label + " empty/external handle changed live route."
                    );
                    Assert.That(
                        capture.Messages.Count,
                        Is.Zero,
                        label + " empty/external handle logged."
                    );
                    Assert.That(
                        external.ExternalId,
                        Is.EqualTo(703),
                        label + " external id round trip changed."
                    );
                    Assert.That(
                        external.ExternalState,
                        Is.SameAs(state),
                        label + " external state round trip changed."
                    );
                });
            }
            finally
            {
                bus.Deregister<ProbeMessage>(in live);
                bus.Trim(force: true);
            }
        }

        private static MessageBusRegistration RegisterHandler(
            MessageBus bus,
            MessageHandler handler,
            MessageScenario scenario
        ) =>
            (scenario.Kind, scenario.UsePostProcessor) switch
            {
                (MessageKind.Untargeted, false) => bus.RegisterUntargeted<ProbeMessage>(handler),
                (MessageKind.Untargeted, true) => bus.RegisterUntargetedPostProcessor<ProbeMessage>(
                    handler
                ),
                (MessageKind.Targeted, false) => bus.RegisterTargeted<ProbeMessage>(
                    Context,
                    handler
                ),
                (MessageKind.Targeted, true) => bus.RegisterTargetedPostProcessor<ProbeMessage>(
                    Context,
                    handler
                ),
                (MessageKind.Broadcast, false) => bus.RegisterSourcedBroadcast<ProbeMessage>(
                    Context,
                    handler
                ),
                (MessageKind.Broadcast, true) => bus.RegisterBroadcastPostProcessor<ProbeMessage>(
                    Context,
                    handler
                ),
                (MessageKind.TargetedWithoutTargeting, false) =>
                    bus.RegisterTargetedWithoutTargeting<ProbeMessage>(handler),
                (MessageKind.TargetedWithoutTargeting, true) =>
                    bus.RegisterTargetedWithoutTargetingPostProcessor<ProbeMessage>(handler),
                (MessageKind.BroadcastWithoutSource, false) =>
                    bus.RegisterSourcedBroadcastWithoutSource<ProbeMessage>(handler),
                (MessageKind.BroadcastWithoutSource, true) =>
                    bus.RegisterBroadcastWithoutSourcePostProcessor<ProbeMessage>(handler),
                _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
            };

        private static int CountHandlers(MessageBus bus, MessageScenario scenario) =>
            scenario.UsePostProcessor
                ? bus.RegisteredPostProcessors
                : scenario.Kind switch
                {
                    MessageKind.Untargeted => bus.RegisteredUntargeted,
                    MessageKind.Targeted or MessageKind.TargetedWithoutTargeting =>
                        bus.RegisteredTargeted,
                    MessageKind.Broadcast or MessageKind.BroadcastWithoutSource =>
                        bus.RegisteredBroadcast,
                    _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
                };

        private static void RegisterCallback(
            MessageRegistrationToken token,
            MessageScenario scenario,
            MessageHandler.FastHandler<ProbeMessage> callback
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                    ScenarioHarness.RegisterUntargeted(scenario, token, callback);
                    break;
                case MessageKind.Targeted:
                    ScenarioHarness.RegisterTargeted(scenario, token, Context, callback);
                    break;
                case MessageKind.Broadcast:
                    ScenarioHarness.RegisterBroadcast(scenario, token, Context, callback);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario));
            }
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
                    bus.TargetedBroadcast(ref context, ref message);
                    break;
                case MessageKind.Broadcast:
                    bus.SourcedBroadcast(ref context, ref message);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario));
            }
        }

        /*
            Unity's bundled NUnit reports the first failure; .NET NUnit aggregates failures.
            Both branches execute the same assertion predicates in the same order.
        */
        private static void AssertAll(Action assertions)
        {
#if UNITY_2021_3_OR_NEWER
            assertions();
#else
            Assert.Multiple(assertions);
#endif
        }

        private readonly struct ProbeMessage
            : IUntargetedMessage,
                ITargetedMessage,
                IBroadcastMessage
        {
            public Type MessageType => typeof(ProbeMessage);
        }

        private sealed class DebugCapture : IDisposable
        {
            private readonly bool _enabled = MessagingDebug.enabled;
            private readonly Action<LogLevel, string> _logger = MessagingDebug.LogFunction;
            internal readonly List<string> Messages = new List<string>();

            internal DebugCapture(bool enabled)
            {
                MessagingDebug.enabled = enabled;
                MessagingDebug.LogFunction = (_, text) => Messages.Add(text);
            }

            public void Dispose()
            {
                MessagingDebug.LogFunction = _logger;
                MessagingDebug.enabled = _enabled;
            }
        }
    }
}
