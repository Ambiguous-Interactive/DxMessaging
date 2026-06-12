#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using UnityEngine;
    using UnityEngine.TestTools;

    /// <summary>
    /// Regression coverage for the displaced-snapshot release hazard: a
    /// handler that mutates the same-type registration set (register OR
    /// deregister) and then REENTRANT re-emit of emits the same message type forces
    /// the nested emission's snapshot promotion to displace the snapshot the
    /// OUTER dispatch loop is still iterating. Before the fix the displaced
    /// snapshot was released (cleared and returned to the array pools)
    /// inline, which either null-ref'd the outer loop at
    /// <c>entry.handler.active</c> (flat untargeted path) or silently dropped
    /// the outer emission's remaining handlers (bucket paths), and at two or
    /// more nesting levels could re-issue the pooled arrays to a nested
    /// rebuild (cross-dispatch aliasing). The fix defers the release of a
    /// DISPLACED active snapshot until the outermost dispatch lease exits,
    /// mirroring the deferred ResetState teardown machinery. These tests pin
    /// exact invocation counts plus ordering traces so both the crash and the
    /// silent-drop/aliasing symptoms fail loudly. Frozen-snapshot semantics
    /// (see <see cref="MutationDuringEmissionTests"/>) are asserted
    /// unchanged: a handler registered mid-emission must NOT fire on the
    /// in-flight OUTER emission but MUST fire on the nested emission (a
    /// nested emit is a NEW emission); a handler deregistered mid-emission
    /// MUST still fire on the in-flight OUTER emission and must NOT fire on
    /// the nested emission.
    /// </summary>
    public sealed class ReentrantMutationEmissionTests : MessagingTestBase
    {
        private const int DeepNestingLevels = 3;

        /// <summary>
        /// A priority-0 handler registers a NEW same-type handler on the same
        /// bus and then re-emits the same type reentrant-style the same message type (guarded to a
        /// single nesting level). A priority-1 peer exists for the whole
        /// test. The outer emission must complete without throwing, the peer
        /// must fire on BOTH emissions (frozen outer snapshot + rebuilt
        /// nested snapshot), and the newly registered handler must fire ONLY
        /// on the nested emission (it is absent from the outer frozen
        /// snapshot; the nested emission is a new emission, so its rebuilt
        /// snapshot includes it).
        /// </summary>
        [UnityTest]
        public IEnumerator MutateThenReentrantEmitSameTypeDoesNotThrow(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(MutateThenReentrantEmitSameTypeDoesNotThrow) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int depth = 0;
            int mutatorCount = 0;
            int newcomerCount = 0;
            int peerCount = 0;
            bool newcomerRegistered = false;
            List<string> trace = new List<string>(8);

            MessageRegistrationHandle mutatorHandle = ScenarioCallbacks.RegisterCountingHandler(
                scenario,
                token,
                hostId,
                () =>
                {
                    ++mutatorCount;
                    trace.Add($"d{depth}:mutator");
                    if (depth != 0 || newcomerRegistered)
                    {
                        return;
                    }

                    // Mutation: stage a same-type pending snapshot...
                    newcomerRegistered = true;
                    _ = ScenarioCallbacks.RegisterCountingHandler(
                        scenario,
                        token,
                        hostId,
                        () =>
                        {
                            ++newcomerCount;
                            trace.Add($"d{depth}:newcomer");
                        },
                        priority: 0
                    );

                    // ...then in a reentrant emission emit the same type, forcing the
                    // nested acquire to promote the staged snapshot under a
                    // new emission id while the outer loop is mid-iteration.
                    ++depth;
                    try
                    {
                        ScenarioCallbacks.EmitForKind(scenario, hostId);
                    }
                    finally
                    {
                        --depth;
                    }
                },
                priority: 0
            );
            MessageRegistrationHandle peerHandle = ScenarioCallbacks.RegisterCountingHandler(
                scenario,
                token,
                hostId,
                () =>
                {
                    ++peerCount;
                    trace.Add($"d{depth}:peer");
                },
                priority: 1
            );

            Assert.DoesNotThrow(
                () => ScenarioCallbacks.EmitForKind(scenario, hostId),
                "[{0}] Register-then-reentrant-emit of the same type must not corrupt the outer dispatch snapshot. trace=[{1}]",
                scenario.Kind,
                string.Join(",", trace)
            );

            Assert.AreEqual(
                2,
                mutatorCount,
                "[{0}] Mutator must run once per emission (outer + nested). trace=[{1}]",
                scenario.Kind,
                string.Join(",", trace)
            );
            Assert.AreEqual(
                1,
                newcomerCount,
                "[{0}] Handler registered mid-emission must fire on the NESTED emission only, never the in-flight OUTER one. trace=[{1}]",
                scenario.Kind,
                string.Join(",", trace)
            );
            Assert.AreEqual(
                2,
                peerCount,
                "[{0}] Peer must fire on the nested emission AND on the outer emission's frozen snapshot. trace=[{1}]",
                scenario.Kind,
                string.Join(",", trace)
            );

            string[] expectedTrace =
            {
                "d0:mutator",
                "d1:mutator",
                "d1:newcomer",
                "d1:peer",
                "d0:peer",
            };
            CollectionAssert.AreEqual(
                expectedTrace,
                trace,
                "[{0}] Outer emission must resume its frozen snapshot after the nested emission. trace=[{1}]",
                scenario.Kind,
                string.Join(",", trace)
            );

            // Next emission uses the rebuilt steady-state snapshot: all three
            // handlers fire exactly once (pool-reuse sanity after the
            // displaced snapshot was released at lease exit).
            trace.Clear();
            ScenarioCallbacks.EmitForKind(scenario, hostId);
            Assert.AreEqual(
                3,
                mutatorCount,
                "[{0}] Mutator must fire once on the follow-up emission. trace=[{1}]",
                scenario.Kind,
                string.Join(",", trace)
            );
            Assert.AreEqual(
                2,
                newcomerCount,
                "[{0}] Newcomer must fire once on the follow-up emission. trace=[{1}]",
                scenario.Kind,
                string.Join(",", trace)
            );
            Assert.AreEqual(
                3,
                peerCount,
                "[{0}] Peer must fire once on the follow-up emission. trace=[{1}]",
                scenario.Kind,
                string.Join(",", trace)
            );

            token.RemoveRegistration(mutatorHandle);
            token.RemoveRegistration(peerHandle);
            yield break;
        }

        /// <summary>
        /// Same shape as
        /// <see cref="MutateThenReentrantEmitSameTypeDoesNotThrow"/> but the
        /// mid-emission mutation is DEREGISTERING the priority-1 peer. The
        /// outer emission must still fire the peer (its entry lives in the
        /// frozen outer snapshot); the nested emission's rebuilt snapshot
        /// must not include it.
        /// </summary>
        [UnityTest]
        public IEnumerator DeregisterThenReentrantEmitSameTypeDoesNotThrow(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(DeregisterThenReentrantEmitSameTypeDoesNotThrow) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int depth = 0;
            int mutatorCount = 0;
            int peerCount = 0;
            MessageRegistrationHandle peerHandle = default;
            List<string> trace = new List<string>(8);

            MessageRegistrationHandle mutatorHandle = ScenarioCallbacks.RegisterCountingHandler(
                scenario,
                token,
                hostId,
                () =>
                {
                    ++mutatorCount;
                    trace.Add($"d{depth}:mutator");
                    if (depth != 0 || peerHandle == default)
                    {
                        return;
                    }

                    // Mutation: deregister the peer (stages a same-type
                    // pending snapshot)...
                    token.RemoveRegistration(peerHandle);
                    peerHandle = default;

                    // ...then in a reentrant emission emit the same type.
                    ++depth;
                    try
                    {
                        ScenarioCallbacks.EmitForKind(scenario, hostId);
                    }
                    finally
                    {
                        --depth;
                    }
                },
                priority: 0
            );
            peerHandle = ScenarioCallbacks.RegisterCountingHandler(
                scenario,
                token,
                hostId,
                () =>
                {
                    ++peerCount;
                    trace.Add($"d{depth}:peer");
                },
                priority: 1
            );

            Assert.DoesNotThrow(
                () => ScenarioCallbacks.EmitForKind(scenario, hostId),
                "[{0}] Deregister-then-reentrant-emit of the same type must not corrupt the outer dispatch snapshot. trace=[{1}]",
                scenario.Kind,
                string.Join(",", trace)
            );

            Assert.AreEqual(
                2,
                mutatorCount,
                "[{0}] Mutator must run once per emission (outer + nested). trace=[{1}]",
                scenario.Kind,
                string.Join(",", trace)
            );
            Assert.AreEqual(
                1,
                peerCount,
                "[{0}] Peer deregistered mid-emission must still fire on the OUTER frozen snapshot and must NOT fire on the nested rebuilt one. trace=[{1}]",
                scenario.Kind,
                string.Join(",", trace)
            );

            string[] expectedTrace = { "d0:mutator", "d1:mutator", "d0:peer" };
            CollectionAssert.AreEqual(
                expectedTrace,
                trace,
                "[{0}] Peer must fire on the outer frame AFTER the nested emission completes. trace=[{1}]",
                scenario.Kind,
                string.Join(",", trace)
            );

            // Follow-up emission: the peer is gone for good; only the mutator
            // fires (which performs no further mutation).
            trace.Clear();
            ScenarioCallbacks.EmitForKind(scenario, hostId);
            Assert.AreEqual(
                3,
                mutatorCount,
                "[{0}] Mutator must fire once on the follow-up emission. trace=[{1}]",
                scenario.Kind,
                string.Join(",", trace)
            );
            Assert.AreEqual(
                1,
                peerCount,
                "[{0}] Deregistered peer must stay silent on the follow-up emission. trace=[{1}]",
                scenario.Kind,
                string.Join(",", trace)
            );

            token.RemoveRegistration(mutatorHandle);
            yield break;
        }

        /// <summary>
        /// Pool-aliasing canary: three nesting levels, each of which
        /// registers a new same-type handler and re-emits before recursing is
        /// cut off at depth 3. Every nesting level displaces the snapshot its
        /// parent is iterating; with the buggy inline release the displaced
        /// arrays are returned to the pool mid-flight and can be re-issued to
        /// a deeper rebuild, silently corrupting sibling dispatches. Exact
        /// per-level invocation counts pin both the crash and the silent
        /// aliasing.
        /// Emission tree (Ek = emission at nesting level k, Nk = handler
        /// registered by the driver during Ek-1):
        ///   E0: driver only (N1..N3 all registered mid-flight, frozen out).
        ///   E1: driver, N1.
        ///   E2: driver, N1, N2.
        ///   E3: driver, N1, N2, N3 (no further recursion).
        /// Totals: driver 4, N1 3, N2 2, N3 1.
        /// </summary>
        [UnityTest]
        public IEnumerator DeepNestedMutateEmitDoesNotCorrupt(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(DeepNestedMutateEmitDoesNotCorrupt) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int depth = 0;
            int driverCount = 0;
            int[] newcomerCounts = new int[DeepNestingLevels];
            bool[] registeredAtLevel = new bool[DeepNestingLevels];
            List<string> trace = new List<string>(16);

            MessageRegistrationHandle driverHandle = ScenarioCallbacks.RegisterCountingHandler(
                scenario,
                token,
                hostId,
                () =>
                {
                    ++driverCount;
                    trace.Add($"d{depth}:driver");
                    if (depth >= DeepNestingLevels || registeredAtLevel[depth])
                    {
                        return;
                    }

                    int level = depth;
                    registeredAtLevel[level] = true;
                    _ = ScenarioCallbacks.RegisterCountingHandler(
                        scenario,
                        token,
                        hostId,
                        () =>
                        {
                            ++newcomerCounts[level];
                            trace.Add($"d{depth}:n{level + 1}");
                        },
                        priority: 0
                    );

                    ++depth;
                    try
                    {
                        ScenarioCallbacks.EmitForKind(scenario, hostId);
                    }
                    finally
                    {
                        --depth;
                    }
                },
                priority: 0
            );

            Assert.DoesNotThrow(
                () => ScenarioCallbacks.EmitForKind(scenario, hostId),
                "[{0}] Three-deep mutate+re-emit must not corrupt any in-flight snapshot. trace=[{1}]",
                scenario.Kind,
                string.Join(",", trace)
            );

            Assert.AreEqual(
                DeepNestingLevels + 1,
                driverCount,
                "[{0}] Driver must run exactly once per nesting level (4 emissions). trace=[{1}]",
                scenario.Kind,
                string.Join(",", trace)
            );
            Assert.AreEqual(
                3,
                newcomerCounts[0],
                "[{0}] N1 (registered during E0) must fire in E1, E2 and E3 only. trace=[{1}]",
                scenario.Kind,
                string.Join(",", trace)
            );
            Assert.AreEqual(
                2,
                newcomerCounts[1],
                "[{0}] N2 (registered during E1) must fire in E2 and E3 only. trace=[{1}]",
                scenario.Kind,
                string.Join(",", trace)
            );
            Assert.AreEqual(
                1,
                newcomerCounts[2],
                "[{0}] N3 (registered during E2) must fire in E3 only. trace=[{1}]",
                scenario.Kind,
                string.Join(",", trace)
            );

            string[] expectedTrace =
            {
                "d0:driver",
                "d1:driver",
                "d2:driver",
                "d3:driver",
                "d3:n1",
                "d3:n2",
                "d3:n3",
                "d2:n1",
                "d2:n2",
                "d1:n1",
            };
            CollectionAssert.AreEqual(
                expectedTrace,
                trace,
                "[{0}] Each unwind level must resume its own frozen snapshot (registration order within the priority bucket). trace=[{1}]",
                scenario.Kind,
                string.Join(",", trace)
            );

            // Follow-up emission: no further mutation/recursion; the rebuilt
            // steady-state snapshot fires every handler exactly once.
            trace.Clear();
            ScenarioCallbacks.EmitForKind(scenario, hostId);
            Assert.AreEqual(
                DeepNestingLevels + 2,
                driverCount,
                "[{0}] Driver must fire once on the follow-up emission. trace=[{1}]",
                scenario.Kind,
                string.Join(",", trace)
            );
            Assert.AreEqual(
                4,
                newcomerCounts[0],
                "[{0}] N1 must fire once on the follow-up emission. trace=[{1}]",
                scenario.Kind,
                string.Join(",", trace)
            );
            Assert.AreEqual(
                3,
                newcomerCounts[1],
                "[{0}] N2 must fire once on the follow-up emission. trace=[{1}]",
                scenario.Kind,
                string.Join(",", trace)
            );
            Assert.AreEqual(
                2,
                newcomerCounts[2],
                "[{0}] N3 must fire once on the follow-up emission. trace=[{1}]",
                scenario.Kind,
                string.Join(",", trace)
            );

            token.RemoveRegistration(driverHandle);
            yield break;
        }
    }
}
#endif
