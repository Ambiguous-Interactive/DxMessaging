namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;
    using BusType = DxMessaging.Core.MessageBus.MessageBus;

    /// <summary>Preserves subscription ownership when builder setup callbacks fail.</summary>
    /// <remarks>2026-10-25: Configure and OnBuild could throw after enabling a token without returning its lease.</remarks>
    [TestFixture]
#if !UNITY_2021_3_OR_NEWER
    [NonParallelizable]
#endif
    public sealed class BuilderSetupFailureOwnershipTests
    {
        private static readonly InstanceId Context = new InstanceId(975);

        private static IEnumerable<TestCaseData> FailureCases()
        {
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            foreach (int carrier in new[] { 0, 1, 2, 3, 4, 5 })
            foreach (string phase in new[] { "Configure", "OnBuild", "OnActivate" })
            foreach (bool manualEnable in new[] { false, true })
            foreach (bool activate in new[] { false, true })
            foreach (bool rejectCleanup in new[] { false, true })
            foreach (bool throwDisposeHook in new[] { false, true })
            {
                if (phase == "OnActivate" && !activate)
                {
                    continue;
                }
                yield return new TestCaseData(
                    scenario,
                    carrier,
                    phase,
                    manualEnable,
                    activate,
                    rejectCleanup,
                    throwDisposeHook
                ).SetName(
                    $"BuilderFailure_{scenario}_carrier{carrier}_{phase}_enable{manualEnable}_activate{activate}_reject{rejectCleanup}_disposeThrow{throwDisposeHook}"
                );
            }
        }

        /// <summary>Cleans the failed build or transfers a retryable lease to the caller.</summary>
        [TestCaseSource(nameof(FailureCases))]
        public void FailedSetupPreservesOwnedCleanup(
            MessageScenario scenario,
            int carrier,
            string phase,
            bool manualEnable,
            bool activate,
            bool rejectCleanup,
            bool throwDisposeHook
        )
        {
            string label =
                $"{scenario},carrier={carrier},{phase},enable={manualEnable},activate={activate},reject={rejectCleanup},disposeThrow={throwDisposeHook}";
            using Scope scope = new Scope(label);
            Exception marker = new InvalidOperationException("setup marker");
            Exception disposeMarker = new InvalidOperationException("dispose hook marker");
            int selectedHooks = 0;
            int deactivateHooks = 0;
            int disposeHooks = 0;
            int calls = 0;
            void Selected(MessageRegistrationToken token)
            {
                ++selectedHooks;
                if (manualEnable)
                {
                    token.Enable();
                }
                throw marker;
            }
            MessageRegistrationBuildOptions options = new MessageRegistrationBuildOptions
            {
                ActivateOnBuild = activate,
                Configure = token =>
                {
                    scope.Token = token;
                    Register(token, scenario, carrier, () => ++calls);
                    if (phase == "Configure")
                    {
                        Selected(token);
                    }
                },
                Lifecycle = new MessageRegistrationLifecycle(
                    phase == "OnBuild" ? (Action<MessageRegistrationToken>)Selected : null,
                    phase == "OnActivate" ? (Action<MessageRegistrationToken>)Selected : null,
                    _ => ++deactivateHooks,
                    _ =>
                    {
                        ++disposeHooks;
                        if (throwDisposeHook)
                        {
                            throw disposeMarker;
                        }
                    }
                ),
            };
            scope.Bus.RejectCleanup = rejectCleanup;
            Exception observed = null;
            try
            {
                scope.Lease = scope.Builder.Build(options);
            }
            catch (Exception exception)
            {
                observed = exception;
            }

            bool liveBeforeFailure = manualEnable || phase == "OnActivate";
            bool retryable = rejectCleanup && liveBeforeFailure;
            Assert.That(selectedHooks, Is.EqualTo(1), label + ": selected setup hook runs once");
            Assert.That(scope.Lease, Is.Null, label + ": a failed build returns no ordinary lease");
            if (retryable)
            {
                Assert.That(
                    observed,
                    Is.TypeOf<MessageRegistrationBuildException>(),
                    label + ": cleanup rejection transfers the owning lease"
                );
                MessageRegistrationBuildException failure =
                    (MessageRegistrationBuildException)observed;
                scope.Lease = failure.Lease;
                Assert.That(scope.Lease, Is.Not.Null, label + ": recovery lease is reachable");
                Assert.That(
                    failure.ActivationException,
                    Is.SameAs(marker),
                    label + ": setup exception identity is retained"
                );
                Assert.That(
                    failure.InnerException,
                    Is.SameAs(marker),
                    label + ": inner exception is the setup failure"
                );
                Exception firstCleanupFailure =
                    throwDisposeHook && phase != "OnActivate"
                        ? disposeMarker
                        : scope.Bus.CleanupMarker;
                Assert.That(
                    failure.CleanupException,
                    Is.SameAs(firstCleanupFailure),
                    label + ": first cleanup failure identity is retained"
                );
                Assert.That(
                    scope.Lease.Token,
                    Is.SameAs(scope.Token),
                    label + ": recovery owns the original token"
                );
                Assert.That(
                    scope.Lease.IsActive,
                    Is.True,
                    label + ": recovery reports its live token"
                );
                Assert.That(
                    scope.Token.Enabled,
                    Is.True,
                    label + ": failed deregistration remains retryable"
                );
                Assert.That(
                    Total(scope.Inner),
                    Is.EqualTo(1),
                    label + ": exactly one registration remains owned"
                );
            }
            else
            {
                Assert.That(
                    observed,
                    Is.SameAs(marker),
                    label + ": successful cleanup rethrows the exact setup failure"
                );
                Assert.That(
                    scope.Token.Enabled,
                    Is.False,
                    label + ": automatic cleanup disables the token"
                );
                Assert.That(
                    Total(scope.Inner),
                    Is.Zero,
                    label + ": no registration escapes the failed build"
                );
                Assert.That(
                    scope.Token._metadata,
                    Is.Empty,
                    label + ": automatic cleanup releases staged callbacks"
                );
            }
            Assert.That(
                disposeHooks,
                Is.EqualTo(1),
                label + ": automatic cleanup invokes disposal once"
            );
            Assert.That(
                deactivateHooks,
                Is.EqualTo(phase == "OnActivate" ? 1 : 0),
                label + ": only lease activation triggers initial deactivation"
            );
            Emit(scope.Inner, scenario);
            Assert.That(
                calls,
                Is.EqualTo(retryable ? 1 : 0),
                label + ": selected delivery follows reachable cleanup ownership"
            );

            scope.Bus.RejectCleanup = false;
            scope.Lease?.Dispose();
            scope.Token.Dispose();
            scope.Lease?.Dispose();
            Assert.That(
                disposeHooks,
                Is.EqualTo(1),
                label + ": retries do not repeat disposal hooks"
            );
            Assert.That(
                deactivateHooks,
                Is.EqualTo(phase == "OnActivate" || retryable ? 1 : 0),
                label + ": cleanup retry deactivation runs at most once"
            );
            Assert.That(
                Total(scope.Inner),
                Is.Zero,
                label + ": retry cleanup clears all live counters"
            );
            Assert.That(
                scope.Token._metadata,
                Is.Empty,
                label + ": retry cleanup clears staged callbacks"
            );
            int callsAfterCleanup = calls;
            Emit(scope.Inner, scenario);
            Assert.That(
                calls,
                Is.EqualTo(callsAfterCleanup),
                label + ": retired callback never runs again"
            );
            AssertRecovery(scope, scenario, carrier, label);
        }

        private static IEnumerable<TestCaseData> SuccessCases()
        {
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            foreach (int carrier in new[] { 0, 1, 2, 3, 4, 5 })
            foreach (string phase in new[] { "Configure", "OnBuild", "OnActivate" })
            foreach (bool manualEnable in new[] { false, true })
            foreach (bool activate in new[] { false, true })
            {
                if (phase == "OnActivate" && !activate)
                {
                    continue;
                }
                yield return new TestCaseData(
                    scenario,
                    carrier,
                    phase,
                    manualEnable,
                    activate
                ).SetName(
                    $"BuilderSuccess_{scenario}_carrier{carrier}_{phase}_enable{manualEnable}_activate{activate}"
                );
            }
        }

        /// <summary>Preserves ownership and selected delivery for successful setup callbacks.</summary>
        [TestCaseSource(nameof(SuccessCases))]
        public void SuccessfulSetupReturnsItsLease(
            MessageScenario scenario,
            int carrier,
            string phase,
            bool manualEnable,
            bool activate
        )
        {
            string label =
                $"{scenario},carrier={carrier},{phase},enable={manualEnable},activate={activate}";
            using Scope scope = new Scope(label);
            int calls = 0;
            int selectedHooks = 0;
            void Selected(MessageRegistrationToken token)
            {
                ++selectedHooks;
                if (manualEnable)
                {
                    token.Enable();
                }
            }
            scope.Lease = scope.Builder.Build(
                new MessageRegistrationBuildOptions
                {
                    ActivateOnBuild = activate,
                    Configure = token =>
                    {
                        scope.Token = token;
                        Register(token, scenario, carrier, () => ++calls);
                        if (phase == "Configure")
                            Selected(token);
                    },
                    Lifecycle = new MessageRegistrationLifecycle(
                        phase == "OnBuild" ? (Action<MessageRegistrationToken>)Selected : null,
                        phase == "OnActivate" ? (Action<MessageRegistrationToken>)Selected : null,
                        null,
                        null
                    ),
                }
            );
            bool live = manualEnable || activate;
            Assert.That(selectedHooks, Is.EqualTo(1), label + ": selected callback runs once");
            Assert.That(
                scope.Lease.Token,
                Is.SameAs(scope.Token),
                label + ": returned lease owns the configured token"
            );
            Assert.That(
                scope.Token.Enabled,
                Is.EqualTo(live),
                label + ": successful enable state is preserved"
            );
            Assert.That(
                Total(scope.Inner),
                Is.EqualTo(live ? 1 : 0),
                label + ": successful registration count"
            );
            Emit(scope.Inner, scenario);
            Assert.That(calls, Is.EqualTo(live ? 1 : 0), label + ": successful selected delivery");
            scope.Lease.Dispose();
            scope.Lease.Dispose();
            Assert.That(
                Total(scope.Inner),
                Is.Zero,
                label + ": returned lease supports idempotent cleanup"
            );
            AssertRecovery(scope, scenario, carrier, label);
        }

        private static IEnumerable<TestCaseData> OptionCases()
        {
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            foreach (string phase in new[] { "Configure", "OnBuild" })
            foreach (bool initialActivate in new[] { false, true })
            foreach (bool finalActivate in new[] { false, true })
            {
                yield return new TestCaseData(
                    scenario,
                    phase,
                    initialActivate,
                    finalActivate
                ).SetName(
                    $"BuilderOptions_{scenario}_{phase}_initial{initialActivate}_final{finalActivate}"
                );
            }
        }

        /// <summary>Preserves when mutable options become visible to the constructed lease.</summary>
        [TestCaseSource(nameof(OptionCases))]
        public void SuccessfulSetupPreservesOptionMutationVisibility(
            MessageScenario scenario,
            string phase,
            bool initialActivate,
            bool finalActivate
        )
        {
            string label = $"{scenario},{phase},initial={initialActivate},final={finalActivate}";
            using Scope scope = new Scope(label);
            List<string> events = new List<string>();
            MessageRegistrationBuildOptions options = new MessageRegistrationBuildOptions();
            MessageRegistrationLifecycle replacement = new MessageRegistrationLifecycle(
                _ => events.Add("replacement build"),
                _ => events.Add("replacement activate"),
                null,
                null
            );
            options.ActivateOnBuild = initialActivate;
            options.Lifecycle = new MessageRegistrationLifecycle(
                _ =>
                {
                    events.Add("original build");
                    if (phase == "OnBuild")
                    {
                        options.Lifecycle = replacement;
                        options.ActivateOnBuild = finalActivate;
                    }
                },
                _ => events.Add("original activate"),
                null,
                null
            );
            options.Configure = token =>
            {
                scope.Token = token;
                Register(token, scenario, 1, () => events.Add("message"));
                events.Add("configure");
                if (phase == "Configure")
                {
                    options.Lifecycle = replacement;
                    options.ActivateOnBuild = finalActivate;
                }
            };
            scope.Lease = scope.Builder.Build(options);
            string selected = phase == "Configure" ? "replacement" : "original";
            List<string> expected = new List<string> { "configure", selected + " build" };
            if (finalActivate)
                expected.Add(selected + " activate");
            Assert.That(
                events,
                Is.EqualTo(expected),
                label + ": successful callback order and lifecycle snapshot"
            );
            Assert.That(
                scope.Lease.IsActive,
                Is.EqualTo(finalActivate),
                label + ": activation flag remains live until activation"
            );
            Emit(scope.Inner, scenario);
            if (finalActivate)
                expected.Add("message");
            Assert.That(
                events,
                Is.EqualTo(expected),
                label + ": mutated activation controls delivery"
            );
            scope.Lease.Dispose();
            Assert.That(
                Total(scope.Inner),
                Is.Zero,
                label + ": mutable options preserve cleanup ownership"
            );
        }

        private static IEnumerable<TestCaseData> ReentryCases()
        {
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            foreach (int carrier in new[] { 0, 1, 2, 3, 4, 5 })
            foreach (string phase in new[] { "Configure", "OnBuild" })
            foreach (string action in new[] { "Disable", "Reset", "Restage", "Sibling" })
            {
                yield return new TestCaseData(scenario, carrier, phase, action).SetName(
                    $"BuilderCleanupReentry_{scenario}_carrier{carrier}_{phase}_{action}"
                );
            }
        }

        /// <summary>Cleans reentrant setup work while preserving separately owned sibling leases.</summary>
        [TestCaseSource(nameof(ReentryCases))]
        public void FailedSetupCleanupPreservesReentrantOwnership(
            MessageScenario scenario,
            int carrier,
            string phase,
            string action
        )
        {
            string label = $"{scenario},carrier={carrier},{phase},{action}";
            using Scope scope = new Scope(label);
            Exception marker = new InvalidOperationException("reentrant setup marker");
            int originalCalls = 0;
            int newCalls = 0;
            int disposeHooks = 0;
            void Selected(MessageRegistrationToken token)
            {
                token.Enable();
                throw marker;
            }
            Exception observed = null;
            try
            {
                scope.Lease = scope.Builder.Build(
                    new MessageRegistrationBuildOptions
                    {
                        Owner = new InstanceId(976),
                        Configure = token =>
                        {
                            scope.Token = token;
                            Register(token, scenario, carrier, () => ++originalCalls);
                            if (phase == "Configure")
                                Selected(token);
                        },
                        Lifecycle = new MessageRegistrationLifecycle(
                            phase == "OnBuild" ? (Action<MessageRegistrationToken>)Selected : null,
                            null,
                            null,
                            token =>
                            {
                                ++disposeHooks;
                                if (action == "Disable")
                                    token.Disable();
                                else if (action == "Reset")
                                    scope.Inner.ResetState();
                                else if (action == "Restage")
                                    Register(token, scenario, carrier, () => ++newCalls);
                                else
                                    scope.Sibling = scope.Builder.Build(
                                        new MessageRegistrationBuildOptions
                                        {
                                            Owner = new InstanceId(977),
                                            ActivateOnBuild = true,
                                            Configure = next =>
                                                Register(next, scenario, carrier, () => ++newCalls),
                                        }
                                    );
                            }
                        ),
                    }
                );
            }
            catch (Exception exception)
            {
                observed = exception;
            }
            Assert.That(
                observed,
                Is.SameAs(marker),
                label + ": cleanup preserves the original failure"
            );
            Assert.That(disposeHooks, Is.EqualTo(1), label + ": cleanup hook runs once");
            Assert.That(scope.Token.Enabled, Is.False, label + ": failed build token is disabled");
            Assert.That(
                scope.Token._metadata,
                Is.Empty,
                label + ": failed build token releases all staging"
            );
            Assert.That(
                Total(scope.Inner),
                Is.EqualTo(action == "Sibling" ? 1 : 0),
                label + ": separately owned sibling survives cleanup"
            );
            Emit(scope.Inner, scenario);
            Assert.That(originalCalls, Is.Zero, label + ": original callback is retired");
            Assert.That(
                newCalls,
                Is.EqualTo(action == "Sibling" ? 1 : 0),
                label + ": only independently owned work remains live"
            );
            scope.Sibling?.Dispose();
            scope.Token.Dispose();
            Assert.That(
                disposeHooks,
                Is.EqualTo(1),
                label + ": direct token cleanup does not repeat lifecycle hooks"
            );
            Assert.That(Total(scope.Inner), Is.Zero, label + ": valid final cleanup precedes trim");
            AssertRecovery(scope, scenario, carrier, label);
        }

        private static void AssertRecovery(
            Scope scope,
            MessageScenario scenario,
            int carrier,
            string label
        )
        {
            int calls = 0;
            using (
                MessageRegistrationLease recovered = scope.Builder.Build(
                    new MessageRegistrationBuildOptions
                    {
                        Owner = new InstanceId(978),
                        ActivateOnBuild = true,
                        Configure = token => Register(token, scenario, carrier, () => ++calls),
                    }
                )
            )
            {
                Emit(scope.Inner, scenario);
                Assert.That(
                    calls,
                    Is.EqualTo(1),
                    label + ": independent recovery lease dispatches exactly once"
                );
            }
            Assert.That(Total(scope.Inner), Is.Zero, label + ": recovery lease cleans validly");
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

        private sealed class RejectingBus : DelegatingMessageBus
        {
            internal bool RejectCleanup;
            internal readonly Exception CleanupMarker = new InvalidOperationException(
                "deregistration marker"
            );

            internal RejectingBus(BusType inner)
                : base(inner) { }

            public override void Deregister<T>(in MessageBusRegistration registration)
            {
                if (RejectCleanup)
                    throw CleanupMarker;
                base.Deregister<T>(in registration);
            }
        }

        private sealed class Scope : IDisposable
        {
            internal readonly BusType Inner = new BusType();
            internal readonly RejectingBus Bus;
            internal readonly MessageRegistrationBuilder Builder;
            internal MessageRegistrationToken Token;
            internal MessageRegistrationLease Lease;
            internal MessageRegistrationLease Sibling;
            private readonly DiagnosticsScope _diagnostics;
            private readonly LeakWatcher _watcher;
            private readonly bool _debug = MessagingDebug.enabled;
            private readonly Action<LogLevel, string> _logger = MessagingDebug.LogFunction;

            internal Scope(string label)
            {
                _diagnostics = new DiagnosticsScope(DiagnosticsTarget.Off, 0, false);
                MessagingDebug.enabled = false;
                MessagingDebug.LogFunction = null;
                Bus = new RejectingBus(Inner);
                Builder = new MessageRegistrationBuilder(new FixedMessageBusProvider(Bus));
                _watcher = new LeakWatcher(Inner, label: label);
            }

            public void Dispose()
            {
                Bus.RejectCleanup = false;
                try
                {
                    try
                    {
                        Sibling?.Dispose();
                    }
                    finally
                    {
                        try
                        {
                            Lease?.Dispose();
                        }
                        finally
                        {
                            Token?.Dispose();
                        }
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
                        MessagingDebug.enabled = _debug;
                        MessagingDebug.LogFunction = _logger;
                    }
                }
            }
        }
    }
}
