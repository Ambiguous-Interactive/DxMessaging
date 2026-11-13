#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Benchmarks
{
    using System;
    using System.Collections.Generic;
    using NUnit.Framework;

    [Category("PerfBench")]
    public sealed class StickyMessageBenchmarkContractTests
    {
        [TestCaseSource(
            typeof(StickyMessageLatencyBenchmarks),
            nameof(StickyMessageLatencyBenchmarks.Cases)
        )]
        public void SingleShotWorkloadReplaysOnlyTheNewCallbackAndKeepsItsOwnership(
            StickyLatencyCase benchmarkCase
        )
        {
            StickyLatencyObservation observed = StickyMessageLatencyBenchmarks.RunForContract(
                benchmarkCase
            );
            int expected =
                benchmarkCase.Operation == StickyLatencyOperation.Register
                    ? (benchmarkCase.HasLatest && benchmarkCase.Replay ? 1 : 0)
                    : (benchmarkCase.Callback == StickyBenchmarkCallback.None ? 0 : 1);
            Assert.That(observed.AddedCalls, Is.EqualTo(expected), benchmarkCase.Key);
            Assert.That(observed.ExistingCalls, Is.Zero, benchmarkCase.Key);
            Assert.That(observed.Sum, Is.EqualTo(expected * 7L), benchmarkCase.Key);
        }

        [Test]
        public void ColdMeasurementRetainsEveryCompletedTrialInAnImmutableSequence()
        {
            int prepared = 0;
            int executed = 0;
            int disposed = 0;
            int[] observed = new int[3];
            ColdLatencyMeasurement measurement = BenchmarkProtocol.MeasureColdLatency(
                3,
                index =>
                {
                    ++prepared;
                    return index;
                },
                index => observed[executed++] = index,
                index => ++disposed
            );
            Assert.That(prepared, Is.EqualTo(3));
            Assert.That(executed, Is.EqualTo(3));
            Assert.That(disposed, Is.EqualTo(3));
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, observed);
            Assert.That(measurement.Trials, Is.EqualTo(3));
            Assert.That(measurement.RawTrials.Count, Is.EqualTo(3));
            for (int index = 0; index < measurement.RawTrials.Count; ++index)
            {
                ColdLatencyTrial trial = measurement.RawTrials[index];
                Assert.That(trial.Index, Is.EqualTo(index));
                Assert.That(trial.WallClockMs, Is.GreaterThanOrEqualTo(0d), $"trial={index}");
                Assert.That(
                    trial.GcAllocations,
                    Is.GreaterThanOrEqualTo(AllocationProbe.Unmeasured),
                    $"trial={index}"
                );
                Assert.That(
                    trial.GcAllocatedBytes,
                    Is.GreaterThanOrEqualTo(AllocationProbe.Unmeasured),
                    $"trial={index}"
                );
            }
            IList<ColdLatencyTrial> writable = (IList<ColdLatencyTrial>)measurement.RawTrials;
            Assert.That(writable.IsReadOnly, Is.True);
            Assert.Throws<NotSupportedException>(() => writable.RemoveAt(0));
            Assert.That(
                measurement.RawTrials.Count,
                Is.EqualTo(3),
                "A reader cannot discard an adverse trial."
            );
        }

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
