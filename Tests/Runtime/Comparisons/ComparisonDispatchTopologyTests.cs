#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using DxMessaging.Tests.Runtime.Benchmarks;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;

    /// <summary>
    /// Pins the relationship between the cross-library comparison matrix and the
    /// DxMessaging-only dispatch-throughput table so the two families are never silently
    /// mistaken for measuring "the same" scenario when they deliberately measure different
    /// shapes. Each comparison scenario declares its nearest dispatch scenario and whether
    /// the two are a TRUE topology twin (identical registration shape, so the DxMessaging
    /// numbers must agree on the same run) or a deliberate divergence (different storage
    /// topology / fan-out count, so the numbers are expected to differ). The map is the
    /// single source of truth documented in
    /// <c>docs/runbooks/perf-benchmark-methodology.md</c>; this suite fails the build if it
    /// drifts from the actual bridge fan-out, the dispatch scenario keys, or the scenario
    /// roster, so a future topology change cannot quietly desync the two tables.
    /// </summary>
    [Category("Comparison")]
    public sealed class ComparisonDispatchTopologyTests
    {
        /// <summary>
        /// One row of the comparison-to-dispatch topology map. <see cref="DxFanOut"/> is the
        /// number of handler invocations a single DxMessaging EmitOnce produces for the
        /// comparison scenario; it must equal <see cref="DxMessagingBridge"/>'s declared
        /// <see cref="IMessagingTechBridge.InvocationsPerOperation"/>.
        /// <see cref="NearestDispatch"/> is the closest dispatch-throughput scenario (null
        /// when no dispatch scenario measures a comparable shape).
        /// <see cref="IsTrueTopologyTwin"/> is true only when the DxMessaging registration
        /// shape is IDENTICAL to the nearest dispatch scenario, so their measured throughput
        /// must agree within noise on the same run.
        /// </summary>
        private readonly struct TopologyMapping
        {
            public readonly long DxFanOut;
            public readonly DispatchBenchmarkScenario? NearestDispatch;
            public readonly bool IsTrueTopologyTwin;
            public readonly string Note;

            public TopologyMapping(
                long dxFanOut,
                DispatchBenchmarkScenario? nearestDispatch,
                bool isTrueTopologyTwin,
                string note
            )
            {
                DxFanOut = dxFanOut;
                NearestDispatch = nearestDispatch;
                IsTrueTopologyTwin = isTrueTopologyTwin;
                Note = note;
            }
        }

        // SINGLE SOURCE OF TRUTH for the comparison <-> dispatch topology relationship.
        // Keep this in lockstep with the table in docs/runbooks/perf-benchmark-methodology.md.
        private static readonly IReadOnlyDictionary<ComparisonScenario, TopologyMapping> Map =
            new Dictionary<ComparisonScenario, TopologyMapping>
            {
                [ComparisonScenario.GlobalToOneSubscriber] = new TopologyMapping(
                    1,
                    DispatchBenchmarkScenario.UntargetedFloodOneHandler,
                    isTrueTopologyTwin: true,
                    "Identical shape: one token, one untargeted handler, untargeted broadcast."
                ),
                [ComparisonScenario.GlobalToManySubscribers] = new TopologyMapping(
                    ComparisonScenarios.FanOutSubscribers,
                    null,
                    isTrueTopologyTwin: false,
                    "16 subscribers across 16 tokens; no dispatch scenario fans untargeted "
                        + "dispatch out to 16 handlers (the dispatch family caps untargeted "
                        + "fan-out at four)."
                ),
                [ComparisonScenario.KeyedToOneOfMany] = new TopologyMapping(
                    1,
                    DispatchBenchmarkScenario.TargetedFloodOneListener,
                    isTrueTopologyTwin: false,
                    "Registers 16 distinct targets and dispatches to ONE, measuring lookup "
                        + "selectivity; TargetedFlood_OneListener registers a single target, so "
                        + "the registration shape differs even though both fan out to one."
                ),
                [ComparisonScenario.PriorityOrderedDispatch] = new TopologyMapping(
                    4,
                    DispatchBenchmarkScenario.UntargetedFloodFourHandlersFourPriorities,
                    isTrueTopologyTwin: false,
                    "Comparison uses ONE token with four priorities (one MessageHandler, four "
                        + "handler-store entries); the dispatch twin uses FOUR tokens with one "
                        + "priority each. Same fan-out (4), different handler-store topology."
                ),
                [ComparisonScenario.FilteredDispatch] = new TopologyMapping(
                    1,
                    DispatchBenchmarkScenario.InterceptorHeavyFourInterceptors,
                    isTrueTopologyTwin: false,
                    "Comparison runs one interceptor plus one handler; the dispatch twin runs "
                        + "four interceptors plus one handler."
                ),
                [ComparisonScenario.PostProcessingDispatch] = new TopologyMapping(
                    1,
                    DispatchBenchmarkScenario.PostProcessingHeavyFourPostProcessors,
                    isTrueTopologyTwin: false,
                    "Comparison runs one post-processor plus one handler; the dispatch twin "
                        + "runs four post-processors plus one handler."
                ),
                [ComparisonScenario.SubscribeUnsubscribeChurn] = new TopologyMapping(
                    1,
                    null,
                    isTrueTopologyTwin: false,
                    "Register/unregister churn cycle; the dispatch family has no "
                        + "subscribe/unsubscribe-throughput scenario."
                ),
                [ComparisonScenario.StructMessageZeroCopy] = new TopologyMapping(
                    1,
                    DispatchBenchmarkScenario.UntargetedFloodOneHandler,
                    isTrueTopologyTwin: true,
                    "DxMessaging dispatches the same SimpleUntargetedMessage shape as "
                        + "GlobalToOne (one token, one handler); only the cross-library intent "
                        + "differs (this row exists to expose other libraries' payload boxing "
                        + "in the bytes/op column), so the DxMessaging throughput agrees with "
                        + "GlobalToOne and UntargetedFlood_OneHandler."
                ),
            };

        [Test]
        public void EveryComparisonScenarioDeclaresATopologyRelationship()
        {
            foreach (ComparisonScenario scenario in ComparisonScenarios.All)
            {
                Assert.IsTrue(
                    Map.ContainsKey(scenario),
                    $"Comparison scenario '{scenario}' has no entry in the dispatch-topology map. "
                        + "Adding a comparison scenario must declare whether it has a dispatch "
                        + "twin (and whether that twin is a true topology match) so the two perf "
                        + "tables never silently diverge. Update Map and the methodology runbook."
                );
            }

            Assert.AreEqual(
                ComparisonScenarios.All.Length,
                Map.Count,
                "The dispatch-topology map must have exactly one entry per comparison scenario; "
                    + "a stale entry means a comparison scenario was removed without updating the map."
            );
        }

        // ComparisonContractTests already asserts the bridge's runtime fan-out via
        // AssertEmitOnceAccounting; this test instead pins the MAP (the documented
        // single source of truth) against that same fan-out, so the runbook table and
        // the bridge cannot drift apart. The two are complementary, not redundant.
        [Test]
        public void DeclaredDxFanOutMatchesTheBridge()
        {
            using IMessagingTechBridge dxMessaging = new DxMessagingBridge();
            foreach ((ComparisonScenario scenario, TopologyMapping mapping) in Map)
            {
                Assert.AreEqual(
                    mapping.DxFanOut,
                    dxMessaging.InvocationsPerOperation(scenario),
                    $"DxMessaging fan-out for '{scenario}' drifted from the topology map. The map "
                        + "(and the methodology runbook) claim "
                        + $"{mapping.DxFanOut} invocation(s) per operation but the bridge declares "
                        + $"{dxMessaging.InvocationsPerOperation(scenario)}. Reconcile the two."
                );
            }
        }

        [Test]
        public void NearestDispatchScenarioKeysResolve()
        {
            foreach ((ComparisonScenario scenario, TopologyMapping mapping) in Map)
            {
                if (mapping.NearestDispatch is not DispatchBenchmarkScenario dispatch)
                {
                    continue;
                }

                // Referencing the dispatch Key here means renaming or removing a dispatch
                // scenario this map points at fails the build instead of silently rotting.
                Assert.IsNotEmpty(
                    DispatchBenchmarkScenarios.Key(dispatch),
                    $"Comparison scenario '{scenario}' points at dispatch scenario '{dispatch}', "
                        + "which must expose a stable non-empty Key."
                );
            }
        }

        [Test]
        public void TrueTopologyTwinsShareTheOneHandlerUntargetedShape()
        {
            // The only registration shape shared verbatim by both families is "one token, one
            // untargeted handler, untargeted broadcast of SimpleUntargetedMessage". Every true
            // twin must resolve to that shape so the "these cells must agree" promise in the
            // runbook is anchored to something concrete; a false twin must point at a different
            // dispatch scenario or none.
            using IMessagingTechBridge dxMessaging = new DxMessagingBridge();
            List<ComparisonScenario> trueTwins = Map.Where(kvp => kvp.Value.IsTrueTopologyTwin)
                .Select(kvp => kvp.Key)
                .ToList();

            CollectionAssert.AreEquivalent(
                new[]
                {
                    ComparisonScenario.GlobalToOneSubscriber,
                    ComparisonScenario.StructMessageZeroCopy,
                },
                trueTwins,
                "The true topology twins are exactly GlobalToOne and StructNoBox (both reduce to "
                    + "DxMessaging's one-handler untargeted shape). Changing this set means the "
                    + "comparison/dispatch parity guarantee moved; update the runbook to match."
            );

            foreach (ComparisonScenario scenario in trueTwins)
            {
                TopologyMapping mapping = Map[scenario];
                Assert.AreEqual(
                    DispatchBenchmarkScenario.UntargetedFloodOneHandler,
                    mapping.NearestDispatch,
                    $"True twin '{scenario}' must point at UntargetedFloodOneHandler, the "
                        + "one-handler untargeted dispatch shape it is supposed to agree with."
                );
                Assert.AreEqual(
                    1,
                    dxMessaging.InvocationsPerOperation(scenario),
                    $"True twin '{scenario}' must fan out to exactly one invocation to match the "
                        + "one-handler dispatch shape."
                );
                Assert.AreEqual(
                    typeof(SimpleUntargetedMessage),
                    dxMessaging.DispatchedPayloadType(scenario),
                    $"True twin '{scenario}' must dispatch SimpleUntargetedMessage, the same "
                        + "payload UntargetedFlood_OneHandler broadcasts."
                );
            }
        }
    }
}
#endif
