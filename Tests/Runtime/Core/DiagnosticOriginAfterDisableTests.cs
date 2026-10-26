namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.Diagnostics;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Runtime;
    using NUnit.Framework;

    /// <summary>Preserves a diagnosed delivery's origin across successful teardown.</summary>
    /// <remarks>
    /// 2026-10-20: The previous bus anchor can retain an old bus after disabled
    /// retargeting, but clearing it during successful teardown loses the trace
    /// still owed by the current callback. These tests preserve that contract
    /// across disable, retarget and immediate re-enable before an older callback returns.
    /// </remarks>
    [TestFixture]
    public sealed class DiagnosticOriginAfterDisableTests
    {
        private static readonly InstanceId Context = new InstanceId(981);

        private static IEnumerable<TestCaseData> Cases()
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
                    foreach (bool retarget in new[] { false, true })
                    {
                        foreach (bool enableInside in new[] { false, true })
                        {
                            yield return Case(scenario, action, false, retarget, enableInside);
                        }
                    }
                }
            }
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            {
                foreach (bool action in new[] { false, true })
                {
                    foreach (bool retarget in new[] { false, true })
                    {
                        foreach (bool enableInside in new[] { false, true })
                        {
                            yield return Case(scenario, action, true, retarget, enableInside);
                        }
                    }
                }
            }
        }

        private static TestCaseData Case(
            MessageScenario scenario,
            bool action,
            bool global,
            bool retarget,
            bool enableInside
        ) =>
            new TestCaseData(scenario, action, global, retarget, enableInside).SetName(
                $"DiagnosticOrigin_{scenario.Kind}_Post{scenario.UsePostProcessor}_Action{action}_Global{global}_Retarget{retarget}"
                    + (enableInside ? "_EnableInsideTrue" : string.Empty)
            );

        [TestCaseSource(nameof(Cases))]
        public void SuccessfulDisablePreservesTheOriginUntilTheCallbackReturns(
            MessageScenario scenario,
            bool action,
            bool global,
            bool retarget,
            bool enableInside
        )
        {
            string label = $"[{scenario.Kind}] action={action},global={global},retarget={retarget}";
            bool previousDebug = MessagingDebug.enabled;
            MessagingDebug.enabled = false;
            MessageBus original = new MessageBus { DiagnosticsMode = true };
            MessageBus replacement = new MessageBus { DiagnosticsMode = true };
            MessageBus neutral = new MessageBus();
            using LeakWatcher oldLeaks = new LeakWatcher(original, label: label + ": original");
            using LeakWatcher newLeaks = new LeakWatcher(
                replacement,
                label: label + ": replacement"
            );
            MessageHandler handler = new MessageHandler(new InstanceId(982), neutral)
            {
                active = true,
            };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, original);
            token.DiagnosticMode = true;
            bool armed = true;
            int calls = 0;
            MessageBus observedBus = original;
            List<long> observedOrigins = new List<long>();
            Action callback = () =>
            {
                calls++;
                observedOrigins.Add(observedBus.EmissionId);
                if (armed)
                {
                    armed = false;
                    token.Disable();
                    if (retarget)
                    {
                        token.RetargetMessageBus(
                            replacement,
                            MessageBusRebindMode.PreserveRegistrations
                        );
                    }
                    if (enableInside)
                    {
                        token.Enable();
                    }
                }
            };
            try
            {
                MessageRegistrationHandle handle = global
                    ? RegisterGlobal(token, action, callback)
                    : RegisterHandler(token, scenario, action, callback);
                if (enableInside)
                {
                    Warm(retarget ? replacement : original);
                }
                token.Enable();
                Assert.That(
                    MessageBusInspection.CountLiveRegistrations(original),
                    Is.EqualTo(1),
                    label + ": actual original registration"
                );
                Emit(original, scenario);
                Assert.That(calls, Is.EqualTo(1), label + ": exactly one original callback");
                Assert.That(
                    token.Enabled,
                    Is.EqualTo(enableInside),
                    label + ": expected state after optional callback re-enable"
                );
                Assert.That(
                    MessageBusInspection.CountLiveRegistrations(original),
                    Is.EqualTo(enableInside && !retarget ? 1 : 0),
                    label + ": consumed original cleanup"
                );
                Assert.That(
                    MessageBusInspection.CountLiveRegistrations(replacement),
                    Is.EqualTo(enableInside && retarget ? 1 : 0),
                    label + ": only explicit re-enable activates rebound staging"
                );
                Assert.That(
                    token._metadata.Count,
                    Is.EqualTo(1),
                    label + ": original staged handle survives"
                );
                Assert.That(
                    token._emissionBuffer.Count,
                    Is.EqualTo(1),
                    label + ": one diagnosed original delivery"
                );
                MessageEmissionData first = token._emissionBuffer[0];
                Assert.That(
                    0L < observedOrigins[0],
                    Is.True,
                    label + ": positive originating emission ID"
                );
                Assert.That(
                    first.traceId,
                    Is.EqualTo(observedOrigins[0]),
                    label + ": preserve in-flight origin after disable/retarget"
                );
                Assert.That(
                    first.registrationHandle,
                    Is.EqualTo(handle),
                    label + ": observing original handle"
                );
                Assert.That(
                    first.message.MessageType,
                    Is.EqualTo(typeof(ProbeMessage)),
                    label + ": diagnosed payload type"
                );
                Assert.That(
                    token._callCounts[handle],
                    Is.EqualTo(1),
                    label + ": original call count"
                );
                MessageBus next = retarget ? replacement : original;
                if (!enableInside)
                {
                    Warm(next);
                }
                Assert.That(calls, Is.EqualTo(1), label + ": disabled staging stays silent");
                observedBus = next;
                token.Enable();
                Emit(next, scenario);
                Assert.That(calls, Is.EqualTo(2), label + ": staged callback replays once");
                Assert.That(
                    token._emissionBuffer.Count,
                    Is.EqualTo(2),
                    label + ": both diagnosed deliveries retained"
                );
                MessageEmissionData second = token._emissionBuffer[1];
                Assert.That(
                    observedOrigins[0] < observedOrigins[1],
                    Is.True,
                    label + ": deliberately distinct next emission ID"
                );
                Assert.That(
                    second.traceId,
                    Is.EqualTo(observedOrigins[1]),
                    label + ": fresh registration has its own actual origin"
                );
                Assert.That(
                    second.registrationHandle,
                    Is.EqualTo(handle),
                    label + ": same staged handle after enable"
                );
                Assert.That(
                    token._callCounts[handle],
                    Is.EqualTo(2),
                    label + ": exact cumulative delivery count"
                );
                token.Dispose();
                Assert.That(
                    MessageBusInspection.CountLiveRegistrations(original),
                    Is.Zero,
                    label + ": final original cleanup"
                );
                Assert.That(
                    MessageBusInspection.CountLiveRegistrations(replacement),
                    Is.Zero,
                    label + ": final replacement cleanup"
                );
                Assert.That(token._metadata.Count, Is.Zero, label + ": disposal clears staging");
                Assert.That(
                    token._emissionBuffer.Count,
                    Is.Zero,
                    label + ": disposal clears diagnostic history"
                );
                Emit(original, scenario);
                Emit(replacement, scenario);
                Assert.That(
                    calls,
                    Is.EqualTo(2),
                    label + ": disposed callbacks stay absent on both buses"
                );
            }
            finally
            {
                token.Dispose();
                handler.active = false;
                _ = original.Trim(force: true);
                _ = replacement.Trim(force: true);
                _ = neutral.Trim(force: true);
                MessagingDebug.enabled = previousDebug;
            }
        }

        private static IEnumerable<TestCaseData> PeerCases()
        {
            foreach (TestCaseData single in Cases())
            {
                if (!(bool)single.Arguments[4])
                {
                    continue;
                }
                for (int nesting = 0; nesting < 3; nesting++)
                {
                    for (int diagnostics = 0; diagnostics < 4; diagnostics++)
                    {
                        yield return new TestCaseData(
                            single.Arguments[0],
                            single.Arguments[1],
                            single.Arguments[2],
                            single.Arguments[3],
                            nesting,
                            diagnostics
                        ).SetName(
                            single.TestName
                                + $"_FrozenPeer_Nesting{nesting}_Diagnostics{diagnostics}"
                        );
                    }
                }
            }
        }

        /// <remarks>
        /// 2026-10-21: An entry snapshot of a replayed Registration's mutable bus repairs
        /// the running callback but misses its pending frozen peer. Both deliveries
        /// must retain their binding bus through nested replay and diagnostic toggles.
        /// </remarks>
        [TestCaseSource(nameof(PeerCases))]
        public void FrozenPeersKeepTheirBindingOriginAcrossNestedReplay(
            MessageScenario scenario,
            bool action,
            bool global,
            bool retarget,
            int nesting,
            int diagnostics
        )
        {
            ExerciseFrozenPeers(scenario, action, global, retarget, nesting, diagnostics, 0);
        }

        private static IEnumerable<TestCaseData> ActiveRebindCases()
        {
            foreach (TestCaseData peer in PeerCases())
            {
                for (int strategy = 1; strategy < 3; strategy++)
                {
                    yield return new TestCaseData(
                        peer.Arguments[0],
                        peer.Arguments[1],
                        peer.Arguments[2],
                        peer.Arguments[3],
                        peer.Arguments[4],
                        peer.Arguments[5],
                        strategy
                    ).SetName(peer.TestName + $"_ActiveRebind{strategy}");
                }
            }
        }

        [TestCaseSource(nameof(ActiveRebindCases))]
        public void FrozenPeersKeepTheirOriginThroughActiveAndDefaultBusRebind(
            MessageScenario scenario,
            bool action,
            bool global,
            bool retarget,
            int nesting,
            int diagnostics,
            int strategy
        )
        {
            ExerciseFrozenPeers(scenario, action, global, retarget, nesting, diagnostics, strategy);
        }

        private static void ExerciseFrozenPeers(
            MessageScenario scenario,
            bool action,
            bool global,
            bool retarget,
            int nesting,
            int diagnostics,
            int strategy
        )
        {
            string label =
                $"{scenario},action={action},global={global},retarget={retarget},nesting={nesting},diagnostics={diagnostics},strategy={strategy}";
            bool previousDebug = MessagingDebug.enabled;
            MessagingDebug.enabled = false;
            MessageBus original = new MessageBus();
            MessageBus replacement = new MessageBus();
            MessageBus neutral = new MessageBus();
            using LeakWatcher oldLeaks = new LeakWatcher(original, label: label + ": original");
            using LeakWatcher newLeaks = new LeakWatcher(
                replacement,
                label: label + ": replacement"
            );
            MessageHandler handler = new MessageHandler(
                new InstanceId(983),
                strategy == 2 ? original : neutral
            )
            {
                active = true,
            };
            MessageRegistrationToken token = MessageRegistrationToken.Create(
                handler,
                strategy == 2 ? null : original
            );
            token.DiagnosticMode = diagnostics == 0 || diagnostics == 2;
            MessageBus observedBus = original;
            MessageBus finalBus = retarget ? replacement : original;
            List<string> calls = new List<string>();
            List<long> expectedTraces = new List<long>();
            List<MessageRegistrationHandle> expectedHandles = new List<MessageRegistrationHandle>();
            MessageRegistrationHandle first = default;
            MessageRegistrationHandle peer = default;
            bool armed = true;
            int firstCalls = 0;
            int peerCalls = 0;
            Action firstCallback = () =>
            {
                firstCalls++;
                calls.Add("A");
                long origin = observedBus.EmissionId;
                if (armed)
                {
                    armed = false;
                    Rebind(finalBus);
                    token.DiagnosticMode = diagnostics == 0 || diagnostics == 1;
                    if (0 < nesting)
                    {
                        observedBus = finalBus;
                        Emit(finalBus, scenario);
                    }
                    if (nesting == 2)
                    {
                        Rebind(original);
                        finalBus = original;
                        observedBus = original;
                        Emit(original, scenario);
                    }
                    observedBus = original;
                }
                if (token.DiagnosticMode)
                {
                    expectedTraces.Add(origin);
                    expectedHandles.Add(first);
                }
            };
            Action peerCallback = () =>
            {
                peerCalls++;
                calls.Add("B");
                if (token.DiagnosticMode)
                {
                    expectedTraces.Add(observedBus.EmissionId);
                    expectedHandles.Add(peer);
                }
            };
            void Rebind(MessageBus destination)
            {
                if (strategy == 0)
                {
                    token.Disable();
                    token.RetargetMessageBus(
                        destination,
                        MessageBusRebindMode.PreserveRegistrations
                    );
                    token.Enable();
                }
                else if (strategy == 1)
                {
                    token.RetargetMessageBus(destination, MessageBusRebindMode.RebindActive);
                }
                else
                {
                    handler.SetDefaultMessageBus(destination);
                    token.RetargetMessageBus(null, MessageBusRebindMode.RebindActive);
                }
            }
            try
            {
                Warm(replacement);
                first = global
                    ? RegisterGlobal(token, action, firstCallback)
                    : RegisterHandler(token, scenario, action, firstCallback);
                peer = global
                    ? RegisterGlobal(token, action, peerCallback)
                    : RegisterHandler(token, scenario, action, peerCallback);
                token.Enable();
                Emit(original, scenario);
                Assert.That(
                    firstCalls,
                    Is.EqualTo(1 + nesting),
                    label + ": all running/nested A callbacks"
                );
                Assert.That(
                    peerCalls,
                    Is.EqualTo(1 + nesting),
                    label + ": all pending/nested B callbacks"
                );
                string[] expectedOrder =
                    nesting == 0 ? new[] { "A", "B" }
                    : nesting == 1 ? new[] { "A", "A", "B", "B" }
                    : new[] { "A", "A", "B", "A", "B", "B" };
                Assert.That(
                    calls,
                    Is.EqualTo(expectedOrder),
                    label + ": frozen outer peer follows nested emissions"
                );
                Assert.That(token.Enabled, Is.True, label + ": replay remains enabled");
                Assert.That(
                    token._metadata.Count,
                    Is.EqualTo(2),
                    label + ": both original handles stay staged"
                );
                Assert.That(
                    MessageBusInspection.CountLiveRegistrations(original),
                    Is.EqualTo(ReferenceEquals(finalBus, original) ? 2 : 0),
                    label + ": final original bus route"
                );
                Assert.That(
                    MessageBusInspection.CountLiveRegistrations(replacement),
                    Is.EqualTo(ReferenceEquals(finalBus, replacement) ? 2 : 0),
                    label + ": final replacement bus route"
                );
                Assert.That(
                    token._emissionBuffer.Count,
                    Is.EqualTo(expectedTraces.Count),
                    label + ": exact post-callback diagnostic toggle semantics"
                );
                Assert.That(
                    token._callCounts.Count,
                    Is.EqualTo(diagnostics < 2 ? 2 : 0),
                    label + ": diagnostic state materializes only when recorded"
                );
                if (diagnostics < 2)
                {
                    Assert.That(
                        token._callCounts[first],
                        Is.EqualTo(1 + nesting),
                        label + ": exact A diagnostic count"
                    );
                    Assert.That(
                        token._callCounts[peer],
                        Is.EqualTo(1 + nesting),
                        label + ": exact B diagnostic count"
                    );
                }
                token.DiagnosticMode = true;
                observedBus = finalBus;
                Emit(finalBus, scenario);
                Assert.That(
                    firstCalls,
                    Is.EqualTo(2 + nesting),
                    label + ": fresh A on latest binding"
                );
                Assert.That(
                    peerCalls,
                    Is.EqualTo(2 + nesting),
                    label + ": fresh B on latest binding"
                );
                Assert.That(
                    token._emissionBuffer.Count,
                    Is.EqualTo(expectedTraces.Count),
                    label + ": fresh records appended"
                );
                for (int i = 0; i < expectedTraces.Count; i++)
                {
                    Assert.That(
                        expectedTraces[i],
                        Is.GreaterThan(0L),
                        label + $": positive independent origin {i}"
                    );
                    Assert.That(
                        token._emissionBuffer[i].registrationHandle,
                        Is.EqualTo(expectedHandles[i]),
                        label + $": original callback handle {i}"
                    );
                }
                List<long> actualTraces = new List<long>();
                for (int i = 0; i < token._emissionBuffer.Count; i++)
                {
                    actualTraces.Add(token._emissionBuffer[i].traceId);
                }
                Assert.That(
                    actualTraces,
                    Is.EqualTo(expectedTraces),
                    label + ": immutable running and pending binding origins"
                );
                token.Dispose();
                Assert.That(token._metadata.Count, Is.Zero, label + ": disposal clears staging");
                Assert.That(
                    token._callCounts.Count,
                    Is.Zero,
                    label + ": disposal clears call counts"
                );
                Assert.That(
                    token._emissionBuffer.Count,
                    Is.Zero,
                    label + ": disposal clears history"
                );
                Emit(original, scenario);
                Emit(replacement, scenario);
                Assert.That(
                    firstCalls,
                    Is.EqualTo(2 + nesting),
                    label + ": stale original/rebound A removed"
                );
                Assert.That(
                    peerCalls,
                    Is.EqualTo(2 + nesting),
                    label + ": stale original/rebound B removed"
                );
            }
            finally
            {
                token.Dispose();
                handler.active = false;
                _ = original.Trim(force: true);
                _ = replacement.Trim(force: true);
                _ = neutral.Trim(force: true);
                MessagingDebug.enabled = previousDebug;
            }
        }

        private static IEnumerable<TestCaseData> LifecycleCases()
        {
            foreach (TestCaseData single in Cases())
            {
                if (!(bool)single.Arguments[3] || !(bool)single.Arguments[4])
                {
                    continue;
                }
                for (int mode = 0; mode < 3; mode++)
                {
                    foreach (bool diagnostics in new[] { false, true })
                    {
                        yield return new TestCaseData(
                            single.Arguments[0],
                            single.Arguments[1],
                            single.Arguments[2],
                            mode,
                            diagnostics
                        ).SetName(single.TestName + $"_Lifecycle{mode}_Diagnostics{diagnostics}");
                    }
                }
            }
        }

        [TestCaseSource(nameof(LifecycleCases))]
        public void ReboundBindingsPreserveExceptionResetAndStaleHandleContracts(
            MessageScenario scenario,
            bool action,
            bool global,
            int mode,
            bool diagnostics
        )
        {
            string label =
                $"{scenario},action={action},global={global},mode={mode},diagnostics={diagnostics}";
            bool previousDebug = MessagingDebug.enabled;
            MessagingDebug.enabled = false;
            MessageBus original = new MessageBus();
            MessageBus replacement = new MessageBus();
            MessageBus neutral = new MessageBus();
            using LeakWatcher oldLeaks = new LeakWatcher(original, label: label + ": original");
            using LeakWatcher newLeaks = new LeakWatcher(
                replacement,
                label: label + ": replacement"
            );
            MessageHandler handler = new MessageHandler(new InstanceId(984), neutral)
            {
                active = true,
            };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, original);
            token.DiagnosticMode = diagnostics;
            InvalidOperationException sentinel = new InvalidOperationException(
                "Expected rebound callback failure."
            );
            bool armed = true;
            int firstCalls = 0;
            int peerCalls = 0;
            long outerOrigin = 0;
            Action firstCallback = () =>
            {
                firstCalls++;
                if (!armed)
                {
                    return;
                }
                armed = false;
                outerOrigin = original.EmissionId;
                token.Disable();
                token.RetargetMessageBus(replacement, MessageBusRebindMode.PreserveRegistrations);
                token.Enable();
                if (mode == 0)
                {
                    throw sentinel;
                }
                if (mode == 1)
                {
                    original.ResetState();
                }
            };
            Action peerCallback = () => peerCalls++;
            try
            {
                Warm(replacement);
                MessageRegistrationHandle first = global
                    ? RegisterGlobal(token, action, firstCallback)
                    : RegisterHandler(token, scenario, action, firstCallback);
                MessageRegistrationHandle peer = global
                    ? RegisterGlobal(token, action, peerCallback)
                    : RegisterHandler(token, scenario, action, peerCallback);
                token.Enable();
                Exception caught = null;
                try
                {
                    Emit(original, scenario);
                }
                catch (Exception exception)
                {
                    caught = exception;
                }
                Assert.That(
                    caught,
                    Is.SameAs(mode == 0 ? sentinel : null),
                    label + ": exact user exception propagates"
                );
                Assert.That(
                    outerOrigin,
                    Is.GreaterThan(0L),
                    label + ": actual original dispatch entered"
                );
                Assert.That(firstCalls, Is.EqualTo(1), label + ": first callback entered once");
                Assert.That(
                    peerCalls,
                    Is.EqualTo(mode == 2 ? 1 : 0),
                    label + ": throw and reset suppress pending peer"
                );
                Assert.That(token.Enabled, Is.True, label + ": rebound token remains active");
                Assert.That(
                    token._metadata.Count,
                    Is.EqualTo(2),
                    label + ": both callbacks remain staged"
                );
                Assert.That(
                    MessageBusInspection.CountLiveRegistrations(original),
                    Is.Zero,
                    label + ": no stale original route"
                );
                Assert.That(
                    MessageBusInspection.CountLiveRegistrations(replacement),
                    Is.EqualTo(2),
                    label + ": both rebound callbacks live"
                );
                int originalRecords = diagnostics && mode != 0 ? (mode == 2 ? 2 : 1) : 0;
                Assert.That(
                    token._emissionBuffer.Count,
                    Is.EqualTo(originalRecords),
                    label + ": only successful callbacks recorded"
                );
                List<long> initialTraces = new List<long>();
                for (int i = 0; i < originalRecords; i++)
                {
                    initialTraces.Add(token._emissionBuffer[i].traceId);
                    Assert.That(
                        token._emissionBuffer[i].registrationHandle,
                        Is.EqualTo(i == 0 ? first : peer),
                        label + $": completed original handle {i}"
                    );
                }
                MessageRegistrationHandle fresh = first;
                if (mode == 2)
                {
                    token.RemoveRegistration(first);
                    fresh = global
                        ? RegisterGlobal(token, action, firstCallback)
                        : RegisterHandler(token, scenario, action, firstCallback);
                    Assert.That(
                        fresh.Slot,
                        Is.EqualTo(first.Slot),
                        label + ": actual staging slot reused"
                    );
                    Assert.That(
                        fresh,
                        Is.Not.EqualTo(first),
                        label + ": reused slot has a fresh identity"
                    );
                    token.RemoveRegistration(first);
                    Assert.That(
                        token._metadata.ContainsKey(fresh),
                        Is.True,
                        label + ": stale removal preserves new staging"
                    );
                    Assert.That(
                        token._metadata.ContainsKey(first),
                        Is.False,
                        label + ": old handle stays absent"
                    );
                }
                Emit(replacement, scenario);
                Assert.That(firstCalls, Is.EqualTo(2), label + ": fresh callback on rebound bus");
                Assert.That(
                    peerCalls,
                    Is.EqualTo(mode == 2 ? 2 : 1),
                    label + ": fresh peer survives exception/reset/stale cleanup"
                );
                Assert.That(
                    token._callCounts.Count,
                    Is.EqualTo(diagnostics ? 2 : 0),
                    label + ": exact diagnostic handle set"
                );
                if (diagnostics)
                {
                    int start = token._emissionBuffer.Count - 2;
                    Assert.That(
                        start,
                        Is.GreaterThanOrEqualTo(0),
                        label + ": two fresh records available"
                    );
                    Assert.That(
                        token._emissionBuffer[start].registrationHandle,
                        Is.EqualTo(mode == 2 ? peer : fresh),
                        label + ": fresh first record identity"
                    );
                    Assert.That(
                        token._emissionBuffer[start + 1].registrationHandle,
                        Is.EqualTo(mode == 2 ? fresh : peer),
                        label + ": fresh second record identity"
                    );
                    Assert.That(
                        token._emissionBuffer[start].traceId,
                        Is.EqualTo(replacement.EmissionId),
                        label + ": latest bus first origin"
                    );
                    Assert.That(
                        token._emissionBuffer[start + 1].traceId,
                        Is.EqualTo(replacement.EmissionId),
                        label + ": latest bus second origin"
                    );
                }
                List<long> expectedInitial = new List<long>();
                for (int i = 0; i < originalRecords; i++)
                {
                    expectedInitial.Add(mode == 1 ? 0L : outerOrigin);
                }
                Assert.That(
                    initialTraces,
                    Is.EqualTo(expectedInitial),
                    label + ": original trace preserved with existing reset semantics"
                );
                token.Dispose();
                Assert.That(token._metadata.Count, Is.Zero, label + ": disposal clears staging");
                Assert.That(token._callCounts.Count, Is.Zero, label + ": disposal clears counts");
                Assert.That(
                    token._emissionBuffer.Count,
                    Is.Zero,
                    label + ": disposal clears history"
                );
            }
            finally
            {
                token.Dispose();
                handler.active = false;
                _ = original.Trim(force: true);
                _ = replacement.Trim(force: true);
                _ = neutral.Trim(force: true);
                MessagingDebug.enabled = previousDebug;
            }
        }

        private static IEnumerable<TestCaseData> BindingLifetimeCases()
        {
            foreach (TestCaseData testCase in LifecycleCases())
            {
                yield return testCase.SetName(testCase.TestName + "_BindingLifetime");
            }
        }

        [TestCaseSource(nameof(BindingLifetimeCases))]
        [Category("MemoryReclaim")]
#if !UNITY_2021_3_OR_NEWER
        [NonParallelizable]
#endif
        public void CompletedSnapshotsReleaseReplacedBindingsAndTheirBus(
            MessageScenario scenario,
            bool action,
            bool global,
            int mode,
            bool diagnostics
        )
        {
            string label =
                $"{scenario},action={action},global={global},mode={mode},diagnostics={diagnostics}";
            bool previousDebug = MessagingDebug.enabled;
            MessagingDebug.enabled = false;
            MessageBus neutral = new MessageBus();
            MessageBus replacement = new MessageBus();
            using LeakWatcher leaks = new LeakWatcher(replacement, label: label);
            BindingLifetime graph = null;
            try
            {
                graph = CreateBindingLifetime(
                    scenario,
                    action,
                    global,
                    mode,
                    diagnostics,
                    neutral,
                    replacement
                );
                for (int i = 0; i < 3; i++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
                Assert.That(
                    graph.ReusedOnSameBus,
                    Is.True,
                    label + ": same-bus replay reuses exact binding"
                );
                Assert.That(
                    graph.ReplacedOnDifferentBus,
                    Is.EqualTo(mode == 0),
                    label + ": different-bus replay replaces exact binding"
                );
                try
                {
                    Assert.That(
                        graph.Binding.IsAlive,
                        Is.EqualTo(mode == 1),
                        label
                            + ": replaced/removed binding collects; disabled staging retains positive control"
                    );
                }
                catch (AssertionException)
                {
#if UNITY_2021_3_OR_NEWER
                    if (mode != 1)
                    {
                        NativeRetentionDiagnostics.Report(
                            "Binding:mode" + mode,
                            graph.Binding,
                            graph
                        );
                    }
#endif
                    throw;
                }
                try
                {
                    Assert.That(
                        graph.Bus.IsAlive,
                        Is.EqualTo(mode == 1),
                        label + ": obsolete binding bus follows its owner lifetime"
                    );
                }
                catch (AssertionException)
                {
#if UNITY_2021_3_OR_NEWER
                    if (mode != 1)
                    {
                        NativeRetentionDiagnostics.Report(
                            "BindingBus:mode" + mode,
                            graph.Bus,
                            graph
                        );
                    }
#endif
                    throw;
                }
                Assert.That(
                    graph.Counter.Calls,
                    Is.EqualTo(mode == 0 ? 3 : 2),
                    label + ": actual delivery on both activations"
                );
                Assert.That(
                    graph.Token.Enabled,
                    Is.EqualTo(mode == 0),
                    label + ": expected final activation"
                );
                Assert.That(
                    graph.Token._metadata.Count,
                    Is.EqualTo(mode == 2 ? 0 : 1),
                    label + ": only retained staging can replay"
                );
                graph.Token.Enable();
                Emit(replacement, scenario);
                Assert.That(
                    graph.Counter.Calls,
                    Is.EqualTo(
                        mode == 0 ? 4
                        : mode == 1 ? 3
                        : 2
                    ),
                    label + ": surviving staging replays its original callback"
                );
                graph.Token.Dispose();
                Assert.That(
                    graph.Token._metadata.Count,
                    Is.Zero,
                    label + ": disposal clears staging"
                );
                Assert.That(
                    graph.Token._callCounts.Count,
                    Is.Zero,
                    label + ": disposal clears counts"
                );
                Assert.That(
                    graph.Token._emissionBuffer.Count,
                    Is.Zero,
                    label + ": disposal clears history"
                );
            }
            finally
            {
                graph?.Token.Dispose();
                if (graph != null)
                {
                    graph.Handler.active = false;
                }
                _ = neutral.Trim(force: true);
                _ = replacement.Trim(force: true);
                MessagingDebug.enabled = previousDebug;
                GC.KeepAlive(graph);
                GC.KeepAlive(neutral);
                GC.KeepAlive(replacement);
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining
        )]
        private static BindingLifetime CreateBindingLifetime(
            MessageScenario scenario,
            bool action,
            bool global,
            int mode,
            bool diagnostics,
            MessageBus neutral,
            MessageBus replacement
        )
        {
            MessageBus original = new MessageBus();
            MessageHandler handler = new MessageHandler(new InstanceId(985), neutral)
            {
                active = true,
            };
            using LeakWatcher leaks = LeakWatcher.WatchWithSlots(
                original,
                handler: handler,
                label: "binding lifetime original"
            );
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, original);
            token.DiagnosticMode = diagnostics;
            BindingCounter counter = new BindingCounter();
            MessageRegistrationHandle handle = global
                ? RegisterGlobal(token, action, counter.Receive)
                : RegisterHandler(token, scenario, action, counter.Receive);
            token.Enable();
            Emit(original, scenario);
            object registration = MessageBusInspection.ReadField(
                ((Array)MessageBusInspection.ReadField(token, "_slots")).GetValue(handle.Slot),
                "Registration"
            );
            object binding = MessageBusInspection.ReadField(registration, "_binding");
            token.Disable();
            token.Enable();
            bool reused = ReferenceEquals(
                binding,
                MessageBusInspection.ReadField(registration, "_binding")
            );
            Emit(original, scenario);
            token.Disable();
            token.RetargetMessageBus(replacement, MessageBusRebindMode.PreserveRegistrations);
            bool replaced = false;
            if (mode == 0)
            {
                token.Enable();
                replaced = !ReferenceEquals(
                    binding,
                    MessageBusInspection.ReadField(registration, "_binding")
                );
                Emit(replacement, scenario);
            }
            else if (mode == 2)
            {
                token.RemoveRegistration(handle);
            }
            _ = original.Trim(force: true);
            return new BindingLifetime
            {
                Token = token,
                Handler = handler,
                Counter = counter,
                Binding = new WeakReference(binding),
                Bus = new WeakReference(original),
                ReusedOnSameBus = reused,
                ReplacedOnDifferentBus = replaced,
            };
        }

        private sealed class BindingLifetime
        {
            public MessageRegistrationToken Token;
            public MessageHandler Handler;
            public BindingCounter Counter;
            public WeakReference Binding;
            public WeakReference Bus;
            public bool ReusedOnSameBus;
            public bool ReplacedOnDifferentBus;
        }

        private sealed class BindingCounter
        {
            public int Calls;

            public void Receive() => Calls++;
        }

        public static IEnumerable<TestCaseData> RebindAllocationCases()
        {
            foreach (TestCaseData single in Cases())
            {
                if ((bool)single.Arguments[3] && (bool)single.Arguments[4])
                {
                    yield return new TestCaseData(
                        single.Arguments[0],
                        single.Arguments[1],
                        single.Arguments[2]
                    ).SetName(single.TestName + "_ReboundEmitZeroAlloc");
                }
            }
        }

        internal static Action CreateReboundAllocationEmit(
            MessageBus bus,
            MessageScenario scenario,
            bool global
        )
        {
            if (!global)
            {
                return () => Emit(bus, scenario);
            }
            GlobalProbeMessage message = new GlobalProbeMessage();
            InstanceId context = Context;
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                    return () => bus.UntargetedBroadcast(ref message);
                case MessageKind.Targeted:
                    return () => bus.TargetedBroadcast(ref context, ref message);
                case MessageKind.Broadcast:
                    return () => bus.SourcedBroadcast(ref context, ref message);
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario));
            }
        }

        private static void Warm(MessageBus bus)
        {
            for (int i = 0; i < 4; i++)
            {
                QuietMessage quiet = default;
                bus.UntargetedBroadcast(ref quiet);
            }
        }

        private static void Emit(MessageBus bus, MessageScenario scenario)
        {
            ProbeMessage message = default;
            InstanceId context = Context;
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                    bus.UntargetedBroadcast(ref message);
                    break;
                case MessageKind.Targeted:
                case MessageKind.TargetedWithoutTargeting:
                    bus.TargetedBroadcast(ref context, ref message);
                    break;
                case MessageKind.Broadcast:
                case MessageKind.BroadcastWithoutSource:
                    bus.SourcedBroadcast(ref context, ref message);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario));
            }
        }

        internal static MessageRegistrationHandle RegisterHandler(
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

        internal static MessageRegistrationHandle RegisterGlobal(
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

        private readonly struct ProbeMessage
            : IUntargetedMessage,
                ITargetedMessage,
                IBroadcastMessage
        {
            public Type MessageType => typeof(ProbeMessage);
        }

        private sealed class GlobalProbeMessage
            : IUntargetedMessage,
                ITargetedMessage,
                IBroadcastMessage
        {
            public Type MessageType => typeof(GlobalProbeMessage);
        }

        private readonly struct QuietMessage : IUntargetedMessage
        {
            public Type MessageType => typeof(QuietMessage);
        }
    }
}
