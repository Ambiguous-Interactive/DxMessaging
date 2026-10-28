namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;
    using BusType = DxMessaging.Core.MessageBus.MessageBus;

    /// <summary>Retarget recovery must preserve successful cleanup requested by diagnostic callbacks.</summary>
    /// <remarks>
    /// 2026-10-24: outer recovery replayed staging after a diagnostic callback successfully
    /// disabled the token. Natural rollback completion must still restore the previous bus.
    /// Callback deliveries, rather than registration buckets, establish routing ownership.
    /// </remarks>
    [TestFixture]
#if !UNITY_2021_3_OR_NEWER
    [NonParallelizable]
#endif
    public sealed class TokenRetargetLoggingCleanupTests
    {
        private static readonly InstanceId Context = new InstanceId(981);

        public enum CleanupAction
        {
            None,
            Disable,
            Clear,
        }

        private static IEnumerable<TestCaseData> CleanupCases()
        {
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            {
                foreach (int family in new[] { 0, 1, 2 })
                {
                    foreach (bool oldTeardown in new[] { false, true })
                    {
                        foreach (
                            CleanupAction cleanup in new[]
                            {
                                CleanupAction.None,
                                CleanupAction.Disable,
                                CleanupAction.Clear,
                            }
                        )
                        {
                            foreach (bool formatter in new[] { false, true })
                            {
                                foreach (bool throws in new[] { false, true })
                                {
                                    yield return new TestCaseData(
                                        scenario,
                                        family,
                                        oldTeardown,
                                        cleanup,
                                        formatter,
                                        throws
                                    ).SetName(
                                        "RetargetLogging_"
                                            + scenario
                                            + "_family"
                                            + family
                                            + "_old"
                                            + oldTeardown
                                            + "_"
                                            + cleanup
                                            + "_format"
                                            + formatter
                                            + "_throw"
                                            + throws
                                    );
                                }
                            }
                        }
                    }
                }
            }
        }

        private static IEnumerable<TestCaseData> NaturalRollbackCases()
        {
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            {
                foreach (int family in new[] { 0, 1, 2 })
                {
                    foreach (bool debug in new[] { false, true })
                    {
                        yield return new TestCaseData(scenario, family, debug).SetName(
                            "RetargetNaturalRollback_"
                                + scenario
                                + "_family"
                                + family
                                + "_debug"
                                + debug
                        );
                    }
                }
            }
        }

        [TestCaseSource(nameof(CleanupCases))]
        public void RetargetRecoveryPreservesDiagnosticCleanup(
            MessageScenario scenario,
            int family,
            bool oldTeardown,
            CleanupAction cleanup,
            bool formatter,
            bool throws
        )
        {
            using CaseState state = new CaseState(
                scenario,
                family,
                oldTeardown,
                cleanup,
                formatter,
                throws
            );
            state.Prepare(failedRollback: true, debug: true);
            Exception failure = Assert.Catch<Exception>(
                state.Retarget,
                state.Label + ": retarget must fail."
            );
            state.StopLogging();
            int[] oldDelivery = state.Deliver(state.OldInner);
            int[] newDelivery = state.Deliver(state.NewInner);
            bool pending = cleanup == CleanupAction.None;
            Assert.That(
                failure,
                Is.SameAs(state.ExpectedFailure),
                state.Label + ": exception identity."
            );
            Assert.That(
                state.FormattingCalls,
                Is.EqualTo(1),
                state.Label + ": one formatting call."
            );
            Assert.That(
                state.ReceiverCalls,
                Is.EqualTo(formatter && throws ? 0 : 1),
                state.Label + ": receiver gate."
            );
            Assert.That(
                state.BoundaryCalls,
                Is.EqualTo(1),
                state.Label + ": one selected callback boundary."
            );
            Assert.That(
                state.EnabledBeforeCleanup,
                Is.True,
                state.Label + ": callback sees pending cleanup."
            );
            Assert.That(
                state.EnabledAfterCleanup,
                Is.EqualTo(pending),
                state.Label + ": requested cleanup completes."
            );
            Assert.That(
                state.Token.Enabled,
                Is.EqualTo(pending),
                state.Label + ": outer recovery preserves cleanup."
            );
            Assert.That(
                state.Token._metadata.Count,
                Is.EqualTo(cleanup == CleanupAction.Clear ? 0 : 2),
                state.Label + ": staging choice."
            );
            Assert.That(
                oldDelivery,
                Is.EqualTo(pending ? new[] { 0, 1 } : new[] { 0, 0 }),
                state.Label + ": old-bus callback ownership."
            );
            Assert.That(
                newDelivery,
                Is.EqualTo(pending && !oldTeardown ? new[] { 1, 0 } : new[] { 0, 0 }),
                state.Label + ": new-bus callback ownership."
            );
            if (pending)
            {
                int oldAttempts = state.OldBus.RegistrationAttempts;
                int newAttempts = state.NewBus.RegistrationAttempts;
                state.Token.Enable();
                Assert.That(
                    state.OldBus.RegistrationAttempts,
                    Is.EqualTo(oldAttempts),
                    state.Label + ": pending Enable does not replay old staging."
                );
                Assert.That(
                    state.NewBus.RegistrationAttempts,
                    Is.EqualTo(newAttempts),
                    state.Label + ": pending Enable does not replay new staging."
                );
            }
            state.VerifyLaterRecovery();
        }

        [TestCaseSource(nameof(NaturalRollbackCases))]
        public void NaturalRollbackCompletionStillRestoresOldBus(
            MessageScenario scenario,
            int family,
            bool debug
        )
        {
            using CaseState state = new CaseState(
                scenario,
                family,
                false,
                CleanupAction.None,
                false,
                false
            );
            state.Prepare(failedRollback: false, debug);
            Exception failure = Assert.Catch<Exception>(
                state.Retarget,
                state.Label + ": replay must fail."
            );
            state.StopLogging();
            Assert.That(
                failure,
                Is.SameAs(state.NewBus.RegistrationFailure),
                state.Label + ": preserve replay exception."
            );
            Assert.That(
                state.FormattingCalls,
                Is.Zero,
                state.Label + ": successful rollback does not log."
            );
            Assert.That(
                state.ReceiverCalls,
                Is.Zero,
                state.Label + ": successful rollback has no receiver."
            );
            Assert.That(
                state.Token.Enabled,
                Is.True,
                state.Label + ": automatic recovery remains active."
            );
            Assert.That(
                state.Deliver(state.OldInner),
                Is.EqualTo(new[] { 1, 1 }),
                state.Label + ": both old callbacks restored."
            );
            Assert.That(
                state.Deliver(state.NewInner),
                Is.EqualTo(new[] { 0, 0 }),
                state.Label + ": new bus fully rolled back."
            );
            state.VerifyLaterRecovery();
        }

        private sealed class CaseState : IDisposable
        {
            private readonly bool _savedDebug = MessagingDebug.enabled;
            private readonly Action<LogLevel, string> _savedLog = MessagingDebug.LogFunction;
            private readonly MessageScenario _scenario;
            private readonly bool _oldTeardown;
            private readonly CleanupAction _cleanup;
            private readonly bool _formatter;
            private readonly bool _throws;
            private readonly MessageHandler _handler;
            private readonly LeakWatcher _oldLeaks;
            private readonly LeakWatcher _newLeaks;
            private readonly Exception _logFailure = new InvalidOperationException(
                "Diagnostic marker."
            );
            private int _firstCalls;
            private int _secondCalls;
            internal readonly BusType OldInner = new BusType { DiagnosticsMode = false };
            internal readonly BusType NewInner = new BusType { DiagnosticsMode = false };
            internal readonly FaultBus OldBus;
            internal readonly FaultBus NewBus;
            internal readonly MessageRegistrationToken Token;
            internal readonly string Label;
            internal int FormattingCalls;
            internal int ReceiverCalls;
            internal int BoundaryCalls;
            internal bool EnabledBeforeCleanup;
            internal bool EnabledAfterCleanup;

            internal CaseState(
                MessageScenario scenario,
                int family,
                bool oldTeardown,
                CleanupAction cleanup,
                bool formatter,
                bool throws
            )
            {
                _scenario = scenario;
                _oldTeardown = oldTeardown;
                _cleanup = cleanup;
                _formatter = formatter;
                _throws = throws;
                Label =
                    "["
                    + scenario
                    + ", family="
                    + family
                    + ", old="
                    + oldTeardown
                    + ", cleanup="
                    + cleanup
                    + ", formatter="
                    + formatter
                    + ", throws="
                    + throws
                    + "]";
                MessagingDebug.enabled = false;
                MessagingDebug.LogFunction = null;
                OldBus = new FaultBus(OldInner);
                NewBus = new FaultBus(NewInner);
                _handler = new MessageHandler(new InstanceId(982), OldBus) { active = true };
                Token = MessageRegistrationToken.Create(_handler, OldBus);
                Token.DiagnosticMode = false;
                _oldLeaks = LeakWatcher.WatchWithSlots(
                    OldInner,
                    label: Label + " old",
                    handler: _handler
                );
                _newLeaks = LeakWatcher.WatchWithSlots(
                    NewInner,
                    label: Label + " new",
                    handler: _handler
                );
                Register(Token, scenario, family, () => ++_firstCalls);
                Register(Token, scenario, family, () => ++_secondCalls);
                OldBus.RegistrationFailure = new FormattingException(FormatLog);
                OldBus.DeregistrationFailure = new InvalidOperationException(
                    "Old teardown marker."
                );
                NewBus.RegistrationFailure = new InvalidOperationException("New replay marker.");
                NewBus.DeregistrationFailure = new FormattingException(FormatLog);
            }

            internal Exception ExpectedFailure =>
                _throws ? _logFailure
                : _oldTeardown ? OldBus.DeregistrationFailure
                : NewBus.RegistrationFailure;

            internal void Prepare(bool failedRollback, bool debug)
            {
                Token.Enable();
                Assert.That(
                    Deliver(OldInner),
                    Is.EqualTo(new[] { 1, 1 }),
                    Label + ": initial old-bus setup."
                );
                Assert.That(
                    Deliver(NewInner),
                    Is.EqualTo(new[] { 0, 0 }),
                    Label + ": initial new-bus setup."
                );
                Assert.That(
                    OldBus.RegistrationAttempts,
                    Is.EqualTo(2),
                    Label + ": two initial registrations."
                );
                if (_oldTeardown)
                {
                    OldBus.AllowDeregistrations = false;
                    OldBus.FailDeregistrationAt = 2;
                    OldBus.AllowRegistrations = false;
                    OldBus.FailRegistrationAt = 3;
                }
                else
                {
                    NewBus.AllowRegistrations = false;
                    NewBus.FailRegistrationAt = 2;
                    NewBus.AllowDeregistrations = !failedRollback;
                    NewBus.FailDeregistrationAt = 1;
                }
                MessagingDebug.enabled = debug;
                MessagingDebug.LogFunction = ReceiveLog;
            }

            internal void Retarget() =>
                Token.RetargetMessageBus(NewBus, MessageBusRebindMode.RebindActive);

            private string FormatLog()
            {
                ++FormattingCalls;
                if (_formatter)
                {
                    ApplyCleanup();
                }
                return "Retarget recovery marker.";
            }

            private void ReceiveLog(LogLevel level, string message)
            {
                ++ReceiverCalls;
                if (!_formatter)
                {
                    ApplyCleanup();
                }
            }

            private void ApplyCleanup()
            {
                ++BoundaryCalls;
                EnabledBeforeCleanup = Token.Enabled;
                if (_cleanup != CleanupAction.None)
                {
                    OldBus.AllowDeregistrations = true;
                    NewBus.AllowDeregistrations = true;
                    if (_cleanup == CleanupAction.Disable)
                    {
                        Token.Disable();
                    }
                    else
                    {
                        Token.UnregisterAll();
                    }
                }
                EnabledAfterCleanup = Token.Enabled;
                if (_throws)
                {
                    throw _logFailure;
                }
            }

            internal int[] Deliver(BusType bus)
            {
                _firstCalls = 0;
                _secondCalls = 0;
                ProbeMessage message = default;
                InstanceId context = Context;
                switch (_scenario.Kind)
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
                        throw new ArgumentOutOfRangeException(nameof(_scenario));
                }
                return new[] { _firstCalls, _secondCalls };
            }

            internal void StopLogging()
            {
                MessagingDebug.enabled = false;
                MessagingDebug.LogFunction = null;
            }

            private void StopFaults()
            {
                StopLogging();
                OldBus.AllowRegistrations = true;
                NewBus.AllowRegistrations = true;
                OldBus.AllowDeregistrations = true;
                NewBus.AllowDeregistrations = true;
            }

            internal void VerifyLaterRecovery()
            {
                StopFaults();
                Token.Disable();
                Assert.That(
                    Token.Enabled,
                    Is.False,
                    Label + ": later Disable drains pending cleanup."
                );
                Assert.That(
                    _oldLeaks.LeakedRegistrations,
                    Is.Zero,
                    Label + ": old registrations drained."
                );
                Assert.That(
                    _newLeaks.LeakedRegistrations,
                    Is.Zero,
                    Label + ": new registrations drained."
                );
                Token.Enable();
                int expected = _cleanup == CleanupAction.Clear ? 0 : 1;
                Assert.That(
                    Deliver(OldInner),
                    Is.EqualTo(new[] { expected, expected }),
                    Label + ": later Enable follows staging choice."
                );
                Assert.That(
                    Deliver(NewInner),
                    Is.EqualTo(new[] { 0, 0 }),
                    Label + ": later Enable retains previous bus ownership."
                );
            }

            public void Dispose()
            {
                try
                {
                    StopFaults();
                    Token.Dispose();
                    _handler.active = false;
                    _ = OldInner.Trim(force: true);
                    _ = NewInner.Trim(force: true);
                    Assert.That(
                        Token._metadata.Count,
                        Is.Zero,
                        Label + ": final metadata cleared."
                    );
                    try
                    {
                        _oldLeaks.Dispose();
                    }
                    finally
                    {
                        _newLeaks.Dispose();
                    }
                }
                finally
                {
                    MessagingDebug.enabled = _savedDebug;
                    MessagingDebug.LogFunction = _savedLog;
                }
            }
        }

        private static void Register(
            MessageRegistrationToken token,
            MessageScenario scenario,
            int family,
            Action record
        )
        {
            if (family == 2)
            {
                _ = token.RegisterGlobalAcceptAll(
                    (in IUntargetedMessage message) => record(),
                    (in InstanceId context, in ITargetedMessage message) => record(),
                    (in InstanceId context, in IBroadcastMessage message) => record()
                );
                return;
            }
            if (family == 1)
            {
                switch (scenario.Kind)
                {
                    case MessageKind.Untargeted:
                        _ = token.RegisterUntargetedInterceptor<ProbeMessage>(
                            (ref ProbeMessage message) =>
                            {
                                record();
                                return true;
                            }
                        );
                        break;
                    case MessageKind.Targeted:
                        _ = token.RegisterTargetedInterceptor<ProbeMessage>(
                            (ref InstanceId context, ref ProbeMessage message) =>
                            {
                                record();
                                return true;
                            }
                        );
                        break;
                    case MessageKind.Broadcast:
                        _ = token.RegisterBroadcastInterceptor<ProbeMessage>(
                            (ref InstanceId context, ref ProbeMessage message) =>
                            {
                                record();
                                return true;
                            }
                        );
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(scenario));
                }
                return;
            }
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                    _ = token.RegisterUntargeted<ProbeMessage>(
                        (in ProbeMessage message) => record()
                    );
                    break;
                case MessageKind.Targeted:
                    _ = token.RegisterTargeted<ProbeMessage>(
                        Context,
                        (in ProbeMessage message) => record()
                    );
                    break;
                case MessageKind.Broadcast:
                    _ = token.RegisterBroadcast<ProbeMessage>(
                        Context,
                        (in ProbeMessage message) => record()
                    );
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario));
            }
        }

        private sealed class FaultBus : DelegatingMessageBus
        {
            internal Exception RegistrationFailure = new InvalidOperationException(
                "Replay marker."
            );
            internal Exception DeregistrationFailure;
            internal int RegistrationAttempts;
            internal bool AllowRegistrations = true;
            internal int FailRegistrationAt = int.MaxValue;
            internal int FailDeregistrationAt = int.MaxValue;
            internal int DeregistrationAttempts;
            internal bool AllowDeregistrations = true;

            internal FaultBus(IMessageBus inner)
                : base(inner) { }

            private void BeforeRegistration()
            {
                ++RegistrationAttempts;
                if (!AllowRegistrations && FailRegistrationAt <= RegistrationAttempts)
                {
                    throw RegistrationFailure;
                }
            }

            public override MessageBusRegistration RegisterUntargeted<T>(
                MessageHandler handler,
                int priority = 0
            )
            {
                BeforeRegistration();
                return base.RegisterUntargeted<T>(handler, priority);
            }

            public override MessageBusRegistration RegisterTargeted<T>(
                InstanceId target,
                MessageHandler handler,
                int priority = 0
            )
            {
                BeforeRegistration();
                return base.RegisterTargeted<T>(target, handler, priority);
            }

            public override MessageBusRegistration RegisterSourcedBroadcast<T>(
                InstanceId source,
                MessageHandler handler,
                int priority = 0
            )
            {
                BeforeRegistration();
                return base.RegisterSourcedBroadcast<T>(source, handler, priority);
            }

            public override MessageBusRegistration RegisterGlobalAcceptAll(MessageHandler handler)
            {
                BeforeRegistration();
                return base.RegisterGlobalAcceptAll(handler);
            }

            public override MessageBusRegistration RegisterUntargetedInterceptor<T>(
                IMessageBus.UntargetedInterceptor<T> interceptor,
                int priority = 0
            )
            {
                BeforeRegistration();
                return base.RegisterUntargetedInterceptor(interceptor, priority);
            }

            public override MessageBusRegistration RegisterTargetedInterceptor<T>(
                IMessageBus.TargetedInterceptor<T> interceptor,
                int priority = 0
            )
            {
                BeforeRegistration();
                return base.RegisterTargetedInterceptor(interceptor, priority);
            }

            public override MessageBusRegistration RegisterBroadcastInterceptor<T>(
                IMessageBus.BroadcastInterceptor<T> interceptor,
                int priority = 0
            )
            {
                BeforeRegistration();
                return base.RegisterBroadcastInterceptor(interceptor, priority);
            }

            public override void Deregister<T>(in MessageBusRegistration registration)
            {
                ++DeregistrationAttempts;
                if (!AllowDeregistrations && FailDeregistrationAt <= DeregistrationAttempts)
                {
                    throw DeregistrationFailure;
                }
                base.Deregister<T>(in registration);
            }
        }

        public sealed class FormattingException : Exception
        {
            private readonly Func<string> _format;

            public FormattingException() { }

            public FormattingException(string message)
                : base(message) { }

            public FormattingException(string message, Exception innerException)
                : base(message, innerException) { }

            internal FormattingException(Func<string> format)
            {
                _format = format;
            }

            public override string ToString() => _format == null ? base.ToString() : _format();
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
