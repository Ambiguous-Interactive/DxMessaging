namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Runtime;
    using NUnit.Framework;
    using BusType = DxMessaging.Core.MessageBus.MessageBus;

    /// <summary>Probes actual nested teardown through custom bus callbacks.</summary>
    /// <remarks>2026-10-18: Preserve outer cleanup ownership when a custom bus reenters token teardown.</remarks>
    [TestFixture]
    public sealed class TokenTeardownReentrancyTests
    {
        private static readonly InstanceId Context = new InstanceId(841);

        private static IEnumerable<(MessageScenario Scenario, bool Action, int Family)> Shapes()
        {
            foreach (
                MessageScenario scenario in MessageScenarios.WithAndWithoutPostProcessorIncludingWithoutContext
            )
            {
                foreach (bool action in new[] { false, true })
                {
                    if (
                        scenario.Kind == MessageKind.Untargeted
                        && scenario.UsePostProcessor
                        && action
                    )
                    {
                        continue;
                    }
                    yield return (scenario, action, 0);
                }
            }
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            {
                yield return (scenario, false, 1);
                yield return (scenario, false, 2);
                yield return (scenario, true, 2);
            }
        }

        private static IEnumerable<TestCaseData> Cases()
        {
            foreach ((MessageScenario Scenario, bool Action, int Family) shape in Shapes())
            {
                foreach (bool diagnostics in new[] { false, true })
                {
                    foreach (int count in new[] { 2, 8 })
                    {
                        foreach (bool outerClear in new[] { false, true })
                        {
                            foreach (int mutation in new[] { 0, 1, 2, 3 })
                            {
                                yield return new TestCaseData(
                                    shape.Scenario,
                                    shape.Action,
                                    shape.Family,
                                    diagnostics,
                                    count,
                                    outerClear,
                                    mutation
                                ).SetName(
                                    "Teardown_family"
                                        + shape.Family
                                        + "_"
                                        + shape.Scenario
                                        + "_action"
                                        + shape.Action
                                        + "_diagnostics"
                                        + diagnostics
                                        + "_count"
                                        + count
                                        + "_clear"
                                        + outerClear
                                        + "_mutation"
                                        + mutation
                                );
                            }
                        }
                    }
                }
            }
        }

        [TestCaseSource(nameof(Cases))]
        public void NestedCleanupPreservesOuterOwnership(
            MessageScenario scenario,
            bool action,
            int family,
            bool diagnostics,
            int count,
            bool outerClear,
            int mutation
        )
        {
            string label =
                scenario
                + ",action="
                + action
                + ",family="
                + family
                + ",diagnostics="
                + diagnostics
                + ",count="
                + count
                + ",outerClear="
                + outerClear
                + ",mutation="
                + mutation;
            using CaseState state = new CaseState(diagnostics);
            for (int index = 0; index < count; ++index)
            {
                string name = "handler-" + index;
                _ = Register(state.Token, scenario, action, family, () => state.Calls.Add(name));
            }
            state.Token.Enable();
            state.Emit(scenario);
            Assert.That(state.Calls.Count, Is.EqualTo(count), label + ": control dispatch");
            state.Calls.Clear();
            int mutations = 0;
            state.Bus.OnFirstDeregistration = () =>
            {
                ++mutations;
                if (mutation == 1)
                {
                    state.Token.Disable();
                }
                else if (mutation == 2)
                {
                    state.Token.UnregisterAll();
                }
                else if (mutation == 3)
                {
                    state.Token.Dispose();
                }
            };
            Exception observed = null;
            try
            {
                if (outerClear)
                {
                    state.Token.UnregisterAll();
                }
                else
                {
                    state.Token.Disable();
                }
            }
            catch (Exception exception)
            {
                observed = exception;
            }
            state.Emit(scenario);
            TestContext.WriteLine(
                label
                    + "; exception="
                    + observed
                    + "; enabled="
                    + state.Token.Enabled
                    + "; metadata="
                    + state.Token._metadata.Count
                    + "; after="
                    + string.Join(",", state.Calls)
            );
            Assert.That(
                observed,
                Is.Null,
                label + ": cleanup must not throw an implementation collection exception"
            );
            Assert.That(mutations, Is.EqualTo(1), label + ": one-shot mutation");
            Assert.That(state.CleanupCount, Is.Zero, label + ": cleanup count returns to zero");
            Assert.That(
                state.Bus.Attempts.Count,
                Is.EqualTo(count),
                label + ": exactly one attempt per accepted handle"
            );
            Assert.That(
                state.Bus.Attempts,
                Is.Unique,
                label + ": in-flight cleanup must not be reentered"
            );
            Assert.That(state.Token.Enabled, Is.False, label + ": accepted teardown completes");
            Assert.That(state.Calls, Is.Empty, label + ": no cleaned registration dispatches");
            bool cleared = outerClear || 2 <= mutation;
            Assert.That(
                state.Token._metadata.Count,
                Is.EqualTo(cleared ? 0 : count),
                label + ": staged survivors"
            );
            state.Token.Enable();
            state.Emit(scenario);
            Assert.That(
                state.Calls.Count,
                Is.EqualTo(cleared ? 0 : count),
                label + ": next enable replays each surviving staged entry once"
            );
            state.Token.UnregisterAll();
        }

        private static IEnumerable<TestCaseData> FailureCases() =>
            CasesWithOptions(new object[] { false, true }, new object[] { 1, 2, 3 });

        private static IEnumerable<TestCaseData> AddedCases() =>
            CasesWithOptions(new object[] { false, true });

        private static IEnumerable<TestCaseData> OverlapCases() =>
            CasesWithOptions(new object[] { false, true }, new object[] { false, true });

        private static IEnumerable<TestCaseData> CasesWithOptions(
            object[] firstOptions,
            object[] secondOptions = null
        )
        {
            foreach ((MessageScenario Scenario, bool Action, int Family) shape in Shapes())
            {
                foreach (bool diagnostics in new[] { false, true })
                {
                    foreach (object first in firstOptions)
                    {
                        if (secondOptions == null)
                        {
                            yield return new TestCaseData(
                                shape.Scenario,
                                shape.Action,
                                shape.Family,
                                diagnostics,
                                first
                            );
                        }
                        else
                        {
                            foreach (object second in secondOptions)
                            {
                                yield return new TestCaseData(
                                    shape.Scenario,
                                    shape.Action,
                                    shape.Family,
                                    diagnostics,
                                    first,
                                    second
                                );
                            }
                        }
                    }
                }
            }
        }

        [TestCaseSource(nameof(FailureCases))]
        public void NestedCleanupFailureKeepsOriginalIdentityRetryable(
            MessageScenario scenario,
            bool action,
            int family,
            bool diagnostics,
            bool outerClear,
            int mutation
        )
        {
            string label =
                scenario
                + ",action="
                + action
                + ",family="
                + family
                + ",diagnostics="
                + diagnostics
                + ",outerClear="
                + outerClear
                + ",mutation="
                + mutation;
            using CaseState state = new CaseState(diagnostics);
            _ = Register(state.Token, scenario, action, family, () => state.Calls.Add("first"));
            _ = Register(state.Token, scenario, action, family, () => state.Calls.Add("second"));
            state.Token.Enable();
            state.Bus.RejectNext = true;
            state.Bus.OnFirstDeregistration = () =>
            {
                if (mutation == 1)
                {
                    state.Token.Disable();
                }
                else if (mutation == 2)
                {
                    state.Token.UnregisterAll();
                }
                else
                {
                    state.Token.Dispose();
                }
            };
            Exception observed = Capture(() =>
            {
                if (outerClear)
                {
                    state.Token.UnregisterAll();
                }
                else
                {
                    state.Token.Disable();
                }
            });
            TestContext.WriteLine(
                label + "; observed=" + observed + "; cleanup=" + state.CleanupCount
            );
            Assert.That(
                observed,
                Is.SameAs(state.Bus.Failure),
                label + ": preserve the original rejected teardown"
            );
            Assert.That(
                state.Token.Enabled,
                Is.True,
                label + ": pending cleanup keeps ownership active"
            );
            Assert.That(
                state.CleanupCount,
                Is.EqualTo(1),
                label + ": exactly the rejected in-flight cleanup remains"
            );
            Assert.That(
                state.Bus.Attempts.Count,
                Is.EqualTo(2),
                label + ": each original handle attempted once"
            );
            Assert.That(
                state.Bus.Attempts,
                Is.Unique,
                label + ": nested cleanup cannot invoke the in-flight action again"
            );
            state.Emit(scenario);
            Assert.That(
                state.Calls,
                Is.EqualTo(new[] { "first" }),
                label + ": only the rejected registration stays live"
            );
            int expectedStaged =
                2 <= mutation ? 0
                : outerClear ? 1
                : 2;
            Assert.That(
                state.Token._metadata.Count,
                Is.EqualTo(expectedStaged),
                label + ": preserve only requested staging"
            );
            state.Bus.Attempts.Clear();
            if (outerClear)
            {
                state.Token.UnregisterAll();
            }
            else
            {
                state.Token.Disable();
            }
            Assert.That(
                state.Bus.Attempts.Count,
                Is.EqualTo(1),
                label + ": retry through owning cleanup API"
            );
            Assert.That(
                state.CleanupCount,
                Is.Zero,
                label + ": retry consumes exactly one retained cleanup"
            );
            state.Calls.Clear();
            state.Emit(scenario);
            Assert.That(state.Calls, Is.Empty, label + ": retry removes the live registration");
            state.Token.Enable();
            state.Emit(scenario);
            Assert.That(
                state.Calls.Count,
                Is.EqualTo(outerClear ? 0 : expectedStaged),
                label + ": staged survivors replay exactly once"
            );
            state.Token.UnregisterAll();
        }

        [TestCaseSource(nameof(AddedCases))]
        public void AcceptedReentrantRegistrationRetainsCleanupForNextPass(
            MessageScenario scenario,
            bool action,
            int family,
            bool diagnostics,
            bool outerClear
        )
        {
            string label =
                scenario
                + ",action="
                + action
                + ",family="
                + family
                + ",diagnostics="
                + diagnostics
                + ",outerClear="
                + outerClear;
            using CaseState state = new CaseState(diagnostics);
            _ = Register(state.Token, scenario, action, family, () => state.Calls.Add("old"));
            state.Token.Enable();
            MessageRegistrationHandle added = default;
            state.Bus.OnFirstDeregistration = () =>
            {
                added = Register(
                    state.Token,
                    scenario,
                    action,
                    family,
                    () => state.Calls.Add("added")
                );
            };
            if (outerClear)
            {
                state.Token.UnregisterAll();
            }
            else
            {
                state.Token.Disable();
            }
            Assert.That(
                state.Token.Enabled,
                Is.True,
                label + ": accepted reentrant registration keeps cleanup active"
            );
            Assert.That(state.CleanupCount, Is.EqualTo(1), label + ": one deferred cleanup");
            Assert.That(
                state.Token._metadata.Count,
                Is.EqualTo(outerClear ? 1 : 2),
                label + ": retain staged ownership of the accepted addition"
            );
            state.Emit(scenario);
            Assert.That(
                state.Calls,
                Is.EqualTo(new[] { "added" }),
                label + ": the new registration is live"
            );
            state.Token.RemoveRegistration(added);
            Assert.That(
                state.CleanupCount,
                Is.Zero,
                label + ": returned handle owns accepted cleanup"
            );
            state.Calls.Clear();
            state.Emit(scenario);
            Assert.That(state.Calls, Is.Empty, label + ": removed addition stays absent");
            state.Token.UnregisterAll();
        }

        [TestCaseSource(nameof(OverlapCases))]
        public void OverlappingCleanupPreservesOriginalBeforeReenabledRegistration(
            MessageScenario scenario,
            bool action,
            int family,
            bool diagnostics,
            bool removeHandle,
            bool reject
        )
        {
            string label =
                scenario
                + ",action="
                + action
                + ",family="
                + family
                + ",diagnostics="
                + diagnostics
                + ",remove="
                + removeHandle
                + ",reject="
                + reject;
            using CaseState state = new CaseState(diagnostics);
            MessageRegistrationHandle handle = Register(
                state.Token,
                scenario,
                action,
                family,
                () => state.Calls.Add("live")
            );
            state.Token.Enable();
            state.Bus.RejectNext = reject;
            state.Bus.OnFirstDeregistration = () =>
            {
                state.Token.Disable();
                state.Token.Enable();
            };
            Exception observed = Capture(() =>
            {
                if (removeHandle)
                {
                    state.Token.RemoveRegistration(handle);
                }
                else
                {
                    state.Token.Disable();
                }
            });
            Assert.That(
                observed,
                reject ? Is.SameAs(state.Bus.Failure) : Is.Null,
                label + ": original failure or successful cleanup"
            );
            Assert.That(
                state.Bus.Attempts.Count,
                Is.EqualTo(1),
                label + ": nested cleanup excludes the current action"
            );
            MessageBusRegistration original = state.Bus.Attempts[0];
            Assert.That(
                state.CleanupCount,
                Is.EqualTo(1),
                label + ": one owned handle, including an overlapping holder"
            );
            Assert.That(
                state.Token._metadata.Count,
                Is.EqualTo(removeHandle && !reject ? 0 : 1),
                label + ": successful removal drops staging only"
            );
            state.Emit(scenario);
            Assert.That(
                state.Calls,
                Is.EqualTo(new[] { "live" }),
                label + ": duplicate registration refcounts dispatch once"
            );
            state.Bus.Attempts.Clear();
            state.Token.UnregisterAll();
            Assert.That(
                state.Bus.Attempts.Count,
                Is.EqualTo(reject ? 2 : 1),
                label + ": every remaining accepted handle is consumed"
            );
            Assert.That(state.Bus.Attempts, Is.Unique, label + ": cleanup handles remain distinct");
            if (reject)
            {
                Assert.That(
                    state.Bus.Attempts[0],
                    Is.EqualTo(original),
                    label + ": failed original precedes reenabled cleanup"
                );
            }
            Assert.That(state.CleanupCount, Is.Zero, label + ": all overlap ownership consumed");
            state.Calls.Clear();
            state.Emit(scenario);
            Assert.That(state.Calls, Is.Empty, label + ": no overlapping registration leaks");
        }

        [TestCaseSource(nameof(OverlapCases))]
        public void SlotReusePreservesFailedOrphanAndReplacementCleanup(
            MessageScenario scenario,
            bool action,
            int family,
            bool diagnostics,
            bool removeHandle,
            bool reject
        )
        {
            string label =
                scenario
                + ",action="
                + action
                + ",family="
                + family
                + ",diagnostics="
                + diagnostics
                + ",remove="
                + removeHandle
                + ",reject="
                + reject;
            using CaseState state = new CaseState(diagnostics);
            MessageRegistrationHandle original = Register(
                state.Token,
                scenario,
                action,
                family,
                () => state.Calls.Add("original")
            );
            MessageRegistrationHandle replacement = default;
            state.Token.Enable();
            state.Bus.RejectNext = reject;
            state.Bus.OnFirstDeregistration = () =>
            {
                state.Token.UnregisterAll();
                replacement = Register(
                    state.Token,
                    scenario,
                    action,
                    family,
                    () => state.Calls.Add("replacement")
                );
                state.Token.Enable();
            };
            Exception observed = Capture(() =>
            {
                if (removeHandle)
                {
                    state.Token.RemoveRegistration(original);
                }
                else
                {
                    state.Token.Disable();
                }
            });
            Assert.That(
                observed,
                reject ? Is.SameAs(state.Bus.Failure) : Is.Null,
                label + ": preserve cleanup outcome"
            );
            Assert.That(replacement.Slot, Is.EqualTo(original.Slot), label + ": actual slot reuse");
            Assert.That(
                replacement,
                Is.Not.EqualTo(original),
                label + ": distinct opaque identity"
            );
            Assert.That(
                state.Token._metadata.Count,
                Is.EqualTo(1),
                label + ": replacement staging survives"
            );
            Assert.That(
                state.CleanupCount,
                Is.EqualTo(reject ? 2 : 1),
                label + ": failed original becomes an independent orphan"
            );
            state.Emit(scenario);
            Assert.That(
                state.Calls,
                Is.EqualTo(reject ? new[] { "original", "replacement" } : new[] { "replacement" }),
                label + ": exact accepted callbacks"
            );
            state.Token.RemoveRegistration(original);
            state.Token.RemoveRegistration(original);
            Assert.That(
                state.CleanupCount,
                Is.EqualTo(1),
                label + ": retry and stale copy preserve replacement"
            );
            state.Calls.Clear();
            state.Emit(scenario);
            Assert.That(
                state.Calls,
                Is.EqualTo(new[] { "replacement" }),
                label + ": original handle cannot remove replacement"
            );
            state.Token.RemoveRegistration(replacement);
            Assert.That(
                state.CleanupCount,
                Is.Zero,
                label + ": replacement cleanup remains reachable"
            );
            state.Calls.Clear();
            state.Emit(scenario);
            Assert.That(state.Calls, Is.Empty, label + ": both original and replacement cleaned");
        }

        private static IEnumerable<TestCaseData> BasicCases()
        {
            foreach ((MessageScenario Scenario, bool Action, int Family) shape in Shapes())
            {
                foreach (bool diagnostics in new[] { false, true })
                {
                    yield return new TestCaseData(
                        shape.Scenario,
                        shape.Action,
                        shape.Family,
                        diagnostics
                    );
                }
            }
        }

        [TestCaseSource(nameof(BasicCases))]
        public void FailedDirectAndOrphanRemovalKeepTokenEnabledForRetry(
            MessageScenario scenario,
            bool action,
            int family,
            bool diagnostics
        )
        {
            string label =
                scenario
                + ",action="
                + action
                + ",family="
                + family
                + ",diagnostics="
                + diagnostics;
            using CaseState state = new CaseState(diagnostics);
            MessageRegistrationHandle handle = Register(
                state.Token,
                scenario,
                action,
                family,
                () => state.Calls.Add("retained")
            );
            state.Token.Enable();
            for (int attempt = 0; attempt < 2; ++attempt)
            {
                state.Bus.RejectNext = true;
                state.Bus.OnFirstDeregistration = state.Token.UnregisterAll;
                Exception observed = Capture(() => state.Token.RemoveRegistration(handle));
                Assert.That(
                    observed,
                    Is.SameAs(state.Bus.Failure),
                    label + ": direct/orphan rejection identity, attempt=" + attempt
                );
                Assert.That(
                    state.Token.Enabled,
                    Is.True,
                    label + ": accepted failed cleanup keeps token enabled, attempt=" + attempt
                );
                Assert.That(
                    state.CleanupCount,
                    Is.EqualTo(1),
                    label + ": one retryable orphan, attempt=" + attempt
                );
                Assert.That(
                    state.Token._metadata.Count,
                    Is.Zero,
                    label + ": nested clear removed staging, attempt=" + attempt
                );
            }
            state.Emit(scenario);
            Assert.That(
                state.Calls,
                Is.EqualTo(new[] { "retained" }),
                label + ": failed original remains live"
            );
            state.Token.RemoveRegistration(handle);
            state.Token.Disable();
            Assert.That(
                state.CleanupCount,
                Is.Zero,
                label + ": successful owner retry consumes orphan"
            );
            Assert.That(
                state.Token.Enabled,
                Is.False,
                label + ": completed cleanup disables token"
            );
            state.Calls.Clear();
            state.Emit(scenario);
            Assert.That(state.Calls, Is.Empty, label + ": final retry removes accepted callback");
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

        private static MessageRegistrationHandle Register(
            MessageRegistrationToken token,
            MessageScenario scenario,
            bool action,
            int family,
            Action record
        )
        {
            return family switch
            {
                0 => RegisterHandler(token, scenario, action, record),
                1 => RegisterInterceptor(token, scenario, record),
                2 => RegisterGlobal(token, action, record),
                _ => throw new ArgumentOutOfRangeException(nameof(family)),
            };
        }

        private sealed class CaseState : IDisposable
        {
            private static readonly FieldInfo CleanupCountField =
                typeof(MessageRegistrationToken).GetField(
                    "_deregistrationCount",
                    BindingFlags.Instance | BindingFlags.NonPublic
                );
            internal int CleanupCount => (int)CleanupCountField.GetValue(Token);
            internal readonly BusType Inner;
            internal readonly ReplayBus Bus;
            internal readonly MessageHandler Handler;
            internal readonly MessageRegistrationToken Token;
            internal readonly List<string> Calls = new List<string>();
            private readonly LeakWatcher _leaks;

            internal CaseState(bool diagnostics)
            {
                Inner = new BusType { DiagnosticsMode = diagnostics };
                Bus = new ReplayBus(Inner);
                Handler = new MessageHandler(new InstanceId(840), Bus) { active = true };
                _leaks = LeakWatcher.WatchWithSlots(Bus, handler: Handler);
                Token = MessageRegistrationToken.Create(Handler, Bus);
                Token.DiagnosticMode = diagnostics;
            }

            internal void Emit(MessageScenario scenario)
            {
                ProbeMessage message = default;
                InstanceId context = Context;
                switch (scenario.Kind)
                {
                    case MessageKind.Untargeted:
                        Bus.UntargetedBroadcast(ref message);
                        break;
                    case MessageKind.Targeted:
                    case MessageKind.TargetedWithoutTargeting:
                        Bus.TargetedBroadcast(ref context, ref message);
                        break;
                    case MessageKind.Broadcast:
                    case MessageKind.BroadcastWithoutSource:
                        Bus.SourcedBroadcast(ref context, ref message);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(scenario));
                }
            }

            public void Dispose()
            {
                try
                {
                    Token.Dispose();
                    Assert.That(
                        Token._metadata.Count,
                        Is.Zero,
                        "Successful disposal clears token metadata."
                    );
                    Assert.That(
                        Token._callCounts.Count,
                        Is.Zero,
                        "Successful disposal clears token call counts."
                    );
                    Assert.That(
                        Token._emissionBuffer.Count,
                        Is.Zero,
                        "Successful disposal clears token emission history."
                    );
                }
                finally
                {
                    Handler.active = false;
                    Inner.Trim(force: true);
                    _leaks.Dispose();
                }
            }
        }

        private static MessageRegistrationHandle RegisterHandler(
            MessageRegistrationToken token,
            MessageScenario scenario,
            bool action,
            Action record
        )
        {
            Action<ProbeMessage> scalar = _ => record();
            Action<InstanceId, ProbeMessage> context = (_, __) => record();
            MessageHandler.FastHandler<ProbeMessage> fast = (in ProbeMessage _) => record();
            MessageHandler.FastHandlerWithContext<ProbeMessage> fastContext = (
                in InstanceId _,
                in ProbeMessage __
            ) => record();
            return (scenario.Kind, scenario.UsePostProcessor, action) switch
            {
                (MessageKind.Untargeted, false, true) => token.RegisterUntargeted(scalar),
                (MessageKind.Untargeted, false, false) => token.RegisterUntargeted(fast),
                (MessageKind.Untargeted, true, _) => token.RegisterUntargetedPostProcessor(fast),
                (MessageKind.Targeted, false, true) => token.RegisterTargeted(Context, scalar),
                (MessageKind.Targeted, false, false) => token.RegisterTargeted(Context, fast),
                (MessageKind.Targeted, true, true) => token.RegisterTargetedPostProcessor(
                    Context,
                    scalar
                ),
                (MessageKind.Targeted, true, false) => token.RegisterTargetedPostProcessor(
                    Context,
                    fast
                ),
                (MessageKind.Broadcast, false, true) => token.RegisterBroadcast(Context, scalar),
                (MessageKind.Broadcast, false, false) => token.RegisterBroadcast(Context, fast),
                (MessageKind.Broadcast, true, true) => token.RegisterBroadcastPostProcessor(
                    Context,
                    scalar
                ),
                (MessageKind.Broadcast, true, false) => token.RegisterBroadcastPostProcessor(
                    Context,
                    fast
                ),
                (MessageKind.TargetedWithoutTargeting, false, true) =>
                    token.RegisterTargetedWithoutTargeting(context),
                (MessageKind.TargetedWithoutTargeting, false, false) =>
                    token.RegisterTargetedWithoutTargeting(fastContext),
                (MessageKind.TargetedWithoutTargeting, true, true) =>
                    token.RegisterTargetedWithoutTargetingPostProcessor(context),
                (MessageKind.TargetedWithoutTargeting, true, false) =>
                    token.RegisterTargetedWithoutTargetingPostProcessor(fastContext),
                (MessageKind.BroadcastWithoutSource, false, true) =>
                    token.RegisterBroadcastWithoutSource(context),
                (MessageKind.BroadcastWithoutSource, false, false) =>
                    token.RegisterBroadcastWithoutSource(fastContext),
                (MessageKind.BroadcastWithoutSource, true, true) =>
                    token.RegisterBroadcastWithoutSourcePostProcessor(context),
                (MessageKind.BroadcastWithoutSource, true, false) =>
                    token.RegisterBroadcastWithoutSourcePostProcessor(fastContext),
                _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
            };
        }

        private static MessageRegistrationHandle RegisterInterceptor(
            MessageRegistrationToken token,
            MessageScenario scenario,
            Action record
        )
        {
            bool Scalar(ref ProbeMessage message)
            {
                record();
                return true;
            }
            bool Contextual(ref InstanceId context, ref ProbeMessage message)
            {
                record();
                return true;
            }
            return scenario.Kind switch
            {
                MessageKind.Untargeted => token.RegisterUntargetedInterceptor<ProbeMessage>(Scalar),
                MessageKind.Targeted => token.RegisterTargetedInterceptor<ProbeMessage>(Contextual),
                MessageKind.Broadcast => token.RegisterBroadcastInterceptor<ProbeMessage>(
                    Contextual
                ),
                _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
            };
        }

        private static MessageRegistrationHandle RegisterGlobal(
            MessageRegistrationToken token,
            bool action,
            Action record
        )
        {
            if (action)
            {
                return token.RegisterGlobalAcceptAll(
                    (IUntargetedMessage _) => record(),
                    (InstanceId _, ITargetedMessage __) => record(),
                    (InstanceId _, IBroadcastMessage __) => record()
                );
            }
            return token.RegisterGlobalAcceptAll(
                (in IUntargetedMessage _) => record(),
                (in InstanceId _, in ITargetedMessage __) => record(),
                (in InstanceId _, in IBroadcastMessage __) => record()
            );
        }

        private sealed class ReplayBus : DelegatingMessageBus
        {
            internal Action OnFirstDeregistration;
            internal bool RejectNext;
            internal readonly InvalidOperationException Failure = new InvalidOperationException(
                "Deregistration rejected before forwarding."
            );
            internal readonly List<MessageBusRegistration> Attempts =
                new List<MessageBusRegistration>();

            internal ReplayBus(BusType inner)
                : base(inner) { }

            public override void Deregister<T>(in MessageBusRegistration registration)
            {
                Attempts.Add(registration);
                bool reject = RejectNext;
                RejectNext = false;
                Action callback = OnFirstDeregistration;
                OnFirstDeregistration = null;
                callback?.Invoke();
                if (reject)
                {
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
