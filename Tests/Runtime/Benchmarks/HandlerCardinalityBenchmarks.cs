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
    /// Characterizes the two small ordered-map shapes used by typed handlers: distinct
    /// priority keys and duplicate registrations of the same handler/refcount entry.
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

            return new HandlerCardinalityBenchmarkResult(
                benchmarkCase,
                measurement.OperationsPerSecond,
                measurement.GcAllocations,
                measurement.GcAllocatedBytes,
                measurement.ElapsedSeconds * 1000d,
                expectedFanOut,
                IsSameHandler(benchmarkCase.Operation) ? 1 : benchmarkCase.Cardinality,
                observation.LiveRegistrations,
                observation.OccupiedTypeSlots
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
                _bus = new MessageBus { DiagnosticsMode = false };
                MessageHandler messageHandler = new(new InstanceId(0x4843_0001), _bus)
                {
                    active = true,
                };
                _token = MessageRegistrationToken.Create(messageHandler, _bus);
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
                    _handles[index] = Register(index);
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
                    _bus.OccupiedTypeSlots
                );
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
                _token.Dispose();
            }
        }

        private readonly struct CardinalityMessage : IUntargetedMessage { }
    }

    public static class HandlerCardinalityScenarios
    {
        private static readonly int[] Cardinalities = { 1, 4, 16, 64 };
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
            int occupiedTypeSlots
        )
        {
            DispatchCalls = dispatchCalls;
            LiveRegistrations = liveRegistrations;
            OccupiedTypeSlots = occupiedTypeSlots;
        }

        public int DispatchCalls { get; }

        public int LiveRegistrations { get; }

        public int OccupiedTypeSlots { get; }
    }

    public readonly struct HandlerCardinalityBenchmarkResult
    {
        public const string CsvHeader =
            "scenario,operationsPerSecond,gcAllocations,wallClockMs,gcAllocatedBytes,dispatchFanOut,distinctMapEntries,liveRegistrations,occupiedTypeSlots";

        public HandlerCardinalityBenchmarkResult(
            HandlerCardinalityBenchmarkCase benchmarkCase,
            double operationsPerSecond,
            long gcAllocations,
            long gcAllocatedBytes,
            double wallClockMilliseconds,
            int dispatchFanOut,
            int distinctMapEntries,
            int liveRegistrations,
            int occupiedTypeSlots
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
                OccupiedTypeSlots.ToString(CultureInfo.InvariantCulture)
            );

        public string ToStructuredLog() =>
            "[HandlerCardinality "
            + $"scenario={BenchmarkCase.Key} "
            + $"operationsPerSecond={OperationsPerSecond.ToString("F3", CultureInfo.InvariantCulture)} "
            + $"gcAllocations={GcAllocations.ToString(CultureInfo.InvariantCulture)} "
            + $"gcAllocatedBytes={GcAllocatedBytes.ToString(CultureInfo.InvariantCulture)} "
            + $"wallClockMs={WallClockMilliseconds.ToString("F3", CultureInfo.InvariantCulture)} "
            + $"dispatchFanOut={DispatchFanOut} distinctMapEntries={DistinctMapEntries} "
            + $"liveRegistrations={LiveRegistrations} occupiedTypeSlots={OccupiedTypeSlots}]";
    }
}
#endif
