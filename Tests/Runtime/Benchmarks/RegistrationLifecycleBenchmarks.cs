#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Benchmarks
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;
    using UnityEngine;
    using Debug = UnityEngine.Debug;

    public enum RegistrationLifecycleOperation
    {
        Stage,
        Enable,
        Disable,
        ReEnable,
        Remove,
        Retarget,
        Dispose,
    }

    /// <summary>
    /// Registration lifecycle latency matrix. Lifecycle operations consume or mutate their
    /// prepared state, so each case takes the minimum of seven fresh, warmed timing trials
    /// without folding state reconstruction into the timed region. Allocation is measured in
    /// a separate prepared pass so profiler overhead cannot distort the latency sample.
    /// </summary>
    public sealed class RegistrationLifecycleBenchmarks
    {
        private const int TimingTrials = 7;

        [Test, Performance, Category("PerfBench")]
        [TestCaseSource(nameof(LifecycleBenchmarkCases))]
        public void RegistrationLifecycleBenchmark(
            RegistrationLifecycleOperation operation,
            int cardinality
        )
        {
            RegistrationLifecycleBenchmarkResult result = RunScenario(operation, cardinality);
            Debug.Log(result.ToStructuredLog());
            TestContext.Out.WriteLine(result.ToCsvRow());
        }

        internal static RegistrationLifecycleObservation ExecuteOnceForContract(
            RegistrationLifecycleOperation operation,
            int cardinality
        )
        {
            ValidateCardinality(cardinality);
            using LifecycleState state = PrepareState(operation, cardinality);
            Execute(operation, state);
            return Verify(operation, state);
        }

        internal static RegistrationLifecycleBenchmarkResult RunScenario(
            RegistrationLifecycleOperation operation,
            int cardinality
        )
        {
            ValidateCardinality(cardinality);

            // Warm the exact lifecycle path on throwaway state. Preparation, correctness
            // checks, and allocation probing are deliberately outside the timing trials.
            _ = ExecuteOnceForContract(operation, cardinality);

            double minElapsedSeconds = double.MaxValue;
            for (int trial = 0; trial < TimingTrials; trial++)
            {
                using LifecycleState timingState = PrepareState(operation, cardinality);
                AllocationProbe.SettleHeapForMeasurement();
                long startTimestamp = Stopwatch.GetTimestamp();
                Execute(operation, timingState);
                long endTimestamp = Stopwatch.GetTimestamp();
                _ = Verify(operation, timingState);
                double elapsedSeconds =
                    (endTimestamp - startTimestamp) / (double)Stopwatch.Frequency;
                if (elapsedSeconds < minElapsedSeconds)
                {
                    minElapsedSeconds = elapsedSeconds;
                }
            }

            RegistrationLifecycleObservation observation;
            AllocationProbe.AllocationSample allocation;
            using (LifecycleState allocationState = PrepareState(operation, cardinality))
            {
                AllocationProbe.SettleHeapForMeasurement();
                using AllocationProbe.Window window = AllocationProbe.BeginWindow();
                Execute(operation, allocationState);
                allocation = window.SampleBoth();
                observation = Verify(operation, allocationState);
            }

            double registrationsPerSecond =
                cardinality / Math.Max(minElapsedSeconds, double.Epsilon);
            return new RegistrationLifecycleBenchmarkResult(
                operation,
                cardinality,
                minElapsedSeconds * 1000d,
                registrationsPerSecond,
                allocation.Allocations,
                allocation.Bytes,
                observation
            );
        }

        private static IEnumerable<TestCaseData> LifecycleBenchmarkCases()
        {
            foreach (
                RegistrationLifecycleBenchmarkCase benchmarkCase in RegistrationLifecycleScenarios.All
            )
            {
                yield return new TestCaseData(
                    benchmarkCase.Operation,
                    benchmarkCase.Cardinality
                ).SetName(benchmarkCase.Key);
            }
        }

        private static LifecycleState PrepareState(
            RegistrationLifecycleOperation operation,
            int cardinality
        )
        {
            LifecycleState state = new(cardinality);
            try
            {
                switch (operation)
                {
                    case RegistrationLifecycleOperation.Stage:
                        break;
                    case RegistrationLifecycleOperation.Enable:
                        state.StageAll();
                        break;
                    case RegistrationLifecycleOperation.Disable:
                    case RegistrationLifecycleOperation.Remove:
                    case RegistrationLifecycleOperation.Retarget:
                    case RegistrationLifecycleOperation.Dispose:
                        state.StageAll();
                        state.Token.Enable();
                        break;
                    case RegistrationLifecycleOperation.ReEnable:
                        state.StageAll();
                        state.Token.Enable();
                        state.Token.Disable();
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
                }

                return state;
            }
            catch
            {
                try
                {
                    state.Dispose();
                }
                catch
                {
                    // Preserve the setup failure; cleanup is best-effort on an incomplete state.
                }

                throw;
            }
        }

        private static void Execute(RegistrationLifecycleOperation operation, LifecycleState state)
        {
            switch (operation)
            {
                case RegistrationLifecycleOperation.Stage:
                    state.StageAll();
                    return;
                case RegistrationLifecycleOperation.Enable:
                case RegistrationLifecycleOperation.ReEnable:
                    state.Token.Enable();
                    return;
                case RegistrationLifecycleOperation.Disable:
                    state.Token.Disable();
                    return;
                case RegistrationLifecycleOperation.Remove:
                    state.RemoveAll();
                    return;
                case RegistrationLifecycleOperation.Retarget:
                    state.Token.RetargetMessageBus(
                        state.SecondaryBus,
                        MessageBusRebindMode.RebindActive
                    );
                    return;
                case RegistrationLifecycleOperation.Dispose:
                    state.Token.Dispose();
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
            }
        }

        private static RegistrationLifecycleObservation Verify(
            RegistrationLifecycleOperation operation,
            LifecycleState state
        )
        {
            int expectedPrimaryRegistrations = 0;
            int expectedSecondaryRegistrations = 0;
            int expectedInvocations = 0;
            switch (operation)
            {
                case RegistrationLifecycleOperation.Stage:
                    Assert.AreEqual(0, state.PrimaryBus.RegisteredUntargeted);
                    state.Token.Enable();
                    expectedPrimaryRegistrations = 1;
                    expectedInvocations = state.Cardinality;
                    break;
                case RegistrationLifecycleOperation.Enable:
                case RegistrationLifecycleOperation.ReEnable:
                    expectedPrimaryRegistrations = 1;
                    expectedInvocations = state.Cardinality;
                    break;
                case RegistrationLifecycleOperation.Disable:
                case RegistrationLifecycleOperation.Remove:
                case RegistrationLifecycleOperation.Dispose:
                    break;
                case RegistrationLifecycleOperation.Retarget:
                    expectedSecondaryRegistrations = 1;
                    expectedInvocations = state.Cardinality;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
            }

            Assert.AreEqual(
                expectedPrimaryRegistrations,
                state.PrimaryBus.RegisteredUntargeted,
                $"{operation} left an unexpected registration count on the original bus."
            );
            Assert.AreEqual(
                expectedSecondaryRegistrations,
                state.SecondaryBus.RegisteredUntargeted,
                $"{operation} left an unexpected registration count on the retarget bus."
            );

            state.Counter.Reset();
            LifecycleMessage message = default;
            state.PrimaryBus.UntargetedBroadcast(ref message);
            state.SecondaryBus.UntargetedBroadcast(ref message);
            Assert.AreEqual(
                expectedInvocations,
                state.Counter.Count,
                $"{operation} produced an unexpected exact dispatch fan-out after mutation."
            );

            return new RegistrationLifecycleObservation(
                state.PrimaryBus.RegisteredUntargeted,
                state.SecondaryBus.RegisteredUntargeted,
                state.Counter.Count
            );
        }

        private static void ValidateCardinality(int cardinality)
        {
            if (cardinality <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(cardinality),
                    cardinality,
                    "Registration cardinality must be positive."
                );
            }
        }

        private readonly struct LifecycleMessage : IUntargetedMessage { }

        private sealed class LifecycleCounter
        {
            public int Count { get; private set; }

            public void Increment(ref LifecycleMessage message)
            {
                Count++;
            }

            public void Reset()
            {
                Count = 0;
            }
        }

        private sealed class LifecycleState : IDisposable
        {
            private readonly MessageRegistrationHandle[] _handles;
            private readonly MessageHandler.FastHandler<LifecycleMessage>[] _handlers;
            private bool _disposed;

            public LifecycleState(int cardinality)
            {
                Cardinality = cardinality;
                // Keep lifecycle measurements independent of the host editor's mutable
                // global diagnostics preference. Diagnostics have separate coverage;
                // these rows characterize registration storage and teardown itself.
                PrimaryBus = new MessageBus { DiagnosticsMode = false };
                SecondaryBus = new MessageBus { DiagnosticsMode = false };
                Counter = new LifecycleCounter();
                MessageHandler handler = new(new InstanceId(41001), PrimaryBus) { active = true };
                Token = MessageRegistrationToken.Create(handler, PrimaryBus);
                Token.DiagnosticMode = false;
                _handles = new MessageRegistrationHandle[cardinality];
                _handlers = new MessageHandler.FastHandler<LifecycleMessage>[cardinality];
                for (int index = 0; index < cardinality; index++)
                {
                    int capturedIndex = index;
                    _handlers[index] = (ref LifecycleMessage message) =>
                    {
                        _ = capturedIndex;
                        Counter.Increment(ref message);
                    };
                }
            }

            public int Cardinality { get; }

            public MessageBus PrimaryBus { get; }

            public MessageBus SecondaryBus { get; }

            public MessageRegistrationToken Token { get; }

            public LifecycleCounter Counter { get; }

            public void StageAll()
            {
                for (int index = 0; index < _handles.Length; index++)
                {
                    _handles[index] = Token.RegisterUntargeted<LifecycleMessage>(_handlers[index]);
                }
            }

            public void RemoveAll()
            {
                for (int index = 0; index < _handles.Length; index++)
                {
                    Token.RemoveRegistration(_handles[index]);
                }
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                Token.Dispose();
            }
        }
    }

    public static class RegistrationLifecycleScenarios
    {
        private static readonly RegistrationLifecycleOperation[] Operations =
        {
            RegistrationLifecycleOperation.Stage,
            RegistrationLifecycleOperation.Enable,
            RegistrationLifecycleOperation.Disable,
            RegistrationLifecycleOperation.ReEnable,
            RegistrationLifecycleOperation.Remove,
            RegistrationLifecycleOperation.Retarget,
            RegistrationLifecycleOperation.Dispose,
        };

        private static readonly int[] Cardinalities = { 1, 4, 16, 1000 };
        private static readonly RegistrationLifecycleBenchmarkCase[] Cases = BuildCases();

        public static IReadOnlyList<RegistrationLifecycleBenchmarkCase> All => Cases;

        private static RegistrationLifecycleBenchmarkCase[] BuildCases()
        {
            RegistrationLifecycleBenchmarkCase[] cases = new RegistrationLifecycleBenchmarkCase[
                Operations.Length * Cardinalities.Length
            ];
            int write = 0;
            for (int operationIndex = 0; operationIndex < Operations.Length; operationIndex++)
            {
                for (
                    int cardinalityIndex = 0;
                    cardinalityIndex < Cardinalities.Length;
                    cardinalityIndex++
                )
                {
                    cases[write++] = new RegistrationLifecycleBenchmarkCase(
                        Operations[operationIndex],
                        Cardinalities[cardinalityIndex]
                    );
                }
            }

            return cases;
        }
    }

    public readonly struct RegistrationLifecycleBenchmarkCase
    {
        public RegistrationLifecycleBenchmarkCase(
            RegistrationLifecycleOperation operation,
            int cardinality
        )
        {
            Operation = operation;
            Cardinality = cardinality;
        }

        public RegistrationLifecycleOperation Operation { get; }

        public int Cardinality { get; }

        public string Key => $"RegistrationLifecycle_{Operation}_{Cardinality}";
    }

    public readonly struct RegistrationLifecycleObservation
    {
        public RegistrationLifecycleObservation(
            int primaryRegistrations,
            int secondaryRegistrations,
            int handlerInvocations
        )
        {
            PrimaryRegistrations = primaryRegistrations;
            SecondaryRegistrations = secondaryRegistrations;
            HandlerInvocations = handlerInvocations;
        }

        public int PrimaryRegistrations { get; }

        public int SecondaryRegistrations { get; }

        public int HandlerInvocations { get; }
    }

    public readonly struct RegistrationLifecycleBenchmarkResult
    {
        public RegistrationLifecycleBenchmarkResult(
            RegistrationLifecycleOperation operation,
            int cardinality,
            double wallClockMs,
            double registrationsPerSecond,
            long gcAllocations,
            long gcAllocatedBytes,
            RegistrationLifecycleObservation observation
        )
        {
            Operation = operation;
            Cardinality = cardinality;
            WallClockMs = wallClockMs;
            RegistrationsPerSecond = registrationsPerSecond;
            GcAllocations = gcAllocations;
            GcAllocatedBytes = gcAllocatedBytes;
            Observation = observation;
        }

        public RegistrationLifecycleOperation Operation { get; }

        public int Cardinality { get; }

        public double WallClockMs { get; }

        public double RegistrationsPerSecond { get; }

        public long GcAllocations { get; }

        public long GcAllocatedBytes { get; }

        public RegistrationLifecycleObservation Observation { get; }

        public string ToStructuredLog()
        {
            return "DX_LIFECYCLE_BENCHMARK "
                + $"operation={Operation} cardinality={Cardinality} "
                + $"registrationsPerSecond={RegistrationsPerSecond.ToString("F2", CultureInfo.InvariantCulture)} "
                + $"wallClockMs={WallClockMs.ToString("F4", CultureInfo.InvariantCulture)} "
                + $"gcAllocations={FormatAllocation(GcAllocations)} "
                + $"gcAllocatedBytes={FormatAllocation(GcAllocatedBytes)}";
        }

        public string ToCsvRow()
        {
            return string.Join(
                ",",
                "registration-lifecycle",
                Operation.ToString(),
                Cardinality.ToString(CultureInfo.InvariantCulture),
                RegistrationsPerSecond.ToString("R", CultureInfo.InvariantCulture),
                WallClockMs.ToString("R", CultureInfo.InvariantCulture),
                FormatAllocation(GcAllocations),
                FormatAllocation(GcAllocatedBytes)
            );
        }

        private static string FormatAllocation(long value) =>
            value == AllocationProbe.Unmeasured
                ? "n/a"
                : value.ToString(CultureInfo.InvariantCulture);
    }
}
#endif
