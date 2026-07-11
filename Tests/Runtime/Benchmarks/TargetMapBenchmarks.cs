#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Benchmarks
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;
    using UnityEngine;

    public enum TargetMapBenchmarkOperation
    {
        Hit,
        Miss,
        Churn,
    }

    /// <summary>
    /// Measures the <see cref="InstanceId"/> routing map independently from the
    /// published dispatch headline. Each row uses one targeted message type and one
    /// handler, varying only the number of target keys and the map operation.
    /// </summary>
    public sealed class TargetMapBenchmarks
    {
        private const int ConstructionAllocationAttempts = 8;
        private const int ConstructionTimingTrials = 7;
        private static object s_freshConstructionSink;

        [Test, Performance, Category("PerfBench")]
        [TestCaseSource(nameof(TargetMapBenchmarkCases))]
        public void TargetMapBenchmark(TargetMapBenchmarkCase benchmarkCase)
        {
            TargetMapBenchmarkResult result = RunScenario(benchmarkCase);
            Debug.Log(result.ToStructuredLog());
            TestContext.Out.WriteLine(TargetMapBenchmarkResult.CsvHeader);
            TestContext.Out.WriteLine(result.ToCsvRow());
        }

        public static TargetMapBenchmarkResult RunScenario(TargetMapBenchmarkCase benchmarkCase)
        {
            using TargetMapState state = new(benchmarkCase.KeyCount);
            BenchmarkMeasurement measurement = BenchmarkProtocol.Measure(
                () => state.RunMany(benchmarkCase.Operation, BenchmarkProtocol.WarmupEmits),
                () =>
                {
                    state.RunMany(benchmarkCase.Operation, BenchmarkProtocol.BatchSize);
                    return BenchmarkProtocol.BatchSize;
                }
            );

            long expectedInvocations =
                benchmarkCase.Operation == TargetMapBenchmarkOperation.Miss
                    ? 0
                    : BenchmarkProtocol.WarmupEmits + measurement.TotalEmittedOperations;
            Assert.AreEqual(
                expectedInvocations,
                state.Invocations,
                $"Target-map scenario '{benchmarkCase.Key}' delivered an unexpected number "
                    + $"of messages. Expected {expectedInvocations}, observed {state.Invocations}. "
                    + $"Breakdown: warmupOps={BenchmarkProtocol.WarmupEmits}, "
                    + $"timedOps={measurement.TotalOperations}, "
                    + $"allocationProbeOps={measurement.AllocationProbeOperations}."
            );
            Assert.AreEqual(
                benchmarkCase.KeyCount,
                state.PhysicalTargetSlots,
                $"Target-map scenario '{benchmarkCase.Key}' changed physical map cardinality."
            );
            state.ObserveStorage(out int targetMapEntries, out int targetMapCapacity);
            Assert.AreEqual(
                benchmarkCase.KeyCount,
                targetMapEntries,
                $"Target-map scenario '{benchmarkCase.Key}' changed its exact map cardinality."
            );

            return new TargetMapBenchmarkResult(
                benchmarkCase,
                measurement.TotalOperations,
                measurement.OperationsPerSecond,
                measurement.ElapsedSeconds * 1000d,
                measurement.GcAllocations,
                measurement.GcAllocatedBytes,
                targetMapEntries,
                targetMapCapacity,
                state.Invocations
            );
        }

        [Test, Performance, Category("PerfBench")]
        [TestCase(1)]
        [TestCase(4)]
        [TestCase(16)]
        [TestCase(256)]
        [TestCase(4096)]
        public void TargetMapFreshConstructionBenchmark(int keyCount)
        {
            TargetMapConstructionResult result = RunFreshConstruction(keyCount);
            Debug.Log(result.ToStructuredLog());
            TestContext.Out.WriteLine(TargetMapConstructionResult.CsvHeader);
            TestContext.Out.WriteLine(result.ToCsvRow());
        }

        internal static TargetMapConstructionResult RunFreshConstruction(int keyCount)
        {
            InstanceId[] keys = new InstanceId[keyCount];
            for (int index = 0; index < keys.Length; ++index)
            {
                keys[index] = new InstanceId(0x5A00_0000 + index);
            }
            object seed = MessageBus.CreateContextMapSeedForBenchmark();
            object prewarmed = MessageBus.CreatePopulatedContextMapForBenchmark(keys, seed);
            MessageBus.ObserveContextMapForBenchmark(
                prewarmed,
                out int prewarmedCount,
                out int capacity
            );
            Assert.AreEqual(keyCount, prewarmedCount);
            prewarmed = null;

            int batchSize = ConstructionBatchSize(keyCount);
            object[] sinks = new object[batchSize];
            double[] samples = new double[ConstructionTimingTrials];
            for (int trial = 0; trial < samples.Length; ++trial)
            {
                AllocationProbe.SettleHeapForMeasurement();
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                for (int index = 0; index < sinks.Length; ++index)
                {
                    sinks[index] = MessageBus.CreatePopulatedContextMapForBenchmark(keys, seed);
                }
                long end = System.Diagnostics.Stopwatch.GetTimestamp();
                for (int index = 0; index < sinks.Length; ++index)
                {
                    MessageBus.ObserveContextMapForBenchmark(
                        sinks[index],
                        out int count,
                        out int attemptCapacity
                    );
                    Assert.AreEqual(keyCount, count);
                    capacity = attemptCapacity;
                    sinks[index] = null;
                }
                samples[trial] =
                    (end - start)
                    / (double)System.Diagnostics.Stopwatch.Frequency
                    * 1000d
                    / batchSize;
            }

            long minAllocations = long.MaxValue;
            long minBytes = AllocationProbe.Unmeasured;
            try
            {
                AllocationProbe.SettleHeapForMeasurement();
                for (int attempt = 0; attempt < ConstructionAllocationAttempts; ++attempt)
                {
                    object candidate;
                    AllocationProbe.AllocationSample sample;
                    using (AllocationProbe.Window window = AllocationProbe.BeginWindow())
                    {
                        candidate = MessageBus.CreatePopulatedContextMapForBenchmark(keys, seed);
                        sample = window.SampleBoth();
                    }
                    s_freshConstructionSink = candidate;
                    MessageBus.ObserveContextMapForBenchmark(
                        candidate,
                        out int count,
                        out int attemptCapacity
                    );
                    Assert.AreEqual(keyCount, count);
                    if (
                        AllocationProbe.ShouldReplaceMinimumAttempt(
                            sample.Allocations,
                            sample.Bytes,
                            minAllocations,
                            minBytes
                        )
                    )
                    {
                        minAllocations = sample.Allocations;
                        minBytes = sample.Bytes;
                        capacity = attemptCapacity;
                    }
                    s_freshConstructionSink = null;
                }
            }
            finally
            {
                s_freshConstructionSink = null;
                AllocationProbe.SettleHeapForMeasurement();
            }
            if (minAllocations == long.MaxValue)
            {
                minAllocations = AllocationProbe.Unmeasured;
                minBytes = AllocationProbe.Unmeasured;
            }

            double wallClockMs = BenchmarkProtocol.Median(samples);
            return new TargetMapConstructionResult(
                keyCount,
                wallClockMs,
                1000d / Math.Max(wallClockMs, double.Epsilon),
                minAllocations,
                minBytes,
                capacity
            );
        }

        private static int ConstructionBatchSize(int keyCount)
        {
            switch (keyCount)
            {
                case 1:
                    return 1000;
                case 4:
                    return 250;
                case 16:
                    return 64;
                case 256:
                    return 4;
                case 4096:
                    return 1;
                default:
                    throw new ArgumentOutOfRangeException(nameof(keyCount));
            }
        }

        internal static TargetMapContractObservation RunOnceForContract(
            TargetMapBenchmarkCase benchmarkCase
        )
        {
            using TargetMapState state = new(benchmarkCase.KeyCount);
            InstanceId originalTarget = state.FirstTarget;
            state.RunMany(benchmarkCase.Operation, 1);
            long operationInvocations = state.Invocations;

            state.ResetInvocations();
            state.Emit(originalTarget);
            long originalTargetInvocations = state.Invocations;

            state.ResetInvocations();
            state.Emit(state.FirstTarget);
            long currentTargetInvocations = state.Invocations;
            state.ObserveStorage(out int targetMapEntries, out int targetMapCapacity);

            return new TargetMapContractObservation(
                operationInvocations,
                originalTargetInvocations,
                currentTargetInvocations,
                state.RegisteredTargets,
                state.PhysicalTargetSlots,
                targetMapEntries,
                targetMapCapacity
            );
        }

        private static IEnumerable<TestCaseData> TargetMapBenchmarkCases()
        {
            foreach (TargetMapBenchmarkCase benchmarkCase in TargetMapBenchmarkScenarios.All)
            {
                yield return new TestCaseData(benchmarkCase).SetName(benchmarkCase.Key);
            }
        }

        private sealed class TargetMapState : IDisposable
        {
            private const int TargetIdBase = 0x4D00_0000;
            private const int AlternateTargetIdBase = 0x4D10_0000;
            private const int MissingTargetIdBase = 0x4D20_0000;
            private readonly IDisposable _contextMapPoolScope;
            private readonly IDisposable _registryScope;
            private readonly MessageRegistrationToken _token;
            private readonly MessageHandler.FastHandler<TargetMapMessage> _handler;
            private readonly InstanceId[] _targets;
            private readonly MessageRegistrationHandle[] _handles;
            private int _cursor;

            internal TargetMapState(int keyCount)
            {
                _contextMapPoolScope = MessageBus.IsolateContextMapPoolForBenchmark();
                _registryScope = MessageBus.IsolateIdleSweepRegistryForBenchmark();
                try
                {
                    MessageBus = new MessageBus { DiagnosticsMode = false };
                    MessageHandler messageHandler = new(
                        new InstanceId(TargetIdBase - 1),
                        MessageBus
                    )
                    {
                        active = true,
                    };
                    _token = MessageRegistrationToken.Create(messageHandler, MessageBus);
                    _token.DiagnosticMode = false;
                    _handler = Handle;
                    _targets = new InstanceId[keyCount];
                    _handles = new MessageRegistrationHandle[keyCount];
                    for (int index = 0; index < keyCount; index++)
                    {
                        InstanceId target = new(TargetIdBase + index);
                        _targets[index] = target;
                        _handles[index] = _token.RegisterTargeted(target, _handler);
                    }

                    _token.Enable();
                }
                catch
                {
                    _registryScope.Dispose();
                    _contextMapPoolScope.Dispose();
                    throw;
                }
            }

            internal MessageBus MessageBus { get; }

            internal long Invocations { get; private set; }

            internal InstanceId FirstTarget => _targets[0];

            internal int RegisteredTargets => MessageBus.RegisteredTargeted;

            internal int PhysicalTargetSlots => MessageBus.OccupiedTargetSlots;

            internal void ObserveStorage(out int entries, out int capacity)
            {
                if (
                    !MessageBus.TryObserveTargetedHandleMapStorageForBenchmark<TargetMapMessage>(
                        out entries,
                        out capacity
                    )
                )
                {
                    throw new InvalidOperationException(
                        "The target-map benchmark must materialize its targeted handle map."
                    );
                }
            }

            internal void RunMany(TargetMapBenchmarkOperation operation, int count)
            {
                switch (operation)
                {
                    case TargetMapBenchmarkOperation.Hit:
                        for (int iteration = 0; iteration < count; iteration++)
                        {
                            Hit();
                        }
                        return;
                    case TargetMapBenchmarkOperation.Miss:
                        for (int iteration = 0; iteration < count; iteration++)
                        {
                            Miss();
                        }
                        return;
                    case TargetMapBenchmarkOperation.Churn:
                        for (int iteration = 0; iteration < count; iteration++)
                        {
                            Churn();
                        }
                        return;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
                }
            }

            internal void Emit(InstanceId target)
            {
                TargetMapMessage message = new();
                MessageBus.TargetedBroadcast(ref target, ref message);
            }

            internal void ResetInvocations()
            {
                Invocations = 0;
            }

            public void Dispose()
            {
                try
                {
                    _token.Dispose();
                }
                finally
                {
                    try
                    {
                        MessageBus.ResetState();
                    }
                    finally
                    {
                        try
                        {
                            _registryScope?.Dispose();
                        }
                        finally
                        {
                            _contextMapPoolScope?.Dispose();
                        }
                    }
                }
            }

            private int NextIndex()
            {
                int index = _cursor;
                _cursor++;
                if (_cursor == _targets.Length)
                {
                    _cursor = 0;
                }

                return index;
            }

            private void Hit()
            {
                Emit(_targets[NextIndex()]);
            }

            private void Miss()
            {
                Emit(new InstanceId(MissingTargetIdBase + NextIndex()));
            }

            private void Churn()
            {
                int index = NextIndex();
                InstanceId oldTarget = _targets[index];
                _token.RemoveRegistration(_handles[index]);
                int evicted = MessageBus.SweepDirtyTargetSlotForBenchmark<TargetMapMessage>(
                    oldTarget
                );
                if (evicted != 1)
                {
                    throw new InvalidOperationException(
                        $"Target-map churn expected one exact slot eviction, observed {evicted}."
                    );
                }
                InstanceId replacement =
                    oldTarget.Id < AlternateTargetIdBase
                        ? new InstanceId(AlternateTargetIdBase + index)
                        : new InstanceId(TargetIdBase + index);
                _targets[index] = replacement;
                _handles[index] = _token.RegisterTargeted(replacement, _handler);
                Emit(replacement);
            }

            private void Handle(ref TargetMapMessage message)
            {
                Invocations++;
            }
        }

        private readonly struct TargetMapMessage : ITargetedMessage<TargetMapMessage> { }
    }

    internal readonly struct TargetMapConstructionResult
    {
        internal const string CsvHeader =
            "keyCount,wallClockMs,operationsPerSecond,gcAllocations,gcAllocatedBytes,targetMapCapacity";

        internal TargetMapConstructionResult(
            int keyCount,
            double wallClockMs,
            double operationsPerSecond,
            long gcAllocations,
            long gcAllocatedBytes,
            int targetMapCapacity
        )
        {
            KeyCount = keyCount;
            WallClockMs = wallClockMs;
            OperationsPerSecond = operationsPerSecond;
            GcAllocations = gcAllocations;
            GcAllocatedBytes = gcAllocatedBytes;
            TargetMapCapacity = targetMapCapacity;
        }

        internal int KeyCount { get; }
        internal double WallClockMs { get; }
        internal double OperationsPerSecond { get; }
        internal long GcAllocations { get; }
        internal long GcAllocatedBytes { get; }
        internal int TargetMapCapacity { get; }

        internal string ToStructuredLog() =>
            string.Format(
                CultureInfo.InvariantCulture,
                "DXM_TARGET_MAP_CONSTRUCTION keyCount={0} wallClockMs={1:F6} operationsPerSecond={2:F3} gcAllocations={3} gcAllocatedBytes={4} targetMapCapacity={5}",
                KeyCount,
                WallClockMs,
                OperationsPerSecond,
                GcAllocations,
                GcAllocatedBytes,
                TargetMapCapacity
            );

        internal string ToCsvRow() =>
            string.Join(
                ",",
                KeyCount.ToString(CultureInfo.InvariantCulture),
                WallClockMs.ToString("F6", CultureInfo.InvariantCulture),
                OperationsPerSecond.ToString("F3", CultureInfo.InvariantCulture),
                GcAllocations.ToString(CultureInfo.InvariantCulture),
                GcAllocatedBytes.ToString(CultureInfo.InvariantCulture),
                TargetMapCapacity.ToString(CultureInfo.InvariantCulture)
            );
    }

    public static class TargetMapBenchmarkScenarios
    {
        private static readonly int[] KeyCounts = { 1, 4, 16, 256, 4096 };

        private static readonly TargetMapBenchmarkOperation[] Operations =
        {
            TargetMapBenchmarkOperation.Hit,
            TargetMapBenchmarkOperation.Miss,
            TargetMapBenchmarkOperation.Churn,
        };

        private static readonly TargetMapBenchmarkCase[] Cases = BuildCases();

        public static IReadOnlyList<TargetMapBenchmarkCase> All => Cases;

        private static TargetMapBenchmarkCase[] BuildCases()
        {
            TargetMapBenchmarkCase[] cases = new TargetMapBenchmarkCase[
                KeyCounts.Length * Operations.Length
            ];
            int writeIndex = 0;
            for (int keyIndex = 0; keyIndex < KeyCounts.Length; keyIndex++)
            {
                for (int operationIndex = 0; operationIndex < Operations.Length; operationIndex++)
                {
                    cases[writeIndex++] = new TargetMapBenchmarkCase(
                        KeyCounts[keyIndex],
                        Operations[operationIndex]
                    );
                }
            }

            return cases;
        }
    }

    public readonly struct TargetMapBenchmarkCase
    {
        public TargetMapBenchmarkCase(int keyCount, TargetMapBenchmarkOperation operation)
        {
            if (keyCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(keyCount));
            }

            KeyCount = keyCount;
            Operation = operation;
        }

        public int KeyCount { get; }

        public TargetMapBenchmarkOperation Operation { get; }

        public string Key => $"TargetMap_{KeyCount}_{Operation}";

        public override string ToString()
        {
            return Key;
        }
    }

    public readonly struct TargetMapBenchmarkResult
    {
        public const string CsvHeader =
            "scenario,keyCount,operation,totalOperations,operationsPerSecond,wallClockMs,gcAllocations,gcAllocatedBytes,targetMapEntries,targetMapCapacity,observedInvocations";

        internal TargetMapBenchmarkResult(
            TargetMapBenchmarkCase benchmarkCase,
            long totalOperations,
            double operationsPerSecond,
            double wallClockMs,
            long gcAllocations,
            long gcAllocatedBytes,
            int targetMapEntries,
            int targetMapCapacity,
            long observedInvocations
        )
        {
            BenchmarkCase = benchmarkCase;
            TotalOperations = totalOperations;
            OperationsPerSecond = operationsPerSecond;
            WallClockMs = wallClockMs;
            GcAllocations = gcAllocations;
            GcAllocatedBytes = gcAllocatedBytes;
            TargetMapEntries = targetMapEntries;
            TargetMapCapacity = targetMapCapacity;
            ObservedInvocations = observedInvocations;
        }

        public TargetMapBenchmarkCase BenchmarkCase { get; }

        public long TotalOperations { get; }

        public double OperationsPerSecond { get; }

        public double WallClockMs { get; }

        public long GcAllocations { get; }

        public long GcAllocatedBytes { get; }

        public int TargetMapEntries { get; }

        public int TargetMapCapacity { get; }

        public long ObservedInvocations { get; }

        public string ToStructuredLog()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "DXM_TARGET_MAP_BENCHMARK scenario={0} keyCount={1} operation={2} "
                    + "totalOperations={3} operationsPerSecond={4:F3} wallClockMs={5:F3} "
                    + "gcAllocations={6} gcAllocatedBytes={7} targetMapEntries={8} "
                    + "targetMapCapacity={9} observedInvocations={10}",
                BenchmarkCase.Key,
                BenchmarkCase.KeyCount,
                BenchmarkCase.Operation,
                TotalOperations,
                OperationsPerSecond,
                WallClockMs,
                GcAllocations,
                GcAllocatedBytes,
                TargetMapEntries,
                TargetMapCapacity,
                ObservedInvocations
            );
        }

        public string ToCsvRow()
        {
            return string.Join(
                ",",
                BenchmarkCase.Key,
                BenchmarkCase.KeyCount.ToString(CultureInfo.InvariantCulture),
                BenchmarkCase.Operation.ToString(),
                TotalOperations.ToString(CultureInfo.InvariantCulture),
                OperationsPerSecond.ToString("F3", CultureInfo.InvariantCulture),
                WallClockMs.ToString("F3", CultureInfo.InvariantCulture),
                FormatAllocation(GcAllocations),
                FormatAllocation(GcAllocatedBytes),
                TargetMapEntries.ToString(CultureInfo.InvariantCulture),
                TargetMapCapacity.ToString(CultureInfo.InvariantCulture),
                ObservedInvocations.ToString(CultureInfo.InvariantCulture)
            );
        }

        private static string FormatAllocation(long value)
        {
            return value == AllocationProbe.Unmeasured
                ? "n/a"
                : value.ToString(CultureInfo.InvariantCulture);
        }
    }

    internal readonly struct TargetMapContractObservation
    {
        internal TargetMapContractObservation(
            long operationInvocations,
            long originalTargetInvocations,
            long currentTargetInvocations,
            int registeredTargets,
            int physicalTargetSlots,
            int targetMapEntries,
            int targetMapCapacity
        )
        {
            OperationInvocations = operationInvocations;
            OriginalTargetInvocations = originalTargetInvocations;
            CurrentTargetInvocations = currentTargetInvocations;
            RegisteredTargets = registeredTargets;
            PhysicalTargetSlots = physicalTargetSlots;
            TargetMapEntries = targetMapEntries;
            TargetMapCapacity = targetMapCapacity;
        }

        internal long OperationInvocations { get; }

        internal long OriginalTargetInvocations { get; }

        internal long CurrentTargetInvocations { get; }

        internal int RegisteredTargets { get; }

        internal int PhysicalTargetSlots { get; }

        internal int TargetMapEntries { get; }

        internal int TargetMapCapacity { get; }
    }

    public sealed class TargetMapBenchmarkContractTests
    {
        [Test]
        public void ScenarioMatrixContainsEveryKeyCountAndOperationExactlyOnce()
        {
            int[] expectedKeyCounts = { 1, 4, 16, 256, 4096 };
            TargetMapBenchmarkOperation[] expectedOperations =
            {
                TargetMapBenchmarkOperation.Hit,
                TargetMapBenchmarkOperation.Miss,
                TargetMapBenchmarkOperation.Churn,
            };
            HashSet<string> observedKeys = new();

            foreach (TargetMapBenchmarkCase benchmarkCase in TargetMapBenchmarkScenarios.All)
            {
                CollectionAssert.Contains(expectedKeyCounts, benchmarkCase.KeyCount);
                CollectionAssert.Contains(expectedOperations, benchmarkCase.Operation);
                Assert.That(observedKeys.Add(benchmarkCase.Key), Is.True, benchmarkCase.Key);
            }

            Assert.AreEqual(
                expectedKeyCounts.Length * expectedOperations.Length,
                observedKeys.Count
            );
        }

        [TestCase(TargetMapBenchmarkOperation.Hit, 1, 1, 1)]
        [TestCase(TargetMapBenchmarkOperation.Miss, 0, 1, 1)]
        [TestCase(TargetMapBenchmarkOperation.Churn, 1, 0, 1)]
        public void OperationContractPreservesCardinalityAndRoutesExactly(
            TargetMapBenchmarkOperation operation,
            long expectedOperationInvocations,
            long expectedOriginalTargetInvocations,
            long expectedCurrentTargetInvocations
        )
        {
            TargetMapBenchmarkCase benchmarkCase = new(4, operation);
            TargetMapContractObservation observation = TargetMapBenchmarks.RunOnceForContract(
                benchmarkCase
            );

            Assert.AreEqual(expectedOperationInvocations, observation.OperationInvocations);
            Assert.AreEqual(
                expectedOriginalTargetInvocations,
                observation.OriginalTargetInvocations
            );
            Assert.AreEqual(expectedCurrentTargetInvocations, observation.CurrentTargetInvocations);
            Assert.AreEqual(benchmarkCase.KeyCount, observation.RegisteredTargets);
            Assert.AreEqual(benchmarkCase.KeyCount, observation.PhysicalTargetSlots);
            Assert.AreEqual(benchmarkCase.KeyCount, observation.TargetMapEntries);
            Assert.GreaterOrEqual(observation.TargetMapCapacity, observation.TargetMapEntries);
        }

        [TestCase(1)]
        [TestCase(4)]
        [TestCase(16)]
        [TestCase(256)]
        [TestCase(4096)]
        public void StorageContractReportsExactCountAndSufficientCapacity(int keyCount)
        {
            TargetMapContractObservation observation = TargetMapBenchmarks.RunOnceForContract(
                new TargetMapBenchmarkCase(keyCount, TargetMapBenchmarkOperation.Hit)
            );

            Assert.AreEqual(keyCount, observation.TargetMapEntries);
            Assert.GreaterOrEqual(observation.TargetMapCapacity, keyCount);
        }

        [Test]
        public void SmallMapCapacityIsRepeatableAfterLargeMapScenario()
        {
            TargetMapContractObservation before = TargetMapBenchmarks.RunOnceForContract(
                new TargetMapBenchmarkCase(1, TargetMapBenchmarkOperation.Hit)
            );
            TargetMapContractObservation large = TargetMapBenchmarks.RunOnceForContract(
                new TargetMapBenchmarkCase(4096, TargetMapBenchmarkOperation.Hit)
            );
            TargetMapContractObservation after = TargetMapBenchmarks.RunOnceForContract(
                new TargetMapBenchmarkCase(1, TargetMapBenchmarkOperation.Hit)
            );

            Assert.AreEqual(before.TargetMapCapacity, after.TargetMapCapacity);
            Assert.Greater(large.TargetMapCapacity, after.TargetMapCapacity);
        }

        [Test]
        public void ContextMapPoolScopeRestoresAbsentOverride()
        {
            Assert.IsFalse(MessageBus.ContextMapPoolOverrideActiveForBenchmark);

            using (MessageBus.IsolateContextMapPoolForBenchmark())
            {
                Assert.IsTrue(MessageBus.ContextMapPoolOverrideActiveForBenchmark);
            }

            Assert.IsFalse(MessageBus.ContextMapPoolOverrideActiveForBenchmark);
        }

        [Test]
        public void ContextMapPoolScopesRestoreNestedIdentityAndCapacity()
        {
            MessageBus.ContextMapPoolBenchmarkObservation baseline =
                MessageBus.ObserveContextMapPoolForBenchmark();
            using (MessageBus.IsolateContextMapPoolForBenchmark())
            {
                MessageBus.ContextMapPoolBenchmarkObservation outer =
                    MessageBus.ObserveContextMapPoolForBenchmark();
                Assert.AreNotSame(baseline.Identity, outer.Identity);
                Assert.AreEqual(baseline.MaxRetained, outer.MaxRetained);

                using (MessageBus.IsolateContextMapPoolForBenchmark())
                {
                    MessageBus.ContextMapPoolBenchmarkObservation inner =
                        MessageBus.ObserveContextMapPoolForBenchmark();
                    Assert.AreNotSame(outer.Identity, inner.Identity);
                    Assert.AreEqual(outer.MaxRetained, inner.MaxRetained);
                }

                Assert.AreSame(
                    outer.Identity,
                    MessageBus.ObserveContextMapPoolForBenchmark().Identity
                );
            }

            MessageBus.ContextMapPoolBenchmarkObservation restored =
                MessageBus.ObserveContextMapPoolForBenchmark();
            Assert.AreSame(baseline.Identity, restored.Identity);
            Assert.AreEqual(baseline.MaxRetained, restored.MaxRetained);
            Assert.AreEqual(baseline.UseLru, restored.UseLru);
        }

        [Test]
        public void ContextMapPoolScopeDisposalIsIdempotent()
        {
            object baselineIdentity = MessageBus.ObserveContextMapPoolForBenchmark().Identity;
            IDisposable scope = MessageBus.IsolateContextMapPoolForBenchmark();

            scope.Dispose();
            scope.Dispose();

            Assert.AreSame(
                baselineIdentity,
                MessageBus.ObserveContextMapPoolForBenchmark().Identity
            );
        }

        [Test]
        public void ContextMapPoolScopeRejectsOutOfOrderDisposalWithoutPoisoningRetry()
        {
            object baselineIdentity = MessageBus.ObserveContextMapPoolForBenchmark().Identity;
            IDisposable outer = MessageBus.IsolateContextMapPoolForBenchmark();
            object outerIdentity = MessageBus.ObserveContextMapPoolForBenchmark().Identity;
            IDisposable inner = MessageBus.IsolateContextMapPoolForBenchmark();
            object innerIdentity = MessageBus.ObserveContextMapPoolForBenchmark().Identity;

            try
            {
                Assert.Throws<InvalidOperationException>(() => outer.Dispose());
                Assert.AreSame(
                    innerIdentity,
                    MessageBus.ObserveContextMapPoolForBenchmark().Identity
                );

                inner.Dispose();
                Assert.AreSame(
                    outerIdentity,
                    MessageBus.ObserveContextMapPoolForBenchmark().Identity
                );
                outer.Dispose();
                Assert.AreSame(
                    baselineIdentity,
                    MessageBus.ObserveContextMapPoolForBenchmark().Identity
                );
            }
            finally
            {
                inner.Dispose();
                outer.Dispose();
            }
        }

        [Test]
        public void ContextMapPoolConfigurationPropagatesThroughNestedScopes()
        {
            MessageBus.ContextMapPoolBenchmarkObservation baseline =
                MessageBus.ObserveContextMapPoolForBenchmark();
            using (MessageBus.IsolateContextMapPoolForBenchmark())
            {
                using (MessageBus.IsolateContextMapPoolForBenchmark())
                {
                    MessageBus.ConfigureContextMapPoolForBenchmark(
                        !baseline.UseLru,
                        baseline.MaxRetained + 1
                    );
                }

                MessageBus.ContextMapPoolBenchmarkObservation propagated =
                    MessageBus.ObserveContextMapPoolForBenchmark();
                Assert.AreEqual(!baseline.UseLru, propagated.UseLru);
                Assert.AreEqual(baseline.MaxRetained + 1, propagated.MaxRetained);

                MessageBus.ConfigureContextMapPoolForBenchmark(
                    baseline.UseLru,
                    baseline.MaxRetained
                );
            }

            MessageBus.ContextMapPoolBenchmarkObservation restored =
                MessageBus.ObserveContextMapPoolForBenchmark();
            Assert.AreSame(baseline.Identity, restored.Identity);
            Assert.AreEqual(baseline.UseLru, restored.UseLru);
            Assert.AreEqual(baseline.MaxRetained, restored.MaxRetained);
        }

        [Test]
        public void ResultSchemaKeepsTopologyInCsvAndStructuredLog()
        {
            TargetMapBenchmarkResult result = new(
                new TargetMapBenchmarkCase(4, TargetMapBenchmarkOperation.Hit),
                1,
                2d,
                3d,
                -1,
                -1,
                4,
                7,
                8
            );

            string[] header = TargetMapBenchmarkResult.CsvHeader.Split(',');
            string[] row = result.ToCsvRow().Split(',');
            Assert.AreEqual(header.Length, row.Length);
            Assert.AreEqual("4", row[8]);
            Assert.AreEqual("7", row[9]);

            string structured = result.ToStructuredLog();
            StringAssert.Contains("targetMapEntries=4", structured);
            StringAssert.Contains("targetMapCapacity=7", structured);
        }

        [TestCase(1)]
        [TestCase(4)]
        [TestCase(16)]
        [TestCase(256)]
        [TestCase(4096)]
        public void FreshConstructionReportsDirectTopologyAndSchema(int keyCount)
        {
            TargetMapConstructionResult result = TargetMapBenchmarks.RunFreshConstruction(keyCount);
            Assert.AreEqual(keyCount, result.KeyCount);
            Assert.GreaterOrEqual(result.TargetMapCapacity, keyCount);
            Assert.AreEqual(
                TargetMapConstructionResult.CsvHeader.Split(',').Length,
                result.ToCsvRow().Split(',').Length
            );
            StringAssert.Contains("keyCount=" + keyCount, result.ToStructuredLog());
        }
    }
}
#endif
