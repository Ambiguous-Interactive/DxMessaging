#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons.External
{
#if MESSAGEPIPE_PRESENT
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Tests.Runtime.Benchmarks;
    using NUnit.Framework;

    /// <summary>
    /// Measures DxMessaging against MessagePipe inside one player process. MessagePipe is the
    /// unchanged in-process control used to remove host-wide movement from later A/B/A verdicts.
    /// </summary>
    [Category("Performance"), Category("PerfComparison"), Category("PairedPerf")]
    public sealed class PairedDxMessagingMessagePipeTests
    {
        private static IEnumerable<TestCaseData> Cases()
        {
            using IMessagingTechBridge messagePipe = new MessagePipeBridge();
            for (
                int scenarioIndex = 0;
                scenarioIndex < ComparisonScenarios.All.Length;
                scenarioIndex++
            )
            {
                ComparisonScenario scenario = ComparisonScenarios.All[scenarioIndex];
                if (
                    !messagePipe.Supports(scenario)
                    // SYNC: scripts/unity/require-comparison-rows.ps1 derives the same
                    // MessagePipe scenario set and excludes SubUnsub fail-closed.
                    || scenario == ComparisonScenario.SubscribeUnsubscribeChurn
                )
                {
                    continue;
                }

                yield return new TestCaseData(scenario).SetName(
                    $"PairedComparison_{scenarioIndex:D2}_{ComparisonScenarios.Key(scenario)}_DxMessaging_MessagePipe"
                );
            }
        }

        [Test]
        [TestCaseSource(nameof(Cases))]
        public void Benchmark(ComparisonScenario scenario)
        {
            DiagnosticsTarget previousTargets = IMessageBus.GlobalDiagnosticsTargets;
            bool previousStackTraces = IMessageBus.GlobalDiagnosticsStackTraces;
            try
            {
                IMessageBus.GlobalDiagnosticsTargets = DiagnosticsTarget.Off;
                IMessageBus.GlobalDiagnosticsStackTraces = false;
                PairedBenchmarkMeasurement measurement = PairedComparisonHarness.Run(
                    () => new DxMessagingBridge(),
                    () => new MessagePipeBridge(),
                    scenario
                );

                Assert.Greater(
                    measurement.FirstToSecondRatio,
                    0d,
                    $"Paired comparison '{scenario}' must produce a positive DxMessaging/MessagePipe ratio."
                );
                Assert.IsFalse(
                    double.IsNaN(measurement.CycleRatioSpreadPercent),
                    $"Paired comparison '{scenario}' must produce a numeric raw-cycle spread."
                );
            }
            finally
            {
                IMessageBus.GlobalDiagnosticsTargets = previousTargets;
                IMessageBus.GlobalDiagnosticsStackTraces = previousStackTraces;
            }
        }
    }
#endif
}
#endif
