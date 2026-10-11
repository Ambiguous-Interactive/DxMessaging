#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using BusType = DxMessaging.Core.MessageBus.MessageBus;

    /// <summary>Checks live-registration accounting above a nonempty baseline.</summary>
    /// <remarks>
    /// On 2026-10-11, same-priority additions and balanced cross-kind changes
    /// escaped the public-counter sum. These tests pin reference counts,
    /// individual counter pairs, complete cleanup, and disposal-time snapshots.
    /// </remarks>
    [TestFixture]
    public sealed class RegistrationLeakAccountingTests
    {
        public static IEnumerable<TestCaseData> CallbackCases()
        {
            foreach (MessageScenario scenario in MessageScenarios.AllKindsIncludingWithoutContext)
            {
                foreach (bool postProcessor in new[] { false, true })
                {
                    foreach (bool sameHandler in new[] { false, true })
                    {
                        foreach (bool forwarding in new[] { false, true })
                        {
                            foreach (int priority in new[] { 0, 1 })
                            {
                                yield return new TestCaseData(
                                    scenario.WithPostProcessor(postProcessor),
                                    sameHandler,
                                    forwarding,
                                    priority
                                );
                            }
                        }
                    }
                }
            }
        }

        public static IEnumerable<TestCaseData> DuplicateCases()
        {
            foreach (MessageScenario scenario in MessageScenarios.AllKindsIncludingWithoutContext)
            {
                foreach (bool postProcessor in new[] { false, true })
                {
                    foreach (bool forwarding in new[] { false, true })
                    {
                        yield return new TestCaseData(
                            scenario.WithPostProcessor(postProcessor),
                            forwarding
                        );
                    }
                }
            }
        }

        public static IEnumerable<TestCaseData> MiddlewareCases()
        {
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            {
                foreach (bool forwarding in new[] { false, true })
                {
                    foreach (bool global in new[] { false, true })
                    {
                        yield return new TestCaseData(scenario, forwarding, global);
                    }
                }
            }
        }

        [SetUp]
        public void ResetBeforeTest() => DxMessagingStaticState.Reset();

        [TearDown]
        public void ResetAfterTest() => DxMessagingStaticState.Reset();

        [TestCaseSource(nameof(CallbackCases))]
        public void AddedLiveCallbackIsDetectedAboveTheBaseline(
            MessageScenario scenario,
            bool sameHandler,
            bool forwarding,
            int priority
        )
        {
            using RegistrationSession session = new(scenario, sameHandler, forwarding, priority);
            Assert.AreEqual("A", session.Observe(), session.Report);
            using LeakWatcher watcher = new(session.Bus, label: session.Report);
            session.AddedToken.Enable();
            session.Register(session.AddedToken, session.AddedCallback, scenario, priority);
            Assert.AreEqual("AB", session.Observe(), session.Report);
            Assert.AreEqual(1, watcher.LeakedRegistrations, session.Report);
            Assert.AreEqual(watcher.InitialSnapshot + 1, watcher.Snapshot, session.Report);
            AssertionException failure = Assert.Throws<AssertionException>(
                watcher.Dispose,
                session.Report
            );
            StringAssert.Contains("Registration delta=1", failure.Message);
            StringAssert.Contains("LiveRegistrations 1->2", failure.Message);
            session.AddedToken.Dispose();
            Assert.AreEqual("A", session.Observe(), session.Report);
            Assert.AreEqual(
                1,
                watcher.LeakedRegistrations,
                "Disposal freezes the recorded leak. " + session.Report
            );
            StringAssert.Contains("LiveRegistrations 1->2", watcher.DescribeDelta());
            Assert.DoesNotThrow(watcher.Dispose, "Disposal remains idempotent. " + session.Report);
        }

        [TestCaseSource(nameof(CallbackCases))]
        public void RemovedCallbackPreservesTheLiveBaseline(
            MessageScenario scenario,
            bool sameHandler,
            bool forwarding,
            int priority
        )
        {
            using RegistrationSession session = new(scenario, sameHandler, forwarding, priority);
            using LeakWatcher watcher = new(session.Bus, label: session.Report);
            session.AddedToken.Enable();
            MessageRegistrationHandle handle = session.Register(
                session.AddedToken,
                session.AddedCallback,
                scenario,
                priority
            );
            Assert.AreEqual("AB", session.Observe(), session.Report);
            session.AddedToken.RemoveRegistration(handle);
            Assert.AreEqual("A", session.Observe(), session.Report);
            Assert.AreEqual(0, watcher.LeakedRegistrations, session.Report);
            Assert.AreEqual(watcher.InitialSnapshot, watcher.Snapshot, session.Report);
            Assert.DoesNotThrow(watcher.Dispose, session.Report);
        }

        [TestCaseSource(nameof(DuplicateCases))]
        public void DuplicateCallbackReferencesRemainCountedUntilRemoved(
            MessageScenario scenario,
            bool forwarding
        )
        {
            using RegistrationSession session = new(scenario, true, forwarding, 0);
            using LeakWatcher watcher = new(session.Bus, label: session.Report);
            session.AddedToken.Enable();
            MessageRegistrationHandle first = session.Register(
                session.AddedToken,
                session.BaselineCallback,
                scenario,
                0
            );
            MessageRegistrationHandle second = session.Register(
                session.AddedToken,
                session.BaselineCallback,
                scenario,
                0
            );
            Assert.AreEqual(
                "A",
                session.Observe(),
                "Equal delegates dispatch once. " + session.Report
            );
            Assert.AreEqual(2, watcher.LeakedRegistrations, session.Report);
            session.AddedToken.RemoveRegistration(first);
            Assert.AreEqual("A", session.Observe(), session.Report);
            Assert.AreEqual(1, watcher.LeakedRegistrations, session.Report);
            session.AddedToken.RemoveRegistration(second);
            Assert.AreEqual("A", session.Observe(), session.Report);
            Assert.AreEqual(0, watcher.LeakedRegistrations, session.Report);
            Assert.DoesNotThrow(watcher.Dispose, session.Report);
        }

        [TestCaseSource(nameof(MiddlewareCases))]
        public void DuplicateMiddlewareReferencesAreDetected(
            MessageScenario scenario,
            bool forwarding,
            bool global
        )
        {
            using RegistrationSession session = new(scenario, true, forwarding, 0);
            using LeakWatcher watcher = new(session.Bus, label: session.Report);
            session.AddedToken.Enable();
            MessageRegistrationHandle first = session.RegisterMiddleware(global);
            MessageRegistrationHandle second = session.RegisterMiddleware(global);
            string expected = global ? "GA" : "IA";
            Assert.AreEqual(expected, session.Observe(), session.Report);
            Assert.AreEqual(2, watcher.LeakedRegistrations, session.Report);
            session.AddedToken.RemoveRegistration(first);
            Assert.AreEqual(expected, session.Observe(), session.Report);
            Assert.AreEqual(1, watcher.LeakedRegistrations, session.Report);
            AssertionException failure = Assert.Throws<AssertionException>(
                watcher.Dispose,
                session.Report
            );
            StringAssert.Contains("Registration delta=1", failure.Message);
            session.AddedToken.RemoveRegistration(second);
            Assert.AreEqual("A", session.Observe(), session.Report);
            Assert.AreEqual(
                1,
                watcher.LeakedRegistrations,
                "Recorded leak remains frozen. " + session.Report
            );
        }

        [Test]
        public void BalancedCrossKindChangesFailTheIndividualCounterInvariant(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(false, true)] bool forwarding
        )
        {
            using RegistrationSession session = new(scenario, true, forwarding, 0);
            using LeakWatcher watcher = new(session.Bus, label: session.Report);
            MessageScenario other =
                scenario.Kind == MessageKind.Untargeted ? MessageScenario.Targeted()
                : scenario.Kind == MessageKind.Targeted ? MessageScenario.Broadcast()
                : MessageScenario.Untargeted();
            session.BaselineToken.Dispose();
            session.Register(session.AddedToken, session.AddedCallback, other, 0);
            session.AddedToken.Enable();
            Assert.AreEqual(string.Empty, session.Observe(), session.Report);
            Assert.AreEqual("B", session.Observe(other), session.Report);
            Assert.AreEqual(
                0,
                watcher.LeakedRegistrations,
                "Total count is unchanged. " + session.Report
            );
            AssertionException failure = Assert.Throws<AssertionException>(
                watcher.Dispose,
                session.Report
            );
            StringAssert.Contains("Registration delta=0", failure.Message);
        }

        [Test]
        public void OpaqueCustomBusKeepsItsPublicCounterAccountingLimit(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            using RegistrationSession session = new(scenario, true, false, 0, opaque: true);
            using LeakWatcher watcher = new(session.Bus, label: session.Report);
            session.AddedToken.Enable();
            session.Register(session.AddedToken, session.AddedCallback, scenario, 0);
            Assert.AreEqual("AB", session.Observe(), session.Report);
            Assert.AreEqual(
                0,
                watcher.LeakedRegistrations,
                "Opaque buses expose only their declared counter granularity. " + session.Report
            );
            session.AddedToken.Dispose();
            Assert.AreEqual("A", session.Observe(), session.Report);
            Assert.DoesNotThrow(watcher.Dispose, session.Report);
        }

        private sealed class OpaqueForwardingBus : DelegatingMessageBus
        {
            public OpaqueForwardingBus(IMessageBus inner)
                : base(inner) { }

            internal override IMessageBus RegistrationCountSource => null;
        }

        private sealed class Callback
        {
            private readonly List<string> _trace;
            private readonly string _label;

            public Callback(List<string> trace, string label)
            {
                _trace = trace;
                _label = label;
            }

            public void Untargeted(in SimpleUntargetedMessage message) => _trace.Add(_label);

            public void Targeted(in SimpleTargetedMessage message) => _trace.Add(_label);

            public void Broadcast(in SimpleBroadcastMessage message) => _trace.Add(_label);

            public void TargetedContext(in InstanceId context, in SimpleTargetedMessage message) =>
                _trace.Add(_label);

            public void BroadcastContext(
                in InstanceId context,
                in SimpleBroadcastMessage message
            ) => _trace.Add(_label);

            public void GlobalUntargeted(in IUntargetedMessage message) => _trace.Add(_label);

            public void GlobalTargeted(in InstanceId context, in ITargetedMessage message) =>
                _trace.Add(_label);

            public void GlobalBroadcast(in InstanceId context, in IBroadcastMessage message) =>
                _trace.Add(_label);

            public bool InterceptUntargeted(ref SimpleUntargetedMessage message)
            {
                _trace.Add(_label);
                return true;
            }

            public bool InterceptTargeted(ref InstanceId context, ref SimpleTargetedMessage message)
            {
                _trace.Add(_label);
                return true;
            }

            public bool InterceptBroadcast(
                ref InstanceId context,
                ref SimpleBroadcastMessage message
            )
            {
                _trace.Add(_label);
                return true;
            }
        }

        private sealed class RegistrationSession : IDisposable
        {
            public readonly IMessageBus Bus;
            public readonly MessageRegistrationToken BaselineToken;
            public readonly MessageRegistrationToken AddedToken;
            public readonly Callback BaselineCallback;
            public readonly Callback AddedCallback;
            private readonly Callback _globalCallback;
            private readonly Callback _interceptorCallback;
            private readonly MessageHandler _baselineHandler;
            private readonly MessageHandler _addedHandler;
            private readonly MessageScenario _scenario;
            private readonly List<string> _trace = new();
            private readonly LeakWatcher _cleanup;
            public string Report { get; }

            public RegistrationSession(
                MessageScenario scenario,
                bool sameHandler,
                bool forwarding,
                int priority,
                bool opaque = false
            )
            {
                _scenario = scenario;
                Report =
                    "kind="
                    + scenario
                    + " sameHandler="
                    + sameHandler
                    + " forwarding="
                    + forwarding
                    + " priority="
                    + priority
                    + " opaque="
                    + opaque;
                BusType inner = new() { DiagnosticsMode = false };
                Bus =
                    opaque ? new OpaqueForwardingBus(inner)
                    : forwarding ? new DelegatingMessageBus(inner)
                    : inner;
                _cleanup = new LeakWatcher(Bus, label: Report);
                _baselineHandler = new MessageHandler(new InstanceId(54800), Bus) { active = true };
                _addedHandler = sameHandler
                    ? _baselineHandler
                    : new MessageHandler(new InstanceId(54801), Bus) { active = true };
                BaselineToken = MessageRegistrationToken.Create(_baselineHandler, Bus);
                AddedToken = MessageRegistrationToken.Create(_addedHandler, Bus);
                BaselineCallback = new Callback(_trace, "A");
                AddedCallback = new Callback(_trace, "B");
                _globalCallback = new Callback(_trace, "G");
                _interceptorCallback = new Callback(_trace, "I");
                Register(BaselineToken, BaselineCallback, scenario, 0);
                BaselineToken.Enable();
            }

            public MessageRegistrationHandle Register(
                MessageRegistrationToken token,
                Callback callback,
                MessageScenario scenario,
                int priority
            )
            {
                InstanceId context = new(54700);
                switch (scenario.Kind)
                {
                    case MessageKind.Untargeted:
                        return scenario.UsePostProcessor
                            ? token.RegisterUntargetedPostProcessor<SimpleUntargetedMessage>(
                                callback.Untargeted,
                                priority
                            )
                            : token.RegisterUntargeted<SimpleUntargetedMessage>(
                                callback.Untargeted,
                                priority
                            );
                    case MessageKind.Targeted:
                        return scenario.UsePostProcessor
                            ? token.RegisterTargetedPostProcessor<SimpleTargetedMessage>(
                                context,
                                callback.Targeted,
                                priority
                            )
                            : token.RegisterTargeted<SimpleTargetedMessage>(
                                context,
                                callback.Targeted,
                                priority
                            );
                    case MessageKind.Broadcast:
                        return scenario.UsePostProcessor
                            ? token.RegisterBroadcastPostProcessor<SimpleBroadcastMessage>(
                                context,
                                callback.Broadcast,
                                priority
                            )
                            : token.RegisterBroadcast<SimpleBroadcastMessage>(
                                context,
                                callback.Broadcast,
                                priority
                            );
                    case MessageKind.TargetedWithoutTargeting:
                        return scenario.UsePostProcessor
                            ? token.RegisterTargetedWithoutTargetingPostProcessor<SimpleTargetedMessage>(
                                callback.TargetedContext,
                                priority
                            )
                            : token.RegisterTargetedWithoutTargeting<SimpleTargetedMessage>(
                                callback.TargetedContext,
                                priority
                            );
                    case MessageKind.BroadcastWithoutSource:
                        return scenario.UsePostProcessor
                            ? token.RegisterBroadcastWithoutSourcePostProcessor<SimpleBroadcastMessage>(
                                callback.BroadcastContext,
                                priority
                            )
                            : token.RegisterBroadcastWithoutSource<SimpleBroadcastMessage>(
                                callback.BroadcastContext,
                                priority
                            );
                    default:
                        throw new ArgumentOutOfRangeException(nameof(scenario));
                }
            }

            public MessageRegistrationHandle RegisterMiddleware(bool global)
            {
                if (global)
                {
                    return AddedToken.RegisterGlobalAcceptAll(
                        _globalCallback.GlobalUntargeted,
                        _globalCallback.GlobalTargeted,
                        _globalCallback.GlobalBroadcast
                    );
                }
                switch (_scenario.Kind)
                {
                    case MessageKind.Untargeted:
                        return AddedToken.RegisterUntargetedInterceptor<SimpleUntargetedMessage>(
                            _interceptorCallback.InterceptUntargeted
                        );
                    case MessageKind.Targeted:
                        return AddedToken.RegisterTargetedInterceptor<SimpleTargetedMessage>(
                            _interceptorCallback.InterceptTargeted
                        );
                    case MessageKind.Broadcast:
                        return AddedToken.RegisterBroadcastInterceptor<SimpleBroadcastMessage>(
                            _interceptorCallback.InterceptBroadcast
                        );
                    default:
                        throw new ArgumentOutOfRangeException(nameof(_scenario));
                }
            }

            public string Observe(MessageScenario scenario = null)
            {
                _trace.Clear();
                InstanceId context = new(54700);
                switch ((scenario ?? _scenario).Kind)
                {
                    case MessageKind.Untargeted:
                        SimpleUntargetedMessage untargeted = new();
                        Bus.UntargetedBroadcast(ref untargeted);
                        break;
                    case MessageKind.Targeted:
                    case MessageKind.TargetedWithoutTargeting:
                        SimpleTargetedMessage targeted = new();
                        Bus.TargetedBroadcast(ref context, ref targeted);
                        break;
                    case MessageKind.Broadcast:
                    case MessageKind.BroadcastWithoutSource:
                        SimpleBroadcastMessage broadcast = new();
                        Bus.SourcedBroadcast(ref context, ref broadcast);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(scenario));
                }
                return string.Concat(_trace);
            }

            public void Dispose()
            {
                AddedToken.Dispose();
                BaselineToken.Dispose();
                _addedHandler.active = false;
                _baselineHandler.active = false;
                Assert.AreEqual(string.Empty, Observe(), "Final private-bus cleanup. " + Report);
                _cleanup.Dispose();
            }
        }
    }
}
#endif
