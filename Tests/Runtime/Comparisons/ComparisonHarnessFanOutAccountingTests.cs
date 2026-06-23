#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons
{
    using System;
    using NUnit.Framework;

    /// <summary>
    /// Regression guard for the fan-out accounting bug. <see cref="ComparisonHarness.Run"/> asserts
    /// ProgressMarker == InvocationsPerOperation * (warmupEmits + total ops). Because
    /// <see cref="DxMessaging.Tests.Runtime.Benchmarks.BenchmarkProtocol.Measure"/> drives ONE extra
    /// emitBatch under the allocation probe AFTER the timed window, the bridge's ProgressMarker
    /// includes that batch too. The harness MUST reconcile against
    /// <c>BenchmarkMeasurement.TotalEmittedOperations</c> (timed window + probe batch) or it
    /// under-counts by exactly one BatchSize and throws -- the exact failure that broke all 44
    /// comparison cases. This drives the harness end to end with a counting fake bridge (no real
    /// messaging library), reproducing the failure in ONE measurement window rather than across the
    /// whole comparison matrix. Carries PerfBench because Run opens the real measurement window.
    /// </summary>
    [Category("Performance"), Category("PerfBench"), Category("PerfComparison")]
    public sealed class ComparisonHarnessFanOutAccountingTests
    {
        // fanOut 1 = GlobalToOne shape (CI diff +10000), 4 = PriorityOrdered shape (+40000),
        // 16 = GlobalToMany shape (+160000). Each value reproduces the corresponding pre-fix
        // CI fan-out diff (fanOut * BatchSize) when the probe batch is left uncounted.
        [Test, Category("PerfBench")]
        [TestCase(1)]
        [TestCase(4)]
        [TestCase(ComparisonScenarios.FanOutSubscribers)]
        public void RunReconcilesProgressMarkerIncludingAllocationProbeBatch(int fanOut)
        {
            Assert.DoesNotThrow(
                () =>
                    ComparisonHarness.Run(
                        () => new CountingFanOutBridge(fanOut),
                        ComparisonScenario.GlobalToManySubscribers
                    ),
                $"ComparisonHarness.Run threw for fan-out {fanOut}. The fan-out assertion's expected "
                    + "total must include the post-window allocation-probe batch (Measure drives one "
                    + "extra emitBatch under AllocationProbe); otherwise it under-counts by exactly one "
                    + "BatchSize. This is the fan-out-undercount regression."
            );
        }

        // Minimal bridge: ProgressMarker counts handler invocations (fanOut per EmitOnce), mirroring
        // a real bridge's accounting with no messaging library. Supports every scenario and reports a
        // constant fan-out, so the scenario argument does not change the arithmetic.
        private sealed class CountingFanOutBridge : IMessagingTechBridge
        {
            private readonly int _fanOut;
            private long _progress;

            public CountingFanOutBridge(int fanOut)
            {
                _fanOut = fanOut;
            }

            public string TechName => "CountingFanOut";

            public string TechKey => "CountingFanOut";

            public bool RequiresPlayMode => false;

            public long ProgressMarker => _progress;

            public bool Supports(ComparisonScenario scenario) => true;

            public void Prepare(ComparisonScenario scenario) { }

            public void EmitOnce() => _progress += _fanOut;

            public long InvocationsPerOperation(ComparisonScenario scenario) => _fanOut;

            public Type DispatchedPayloadType(ComparisonScenario scenario) =>
                typeof(ComparisonStructPayload);

            public void Dispose() { }
        }
    }
}
#endif
