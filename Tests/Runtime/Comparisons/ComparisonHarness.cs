#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons
{
    using System;
    using DxMessaging.Tests.Runtime.Benchmarks;
    using NUnit.Framework;
    using UnityEngine;
    using Debug = UnityEngine.Debug;

    /// <summary>
    /// Shared runner used by THIS assembly's tests AND the future gated package-bridge
    /// assembly. Reuses <see cref="BenchmarkProtocol"/> for measurement methodology and
    /// <see cref="DispatchBenchmarkResult"/> for row emission so comparison rows share the
    /// exact CSV/log shape of the dispatch-throughput benchmarks.
    /// </summary>
    public static class ComparisonHarness
    {
        /// <summary>
        /// Row scenario id: "Comparison_&lt;TechKey&gt;_&lt;ScenarioKey&gt;" e.g.
        /// "Comparison_DxMessaging_GlobalToOne".
        /// </summary>
        public static string RowScenarioId(string techKey, ComparisonScenario scenario)
        {
            return $"Comparison_{techKey}_{ComparisonScenarios.Key(scenario)}";
        }

        public static void Run(
            Func<IMessagingTechBridge> bridgeFactory,
            ComparisonScenario scenario
        )
        {
            if (bridgeFactory == null)
            {
                throw new ArgumentNullException(nameof(bridgeFactory));
            }

            using IMessagingTechBridge bridge = bridgeFactory();
            if (bridge.RequiresPlayMode && !Application.isPlaying)
            {
                Assert.Ignore($"{bridge.TechName} requires PlayMode; skipping in EditMode.");
                return;
            }
            if (!bridge.Supports(scenario))
            {
                Assert.Ignore(
                    $"{bridge.TechName} does not support '{ComparisonScenarios.DisplayName(scenario)}'."
                );
                return;
            }

            bridge.Prepare(scenario);
            // Capture the warm-up count ONCE so the warm-up loop and the fan-out
            // assertion below stay coupled: expectedInvocations adds warmupEmits, so if
            // these two read different values the fan-out assertion breaks.
            int warmupEmits = ComparisonScenarios.WarmupEmits(scenario);
            BenchmarkMeasurement measurement = BenchmarkProtocol.Measure(
                () =>
                {
                    for (int i = 0; i < warmupEmits; i++)
                    {
                        bridge.EmitOnce();
                    }
                },
                () =>
                {
                    for (int i = 0; i < BenchmarkProtocol.BatchSize; i++)
                    {
                        bridge.EmitOnce();
                    }
                    return BenchmarkProtocol.BatchSize;
                }
            );
            // Reconcile against TotalEmittedOperations (timed window + the untimed
            // allocation-probe batch), NOT TotalOperations: BenchmarkProtocol.Measure drives
            // one extra emitBatch under AllocationProbe after the window, which advances
            // ProgressMarker too. Counting only the timed window under-counts by exactly one
            // BatchSize and the exact-equality fan-out check fails for every case. This stays
            // an EXACT correctness check (no tolerance): it must still catch a library that
            // drops, duplicates, or dedups any message.
            long invocationsPerOperation = bridge.InvocationsPerOperation(scenario);
            long expectedInvocations =
                invocationsPerOperation * (warmupEmits + measurement.TotalEmittedOperations);
            long observedInvocations = bridge.ProgressMarker;
            long deltaInvocations = observedInvocations - expectedInvocations;
            Assert.AreEqual(
                expectedInvocations,
                observedInvocations,
                $"{bridge.TechName} '{ComparisonScenarios.DisplayName(scenario)}' fan-out mismatch: "
                    + $"expected {expectedInvocations} invocations, observed {observedInvocations} "
                    + $"(delta {DescribeFanOutDelta(deltaInvocations, invocationsPerOperation)}). Breakdown: "
                    + $"invocationsPerOperation={invocationsPerOperation}, warmupEmits={warmupEmits}, "
                    + $"timedOps={measurement.TotalOperations}, allocationProbeOps={measurement.AllocationProbeOperations}, "
                    + $"totalEmittedOps={measurement.TotalEmittedOperations} (= warmup + timed + probe). "
                    + $"A delta of exactly +{BenchmarkProtocol.BatchSize} ops means the post-window "
                    + "allocation-probe batch is not being counted in the expected total; any other "
                    + "delta means the library dropped, duplicated, or deduped a message (a real "
                    + "fan-out/correctness defect, NOT a harness accounting bug)."
            );

            DispatchBenchmarkResult result = DispatchBenchmarkResult.ForEmitScenario(
                RowScenarioId(bridge.TechKey, scenario),
                runIndex: -1,
                measurement.OperationsPerSecond,
                measurement.GcAllocations,
                measurement.ElapsedSeconds * 1000d
            );
            Debug.Log(result.ToStructuredLog());
            TestContext.Out.WriteLine(result.ToCsvRow());
        }

        /// <summary>
        /// Formats a fan-out invocation delta for the mismatch diagnostic WITHOUT the silent
        /// truncation of plain integer division. When the delta is an exact multiple of
        /// <paramref name="invocationsPerOperation"/> it reads as "<c>D invocations = N ops</c>";
        /// when it is NOT, the leftover invocations are shown explicitly (<c>N ops + R leftover</c>
        /// always reconstructs the raw delta as <c>N * invocationsPerOperation + R</c>). A
        /// non-integral remainder is itself a strong signal: the library emitted a partial
        /// operation's worth of invocations, i.e. it fanned out inconsistently across emits -- a
        /// real correctness defect the old truncating "= N ops" form hid. A non-positive
        /// <paramref name="invocationsPerOperation"/> cannot be divided, so the raw invocation
        /// delta is reported as-is.
        /// </summary>
        internal static string DescribeFanOutDelta(
            long deltaInvocations,
            long invocationsPerOperation
        )
        {
            if (invocationsPerOperation <= 0)
            {
                return $"{deltaInvocations} invocations "
                    + $"(invocationsPerOperation={invocationsPerOperation})";
            }

            long deltaOperations = deltaInvocations / invocationsPerOperation;
            long remainder = deltaInvocations % invocationsPerOperation;
            if (remainder == 0)
            {
                return $"{deltaInvocations} invocations = {deltaOperations} ops";
            }

            return $"{deltaInvocations} invocations = {deltaOperations} ops + {remainder} leftover "
                + "(NON-INTEGRAL fan-out: a partial operation's worth of invocations, i.e. the "
                + "library fanned out inconsistently across emits -- a real correctness defect)";
        }
    }
}
#endif
