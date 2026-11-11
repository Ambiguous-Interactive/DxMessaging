namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Testing;
    using NUnit.Framework;
    using BusType = DxMessaging.Core.MessageBus.MessageBus;

    /// <summary>Checks ordered value and reference captures plus disposal for each message route.</summary>
    [Category("Contract")]
    public sealed class MessageRecorderTests
    {
        /// <summary>Checks ordered value and reference captures plus disposal for each message route.</summary>
        [Test]
        public void RecorderCapturesMessagesAndReleasesItsSubscription(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(false, true)] bool referencePayload
        )
        {
            if (referencePayload)
            {
                Exercise(scenario, new ReferenceMessage(10), new ReferenceMessage(20));
            }
            else
            {
                Exercise(scenario, new ValueMessage(10), new ValueMessage(20));
            }
        }

        /// <summary>Checks that clearing updates the existing read-only view without removing the subscription.</summary>
        [Test]
        public void ClearPreservesTheReadOnlyViewAndSubscription(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            BusType bus = new();
            using LeakWatcher leaks = new(bus: bus, label: scenario.Kind.ToString());
            using MessageRecorder<ValueMessage> recorder = Create<ValueMessage>(scenario, bus);
            IReadOnlyList<ValueMessage> view = recorder.Received;
            ValueMessage message = new(10);
            Emit(scenario, bus, ref message);
            Assert.That(view.Count, Is.EqualTo(1), "The view must show live captures.");
            Assert.Throws<NotSupportedException>(
                () => ((IList<ValueMessage>)view).Add(message),
                "Callers must not mutate the recorder's collection through its view."
            );
            recorder.Clear();
            Assert.That(view.Count, Is.Zero, "Clear must update the same view.");
            message = new ValueMessage(20);
            Emit(scenario, bus, ref message);
            Assert.That(view, Is.EqualTo(new[] { message }), "Clear must leave recording active.");
        }

        /// <summary>Checks that disposing one recorder leaves another recorder active on the same bus.</summary>
        [Test]
        public void RecordersOwnIndependentSubscriptions(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            BusType bus = new();
            using LeakWatcher leaks = new(bus: bus, label: scenario.Kind.ToString());
            using MessageRecorder<ValueMessage> first = Create<ValueMessage>(scenario, bus);
            using MessageRecorder<ValueMessage> second = Create<ValueMessage>(scenario, bus);
            ValueMessage message = new(1);
            Emit(scenario, bus, ref message);
            Assert.That(
                first.Received.Count,
                Is.EqualTo(1),
                "The first recorder must receive its own callback."
            );
            Assert.That(
                second.Received.Count,
                Is.EqualTo(1),
                "The second recorder must not share the first token."
            );
            first.Dispose();
            Emit(scenario, bus, ref message);
            Assert.That(
                first.Received,
                Is.Empty,
                "Disposed recorders must release captures and stop recording."
            );
            Assert.That(
                second.Received.Count,
                Is.EqualTo(2),
                "Disposing the first recorder must preserve the second."
            );
        }

        /// <summary>Checks bus isolation and contextual filtering without observing unrelated messages.</summary>
        [Test]
        public void RecorderUsesOnlyItsExplicitBusAndContext(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            BusType selected = new();
            BusType other = new();
            using LeakWatcher selectedLeaks = new(bus: selected, label: "selected");
            using LeakWatcher otherLeaks = new(bus: other, label: "other");
            using MessageRecorder<ValueMessage> recorder = Create<ValueMessage>(scenario, selected);
            ValueMessage message = new(1);
            Emit(scenario, other, ref message);
            Assert.That(
                recorder.Received,
                Is.Empty,
                "Messages on another bus must not be captured."
            );
            if (scenario.Kind != MessageKind.Untargeted)
            {
                Emit(scenario, selected, ref message, new InstanceId(124));
                Assert.That(
                    recorder.Received,
                    Is.Empty,
                    "Messages for another context must not be captured."
                );
            }
            Emit(scenario, selected, ref message);
            Assert.That(
                recorder.Received.Count,
                Is.EqualTo(1),
                "The matching bus/context must be captured."
            );
        }

        /// <summary>Checks the required explicit bus argument for each recorder factory.</summary>
        [Test]
        public void NullBusIsRejected(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            Assert.Throws<ArgumentNullException>(
                () => Create<ValueMessage>(scenario, null),
                "A test recorder must require an explicit non-null bus."
            );
        }

        /// <summary>Checks that failed cleanup preserves captures and can be completed by the same recorder.</summary>
        [Test]
        public void DisposalFailureRetainsCleanupOnTheRecorderForRetry(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            BusType inner = new();
            FailingCleanupBus bus = new(inner);
            using LeakWatcher leaks = new(bus: inner, label: scenario.Kind.ToString());
            MessageRecorder<ValueMessage> recorder = Create<ValueMessage>(scenario, bus);
            try
            {
                ValueMessage message = new(7);
                Emit(scenario, bus, ref message);
                InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
                    recorder.Dispose,
                    "The failed deregistration must reach the caller."
                );
                Assert.That(
                    failure.Message,
                    Does.Contain("Intentional recorder cleanup failure"),
                    "The bus failure must propagate unchanged."
                );
                Assert.That(
                    recorder.Received.Count,
                    Is.EqualTo(1),
                    "Failed cleanup must not discard the recorder's state."
                );
                bus.AllowCleanup = true;
                recorder.Dispose();
                Assert.That(
                    recorder.Received,
                    Is.Empty,
                    "Successful retry must release captured payloads."
                );
                Emit(scenario, bus, ref message);
                Assert.That(
                    recorder.Received,
                    Is.Empty,
                    "The owning recorder must finish removing its callback."
                );
            }
            finally
            {
                bus.AllowCleanup = true;
                recorder.Dispose();
            }
        }

        /// <summary>Checks assembly separation and the emitted support assembly references.</summary>
        [Test]
        public void TestSupportIsSeparateFromTheRuntimeAndHasNoFrameworkApiDependency()
        {
            System.Reflection.Assembly support = typeof(MessageRecorder).Assembly;
            Assert.That(
                support,
                Is.Not.SameAs(typeof(BusType).Assembly),
                "Test support must have its own assembly."
            );
            Assert.That(
                typeof(BusType).Assembly.GetType("DxMessaging.Testing.MessageRecorder"),
                Is.Null,
                "The runtime core must not contain the test helper."
            );
            foreach (System.Reflection.AssemblyName dependency in support.GetReferencedAssemblies())
            {
                Assert.That(
                    dependency.Name,
                    Does.Not.StartWith("Unity"),
                    "The support API must not reference Unity assemblies."
                );
                Assert.That(
                    dependency.Name,
                    Does.Not.StartWith("nunit"),
                    "The support API must not reference NUnit."
                );
            }
        }

        /// <summary>Checks zero and extreme numeric contexts through the ordinary routing APIs.</summary>
        [Test]
        public void NumericContextBoundariesRemainValid(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(0, int.MinValue, int.MaxValue)] int contextValue
        )
        {
            BusType bus = new();
            using LeakWatcher leaks = new(bus: bus, label: $"{scenario.Kind}/{contextValue}");
            InstanceId context = new(contextValue);
            using MessageRecorder<ValueMessage> recorder = Create<ValueMessage>(
                scenario,
                bus,
                context
            );
            ValueMessage message = new(10);
            Emit(scenario, bus, ref message, context);
            Assert.That(
                recorder.Received,
                Is.EqualTo(new[] { message }),
                "The full numeric context range must route correctly."
            );
        }

        /// <summary>Checks that recording starts at subscription time and does not replay earlier emissions.</summary>
        [Test]
        public void RecorderDoesNotReplayEarlierMessages(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            BusType bus = new();
            using LeakWatcher leaks = new(bus: bus, label: scenario.Kind.ToString());
            ValueMessage message = new(10);
            Emit(scenario, bus, ref message);
            using MessageRecorder<ValueMessage> recorder = Create<ValueMessage>(scenario, bus);
            Assert.That(
                recorder.Received,
                Is.Empty,
                "A recorder must only capture messages delivered after registration."
            );
        }

        /// <summary>Checks nested disposal during both successful and failed outer cleanup.</summary>
        [Test]
        public void ReentrantDisposalDoesNotLoseRetryableCleanup(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(false, true)] bool failCleanup
        )
        {
            BusType inner = new();
            FailingCleanupBus bus = new(inner) { AllowCleanup = !failCleanup };
            using LeakWatcher leaks = new(bus: inner, label: $"{scenario.Kind}/{failCleanup}");
            MessageRecorder<ValueMessage> recorder = Create<ValueMessage>(scenario, bus);
            try
            {
                ValueMessage message = new(1);
                Emit(scenario, bus, ref message);
                bus.OnCleanup = recorder.Dispose;
                if (failCleanup)
                {
                    Assert.Throws<InvalidOperationException>(
                        recorder.Dispose,
                        "The outer cleanup failure must remain retryable."
                    );
                    Assert.That(
                        recorder.Received.Count,
                        Is.EqualTo(1),
                        "A nested Dispose must not clear state before failed outer cleanup finishes."
                    );
                    bus.AllowCleanup = true;
                    recorder.Dispose();
                }
                else
                {
                    Assert.DoesNotThrow(
                        recorder.Dispose,
                        "Reentrant successful cleanup must finish once."
                    );
                }
                Assert.That(
                    recorder.Received,
                    Is.Empty,
                    "Successful final cleanup must release captures."
                );
                Emit(scenario, bus, ref message);
                Assert.That(
                    recorder.Received,
                    Is.Empty,
                    "No subscription may survive successful cleanup."
                );
            }
            finally
            {
                bus.AllowCleanup = true;
                recorder.Dispose();
            }
        }

        /// <summary>Checks that captures follow ordinary interceptor acceptance and payload mutation.</summary>
        [Test]
        public void RecorderRespectsInterceptorVetoAndMutation(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            BusType bus = new();
            using LeakWatcher leaks = new(bus: bus, label: scenario.Kind.ToString());
            bool accept = false;
            bool Intercept(ref ValueMessage message)
            {
                message = new ValueMessage(99);
                return accept;
            }
            bool InterceptContext(ref InstanceId context, ref ValueMessage message)
            {
                return Intercept(ref message);
            }
            MessageRegistrationBuilder builder = new();
            using MessageRegistrationLease interceptor = builder.Build(
                new MessageRegistrationBuildOptions
                {
                    PreferredMessageBus = bus,
                    ActivateOnBuild = true,
                    Configure = token =>
                    {
                        switch (scenario.Kind)
                        {
                            case MessageKind.Untargeted:
                                token.RegisterUntargetedInterceptor<ValueMessage>(Intercept);
                                break;
                            case MessageKind.Targeted:
                                token.RegisterTargetedInterceptor<ValueMessage>(InterceptContext);
                                break;
                            case MessageKind.Broadcast:
                                token.RegisterBroadcastInterceptor<ValueMessage>(InterceptContext);
                                break;
                            default:
                                throw new ArgumentOutOfRangeException(nameof(scenario));
                        }
                    },
                }
            );
            using MessageRecorder<ValueMessage> recorder = Create<ValueMessage>(scenario, bus);
            ValueMessage message = new(1);
            Emit(scenario, bus, ref message);
            Assert.That(recorder.Received, Is.Empty, "Vetoed delivery must not be captured.");
            accept = true;
            Emit(scenario, bus, ref message);
            Assert.That(
                recorder.Received.Count,
                Is.EqualTo(1),
                "Accepted delivery must be captured once."
            );
            Assert.That(
                recorder.Received[0].Value,
                Is.EqualTo(99),
                "The recorder must observe interceptor mutations."
            );
        }

        /// <summary>Checks capture order, reference identity and final subscription cleanup for one payload shape.</summary>
        private static void Exercise<T>(MessageScenario scenario, T first, T second)
            where T : IUntargetedMessage, ITargetedMessage, IBroadcastMessage
        {
            BusType bus = new();
            using LeakWatcher leaks = new(bus: bus, label: scenario.Kind.ToString());
            using MessageRecorder<T> recorder = Create<T>(scenario, bus);
            Assert.That(recorder.Received, Is.Empty, "New recorders must start empty.");
            Emit(scenario, bus, ref first);
            Emit(scenario, bus, ref second);
            if (!typeof(T).IsValueType)
            {
                Assert.That(
                    recorder.Received[0],
                    Is.SameAs((object)first),
                    "Class captures must retain the original object reference."
                );
            }
            Assert.That(
                recorder.Received,
                Is.EqualTo(new[] { first, second }),
                "Captures must retain the delivery order and message values."
            );
            recorder.Dispose();
            recorder.Dispose();
            Assert.That(
                recorder.Received,
                Is.Empty,
                "Repeated successful disposal must remain empty."
            );
            Emit(scenario, bus, ref first);
            Assert.That(recorder.Received, Is.Empty, "Disposal must remove the receiver.");
        }

        /// <summary>Selects the recorder factory matching the shared message scenario.</summary>
        private static MessageRecorder<T> Create<T>(
            MessageScenario scenario,
            IMessageBus bus,
            InstanceId? selectedContext = null
        )
            where T : IUntargetedMessage, ITargetedMessage, IBroadcastMessage
        {
            InstanceId context = selectedContext ?? new InstanceId(123);
            return scenario.Kind switch
            {
                MessageKind.Untargeted => MessageRecorder.Untargeted<T>(bus),
                MessageKind.Targeted => MessageRecorder.Targeted<T>(context, bus),
                MessageKind.Broadcast => MessageRecorder.Broadcast<T>(context, bus),
                _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
            };
        }

        /// <summary>Emits a shared test payload through the scenario route and selected context.</summary>
        private static void Emit<T>(
            MessageScenario scenario,
            IMessageBus bus,
            ref T message,
            InstanceId? context = null
        )
            where T : IUntargetedMessage, ITargetedMessage, IBroadcastMessage
        {
            InstanceId selectedContext = context ?? new InstanceId(123);
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                    bus.UntargetedBroadcast(ref message);
                    break;
                case MessageKind.Targeted:
                    bus.TargetedBroadcast(ref selectedContext, ref message);
                    break;
                case MessageKind.Broadcast:
                    bus.SourcedBroadcast(ref selectedContext, ref message);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario));
            }
        }

        private sealed class FailingCleanupBus : DelegatingMessageBus
        {
            internal bool AllowCleanup;
            internal Action OnCleanup;

            internal FailingCleanupBus(IMessageBus inner)
                : base(inner) { }

            /// <summary>Runs the controlled cleanup callback before optionally raising the declared cleanup failure.</summary>
            public override void Deregister<T>(in MessageBusRegistration registration)
            {
                Action callback = OnCleanup;
                OnCleanup = null;
                callback?.Invoke();
                if (!AllowCleanup)
                {
                    throw new InvalidOperationException("Intentional recorder cleanup failure.");
                }
                base.Deregister<T>(in registration);
            }
        }

        private readonly struct ValueMessage
            : IUntargetedMessage<ValueMessage>,
                ITargetedMessage<ValueMessage>,
                IBroadcastMessage<ValueMessage>
        {
            public ValueMessage(int value)
            {
                Value = value;
            }

            public int Value { get; }
            public Type MessageType => typeof(ValueMessage);
        }

        private sealed class ReferenceMessage
            : IUntargetedMessage<ReferenceMessage>,
                ITargetedMessage<ReferenceMessage>,
                IBroadcastMessage<ReferenceMessage>
        {
            public ReferenceMessage(int value)
            {
                Value = value;
            }

            public int Value { get; }
            public Type MessageType => typeof(ReferenceMessage);
        }
    }
}
