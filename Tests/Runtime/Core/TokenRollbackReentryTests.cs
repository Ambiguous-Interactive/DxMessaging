namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;
    using BusType = DxMessaging.Core.MessageBus.MessageBus;

    /// <summary>Preserves cleanup accepted by a nested activation during rollback.</summary>
    /// <remarks>2026-10-25: Successful rollback cleanup used to discard newly accepted cleanup in the same token slot.</remarks>
    [TestFixture]
#if !UNITY_2021_3_OR_NEWER
    [NonParallelizable]
#endif
    public sealed class TokenRollbackReentryTests
    {
        private static IEnumerable<TestCaseData> RollbackCases()
        {
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            {
                foreach (bool fast in new[] { false, true })
                {
                    foreach (int count in new[] { 2, 3 })
                    {
                        foreach (bool after in new[] { false, true })
                        {
                            foreach (bool nested in new[] { false, true })
                            {
                                foreach (bool rejectCleanup in new[] { false, true })
                                {
                                    foreach (bool diagnostics in new[] { false, true })
                                    {
                                        yield return new TestCaseData(
                                            scenario,
                                            fast,
                                            count,
                                            after,
                                            nested,
                                            rejectCleanup,
                                            diagnostics
                                        ).SetName(
                                            $"RollbackReentry_{scenario}_fast{fast}_count{count}_after{after}_nested{nested}_reject{rejectCleanup}_diagnostics{diagnostics}"
                                        );
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>Keeps accepted cleanup retryable across failed activation and nested Enable.</summary>
        [TestCaseSource(nameof(RollbackCases))]
        public void RollbackPreservesNestedActivationCleanup(
            MessageScenario scenario,
            bool fast,
            int count,
            bool after,
            bool nested,
            bool rejectCleanup,
            bool diagnostics
        )
        {
            Scope scope = new Scope(
                scenario,
                fast,
                count,
                after,
                nested,
                rejectCleanup,
                diagnostics
            );
            using (new LeakWatcher(scope.Inner, label: scope.Label))
            {
                try
                {
                    scope.Run();
                }
                finally
                {
                    scope.Dispose();
                }
            }

            string report = scope.Report;
            Assert.That(scope.Setup, Is.True, report);
            Assert.That(scope.Failure, Is.SameAs(scope.Bus.RegistrationMarker), report);
            Assert.That(scope.Bus.Callbacks, Is.EqualTo(1), report);
            Assert.That(scope.EnabledAtCallback, Is.False, report);
            Assert.That(scope.EnabledAfterCallback, Is.EqualTo(nested), report);
            Assert.That(scope.EnabledAfterRollback, Is.EqualTo(nested || rejectCleanup), report);
            Assert.That(scope.MetadataAfterRollback, Is.EqualTo(count), report);
            Assert.That(scope.RollbackDelivery, Is.All.EqualTo(nested ? 1 : 0), report);
            Assert.That(scope.DisableFailure, Is.Null, report);
            Assert.That(scope.EnabledAfterDisable, Is.False, report);
            Assert.That(scope.DisabledDelivery, Is.All.EqualTo(0), report);
            Assert.That(scope.RecoveryFailure, Is.Null, report);
            Assert.That(scope.RecoveryDelivery, Is.All.EqualTo(1), report);
            Assert.That(scope.UnregisterFailure, Is.Null, report);
            Assert.That(scope.EnabledAfterUnregister, Is.False, report);
            Assert.That(scope.UnregisteredDelivery, Is.All.EqualTo(0), report);
            Assert.That(scope.TokenCleanupCounters, Is.All.EqualTo(0), report);
            Assert.That(scope.MetadataAfterUnregister, Is.Zero, report);
            Assert.That(scope.CallCountsAfterUnregister, Is.Zero, report);
            Assert.That(scope.HistoryAfterUnregister, Is.Zero, report);
            Assert.That(scope.FinalCleanupFailure, Is.Null, report);
            Assert.That(scope.FinalCounters, Is.All.EqualTo(0), report);
            Assert.That(scope.StaticsRestored, Is.True, report);
        }

        private sealed class Scope : IDisposable
        {
            private static readonly InstanceId Context = new InstanceId(990);
            private readonly bool _oldDebug = MessagingDebug.enabled;
            private readonly Action<LogLevel, string> _oldLog = MessagingDebug.LogFunction;
            private readonly MessageScenario _scenario;
            private readonly bool _fast,
                _nested;
            private readonly MessageHandler _handler;
            private readonly MessageRegistrationToken _token;
            private readonly int[] _calls;
            internal readonly BusType Inner = new BusType { DiagnosticsMode = false };
            internal readonly ProbeBus Bus;
            internal readonly string Label;
            internal Exception Failure,
                DisableFailure,
                RecoveryFailure,
                UnregisterFailure,
                FinalCleanupFailure;
            internal bool Setup,
                EnabledAtCallback,
                EnabledAfterCallback,
                EnabledAfterRollback,
                EnabledAfterDisable,
                EnabledAfterUnregister,
                StaticsRestored;
            internal int MetadataAfterRollback,
                MetadataAfterUnregister,
                CallCountsAfterUnregister,
                HistoryAfterUnregister;
            internal int[] RollbackDelivery,
                DisabledDelivery,
                RecoveryDelivery,
                UnregisteredDelivery,
                TokenCleanupCounters,
                FinalCounters;

            internal Scope(
                MessageScenario scenario,
                bool fast,
                int count,
                bool after,
                bool nested,
                bool rejectCleanup,
                bool diagnostics
            )
            {
                _scenario = scenario;
                _fast = fast;
                _nested = nested;
                _calls = new int[count];
                Label =
                    $"[{scenario.Kind}] fast={fast},count={count},after={after},nested={nested},reject={rejectCleanup},diagnostics={diagnostics}";
                Bus = new ProbeBus(Inner, count, after, rejectCleanup);
                _handler = new MessageHandler(new InstanceId(991), Bus) { active = true };
                _token = MessageRegistrationToken.Create(_handler, Bus);
                _token.DiagnosticMode = diagnostics;
                MessagingDebug.enabled = false;
                MessagingDebug.LogFunction = null;
            }

            internal string Report =>
                Label
                + $": failure={Failure},disable={DisableFailure},recovery={RecoveryFailure},unregister={UnregisterFailure},final={FinalCleanupFailure}";

            internal void Run()
            {
                for (int index = 0; index < _calls.Length; ++index)
                {
                    Stage(index);
                }
                Setup = !_token.Enabled && _token._metadata.Count == _calls.Length;
                foreach (int count in Counters())
                {
                    Setup &= count == 0;
                }
                foreach (int count in Deliver())
                {
                    Setup &= count == 0;
                }
                Bus.Callback = () =>
                {
                    EnabledAtCallback = _token.Enabled;
                    if (_nested)
                    {
                        _token.Enable();
                    }
                    EnabledAfterCallback = _token.Enabled;
                };
                Failure = Capture(_token.Enable);
                EnabledAfterRollback = _token.Enabled;
                MetadataAfterRollback = _token._metadata.Count;
                RollbackDelivery = Deliver();
                DisableFailure = Capture(_token.Disable);
                EnabledAfterDisable = _token.Enabled;
                DisabledDelivery = Deliver();
                RecoveryFailure = Capture(_token.Enable);
                RecoveryDelivery = Deliver();
                UnregisterFailure = Capture(_token.UnregisterAll);
                EnabledAfterUnregister = _token.Enabled;
                TokenCleanupCounters = Counters();
                UnregisteredDelivery = Deliver();
                MetadataAfterUnregister = _token._metadata.Count;
                CallCountsAfterUnregister = _token._callCounts.Count;
                HistoryAfterUnregister = _token._emissionBuffer.Count;
            }

            private void Stage(int index)
            {
                Action<ProbeMessage> action = _ => ++_calls[index];
                MessageHandler.FastHandler<ProbeMessage> fast = (in ProbeMessage _) =>
                    ++_calls[index];
                switch (_scenario.Kind)
                {
                    case MessageKind.Untargeted:
                        if (_fast)
                        {
                            ScenarioHarness.RegisterUntargeted(_scenario, _token, fast);
                        }
                        else
                        {
                            _token.RegisterUntargeted(action);
                        }
                        break;
                    case MessageKind.Targeted:
                        if (_fast)
                        {
                            ScenarioHarness.RegisterTargeted(_scenario, _token, Context, fast);
                        }
                        else
                        {
                            _token.RegisterTargeted(Context, action);
                        }
                        break;
                    case MessageKind.Broadcast:
                        if (_fast)
                        {
                            ScenarioHarness.RegisterBroadcast(_scenario, _token, Context, fast);
                        }
                        else
                        {
                            _token.RegisterBroadcast(Context, action);
                        }
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(_scenario));
                }
            }

            private int[] Deliver()
            {
                Array.Clear(_calls, 0, _calls.Length);
                ProbeMessage message = default;
                InstanceId context = Context;
                switch (_scenario.Kind)
                {
                    case MessageKind.Untargeted:
                        ScenarioHarness.EmitUntargeted(_scenario, ref message, Inner);
                        break;
                    case MessageKind.Targeted:
                        ScenarioHarness.EmitTargeted(_scenario, ref message, context, Inner);
                        break;
                    case MessageKind.Broadcast:
                        ScenarioHarness.EmitBroadcast(_scenario, ref message, context, Inner);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(_scenario));
                }
                return (int[])_calls.Clone();
            }

            private int[] Counters() =>
                new[]
                {
                    Inner.RegisteredUntargeted,
                    Inner.RegisteredTargeted,
                    Inner.RegisteredBroadcast,
                    Inner.RegisteredInterceptors,
                    Inner.RegisteredPostProcessors,
                    Inner.RegisteredGlobalAcceptAll,
                };

            public void Dispose()
            {
                try
                {
                    Bus.Callback = null;
                    FinalCleanupFailure = Capture(_token.Dispose);
                    foreach (MessageBusRegistration handle in Bus.Handles)
                    {
                        Exception failure = Capture(() =>
                            Inner.Deregister<ProbeMessage>(in handle)
                        );
                        FinalCleanupFailure ??= failure;
                    }
                    _handler.active = false;
                    Inner.Trim(force: true);
                    FinalCounters = Counters();
                }
                finally
                {
                    MessagingDebug.enabled = _oldDebug;
                    MessagingDebug.LogFunction = _oldLog;
                    StaticsRestored =
                        MessagingDebug.enabled == _oldDebug
                        && ReferenceEquals(MessagingDebug.LogFunction, _oldLog);
                }
            }
        }

        private static Exception Capture(Action action)
        {
            try
            {
                action();
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }

        private sealed class ProbeBus : DelegatingMessageBus
        {
            private readonly int _failOn;
            private readonly bool _after,
                _rejectCleanup;
            private int _registrationAttempts;
            internal Action Callback;
            internal int Callbacks;
            internal readonly List<MessageBusRegistration> Handles =
                new List<MessageBusRegistration>();
            internal readonly Exception RegistrationMarker = new InvalidOperationException(
                "rollback-reentry-registration"
            );
            private readonly Exception _cleanupMarker = new InvalidOperationException(
                "rollback-reentry-cleanup"
            );

            internal ProbeBus(IMessageBus inner, int failOn, bool after, bool rejectCleanup)
                : base(inner)
            {
                _failOn = failOn;
                _after = after;
                _rejectCleanup = rejectCleanup;
            }

            private void BeforeRegistration()
            {
                if (++_registrationAttempts == _failOn)
                {
                    throw RegistrationMarker;
                }
            }

            private MessageBusRegistration Keep(MessageBusRegistration handle)
            {
                Handles.Add(handle);
                return handle;
            }

            public override MessageBusRegistration RegisterUntargeted<T>(
                MessageHandler handler,
                int priority = 0
            )
            {
                BeforeRegistration();
                return Keep(base.RegisterUntargeted<T>(handler, priority));
            }

            public override MessageBusRegistration RegisterTargeted<T>(
                InstanceId context,
                MessageHandler handler,
                int priority = 0
            )
            {
                BeforeRegistration();
                return Keep(base.RegisterTargeted<T>(context, handler, priority));
            }

            public override MessageBusRegistration RegisterSourcedBroadcast<T>(
                InstanceId context,
                MessageHandler handler,
                int priority = 0
            )
            {
                BeforeRegistration();
                return Keep(base.RegisterSourcedBroadcast<T>(context, handler, priority));
            }

            public override void Deregister<T>(in MessageBusRegistration registration)
            {
                Action callback = Callback;
                Callback = null;
                if (callback != null && !_after)
                {
                    ++Callbacks;
                    callback();
                }
                base.Deregister<T>(in registration);
                if (callback != null && _after)
                {
                    ++Callbacks;
                    callback();
                }
                if (callback != null && _rejectCleanup)
                {
                    throw _cleanupMarker;
                }
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
