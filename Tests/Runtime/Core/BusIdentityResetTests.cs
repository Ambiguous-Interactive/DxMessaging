namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Threading.Tasks;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;

    /// <summary>Independent buses keep independent typed-handler caches across static resets.</summary>
    /// <remarks>
    /// 2026-10-19: resetting the bus index counter reused keys belonging to retained buses.
    /// One shared handler then delivered callbacks from both buses, even after owner cleanup.
    /// </remarks>
    [TestFixture]
#if !UNITY_2021_3_OR_NEWER
    [NonParallelizable]
#endif
    public sealed class BusIdentityResetTests
    {
        private static readonly InstanceId Context = new InstanceId(947);

        private static IEnumerable<(MessageScenario Scenario, bool Fast)> RegistrationShapes()
        {
            foreach (
                MessageScenario scenario in MessageScenarios.WithAndWithoutPostProcessorIncludingWithoutContext
            )
            {
                foreach (bool fast in new[] { false, true })
                {
                    // The untargeted post API has only the readonly-reference delegate shape.
                    if (
                        scenario.Kind == MessageKind.Untargeted
                        && scenario.UsePostProcessor
                        && !fast
                    )
                    {
                        continue;
                    }
                    yield return (scenario, fast);
                }
            }
        }

        private static IEnumerable<TestCaseData> IsolationCases()
        {
            foreach ((MessageScenario scenario, bool fast) in RegistrationShapes())
            {
                foreach (bool useDefault in new[] { false, true })
                {
                    foreach (bool sharedHandler in new[] { false, true })
                    {
                        yield return new TestCaseData(scenario, fast, useDefault, sharedHandler);
                    }
                }
            }
        }

        [TestCaseSource(nameof(IsolationCases))]
        public void ResetKeepsCallbackOrderAndCleanupIsolated(
            MessageScenario scenario,
            bool fast,
            bool useDefault,
            bool sharedHandler
        )
        {
            using ResetScope scope = new ResetScope();
            scope.CreatePair(useDefault, out MessageBus first, out MessageBus second);
            using LeakWatcher firstLeaks = new LeakWatcher(first);
            using LeakWatcher secondLeaks = new LeakWatcher(second);
            MessageHandler firstHandler = new MessageHandler(new InstanceId(948), first)
            {
                active = true,
            };
            MessageHandler secondHandler = sharedHandler
                ? firstHandler
                : new MessageHandler(new InstanceId(949), second) { active = true };
            using MessageRegistrationToken firstToken = MessageRegistrationToken.Create(
                firstHandler,
                first
            );
            using MessageRegistrationToken secondToken = MessageRegistrationToken.Create(
                secondHandler,
                second
            );
            List<string> callbacks = new List<string>();
            List<string> observations = new List<string>();
            MessageRegistrationHandle firstRegistration = Register(
                firstToken,
                scenario,
                fast,
                () => callbacks.Add("A0"),
                -7
            );
            Register(firstToken, scenario, fast, () => callbacks.Add("A1"), 4);
            Register(secondToken, scenario, fast, () => callbacks.Add("B0"), -7);
            Register(secondToken, scenario, fast, () => callbacks.Add("B1"), 4);
            firstToken.Enable();
            secondToken.Enable();
            void Observe(string label, IMessageBus bus)
            {
                callbacks.Clear();
                Emit(scenario, bus);
                observations.Add(label + ":" + string.Join(",", callbacks));
            }
            Observe("A", first);
            Observe("B", second);
            firstToken.RemoveRegistration(firstRegistration);
            Observe("A-one", first);
            Observe("B-after-remove", second);
            firstToken.Dispose();
            Observe("B-after-dispose", second);
            first.Trim(force: true);
            Observe("B-after-trim", second);
            secondToken.Dispose();
            Observe("B-empty", second);
            string label =
                $"[{scenario}, fast={fast}, default={useDefault}, shared={sharedHandler}, indices={first.RegisteredGlobalSequentialIndex}/{second.RegisteredGlobalSequentialIndex}]";
            AssertAll(() =>
            {
                Assert.That(
                    second.RegisteredGlobalSequentialIndex,
                    Is.Not.EqualTo(first.RegisteredGlobalSequentialIndex),
                    label + " live bus keys must differ."
                );
                Assert.That(
                    observations,
                    Is.EqualTo(
                        new[]
                        {
                            "A:A0,A1",
                            "B:B0,B1",
                            "A-one:A1",
                            "B-after-remove:B0,B1",
                            "B-after-dispose:B0,B1",
                            "B-after-trim:B0,B1",
                            "B-empty:",
                        }
                    ),
                    label + " callbacks, priority order and cleanup must stay local to each bus."
                );
            });
        }

        [Test]
        public void ResetKeepsGlobalCallbacksIsolated(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(false, true)] bool fast,
            [Values(false, true)] bool useDefault
        )
        {
            using ResetScope scope = new ResetScope();
            scope.CreatePair(useDefault, out MessageBus first, out MessageBus second);
            using LeakWatcher firstLeaks = new LeakWatcher(first);
            using LeakWatcher secondLeaks = new LeakWatcher(second);
            MessageHandler handler = new MessageHandler(new InstanceId(950)) { active = true };
            using MessageRegistrationToken firstToken = MessageRegistrationToken.Create(
                handler,
                first
            );
            using MessageRegistrationToken secondToken = MessageRegistrationToken.Create(
                handler,
                second
            );
            List<string> callbacks = new List<string>();
            RegisterGlobal(firstToken, fast, () => callbacks.Add("A"));
            RegisterGlobal(secondToken, fast, () => callbacks.Add("B"));
            firstToken.Enable();
            secondToken.Enable();
            Emit(scenario, first);
            string[] firstCalls = callbacks.ToArray();
            callbacks.Clear();
            Emit(scenario, second);
            string[] secondCalls = callbacks.ToArray();
            callbacks.Clear();
            firstToken.Dispose();
            first.Trim(force: true);
            Emit(scenario, second);
            string[] survivorCalls = callbacks.ToArray();
            secondToken.Dispose();
            string label = $"[{scenario}, fast={fast}, default={useDefault}]";
            AssertAll(() =>
            {
                Assert.That(
                    firstCalls,
                    Is.EqualTo(new[] { "A" }),
                    label + " first bus callback ownership."
                );
                Assert.That(
                    secondCalls,
                    Is.EqualTo(new[] { "B" }),
                    label + " second bus callback ownership."
                );
                Assert.That(
                    survivorCalls,
                    Is.EqualTo(new[] { "B" }),
                    label + " cleanup must preserve the other global observer."
                );
            });
        }

        [Test]
        public void ExistingCustomRegistrationsSurviveResetWithoutJoiningANewBus(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            using ResetScope scope = new ResetScope();
            MessageBus retained = scope.NewBus();
            MessageHandler handler = new MessageHandler(new InstanceId(951), retained)
            {
                active = true,
            };
            using LeakWatcher retainedLeaks = new LeakWatcher(retained);
            using MessageRegistrationToken oldToken = MessageRegistrationToken.Create(
                handler,
                retained
            );
            List<string> callbacks = new List<string>();
            Register(oldToken, scenario, true, () => callbacks.Add("old"), 0);
            oldToken.Enable();
            Emit(scenario, retained);
            string[] beforeReset = callbacks.ToArray();
            callbacks.Clear();
            MessageHandler.SetGlobalMessageBus(retained);
            DxMessagingStaticState.Reset();
            MessageBus replacement = scope.NewBus();
            using LeakWatcher replacementLeaks = new LeakWatcher(replacement);
            using MessageRegistrationToken newToken = MessageRegistrationToken.Create(
                handler,
                replacement
            );
            try
            {
                Register(newToken, scenario, true, () => callbacks.Add("new"), 0);
                newToken.Enable();
                Emit(scenario, retained);
                string[] retainedCalls = callbacks.ToArray();
                callbacks.Clear();
                Emit(scenario, replacement);
                string[] replacementCalls = callbacks.ToArray();
                oldToken.Dispose();
                callbacks.Clear();
                Emit(scenario, retained);
                string[] afterStaleCleanup = callbacks.ToArray();
                AssertAll(() =>
                {
                    Assert.That(
                        beforeReset,
                        Is.EqualTo(new[] { "old" }),
                        scenario + " pre-reset control."
                    );
                    Assert.That(
                        retainedCalls,
                        Is.EqualTo(new[] { "old" }),
                        scenario + " custom sinks must survive reset."
                    );
                    Assert.That(
                        replacementCalls,
                        Is.EqualTo(new[] { "new" }),
                        scenario + " a new bus must not share custom delegates."
                    );
                    Assert.That(
                        afterStaleCleanup,
                        Is.EqualTo(new[] { "old" }),
                        scenario + " pre-reset teardown must remain stale."
                    );
                });
            }
            finally
            {
                newToken.Dispose();
                // Public reset intentionally preserves custom sinks and invalidates old teardown.
                // This test owns the retained bus and clears it through its real internal reset.
                retained.ResetState();
            }
        }

        [Test]
        public void BusCreatedDuringResetRemainsSeparateFromTheDefaultBus(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            using ResetScope scope = new ResetScope();
            MessageBus original = (MessageBus)MessageHandler.MessageBus;
            using LeakWatcher originalLeaks = new LeakWatcher(original);
            MessageHandler handler = new MessageHandler(new InstanceId(952)) { active = true };
            using MessageRegistrationToken oldToken = MessageRegistrationToken.Create(
                handler,
                original
            );
            MessageRegistrationToken nestedToken = null;
            MessageBus nestedBus = null;
            List<string> callbacks = new List<string>();
            try
            {
                Register(
                    oldToken,
                    scenario,
                    true,
                    () =>
                    {
                        callbacks.Add("reset");
                        DxMessagingStaticState.Reset();
                        nestedBus = scope.NewBus();
                        nestedToken = MessageRegistrationToken.Create(handler, nestedBus);
                        Register(nestedToken, scenario, true, () => callbacks.Add("nested"), 0);
                        nestedToken.Enable();
                        Emit(scenario, nestedBus);
                    },
                    0
                );
                Register(oldToken, scenario, true, () => callbacks.Add("peer"), 1);
                oldToken.Enable();
                Emit(scenario, original);
                using MessageRegistrationToken current = MessageRegistrationToken.Create(
                    handler,
                    original
                );
                Register(current, scenario, true, () => callbacks.Add("current"), 0);
                current.Enable();
                oldToken.Dispose();
                Emit(scenario, original);
                Assert.That(
                    callbacks,
                    Is.EqualTo(new[] { "reset", "nested", "current" }),
                    scenario
                        + " reset must stop old peers and keep nested/new default routes separate."
                );
            }
            finally
            {
                nestedToken?.Dispose();
            }
        }

        [Test]
        public void RepeatedResetPreservesAllIssuedBusIndices()
        {
            using ResetScope scope = new ResetScope();
            HashSet<int> issued = new HashSet<int>
            {
                MessageHandler.MessageBus.RegisteredGlobalSequentialIndex,
            };
            for (int iteration = 0; iteration < 8; ++iteration)
            {
                MessageBus bus = scope.NewBus();
                Assert.That(
                    issued.Add(bus.RegisteredGlobalSequentialIndex),
                    Is.True,
                    $"Reset {iteration} reused bus index {bus.RegisteredGlobalSequentialIndex}."
                );
                DxMessagingStaticState.Reset();
            }
        }

        [Test]
        public void ConcurrentIndexReservationsAreUnique()
        {
            Func<int> reserve = IndexAllocator();
            int[] issued = new int[128];
            Parallel.For(0, issued.Length, index => issued[index] = reserve());
            AssertAll(() =>
            {
                Assert.That(
                    issued.All(index => 0 <= index),
                    Is.True,
                    "Reservations must not wrap negative."
                );
                Assert.That(
                    new HashSet<int>(issued).Count,
                    Is.EqualTo(issued.Length),
                    "Each concurrent reservation must own one unique index."
                );
            });
        }

        [Test]
        public void ExhaustedIndexAllocatorNeverWrapsOrIssuesAnotherIndex()
        {
            FieldInfo counter = typeof(IMessageBus).GetField(
                "GlobalSequentialIndex",
                BindingFlags.NonPublic | BindingFlags.Static
            );
            Assert.That(
                counter,
                Is.Not.Null,
                "The actual allocator counter must be available to this cold boundary test."
            );
            Func<int> reserve = IndexAllocator();
            int previous = (int)counter.GetValue(null);
            try
            {
                // Reserve identifiers only; no live bus is created with an injected index.
                counter.SetValue(null, int.MaxValue - 1);
                Assert.That(
                    reserve(),
                    Is.EqualTo(int.MaxValue),
                    "The last representable index is valid."
                );
                Assert.Throws<InvalidOperationException>(
                    () => reserve(),
                    "Exhaustion must fail before wrapping."
                );
                Assert.Throws<InvalidOperationException>(
                    () => reserve(),
                    "Exhaustion must stay terminal."
                );
                Assert.That(
                    counter.GetValue(null),
                    Is.EqualTo(int.MaxValue),
                    "A failed reservation must not change the exhausted counter."
                );
            }
            finally
            {
                counter.SetValue(null, previous);
            }
        }

        public enum LeaseResetMode
        {
            NoReset,
            ResetAutomaticOwners,
            ResetExplicitOwners,
        }

        private static IEnumerable<(
            MessageScenario Scenario,
            bool Fast,
            bool Global
        )> RegistrationFamilies()
        {
            foreach ((MessageScenario scenario, bool fast) in RegistrationShapes())
            {
                yield return (scenario, fast, false);
            }
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            {
                foreach (bool fast in new[] { false, true })
                {
                    yield return (scenario, fast, true);
                }
            }
        }

        private static IEnumerable<TestCaseData> LeaseCases()
        {
            foreach ((MessageScenario scenario, bool fast, bool global) in RegistrationFamilies())
            {
                foreach (
                    LeaseResetMode mode in new[]
                    {
                        LeaseResetMode.NoReset,
                        LeaseResetMode.ResetAutomaticOwners,
                        LeaseResetMode.ResetExplicitOwners,
                    }
                )
                {
                    foreach (bool activateBeforeReset in new[] { false, true })
                    {
                        yield return new TestCaseData(
                            scenario,
                            fast,
                            global,
                            mode,
                            activateBeforeReset
                        );
                    }
                }
            }
        }

        /// <remarks>
        /// 2026-10-19: resetting the synthetic owner counter made independent builder handlers
        /// compare equal on a preserved custom bus. Only the first registered lease received messages.
        /// An inactive lease also reserves its owner and must remain distinct when activated later.
        /// </remarks>
        [TestCaseSource(nameof(LeaseCases))]
        public void BuilderLeasesKeepCallbacksAndOwnersAcrossReset(
            MessageScenario scenario,
            bool fast,
            bool global,
            LeaseResetMode mode,
            bool activateBeforeReset
        )
        {
            using ResetScope scope = new ResetScope();
            MessageBus bus = scope.NewBus();
            using LeakWatcher leaks = new LeakWatcher(bus);
            MessageRegistrationBuilder builder = new MessageRegistrationBuilder(
                new FixedMessageBusProvider(bus)
            );
            List<string> callbacks = new List<string>();
            MessageRegistrationLease first = null;
            MessageRegistrationLease second = null;
            MessageRegistrationLease Build(string name, int explicitOwner, bool activate)
            {
                MessageRegistrationBuildOptions options = new MessageRegistrationBuildOptions
                {
                    PreferredMessageBus = bus,
                    ActivateOnBuild = activate,
                    Configure = token =>
                    {
                        if (global)
                        {
                            RegisterGlobal(token, fast, () => callbacks.Add(name));
                        }
                        else
                        {
                            Register(token, scenario, fast, () => callbacks.Add(name), 0);
                        }
                    },
                };
                if (mode == LeaseResetMode.ResetExplicitOwners)
                {
                    options.Owner = new InstanceId(explicitOwner);
                }
                return builder.Build(options);
            }
            try
            {
                first = Build("old", 953, activateBeforeReset);
                Emit(scenario, bus);
                string[] before = callbacks.ToArray();
                callbacks.Clear();
                MessageHandler.SetGlobalMessageBus(bus);
                if (mode != LeaseResetMode.NoReset)
                {
                    DxMessagingStaticState.Reset();
                }
                second = Build("new", 954, true);
                if (!activateBeforeReset)
                {
                    first.Activate();
                }
                Emit(scenario, bus);
                string[] both = callbacks.ToArray();
                int registrationsBeforeCleanup = leaks.Snapshot;
                callbacks.Clear();
                second.Dispose();
                int registrationsAfterSecond = leaks.Snapshot;
                Emit(scenario, bus);
                string[] survivor = callbacks.ToArray();
                first.Dispose();
                int registrationsAfterFirst = leaks.Snapshot;
                int expectedRetained =
                    activateBeforeReset && mode != LeaseResetMode.NoReset ? 1 : 0;
                string label =
                    $"[{scenario}, fast={fast}, global={global}, mode={mode}, active={activateBeforeReset}, owners={first.Owner.Id}/{second.Owner.Id}]";
                AssertAll(() =>
                {
                    Assert.That(
                        second.Owner,
                        Is.Not.EqualTo(first.Owner),
                        label + " independent leases must own distinct handlers."
                    );
                    Assert.That(
                        first.Handler.Equals(second.Handler),
                        Is.False,
                        label + " independent generated handlers must not compare equal."
                    );
                    Assert.That(
                        before,
                        Is.EqualTo(activateBeforeReset ? new[] { "old" } : Array.Empty<string>()),
                        label + " activation control before reset."
                    );
                    Assert.That(
                        both,
                        Is.EqualTo(
                            activateBeforeReset ? new[] { "old", "new" } : new[] { "new", "old" }
                        ),
                        label + " both leases must receive messages in activation order."
                    );
                    Assert.That(
                        survivor,
                        Is.EqualTo(new[] { "old" }),
                        label + " disposing the new lease must preserve the old lease."
                    );
                    Assert.That(
                        registrationsBeforeCleanup,
                        Is.EqualTo(2),
                        label + " both accepted leases must own one live registration."
                    );
                    Assert.That(
                        registrationsAfterSecond,
                        Is.EqualTo(1),
                        label
                            + " valid new-lease cleanup must release its registration before the owned-bus reset."
                    );
                    Assert.That(
                        registrationsAfterFirst,
                        Is.EqualTo(expectedRetained),
                        label
                            + " only intentionally stale pre-reset teardown may retain a registration."
                    );
                });
            }
            finally
            {
                second?.Dispose();
                first?.Dispose();
                // A public reset intentionally stales earlier custom-bus teardown. This test
                // owns the bus and explicitly reclaims its retained registrations afterward.
                bus.ResetState();
            }
        }

        [Test]
        public void RepeatedResetDoesNotReuseAutomaticallyGeneratedOwners()
        {
            using ResetScope scope = new ResetScope();
            MessageBus bus = scope.NewBus();
            MessageRegistrationBuilder builder = new MessageRegistrationBuilder();
            HashSet<InstanceId> issued = new HashSet<InstanceId>();
            for (int iteration = 0; iteration < 8; ++iteration)
            {
                using MessageRegistrationLease lease = builder.Build(
                    new MessageRegistrationBuildOptions { PreferredMessageBus = bus }
                );
                Assert.That(
                    issued.Add(lease.Owner),
                    Is.True,
                    $"Reset {iteration} reused synthetic owner {lease.Owner.Id}."
                );
                DxMessagingStaticState.Reset();
            }
        }

        [Test]
        public void ExhaustedSyntheticOwnersNeverWrapAndExplicitOwnersRemainAvailable()
        {
            using ResetScope scope = new ResetScope();
            MessageBus bus = scope.NewBus();
            MessageRegistrationBuilder builder = new MessageRegistrationBuilder();
            FieldInfo counter = typeof(MessageRegistrationBuilder).GetField(
                "_syntheticOwnerCounter",
                BindingFlags.NonPublic | BindingFlags.Static
            );
            Assert.That(
                counter,
                Is.Not.Null,
                "Inspect only the actual private allocator's cold boundary."
            );
            int previous = (int)counter.GetValue(null);
            MessageRegistrationLease last = null;
            MessageRegistrationBuildOptions automatic = new MessageRegistrationBuildOptions
            {
                PreferredMessageBus = bus,
            };
            try
            {
                // No callback is registered with an injected identifier; dispose all leases
                // before restoring the original allocator value.
                counter.SetValue(null, int.MinValue + 1);
                last = builder.Build(automatic);
                Assert.That(
                    last.Owner.Id,
                    Is.EqualTo(int.MinValue),
                    "The last negative synthetic owner is valid."
                );
                Assert.Throws<InvalidOperationException>(
                    () =>
                    {
                        using MessageRegistrationLease unexpected = builder.Build(automatic);
                    },
                    "Exhaustion must fail before wrapping into positive identifiers."
                );
                Assert.Throws<InvalidOperationException>(
                    () =>
                    {
                        using MessageRegistrationLease unexpected = builder.Build(automatic);
                    },
                    "An exhausted synthetic owner range must remain exhausted."
                );
                using MessageRegistrationLease explicitLease = builder.Build(
                    new MessageRegistrationBuildOptions
                    {
                        PreferredMessageBus = bus,
                        Owner = new InstanceId(955),
                    }
                );
                AssertAll(() =>
                {
                    Assert.That(
                        explicitLease.Owner.Id,
                        Is.EqualTo(955),
                        "Explicit owners do not consume the synthetic range."
                    );
                    Assert.That(
                        counter.GetValue(null),
                        Is.EqualTo(int.MinValue),
                        "Failed reservations and explicit owners must leave the exhausted counter unchanged."
                    );
                });
            }
            finally
            {
                last?.Dispose();
                counter.SetValue(null, previous);
            }
        }

        private static IEnumerable<TestCaseData> ReclamationCases()
        {
            foreach ((MessageScenario scenario, bool fast, bool global) in RegistrationFamilies())
            {
                foreach (bool emitBeforeReset in new[] { false, true })
                {
                    foreach (bool trimBeforeReset in new[] { false, true })
                    {
                        yield return new TestCaseData(
                            scenario,
                            fast,
                            global,
                            emitBeforeReset,
                            trimBeforeReset
                        );
                    }
                }
            }
        }

        /// <remarks>
        /// 2026-10-19: reset dropped dirty handlers after their last deregistration without
        /// clearing their typed state. Empty wrappers and frozen global callback lists survived;
        /// the first post-reset emission could reuse the old list instead of the fresh callback.
        /// </remarks>
        [TestCaseSource(nameof(ReclamationCases))]
        public void ResetReclaimsDeregisteredHandlerStateAndStartsFreshCallbacks(
            MessageScenario scenario,
            bool fast,
            bool global,
            bool emitBeforeReset,
            bool trimBeforeReset
        )
        {
            using ResetScope scope = new ResetScope();
            MessageBus bus = (MessageBus)MessageHandler.MessageBus;
            MessageHandler handler = new MessageHandler(new InstanceId(956), bus) { active = true };
            using LeakWatcher leaks = new LeakWatcher(bus);
            using MessageRegistrationToken oldToken = MessageRegistrationToken.Create(handler, bus);
            int oldCalls = 0;
            int newCalls = 0;
            void Old()
            {
                oldCalls++;
            }
            void New()
            {
                newCalls++;
            }
            if (global)
            {
                RegisterGlobal(oldToken, fast, Old);
            }
            else
            {
                Register(oldToken, scenario, fast, Old, 0);
            }
            oldToken.Enable();
            if (emitBeforeReset)
            {
                Emit(scenario, bus);
            }
            oldToken.Dispose();
            int registrationsAfterValidCleanup = leaks.Snapshot;
            if (trimBeforeReset)
            {
                bus.Trim(force: true);
            }
            DxMessagingStaticState.Reset();
            bus.Trim(force: true);
            bool hasTypedAfterReset = handler.HasTypedHandlersForBus(bus);
            int emptyAfterReset = handler.CountEmptyTypedSlotsForSweep(bus);
            using MessageRegistrationToken replacement = MessageRegistrationToken.Create(
                handler,
                bus
            );
            if (global)
            {
                RegisterGlobal(replacement, fast, New);
            }
            else
            {
                Register(replacement, scenario, fast, New, 0);
            }
            replacement.Enable();
            oldToken.Dispose();
            Emit(scenario, bus);
            replacement.Dispose();
            int registrationsAfterReplacement = leaks.Snapshot;
            for (int repeat = 0; repeat < 3; ++repeat)
            {
                DxMessagingStaticState.Reset();
                bus.Trim(force: true);
            }
            string label =
                $"[{scenario}, fast={fast}, global={global}, emit={emitBeforeReset}, trimFirst={trimBeforeReset}]";
            AssertAll(() =>
            {
                Assert.That(
                    registrationsAfterValidCleanup,
                    Is.Zero,
                    label + " valid old-token cleanup must complete before reset."
                );
                Assert.That(
                    hasTypedAfterReset,
                    Is.False,
                    label + " reset and forced trim must release the empty typed wrapper."
                );
                Assert.That(
                    emptyAfterReset,
                    Is.Zero,
                    label + " reset must not strand empty slots outside the dirty queue."
                );
                Assert.That(
                    oldCalls,
                    Is.EqualTo(emitBeforeReset ? 1 : 0),
                    label + " a disposed callback must not return after reset."
                );
                Assert.That(
                    newCalls,
                    Is.EqualTo(1),
                    label + " the replacement must receive the first post-reset emission."
                );
                Assert.That(
                    registrationsAfterReplacement,
                    Is.Zero,
                    label + " replacement cleanup must complete before maintenance."
                );
                Assert.That(
                    handler.HasTypedHandlersForBus(bus),
                    Is.False,
                    label + " repeated reset/trim must leave no wrapper."
                );
                Assert.That(
                    handler.CountEmptyTypedSlotsForSweep(bus),
                    Is.Zero,
                    label + " repeated reset/trim must leave no empty slot."
                );
            });
        }

        [Test]
        public void FinalGlobalDeregistrationThenNestedResetStopsPeersAndPreservesNewCallbacks(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(false, true)] bool fast,
            [Values(false, true)] bool throwAfterNested
        )
        {
            RunFinalGlobalDeregistrationThenNestedReset(scenario, fast, throwAfterNested, 2);
        }

        [Test]
        public void SpilledFinalGlobalDeregistrationThenNestedResetKeepsSnapshotOwnership(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(false, true)] bool fast,
            [Values(false, true)] bool throwAfterNested
        )
        {
            RunFinalGlobalDeregistrationThenNestedReset(scenario, fast, throwAfterNested, 8);
        }

        private static void RunFinalGlobalDeregistrationThenNestedReset(
            MessageScenario scenario,
            bool fast,
            bool throwAfterNested,
            int callbackCount
        )
        {
            using ResetScope scope = new ResetScope();
            MessageBus bus = (MessageBus)MessageHandler.MessageBus;
            using LeakWatcher leaks = new LeakWatcher(bus);
            MessageHandler handler = new MessageHandler(new InstanceId(957), bus) { active = true };
            using MessageRegistrationToken oldToken = MessageRegistrationToken.Create(handler, bus);
            MessageRegistrationToken replacement = null;
            List<string> events = new List<string>();
            InvalidOperationException failure = new InvalidOperationException(
                "after nested reset emission"
            );
            int firstCalls = 0;
            int registrationsBeforeReset = -1;
            try
            {
                RegisterGlobal(
                    oldToken,
                    fast,
                    () =>
                    {
                        events.Add("old");
                        firstCalls++;
                        if (firstCalls != 1)
                        {
                            return;
                        }
                        oldToken.Dispose();
                        registrationsBeforeReset = leaks.Snapshot;
                        DxMessagingStaticState.Reset();
                        replacement = MessageRegistrationToken.Create(handler, bus);
                        RegisterGlobal(replacement, fast, () => events.Add("new"));
                        replacement.Enable();
                        Emit(scenario, bus);
                        if (throwAfterNested)
                        {
                            throw failure;
                        }
                    }
                );
                for (int peer = 1; peer < callbackCount; ++peer)
                {
                    int peerIndex = peer;
                    RegisterGlobal(
                        oldToken,
                        fast,
                        () => events.Add(callbackCount == 2 ? "peer" : "peer" + peerIndex)
                    );
                }
                oldToken.Enable();
                if (throwAfterNested)
                {
                    InvalidOperationException observed = Assert.Throws<InvalidOperationException>(
                        () =>
                            Emit(scenario, bus)
                    );
                    Assert.That(
                        observed,
                        Is.SameAs(failure),
                        scenario + " the original callback exception must propagate."
                    );
                }
                else
                {
                    Emit(scenario, bus);
                }
                Emit(scenario, bus);
                replacement.Dispose();
                int registrationsAfterReplacement = leaks.Snapshot;
                bus.Trim(force: true);
                AssertAll(() =>
                {
                    Assert.That(
                        registrationsBeforeReset,
                        Is.Zero,
                        scenario + " final deregistration must precede reset."
                    );
                    Assert.That(
                        events,
                        Is.EqualTo(new[] { "old", "new", "new" }),
                        scenario
                            + " nested reset must stop old peers and preserve new emissions after normal/exception exits."
                    );
                    Assert.That(
                        registrationsAfterReplacement,
                        Is.Zero,
                        scenario + " valid replacement teardown must complete before trim."
                    );
                    Assert.That(
                        handler.HasTypedHandlersForBus(bus),
                        Is.False,
                        scenario + " the final forced trim must release new typed state."
                    );
                });
            }
            finally
            {
                replacement?.Dispose();
                oldToken.Dispose();
                bus.Trim(force: true);
            }
        }

        private static void AssertAll(Action assertions)
        {
#if UNITY_2021_3_OR_NEWER
            assertions();
#else
            Assert.Multiple(assertions);
#endif
        }

        private static Func<int> IndexAllocator()
        {
            MethodInfo method = typeof(IMessageBus).GetMethod(
                "GenerateNewGlobalSequentialIndex",
                BindingFlags.NonPublic | BindingFlags.Static
            );
            Assert.That(
                method,
                Is.Not.Null,
                "Test the real protected allocator without adding a production hook."
            );
            return (Func<int>)method.CreateDelegate(typeof(Func<int>));
        }

        private static MessageRegistrationHandle Register(
            MessageRegistrationToken token,
            MessageScenario scenario,
            bool fast,
            Action callback,
            int priority
        )
        {
            MessageHandler.FastHandler<ProbeMessage> readonlyCallback = (in ProbeMessage _) =>
                callback();
            Action<ProbeMessage> action = _ => callback();
            MessageHandler.FastHandlerWithContext<ProbeMessage> readonlyContext = (
                in InstanceId _,
                in ProbeMessage __
            ) => callback();
            Action<InstanceId, ProbeMessage> contextAction = (_, __) => callback();
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                    if (scenario.UsePostProcessor)
                    {
                        return ScenarioHarness.RegisterUntargetedPostProcessor(
                            scenario,
                            token,
                            readonlyCallback,
                            priority
                        );
                    }
                    return fast
                        ? ScenarioHarness.RegisterUntargeted(
                            scenario,
                            token,
                            readonlyCallback,
                            priority
                        )
                        : token.RegisterUntargeted(action, priority);
                case MessageKind.Targeted:
                    if (scenario.UsePostProcessor)
                    {
                        return fast
                            ? ScenarioHarness.RegisterTargetedPostProcessor(
                                scenario,
                                token,
                                Context,
                                readonlyCallback,
                                priority
                            )
                            : token.RegisterTargetedPostProcessor(Context, action, priority);
                    }
                    return fast
                        ? ScenarioHarness.RegisterTargeted(
                            scenario,
                            token,
                            Context,
                            readonlyCallback,
                            priority
                        )
                        : token.RegisterTargeted(Context, action, priority);
                case MessageKind.Broadcast:
                    if (scenario.UsePostProcessor)
                    {
                        return fast
                            ? ScenarioHarness.RegisterBroadcastPostProcessor(
                                scenario,
                                token,
                                Context,
                                readonlyCallback,
                                priority
                            )
                            : token.RegisterBroadcastPostProcessor(Context, action, priority);
                    }
                    return fast
                        ? ScenarioHarness.RegisterBroadcast(
                            scenario,
                            token,
                            Context,
                            readonlyCallback,
                            priority
                        )
                        : token.RegisterBroadcast(Context, action, priority);
                case MessageKind.TargetedWithoutTargeting:
                    if (scenario.UsePostProcessor)
                    {
                        return fast
                            ? token.RegisterTargetedWithoutTargetingPostProcessor(
                                readonlyContext,
                                priority
                            )
                            : token.RegisterTargetedWithoutTargetingPostProcessor(
                                contextAction,
                                priority
                            );
                    }
                    return fast
                        ? token.RegisterTargetedWithoutTargeting(readonlyContext, priority)
                        : token.RegisterTargetedWithoutTargeting(contextAction, priority);
                case MessageKind.BroadcastWithoutSource:
                    if (scenario.UsePostProcessor)
                    {
                        return fast
                            ? token.RegisterBroadcastWithoutSourcePostProcessor(
                                readonlyContext,
                                priority
                            )
                            : token.RegisterBroadcastWithoutSourcePostProcessor(
                                contextAction,
                                priority
                            );
                    }
                    return fast
                        ? token.RegisterBroadcastWithoutSource(readonlyContext, priority)
                        : token.RegisterBroadcastWithoutSource(contextAction, priority);
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario));
            }
        }

        private static void RegisterGlobal(
            MessageRegistrationToken token,
            bool fast,
            Action callback
        )
        {
            if (fast)
            {
                token.RegisterGlobalAcceptAll(
                    (in IUntargetedMessage _) => callback(),
                    (in InstanceId _, in ITargetedMessage __) => callback(),
                    (in InstanceId _, in IBroadcastMessage __) => callback()
                );
            }
            else
            {
                token.RegisterGlobalAcceptAll(
                    _ => callback(),
                    (_, __) => callback(),
                    (_, __) => callback()
                );
            }
        }

        private static void Emit(MessageScenario scenario, IMessageBus bus)
        {
            ProbeMessage message = new ProbeMessage();
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
                case MessageKind.TargetedWithoutTargeting:
                    ScenarioHarness.EmitTargeted(
                        MessageScenario.Targeted(),
                        ref message,
                        Context,
                        bus
                    );
                    break;
                case MessageKind.BroadcastWithoutSource:
                    ScenarioHarness.EmitBroadcast(
                        MessageScenario.Broadcast(),
                        ref message,
                        Context,
                        bus
                    );
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario));
            }
        }

        private readonly struct ProbeMessage
            : IUntargetedMessage,
                ITargetedMessage,
                IBroadcastMessage
        {
            public Type MessageType => typeof(ProbeMessage);
        }

        private sealed class ResetScope : IDisposable
        {
            private readonly IMessageBus _previousBus = MessageHandler.MessageBus;
            private readonly DiagnosticsTarget _targets = IMessageBus.GlobalDiagnosticsTargets;
            private readonly bool _stacks = IMessageBus.GlobalDiagnosticsStackTraces;
            private readonly int _buffer = IMessageBus.GlobalMessageBufferSize;
            private readonly bool _debug = MessagingDebug.enabled;
            private readonly Action<LogLevel, string> _log = MessagingDebug.LogFunction;
            private readonly List<MessageBus> _owned = new List<MessageBus>();

            public ResetScope()
            {
                DxMessagingStaticState.Reset();
                DxMessagingStaticState.Reset();
                IMessageBus.GlobalDiagnosticsTargets = DiagnosticsTarget.Off;
                MessagingDebug.enabled = false;
            }

            public MessageBus NewBus()
            {
                MessageBus bus = new MessageBus();
                _owned.Add(bus);
                return bus;
            }

            public void CreatePair(bool useDefault, out MessageBus first, out MessageBus second)
            {
                first = useDefault ? (MessageBus)MessageHandler.MessageBus : NewBus();
                if (!useDefault)
                {
                    MessageHandler.SetGlobalMessageBus(first);
                    DxMessagingStaticState.Reset();
                }
                second = NewBus();
            }

            public void Dispose()
            {
                foreach (MessageBus bus in _owned)
                {
                    bus.ResetState();
                }
                DxMessagingStaticState.Reset();
                MessageHandler.SetGlobalMessageBus(_previousBus);
                IMessageBus.GlobalDiagnosticsTargets = _targets;
                IMessageBus.GlobalDiagnosticsStackTraces = _stacks;
                IMessageBus.GlobalMessageBufferSize = _buffer;
                MessagingDebug.enabled = _debug;
                MessagingDebug.LogFunction = _log;
            }
        }
    }
}
