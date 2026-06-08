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
            long expectedInvocations =
                bridge.InvocationsPerOperation(scenario)
                * (warmupEmits + measurement.TotalOperations);
            Assert.AreEqual(
                expectedInvocations,
                bridge.ProgressMarker,
                $"{bridge.TechName} '{ComparisonScenarios.DisplayName(scenario)}' fan-out mismatch: "
                    + $"expected {expectedInvocations} invocations, observed {bridge.ProgressMarker}."
            );

            DispatchBenchmarkResult result = DispatchBenchmarkResult.ForEmitScenario(
                RowScenarioId(bridge.TechKey, scenario),
                runIndex: -1,
                measurement.OperationsPerSecond,
                measurement.AllocatedBytesDelta,
                measurement.ElapsedSeconds * 1000d
            );
            Debug.Log(result.ToStructuredLog());
            TestContext.Out.WriteLine(result.ToCsvRow());
        }
    }
}
#endif
