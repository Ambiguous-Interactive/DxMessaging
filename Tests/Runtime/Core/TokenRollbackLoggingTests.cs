namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;
    using BusType = DxMessaging.Core.MessageBus.MessageBus;

    /// <summary>Diagnostic callbacks must see retryable rollback state before entering user code.</summary>
    /// <remarks>
    /// 2026-10-23: rollback logging ran before Enabled was updated. Throwing formatting or
    /// receiver callbacks left a disabled token with pending cleanup, and reentrant Enable
    /// activated staged callbacks before that cleanup completed. Registered counters measure
    /// priority buckets or delegate entries; callback delivery is asserted separately.
    /// </remarks>
    [TestFixture]
#if !UNITY_2021_3_OR_NEWER
    [NonParallelizable]
#endif
    public sealed class TokenRollbackLoggingTests
    {
        private static readonly InstanceId Context = new InstanceId(971);
        private static readonly int[] Families = { 0, 1, 2 };
        private static readonly bool[] Flags = { false, true };
        private static readonly LoggingMode[] Modes =
        {
            LoggingMode.Quiet,
            LoggingMode.ThrowReceiver,
            LoggingMode.ThrowFormatter,
            LoggingMode.ReenableReceiver,
            LoggingMode.NullReceiver,
            LoggingMode.ReenableFormatter,
        };

        public enum LoggingMode
        {
            Quiet,
            ThrowReceiver,
            ThrowFormatter,
            ReenableReceiver,
            NullReceiver,
            ReenableFormatter,
        }

        private static IEnumerable<TestCaseData> PendingCases()
        {
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            {
                foreach (int family in Families)
                {
                    foreach (bool debug in Flags)
                    {
                        foreach (LoggingMode mode in Modes)
                        {
                            yield return new TestCaseData(scenario, family, debug, mode).SetName(
                                "PendingRollback_"
                                    + scenario
                                    + "_family"
                                    + family
                                    + "_debug"
                                    + debug
                                    + "_"
                                    + mode
                            );
                        }
                    }
                }
            }
        }

        private static IEnumerable<TestCaseData> CleanupCases()
        {
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            {
                foreach (int family in Families)
                {
                    foreach (bool formatting in Flags)
                    {
                        foreach (bool clearAll in Flags)
                        {
                            yield return new TestCaseData(
                                scenario,
                                family,
                                formatting,
                                clearAll
                            ).SetName(
                                "LoggingCleanup_"
                                    + scenario
                                    + "_family"
                                    + family
                                    + "_format"
                                    + formatting
                                    + "_clear"
                                    + clearAll
                            );
                        }
                    }
                }
            }
        }

        [TestCaseSource(nameof(PendingCases))]
        public void RollbackLoggingPreservesPendingCleanup(
            MessageScenario scenario,
            int family,
            bool debug,
            LoggingMode mode
        )
        {
            using CaseState state = new CaseState(scenario, family, debug, mode);
            Exception failure = Assert.Catch<Exception>(
                state.Token.Enable,
                state.Label + ": replay must fail."
            );
            Assert.That(
                failure,
                Is.SameAs(state.ExpectedFailure),
                state.Label + ": preserve exception identity."
            );
            Assert.That(
                state.FormattingCalls,
                Is.EqualTo(state.ExpectsFormatting ? 1 : 0),
                state.Label + ": formatting gate."
            );
            Assert.That(
                state.ReceiverCalls,
                Is.EqualTo(state.ExpectsReceiver ? 1 : 0),
                state.Label + ": receiver gate."
            );
            if (state.ExpectsFormatting)
            {
                Assert.That(
                    state.EnabledAtFormatting,
                    Is.True,
                    state.Label + ": publish pending state before formatting."
                );
            }
            if (state.ExpectsReceiver)
            {
                Assert.That(
                    state.EnabledAtReceiver,
                    Is.True,
                    state.Label + ": publish pending state before receiver."
                );
            }
            Assert.That(
                state.Token.Enabled,
                Is.True,
                state.Label + ": pending cleanup must keep Enabled true."
            );
            Assert.That(
                state.Bus.RegistrationAttempts,
                Is.EqualTo(2),
                state.Label + ": diagnostic Enable must not replay staging."
            );
            Assert.That(
                state.Token._metadata.Count,
                Is.EqualTo(2),
                state.Label + ": both staged handles survive."
            );
            state.StopLogging();
            state.Bus.AllowRegistrations = true;
            state.Token.Enable();
            Assert.That(
                state.Bus.RegistrationAttempts,
                Is.EqualTo(2),
                state.Label + ": pending Enable remains a no-op."
            );
            state.Emit();
            Assert.That(
                state.FirstCalls,
                Is.EqualTo(1),
                state.Label + ": only the first registration was accepted."
            );
            Assert.That(
                state.SecondCalls,
                Is.Zero,
                state.Label + ": rejected staging must stay inactive until cleanup."
            );
            state.Bus.AllowDeregistrations = true;
            state.Token.Disable();
            Assert.That(
                state.Token.Enabled,
                Is.False,
                state.Label + ": recovered cleanup disables the token."
            );
            Assert.That(
                state.LiveCount,
                Is.Zero,
                state.Label + ": recovered cleanup drains the bus."
            );
            state.FirstCalls = 0;
            state.SecondCalls = 0;
            state.Token.Enable();
            state.Emit();
            Assert.That(
                state.FirstCalls,
                Is.EqualTo(1),
                state.Label + ": first staged callback replays once."
            );
            Assert.That(
                state.SecondCalls,
                Is.EqualTo(1),
                state.Label + ": second staged callback replays once."
            );
        }

        [TestCaseSource(nameof(CleanupCases))]
        public void LoggingCleanupCanResolvePendingState(
            MessageScenario scenario,
            int family,
            bool formatting,
            bool clearAll
        )
        {
            using CaseState state = new CaseState(scenario, family, true, LoggingMode.Quiet);
            Action cleanup = () =>
            {
                state.Bus.AllowDeregistrations = true;
                if (clearAll)
                {
                    state.Token.UnregisterAll();
                }
                else
                {
                    state.Token.Disable();
                }
            };
            if (formatting)
            {
                state.DuringFormatting = cleanup;
            }
            else
            {
                state.DuringReceiver = cleanup;
            }
            Exception failure = Assert.Catch<Exception>(
                state.Token.Enable,
                state.Label + ": replay must fail."
            );
            Assert.That(
                failure,
                Is.SameAs(state.Bus.RegistrationFailure),
                state.Label + ": preserve replay failure after callback cleanup."
            );
            Assert.That(
                state.Token.Enabled,
                Is.False,
                state.Label + ": callback cleanup resolves pending state."
            );
            Assert.That(
                state.LiveCount,
                Is.Zero,
                state.Label + ": callback cleanup drains the bus."
            );
            Assert.That(
                state.Token._metadata.Count,
                Is.EqualTo(clearAll ? 0 : 2),
                state.Label + ": preserve requested staging semantics."
            );
            state.StopLogging();
            state.Bus.AllowRegistrations = true;
            state.Token.Enable();
            state.Emit();
            Assert.That(
                state.FirstCalls,
                Is.EqualTo(clearAll ? 0 : 1),
                state.Label + ": first callback follows cleanup choice."
            );
            Assert.That(
                state.SecondCalls,
                Is.EqualTo(clearAll ? 0 : 1),
                state.Label + ": second callback follows cleanup choice."
            );
        }

        private sealed class CaseState : IDisposable
        {
            private readonly bool _savedDebug = MessagingDebug.enabled;
            private readonly Action<LogLevel, string> _savedLog = MessagingDebug.LogFunction;
            private readonly MessageScenario _scenario;
            private readonly LoggingMode _mode;
            private readonly bool _debug;
            private readonly MessageHandler _handler;
            private readonly LeakWatcher _leaks;
            internal readonly BusType Inner;
            internal readonly RejectingBus Bus;
            internal readonly MessageRegistrationToken Token;
            internal readonly string Label;
            internal readonly Exception ReceiverFailure = new InvalidOperationException(
                "Receiver marker."
            );
            internal readonly Exception FormatterFailure = new InvalidOperationException(
                "Formatter marker."
            );
            internal int FirstCalls;
            internal int SecondCalls;
            internal int FormattingCalls;
            internal int ReceiverCalls;
            internal bool? EnabledAtFormatting;
            internal bool? EnabledAtReceiver;
            internal Action DuringFormatting;
            internal Action DuringReceiver;

            internal CaseState(MessageScenario scenario, int family, bool debug, LoggingMode mode)
            {
                _scenario = scenario;
                _mode = mode;
                _debug = debug;
                Label =
                    "["
                    + scenario
                    + ", family="
                    + family
                    + ", debug="
                    + debug
                    + ", mode="
                    + mode
                    + "]";
                Inner = new BusType { DiagnosticsMode = false };
                Bus = new RejectingBus(Inner);
                _handler = new MessageHandler(new InstanceId(972), Bus) { active = true };
                Token = MessageRegistrationToken.Create(_handler, Bus);
                Token.DiagnosticMode = false;
                _leaks = LeakWatcher.WatchWithSlots(Inner, label: Label, handler: _handler);
                Register(Token, scenario, family, () => ++FirstCalls);
                Register(Token, scenario, family, () => ++SecondCalls);
                Bus.RollbackFailure = new RollbackException(() =>
                {
                    ++FormattingCalls;
                    EnabledAtFormatting = Token.Enabled;
                    DuringFormatting?.Invoke();
                    if (_mode == LoggingMode.ReenableFormatter)
                    {
                        Reenable();
                    }
                    if (_mode == LoggingMode.ThrowFormatter)
                    {
                        throw FormatterFailure;
                    }
                    return "Rollback marker.";
                });
                MessagingDebug.enabled = debug;
                MessagingDebug.LogFunction = mode == LoggingMode.NullReceiver ? null : ReceiveLog;
            }

            internal bool ExpectsFormatting => _debug && _mode != LoggingMode.NullReceiver;
            internal bool ExpectsReceiver =>
                ExpectsFormatting && _mode != LoggingMode.ThrowFormatter;
            internal Exception ExpectedFailure =>
                _debug && _mode == LoggingMode.ThrowReceiver ? ReceiverFailure
                : _debug && _mode == LoggingMode.ThrowFormatter ? FormatterFailure
                : Bus.RegistrationFailure;
            internal int LiveCount =>
                Inner.RegisteredUntargeted
                + Inner.RegisteredTargeted
                + Inner.RegisteredBroadcast
                + Inner.RegisteredInterceptors
                + Inner.RegisteredPostProcessors
                + Inner.RegisteredGlobalAcceptAll;

            private void ReceiveLog(LogLevel level, string message)
            {
                ++ReceiverCalls;
                EnabledAtReceiver = Token.Enabled;
                DuringReceiver?.Invoke();
                if (_mode == LoggingMode.ReenableReceiver)
                {
                    Reenable();
                }
                if (_mode == LoggingMode.ThrowReceiver)
                {
                    throw ReceiverFailure;
                }
            }

            private void Reenable()
            {
                Bus.AllowRegistrations = true;
                Token.Enable();
            }

            internal void StopLogging()
            {
                MessagingDebug.enabled = false;
                MessagingDebug.LogFunction = null;
            }

            internal void Emit()
            {
                ProbeMessage message = default;
                InstanceId context = Context;
                switch (_scenario.Kind)
                {
                    case MessageKind.Untargeted:
                        Inner.UntargetedBroadcast(ref message);
                        break;
                    case MessageKind.Targeted:
                        Inner.TargetedBroadcast(ref context, ref message);
                        break;
                    case MessageKind.Broadcast:
                        Inner.SourcedBroadcast(ref context, ref message);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(_scenario));
                }
            }

            public void Dispose()
            {
                try
                {
                    StopLogging();
                    Bus.AllowRegistrations = true;
                    Bus.AllowDeregistrations = true;
                    Token.Dispose();
                    _handler.active = false;
                    _ = Inner.Trim(force: true);
                    _leaks.Dispose();
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

        private sealed class RejectingBus : DelegatingMessageBus
        {
            internal readonly Exception RegistrationFailure = new InvalidOperationException(
                "Replay marker."
            );
            internal Exception RollbackFailure;
            internal int RegistrationAttempts;
            internal bool AllowRegistrations;
            internal bool AllowDeregistrations;

            internal RejectingBus(IMessageBus inner)
                : base(inner) { }

            private void BeforeRegistration()
            {
                ++RegistrationAttempts;
                if (!AllowRegistrations && RegistrationAttempts == 2)
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
                if (!AllowDeregistrations)
                {
                    throw RollbackFailure;
                }
                base.Deregister<T>(in registration);
            }
        }

        private sealed class RollbackException : Exception
        {
            private readonly Func<string> _format;

            internal RollbackException(Func<string> format)
            {
                _format = format;
            }

            public override string ToString() => _format();
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
