namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;

    /// <summary>Checks selective ownership and retryable cleanup of registration groups.</summary>
    /// <remarks>
    /// 2026-11-11: cleanup accepted during teardown or after an in-flight registration callback
    /// must remain reachable through its group. Final orphan cleanup also clears empty-token diagnostics.
    /// </remarks>
    [TestFixture]
#if !UNITY_2021_3_OR_NEWER
    [NonParallelizable]
#endif
    public sealed class MessageRegistrationGroupTests
    {
        private static readonly InstanceId Context = new InstanceId(952);

        [Test]
        public void DisposeRemovesOnlyEnrolledRegistrations(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(false, true)] bool enabled
        )
        {
            using CaseState state = new CaseState();
            if (enabled)
            {
                state.Token.Enable();
            }
            int grouped = 0;
            int unrelated = 0;
            MessageRegistrationGroup group = state.Token.CreateGroup();
            MessageRegistrationHandle handle = Register(state.Token, scenario, () => ++grouped);
            Assert.That(
                group.Add(handle),
                Is.EqualTo(handle),
                $"[{scenario}, {enabled}] Add returns its handle."
            );
            _ = Register(state.Token, scenario, () => ++unrelated);
            group.Dispose();
            group.Dispose();
            state.Token.Enable();
            Emit(state.Bus, scenario);
            Assert.That(
                grouped,
                Is.Zero,
                $"[{scenario}, {enabled}] Disposed group must not replay."
            );
            Assert.That(
                unrelated,
                Is.EqualTo(1),
                $"[{scenario}, {enabled}] Other registrations stay active."
            );
            Assert.That(
                group.Count,
                Is.Zero,
                $"[{scenario}, {enabled}] Completed cleanup releases handles."
            );
        }

        [Test]
        public void DuplicateEnrollmentAndOverlappingGroupsAreHarmless(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            using CaseState state = new CaseState();
            state.Token.Enable();
            int calls = 0;
            MessageRegistrationHandle handle = Register(state.Token, scenario, () => ++calls);
            MessageRegistrationGroup first = state.Token.CreateGroup();
            MessageRegistrationGroup second = state.Token.CreateGroup();
            first.Add(handle);
            first.Add(handle);
            second.Add(handle);
            Assert.That(
                first.Count,
                Is.EqualTo(1),
                $"[{scenario}] Duplicate enrollment stores one obligation."
            );
            first.Dispose();
            MessageRegistrationHandle replacement = Register(state.Token, scenario, () => ++calls);
            second.Dispose();
            Emit(state.Bus, scenario);
            Assert.That(
                calls,
                Is.EqualTo(1),
                $"[{scenario}] A stale overlapping member must not remove a reused slot."
            );
            Assert.That(
                replacement,
                Is.Not.EqualTo(handle),
                $"[{scenario}] Replacement has a new identity."
            );
        }

        [Test]
        public void DisposingDuringDispatchPreservesTheFrozenCallbacks(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            using CaseState state = new CaseState();
            state.Token.Enable();
            List<string> calls = new List<string>();
            MessageRegistrationGroup group = state.Token.CreateGroup();
            group.Add(
                Register(
                    state.Token,
                    scenario,
                    () =>
                    {
                        calls.Add("first");
                        group.Dispose();
                        Emit(state.Bus, scenario);
                    },
                    -1
                )
            );
            group.Add(Register(state.Token, scenario, () => calls.Add("second"), 1));
            Emit(state.Bus, scenario);
            Assert.That(
                calls,
                Is.EqualTo(new[] { "first", "second" }),
                $"[{scenario}] Outer callbacks finish; nested emission sees removal."
            );
        }

        [Test]
        public void TokenCleanupBeforeGroupDisposalDoesNotRemoveReplacement(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(false, true)] bool disposeToken
        )
        {
            using CaseState state = new CaseState();
            state.Token.Enable();
            MessageRegistrationGroup group = state.Token.CreateGroup();
            group.Add(
                Register(state.Token, scenario, () =>
                    Assert.Fail("Removed callback must stay removed.")
                )
            );
            if (disposeToken)
            {
                state.Token.Dispose();
            }
            else
            {
                state.Token.UnregisterAll();
            }
            int calls = 0;
            Register(state.Token, scenario, () => ++calls);
            state.Token.Enable();
            group.Dispose();
            Emit(state.Bus, scenario);
            Assert.That(
                calls,
                Is.EqualTo(1),
                $"[{scenario}, {disposeToken}] Stale group must preserve replacement registration."
            );
        }

        [Test]
        public void AddRejectsForeignMissingAndRetiredHandles()
        {
            using CaseState state = new CaseState();
            using CaseState other = new CaseState();
            MessageScenario scenario = MessageScenario.Untargeted();
            MessageRegistrationGroup group = state.Token.CreateGroup();
            MessageRegistrationHandle foreign = Register(other.Token, scenario, () => { });
            MessageRegistrationHandle retired = Register(state.Token, scenario, () => { });
            state.Token.RemoveRegistration(retired);
            foreach (
                MessageRegistrationHandle handle in new[]
                {
                    default,
                    foreign,
                    retired,
                    MessageRegistrationHandle.CreateMessageRegistrationHandle(),
                }
            )
            {
                Assert.Throws<ArgumentException>(
                    () => group.Add(handle),
                    $"Invalid handle {handle} must not be silently enrolled."
                );
            }
            Assert.That(group.Count, Is.Zero, "Rejected enrollment leaves the group empty.");
            group.Dispose();
        }

        [Test]
        public void AddAfterDisposalStartsThrowsWithoutTakingOwnership()
        {
            using CaseState state = new CaseState();
            state.Token.Enable();
            MessageScenario scenario = MessageScenario.Untargeted();
            MessageRegistrationGroup group = state.Token.CreateGroup();
            group.Add(Register(state.Token, scenario, () => { }));
            int calls = 0;
            MessageRegistrationHandle outside = Register(state.Token, scenario, () => ++calls, 1);
            state.Bus.OnCleanup = () =>
                Assert.Throws<ObjectDisposedException>(
                    () => group.Add(outside),
                    "Reentrant enrollment after disposal starts must be rejected."
                );
            group.Dispose();
            Assert.Throws<ObjectDisposedException>(
                () => group.Add(outside),
                "Completed group cannot accept ownership."
            );
            Emit(state.Bus, scenario);
            Assert.That(
                calls,
                Is.EqualTo(1),
                "Rejected enrollment must leave the external handle active."
            );
        }

        [Test]
        public void EmptyGroupDisposalIsIdempotent()
        {
            using CaseState state = new CaseState();
            MessageRegistrationGroup group = state.Token.CreateGroup();
            group.Dispose();
            Assert.DoesNotThrow(
                group.Dispose,
                "Empty groups complete disposal without invoking token cleanup."
            );
            Assert.That(group.Count, Is.Zero, "Empty group has no remaining handles.");
        }

        [Test]
        public void CleanupContinuesAfterFailuresAndRetriesOnlyFailedMembers(
            [Values(1, 2)] int failures
        )
        {
            using CaseState state = new CaseState();
            state.Token.Enable();
            MessageScenario scenario = MessageScenario.Untargeted();
            MessageRegistrationGroup group = state.Token.CreateGroup();
            int[] calls = new int[3];
            for (int i = 0; i < calls.Length; ++i)
            {
                int index = i;
                group.Add(Register(state.Token, scenario, () => ++calls[index], i));
            }
            state.Bus.Failures = failures;
            if (failures == 1)
            {
                Assert.That(
                    Assert.Throws<InvalidOperationException>(group.Dispose),
                    Is.SameAs(state.Bus.Failure),
                    "One failure keeps the original exception."
                );
            }
            else
            {
                AggregateException error = Assert.Throws<AggregateException>(group.Dispose);
                Assert.That(
                    error.InnerExceptions.Count,
                    Is.EqualTo(failures),
                    "Every failed obligation must be reported."
                );
            }
            Assert.That(
                state.Bus.Attempts.Count,
                Is.EqualTo(3),
                "A failure must not stop cleanup of other members."
            );
            Assert.That(
                group.Count,
                Is.EqualTo(failures),
                "Only failed removals remain owned by the group."
            );
            Assert.Throws<ObjectDisposedException>(
                () => group.Add(Register(state.Token, scenario, () => { }, 5)),
                "Failed disposal still closes enrollment."
            );
            Emit(state.Bus, scenario);
            Assert.That(
                calls,
                Is.EqualTo(failures == 1 ? new[] { 1, 0, 0 } : new[] { 1, 1, 0 }),
                "Only callbacks with failed removals stay active."
            );
            group.Dispose();
            Assert.That(
                state.Bus.Attempts.Count,
                Is.EqualTo(3 + failures),
                "Retry must visit only failed members."
            );
            Assert.That(group.Count, Is.Zero, "Successful retry releases all group handles.");
        }

        [Test]
        public void ReentrantDisposalDoesNotInvokeAnObligationTwice()
        {
            using CaseState state = new CaseState();
            state.Token.Enable();
            MessageRegistrationGroup group = state.Token.CreateGroup();
            group.Add(Register(state.Token, MessageScenario.Untargeted(), () => { }));
            state.Bus.OnCleanup = group.Dispose;
            group.Dispose();
            Assert.That(
                state.Bus.Attempts.Count,
                Is.EqualTo(1),
                "Nested Dispose must defer to the current cleanup owner."
            );
            Assert.That(group.Count, Is.Zero, "Outer disposal completes the group.");
        }

        [Test]
        public void CleanupAcceptedDuringRemovalRemainsOwnedForTheNextPass()
        {
            using CaseState state = new CaseState();
            state.Token.Enable();
            MessageRegistrationGroup group = state.Token.CreateGroup();
            int calls = 0;
            group.Add(Register(state.Token, MessageScenario.Untargeted(), () => ++calls));
            state.Bus.OnCleanup = () =>
            {
                state.Token.Disable();
                state.Token.Enable();
            };
            group.Dispose();
            Assert.That(
                group.Count,
                Is.EqualTo(1),
                "Reentrant activation creates cleanup that must stay reachable through the group."
            );
            Emit(state.Bus, MessageScenario.Untargeted());
            Assert.That(
                calls,
                Is.EqualTo(1),
                "The newly accepted registration remains live until the next cleanup pass."
            );
            group.Dispose();
            Assert.That(group.Count, Is.Zero, "The next pass drains the preserved obligation.");
            Emit(state.Bus, MessageScenario.Untargeted());
            Assert.That(calls, Is.EqualTo(1), "No callback remains after the second cleanup pass.");
        }

        [Test]
        public void DisposalDuringReplayKeepsCleanupThatArrivesAfterTheCallback(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(false, true)] bool createDuringReplay
        )
        {
            using CaseState state = new CaseState();
            MessageRegistrationGroup group = createDuringReplay ? null : state.Token.CreateGroup();
            int calls = 0;
            MessageRegistrationHandle handle = Register(state.Token, scenario, () => ++calls);
            if (!createDuringReplay)
            {
                group.Add(handle);
            }
            state.Bus.OnRegistration = () =>
            {
                if (createDuringReplay)
                {
                    group = state.Token.CreateGroup();
                    group.Add(handle);
                }
                group.Dispose();
            };
            state.Token.Enable();
            Assert.That(
                group.Count,
                Is.EqualTo(1),
                $"[{scenario}, {createDuringReplay}] In-flight acceptance must keep the group available for its late cleanup handle."
            );
            Emit(state.Bus, scenario);
            Assert.That(
                calls,
                Is.EqualTo(1),
                $"[{scenario}] Accepted callback stays reachable until its cleanup arrives."
            );
            group.Dispose();
            Emit(state.Bus, scenario);
            Assert.That(
                calls,
                Is.EqualTo(1),
                $"[{scenario}] Retry through the group must drain the late orphan cleanup."
            );
            Assert.That(
                group.Count,
                Is.Zero,
                $"[{scenario}] Group completes after the accepting call has returned."
            );
        }

        /// <summary>Clears diagnostics after draining the last late-accepted cleanup.</summary>
        /// <remarks>2026-11-11: a late callback can record history after its staged slot was removed.</remarks>
        [Test]
        public void LastLateCleanupClearsDiagnosticState(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            using DiagnosticsScope diagnostics = new DiagnosticsScope(
                messageBufferSize: 8,
                diagnosticsStackTraces: false
            );
            using CaseState state = new CaseState();
            state.Token.DiagnosticMode = true;
            MessageRegistrationGroup group = state.Token.CreateGroup();
            group.Add(Register(state.Token, scenario, () => { }));
            state.Bus.OnRegistration = group.Dispose;
            state.Token.Enable();
            Emit(state.Bus, scenario);
            Assert.That(
                state.Token._metadata.Count,
                Is.Zero,
                $"[{scenario}] The accepting callback removed staging."
            );
            Assert.That(
                state.Token._emissionBuffer.Count,
                Is.EqualTo(1),
                $"[{scenario}] Late delivery records bounded history before final cleanup."
            );
            group.Dispose();
            Assert.That(
                group.Count,
                Is.Zero,
                $"[{scenario}] Late cleanup completes through the group."
            );
            Assert.That(
                state.Token._metadata.Count,
                Is.Zero,
                $"[{scenario}] Completed cleanup leaves no staging."
            );
            Assert.That(
                state.Token._callCounts.Count,
                Is.Zero,
                $"[{scenario}] Completed cleanup leaves no call counters."
            );
            Assert.That(
                state.Token._emissionBuffer.Count,
                Is.Zero,
                $"[{scenario}] Final orphan cleanup must clear diagnostic history."
            );
        }

        [Test]
        public void LateAcceptanceUsesOwningGroupForEveryRegistrationFamily(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(false, true)] bool globalObserver
        )
        {
            using CaseState state = new CaseState();
            MessageRegistrationGroup group = state.Token.CreateGroup();
            int calls = 0;
            MessageRegistrationHandle handle;
            if (globalObserver)
            {
                handle = state.Token.RegisterGlobalAcceptAll(
                    (in IUntargetedMessage _) => ++calls,
                    (in InstanceId _, in ITargetedMessage __) => ++calls,
                    (in InstanceId _, in IBroadcastMessage __) => ++calls
                );
            }
            else
            {
                switch (scenario.Kind)
                {
                    case MessageKind.Untargeted:
                        handle = state.Token.RegisterUntargetedInterceptor<ProbeMessage>(
                            (ref ProbeMessage _) =>
                            {
                                ++calls;
                                return true;
                            }
                        );
                        break;
                    case MessageKind.Targeted:
                        handle = state.Token.RegisterTargetedInterceptor<ProbeMessage>(
                            (ref InstanceId _, ref ProbeMessage __) =>
                            {
                                ++calls;
                                return true;
                            }
                        );
                        break;
                    case MessageKind.Broadcast:
                        handle = state.Token.RegisterBroadcastInterceptor<ProbeMessage>(
                            (ref InstanceId _, ref ProbeMessage __) =>
                            {
                                ++calls;
                                return true;
                            }
                        );
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(scenario));
                }
            }
            group.Add(handle);
            _ = Register(state.Token, scenario, () => { });
            state.Bus.OnRegistration = group.Dispose;
            state.Token.Enable();
            Assert.That(
                group.Count,
                Is.EqualTo(1),
                $"[{scenario}, global={globalObserver}] Late acceptance keeps its owning group."
            );
            Emit(state.Bus, scenario);
            Assert.That(
                calls,
                Is.EqualTo(1),
                $"[{scenario}, global={globalObserver}] Accepted callback remains live before retry."
            );
            group.Dispose();
            Emit(state.Bus, scenario);
            Assert.That(
                calls,
                Is.EqualTo(1),
                $"[{scenario}, global={globalObserver}] Group retry drains the accepted callback."
            );
            Assert.That(
                group.Count,
                Is.Zero,
                $"[{scenario}, global={globalObserver}] Group releases completed ownership."
            );
        }

        private static MessageRegistrationHandle Register(
            MessageRegistrationToken token,
            MessageScenario scenario,
            Action record,
            int priority = 0
        )
        {
            MessageHandler.FastHandler<ProbeMessage> handler = (in ProbeMessage _) => record();
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                    return ScenarioHarness.RegisterUntargeted(scenario, token, handler, priority);
                case MessageKind.Targeted:
                    return ScenarioHarness.RegisterTargeted(
                        scenario,
                        token,
                        Context,
                        handler,
                        priority
                    );
                case MessageKind.Broadcast:
                    return ScenarioHarness.RegisterBroadcast(
                        scenario,
                        token,
                        Context,
                        handler,
                        priority
                    );
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario));
            }
        }

        private static void Emit(IMessageBus bus, MessageScenario scenario)
        {
            ProbeMessage message = default;
            InstanceId context = Context;
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                    ScenarioHarness.EmitUntargeted(scenario, ref message, bus);
                    break;
                case MessageKind.Targeted:
                    ScenarioHarness.EmitTargeted(scenario, ref message, context, bus);
                    break;
                case MessageKind.Broadcast:
                    ScenarioHarness.EmitBroadcast(scenario, ref message, context, bus);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario));
            }
        }

        private sealed class CaseState : IDisposable
        {
            internal readonly MessageBus Inner = new MessageBus();
            internal readonly CleanupBus Bus;
            internal readonly MessageRegistrationToken Token;
            private readonly LeakWatcher _leaks;

            internal CaseState()
            {
                _leaks = new LeakWatcher(Inner);
                Bus = new CleanupBus(Inner);
                MessageHandler handler = new MessageHandler(new InstanceId(951), Bus)
                {
                    active = true,
                };
                Token = MessageRegistrationToken.Create(handler, Bus);
                Token.DiagnosticMode = false;
            }

            public void Dispose()
            {
                Bus.Failures = 0;
                Bus.OnCleanup = null;
                Bus.OnRegistration = null;
                Token.Dispose();
                Inner.Trim(true);
                _leaks.Dispose();
            }
        }

        private sealed class CleanupBus : DelegatingMessageBus
        {
            internal readonly InvalidOperationException Failure = new InvalidOperationException(
                "Cleanup rejected before forwarding."
            );
            internal readonly List<MessageBusRegistration> Attempts =
                new List<MessageBusRegistration>();
            internal int Failures;
            internal Action OnCleanup;
            internal Action OnRegistration;

            internal CleanupBus(IMessageBus inner)
                : base(inner) { }

            private void BeforeRegistration()
            {
                Action callback = OnRegistration;
                OnRegistration = null;
                callback?.Invoke();
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
                Attempts.Add(registration);
                Action callback = OnCleanup;
                OnCleanup = null;
                callback?.Invoke();
                if (0 < Failures)
                {
                    --Failures;
                    throw Failure;
                }
                base.Deregister<T>(in registration);
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
