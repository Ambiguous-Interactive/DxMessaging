#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Benchmarks
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using DxMessaging.Core;
    using DxMessaging.Core.DataStructure;
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

    public enum TargetMapKeyFamily
    {
        SequentialSanitized,
        SignedExtremes,
        PowerOfTwoStride,
        UniformSeededRandom,
        DesignedMixerCollision,
    }

    public enum TargetMapMissProbeKind
    {
        OutsideCluster,
        InsideCluster,
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
            using TargetMapState state = new(benchmarkCase);
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
            using TargetMapState state = new(benchmarkCase);
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
            private readonly IDisposable _contextMapPoolScope;
            private readonly IDisposable _registryScope;
            private readonly MessageRegistrationToken _token;
            private readonly MessageHandler.FastHandler<TargetMapMessage> _handler;
            private readonly InstanceId[] _targets;
            private readonly InstanceId[] _primaryTargets;
            private readonly InstanceId[] _replacementTargets;
            private readonly InstanceId[] _missTargets;
            private readonly MessageRegistrationHandle[] _handles;
            private int _cursor;

            internal TargetMapState(TargetMapBenchmarkCase benchmarkCase)
            {
                _contextMapPoolScope = MessageBus.IsolateContextMapPoolForBenchmark();
                _registryScope = MessageBus.IsolateIdleSweepRegistryForBenchmark();
                try
                {
                    MessageBus = new MessageBus { DiagnosticsMode = false };
                    MessageHandler messageHandler = new(new InstanceId(0x4CFF_FFFF), MessageBus)
                    {
                        active = true,
                    };
                    _token = MessageRegistrationToken.Create(messageHandler, MessageBus);
                    _token.DiagnosticMode = false;
                    _handler = Handle;
                    TargetMapKeySet keySet = TargetMapBenchmarkKeys.Create(
                        benchmarkCase.KeyFamily,
                        benchmarkCase.KeyCount
                    );
                    _targets = new InstanceId[benchmarkCase.KeyCount];
                    _primaryTargets = new InstanceId[benchmarkCase.KeyCount];
                    _replacementTargets = new InstanceId[benchmarkCase.KeyCount];
                    int[] missKeys =
                        benchmarkCase.MissProbeKind == TargetMapMissProbeKind.InsideCluster
                            ? keySet.InsideClusterMisses
                            : keySet.OutsideClusterMisses;
                    _missTargets = new InstanceId[benchmarkCase.KeyCount];
                    _handles = new MessageRegistrationHandle[benchmarkCase.KeyCount];
                    for (int index = 0; index < benchmarkCase.KeyCount; index++)
                    {
                        InstanceId target = new(keySet.RegisteredKeys[index]);
                        _targets[index] = target;
                        _primaryTargets[index] = target;
                        _replacementTargets[index] = new InstanceId(keySet.ReplacementKeys[index]);
                        _missTargets[index] = new InstanceId(missKeys[index]);
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
                Emit(_missTargets[NextIndex()]);
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
                    oldTarget.Id == _primaryTargets[index].Id
                        ? _replacementTargets[index]
                        : _primaryTargets[index];
                _targets[index] = replacement;
                _handles[index] = _token.RegisterTargeted(replacement, _handler);
                Emit(replacement);
            }

            private void Handle(in TargetMapMessage message)
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

    internal readonly struct TargetMapKeySet
    {
        internal TargetMapKeySet(
            int[] registeredKeys,
            int[] replacementKeys,
            int[] insideClusterMisses,
            int[] outsideClusterMisses,
            int expectedCapacity
        )
        {
            RegisteredKeys = registeredKeys;
            ReplacementKeys = replacementKeys;
            InsideClusterMisses = insideClusterMisses;
            OutsideClusterMisses = outsideClusterMisses;
            ExpectedCapacity = expectedCapacity;
        }

        internal int[] RegisteredKeys { get; }

        internal int[] ReplacementKeys { get; }

        internal int[] InsideClusterMisses { get; }

        internal int[] OutsideClusterMisses { get; }

        internal int ExpectedCapacity { get; }
    }

    internal static class TargetMapBenchmarkKeys
    {
        private const uint HashMultiplierOne = 0x7FEB352Du;
        private const uint HashMultiplierTwo = 0x846CA68Bu;
        private static readonly uint HashMultiplierOneInverse = MultiplicativeInverse(
            HashMultiplierOne
        );
        private static readonly uint HashMultiplierTwoInverse = MultiplicativeInverse(
            HashMultiplierTwo
        );

        internal static TargetMapKeySet Create(TargetMapKeyFamily family, int keyCount)
        {
            if (keyCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(keyCount));
            }
            if (!Enum.IsDefined(typeof(TargetMapKeyFamily), family))
            {
                throw new ArgumentOutOfRangeException(nameof(family), family, null);
            }

            int capacity = CapacityForKeyCount(keyCount);
            HashSet<int> used = new();
            return new TargetMapKeySet(
                CreateStream(family, keyCount, 0, false, capacity, used),
                CreateStream(family, keyCount, 1, false, capacity, used),
                CreateStream(family, keyCount, 3, false, capacity, used),
                CreateStream(family, keyCount, 2, true, capacity, used),
                capacity
            );
        }

        internal static int CapacityForKeyCount(int keyCount)
        {
            if (keyCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(keyCount));
            }

            int capacity = 4;
            while (capacity - (capacity >> 2) < keyCount)
            {
                capacity <<= 1;
            }
            return capacity;
        }

        private static int[] CreateStream(
            TargetMapKeyFamily family,
            int keyCount,
            int stream,
            bool outsideCluster,
            int capacity,
            HashSet<int> used
        )
        {
            int[] keys = new int[keyCount];
            uint random = RandomSeed(stream);
            for (int index = 0; index < keys.Length; ++index)
            {
                int key;
                switch (family)
                {
                    case TargetMapKeyFamily.SequentialSanitized:
                        key = 0x4D00_0000 + stream * 0x0010_0000 + index;
                        break;
                    case TargetMapKeyFamily.SignedExtremes:
                        int offset = stream * 0x0010_0000 + (index >> 1);
                        key = (index & 1) == 0 ? int.MinValue + offset : int.MaxValue - offset;
                        break;
                    case TargetMapKeyFamily.PowerOfTwoStride:
                        key = 0x0100_0000 + stream * 0x0100_0000 + index * 4096;
                        break;
                    case TargetMapKeyFamily.UniformSeededRandom:
                        do
                        {
                            random = NextRandom(random);
                            key = unchecked((int)random);
                        } while (used.Contains(key));
                        break;
                    case TargetMapKeyFamily.DesignedMixerCollision:
                        int bucket = outsideCluster ? capacity >> 1 : 0;
                        int ordinal = stream * keyCount + index;
                        key = KeyForBucketOrdinal(bucket, capacity, ordinal);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(family), family, null);
                }

                if (!used.Add(key))
                {
                    throw new InvalidOperationException(
                        $"Target-map key family '{family}' generated duplicate key {key}."
                    );
                }
                keys[index] = key;
            }
            return keys;
        }

        private static int KeyForBucketOrdinal(int bucket, int capacity, int ordinal)
        {
            int shift = 0;
            for (int value = capacity; 1 < value; value >>= 1)
            {
                shift++;
            }
            uint mixed = ((uint)ordinal << shift) | (uint)bucket;
            return unchecked((int)Unmix(mixed));
        }

        private static uint Unmix(uint hash)
        {
            hash = InvertXorShiftRight(hash, 16);
            hash = unchecked(hash * HashMultiplierTwoInverse);
            hash = InvertXorShiftRight(hash, 15);
            hash = unchecked(hash * HashMultiplierOneInverse);
            return InvertXorShiftRight(hash, 16);
        }

        private static uint InvertXorShiftRight(uint value, int shift)
        {
            uint result = value;
            for (int distance = shift; distance < 32; distance += shift)
            {
                result ^= value >> distance;
            }
            return result;
        }

        private static uint MultiplicativeInverse(uint value)
        {
            uint inverse = value;
            for (int iteration = 0; iteration < 5; ++iteration)
            {
                inverse = unchecked(inverse * (2u - value * inverse));
            }
            return inverse;
        }

        private static uint RandomSeed(int stream)
        {
            switch (stream)
            {
                case 0:
                    return 0xA341_316Cu;
                case 1:
                    return 0xC801_3EA4u;
                case 2:
                    return 0xAD90_777Du;
                case 3:
                    return 0x7E95_761Eu;
                default:
                    throw new ArgumentOutOfRangeException(nameof(stream));
            }
        }

        private static uint NextRandom(uint value)
        {
            value ^= value << 13;
            value ^= value >> 17;
            value ^= value << 5;
            return value;
        }
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
        public TargetMapBenchmarkCase(
            int keyCount,
            TargetMapBenchmarkOperation operation,
            TargetMapKeyFamily keyFamily = TargetMapKeyFamily.SequentialSanitized,
            TargetMapMissProbeKind missProbeKind = TargetMapMissProbeKind.OutsideCluster
        )
        {
            if (keyCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(keyCount));
            }

            KeyCount = keyCount;
            Operation = operation;
            KeyFamily = keyFamily;
            MissProbeKind = missProbeKind;
        }

        public int KeyCount { get; }

        public TargetMapBenchmarkOperation Operation { get; }

        public TargetMapKeyFamily KeyFamily { get; }

        public TargetMapMissProbeKind MissProbeKind { get; }

        public string Key =>
            KeyFamily == TargetMapKeyFamily.SequentialSanitized
            && MissProbeKind == TargetMapMissProbeKind.OutsideCluster
                ? $"TargetMap_{KeyCount}_{Operation}"
                : $"TargetMap_{KeyFamily}_{MissProbeKind}_{KeyCount}_{Operation}";

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
                Assert.AreEqual(TargetMapKeyFamily.SequentialSanitized, benchmarkCase.KeyFamily);
                Assert.AreEqual(TargetMapMissProbeKind.OutsideCluster, benchmarkCase.MissProbeKind);
                Assert.AreEqual(
                    $"TargetMap_{benchmarkCase.KeyCount}_{benchmarkCase.Operation}",
                    benchmarkCase.Key
                );
                Assert.That(observedKeys.Add(benchmarkCase.Key), Is.True, benchmarkCase.Key);
            }

            Assert.AreEqual(
                expectedKeyCounts.Length * expectedOperations.Length,
                observedKeys.Count
            );
        }

        [TestCase(TargetMapKeyFamily.SequentialSanitized)]
        [TestCase(TargetMapKeyFamily.SignedExtremes)]
        [TestCase(TargetMapKeyFamily.PowerOfTwoStride)]
        [TestCase(TargetMapKeyFamily.UniformSeededRandom)]
        [TestCase(TargetMapKeyFamily.DesignedMixerCollision)]
        public void KeyFamiliesAreDeterministicUniqueAndDisjoint(TargetMapKeyFamily family)
        {
            const int keyCount = 256;
            TargetMapKeySet first = TargetMapBenchmarkKeys.Create(family, keyCount);
            TargetMapKeySet second = TargetMapBenchmarkKeys.Create(family, keyCount);

            CollectionAssert.AreEqual(first.RegisteredKeys, second.RegisteredKeys);
            CollectionAssert.AreEqual(first.ReplacementKeys, second.ReplacementKeys);
            CollectionAssert.AreEqual(first.InsideClusterMisses, second.InsideClusterMisses);
            CollectionAssert.AreEqual(first.OutsideClusterMisses, second.OutsideClusterMisses);
            Assert.AreEqual(first.ExpectedCapacity, second.ExpectedCapacity);

            HashSet<int> unique = new();
            foreach (
                int[] stream in new[]
                {
                    first.RegisteredKeys,
                    first.ReplacementKeys,
                    first.InsideClusterMisses,
                    first.OutsideClusterMisses,
                }
            )
            {
                Assert.AreEqual(keyCount, stream.Length);
                foreach (int key in stream)
                {
                    Assert.That(unique.Add(key), Is.True, $"Duplicate key {key} in {family}.");
                }
            }

            Assert.AreEqual(keyCount * 4, unique.Count);
        }

        [Test]
        public void KeyFamiliesRetainTheirDeclaredShapes()
        {
            TargetMapKeySet sequential = TargetMapBenchmarkKeys.Create(
                TargetMapKeyFamily.SequentialSanitized,
                4
            );
            CollectionAssert.AreEqual(
                new[] { 0x4D00_0000, 0x4D00_0001, 0x4D00_0002, 0x4D00_0003 },
                sequential.RegisteredKeys
            );
            CollectionAssert.AreEqual(
                new[] { 0x4D10_0000, 0x4D10_0001, 0x4D10_0002, 0x4D10_0003 },
                sequential.ReplacementKeys
            );
            CollectionAssert.AreEqual(
                new[] { 0x4D20_0000, 0x4D20_0001, 0x4D20_0002, 0x4D20_0003 },
                sequential.OutsideClusterMisses
            );

            TargetMapKeySet extremes = TargetMapBenchmarkKeys.Create(
                TargetMapKeyFamily.SignedExtremes,
                4
            );
            CollectionAssert.AreEqual(
                new[] { int.MinValue, int.MaxValue, int.MinValue + 1, int.MaxValue - 1 },
                extremes.RegisteredKeys
            );

            TargetMapKeySet strides = TargetMapBenchmarkKeys.Create(
                TargetMapKeyFamily.PowerOfTwoStride,
                16
            );
            for (int index = 0; index < strides.RegisteredKeys.Length; ++index)
            {
                Assert.AreEqual(0, strides.RegisteredKeys[index] & 0xFFF);
                if (0 < index)
                {
                    Assert.AreEqual(
                        4096,
                        strides.RegisteredKeys[index] - strides.RegisteredKeys[index - 1]
                    );
                }
            }

            TargetMapKeySet random = TargetMapBenchmarkKeys.Create(
                TargetMapKeyFamily.UniformSeededRandom,
                4
            );
            CollectionAssert.AreEqual(
                new[] { 0x28F2_889A, 0x45DF_792A, unchecked((int)0xF5B7_E6B7u), 0x2541_42E7 },
                random.RegisteredKeys
            );
        }

        [TestCase(1)]
        [TestCase(4)]
        [TestCase(16)]
        [TestCase(256)]
        [TestCase(4096)]
        public void DesignedCollisionKeysMapToDeclaredCurrentMixerBuckets(int keyCount)
        {
            TargetMapKeySet keys = TargetMapBenchmarkKeys.Create(
                TargetMapKeyFamily.DesignedMixerCollision,
                keyCount
            );
            int mask = keys.ExpectedCapacity - 1;
            int outsideBucket = keys.ExpectedCapacity >> 1;

            foreach (int key in keys.RegisteredKeys)
            {
                Assert.AreEqual(0, IntKeyMap<object>.Bucket(key, mask));
            }
            foreach (int key in keys.ReplacementKeys)
            {
                Assert.AreEqual(0, IntKeyMap<object>.Bucket(key, mask));
            }
            foreach (int key in keys.InsideClusterMisses)
            {
                Assert.AreEqual(0, IntKeyMap<object>.Bucket(key, mask));
            }
            foreach (int key in keys.OutsideClusterMisses)
            {
                Assert.AreEqual(outsideBucket, IntKeyMap<object>.Bucket(key, mask));
            }
        }

        [TestCase(TargetMapKeyFamily.SequentialSanitized)]
        [TestCase(TargetMapKeyFamily.SignedExtremes)]
        [TestCase(TargetMapKeyFamily.PowerOfTwoStride)]
        [TestCase(TargetMapKeyFamily.UniformSeededRandom)]
        [TestCase(TargetMapKeyFamily.DesignedMixerCollision)]
        public void GeneratedKeyFamiliesRouteThroughProductionMap(TargetMapKeyFamily family)
        {
            const int keyCount = 16;
            TargetMapContractObservation hit = TargetMapBenchmarks.RunOnceForContract(
                new TargetMapBenchmarkCase(keyCount, TargetMapBenchmarkOperation.Hit, family)
            );
            TargetMapContractObservation miss = TargetMapBenchmarks.RunOnceForContract(
                new TargetMapBenchmarkCase(keyCount, TargetMapBenchmarkOperation.Miss, family)
            );
            TargetMapContractObservation churn = TargetMapBenchmarks.RunOnceForContract(
                new TargetMapBenchmarkCase(keyCount, TargetMapBenchmarkOperation.Churn, family)
            );

            Assert.AreEqual(1, hit.OperationInvocations);
            Assert.AreEqual(0, miss.OperationInvocations);
            Assert.AreEqual(1, churn.OperationInvocations);
            Assert.AreEqual(0, churn.OriginalTargetInvocations);
            Assert.AreEqual(1, churn.CurrentTargetInvocations);
            foreach (TargetMapContractObservation observation in new[] { hit, miss, churn })
            {
                Assert.AreEqual(keyCount, observation.RegisteredTargets);
                Assert.AreEqual(keyCount, observation.PhysicalTargetSlots);
                Assert.AreEqual(keyCount, observation.TargetMapEntries);
                Assert.GreaterOrEqual(observation.TargetMapCapacity, keyCount);
            }
        }

        [Test]
        public void CollisionMissProbeKindsRemainDistinctAndAbsent()
        {
            const int keyCount = 16;
            TargetMapContractObservation inside = TargetMapBenchmarks.RunOnceForContract(
                new TargetMapBenchmarkCase(
                    keyCount,
                    TargetMapBenchmarkOperation.Miss,
                    TargetMapKeyFamily.DesignedMixerCollision,
                    TargetMapMissProbeKind.InsideCluster
                )
            );
            TargetMapContractObservation outside = TargetMapBenchmarks.RunOnceForContract(
                new TargetMapBenchmarkCase(
                    keyCount,
                    TargetMapBenchmarkOperation.Miss,
                    TargetMapKeyFamily.DesignedMixerCollision,
                    TargetMapMissProbeKind.OutsideCluster
                )
            );

            Assert.AreEqual(0, inside.OperationInvocations);
            Assert.AreEqual(0, outside.OperationInvocations);
            Assert.AreEqual(keyCount, inside.TargetMapEntries);
            Assert.AreEqual(keyCount, outside.TargetMapEntries);
            Assert.AreEqual(inside.TargetMapCapacity, outside.TargetMapCapacity);
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
