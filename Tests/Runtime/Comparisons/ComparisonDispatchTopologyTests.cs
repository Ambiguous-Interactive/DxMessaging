#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Tests.Runtime.Benchmarks;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;

    /// <summary>
    /// Pins the relationship between the cross-library comparison matrix and the
    /// DxMessaging-only dispatch-throughput table so the two families are never silently
    /// mistaken for measuring "the same" scenario when they deliberately measure different
    /// shapes. Each comparison scenario declares its nearest dispatch scenario and whether
    /// the two have identical registration shapes or different storage topology / fan-out.
    /// Matching topology does not establish timing equivalence across players. The map is the
    /// single source of truth documented in
    /// <c>docs/runbooks/perf-benchmark-methodology.md</c>; this suite fails the build if it
    /// drifts from the actual bridge fan-out, the dispatch scenario keys, or the scenario
    /// roster, so a future topology change cannot quietly desync the two tables.
    /// </summary>
    [Category("ComparisonContract")]
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
        /// shape is identical to the nearest dispatch scenario. Timing equivalence needs
        /// separate measurement evidence.
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
                    DispatchBenchmarkScenario.UntargetedFloodSixteenHandlersOnePriority,
                    isTrueTopologyTwin: true,
                    "Identical shape: 16 tokens, one active untargeted handler per token, "
                        + "priority zero, and the same SimpleUntargetedMessage payload."
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
                [ComparisonScenario.InterceptedPostProcessingDispatch] = new TopologyMapping(
                    1,
                    null,
                    isTrueTopologyTwin: false,
                    "One interceptor plus one post-processor plus one handler; no dispatch "
                        + "scenario combines hook kinds, so this row only supports within-matrix "
                        + "comparison against GlobalToOne, Filtered, and PostProcess."
                ),
                [ComparisonScenario.SubscribeUnsubscribeChurn] = new TopologyMapping(
                    1,
                    null,
                    isTrueTopologyTwin: false,
                    "Register/unregister churn cycle; the dispatch family has no "
                        + "subscribe/unsubscribe-throughput scenario."
                ),
                [ComparisonScenario.StructMessageNoBoxing] = new TopologyMapping(
                    1,
                    DispatchBenchmarkScenario.UntargetedFloodOneHandler,
                    isTrueTopologyTwin: false,
                    "Uses one token and one handler, but dispatches the canonical "
                        + "ComparisonStructPayload required across every technology instead of "
                        + "the dispatch row's SimpleUntargetedMessage. The storage topology "
                        + "matches while the closed generic payload path differs."
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
        public void TrueTopologyTwinsIncludeBothExistingUntargetedFanOuts()
        {
            List<ComparisonScenario> trueTwins = Map.Where(kvp => kvp.Value.IsTrueTopologyTwin)
                .Select(kvp => kvp.Key)
                .ToList();

            CollectionAssert.AreEquivalent(
                new[]
                {
                    ComparisonScenario.GlobalToOneSubscriber,
                    ComparisonScenario.GlobalToManySubscribers,
                },
                trueTwins,
                "GlobalToOne and GlobalToMany have existing exact internal fan-out rows. "
                    + "StructNoBox still uses a different payload. Update the map and runbook together."
            );
        }

        private static IEnumerable<TestCaseData> TrueTwinCases()
        {
            (
                ComparisonScenario comparison,
                DispatchBenchmarkScenario dispatch,
                int subscribers
            )[] twins =
            {
                (
                    ComparisonScenario.GlobalToOneSubscriber,
                    DispatchBenchmarkScenario.UntargetedFloodOneHandler,
                    1
                ),
                (
                    ComparisonScenario.GlobalToManySubscribers,
                    DispatchBenchmarkScenario.UntargetedFloodSixteenHandlersOnePriority,
                    16
                ),
            };
            foreach (
                (
                    ComparisonScenario comparison,
                    DispatchBenchmarkScenario dispatch,
                    int subscribers
                ) in twins
            )
            {
                foreach (int emits in new[] { 0, 1, 17 })
                {
                    foreach (bool diagnostics in new[] { false, true })
                    {
                        yield return new TestCaseData(
                            comparison,
                            dispatch,
                            subscribers,
                            emits,
                            diagnostics
                        ).SetName($"TrueTwin{comparison}Emits{emits}Diagnostics{diagnostics}");
                    }
                }
            }
        }

        [TestCaseSource(nameof(TrueTwinCases))]
        public void TrueTwinsObserveMatchingRegistrationsDispatchAndCleanup(
            ComparisonScenario scenario,
            DispatchBenchmarkScenario dispatch,
            int subscribers,
            int emits,
            bool globalDiagnostics
        )
        {
            string label =
                $"[{scenario}, {dispatch}, subscribers={subscribers}, emits={emits}, diagnostics={globalDiagnostics}]";
            using DiagnosticsScope diagnostics = new(
                globalDiagnostics ? DiagnosticsTarget.All : DiagnosticsTarget.Off,
                diagnosticsStackTraces: globalDiagnostics
            );
            TopologyMapping mapping = Map[scenario];
            Assert.IsTrue(mapping.IsTrueTopologyTwin, $"{label} The existing twin must be mapped.");
            Assert.AreEqual(
                dispatch,
                mapping.NearestDispatch,
                $"{label} The mapped row must match."
            );

            (string[] topology, long invocations) =
                DispatchThroughputBenchmarks.ObserveTopologyForContract(dispatch, emits);
            using DxMessagingBridge bridge = new();
            bridge.Prepare(scenario);
            MessageBus bus = bridge.BusForContract;
            CollectionAssert.AreEqual(
                topology,
                bridge.CaptureTopologyForContract(),
                $"{label} Actual bus counters, token ownership, payload, priority, context, and diagnostics must match."
            );
            Assert.AreEqual(
                subscribers,
                topology.Count(row => row.StartsWith("token:", StringComparison.Ordinal)),
                $"{label} Each subscriber needs one token, with no unused token."
            );
            for (int index = 0; index < subscribers; index++)
            {
                CollectionAssert.Contains(
                    topology,
                    $"token:{index}:True:False",
                    $"{label} Token {index} must be enabled with diagnostics disabled."
                );
                CollectionAssert.Contains(
                    topology,
                    $"registration:{index}:Untargeted:{typeof(SimpleUntargetedMessage).FullName}:0:none",
                    $"{label} Token {index} must register the exact payload at priority zero without a context."
                );
            }
            Assert.AreEqual(
                subscribers,
                bus.RegisteredUntargeted,
                $"{label} Every subscriber must be registered independently."
            );
            Assert.AreEqual(
                typeof(SimpleUntargetedMessage),
                bridge.DispatchedPayloadType(scenario),
                $"{label} Both twins must emit the same closed message type."
            );
            for (int index = 0; index < emits; index++)
            {
                bridge.EmitOnce();
            }
            Assert.AreEqual(
                (long)subscribers * emits,
                invocations,
                $"{label} Internal callbacks must reconcile."
            );
            Assert.AreEqual(
                invocations,
                bridge.ProgressMarker,
                $"{label} Comparison callbacks must reconcile."
            );
            bridge.Dispose();
            CollectionAssert.AreEqual(
                new[] { "bus:0:0:0:0:0:0", "diagnostics:False" },
                DispatchThroughputBenchmarks.CaptureTopologyForContract(
                    bus,
                    Array.Empty<MessageRegistrationToken>()
                ),
                $"{label} Disposing the real bridge must clear all six registration counters."
            );
        }
    }
}
#endif
