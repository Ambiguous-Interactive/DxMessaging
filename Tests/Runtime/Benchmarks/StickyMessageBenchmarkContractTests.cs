#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Benchmarks
{
    using System.Collections.Generic;
    using NUnit.Framework;

    [Category("PerfBench")]
    public sealed class StickyMessageBenchmarkContractTests
    {
        [TestCaseSource(nameof(Cases))]
        public void WorkloadConsumesEveryOperationAndKeepsOnlyItsExpectedRegistration(
            StickyBenchmarkCase benchmarkCase
        )
        {
            StickyWorkloadObservation observed = StickyMessageBenchmarks.RunForContract(
                benchmarkCase
            );
            long expectedCalls = benchmarkCase.Callback == StickyBenchmarkCallback.None ? 0 : 10;
            Assert.That(observed.Calls, Is.EqualTo(expectedCalls), benchmarkCase.Key);
            Assert.That(observed.Sum, Is.EqualTo(expectedCalls * 7), benchmarkCase.Key);
            Assert.That(
                observed.Found,
                Is.EqualTo(
                    benchmarkCase.Operation == StickyBenchmarkOperation.LatestPresent ? 10 : 0
                ),
                benchmarkCase.Key
            );
            Assert.That(
                observed.Registrations,
                Is.EqualTo(expectedCalls == 0 ? 0 : 1),
                benchmarkCase.Key
            );
        }

        private static IEnumerable<TestCaseData> Cases() => StickyMessageBenchmarks.Cases();
    }
}
#endif
