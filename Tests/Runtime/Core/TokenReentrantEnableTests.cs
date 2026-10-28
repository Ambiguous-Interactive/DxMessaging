namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;
    using BusType = DxMessaging.Core.MessageBus.MessageBus;

    /// <summary>Preserves activation when a foreign registration callback calls Enable again.</summary>
    /// <remarks>
    /// 2026-10-24: Nested Enable rebuilt the shared replay list, invalidating its outer
    /// enumerator and rolling back an otherwise successful activation. The current replay
    /// owns activation; failure must still release that ownership before later recovery.
    /// </remarks>
    [TestFixture]
#if !UNITY_2021_3_OR_NEWER
    [NonParallelizable]
#endif
    public sealed class TokenReentrantEnableTests
    {
        private static IEnumerable<TestCaseData> ActivationCases()
        {
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            {
                foreach (bool fast in new[] { false, true })
                {
                    foreach (int count in new[] { 1, 2, 3 })
                    {
                        foreach (bool after in new[] { false, true })
                        {
                            foreach (bool nested in new[] { false, true })
                            {
                                foreach (bool retarget in new[] { false, true })
                                {
                                    yield return new TestCaseData(
                                        scenario,
                                        fast,
                                        count,
                                        after,
                                        nested,
                                        retarget
                                    ).SetName(
                                        $"ReentrantEnable_{scenario}_fast{fast}_count{count}_after{after}_nested{nested}_retarget{retarget}"
                                    );
                                }
                            }
                        }
                    }
                }
            }
        }

        private static IEnumerable<TestCaseData> FailureCases()
        {
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            {
                foreach (bool fast in new[] { false, true })
                {
                    foreach (bool after in new[] { false, true })
                    {
                        foreach (bool retarget in new[] { false, true })
                        {
                            yield return new TestCaseData(scenario, fast, after, retarget).SetName(
                                $"ReentrantEnableFailure_{scenario}_fast{fast}_after{after}_retarget{retarget}"
                            );
                        }
                    }
                }
            }
        }

        /// <summary>Nested activation leaves the owning replay and subsequent lifecycle intact.</summary>
        [TestCaseSource(nameof(ActivationCases))]
        public void NestedEnableKeepsOwningActivationAndCleanup(
            MessageScenario scenario,
            bool fast,
            int count,
            bool after,
            bool nested,
            bool retarget
        )
        {
            Scope scope = new Scope(scenario, fast, count, after, nested, retarget, false);
            try
            {
                scope.Run();
            }
            finally
            {
                scope.Dispose();
            }
            string report = scope.Report;
            Assert.That(scope.Failure, Is.Null, report);
            Assert.That(scope.NestedAcquisitions, Is.Zero, report);
            Assert.That(scope.Callbacks, Is.EqualTo(1), report);
            Assert.That(scope.MetadataAfter, Is.EqualTo(count), report);
            Assert.That(scope.EnabledAfter, Is.True, report);
            Assert.That(scope.FirstDelivery, Is.All.EqualTo(1), report);
            Assert.That(scope.DisabledDelivery, Is.All.EqualTo(0), report);
            Assert.That(scope.RecoveryDelivery, Is.All.EqualTo(1), report);
            Assert.That(scope.RecoveryFailure, Is.Null, report);
            Assert.That(scope.CleanupFailure, Is.Null, report);
            Assert.That(scope.TokenCleanupCounters, Is.All.EqualTo(0), report);
            Assert.That(scope.FinalCounters, Is.All.EqualTo(0), report);
        }

        /// <summary>Failed activation preserves its error and releases replay ownership for retry.</summary>
        [TestCaseSource(nameof(FailureCases))]
        public void ThrowAfterNestedEnablePreservesErrorAndAllowsRecovery(
            MessageScenario scenario,
            bool fast,
            bool after,
            bool retarget
        )
        {
            Scope scope = new Scope(scenario, fast, 2, after, true, retarget, true);
            try
            {
                scope.Run();
            }
            finally
            {
                scope.Dispose();
            }
            string report = scope.Report;
            Assert.That(scope.Failure, Is.SameAs(scope.Marker), report);
            Assert.That(scope.NestedAcquisitions, Is.Zero, report);
            Assert.That(scope.Callbacks, Is.EqualTo(1), report);
            Assert.That(scope.MetadataAfter, Is.EqualTo(2), report);
            Assert.That(scope.EnabledAfter, Is.EqualTo(retarget), report);
            Assert.That(scope.FirstDelivery, Is.All.EqualTo(retarget ? 1 : 0), report);
            Assert.That(scope.DisabledDelivery, Is.All.EqualTo(0), report);
            Assert.That(scope.RecoveryDelivery, Is.All.EqualTo(1), report);
            Assert.That(scope.RecoveryFailure, Is.Null, report);
            Assert.That(scope.CleanupFailure, Is.Null, report);
            Assert.That(scope.TokenCleanupCounters, Is.All.EqualTo(0), report);
            Assert.That(scope.FinalCounters, Is.All.EqualTo(0), report);
        }

        private sealed class Scope : IDisposable
        {
            private static readonly InstanceId Context = new InstanceId(988);
            private readonly bool _oldDebug = MessagingDebug.enabled;
            private readonly Action<LogLevel, string> _oldLog = MessagingDebug.LogFunction;
            private readonly MessageScenario _scenario;
            private readonly bool _fast,
                _nested,
                _retarget,
                _throwing;
            private readonly ProbeBus[] _buses;
            private readonly MessageHandler _handler;
            private readonly MessageRegistrationToken _token;
            private readonly int[] _calls;
            private readonly List<string> _trace = new List<string>();
            internal readonly Exception Marker = new InvalidOperationException(
                "reentrant-enable-marker"
            );
            internal Exception Failure,
                RecoveryFailure,
                CleanupFailure;
            internal int Callbacks,
                NestedAcquisitions,
                MetadataAfter;
            internal bool EnabledAfter;
            internal int[] FirstDelivery,
                DisabledDelivery,
                RecoveryDelivery,
                TokenCleanupCounters,
                FinalCounters;
            internal string Report =>
                $"{_scenario}, fast={_fast}, nested={_nested}, retarget={_retarget}, throwing={_throwing}; {string.Join(",", _trace)}";

            internal Scope(
                MessageScenario scenario,
                bool fast,
                int count,
                bool after,
                bool nested,
                bool retarget,
                bool throwing
            )
            {
                _scenario = scenario;
                _fast = fast;
                _nested = nested;
                _retarget = retarget;
                _throwing = throwing;
                _calls = new int[count];
                _buses = new[] { new ProbeBus(after), new ProbeBus(after) };
                _handler = new MessageHandler(new InstanceId(989), _buses[0]) { active = true };
                _token = MessageRegistrationToken.Create(_handler, _buses[0]);
                _token.DiagnosticMode = false;
            }

            internal void Run()
            {
                MessagingDebug.enabled = false;
                MessagingDebug.LogFunction = null;
                for (int i = 0; i < _calls.Length; ++i)
                {
                    Stage(i);
                }
                if (_retarget)
                {
                    _token.Enable();
                }
                ProbeBus target = _buses[_retarget ? 1 : 0];
                target.Callback = () =>
                {
                    ++Callbacks;
                    int before = target.Completed;
                    _trace.Add("callback");
                    if (_nested)
                    {
                        _token.Enable();
                    }
                    NestedAcquisitions = target.Completed - before;
                    if (_throwing)
                    {
                        throw Marker;
                    }
                };
                try
                {
                    if (_retarget)
                    {
                        _token.RetargetMessageBus(target, MessageBusRebindMode.RebindActive);
                    }
                    else
                    {
                        _token.Enable();
                    }
                }
                catch (Exception exception)
                {
                    Failure = exception;
                    _trace.Add(exception.GetType().Name);
                }
                MetadataAfter = _token._metadata.Count;
                EnabledAfter = _token.Enabled;
                FirstDelivery = Deliver();
                _token.Disable();
                DisabledDelivery = Deliver();
                try
                {
                    _token.Enable();
                }
                catch (Exception exception)
                {
                    RecoveryFailure = exception;
                }
                RecoveryDelivery = Deliver();
            }

            private void Stage(int index)
            {
                Action<ProbeMessage> normal = _ => ++_calls[index];
                MessageHandler.FastHandler<ProbeMessage> fast = (in ProbeMessage _) =>
                    ++_calls[index];
                switch (_scenario.Kind)
                {
                    case MessageKind.Untargeted:
                        if (_fast)
                        {
                            _token.RegisterUntargeted(fast);
                        }
                        else
                        {
                            _token.RegisterUntargeted(normal);
                        }
                        break;
                    case MessageKind.Targeted:
                        if (_fast)
                        {
                            _token.RegisterTargeted(Context, fast);
                        }
                        else
                        {
                            _token.RegisterTargeted(Context, normal);
                        }
                        break;
                    case MessageKind.Broadcast:
                        if (_fast)
                        {
                            _token.RegisterBroadcast(Context, fast);
                        }
                        else
                        {
                            _token.RegisterBroadcast(Context, normal);
                        }
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(_scenario));
                }
            }

            private int[] Deliver()
            {
                Array.Clear(_calls, 0, _calls.Length);
                foreach (ProbeBus bus in _buses)
                {
                    ProbeMessage message = default;
                    InstanceId context = Context;
                    switch (_scenario.Kind)
                    {
                        case MessageKind.Untargeted:
                            bus.Actual.UntargetedBroadcast(ref message);
                            break;
                        case MessageKind.Targeted:
                            bus.Actual.TargetedBroadcast(ref context, ref message);
                            break;
                        case MessageKind.Broadcast:
                            bus.Actual.SourcedBroadcast(ref context, ref message);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(_scenario));
                    }
                }
                return (int[])_calls.Clone();
            }

            private int[] Counters()
            {
                List<int> counts = new List<int>();
                foreach (ProbeBus bus in _buses)
                {
                    counts.Add(bus.Actual.RegisteredUntargeted);
                    counts.Add(bus.Actual.RegisteredTargeted);
                    counts.Add(bus.Actual.RegisteredBroadcast);
                    counts.Add(bus.Actual.RegisteredInterceptors);
                    counts.Add(bus.Actual.RegisteredPostProcessors);
                    counts.Add(bus.Actual.RegisteredGlobalAcceptAll);
                }
                return counts.ToArray();
            }

            public void Dispose()
            {
                try
                {
                    try
                    {
                        _token.UnregisterAll();
                    }
                    catch (Exception exception)
                    {
                        CleanupFailure = exception;
                    }
                    TokenCleanupCounters = Counters();
                    foreach (ProbeBus bus in _buses)
                    {
                        bus.Callback = null;
                        foreach (MessageBusRegistration handle in bus.Handles)
                        {
                            bus.Actual.Deregister<ProbeMessage>(in handle);
                        }
                        bus.Actual.Trim(force: true);
                    }
                    _handler.active = false;
                    FinalCounters = Counters();
                }
                finally
                {
                    MessagingDebug.enabled = _oldDebug;
                    MessagingDebug.LogFunction = _oldLog;
                }
            }
        }

        private sealed class ProbeBus : DelegatingMessageBus
        {
            internal readonly BusType Actual;
            internal readonly List<MessageBusRegistration> Handles =
                new List<MessageBusRegistration>();
            internal Action Callback;
            internal int Completed;
            private readonly bool _after;

            internal ProbeBus(bool after)
                : this(new BusType { DiagnosticsMode = false }, after) { }

            private ProbeBus(BusType actual, bool after)
                : base(actual)
            {
                Actual = actual;
                _after = after;
            }

            private void Before()
            {
                if (!_after)
                {
                    InvokeOnce();
                }
            }

            private void InvokeOnce()
            {
                Action callback = Callback;
                Callback = null;
                callback?.Invoke();
            }

            private MessageBusRegistration After<T>(MessageBusRegistration value)
                where T : IMessage
            {
                ++Completed;
                Handles.Add(value);
                if (_after)
                {
                    try
                    {
                        InvokeOnce();
                    }
                    catch
                    {
                        Actual.Deregister<T>(in value);
                        throw;
                    }
                }
                return value;
            }

            public override MessageBusRegistration RegisterUntargeted<T>(
                MessageHandler handler,
                int priority = 0
            )
            {
                Before();
                return After<T>(base.RegisterUntargeted<T>(handler, priority));
            }

            public override MessageBusRegistration RegisterTargeted<T>(
                InstanceId context,
                MessageHandler handler,
                int priority = 0
            )
            {
                Before();
                return After<T>(base.RegisterTargeted<T>(context, handler, priority));
            }

            public override MessageBusRegistration RegisterSourcedBroadcast<T>(
                InstanceId context,
                MessageHandler handler,
                int priority = 0
            )
            {
                Before();
                return After<T>(base.RegisterSourcedBroadcast<T>(context, handler, priority));
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
