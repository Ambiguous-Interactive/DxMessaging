namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Runtime;
    using NUnit.Framework;

    /// <summary>Checks reset boundaries between global callbacks and later dispatch phases.</summary>
    /// <remarks>
    /// 2026-10-20: A global callback could reset the bus and let replacement callbacks
    /// receive the invalidated message. Use real private buses to exercise the same
    /// reset path without changing the host's global bus or creating Unity objects.
    /// </remarks>
    [TestFixture]
    public sealed class GlobalResetBoundaryTests
    {
        private static readonly InstanceId Context = new InstanceId(2249);

        private static IEnumerable<TestCaseData> GlobalCases()
        {
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            {
                foreach (bool fast in new[] { false, true })
                {
                    foreach (bool sameHandler in new[] { false, true })
                    {
                        foreach (int count in new[] { 1, 2, 3, 4, 5, 8 })
                        {
                            yield return Case(scenario, fast, sameHandler, count, -1, 0);
                            HashSet<int> positions = new HashSet<int> { 0, count / 2, count - 1 };
                            foreach (int position in positions)
                            {
                                yield return Case(scenario, fast, sameHandler, count, position, 1);
                            }
                        }
                        foreach (int count in new[] { 2, 8 })
                        {
                            foreach (int position in new[] { 0, count - 1 })
                            {
                                foreach (int behavior in new[] { 2, 3, 4 })
                                {
                                    yield return Case(
                                        scenario,
                                        fast,
                                        sameHandler,
                                        count,
                                        position,
                                        behavior
                                    );
                                }
                            }
                        }
                    }
                }
            }
        }

        private static TestCaseData Case(
            MessageScenario scenario,
            bool fast,
            bool sameHandler,
            int count,
            int position,
            int behavior
        )
        {
            return new TestCaseData(scenario, fast, sameHandler, count, position, behavior).SetName(
                "GlobalReset_"
                    + scenario
                    + "_fast"
                    + fast
                    + "_shared"
                    + sameHandler
                    + "_count"
                    + count
                    + "_position"
                    + position
                    + "_behavior"
                    + behavior
            );
        }

        [TestCaseSource(nameof(GlobalCases))]
        public void GlobalResetStopsOldDeliveryAndKeepsFreshRegistrations(
            MessageScenario scenario,
            bool fast,
            bool sameHandler,
            int count,
            int resetPosition,
            int behavior
        )
        {
            string label =
                scenario
                + ",fast="
                + fast
                + ",shared="
                + sameHandler
                + ",count="
                + count
                + ",position="
                + resetPosition
                + ",behavior="
                + behavior;
            using CaseState state = new CaseState(scenario, fast, sameHandler, count);
            InvalidOperationException failure = new InvalidOperationException(label);
            bool nested = behavior == 2 || behavior == 4;
            bool throwing = behavior == 3 || behavior == 4;
            for (int index = 0; index < count; ++index)
            {
                int captured = index;
                RegisterGlobal(
                    state.Tokens[index],
                    fast,
                    () =>
                    {
                        state.Calls.Add("global-" + captured);
                        if (captured != resetPosition)
                        {
                            return;
                        }
                        state.Bus.ResetState();
                        state.RegisterFresh();
                        if (nested)
                        {
                            state.Emit();
                        }
                        if (throwing)
                        {
                            throw failure;
                        }
                    }
                );
            }
            state.RegisterOriginalTypedPhases(null);
            state.EnableOriginals();
            Exception observed = null;
            try
            {
                state.Emit();
            }
            catch (Exception exception)
            {
                observed = exception;
            }
            List<string> expected = new List<string>();
            int visibleGlobals = behavior == 0 ? count : resetPosition + 1;
            for (int index = 0; index < visibleGlobals; ++index)
            {
                expected.Add("global-" + index);
            }
            if (nested)
            {
                expected.AddRange(new[] { "fresh-global", "fresh-handler", "fresh-post" });
            }
            if (behavior == 0)
            {
                expected.AddRange(new[] { "typed-first", "typed-peer", "post-old" });
            }
            string[] first = state.Calls.ToArray();
            state.Calls.Clear();
            state.Emit();
            string[] second = state.Calls.ToArray();
            List<string> expectedSecond = new List<string>();
            if (behavior == 0)
            {
                for (int index = 0; index < count; ++index)
                {
                    expectedSecond.Add("global-" + index);
                }
                expectedSecond.AddRange(new[] { "typed-first", "typed-peer", "post-old" });
            }
            else
            {
                expectedSecond.AddRange(new[] { "fresh-global", "fresh-handler", "fresh-post" });
            }
            state.DisposeOriginals();
            int afterOriginalCleanup = state.LiveRegistrations;
            state.Replacement?.Dispose();
            int afterValidCleanup = state.LiveRegistrations;
            AssertAll(() =>
            {
                Assert.That(
                    observed,
                    throwing ? Is.SameAs(failure) : Is.Null,
                    label + ": preserve the exact callback exception or complete normally."
                );
                Assert.That(
                    first,
                    Is.EqualTo(expected),
                    label + ": the invalidated emission must stop at the resetting callback."
                );
                Assert.That(
                    second,
                    Is.EqualTo(expectedSecond),
                    label + ": a separate fresh emission must retain order and callbacks."
                );
                Assert.That(
                    afterOriginalCleanup,
                    Is.EqualTo(behavior == 0 ? 0 : 3),
                    label + ": stale original cleanup must preserve all fresh registrations."
                );
                Assert.That(
                    afterValidCleanup,
                    Is.Zero,
                    label + ": valid cleanup must reach zero before maintenance."
                );
            });
        }

        private static IEnumerable<TestCaseData> PreservedCases()
        {
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            {
                foreach (bool sameHandler in new[] { false, true })
                {
                    foreach (int style in new[] { 0, 1, 2 })
                    {
                        int[] counts = style == 2 ? new[] { 2, 8 } : new[] { 1, 2, 3, 4, 5, 8 };
                        foreach (int count in counts)
                        {
                            yield return PreservedCase(scenario, sameHandler, style, count, -1, 0);
                            int[] order = ExpectedGlobalOrder(count, sameHandler, style);
                            HashSet<int> positions = new HashSet<int>
                            {
                                order[0],
                                order[count / 2],
                                order[count - 1],
                            };
                            foreach (int position in positions)
                            {
                                yield return PreservedCase(
                                    scenario,
                                    sameHandler,
                                    style,
                                    count,
                                    position,
                                    1
                                );
                            }
                        }
                        foreach (int count in new[] { 2, 8 })
                        {
                            foreach (int behavior in new[] { 2, 3, 4 })
                            {
                                yield return PreservedCase(
                                    scenario,
                                    sameHandler,
                                    style,
                                    count,
                                    0,
                                    behavior
                                );
                            }
                        }
                    }
                }
            }
        }

        private static TestCaseData PreservedCase(
            MessageScenario scenario,
            bool sameHandler,
            int style,
            int count,
            int position,
            int behavior
        )
        {
            return new TestCaseData(
                scenario,
                sameHandler,
                style,
                count,
                position,
                behavior
            ).SetName(
                "PreservedReset_"
                    + scenario
                    + "_shared"
                    + sameHandler
                    + "_style"
                    + style
                    + "_count"
                    + count
                    + "_position"
                    + position
                    + "_behavior"
                    + behavior
            );
        }

        private static int[] ExpectedGlobalOrder(int count, bool sameHandler, int style)
        {
            if (sameHandler && style == 2)
            {
                // Global callbacks on one handler dispatch fast delegates before Action delegates.
                return count == 2 ? new[] { 0, 1 } : new[] { 0, 2, 4, 6, 1, 3, 5, 7 };
            }
            int[] order = new int[count];
            for (int index = 0; index < count; ++index)
            {
                order[index] = index;
            }
            return order;
        }

        /// <summary>Exercises the public generation-only reset used to preserve custom bus storage.</summary>
        [TestCaseSource(nameof(PreservedCases))]
        public void PreservedStorageResetStopsEveryCallbackAndKeepsLaterDelivery(
            MessageScenario scenario,
            bool sameHandler,
            int style,
            int count,
            int resetPosition,
            int behavior
        )
        {
            string label =
                scenario
                + ",shared="
                + sameHandler
                + ",style="
                + style
                + ",count="
                + count
                + ",position="
                + resetPosition
                + ",behavior="
                + behavior;
            using CaseState state = new CaseState(scenario, style == 1, sameHandler, count);
            bool reset = false;
            bool nested = behavior == 2 || behavior == 4;
            bool throwing = behavior == 3 || behavior == 4;
            InvalidOperationException failure = new InvalidOperationException(label);
            for (int index = 0; index < count; ++index)
            {
                int captured = index;
                bool fast = style == 1 || style == 2 && index % 2 == 0;
                RegisterGlobal(
                    state.Tokens[index],
                    fast,
                    () =>
                    {
                        state.Calls.Add("global-" + captured);
                        if (captured != resetPosition || reset)
                        {
                            return;
                        }
                        reset = true;
                        state.Bus.BumpResetGeneration();
                        if (nested)
                        {
                            state.Emit();
                        }
                        if (throwing)
                        {
                            throw failure;
                        }
                    }
                );
            }
            state.RegisterOriginalTypedPhases(null);
            state.EnableOriginals();
            int initialLive = state.LiveRegistrations;
            Exception observed = null;
            try
            {
                state.Emit();
            }
            catch (Exception exception)
            {
                observed = exception;
            }
            List<string> full = new List<string>();
            foreach (int index in ExpectedGlobalOrder(count, sameHandler, style))
            {
                full.Add("global-" + index);
            }
            full.AddRange(new[] { "typed-first", "typed-peer", "post-old" });
            List<string> expectedFirst = new List<string>();
            if (behavior == 0)
            {
                expectedFirst.AddRange(full);
            }
            else
            {
                foreach (int index in ExpectedGlobalOrder(count, sameHandler, style))
                {
                    expectedFirst.Add("global-" + index);
                    if (index == resetPosition)
                    {
                        break;
                    }
                }
                if (nested)
                {
                    expectedFirst.AddRange(full);
                }
            }
            string[] first = state.Calls.ToArray();
            state.Calls.Clear();
            state.Emit();
            string[] second = state.Calls.ToArray();
            state.DisposeOriginals();
            int afterOriginalCleanup = state.LiveRegistrations;
            state.Bus.ResetState();
            AssertAll(() =>
            {
                Assert.That(
                    observed,
                    throwing ? Is.SameAs(failure) : Is.Null,
                    label + ": retain callback exception identity or complete normally."
                );
                Assert.That(
                    first,
                    Is.EqualTo(expectedFirst),
                    label + ": generation-only reset must stop within a shared handler too."
                );
                Assert.That(
                    second,
                    Is.EqualTo(full),
                    label + ": the next emission must preserve all original registrations."
                );
                Assert.That(
                    afterOriginalCleanup,
                    Is.EqualTo(behavior == 0 ? 0 : initialLive),
                    label + ": invalidated handles must leave preserved storage unchanged."
                );
                Assert.That(
                    state.LiveRegistrations,
                    Is.Zero,
                    label + ": explicit owned-bus reset must clear preserved registrations."
                );
            });
        }

        [Test]
        public void OtherResetOriginsKeepTheirPhaseBoundary(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(false, true)] bool fast,
            [Values(false, true)] bool interceptor
        )
        {
            string label = scenario + ",fast=" + fast + ",interceptor=" + interceptor;
            using CaseState state = new CaseState(scenario, fast, false, 2);
            RegisterGlobal(state.Tokens[0], fast, () => state.Calls.Add("global-0"));
            RegisterGlobal(state.Tokens[1], fast, () => state.Calls.Add("global-1"));
            Action reset = () =>
            {
                state.Bus.ResetState();
                state.RegisterFresh();
            };
            state.RegisterOriginalTypedPhases(interceptor ? null : reset);
            if (interceptor)
            {
                RegisterInterceptor(
                    state.Tokens[0],
                    scenario,
                    () =>
                    {
                        state.Calls.Add("interceptor");
                        reset();
                    }
                );
            }
            state.EnableOriginals();
            state.Emit();
            string[] first = state.Calls.ToArray();
            state.Calls.Clear();
            state.Emit();
            string[] second = state.Calls.ToArray();
            state.DisposeOriginals();
            int afterStale = state.LiveRegistrations;
            state.Replacement.Dispose();
            AssertAll(() =>
            {
                Assert.That(
                    first,
                    Is.EqualTo(
                        interceptor
                            ? new[] { "interceptor" }
                            : new[] { "global-0", "global-1", "typed-first" }
                    ),
                    label + ": existing reset origins must stop remaining old phases."
                );
                Assert.That(
                    second,
                    Is.EqualTo(new[] { "fresh-global", "fresh-handler", "fresh-post" }),
                    label + ": next emission must deliver fresh callbacks once."
                );
                Assert.That(
                    afterStale,
                    Is.EqualTo(3),
                    label + ": stale cleanup must preserve all three replacements."
                );
                Assert.That(
                    state.LiveRegistrations,
                    Is.Zero,
                    label + ": valid cleanup must finish before maintenance."
                );
            });
        }

        [Test]
        public void GlobalPeerActivityRemainsLiveWithinTheFrozenWalk(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(false, true)] bool fast,
            [Values(false, true)] bool activate
        )
        {
            string label = scenario + ",fast=" + fast + ",activate=" + activate;
            using CaseState state = new CaseState(scenario, fast, false, 2);
            bool changed = false;
            state.Handlers[1].active = !activate;
            RegisterGlobal(
                state.Tokens[0],
                fast,
                () =>
                {
                    state.Calls.Add("global-0");
                    if (!changed)
                    {
                        state.Handlers[1].active = activate;
                        changed = true;
                    }
                }
            );
            RegisterGlobal(state.Tokens[1], fast, () => state.Calls.Add("global-1"));
            state.EnableOriginals();
            state.Emit();
            string[] first = state.Calls.ToArray();
            state.Handlers[1].active = true;
            state.Calls.Clear();
            state.Emit();
            string[] second = state.Calls.ToArray();
            state.DisposeOriginals();
            AssertAll(() =>
            {
                Assert.That(
                    first,
                    Is.EqualTo(activate ? new[] { "global-0", "global-1" } : new[] { "global-0" }),
                    label + ": a prior callback's activity change must affect the peer live."
                );
                Assert.That(
                    second,
                    Is.EqualTo(new[] { "global-0", "global-1" }),
                    label + ": later activation must preserve both global registrations."
                );
                Assert.That(
                    state.LiveRegistrations,
                    Is.Zero,
                    label + ": global cleanup must finish before maintenance."
                );
            });
        }

        private static void RegisterGlobal(MessageRegistrationToken token, bool fast, Action call)
        {
            if (fast)
            {
                token.RegisterGlobalAcceptAll(
                    acceptAllUntargeted: (in IUntargetedMessage _) => call(),
                    acceptAllTargeted: (in InstanceId _, in ITargetedMessage __) => call(),
                    acceptAllBroadcast: (in InstanceId _, in IBroadcastMessage __) => call()
                );
            }
            else
            {
                token.RegisterGlobalAcceptAll(
                    acceptAllUntargeted: _ => call(),
                    acceptAllTargeted: (_, _) => call(),
                    acceptAllBroadcast: (_, _) => call()
                );
            }
        }

        private static void RegisterTyped(
            MessageRegistrationToken token,
            MessageScenario scenario,
            Action call,
            bool post,
            int priority
        )
        {
            MessageHandler.FastHandler<ProbeMessage> callback = (in ProbeMessage _) => call();
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                    if (post)
                    {
                        ScenarioHarness.RegisterUntargetedPostProcessor(
                            scenario,
                            token,
                            callback,
                            priority
                        );
                    }
                    else
                    {
                        ScenarioHarness.RegisterUntargeted(scenario, token, callback, priority);
                    }
                    break;
                case MessageKind.Targeted:
                    if (post)
                    {
                        ScenarioHarness.RegisterTargetedPostProcessor(
                            scenario,
                            token,
                            Context,
                            callback,
                            priority
                        );
                    }
                    else
                    {
                        ScenarioHarness.RegisterTargeted(
                            scenario,
                            token,
                            Context,
                            callback,
                            priority
                        );
                    }
                    break;
                case MessageKind.Broadcast:
                    if (post)
                    {
                        ScenarioHarness.RegisterBroadcastPostProcessor(
                            scenario,
                            token,
                            Context,
                            callback,
                            priority
                        );
                    }
                    else
                    {
                        ScenarioHarness.RegisterBroadcast(
                            scenario,
                            token,
                            Context,
                            callback,
                            priority
                        );
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario));
            }
        }

        private static void RegisterInterceptor(
            MessageRegistrationToken token,
            MessageScenario scenario,
            Action call
        )
        {
            bool Scalar(ref ProbeMessage _)
            {
                call();
                return true;
            }
            bool Contextual(ref InstanceId _, ref ProbeMessage __)
            {
                call();
                return true;
            }
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                    token.RegisterUntargetedInterceptor<ProbeMessage>(Scalar);
                    break;
                case MessageKind.Targeted:
                    token.RegisterTargetedInterceptor<ProbeMessage>(Contextual);
                    break;
                case MessageKind.Broadcast:
                    token.RegisterBroadcastInterceptor<ProbeMessage>(Contextual);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario));
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

        private readonly struct ProbeMessage
            : IUntargetedMessage,
                ITargetedMessage,
                IBroadcastMessage
        {
            public Type MessageType => typeof(ProbeMessage);
        }

        private sealed class CaseState : IDisposable
        {
            private readonly MessageScenario _scenario;
            private readonly bool _fast;
            private readonly LeakWatcher _leaks;
            private readonly bool _debug = MessagingDebug.enabled;
            private readonly DiagnosticsTarget _targets = IMessageBus.GlobalDiagnosticsTargets;
            internal readonly MessageBus Bus;
            internal readonly MessageHandler[] Handlers;
            internal readonly MessageRegistrationToken[] Tokens;
            internal readonly List<string> Calls = new List<string>();
            internal MessageRegistrationToken Replacement;

            internal CaseState(MessageScenario scenario, bool fast, bool sameHandler, int count)
            {
                _scenario = scenario;
                _fast = fast;
                MessagingDebug.enabled = false;
                IMessageBus.GlobalDiagnosticsTargets = DiagnosticsTarget.Off;
                Bus = new MessageBus();
                _leaks = new LeakWatcher(Bus, label: scenario.ToString());
                Handlers = new MessageHandler[count];
                Tokens = new MessageRegistrationToken[count];
                for (int index = 0; index < count; ++index)
                {
                    Handlers[index] =
                        sameHandler && 0 < index
                            ? Handlers[0]
                            : new MessageHandler(new InstanceId(3100 + index), Bus)
                            {
                                active = true,
                            };
                    Tokens[index] = MessageRegistrationToken.Create(Handlers[index], Bus);
                }
            }

            internal int LiveRegistrations =>
                Bus.RegisteredUntargeted
                + Bus.RegisteredTargeted
                + Bus.RegisteredBroadcast
                + Bus.RegisteredInterceptors
                + Bus.RegisteredPostProcessors
                + Bus.RegisteredGlobalAcceptAll;

            internal void RegisterOriginalTypedPhases(Action reset)
            {
                RegisterTyped(
                    Tokens[0],
                    _scenario,
                    () =>
                    {
                        Calls.Add("typed-first");
                        reset?.Invoke();
                    },
                    false,
                    0
                );
                RegisterTyped(
                    Tokens[Tokens.Length - 1],
                    _scenario,
                    () => Calls.Add("typed-peer"),
                    false,
                    1
                );
                RegisterTyped(
                    Tokens[Tokens.Length - 1],
                    _scenario,
                    () => Calls.Add("post-old"),
                    true,
                    0
                );
            }

            internal void RegisterFresh()
            {
                Replacement = MessageRegistrationToken.Create(Handlers[Handlers.Length - 1], Bus);
                RegisterGlobal(Replacement, _fast, () => Calls.Add("fresh-global"));
                RegisterTyped(Replacement, _scenario, () => Calls.Add("fresh-handler"), false, 0);
                RegisterTyped(Replacement, _scenario, () => Calls.Add("fresh-post"), true, 0);
                Replacement.Enable();
            }

            internal void EnableOriginals()
            {
                foreach (MessageRegistrationToken token in Tokens)
                {
                    token.Enable();
                }
            }

            internal void DisposeOriginals()
            {
                foreach (MessageRegistrationToken token in Tokens)
                {
                    token.Dispose();
                }
            }

            internal void Emit()
            {
                ProbeMessage message = new ProbeMessage();
                switch (_scenario.Kind)
                {
                    case MessageKind.Untargeted:
                        ScenarioHarness.EmitUntargeted(_scenario, ref message, Bus);
                        break;
                    case MessageKind.Targeted:
                        ScenarioHarness.EmitTargeted(_scenario, ref message, Context, Bus);
                        break;
                    case MessageKind.Broadcast:
                        ScenarioHarness.EmitBroadcast(_scenario, ref message, Context, Bus);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(_scenario));
                }
            }

            public void Dispose()
            {
                try
                {
                    DisposeOriginals();
                    Replacement?.Dispose();
                    _leaks.Dispose();
                }
                finally
                {
                    try
                    {
                        Bus.Trim(force: true);
                        Bus.ResetState();
                    }
                    finally
                    {
                        IMessageBus.GlobalDiagnosticsTargets = _targets;
                        MessagingDebug.enabled = _debug;
                    }
                }
            }
        }
    }
}
