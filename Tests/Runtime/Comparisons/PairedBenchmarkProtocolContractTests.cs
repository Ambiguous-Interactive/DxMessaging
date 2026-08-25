#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Threading;
    using DxMessaging.Tests.Runtime.Benchmarks;
    using NUnit.Framework;

    [Category("ComparisonContract")]
    public sealed class PairedBenchmarkProtocolContractTests
    {
        [Test]
        public void PublishedDefaultsBalanceEveryPositionAndRetainMinimumActiveTime()
        {
            Assert.AreEqual(
                4,
                BenchmarkProtocol.PairedMeasurementCycles,
                "Published evidence and its fail-closed validator require four raw cycles."
            );
            Assert.AreEqual(
                2_500,
                BenchmarkProtocol.PairedMeasurementCycles
                    * BenchmarkProtocol.PairedMinimumCycleActiveMilliseconds,
                "Each workload must retain at least 2.5 seconds of measured active time."
            );
            Assert.AreEqual(
                3d,
                BenchmarkProtocol.PairedMaterialityBandPercent,
                "The paired evidence band must remain aligned with the candidate verdict gate."
            );
        }

        [Test]
        public void MeasurePairedRepeatsUntilBothAsymmetricWorkloadsReachMinimumActiveTime()
        {
            TimeSpan minimum = TimeSpan.FromMilliseconds(2);
            int firstCalls = 0;
            int secondCalls = 0;
            PairedBenchmarkMeasurement measurement = BenchmarkProtocol.MeasurePaired(
                null,
                () => SpinBatch(ref firstCalls, 10_000),
                null,
                () => SpinBatch(ref secondCalls, 500_000),
                cycles: 1,
                minimumCycleActiveDuration: minimum
            );

            Assert.GreaterOrEqual(
                measurement.Cycles[0].First.ElapsedSeconds,
                minimum.TotalSeconds,
                "The faster workload must keep repeating after the slower side reaches the minimum."
            );
            Assert.GreaterOrEqual(
                measurement.Cycles[0].Second.ElapsedSeconds,
                minimum.TotalSeconds,
                "The slower workload must also reach the minimum active time."
            );
            Assert.AreEqual(
                firstCalls,
                secondCalls,
                "Every completed ABBA/BAAB super-cycle must retain equal batch counts."
            );
        }

        [Test]
        public void MeasurePairedInterleavesSuperCyclesAndRetainsAllOperations()
        {
            List<char> order = new();
            PairedBenchmarkMeasurement measurement = BenchmarkProtocol.MeasurePaired(
                null,
                () => RecordBatch(order, 'A', 10),
                null,
                () => RecordBatch(order, 'B', 20),
                cycles: 2,
                minimumCycleActiveDuration: TimeSpan.FromTicks(1)
            );

            CollectionAssert.AreEqual(
                new[]
                {
                    'A',
                    'B',
                    'B',
                    'A',
                    'B',
                    'A',
                    'A',
                    'B',
                    'A',
                    'B',
                    'B',
                    'A',
                    'B',
                    'A',
                    'A',
                    'B',
                },
                order,
                "Every paired cycle must use the predeclared ABBA then BAAB batch order."
            );
            Assert.AreEqual(
                80,
                measurement.First.TotalOperations,
                "Eight retained first-workload batches must contribute every reported operation."
            );
            Assert.AreEqual(
                160,
                measurement.Second.TotalOperations,
                "Eight retained second-workload batches must contribute every reported operation."
            );
            Assert.AreEqual(
                2,
                measurement.CycleRatios.Count,
                "The protocol must retain one raw ratio for every cycle."
            );
            Assert.IsFalse(
                measurement.CycleRatios is double[],
                "Callers must not be able to cast retained cycle evidence back to a mutable array."
            );
            Assert.Throws<NotSupportedException>(
                () => ((IList<double>)measurement.CycleRatios)[0] = 999d,
                "Retained raw cycle evidence must reject mutation through IList<double>."
            );
            Assert.IsFalse(
                measurement.Cycles is PairedCycleMeasurement[],
                "Callers must not be able to cast retained cycle measurements to a mutable array."
            );
            Assert.Throws<NotSupportedException>(
                () =>
                    ((IList<PairedCycleMeasurement>)measurement.Cycles)[0] = measurement.Cycles[0],
                "Retained cycle measurements must reject mutation through IList<T>."
            );
        }

        [TestCase(0)]
        [TestCase(-1)]
        public void MeasurePairedRejectsNonPositiveCycleCount(int cycles)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () =>
                    BenchmarkProtocol.MeasurePaired(
                        null,
                        () => 1,
                        null,
                        () => 1,
                        cycles,
                        TimeSpan.FromTicks(1)
                    ),
                $"Cycle count {cycles} must fail before the paired measurement starts."
            );
        }

        [TestCase(0)]
        [TestCase(-1)]
        public void MeasurePairedRejectsNonPositiveCycleActiveDuration(long durationTicks)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () =>
                    BenchmarkProtocol.MeasurePaired(
                        null,
                        () => 1,
                        null,
                        () => 1,
                        cycles: 1,
                        minimumCycleActiveDuration: TimeSpan.FromTicks(durationTicks)
                    ),
                "A non-positive paired cycle duration must fail before measurement starts."
            );
        }

        [Test]
        public void MeasurePairedRejectsCycleDurationBeyondStopwatchRange()
        {
            double requestedTicks = Stopwatch.Frequency * TimeSpan.MaxValue.TotalSeconds;
            if (requestedTicks < long.MaxValue)
            {
                Assert.Pass("TimeSpan cannot express a duration beyond this stopwatch's range.");
            }

            Assert.Throws<ArgumentOutOfRangeException>(
                () =>
                    BenchmarkProtocol.MeasurePaired(
                        null,
                        () => 1,
                        null,
                        () => 1,
                        cycles: 1,
                        minimumCycleActiveDuration: TimeSpan.MaxValue
                    ),
                "An unrepresentable duration must fail instead of collapsing to a one-tick cycle."
            );
        }

        [TestCase(true)]
        [TestCase(false)]
        public void MeasurePairedRejectsNullBatch(bool firstIsNull)
        {
            Func<int> validBatch = () => 1;
            Assert.Throws<ArgumentNullException>(
                () =>
                    BenchmarkProtocol.MeasurePaired(
                        null,
                        firstIsNull ? null : validBatch,
                        null,
                        firstIsNull ? validBatch : null,
                        cycles: 1,
                        minimumCycleActiveDuration: TimeSpan.FromTicks(1)
                    ),
                $"The {(firstIsNull ? "first" : "second")} null batch must fail before measurement starts."
            );
        }

        [TestCase(0)]
        [TestCase(-1)]
        public void MeasurePairedRejectsNonPositiveBatchOperationCount(int operations)
        {
            Assert.Throws<InvalidOperationException>(
                () =>
                    BenchmarkProtocol.MeasurePaired(
                        null,
                        () => operations,
                        null,
                        () => 1,
                        cycles: 1,
                        minimumCycleActiveDuration: TimeSpan.FromTicks(1)
                    ),
                $"Batch operation count {operations} must fail instead of producing a false rate."
            );
        }

        private static int RecordBatch(List<char> order, char workload, int operations)
        {
            order.Add(workload);
            Thread.SpinWait(1_000);
            return operations;
        }

        private static int SpinBatch(ref int calls, int iterations)
        {
            calls++;
            Thread.SpinWait(iterations);
            return 1;
        }
    }
}
#endif
