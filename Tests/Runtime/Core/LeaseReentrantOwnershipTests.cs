namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;
    using BusType = DxMessaging.Core.MessageBus.MessageBus;

    /// <summary>Keeps lease activation and cleanup owned across custom bus callbacks.</summary>
    /// <remarks>2026-10-25: Reentrant activation duplicated lifecycle hooks and disposal could strand live cleanup.</remarks>
    [TestFixture]
#if !UNITY_2021_3_OR_NEWER
    [NonParallelizable]
#endif
    public sealed class LeaseReentrantOwnershipTests
    {
        private static readonly InstanceId Context = new InstanceId(970);

        private static IEnumerable<TestCaseData> ActivationCases()
        {
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            foreach (int carrier in new[] { 0, 1, 2, 3, 4, 5 })
            foreach (bool before in new[] { false, true })
            foreach (string mutation in new[] { "None", "Activate", "Dispose" })
            foreach (bool rejectCleanup in new[] { false, true })
            {
                yield return new TestCaseData(
                    scenario,
                    carrier,
                    before,
                    mutation,
                    rejectCleanup
                ).SetName(
                    $"LeaseActivationOwnership_{scenario}_carrier{carrier}_before{before}_{mutation}_reject{rejectCleanup}"
                );
            }
        }

        /// <summary>Publishes one activation or retains retryable cleanup after reentrant disposal.</summary>
        [TestCaseSource(nameof(ActivationCases))]
        public void ForeignRegistrationCallbackPreservesLeaseOwnership(
            MessageScenario scenario,
            int carrier,
            bool before,
            string mutation,
            bool rejectCleanup
        )
        {
            string label =
                $"{scenario},carrier={carrier},before={before},{mutation},reject={rejectCleanup}";
            using Scope scope = new Scope(label);
            int calls = 0;
            int activations = 0;
            int deactivations = 0;
            int disposals = 0;
            scope.Lease = scope.Builder.Build(
                new MessageRegistrationBuildOptions
                {
                    Owner = new InstanceId(971),
                    Configure = token =>
                    {
                        scope.Token = token;
                        Register(token, scenario, carrier, () => ++calls);
                    },
                    Lifecycle = new MessageRegistrationLifecycle(
                        null,
                        _ => ++activations,
                        _ => ++deactivations,
                        _ => ++disposals
                    ),
                }
            );
            Assert.That(scope.Lease.IsActive, Is.False, label + ": lease begins inactive");
            Assert.That(scope.Token.Enabled, Is.False, label + ": token begins staged");
            Assert.That(Total(scope.Inner), Is.Zero, label + ": setup has no live registration");
            scope.Bus.Before = before;
            scope.Bus.RejectCleanup = rejectCleanup;
            scope.Bus.OnRegister = () =>
            {
                if (mutation == "Activate")
                    scope.Lease.Activate();
                else if (mutation == "Dispose")
                    scope.Lease.Dispose();
            };
            Exception failure = null;
            try
            {
                scope.Lease.Activate();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            bool disposedDuringActivation = mutation == "Dispose";
            bool retryable = disposedDuringActivation && rejectCleanup;
            bool live = !disposedDuringActivation || retryable;
            Assert.That(
                scope.Bus.Callbacks,
                Is.EqualTo(1),
                label + ": foreign registration callback is one-shot"
            );
            Assert.That(
                failure,
                Is.SameAs(retryable ? scope.Bus.CleanupMarker : null),
                label + ": only rejected accepted cleanup fails activation"
            );
            Assert.That(
                activations,
                Is.EqualTo(disposedDuringActivation ? 0 : 1),
                label + ": activation is published once and never after disposal"
            );
            Assert.That(
                disposals,
                Is.EqualTo(disposedDuringActivation ? 1 : 0),
                label + ": reentrant disposal hook runs once"
            );
            Assert.That(
                deactivations,
                Is.Zero,
                label + ": unpublished activation has no deactivation hook"
            );
            Assert.That(
                scope.Lease.IsActive,
                Is.EqualTo(live),
                label + ": lease reports remaining owned cleanup"
            );
            Assert.That(
                scope.Token.Enabled,
                Is.EqualTo(live),
                label + ": token state agrees with cleanup ownership"
            );
            Assert.That(
                scope.Lease.Handler.active,
                Is.EqualTo(live),
                label + ": handler follows complete or retryable cleanup"
            );
            Assert.That(
                Total(scope.Inner),
                Is.EqualTo(live ? 1 : 0),
                label + ": accepted registration has a reachable owner"
            );
            Emit(scope.Inner, scenario);
            Assert.That(
                calls,
                Is.EqualTo(live ? 1 : 0),
                label + ": selected delivery reflects live ownership"
            );
            if (disposedDuringActivation && !retryable)
            {
                Assert.Throws<ObjectDisposedException>(
                    () => scope.Lease.Activate(),
                    label + ": completed disposal rejects activation"
                );
            }
            else
            {
                scope.Lease.Activate();
                Assert.That(
                    activations,
                    Is.EqualTo(disposedDuringActivation ? 0 : 1),
                    label + ": active or retryable ownership is not republished"
                );
            }
            scope.Bus.RejectCleanup = false;
            scope.Lease.Dispose();
            scope.Lease.Dispose();
            Assert.That(
                disposals,
                Is.EqualTo(1),
                label + ": disposal retries do not repeat its hook"
            );
            Assert.That(
                scope.Lease.IsActive,
                Is.False,
                label + ": valid lease cleanup becomes inactive"
            );
            Assert.That(
                scope.Token.Enabled,
                Is.False,
                label + ": valid lease cleanup disables the token"
            );
            Assert.That(
                scope.Token._metadata,
                Is.Empty,
                label + ": valid lease cleanup releases staging"
            );
            Assert.That(
                Total(scope.Inner),
                Is.Zero,
                label + ": lease-only cleanup clears live registrations"
            );
            int afterCleanup = calls;
            Emit(scope.Inner, scenario);
            Assert.That(
                calls,
                Is.EqualTo(afterCleanup),
                label + ": retired callbacks stay retired"
            );
            AssertRecovery(scope, scenario, carrier, label);
        }

        private static IEnumerable<TestCaseData> CleanupCases()
        {
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            foreach (int carrier in new[] { 0, 1, 2, 3, 4, 5 })
            foreach (bool before in new[] { false, true })
            foreach (string outer in new[] { "Dispose", "Deactivate" })
            foreach (bool restage in new[] { false, true })
            {
                yield return new TestCaseData(scenario, carrier, before, outer, restage).SetName(
                    $"LeaseCleanupOwnership_{scenario}_carrier{carrier}_before{before}_{outer}_restage{restage}"
                );
            }
        }

        /// <summary>Lets lease cleanup retry registrations accepted during its final token cleanup.</summary>
        [TestCaseSource(nameof(CleanupCases))]
        public void CleanupAcceptedByForeignCallbackRemainsRetryableThroughLease(
            MessageScenario scenario,
            int carrier,
            bool before,
            string outer,
            bool restage
        )
        {
            string label =
                $"{scenario},carrier={carrier},before={before},{outer},restage={restage}";
            using Scope scope = new Scope(label);
            int originalCalls = 0;
            int replacementCalls = 0;
            int activations = 0;
            int deactivations = 0;
            int disposals = 0;
            void DuringCleanup()
            {
                if (restage)
                    Register(scope.Token, scenario, carrier, () => ++replacementCalls);
            }
            scope.Lease = scope.Builder.Build(
                new MessageRegistrationBuildOptions
                {
                    Owner = new InstanceId(971),
                    ActivateOnBuild = true,
                    Configure = token =>
                    {
                        scope.Token = token;
                        Register(token, scenario, carrier, () => ++originalCalls);
                    },
                    Lifecycle = new MessageRegistrationLifecycle(
                        null,
                        _ => ++activations,
                        _ => ++deactivations,
                        token =>
                        {
                            ++disposals;
                            if (outer == "Dispose")
                            {
                                token.Enable();
                                scope.Bus.OnDeregister = DuringCleanup;
                            }
                        }
                    ),
                }
            );
            Emit(scope.Inner, scenario);
            Assert.That(
                originalCalls,
                Is.EqualTo(1),
                label + ": original callback is live before cleanup"
            );
            Assert.That(
                Total(scope.Inner),
                Is.EqualTo(1),
                label + ": setup owns one live registration"
            );
            scope.Bus.Before = before;
            if (outer == "Deactivate")
                scope.Bus.OnDeregister = DuringCleanup;
            if (outer == "Dispose")
                scope.Lease.Dispose();
            else
                scope.Lease.Deactivate();
            Assert.That(
                scope.Bus.Callbacks,
                Is.EqualTo(1),
                label + ": one final or deactivation cleanup callback"
            );
            Assert.That(
                activations,
                Is.EqualTo(1),
                label + ": token reenable does not publish lease activation"
            );
            Assert.That(
                deactivations,
                Is.EqualTo(1),
                label + ": initial deactivation hook runs once"
            );
            Assert.That(
                disposals,
                Is.EqualTo(outer == "Dispose" ? 1 : 0),
                label + ": disposal hook follows the selected operation"
            );
            Assert.That(
                scope.Lease.IsActive,
                Is.EqualTo(restage),
                label + ": callback-accepted cleanup stays visible"
            );
            Assert.That(
                scope.Token.Enabled,
                Is.EqualTo(restage),
                label + ": token reports deferred cleanup"
            );
            Assert.That(
                Total(scope.Inner),
                Is.EqualTo(restage ? 1 : 0),
                label + ": only newly accepted cleanup can remain"
            );
            int beforeOriginal = originalCalls;
            Emit(scope.Inner, scenario);
            Assert.That(
                originalCalls,
                Is.EqualTo(beforeOriginal),
                label + ": original registration is retired"
            );
            Assert.That(
                replacementCalls,
                Is.EqualTo(restage ? 1 : 0),
                label + ": new registration retains exact delivery identity"
            );
            if (restage)
            {
                scope.Lease.Activate();
                Assert.That(
                    activations,
                    Is.EqualTo(1),
                    label + ": pending cleanup stays reachable without republishing activation"
                );
            }
            scope.Lease.Dispose();
            scope.Lease.Dispose();
            Assert.That(
                Total(scope.Inner),
                Is.Zero,
                label + ": second lease disposal consumes deferred cleanup"
            );
            Assert.That(
                scope.Lease.IsActive,
                Is.False,
                label + ": completed disposal becomes inactive"
            );
            Assert.That(
                scope.Token.Enabled,
                Is.False,
                label + ": completed disposal disables the token"
            );
            Assert.That(
                scope.Token._metadata,
                Is.Empty,
                label + ": completed disposal releases staging"
            );
            Assert.That(
                disposals,
                Is.EqualTo(1),
                label + ": cleanup retry does not repeat disposal hooks"
            );
            Assert.That(
                deactivations,
                Is.EqualTo(1),
                label + ": cleanup retry does not repeat deactivation hooks"
            );
            Assert.Throws<ObjectDisposedException>(
                () => scope.Lease.Activate(),
                label + ": completed disposal rejects activation"
            );
            AssertRecovery(scope, scenario, carrier, label);
        }

        private static IEnumerable<TestCaseData> ActivationHookCases()
        {
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            foreach (int carrier in new[] { 0, 1, 2, 3, 4, 5 })
            foreach (
                string mutation in new[] { "Reactivate", "Dispose", "Throw", "DeactivateThrow" }
            )
            {
                yield return new TestCaseData(scenario, carrier, mutation).SetName(
                    $"LeaseActivationHookControl_{scenario}_carrier{carrier}_{mutation}"
                );
            }
        }

        /// <summary>Preserves normal lifecycle callback reactivation and exception recovery.</summary>
        [TestCaseSource(nameof(ActivationHookCases))]
        public void ActivationHookCanReenterAfterReplayOwnershipEnds(
            MessageScenario scenario,
            int carrier,
            string mutation
        )
        {
            string label = $"{scenario},carrier={carrier},{mutation}";
            using Scope scope = new Scope(label);
            Exception marker = new InvalidOperationException("activation hook marker");
            int calls = 0;
            int activations = 0;
            int deactivations = 0;
            int disposals = 0;
            scope.Lease = scope.Builder.Build(
                new MessageRegistrationBuildOptions
                {
                    Owner = new InstanceId(971),
                    Configure = token =>
                    {
                        scope.Token = token;
                        Register(token, scenario, carrier, () => ++calls);
                    },
                    Lifecycle = new MessageRegistrationLifecycle(
                        null,
                        _ =>
                        {
                            ++activations;
                            if (activations != 1)
                                return;
                            if (mutation == "Reactivate")
                            {
                                scope.Lease.Deactivate();
                                scope.Lease.Activate();
                            }
                            else if (mutation == "Dispose")
                                scope.Lease.Dispose();
                            else
                            {
                                if (mutation == "DeactivateThrow")
                                    scope.Lease.Deactivate();
                                throw marker;
                            }
                        },
                        _ => ++deactivations,
                        _ => ++disposals
                    ),
                }
            );
            Exception failure = null;
            try
            {
                scope.Lease.Activate();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            bool throws = mutation == "Throw" || mutation == "DeactivateThrow";
            bool live = mutation == "Reactivate" || mutation == "Throw";
            Assert.That(
                failure,
                Is.SameAs(throws ? marker : null),
                label + ": callback failure identity is preserved"
            );
            Assert.That(
                activations,
                Is.EqualTo(mutation == "Reactivate" ? 2 : 1),
                label + ": separate callback-requested activation remains supported"
            );
            Assert.That(
                scope.Lease.IsActive,
                Is.EqualTo(live),
                label + ": callback owns the resulting lifecycle state"
            );
            Assert.That(
                scope.Token.Enabled,
                Is.EqualTo(live),
                label + ": callback and token state agree"
            );
            Assert.That(
                Total(scope.Inner),
                Is.EqualTo(live ? 1 : 0),
                label + ": callback controls live registrations"
            );
            Emit(scope.Inner, scenario);
            Assert.That(
                calls,
                Is.EqualTo(live ? 1 : 0),
                label + ": callback-selected delivery remains exact"
            );
            scope.Lease.Dispose();
            scope.Lease.Dispose();
            Assert.That(
                Total(scope.Inner),
                Is.Zero,
                label + ": callback reentry keeps valid cleanup"
            );
            Assert.That(disposals, Is.EqualTo(1), label + ": callback disposal runs once");
            Assert.That(
                deactivations,
                Is.EqualTo(mutation == "Reactivate" ? 2 : 1),
                label + ": deactivation follows distinct activation lifetimes"
            );
            AssertRecovery(scope, scenario, carrier, label);
        }

        private static IEnumerable<TestCaseData> BuildRecoveryCases()
        {
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            foreach (int carrier in new[] { 0, 1, 2, 3, 4, 5 })
            foreach (bool before in new[] { false, true })
            foreach (string phase in new[] { "Configure", "OnBuild" })
            {
                yield return new TestCaseData(scenario, carrier, before, phase).SetName(
                    $"LeaseBuildRecovery_{scenario}_carrier{carrier}_before{before}_{phase}"
                );
            }
        }

        /// <summary>Retains a builder recovery lease when callbacks accept cleanup without throwing.</summary>
        [TestCaseSource(nameof(BuildRecoveryCases))]
        public void FailedBuildTransfersDeferredCleanupToRetryableLease(
            MessageScenario scenario,
            int carrier,
            bool before,
            string phase
        )
        {
            string label = $"{scenario},carrier={carrier},before={before},{phase}";
            using Scope scope = new Scope(label);
            Exception marker = new InvalidOperationException("deferred build setup marker");
            int originalCalls = 0;
            int replacementCalls = 0;
            int disposals = 0;
            scope.Bus.Before = before;
            void Selected(MessageRegistrationToken token)
            {
                token.Enable();
                throw marker;
            }
            MessageRegistrationBuildException failure =
                Assert.Throws<MessageRegistrationBuildException>(
                    () =>
                        scope.Builder.Build(
                            new MessageRegistrationBuildOptions
                            {
                                Owner = new InstanceId(971),
                                Configure = token =>
                                {
                                    scope.Token = token;
                                    Register(token, scenario, carrier, () => ++originalCalls);
                                    if (phase == "Configure")
                                        Selected(token);
                                },
                                Lifecycle = new MessageRegistrationLifecycle(
                                    phase == "OnBuild"
                                        ? (Action<MessageRegistrationToken>)Selected
                                        : null,
                                    null,
                                    null,
                                    token =>
                                    {
                                        ++disposals;
                                        token.Enable();
                                        scope.Bus.OnDeregister = () =>
                                            Register(
                                                token,
                                                scenario,
                                                carrier,
                                                () => ++replacementCalls
                                            );
                                    }
                                ),
                            }
                        ),
                    label + ": deferred cleanup transfers an owning lease"
                );
            scope.Lease = failure.Lease;
            Assert.That(scope.Lease, Is.Not.Null, label + ": the recovery lease is reachable");
            Assert.That(
                failure.ActivationException,
                Is.SameAs(marker),
                label + ": original setup failure identity is retained"
            );
            Assert.That(
                failure.InnerException,
                Is.SameAs(marker),
                label + ": inner failure identity is retained"
            );
            Assert.That(
                failure.CleanupException,
                Is.Null,
                label + ": newly accepted cleanup need not throw"
            );
            Assert.That(
                scope.Bus.Callbacks,
                Is.EqualTo(1),
                label + ": final cleanup callback is one-shot"
            );
            Assert.That(
                scope.Lease.IsActive,
                Is.True,
                label + ": recovery reports its accepted cleanup"
            );
            Assert.That(scope.Token.Enabled, Is.True, label + ": accepted cleanup remains enabled");
            Assert.That(
                Total(scope.Inner),
                Is.EqualTo(1),
                label + ": exactly the replacement remains live"
            );
            Emit(scope.Inner, scenario);
            Assert.That(originalCalls, Is.Zero, label + ": original callback is retired");
            Assert.That(
                replacementCalls,
                Is.EqualTo(1),
                label + ": the replacement has reachable ownership"
            );
            scope.Lease.Dispose();
            scope.Lease.Dispose();
            Assert.That(
                Total(scope.Inner),
                Is.Zero,
                label + ": recovery lease cleanup consumes accepted work"
            );
            Assert.That(
                scope.Token.Enabled,
                Is.False,
                label + ": recovery cleanup disables the token"
            );
            Assert.That(
                scope.Token._metadata,
                Is.Empty,
                label + ": recovery cleanup releases staging"
            );
            Assert.That(
                disposals,
                Is.EqualTo(1),
                label + ": cleanup retry does not repeat the disposal hook"
            );
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
                        Owner = new InstanceId(972),
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

        private sealed class ReentrantBus : DelegatingMessageBus
        {
            internal bool Before;
            internal bool RejectCleanup;
            internal int Callbacks;
            internal Action OnRegister;
            internal Action OnDeregister;
            internal readonly Exception CleanupMarker = new InvalidOperationException(
                "lease cleanup marker"
            );

            internal ReentrantBus(BusType inner)
                : base(inner) { }

            private MessageBusRegistration Register(Func<MessageBusRegistration> register)
            {
                Action callback = OnRegister;
                OnRegister = null;
                if (callback != null && Before)
                {
                    ++Callbacks;
                    callback();
                }
                MessageBusRegistration registration = register();
                if (callback != null && !Before)
                {
                    ++Callbacks;
                    callback();
                }
                return registration;
            }

            public override MessageBusRegistration RegisterUntargeted<T>(
                MessageHandler handler,
                int priority = 0
            ) => Register(() => base.RegisterUntargeted<T>(handler, priority));

            public override MessageBusRegistration RegisterTargeted<T>(
                InstanceId context,
                MessageHandler handler,
                int priority = 0
            ) => Register(() => base.RegisterTargeted<T>(context, handler, priority));

            public override MessageBusRegistration RegisterSourcedBroadcast<T>(
                InstanceId context,
                MessageHandler handler,
                int priority = 0
            ) => Register(() => base.RegisterSourcedBroadcast<T>(context, handler, priority));

            public override MessageBusRegistration RegisterUntargetedPostProcessor<T>(
                MessageHandler handler,
                int priority = 0
            ) => Register(() => base.RegisterUntargetedPostProcessor<T>(handler, priority));

            public override MessageBusRegistration RegisterTargetedPostProcessor<T>(
                InstanceId context,
                MessageHandler handler,
                int priority = 0
            ) => Register(() => base.RegisterTargetedPostProcessor<T>(context, handler, priority));

            public override MessageBusRegistration RegisterBroadcastPostProcessor<T>(
                InstanceId context,
                MessageHandler handler,
                int priority = 0
            ) => Register(() => base.RegisterBroadcastPostProcessor<T>(context, handler, priority));

            public override MessageBusRegistration RegisterUntargetedInterceptor<T>(
                IMessageBus.UntargetedInterceptor<T> callback,
                int priority = 0
            ) => Register(() => base.RegisterUntargetedInterceptor(callback, priority));

            public override MessageBusRegistration RegisterTargetedInterceptor<T>(
                IMessageBus.TargetedInterceptor<T> callback,
                int priority = 0
            ) => Register(() => base.RegisterTargetedInterceptor(callback, priority));

            public override MessageBusRegistration RegisterBroadcastInterceptor<T>(
                IMessageBus.BroadcastInterceptor<T> callback,
                int priority = 0
            ) => Register(() => base.RegisterBroadcastInterceptor(callback, priority));

            public override MessageBusRegistration RegisterGlobalAcceptAll(
                MessageHandler handler
            ) => Register(() => base.RegisterGlobalAcceptAll(handler));

            public override void Deregister<T>(in MessageBusRegistration registration)
            {
                if (RejectCleanup)
                    throw CleanupMarker;
                Action callback = OnDeregister;
                OnDeregister = null;
                if (callback != null && Before)
                {
                    ++Callbacks;
                    callback();
                }
                base.Deregister<T>(in registration);
                if (callback != null && !Before)
                {
                    ++Callbacks;
                    callback();
                }
            }
        }

        private sealed class Scope : IDisposable
        {
            internal readonly BusType Inner = new BusType();
            internal readonly ReentrantBus Bus;
            internal readonly MessageRegistrationBuilder Builder;
            internal MessageRegistrationToken Token;
            internal MessageRegistrationLease Lease;
            private readonly DiagnosticsScope _diagnostics;
            private readonly LeakWatcher _watcher;
            private readonly bool _debug = MessagingDebug.enabled;
            private readonly Action<LogLevel, string> _logger = MessagingDebug.LogFunction;

            internal Scope(string label)
            {
                _diagnostics = new DiagnosticsScope(DiagnosticsTarget.Off, 0, false);
                MessagingDebug.enabled = false;
                MessagingDebug.LogFunction = null;
                Bus = new ReentrantBus(Inner);
                Builder = new MessageRegistrationBuilder(new FixedMessageBusProvider(Bus));
                _watcher = new LeakWatcher(Inner, label: label);
            }

            public void Dispose()
            {
                Bus.RejectCleanup = false;
                Bus.OnRegister = null;
                Bus.OnDeregister = null;
                try
                {
                    try
                    {
                        Lease?.Dispose();
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
                        MessagingDebug.enabled = _debug;
                        MessagingDebug.LogFunction = _logger;
                    }
                }
            }
        }
    }
}
