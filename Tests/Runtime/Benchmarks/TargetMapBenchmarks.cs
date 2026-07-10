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

            return new TargetMapBenchmarkResult(
                benchmarkCase,
                measurement.TotalOperations,
                measurement.OperationsPerSecond,
                measurement.ElapsedSeconds * 1000d,
                measurement.GcAllocations,
                measurement.GcAllocatedBytes,
                state.Invocations
            );
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

            return new TargetMapContractObservation(
                operationInvocations,
                originalTargetInvocations,
                currentTargetInvocations,
                state.RegisteredTargets,
                state.PhysicalTargetSlots
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
            private readonly MessageBus.IdleSweepRegistryBenchmarkScope _registryScope;
            private readonly MessageRegistrationToken _token;
            private readonly MessageHandler.FastHandler<TargetMapMessage> _handler;
            private readonly InstanceId[] _targets;
            private readonly MessageRegistrationHandle[] _handles;
            private int _cursor;

            internal TargetMapState(int keyCount)
            {
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
                    throw;
                }
            }

            internal MessageBus MessageBus { get; }

            internal long Invocations { get; private set; }

            internal InstanceId FirstTarget => _targets[0];

            internal int RegisteredTargets => MessageBus.RegisteredTargeted;

            internal int PhysicalTargetSlots => MessageBus.OccupiedTargetSlots;

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
                        _registryScope.Dispose();
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
            "scenario,keyCount,operation,totalOperations,operationsPerSecond,wallClockMs,gcAllocations,gcAllocatedBytes,observedInvocations";

        internal TargetMapBenchmarkResult(
            TargetMapBenchmarkCase benchmarkCase,
            long totalOperations,
            double operationsPerSecond,
            double wallClockMs,
            long gcAllocations,
            long gcAllocatedBytes,
            long observedInvocations
        )
        {
            BenchmarkCase = benchmarkCase;
            TotalOperations = totalOperations;
            OperationsPerSecond = operationsPerSecond;
            WallClockMs = wallClockMs;
            GcAllocations = gcAllocations;
            GcAllocatedBytes = gcAllocatedBytes;
            ObservedInvocations = observedInvocations;
        }

        public TargetMapBenchmarkCase BenchmarkCase { get; }

        public long TotalOperations { get; }

        public double OperationsPerSecond { get; }

        public double WallClockMs { get; }

        public long GcAllocations { get; }

        public long GcAllocatedBytes { get; }

        public long ObservedInvocations { get; }

        public string ToStructuredLog()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "DXM_TARGET_MAP_BENCHMARK scenario={0} keyCount={1} operation={2} "
                    + "totalOperations={3} operationsPerSecond={4:F3} wallClockMs={5:F3} "
                    + "gcAllocations={6} gcAllocatedBytes={7} observedInvocations={8}",
                BenchmarkCase.Key,
                BenchmarkCase.KeyCount,
                BenchmarkCase.Operation,
                TotalOperations,
                OperationsPerSecond,
                WallClockMs,
                GcAllocations,
                GcAllocatedBytes,
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
            int physicalTargetSlots
        )
        {
            OperationInvocations = operationInvocations;
            OriginalTargetInvocations = originalTargetInvocations;
            CurrentTargetInvocations = currentTargetInvocations;
            RegisteredTargets = registeredTargets;
            PhysicalTargetSlots = physicalTargetSlots;
        }

        internal long OperationInvocations { get; }

        internal long OriginalTargetInvocations { get; }

        internal long CurrentTargetInvocations { get; }

        internal int RegisteredTargets { get; }

        internal int PhysicalTargetSlots { get; }
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
        }
    }
}
#endif
