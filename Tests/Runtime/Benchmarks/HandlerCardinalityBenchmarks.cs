#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Benchmarks
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using DxMessaging.Core;
    using DxMessaging.Core.Internal;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;
    using UnityEngine;

    public enum HandlerCardinalityOperation
    {
        PriorityDispatch,
        PriorityChurn,
        HandlerDispatch,
        HandlerChurn,
        SameHandlerDispatch,
        SameHandlerChurn,
    }

    /// <summary>
    /// Characterizes the three small ordered-map shapes used by typed handlers: distinct
    /// priority keys, distinct handlers at one priority, and duplicate registrations of
    /// the same handler/refcount entry.
    /// Every row preserves a fixed live cardinality while dispatch or remove/re-register
    /// churn runs through the shared five-second benchmark protocol.
    /// </summary>
    public sealed class HandlerCardinalityBenchmarks
    {
        private const int ChurnWarmupOperations = 1_000;

        [Test, Performance, Category("PerfBench")]
        [TestCaseSource(nameof(BenchmarkCases))]
        public void HandlerCardinalityBenchmark(HandlerCardinalityBenchmarkCase benchmarkCase)
        {
            HandlerCardinalityBenchmarkResult result = RunScenario(benchmarkCase);
            Debug.Log(result.ToStructuredLog());
            TestContext.Out.WriteLine(result.ToCsvRow());
        }

        public static HandlerCardinalityBenchmarkResult RunScenario(
            HandlerCardinalityBenchmarkCase benchmarkCase
        )
        {
            using HandlerCardinalityState state = new(
                benchmarkCase.Cardinality,
                benchmarkCase.Operation
            );
            int warmupOperations = IsDispatch(benchmarkCase.Operation)
                ? BenchmarkProtocol.WarmupEmits
                : ChurnWarmupOperations;
            BenchmarkMeasurement measurement = BenchmarkProtocol.Measure(
                () => state.RunMany(benchmarkCase.Operation, warmupOperations),
                () =>
                {
                    state.RunMany(benchmarkCase.Operation, BenchmarkProtocol.BatchSize);
                    return BenchmarkProtocol.BatchSize;
                }
            );

            int expectedFanOut = IsSameHandler(benchmarkCase.Operation)
                ? 1
                : benchmarkCase.Cardinality;
            if (IsDispatch(benchmarkCase.Operation))
            {
                long expectedCalls =
                    (long)expectedFanOut * (warmupOperations + measurement.TotalEmittedOperations);
                Assert.AreEqual(
                    expectedCalls,
                    state.Calls,
                    $"{benchmarkCase.Key} changed dispatch fan-out during measurement."
                );
            }
            else
            {
                Assert.AreEqual(
                    0,
                    state.Calls,
                    $"{benchmarkCase.Key} churn must not invoke handlers."
                );
            }

            HandlerCardinalityObservation observation = state.Observe();
            Assert.AreEqual(expectedFanOut, observation.DispatchCalls);
            Assert.AreEqual(benchmarkCase.Cardinality, observation.LiveRegistrations);
            Assert.AreEqual(1, observation.OccupiedTypeSlots);
            MessageHandler.HandlerCacheStorageObservation storage = observation.Storage;
            MessageBus.PriorityStorageObservation busStorage = observation.BusPriorityStorage;

            return new HandlerCardinalityBenchmarkResult(
                benchmarkCase,
                measurement.OperationsPerSecond,
                measurement.GcAllocations,
                measurement.GcAllocatedBytes,
                measurement.ElapsedSeconds * 1000d,
                expectedFanOut,
                IsSameHandler(benchmarkCase.Operation) ? 1 : benchmarkCase.Cardinality,
                observation.LiveRegistrations,
                observation.OccupiedTypeSlots,
                storage.PriorityEntries,
                storage.PriorityInlineCapacity,
                storage.PriorityMapCapacity,
                storage.PriorityOrderCapacity,
                storage.PriorityUsesSpillStorage,
                storage.HandlerEntries,
                storage.HandlerInlineCapacity,
                storage.HandlerMapCapacity,
                storage.HandlerOrderCapacity,
                storage.HandlerUsesSpillStorage,
                busStorage.Entries,
                busStorage.MapCapacity,
                busStorage.OrderCapacity
            );
        }

        internal static HandlerCardinalityObservation RunOnceForContract(
            HandlerCardinalityBenchmarkCase benchmarkCase
        )
        {
            using HandlerCardinalityState state = new(
                benchmarkCase.Cardinality,
                benchmarkCase.Operation
            );
            if (IsDispatch(benchmarkCase.Operation))
            {
                state.RunMany(benchmarkCase.Operation, 1);
            }
            else
            {
                state.RunMany(benchmarkCase.Operation, benchmarkCase.Cardinality);
            }

            return state.Observe();
        }

        private static bool IsDispatch(HandlerCardinalityOperation operation) =>
            operation == HandlerCardinalityOperation.PriorityDispatch
            || operation == HandlerCardinalityOperation.HandlerDispatch
            || operation == HandlerCardinalityOperation.SameHandlerDispatch;

        private static bool IsSameHandler(HandlerCardinalityOperation operation) =>
            operation == HandlerCardinalityOperation.SameHandlerDispatch
            || operation == HandlerCardinalityOperation.SameHandlerChurn;

        private static IEnumerable<TestCaseData> BenchmarkCases()
        {
            foreach (
                HandlerCardinalityBenchmarkCase benchmarkCase in HandlerCardinalityScenarios.All
            )
            {
                yield return new TestCaseData(benchmarkCase).SetName(benchmarkCase.Key);
            }
        }

        private sealed class HandlerCardinalityState : IDisposable
        {
            private readonly MessageBus _bus;
            private readonly IDisposable _registryScope;
            private readonly MessageHandler _messageHandler;
            private readonly MessageRegistrationToken _token;
            private readonly MessageHandler.FastHandler<CardinalityMessage>[] _handlers;
            private readonly MessageRegistrationHandle[] _handles;
            private readonly MessageHandler.FastHandler<CardinalityMessage> _sameHandler;
            private readonly bool _sameHandlerShape;
            private readonly bool _distinctPriorityShape;
            private int _cursor;
            private long _calls;

            internal HandlerCardinalityState(int cardinality, HandlerCardinalityOperation operation)
            {
                if (cardinality <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(cardinality));
                }

                _sameHandlerShape = IsSameHandler(operation);
                _distinctPriorityShape =
                    operation == HandlerCardinalityOperation.PriorityDispatch
                    || operation == HandlerCardinalityOperation.PriorityChurn;
                _registryScope = MessageBus.IsolateIdleSweepRegistryForBenchmark();
                MessageRegistrationToken token = null;
                try
                {
                    _bus = new MessageBus { DiagnosticsMode = false };
                    _messageHandler = new MessageHandler(new InstanceId(0x4843_0001), _bus)
                    {
                        active = true,
                    };
                    token = MessageRegistrationToken.Create(_messageHandler, _bus);
                    _token = token;
                    _token.DiagnosticMode = false;
                    _token.Enable();
                    _handlers = new MessageHandler.FastHandler<CardinalityMessage>[cardinality];
                    _handles = new MessageRegistrationHandle[cardinality];
                    _sameHandler = HandleSame;

                    for (int index = 0; index < cardinality; ++index)
                    {
                        int capturedIndex = index;
                        _handlers[index] = (ref CardinalityMessage message) =>
                        {
                            _ = capturedIndex;
                            ++_calls;
                        };
                    }

                    for (int index = 0; index < cardinality; ++index)
                    {
                        _handles[index] = Register(index);
                    }
                }
                catch
                {
                    try
                    {
                        token?.Dispose();
                    }
                    catch
                    {
                        // Preserve the constructor failure that initiated cleanup.
                    }
                    _registryScope.Dispose();
                    throw;
                }
            }

            internal long Calls => _calls;

            internal void RunMany(HandlerCardinalityOperation operation, int count)
            {
                switch (operation)
                {
                    case HandlerCardinalityOperation.PriorityDispatch:
                    case HandlerCardinalityOperation.HandlerDispatch:
                    case HandlerCardinalityOperation.SameHandlerDispatch:
                    {
                        for (int index = 0; index < count; ++index)
                        {
                            CardinalityMessage message = default;
                            _bus.UntargetedBroadcast(ref message);
                        }
                        return;
                    }
                    case HandlerCardinalityOperation.PriorityChurn:
                    case HandlerCardinalityOperation.HandlerChurn:
                    case HandlerCardinalityOperation.SameHandlerChurn:
                    {
                        for (int index = 0; index < count; ++index)
                        {
                            int slot = _cursor++ % _handles.Length;
                            _token.RemoveRegistration(_handles[slot]);
                            _handles[slot] = Register(slot);
                        }
                        return;
                    }
                    default:
                        throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
                }
            }

            internal HandlerCardinalityObservation Observe()
            {
                _calls = 0;
                CardinalityMessage message = default;
                _bus.UntargetedBroadcast(ref message);
                return new HandlerCardinalityObservation(
                    checked((int)_calls),
                    _token._metadata.Count,
                    _bus.OccupiedTypeSlots,
                    ObserveBusPriorityStorage(),
                    ObserveStorage()
                );
            }

            internal MessageBus.PriorityStorageObservation ObserveBusPriorityStorage()
            {
                if (
                    !_bus.TryObserveUntargetedPriorityStorageForBenchmark<CardinalityMessage>(
                        out MessageBus.PriorityStorageObservation storage
                    )
                )
                {
                    throw new InvalidOperationException(
                        "The benchmark must materialize bus-side untargeted priority storage."
                    );
                }
                return storage;
            }

            internal MessageHandler.HandlerCacheStorageObservation ObserveStorage()
            {
                if (
                    !_messageHandler.TryObserveFastPriorityHandlerStorage<CardinalityMessage>(
                        _bus,
                        TypedSlotIndex.UntargetedHandleFast,
                        0,
                        out MessageHandler.HandlerCacheStorageObservation storage
                    )
                )
                {
                    throw new InvalidOperationException(
                        "The benchmark must materialize priority zero storage."
                    );
                }
                return storage;
            }

            private MessageRegistrationHandle Register(int index)
            {
                return _token.RegisterUntargeted<CardinalityMessage>(
                    _sameHandlerShape ? _sameHandler : _handlers[index],
                    priority: _distinctPriorityShape ? index : 0
                );
            }

            private void HandleSame(ref CardinalityMessage message)
            {
                ++_calls;
            }

            public void Dispose()
            {
                try
                {
                    _token.Dispose();
                }
                finally
                {
                    _registryScope.Dispose();
                }
            }
        }

        private readonly struct CardinalityMessage : IUntargetedMessage { }
    }

    public enum HandlerStorageConstructionKind
    {
        HandlerCache,
        PrioritySlot,
        BusPriorityOwner,
    }

    /// <summary>
    /// Isolates the eager object count, bytes, and fixed-batch construction latency of
    /// the handler-entry cache, typed priority slot, and bus priority owner. These rows run once
    /// per benchmark execution,
    /// independently of the cardinality matrix.
    /// </summary>
    public sealed class HandlerStorageConstructionBenchmarks
    {
        private const int AllocationAttempts = 8;
        private const int ConstructionSamples = 1_000;
        private const int TimingTrials = 7;
        private static object s_constructionSink;

        [Test, Performance, Category("PerfBench")]
        [TestCase(HandlerStorageConstructionKind.HandlerCache)]
        [TestCase(HandlerStorageConstructionKind.PrioritySlot)]
        [TestCase(HandlerStorageConstructionKind.BusPriorityOwner)]
        public void HandlerStorageConstructionBenchmark(HandlerStorageConstructionKind kind)
        {
            HandlerStorageConstructionBenchmarkResult result = RunScenario(kind);
            Debug.Log(result.ToStructuredLog());
            TestContext.Out.WriteLine(result.ToCsvRow());
        }

        public static HandlerStorageConstructionBenchmarkResult RunScenario(
            HandlerStorageConstructionKind kind
        )
        {
            ValidateFreshConstruction(kind);

            double minElapsedSeconds = double.MaxValue;
            for (int trial = 0; trial < TimingTrials; ++trial)
            {
                AllocationProbe.SettleHeapForMeasurement();
                long startTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                int constructed = ConstructBatch(kind, ConstructionSamples);
                long endTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                s_constructionSink = null;
                Assert.AreEqual(ConstructionSamples, constructed);
                double elapsedSeconds =
                    (endTimestamp - startTimestamp)
                    / (double)System.Diagnostics.Stopwatch.Frequency;
                minElapsedSeconds = Math.Min(minElapsedSeconds, elapsedSeconds);
            }

            AllocationProbe.MinimumMeasurement<int> allocation;
            try
            {
                allocation = AllocationProbe.MeasureMinWithDiagnostics(
                    AllocationAttempts,
                    null,
                    () => ConstructBatch(kind, ConstructionSamples)
                );
            }
            finally
            {
                s_constructionSink = null;
            }

            if (allocation.GcAllocations != AllocationProbe.Unmeasured)
            {
                Assert.AreEqual(
                    ConstructionSamples,
                    allocation.Diagnostics,
                    "The selected allocation attempt must construct the reported sample count."
                );
            }

            return new HandlerStorageConstructionBenchmarkResult(
                kind,
                ConstructionSamples / Math.Max(minElapsedSeconds, double.Epsilon),
                allocation.GcAllocations,
                allocation.GcAllocatedBytes,
                minElapsedSeconds * 1000d,
                ConstructionSamples
            );
        }

        private static int ConstructBatch(HandlerStorageConstructionKind kind, int count)
        {
            switch (kind)
            {
                case HandlerStorageConstructionKind.HandlerCache:
                    for (int index = 0; index < count; ++index)
                    {
                        s_constructionSink =
                            new MessageHandler.HandlerActionCache<MessageHandler.FastHandler<StorageConstructionMessage>>();
                    }
                    return count;
                case HandlerStorageConstructionKind.PrioritySlot:
                    for (int index = 0; index < count; ++index)
                    {
                        s_constructionSink = new TypedSlot<StorageConstructionMessage>(
                            requiresContext: false
                        );
                    }
                    return count;
                case HandlerStorageConstructionKind.BusPriorityOwner:
                    for (int index = 0; index < count; ++index)
                    {
                        s_constructionSink = MessageBus.CreatePriorityStorageOwnerForBenchmark();
                    }
                    return count;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
            }
        }

        private static void ValidateFreshConstruction(HandlerStorageConstructionKind kind)
        {
            Assert.AreEqual(1, ConstructBatch(kind, 1));
            if (kind == HandlerStorageConstructionKind.BusPriorityOwner)
            {
                Assert.IsTrue(
                    MessageBus.TryObservePriorityStorageOwnerForBenchmark(
                        s_constructionSink,
                        out MessageBus.PriorityStorageObservation observation
                    )
                );
                Assert.Zero(observation.Entries);
                Assert.Zero(observation.MapCapacity);
                Assert.Zero(observation.OrderCapacity);
            }
            s_constructionSink = null;
        }

        private readonly struct StorageConstructionMessage : IUntargetedMessage { }
    }

    public readonly struct HandlerStorageConstructionBenchmarkResult
    {
        public const string CsvHeader =
            "scenario,constructionsPerSecond,gcAllocations,wallClockMs,gcAllocatedBytes,samples";

        public HandlerStorageConstructionBenchmarkResult(
            HandlerStorageConstructionKind kind,
            double constructionsPerSecond,
            long gcAllocations,
            long gcAllocatedBytes,
            double wallClockMilliseconds,
            int samples
        )
        {
            Kind = kind;
            ConstructionsPerSecond = constructionsPerSecond;
            GcAllocations = gcAllocations;
            GcAllocatedBytes = gcAllocatedBytes;
            WallClockMilliseconds = wallClockMilliseconds;
            Samples = samples;
        }

        public HandlerStorageConstructionKind Kind { get; }

        public double ConstructionsPerSecond { get; }

        public long GcAllocations { get; }

        public long GcAllocatedBytes { get; }

        public double WallClockMilliseconds { get; }

        public int Samples { get; }

        public string ToCsvRow() =>
            string.Join(
                ",",
                $"HandlerStorageConstruction_{Kind}",
                ConstructionsPerSecond.ToString("F3", CultureInfo.InvariantCulture),
                GcAllocations.ToString(CultureInfo.InvariantCulture),
                WallClockMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
                GcAllocatedBytes.ToString(CultureInfo.InvariantCulture),
                Samples.ToString(CultureInfo.InvariantCulture)
            );

        public string ToStructuredLog() =>
            "[HandlerStorageConstruction "
            + $"scenario=HandlerStorageConstruction_{Kind} "
            + $"constructionsPerSecond={ConstructionsPerSecond.ToString("F3", CultureInfo.InvariantCulture)} "
            + $"gcAllocations={GcAllocations.ToString(CultureInfo.InvariantCulture)} "
            + $"gcAllocatedBytes={GcAllocatedBytes.ToString(CultureInfo.InvariantCulture)} "
            + $"wallClockMs={WallClockMilliseconds.ToString("F3", CultureInfo.InvariantCulture)} "
            + $"samples={Samples}]";
    }

    public static class HandlerCardinalityScenarios
    {
        private static readonly int[] Cardinalities = { 1, 2, 3, 4, 5, 8, 9, 16, 64 };
        private static readonly HandlerCardinalityOperation[] Operations =
        {
            HandlerCardinalityOperation.PriorityDispatch,
            HandlerCardinalityOperation.PriorityChurn,
            HandlerCardinalityOperation.HandlerDispatch,
            HandlerCardinalityOperation.HandlerChurn,
            HandlerCardinalityOperation.SameHandlerDispatch,
            HandlerCardinalityOperation.SameHandlerChurn,
        };
        private static readonly HandlerCardinalityBenchmarkCase[] Cases = BuildCases();

        public static IReadOnlyList<HandlerCardinalityBenchmarkCase> All => Cases;

        private static HandlerCardinalityBenchmarkCase[] BuildCases()
        {
            HandlerCardinalityBenchmarkCase[] cases = new HandlerCardinalityBenchmarkCase[
                Cardinalities.Length * Operations.Length
            ];
            int writeIndex = 0;
            for (int operationIndex = 0; operationIndex < Operations.Length; ++operationIndex)
            {
                for (
                    int cardinalityIndex = 0;
                    cardinalityIndex < Cardinalities.Length;
                    ++cardinalityIndex
                )
                {
                    cases[writeIndex++] = new HandlerCardinalityBenchmarkCase(
                        Operations[operationIndex],
                        Cardinalities[cardinalityIndex]
                    );
                }
            }
            return cases;
        }
    }

    public readonly struct HandlerCardinalityBenchmarkCase
    {
        public HandlerCardinalityBenchmarkCase(
            HandlerCardinalityOperation operation,
            int cardinality
        )
        {
            Operation = operation;
            Cardinality = cardinality;
        }

        public HandlerCardinalityOperation Operation { get; }

        public int Cardinality { get; }

        public string Key => $"HandlerCardinality_{Operation}_{Cardinality}";
    }

    internal readonly struct HandlerCardinalityObservation
    {
        public HandlerCardinalityObservation(
            int dispatchCalls,
            int liveRegistrations,
            int occupiedTypeSlots,
            MessageBus.PriorityStorageObservation busPriorityStorage,
            MessageHandler.HandlerCacheStorageObservation storage
        )
        {
            DispatchCalls = dispatchCalls;
            LiveRegistrations = liveRegistrations;
            OccupiedTypeSlots = occupiedTypeSlots;
            BusPriorityStorage = busPriorityStorage;
            Storage = storage;
        }

        public int DispatchCalls { get; }

        public int LiveRegistrations { get; }

        public int OccupiedTypeSlots { get; }

        public MessageBus.PriorityStorageObservation BusPriorityStorage { get; }

        public MessageHandler.HandlerCacheStorageObservation Storage { get; }
    }

    public readonly struct HandlerCardinalityBenchmarkResult
    {
        public const string CsvHeader =
            "scenario,operationsPerSecond,gcAllocations,wallClockMs,gcAllocatedBytes,dispatchFanOut,distinctMapEntries,liveRegistrations,occupiedTypeSlots,priorityEntries,priorityInlineCapacity,priorityMapCapacity,priorityOrderCapacity,priorityUsesSpillStorage,handlerEntries,handlerInlineCapacity,handlerMapCapacity,handlerOrderCapacity,handlerUsesSpillStorage,busPriorityEntries,busPriorityMapCapacity,busPriorityOrderCapacity";

        public HandlerCardinalityBenchmarkResult(
            HandlerCardinalityBenchmarkCase benchmarkCase,
            double operationsPerSecond,
            long gcAllocations,
            long gcAllocatedBytes,
            double wallClockMilliseconds,
            int dispatchFanOut,
            int distinctMapEntries,
            int liveRegistrations,
            int occupiedTypeSlots,
            int priorityEntries,
            int priorityInlineCapacity,
            int priorityMapCapacity,
            int priorityOrderCapacity,
            bool priorityUsesSpillStorage,
            int handlerEntries,
            int handlerInlineCapacity,
            int handlerMapCapacity,
            int handlerOrderCapacity,
            bool handlerUsesSpillStorage
        )
            : this(
                benchmarkCase,
                operationsPerSecond,
                gcAllocations,
                gcAllocatedBytes,
                wallClockMilliseconds,
                dispatchFanOut,
                distinctMapEntries,
                liveRegistrations,
                occupiedTypeSlots,
                priorityEntries,
                priorityInlineCapacity,
                priorityMapCapacity,
                priorityOrderCapacity,
                priorityUsesSpillStorage,
                handlerEntries,
                handlerInlineCapacity,
                handlerMapCapacity,
                handlerOrderCapacity,
                handlerUsesSpillStorage,
                -1,
                -1,
                -1
            ) { }

        public HandlerCardinalityBenchmarkResult(
            HandlerCardinalityBenchmarkCase benchmarkCase,
            double operationsPerSecond,
            long gcAllocations,
            long gcAllocatedBytes,
            double wallClockMilliseconds,
            int dispatchFanOut,
            int distinctMapEntries,
            int liveRegistrations,
            int occupiedTypeSlots,
            int priorityEntries,
            int priorityInlineCapacity,
            int priorityMapCapacity,
            int priorityOrderCapacity,
            bool priorityUsesSpillStorage,
            int handlerEntries,
            int handlerInlineCapacity,
            int handlerMapCapacity,
            int handlerOrderCapacity,
            bool handlerUsesSpillStorage,
            int busPriorityEntries,
            int busPriorityMapCapacity,
            int busPriorityOrderCapacity
        )
        {
            BenchmarkCase = benchmarkCase;
            OperationsPerSecond = operationsPerSecond;
            GcAllocations = gcAllocations;
            GcAllocatedBytes = gcAllocatedBytes;
            WallClockMilliseconds = wallClockMilliseconds;
            DispatchFanOut = dispatchFanOut;
            DistinctMapEntries = distinctMapEntries;
            LiveRegistrations = liveRegistrations;
            OccupiedTypeSlots = occupiedTypeSlots;
            BusPriorityEntries = busPriorityEntries;
            BusPriorityMapCapacity = busPriorityMapCapacity;
            BusPriorityOrderCapacity = busPriorityOrderCapacity;
            PriorityEntries = priorityEntries;
            PriorityInlineCapacity = priorityInlineCapacity;
            PriorityMapCapacity = priorityMapCapacity;
            PriorityOrderCapacity = priorityOrderCapacity;
            PriorityUsesSpillStorage = priorityUsesSpillStorage;
            HandlerEntries = handlerEntries;
            HandlerInlineCapacity = handlerInlineCapacity;
            HandlerMapCapacity = handlerMapCapacity;
            HandlerOrderCapacity = handlerOrderCapacity;
            HandlerUsesSpillStorage = handlerUsesSpillStorage;
        }

        public HandlerCardinalityBenchmarkCase BenchmarkCase { get; }

        public double OperationsPerSecond { get; }

        public long GcAllocations { get; }

        public long GcAllocatedBytes { get; }

        public double WallClockMilliseconds { get; }

        public int DispatchFanOut { get; }

        public int DistinctMapEntries { get; }

        public int LiveRegistrations { get; }

        public int OccupiedTypeSlots { get; }

        public int BusPriorityEntries { get; }

        public int BusPriorityMapCapacity { get; }

        public int BusPriorityOrderCapacity { get; }

        public int PriorityEntries { get; }

        public int PriorityInlineCapacity { get; }

        public int PriorityMapCapacity { get; }

        public int PriorityOrderCapacity { get; }

        public bool PriorityUsesSpillStorage { get; }

        public int HandlerEntries { get; }

        public int HandlerInlineCapacity { get; }

        public int HandlerMapCapacity { get; }

        public int HandlerOrderCapacity { get; }

        public bool HandlerUsesSpillStorage { get; }

        public string ToCsvRow() =>
            string.Join(
                ",",
                BenchmarkCase.Key,
                OperationsPerSecond.ToString("F3", CultureInfo.InvariantCulture),
                GcAllocations.ToString(CultureInfo.InvariantCulture),
                WallClockMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
                GcAllocatedBytes.ToString(CultureInfo.InvariantCulture),
                DispatchFanOut.ToString(CultureInfo.InvariantCulture),
                DistinctMapEntries.ToString(CultureInfo.InvariantCulture),
                LiveRegistrations.ToString(CultureInfo.InvariantCulture),
                OccupiedTypeSlots.ToString(CultureInfo.InvariantCulture),
                PriorityEntries.ToString(CultureInfo.InvariantCulture),
                PriorityInlineCapacity.ToString(CultureInfo.InvariantCulture),
                PriorityMapCapacity.ToString(CultureInfo.InvariantCulture),
                PriorityOrderCapacity.ToString(CultureInfo.InvariantCulture),
                PriorityUsesSpillStorage ? "true" : "false",
                HandlerEntries.ToString(CultureInfo.InvariantCulture),
                HandlerInlineCapacity.ToString(CultureInfo.InvariantCulture),
                HandlerMapCapacity.ToString(CultureInfo.InvariantCulture),
                HandlerOrderCapacity.ToString(CultureInfo.InvariantCulture),
                HandlerUsesSpillStorage ? "true" : "false",
                BusPriorityEntries.ToString(CultureInfo.InvariantCulture),
                BusPriorityMapCapacity.ToString(CultureInfo.InvariantCulture),
                BusPriorityOrderCapacity.ToString(CultureInfo.InvariantCulture)
            );

        public string ToStructuredLog() =>
            "[HandlerCardinality "
            + $"scenario={BenchmarkCase.Key} "
            + $"operationsPerSecond={OperationsPerSecond.ToString("F3", CultureInfo.InvariantCulture)} "
            + $"gcAllocations={GcAllocations.ToString(CultureInfo.InvariantCulture)} "
            + $"gcAllocatedBytes={GcAllocatedBytes.ToString(CultureInfo.InvariantCulture)} "
            + $"wallClockMs={WallClockMilliseconds.ToString("F3", CultureInfo.InvariantCulture)} "
            + $"dispatchFanOut={DispatchFanOut} distinctMapEntries={DistinctMapEntries} "
            + $"liveRegistrations={LiveRegistrations} occupiedTypeSlots={OccupiedTypeSlots} "
            + $"priorityEntries={PriorityEntries} priorityInlineCapacity={PriorityInlineCapacity} "
            + $"priorityMapCapacity={PriorityMapCapacity} "
            + $"priorityOrderCapacity={PriorityOrderCapacity} "
            + $"priorityUsesSpillStorage={PriorityUsesSpillStorage} "
            + $"handlerEntries={HandlerEntries} "
            + $"handlerInlineCapacity={HandlerInlineCapacity} "
            + $"handlerMapCapacity={HandlerMapCapacity} "
            + $"handlerOrderCapacity={HandlerOrderCapacity} "
            + $"handlerUsesSpillStorage={HandlerUsesSpillStorage} "
            + $"busPriorityEntries={BusPriorityEntries} "
            + $"busPriorityMapCapacity={BusPriorityMapCapacity} "
            + $"busPriorityOrderCapacity={BusPriorityOrderCapacity}]";
    }
}
#endif
