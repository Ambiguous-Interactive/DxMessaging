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

    /// <summary>
    /// Measures the mutation-heavy path that stages a flat dispatch snapshot and rebuilds it on
    /// the immediately following emission. Each operation keeps the live handler cardinality
    /// constant by disabling one registration generation and enabling a distinguishable
    /// replacement before dispatch.
    /// </summary>
    public sealed class SnapshotRebuildBenchmarks
    {
        private const int WarmupOperations = 1_000;

        [Test, Performance, Category("PerfBench")]
        [TestCaseSource(nameof(BenchmarkCases))]
        public void SnapshotRebuildBenchmark(SnapshotRebuildBenchmarkCase benchmarkCase)
        {
            SnapshotRebuildBenchmarkResult result = RunScenario(benchmarkCase);
            Debug.Log(result.ToStructuredLog());
            TestContext.Out.WriteLine(result.ToCsvRow());
        }

        public static SnapshotRebuildBenchmarkResult RunScenario(
            SnapshotRebuildBenchmarkCase benchmarkCase
        )
        {
            using SnapshotRebuildState state = new(benchmarkCase.Cardinality);
            BenchmarkMeasurement measurement = BenchmarkProtocol.Measure(
                () => state.RunMany(WarmupOperations),
                () =>
                {
                    state.RunMany(BenchmarkProtocol.BatchSize);
                    return BenchmarkProtocol.BatchSize;
                }
            );

            long expectedCalls =
                (long)benchmarkCase.Cardinality
                * (WarmupOperations + measurement.TotalEmittedOperations);
            Assert.AreEqual(
                expectedCalls,
                state.Calls,
                $"{benchmarkCase.Key} changed dispatch fan-out during snapshot rebuild."
            );
            Assert.AreEqual(benchmarkCase.Cardinality, state.LiveRegistrations);
            Assert.AreEqual(
                benchmarkCase.Cardinality + 1,
                state.OccupiedTypeSlots,
                "After warmup has replaced every handler, topology must contain the active bus "
                    + "slot plus one dirty-empty displaced typed-handler slot per cardinality entry."
            );
            Assert.Zero(
                state.StaleDispatchCalls,
                $"{benchmarkCase.Key} reused a stale flat snapshot after replacement."
            );
            MessageBus.FlatSnapshotStorageObservation storage = state.ObserveSnapshotStorage();
            Assert.AreEqual(benchmarkCase.Cardinality, storage.EntryCount);
            Assert.GreaterOrEqual(storage.ArrayCapacity, storage.EntryCount);
            Assert.AreEqual(
                1,
                storage.EmptyHolderPoolCount,
                "Sequential rebuild keeps exactly one released empty holder beside the active holder."
            );

            return new SnapshotRebuildBenchmarkResult(
                benchmarkCase,
                measurement.TotalOperations,
                measurement.OperationsPerSecond,
                measurement.ElapsedSeconds * 1000d,
                measurement.GcAllocations,
                measurement.GcAllocatedBytes,
                state.Calls,
                state.LiveRegistrations,
                state.OccupiedTypeSlots,
                state.StaleDispatchCalls,
                storage.EntryCount,
                storage.ArrayCapacity,
                storage.EmptyHolderPoolCount
            );
        }

        internal static SnapshotRebuildObservation RunOnceForContract(int cardinality)
        {
            using SnapshotRebuildState state = new(cardinality);
            state.RunMany(1);
            return new SnapshotRebuildObservation(
                state.Calls,
                state.LiveRegistrations,
                state.OccupiedTypeSlots,
                state.ReplacementDispatchCalls,
                state.StaleDispatchCalls,
                state.ObserveSnapshotStorage()
            );
        }

        private static IEnumerable<TestCaseData> BenchmarkCases()
        {
            foreach (SnapshotRebuildBenchmarkCase benchmarkCase in SnapshotRebuildScenarios.All)
            {
                yield return new TestCaseData(benchmarkCase).SetName(benchmarkCase.Key);
            }
        }

        private sealed class SnapshotRebuildState : IDisposable
        {
            private readonly MessageBus _bus;
            private readonly IDisposable _registryScope;
            private readonly int[] _activeGenerations;
            private readonly MessageRegistrationToken[] _tokens;
            private int _cursor;
            private long _calls;
            private long _generationOneCalls;
            private long _staleDispatchCalls;

            internal SnapshotRebuildState(int cardinality)
            {
                if (cardinality <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(cardinality));
                }

                _registryScope = MessageBus.IsolateIdleSweepRegistryForBenchmark();
                _tokens = new MessageRegistrationToken[checked(cardinality * 2)];
                _activeGenerations = new int[cardinality];
                try
                {
                    _bus = new MessageBus { DiagnosticsMode = false };
                    for (int slot = 0; slot < cardinality; ++slot)
                    {
                        for (int generation = 0; generation < 2; ++generation)
                        {
                            int capturedSlot = slot;
                            int capturedGeneration = generation;
                            MessageHandler messageHandler = new(
                                new InstanceId(0x5352_0001 + slot * 2 + generation),
                                _bus
                            )
                            {
                                active = true,
                            };
                            MessageRegistrationToken token = MessageRegistrationToken.Create(
                                messageHandler,
                                _bus
                            );
                            _tokens[TokenIndex(slot, generation)] = token;
                            token.DiagnosticMode = false;
                            _ = token.RegisterUntargeted<SnapshotRebuildMessage>(
                                (ref SnapshotRebuildMessage message) =>
                                {
                                    ++_calls;
                                    if (capturedGeneration == 1)
                                    {
                                        ++_generationOneCalls;
                                    }
                                    if (_activeGenerations[capturedSlot] != capturedGeneration)
                                    {
                                        ++_staleDispatchCalls;
                                    }
                                }
                            );
                        }

                        _tokens[TokenIndex(slot, 0)].Enable();
                    }

                    // Build the initial active snapshot before any measured mutation/rebuild.
                    Dispatch();
                    _calls = 0;
                    _generationOneCalls = 0;
                    _staleDispatchCalls = 0;
                }
                catch
                {
                    DisposeTokensBestEffort();
                    try
                    {
                        _bus?.ResetState();
                    }
                    finally
                    {
                        _registryScope.Dispose();
                    }
                    throw;
                }
            }

            internal long Calls => _calls;

            internal long ReplacementDispatchCalls => _generationOneCalls;

            internal long StaleDispatchCalls => _staleDispatchCalls;

            internal int LiveRegistrations
            {
                get
                {
                    int enabled = 0;
                    for (int index = 0; index < _tokens.Length; ++index)
                    {
                        if (_tokens[index].Enabled)
                        {
                            ++enabled;
                        }
                    }
                    return enabled;
                }
            }

            internal int OccupiedTypeSlots => _bus.OccupiedTypeSlots;

            internal void RunMany(int count)
            {
                for (int operation = 0; operation < count; ++operation)
                {
                    int slot = _cursor++ % _activeGenerations.Length;
                    int previousGeneration = _activeGenerations[slot];
                    int replacementGeneration = 1 - previousGeneration;
                    _tokens[TokenIndex(slot, previousGeneration)].Disable();
                    _activeGenerations[slot] = replacementGeneration;
                    _tokens[TokenIndex(slot, replacementGeneration)].Enable();
                    Dispatch();
                }
            }

            internal MessageBus.FlatSnapshotStorageObservation ObserveSnapshotStorage()
            {
                if (
                    !_bus.TryObserveUntargetedFlatSnapshotStorageForBenchmark<SnapshotRebuildMessage>(
                        out MessageBus.FlatSnapshotStorageObservation observation
                    )
                )
                {
                    throw new InvalidOperationException(
                        "The benchmark must leave an active untargeted flat snapshot."
                    );
                }
                return observation;
            }

            private void Dispatch()
            {
                SnapshotRebuildMessage message = default;
                _bus.UntargetedBroadcast(ref message);
            }

            private static int TokenIndex(int slot, int generation)
            {
                return checked(slot * 2 + generation);
            }

            private void DisposeTokensBestEffort()
            {
                for (int index = _tokens.Length - 1; index >= 0; --index)
                {
                    try
                    {
                        _tokens[index]?.Dispose();
                    }
                    catch
                    {
                        // Cleanup is best-effort while preserving an earlier failure.
                    }
                }
            }

            public void Dispose()
            {
                try
                {
                    for (int index = _tokens.Length - 1; index >= 0; --index)
                    {
                        _tokens[index]?.Dispose();
                    }
                }
                finally
                {
                    try
                    {
                        // Token teardown stages the now-empty sink. Reset through the production
                        // bus path so the active snapshot releases its holder and rented array
                        // before the benchmark's isolated idle-sweep registry is restored.
                        _bus.ResetState();
                    }
                    finally
                    {
                        _registryScope.Dispose();
                    }
                }
            }
        }

        private readonly struct SnapshotRebuildMessage : IUntargetedMessage { }
    }

    public static class SnapshotRebuildScenarios
    {
        private static readonly SnapshotRebuildBenchmarkCase[] Cases =
        {
            new(1),
            new(4),
            new(16),
            new(64),
        };

        public static IReadOnlyList<SnapshotRebuildBenchmarkCase> All => Cases;
    }

    public readonly struct SnapshotRebuildBenchmarkCase
    {
        public SnapshotRebuildBenchmarkCase(int cardinality)
        {
            Cardinality = cardinality;
        }

        public int Cardinality { get; }

        public string Key => $"SnapshotRebuild_{Cardinality}";
    }

    internal readonly struct SnapshotRebuildObservation
    {
        internal SnapshotRebuildObservation(
            long dispatchCalls,
            int liveRegistrations,
            int occupiedTypeSlots,
            long replacementDispatchCalls,
            long staleDispatchCalls,
            MessageBus.FlatSnapshotStorageObservation storage
        )
        {
            DispatchCalls = dispatchCalls;
            LiveRegistrations = liveRegistrations;
            OccupiedTypeSlots = occupiedTypeSlots;
            ReplacementDispatchCalls = replacementDispatchCalls;
            StaleDispatchCalls = staleDispatchCalls;
            Storage = storage;
        }

        internal long DispatchCalls { get; }

        internal int LiveRegistrations { get; }

        internal int OccupiedTypeSlots { get; }

        internal long ReplacementDispatchCalls { get; }

        internal long StaleDispatchCalls { get; }

        internal MessageBus.FlatSnapshotStorageObservation Storage { get; }
    }

    public readonly struct SnapshotRebuildBenchmarkResult
    {
        public const string CsvHeader =
            "scenario,cardinality,totalOperations,operationsPerSecond,wallClockMs,gcAllocations,gcAllocatedBytes,dispatchCalls,liveRegistrations,occupiedTypeSlots,staleDispatchCalls,flatEntryCount,flatArrayCapacity,emptyHolderPoolCount";

        public SnapshotRebuildBenchmarkResult(
            SnapshotRebuildBenchmarkCase benchmarkCase,
            long totalOperations,
            double operationsPerSecond,
            double wallClockMilliseconds,
            long gcAllocations,
            long gcAllocatedBytes,
            long dispatchCalls,
            int liveRegistrations,
            int occupiedTypeSlots,
            long staleDispatchCalls,
            int flatEntryCount,
            int flatArrayCapacity,
            int emptyHolderPoolCount
        )
        {
            BenchmarkCase = benchmarkCase;
            TotalOperations = totalOperations;
            OperationsPerSecond = operationsPerSecond;
            WallClockMilliseconds = wallClockMilliseconds;
            GcAllocations = gcAllocations;
            GcAllocatedBytes = gcAllocatedBytes;
            DispatchCalls = dispatchCalls;
            LiveRegistrations = liveRegistrations;
            OccupiedTypeSlots = occupiedTypeSlots;
            StaleDispatchCalls = staleDispatchCalls;
            FlatEntryCount = flatEntryCount;
            FlatArrayCapacity = flatArrayCapacity;
            EmptyHolderPoolCount = emptyHolderPoolCount;
        }

        public SnapshotRebuildBenchmarkCase BenchmarkCase { get; }

        public long TotalOperations { get; }

        public double OperationsPerSecond { get; }

        public double WallClockMilliseconds { get; }

        public long GcAllocations { get; }

        public long GcAllocatedBytes { get; }

        public long DispatchCalls { get; }

        public int LiveRegistrations { get; }

        public int OccupiedTypeSlots { get; }

        public long StaleDispatchCalls { get; }

        public int FlatEntryCount { get; }

        public int FlatArrayCapacity { get; }

        /// <summary>
        /// Released holder objects with empty entry arrays. This is pool topology only; it is not
        /// evidence of retained ArrayPool bytes, which require external memory measurement.
        /// </summary>
        public int EmptyHolderPoolCount { get; }

        public string ToCsvRow() =>
            string.Join(
                ",",
                BenchmarkCase.Key,
                BenchmarkCase.Cardinality.ToString(CultureInfo.InvariantCulture),
                TotalOperations.ToString(CultureInfo.InvariantCulture),
                OperationsPerSecond.ToString("F3", CultureInfo.InvariantCulture),
                WallClockMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
                GcAllocations.ToString(CultureInfo.InvariantCulture),
                GcAllocatedBytes.ToString(CultureInfo.InvariantCulture),
                DispatchCalls.ToString(CultureInfo.InvariantCulture),
                LiveRegistrations.ToString(CultureInfo.InvariantCulture),
                OccupiedTypeSlots.ToString(CultureInfo.InvariantCulture),
                StaleDispatchCalls.ToString(CultureInfo.InvariantCulture),
                FlatEntryCount.ToString(CultureInfo.InvariantCulture),
                FlatArrayCapacity.ToString(CultureInfo.InvariantCulture),
                EmptyHolderPoolCount.ToString(CultureInfo.InvariantCulture)
            );

        public string ToStructuredLog() =>
            "[SnapshotRebuild "
            + $"scenario={BenchmarkCase.Key} cardinality={BenchmarkCase.Cardinality} "
            + $"totalOperations={TotalOperations} "
            + $"operationsPerSecond={OperationsPerSecond.ToString("F3", CultureInfo.InvariantCulture)} "
            + $"wallClockMs={WallClockMilliseconds.ToString("F3", CultureInfo.InvariantCulture)} "
            + $"gcAllocations={GcAllocations} gcAllocatedBytes={GcAllocatedBytes} "
            + $"dispatchCalls={DispatchCalls} liveRegistrations={LiveRegistrations} "
            + $"occupiedTypeSlots={OccupiedTypeSlots} staleDispatchCalls={StaleDispatchCalls} "
            + $"flatEntryCount={FlatEntryCount} flatArrayCapacity={FlatArrayCapacity} "
            + $"emptyHolderPoolCount={EmptyHolderPoolCount}]";
    }

    public sealed class SnapshotRebuildBenchmarkContractTests
    {
        [Test]
        public void MatrixCoversRepresentativeCardinalitiesExactlyOnce()
        {
            CollectionAssert.AreEqual(
                new[] { 1, 4, 16, 64 },
                BuildCardinalities(SnapshotRebuildScenarios.All)
            );
        }

        [TestCase(1)]
        [TestCase(4)]
        [TestCase(16)]
        [TestCase(64)]
        public void OneMutationRebuildPreservesFanOutAndLiveTopology(int cardinality)
        {
            SnapshotRebuildObservation observation = SnapshotRebuildBenchmarks.RunOnceForContract(
                cardinality
            );

            Assert.AreEqual(cardinality, observation.DispatchCalls);
            Assert.AreEqual(cardinality, observation.LiveRegistrations);
            Assert.AreEqual(
                2,
                observation.OccupiedTypeSlots,
                "One replacement must leave one active bus slot and one dirty-empty displaced "
                    + "typed-handler slot."
            );
            Assert.AreEqual(
                1,
                observation.ReplacementDispatchCalls,
                "The first replacement generation must be the one invoked after mutation."
            );
            Assert.Zero(
                observation.StaleDispatchCalls,
                "The displaced generation must not be invoked by a stale snapshot."
            );
            Assert.AreEqual(cardinality, observation.Storage.EntryCount);
            Assert.GreaterOrEqual(
                observation.Storage.ArrayCapacity,
                observation.Storage.EntryCount
            );
            Assert.AreEqual(
                1,
                observation.Storage.EmptyHolderPoolCount,
                "One sequential rebuild must leave exactly one released empty holder."
            );
        }

        [Test]
        public void ResultSchemaKeepsCsvAndStructuredLogAligned()
        {
            SnapshotRebuildBenchmarkResult result = new(
                new SnapshotRebuildBenchmarkCase(4),
                1,
                2d,
                3d,
                -1,
                -1,
                4,
                4,
                1,
                0,
                4,
                16,
                1
            );

            string[] header = SnapshotRebuildBenchmarkResult.CsvHeader.Split(',');
            string[] row = result.ToCsvRow().Split(',');
            Assert.AreEqual(header.Length, row.Length);
            Assert.AreEqual("-1", row[5]);
            Assert.AreEqual("-1", row[6]);

            string structured = result.ToStructuredLog();
            foreach (string key in header)
            {
                StringAssert.Contains(key + "=", structured, $"Missing structured key {key}.");
            }
        }

        private static int[] BuildCardinalities(IReadOnlyList<SnapshotRebuildBenchmarkCase> cases)
        {
            int[] cardinalities = new int[cases.Count];
            for (int index = 0; index < cases.Count; ++index)
            {
                cardinalities[index] = cases[index].Cardinality;
            }
            return cardinalities;
        }
    }
}
#endif
