namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;
    using BusType = DxMessaging.Core.MessageBus.MessageBus;

    /// <summary>Retains build-failure cleanup ownership across diagnostic formatting and receivers.</summary>
    /// <remarks>2026-10-25: Cleanup diagnostics could accept work after the builder's last ownership check.</remarks>
    [TestFixture]
#if !UNITY_2021_3_OR_NEWER
    [NonParallelizable]
#endif
    public sealed class BuilderDiagnosticOwnershipTests
    {
        private static readonly InstanceId Context = new InstanceId(965);

        private static IEnumerable<TestCaseData> DiagnosticCases()
        {
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            foreach (int carrier in new[] { 0, 1, 2, 3, 4, 5 })
            foreach (string phase in new[] { "Configure", "OnBuild", "OnActivate" })
            foreach (string mode in new[] { "Off", "NullReceiver", "Receiver" })
            foreach (string boundary in new[] { "Format", "Receiver" })
            foreach (string action in new[] { "None", "Restage", "Throw", "RestageThrow" })
            {
                yield return new TestCaseData(
                    scenario,
                    carrier,
                    phase,
                    mode,
                    boundary,
                    action,
                    false
                ).SetName(
                    $"BuilderDiagnosticOwnership_{scenario}_carrier{carrier}_{phase}_{mode}_{boundary}_{action}"
                );
            }
        }

        private static IEnumerable<TestCaseData> StagedOnlyCases()
        {
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            foreach (int carrier in new[] { 0, 1, 2, 3, 4, 5 })
            foreach (string phase in new[] { "Configure", "OnBuild", "OnActivate" })
            foreach (string mode in new[] { "Off", "NullReceiver", "Receiver" })
            foreach (string boundary in new[] { "Format", "Receiver" })
            foreach (string action in new[] { "Stage", "StageThrow" })
            {
                yield return new TestCaseData(
                    scenario,
                    carrier,
                    phase,
                    mode,
                    boundary,
                    action,
                    false
                ).SetName(
                    $"BuilderDiagnosticStaging_{scenario}_carrier{carrier}_{phase}_{mode}_{boundary}_{action}"
                );
            }
        }

        private static IEnumerable<TestCaseData> RecoveryCases()
        {
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            foreach (int carrier in new[] { 0, 1, 2, 3, 4, 5 })
            foreach (string phase in new[] { "Configure", "OnBuild", "OnActivate" })
            foreach (string boundary in new[] { "Format", "Receiver" })
            foreach (string action in new[] { "Restage", "RestageThrow" })
            {
                yield return new TestCaseData(
                    scenario,
                    carrier,
                    phase,
                    "Receiver",
                    boundary,
                    action,
                    true
                ).SetName(
                    $"BuilderDiagnosticRecovery_{scenario}_carrier{carrier}_{phase}_{boundary}_{action}"
                );
            }
        }

        /// <summary>Cleans diagnostic-accepted work or returns its retryable owning lease.</summary>
        [TestCaseSource(nameof(DiagnosticCases))]
        [TestCaseSource(nameof(RecoveryCases))]
        [TestCaseSource(nameof(StagedOnlyCases))]
        public void FailedBuildPreservesOwnershipAfterCleanupDiagnostics(
            MessageScenario scenario,
            int carrier,
            string phase,
            string mode,
            string boundary,
            string action,
            bool rejectCleanup
        )
        {
            string label =
                $"{scenario},carrier={carrier},{phase},{mode},{boundary},{action},reject={rejectCleanup}";
            using Scope scope = new Scope(
                scenario,
                carrier,
                phase,
                mode,
                boundary,
                action,
                rejectCleanup,
                label
            );
            scope.Run();
            bool diagnosticsRuns = mode == "Receiver";
            bool diagnosticThrows =
                diagnosticsRuns && action.EndsWith("Throw", StringComparison.Ordinal);
            Assert.That(
                scope.Setup,
                Is.True,
                label + ": setup enables exactly one original registration"
            );
            Assert.That(
                scope.SelectedHooks,
                Is.EqualTo(1),
                label + ": selected setup hook runs once"
            );
            Assert.That(
                scope.DisposalHooks,
                Is.EqualTo(1),
                label + ": original cleanup invokes disposal once"
            );
            Assert.That(
                scope.Formats,
                Is.EqualTo(diagnosticsRuns ? 1 : 0),
                label + ": formatting respects debug and receiver gates"
            );
            Assert.That(
                scope.Actions,
                Is.EqualTo(diagnosticsRuns ? 1 : 0),
                label + ": diagnostic action is one-shot"
            );
            int receivers = diagnosticsRuns && !(boundary == "Format" && diagnosticThrows) ? 1 : 0;
            Assert.That(
                scope.Receivers,
                Is.EqualTo(receivers),
                label + ": formatting failures precede the receiver"
            );
            Assert.That(scope.OtherLogs, Is.Zero, label + ": no undeclared logging boundary ran");
            Assert.That(
                scope.ReturnedLease,
                Is.Null,
                label + ": a failed build returns no ordinary lease"
            );
            if (rejectCleanup)
            {
                Assert.That(
                    scope.Failure,
                    Is.TypeOf<MessageRegistrationBuildException>(),
                    label + ": rejected late cleanup transfers ownership"
                );
                MessageRegistrationBuildException failure = (MessageRegistrationBuildException)
                    scope.Failure;
                scope.RecoveryLease = failure.Lease;
                Assert.That(
                    scope.RecoveryLease,
                    Is.Not.Null,
                    label + ": the recovery lease is reachable"
                );
                Assert.That(
                    failure.ActivationException,
                    Is.SameAs(scope.BuildMarker),
                    label + ": original setup failure identity is retained"
                );
                Assert.That(
                    failure.InnerException,
                    Is.SameAs(scope.BuildMarker),
                    label + ": inner exception preserves the setup failure"
                );
                Assert.That(
                    failure.CleanupException,
                    Is.SameAs(scope.CleanupMarker),
                    label + ": first cleanup exception wins"
                );
                Assert.That(
                    scope.RecoveryLease.Token,
                    Is.SameAs(scope.Token),
                    label + ": recovery owns the accepted token"
                );
                Assert.That(
                    scope.RecoveryLease.IsActive,
                    Is.True,
                    label + ": pending cleanup remains visible"
                );
                Assert.That(
                    scope.Token.Enabled,
                    Is.True,
                    label + ": rejected cleanup remains retryable"
                );
                Assert.That(
                    Total(scope.Inner),
                    Is.EqualTo(1),
                    label + ": exactly the replacement remains owned"
                );
            }
            else
            {
                Assert.That(
                    scope.Failure,
                    Is.SameAs(diagnosticThrows ? scope.DiagnosticMarker : scope.BuildMarker),
                    label + ": completed cleanup preserves existing failure identity"
                );
                Assert.That(
                    scope.Token.Enabled,
                    Is.False,
                    label + ": automatic cleanup disables all accepted work"
                );
                Assert.That(
                    scope.Token._metadata,
                    Is.Empty,
                    label + ": automatic cleanup releases staging"
                );
                Assert.That(
                    Total(scope.Inner),
                    Is.Zero,
                    label + ": no registration escapes the thrown build"
                );
            }
            Emit(scope.Inner, scenario);
            Assert.That(
                scope.OriginalCalls,
                Is.Zero,
                label + ": original callback remains retired"
            );
            Assert.That(
                scope.ReplacementCalls,
                Is.EqualTo(rejectCleanup ? 1 : 0),
                label + ": only reachable retryable ownership can deliver"
            );
            scope.Bus.RejectCleanup = false;
            scope.RecoveryLease?.Dispose();
            scope.RecoveryLease?.Dispose();
            Assert.That(
                Total(scope.Inner),
                Is.Zero,
                label + ": recovery lease consumes late accepted work"
            );
            scope.Token.Dispose();
            Assert.That(scope.Token.Enabled, Is.False, label + ": final token state is inactive");
            Assert.That(scope.Token._metadata, Is.Empty, label + ": final staging is empty");
            Assert.That(
                scope.DisposalHooks,
                Is.EqualTo(1),
                label + ": retries do not repeat disposal notification"
            );
            int replacements = scope.ReplacementCalls;
            Emit(scope.Inner, scenario);
            Assert.That(
                scope.ReplacementCalls,
                Is.EqualTo(replacements),
                label + ": retired replacement never runs again"
            );
            int recoveredCalls = 0;
            using (
                MessageRegistrationLease recovered = scope.Builder.Build(
                    new MessageRegistrationBuildOptions
                    {
                        Owner = new InstanceId(967),
                        ActivateOnBuild = true,
                        Configure = token =>
                            Register(token, scenario, carrier, () => ++recoveredCalls),
                    }
                )
            )
            {
                Emit(scope.Inner, scenario);
                Assert.That(
                    recoveredCalls,
                    Is.EqualTo(1),
                    label + ": independent recovery lease dispatches once"
                );
            }
            Assert.That(
                Total(scope.Inner),
                Is.Zero,
                label + ": independent recovery cleanup is valid"
            );
        }

        private static MessageRegistrationHandle Register(
            MessageRegistrationToken token,
            MessageScenario scenario,
            int carrier,
            Action callback
        )
        {
            Action<ProbeMessage> action = _ => callback();
            MessageHandler.FastHandler<ProbeMessage> fast = (in ProbeMessage _) => callback();
            if (carrier < 2)
            {
                return scenario.Kind switch
                {
                    MessageKind.Untargeted => carrier == 0
                        ? token.RegisterUntargeted(action)
                        : ScenarioHarness.RegisterUntargeted(scenario, token, fast),
                    MessageKind.Targeted => carrier == 0
                        ? token.RegisterTargeted(Context, action)
                        : ScenarioHarness.RegisterTargeted(scenario, token, Context, fast),
                    MessageKind.Broadcast => carrier == 0
                        ? token.RegisterBroadcast(Context, action)
                        : ScenarioHarness.RegisterBroadcast(scenario, token, Context, fast),
                    _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
                };
            }
            if (carrier == 2)
            {
                return scenario.Kind switch
                {
                    MessageKind.Untargeted => ScenarioHarness.RegisterUntargetedPostProcessor(
                        scenario,
                        token,
                        fast
                    ),
                    MessageKind.Targeted => ScenarioHarness.RegisterTargetedPostProcessor(
                        scenario,
                        token,
                        Context,
                        fast
                    ),
                    MessageKind.Broadcast => ScenarioHarness.RegisterBroadcastPostProcessor(
                        scenario,
                        token,
                        Context,
                        fast
                    ),
                    _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
                };
            }
            if (carrier == 3)
            {
                bool Scalar(ref ProbeMessage _)
                {
                    callback();
                    return true;
                }
                bool Contextual(ref InstanceId _, ref ProbeMessage __)
                {
                    callback();
                    return true;
                }
                return scenario.Kind switch
                {
                    MessageKind.Untargeted => token.RegisterUntargetedInterceptor<ProbeMessage>(
                        Scalar
                    ),
                    MessageKind.Targeted => token.RegisterTargetedInterceptor<ProbeMessage>(
                        Contextual
                    ),
                    MessageKind.Broadcast => token.RegisterBroadcastInterceptor<ProbeMessage>(
                        Contextual
                    ),
                    _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
                };
            }
            if (carrier == 4)
            {
                return token.RegisterGlobalAcceptAll(
                    (IUntargetedMessage _) => callback(),
                    (InstanceId _, ITargetedMessage __) => callback(),
                    (InstanceId _, IBroadcastMessage __) => callback()
                );
            }
            void Untargeted(in IUntargetedMessage _)
            {
                callback();
            }
            void Targeted(in InstanceId _, in ITargetedMessage __)
            {
                callback();
            }
            void Broadcast(in InstanceId _, in IBroadcastMessage __)
            {
                callback();
            }
            return token.RegisterGlobalAcceptAll(Untargeted, Targeted, Broadcast);
        }

        private static void Emit(BusType bus, MessageScenario scenario)
        {
            ProbeMessage message = default;
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                    ScenarioHarness.EmitUntargeted(scenario, ref message, bus);
                    break;
                case MessageKind.Targeted:
                    ScenarioHarness.EmitTargeted(scenario, ref message, Context, bus);
                    break;
                case MessageKind.Broadcast:
                    ScenarioHarness.EmitBroadcast(scenario, ref message, Context, bus);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario));
            }
        }

        private static int Total(BusType bus) =>
            bus.RegisteredUntargeted
            + bus.RegisteredTargeted
            + bus.RegisteredBroadcast
            + bus.RegisteredPostProcessors
            + bus.RegisteredInterceptors
            + bus.RegisteredGlobalAcceptAll;

        private readonly struct ProbeMessage
            : IUntargetedMessage<ProbeMessage>,
                ITargetedMessage<ProbeMessage>,
                IBroadcastMessage<ProbeMessage>
        {
            public Type MessageType => typeof(ProbeMessage);
        }

        /// <summary>Runs an optional callback when cleanup diagnostics format this test exception.</summary>
        public sealed class FormattingException : Exception
        {
            /// <summary>Creates the diagnostic test exception.</summary>
            public FormattingException() { }

            /// <summary>Creates the diagnostic test exception with a message.</summary>
            /// <param name="message">Diagnostic failure message.</param>
            public FormattingException(string message)
                : base(message) { }

            /// <summary>Creates the diagnostic test exception with an underlying failure.</summary>
            /// <param name="message">Diagnostic failure message.</param>
            /// <param name="innerException">Underlying diagnostic failure.</param>
            public FormattingException(string message, Exception innerException)
                : base(message, innerException) { }

            internal Action OnFormat;

            /// <summary>Formats the diagnostic marker and invokes its optional callback.</summary>
            /// <returns>Fixed marker text after the callback completes.</returns>
            public override string ToString()
            {
                OnFormat?.Invoke();
                return "builder cleanup diagnostic marker";
            }
        }

        private sealed class RejectingBus : DelegatingMessageBus
        {
            internal bool RejectCleanup;
            internal readonly Exception Marker = new InvalidOperationException(
                "late cleanup rejection"
            );

            internal RejectingBus(BusType inner)
                : base(inner) { }

            public override void Deregister<T>(in MessageBusRegistration registration)
            {
                if (RejectCleanup)
                    throw Marker;
                base.Deregister<T>(in registration);
            }
        }

        private sealed class Scope : IDisposable
        {
            internal readonly BusType Inner = new BusType();
            internal readonly RejectingBus Bus;
            internal readonly MessageRegistrationBuilder Builder;
            internal readonly Exception BuildMarker = new InvalidOperationException(
                "build setup marker"
            );
            internal readonly Exception DiagnosticMarker = new InvalidOperationException(
                "diagnostic marker"
            );
            internal readonly FormattingException CleanupMarker = new FormattingException();
            internal MessageRegistrationToken Token;
            internal MessageRegistrationLease ReturnedLease;
            internal MessageRegistrationLease RecoveryLease;
            internal Exception Failure;
            internal bool Setup;
            internal int SelectedHooks,
                DisposalHooks,
                Formats,
                Receivers,
                Actions,
                OtherLogs,
                OriginalCalls,
                ReplacementCalls;
            private readonly MessageScenario _scenario;
            private readonly int _carrier;
            private readonly string _phase,
                _mode,
                _boundary,
                _action;
            private readonly bool _rejectCleanup;
            private readonly DiagnosticsScope _diagnostics;
            private readonly LeakWatcher _watcher;
            private readonly bool _savedDebug = MessagingDebug.enabled;
            private readonly Action<LogLevel, string> _savedLogger = MessagingDebug.LogFunction;
            private bool _actionConsumed;

            internal Scope(
                MessageScenario scenario,
                int carrier,
                string phase,
                string mode,
                string boundary,
                string action,
                bool rejectCleanup,
                string label
            )
            {
                _scenario = scenario;
                _carrier = carrier;
                _phase = phase;
                _mode = mode;
                _boundary = boundary;
                _action = action;
                _rejectCleanup = rejectCleanup;
                _diagnostics = new DiagnosticsScope(DiagnosticsTarget.Off, 0, false);
                Bus = new RejectingBus(Inner);
                Builder = new MessageRegistrationBuilder(new FixedMessageBusProvider(Bus));
                _watcher = new LeakWatcher(Inner, label: label);
            }

            private void Selected(MessageRegistrationToken token)
            {
                ++SelectedHooks;
                token.Enable();
                Setup = token.Enabled && Total(Inner) == 1;
                throw BuildMarker;
            }

            private void DiagnosticAction()
            {
                if (_actionConsumed)
                    return;
                _actionConsumed = true;
                ++Actions;
                if (
                    _action.StartsWith("Restage", StringComparison.Ordinal)
                    || _action.StartsWith("Stage", StringComparison.Ordinal)
                )
                {
                    Register(Token, _scenario, _carrier, () => ++ReplacementCalls);
                    if (_action.StartsWith("Restage", StringComparison.Ordinal))
                        Token.Enable();
                    Bus.RejectCleanup = _rejectCleanup;
                }
                if (_action.EndsWith("Throw", StringComparison.Ordinal))
                    throw DiagnosticMarker;
            }

            internal void Run()
            {
                CleanupMarker.OnFormat = () =>
                {
                    ++Formats;
                    if (_boundary == "Format")
                        DiagnosticAction();
                };
                MessagingDebug.enabled = _mode != "Off";
                MessagingDebug.LogFunction =
                    _mode == "NullReceiver"
                        ? null
                        : (level, message) =>
                        {
                            if (
                                message.StartsWith(
                                    "Registration build cleanup failed",
                                    StringComparison.Ordinal
                                )
                            )
                            {
                                ++Receivers;
                                if (_boundary == "Receiver")
                                    DiagnosticAction();
                            }
                            else
                                ++OtherLogs;
                        };
                try
                {
                    ReturnedLease = Builder.Build(
                        new MessageRegistrationBuildOptions
                        {
                            Owner = new InstanceId(966),
                            ActivateOnBuild = true,
                            Configure = token =>
                            {
                                Token = token;
                                Register(token, _scenario, _carrier, () => ++OriginalCalls);
                                if (_phase == "Configure")
                                    Selected(token);
                            },
                            Lifecycle = new MessageRegistrationLifecycle(
                                _phase == "OnBuild"
                                    ? (Action<MessageRegistrationToken>)Selected
                                    : null,
                                _phase == "OnActivate"
                                    ? (Action<MessageRegistrationToken>)Selected
                                    : null,
                                null,
                                _ =>
                                {
                                    ++DisposalHooks;
                                    throw CleanupMarker;
                                }
                            ),
                        }
                    );
                }
                catch (Exception exception)
                {
                    Failure = exception;
                    if (exception is MessageRegistrationBuildException build)
                        RecoveryLease = build.Lease;
                }
                finally
                {
                    CleanupMarker.OnFormat = null;
                    MessagingDebug.enabled = false;
                    MessagingDebug.LogFunction = null;
                }
            }

            public void Dispose()
            {
                CleanupMarker.OnFormat = null;
                MessagingDebug.enabled = false;
                MessagingDebug.LogFunction = null;
                Bus.RejectCleanup = false;
                try
                {
                    try
                    {
                        ReturnedLease?.Dispose();
                        RecoveryLease?.Dispose();
                    }
                    finally
                    {
                        Token?.Dispose();
                    }
                    Inner.Trim(force: true);
                }
                finally
                {
                    try
                    {
                        _watcher.Dispose();
                    }
                    finally
                    {
                        _diagnostics.Dispose();
                        MessagingDebug.enabled = _savedDebug;
                        MessagingDebug.LogFunction = _savedLogger;
                    }
                }
            }
        }
    }
}
