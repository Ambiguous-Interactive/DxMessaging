#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Benchmarks
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.Runtime.CompilerServices;
    using System.Text;
    using DxMessaging.Core;
    using DxMessaging.Core.Internal;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Runtime;
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
        private const int AllocationAttempts = 8;

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

            // Collect once before the timing loop. Every trial still prepares fresh state
            // outside its stopwatch; repeating a forced collection before each sub-millisecond
            // sample makes the reported floor depend on collection overhead.
            AllocationProbe.SettleHeapForMeasurement();
            double minElapsedSeconds = double.MaxValue;
            for (int trial = 0; trial < TimingTrials; trial++)
            {
                using LifecycleState timingState = PrepareState(operation, cardinality);
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

            LifecycleState allocationState = null;
            bool allocationStateExecuted = false;
            AllocationProbe.MinimumMeasurement<RegistrationLifecycleObservation> allocation;
            try
            {
                allocation = AllocationProbe.MeasureMinWithDiagnostics(
                    AllocationAttempts,
                    prepare: () =>
                    {
                        if (allocationState != null)
                        {
                            if (allocationStateExecuted)
                            {
                                _ = Verify(operation, allocationState);
                                allocationStateExecuted = false;
                            }

                            allocationState.Dispose();
                        }

                        allocationState = PrepareState(operation, cardinality);
                        allocationStateExecuted = false;
                    },
                    operation: () =>
                    {
                        Execute(operation, allocationState);
                        allocationStateExecuted = true;
                        return Observe(allocationState);
                    }
                );

                if (allocationStateExecuted)
                {
                    _ = Verify(operation, allocationState);
                    allocationStateExecuted = false;
                }
            }
            finally
            {
                allocationState?.Dispose();
            }

            if (allocation.GcAllocations != AllocationProbe.Unmeasured)
            {
                AssertAllocationObservation(operation, allocation.Diagnostics);
            }

            double registrationsPerSecond =
                cardinality / Math.Max(minElapsedSeconds, double.Epsilon);
            return new RegistrationLifecycleBenchmarkResult(
                operation,
                cardinality,
                minElapsedSeconds * 1000d,
                registrationsPerSecond,
                allocation.GcAllocations,
                allocation.GcAllocatedBytes,
                allocation.Diagnostics
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

        private static RegistrationLifecycleObservation Observe(LifecycleState state)
        {
            return new RegistrationLifecycleObservation(
                state.PrimaryBus.RegisteredUntargeted,
                state.SecondaryBus.RegisteredUntargeted,
                state.Counter.Count
            );
        }

        private static void AssertAllocationObservation(
            RegistrationLifecycleOperation operation,
            RegistrationLifecycleObservation observation
        )
        {
            int expectedPrimaryRegistrations = operation switch
            {
                RegistrationLifecycleOperation.Enable => 1,
                RegistrationLifecycleOperation.ReEnable => 1,
                _ => 0,
            };
            int expectedSecondaryRegistrations =
                operation == RegistrationLifecycleOperation.Retarget ? 1 : 0;
            Assert.AreEqual(
                expectedPrimaryRegistrations,
                observation.PrimaryRegistrations,
                $"{operation}: selected allocation attempt had unexpected primary bus state."
            );
            Assert.AreEqual(
                expectedSecondaryRegistrations,
                observation.SecondaryRegistrations,
                $"{operation}: selected allocation attempt had unexpected secondary bus state."
            );
            Assert.AreEqual(
                0,
                observation.HandlerInvocations,
                $"{operation}: selected allocation attempt dispatched during the measured operation."
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

            public void Increment(in LifecycleMessage message)
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
            private readonly IDisposable _registryScope;
            private readonly MessageRegistrationHandle[] _handles;
            private readonly MessageHandler.FastHandler<LifecycleMessage>[] _handlers;
            private bool _disposed;

            public LifecycleState(int cardinality)
            {
                Cardinality = cardinality;
                _registryScope = MessageBus.IsolateIdleSweepRegistryForBenchmark();
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
                    _handlers[index] = (in LifecycleMessage message) =>
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
                try
                {
                    Token.Dispose();
                }
                finally
                {
                    _registryScope.Dispose();
                }
            }
        }
    }

    public enum RegistrationAttributionOperation
    {
        DirectBus,
        DirectHandler,
        TokenStage,
        TokenActive,
    }

    /// <summary>
    /// Attributes the complete register/remove cycle across the bus, handler cache, disabled token,
    /// and active token layers. Each row reuses one by-ref delegate and returns to an empty state
    /// after every cycle. The active-token row matches the comparison suite's
    /// subscribe/unsubscribe workload.
    /// </summary>
    internal static class RegistrationAttributionBenchmarks
    {
        internal const int CycleCount = 131_072;
        internal const int AllocationCycleCount = BenchmarkProtocol.BatchSize;
        private const int TimingTrials = 7;
        private const int AllocationAttempts = 7;

        internal static RegistrationAttributionObservation ExecuteOnceForContract(
            RegistrationAttributionOperation operation
        )
        {
            using RegistrationAttributionState state = new(operation);
            using LeakWatcher watcher = new(
                state.Bus,
                label: $"registration attribution observed cycle ({operation})"
            );
            return state.ExecuteSingleCycleWithObservation();
        }

        internal static void ExecuteCyclesForContract(
            RegistrationAttributionOperation operation,
            int cycleCount
        )
        {
            using RegistrationAttributionState state = new(operation);
            using LeakWatcher watcher = new(
                state.Bus,
                label: $"registration attribution repeated cycles ({operation})"
            );
            state.ExecuteCycles(cycleCount);
            state.ExecuteCycles(cycleCount);
            state.VerifyFinal(expectedCycles: cycleCount * 2);
        }

        internal static DispatchBenchmarkResult RunScenario(
            RegistrationAttributionOperation operation
        )
        {
            using (RegistrationAttributionState warmup = new(operation))
            {
                warmup.ExecuteCycles(cycleCount: 16);
                warmup.VerifyFinal(expectedCycles: 16);
            }

            AllocationProbe.SettleHeapForMeasurement();
            double minElapsedSeconds = double.MaxValue;
            for (int trial = 0; trial < TimingTrials; trial++)
            {
                using RegistrationAttributionState state = new(operation);
                long startTimestamp = Stopwatch.GetTimestamp();
                state.ExecuteCycles(CycleCount);
                long endTimestamp = Stopwatch.GetTimestamp();
                state.VerifyFinal(CycleCount);
                double elapsedSeconds =
                    (endTimestamp - startTimestamp) / (double)Stopwatch.Frequency;
                if (elapsedSeconds < minElapsedSeconds)
                {
                    minElapsedSeconds = elapsedSeconds;
                }
            }

            AllocationProbe.AllocationSample allocation = MeasureAllocation(operation);

            return DispatchBenchmarkResult.ForRegistrationScenario(
                ScenarioKey(operation),
                runIndex: -1,
                allocation.Allocations,
                allocation.Bytes,
                minElapsedSeconds * 1000d
            );
        }

        private static AllocationProbe.AllocationSample MeasureAllocation(
            RegistrationAttributionOperation operation
        )
        {
            AllocationProbe.SettleHeapForMeasurement();
            if (!AllocationProbe.IsFunctional)
            {
                return new AllocationProbe.AllocationSample(
                    AllocationProbe.Unmeasured,
                    AllocationProbe.Unmeasured
                );
            }

            long minimumCount = long.MaxValue;
            long minimumBytes = AllocationProbe.Unmeasured;
            try
            {
                for (int attempt = 0; attempt < AllocationAttempts; attempt++)
                {
                    using RegistrationAttributionState state = new(operation);
                    // Match BenchmarkProtocol.Measure: warm this exact bus/token state before
                    // opening the profiler recorder, then measure one same-sized steady-state batch.
                    state.ExecuteCycles(AllocationCycleCount);
                    AllocationProbe.AllocationSample sample;
                    using (AllocationProbe.Window window = AllocationProbe.BeginWindow())
                    {
                        state.ExecuteCycles(AllocationCycleCount);
                        sample = window.SampleBoth();
                    }
                    state.VerifyFinal(expectedCycles: AllocationCycleCount * 2);
                    if (
                        AllocationProbe.ShouldReplaceMinimumAttempt(
                            sample.Allocations,
                            sample.Bytes,
                            minimumCount,
                            minimumBytes
                        )
                    )
                    {
                        minimumCount = sample.Allocations;
                        minimumBytes = sample.Bytes;
                    }
                }
            }
            finally
            {
                AllocationProbe.SettleHeapForMeasurement();
            }

            return new AllocationProbe.AllocationSample(minimumCount, minimumBytes);
        }

        internal static string ScenarioKey(RegistrationAttributionOperation operation)
        {
            // SYNC: scripts/unity/perf-scenarios.js mirrors these stable rendered keys.
            return operation switch
            {
                RegistrationAttributionOperation.DirectBus =>
                    $"RegistrationAttribution_DirectBus_{CycleCount}",
                RegistrationAttributionOperation.DirectHandler =>
                    $"RegistrationAttribution_DirectHandler_{CycleCount}",
                RegistrationAttributionOperation.TokenStage =>
                    $"RegistrationAttribution_TokenStage_{CycleCount}",
                RegistrationAttributionOperation.TokenActive =>
                    $"RegistrationAttribution_TokenActive_{CycleCount}",
                _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
            };
        }

        private readonly struct AttributionMessage : IUntargetedMessage { }

        private interface IRegistrationCyclePath
        {
            RegistrationAttributionStateObservation ExecuteObservedCycle(
                RegistrationAttributionState state
            );

            void ExecuteCycles(RegistrationAttributionState state, int cycleCount);
        }

        private sealed class DirectBusCyclePath : IRegistrationCyclePath
        {
            public RegistrationAttributionStateObservation ExecuteObservedCycle(
                RegistrationAttributionState state
            )
            {
                MessageBusRegistration registration = state.RegisterDirectBus();
                RegistrationAttributionStateObservation live = state.ObserveState();
                state.DeregisterDirectBus(in registration);
                return live;
            }

            public void ExecuteCycles(RegistrationAttributionState state, int cycleCount)
            {
                for (int index = 0; index < cycleCount; index++)
                {
                    MessageBusRegistration registration = state.RegisterDirectBus();
                    state.DeregisterDirectBus(in registration);
                }
            }
        }

        private sealed class DirectHandlerCyclePath : IRegistrationCyclePath
        {
            public RegistrationAttributionStateObservation ExecuteObservedCycle(
                RegistrationAttributionState state
            )
            {
                MessageHandler.TypedHandler<AttributionMessage>.TypedHandlerDeregistrationState deregistration =
                    state.RegisterDirectHandler();
                RegistrationAttributionStateObservation live = state.ObserveState();
                state.DeregisterDirectHandler(in deregistration);
                return live;
            }

            public void ExecuteCycles(RegistrationAttributionState state, int cycleCount)
            {
                for (int index = 0; index < cycleCount; index++)
                {
                    MessageHandler.TypedHandler<AttributionMessage>.TypedHandlerDeregistrationState deregistration =
                        state.RegisterDirectHandler();
                    state.DeregisterDirectHandler(in deregistration);
                }
            }
        }

        private sealed class TokenCyclePath : IRegistrationCyclePath
        {
            public RegistrationAttributionStateObservation ExecuteObservedCycle(
                RegistrationAttributionState state
            )
            {
                MessageRegistrationHandle handle = state.RegisterToken();
                RegistrationAttributionStateObservation live = state.ObserveState();
                state.RemoveToken(handle);
                return live;
            }

            public void ExecuteCycles(RegistrationAttributionState state, int cycleCount)
            {
                for (int index = 0; index < cycleCount; index++)
                {
                    MessageRegistrationHandle handle = state.RegisterToken();
                    state.RemoveToken(handle);
                }
            }
        }

        private static readonly IRegistrationCyclePath DirectBusPath = new DirectBusCyclePath();
        private static readonly IRegistrationCyclePath DirectHandlerPath =
            new DirectHandlerCyclePath();
        private static readonly IRegistrationCyclePath TokenPath = new TokenCyclePath();

        private static IRegistrationCyclePath ResolveCyclePath(
            RegistrationAttributionOperation operation
        )
        {
            return operation switch
            {
                RegistrationAttributionOperation.DirectBus => DirectBusPath,
                RegistrationAttributionOperation.DirectHandler => DirectHandlerPath,
                RegistrationAttributionOperation.TokenStage => TokenPath,
                RegistrationAttributionOperation.TokenActive => TokenPath,
                _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
            };
        }

        private sealed class RegistrationAttributionState : IDisposable
        {
            private readonly RegistrationAttributionOperation _operation;
            private readonly IRegistrationCyclePath _cyclePath;
            private readonly IDisposable _registryScope;
            private readonly MessageBus _bus;
            private readonly MessageHandler _handler;
            private readonly MessageHandler.FastHandler<AttributionMessage> _callback;
            private readonly MessageRegistrationToken _token;
            private int _completedCycles;
            private int _handlerInvocations;
            private bool _disposed;

            public RegistrationAttributionState(RegistrationAttributionOperation operation)
            {
                _operation = operation;
                _cyclePath = ResolveCyclePath(operation);
                _registryScope = MessageBus.IsolateIdleSweepRegistryForBenchmark();
                _bus = new MessageBus { DiagnosticsMode = false };
                _handler = new MessageHandler(new InstanceId(43001), _bus) { active = true };
                _callback = Handle;
                if (
                    operation == RegistrationAttributionOperation.TokenStage
                    || operation == RegistrationAttributionOperation.TokenActive
                )
                {
                    _token = MessageRegistrationToken.Create(_handler, _bus);
                    _token.DiagnosticMode = false;
                    if (operation == RegistrationAttributionOperation.TokenActive)
                    {
                        _token.Enable();
                    }
                }
            }

            public IMessageBus Bus => _bus;

            public RegistrationAttributionObservation ExecuteSingleCycleWithObservation()
            {
                RegistrationAttributionStateObservation live = _cyclePath.ExecuteObservedCycle(
                    this
                );
                _completedCycles = 1;
                RegistrationAttributionStateObservation final = ObserveState();
                return new RegistrationAttributionObservation(_operation, live, final);
            }

            public void ExecuteCycles(int cycleCount)
            {
                if (cycleCount <= 0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(cycleCount),
                        cycleCount,
                        "Registration attribution cycle count must be positive."
                    );
                }
                _cyclePath.ExecuteCycles(this, cycleCount);
                _completedCycles = checked(_completedCycles + cycleCount);
            }

            public void VerifyFinal(int expectedCycles)
            {
                Assert.AreEqual(
                    expectedCycles,
                    _completedCycles,
                    $"{_operation}: completed cycle count drifted."
                );
                RegistrationAttributionStateObservation final = ObserveState();
                Assert.AreEqual(
                    0,
                    final.BusRegistrations,
                    $"{_operation}: registration cycles left a bus registration live."
                );
                Assert.AreEqual(
                    0,
                    final.HandlerRegistrations,
                    $"{_operation}: registration cycles left a flat handler live."
                );
                Assert.AreEqual(
                    0,
                    final.TokenRegistrations,
                    $"{_operation}: registration cycles left token metadata live."
                );
                Assert.AreEqual(
                    0,
                    final.HandlerInvocations,
                    $"{_operation}: registration cycles still delivered after removal."
                );
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public MessageBusRegistration RegisterDirectBus()
            {
                return _bus.RegisterUntargeted<AttributionMessage>(_handler);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void DeregisterDirectBus(in MessageBusRegistration registration)
            {
                _bus.Deregister<AttributionMessage>(in registration);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public MessageHandler.TypedHandler<AttributionMessage>.TypedHandlerDeregistrationState RegisterDirectHandler()
            {
                return _handler.RegisterUntargetedMessageHandler(
                    _callback,
                    _callback,
                    messageBus: _bus
                );
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void DeregisterDirectHandler(
                in MessageHandler.TypedHandler<AttributionMessage>.TypedHandlerDeregistrationState deregistration
            )
            {
                deregistration.Deregister();
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public MessageRegistrationHandle RegisterToken()
            {
                return _token.RegisterUntargeted<AttributionMessage>(_callback);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void RemoveToken(MessageRegistrationHandle handle)
            {
                _token.RemoveRegistration(handle);
            }

            public RegistrationAttributionStateObservation ObserveState()
            {
                _handlerInvocations = 0;
                AttributionMessage message = default;
                _bus.UntargetedBroadcast(ref message);
                return new RegistrationAttributionStateObservation(
                    _bus.RegisteredUntargeted,
                    CountHandlerRegistrations(),
                    _token?._metadata.Count ?? 0,
                    _handlerInvocations
                );
            }

            private void Handle(in AttributionMessage message)
            {
                _handlerInvocations++;
            }

            private int CountHandlerRegistrations()
            {
                return _handler.CountFlatHandlers<AttributionMessage>(
                    _bus,
                    priority: 0,
                    fastIndex: TypedSlotIndex.UntargetedHandleFast,
                    defaultIndex: TypedSlotIndex.UntargetedHandleDefault
                );
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                try
                {
                    _token?.Dispose();
                }
                finally
                {
                    _registryScope.Dispose();
                }
            }
        }
    }

    public readonly struct RegistrationAttributionObservation
    {
        public RegistrationAttributionObservation(
            RegistrationAttributionOperation operation,
            RegistrationAttributionStateObservation live,
            RegistrationAttributionStateObservation final
        )
        {
            Operation = operation;
            Live = live;
            Final = final;
        }

        public RegistrationAttributionOperation Operation { get; }

        public RegistrationAttributionStateObservation Live { get; }

        public RegistrationAttributionStateObservation Final { get; }
    }

    public readonly struct RegistrationAttributionStateObservation
    {
        public RegistrationAttributionStateObservation(
            int busRegistrations,
            int handlerRegistrations,
            int tokenRegistrations,
            int handlerInvocations
        )
        {
            BusRegistrations = busRegistrations;
            HandlerRegistrations = handlerRegistrations;
            TokenRegistrations = tokenRegistrations;
            HandlerInvocations = handlerInvocations;
        }

        public int BusRegistrations { get; }

        public int HandlerRegistrations { get; }

        public int TokenRegistrations { get; }

        public int HandlerInvocations { get; }
    }

    public enum DeregistrationAttributionOperation
    {
        DirectBus,
        DirectHandler,
        TokenRemove,
        TokenDisable,
    }

    /// <summary>
    /// Reports cumulative deregistration layers for direct bus, handler-cache, and per-handle token
    /// removal. Token queue teardown is a sibling end-to-end path because it retains staged token
    /// state. Every row uses a high-cardinality, same-type population so the timed region is long
    /// enough to compare without folding registration setup into the clock.
    /// </summary>
    internal static class DeregistrationAttributionBenchmarks
    {
        internal const int Cardinality = 131_072;
        internal const double MaxSamePathDriftPercent = 3d;
        internal const double MaxHandlerExcessSpreadPercent = 3d;
        private const int TimingTrials = 7;
        internal const int PalindromeTimingTrials = 8;

        internal static DeregistrationAttributionObservation ExecuteOnceForContract(
            DeregistrationAttributionOperation operation,
            int cardinality
        )
        {
            ValidateCardinality(cardinality);
            using DeregistrationAttributionState state = CreateState(operation, cardinality);
            state.VerifyPrepared();
            state.Execute();
            return state.Verify();
        }

        internal static DispatchBenchmarkResult RunScenario(
            DeregistrationAttributionOperation operation
        )
        {
            _ = ExecuteOnceForContract(operation, cardinality: 16);

            // Collect once before the timing loop. Each state is prepared before its stopwatch;
            // the minimum rejects a trial interrupted by later organic GC or scheduler work
            // without issuing seven heap-wide collections per row.
            AllocationProbe.SettleHeapForMeasurement();
            double minElapsedSeconds = double.MaxValue;
            for (int trial = 0; trial < TimingTrials; trial++)
            {
                using DeregistrationAttributionState state = CreateState(operation, Cardinality);
                state.VerifyPrepared();
                long startTimestamp = Stopwatch.GetTimestamp();
                state.Execute();
                long endTimestamp = Stopwatch.GetTimestamp();
                _ = state.Verify();
                double elapsedSeconds =
                    (endTimestamp - startTimestamp) / (double)Stopwatch.Frequency;
                if (elapsedSeconds < minElapsedSeconds)
                {
                    minElapsedSeconds = elapsedSeconds;
                }
            }

            return DispatchBenchmarkResult.ForRegistrationScenario(
                ScenarioKey(operation),
                runIndex: -1,
                AllocationProbe.Unmeasured,
                AllocationProbe.Unmeasured,
                minElapsedSeconds * 1000d
            );
        }

        internal static string ScenarioKey(DeregistrationAttributionOperation operation)
        {
            // SYNC: scripts/unity/perf-scenarios.js mirrors these stable rendered keys.
            return operation switch
            {
                DeregistrationAttributionOperation.DirectBus =>
                    $"DeregistrationAttribution_DirectBus_{Cardinality}",
                DeregistrationAttributionOperation.DirectHandler =>
                    $"DeregistrationAttribution_DirectHandler_{Cardinality}",
                DeregistrationAttributionOperation.TokenRemove =>
                    $"DeregistrationAttribution_TokenRemove_{Cardinality}",
                DeregistrationAttributionOperation.TokenDisable =>
                    $"DeregistrationAttribution_TokenDisable_{Cardinality}",
                _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
            };
        }

        internal static DeregistrationAttributionPalindromeDiagnostic AnalyzePalindrome(
            double handlerA,
            double busA,
            double busB,
            double handlerB
        )
        {
            return AnalyzePalindrome(
                new DeregistrationAttributionPalindromeSample(
                    handlerA,
                    busA,
                    busB,
                    handlerB,
                    trial: -1,
                    prepareForward: false
                ),
                timingTrials: 0,
                jointTrialSelection: false,
                sameTrialArms: false,
                preparationDirectionAlternated: false,
                trialSequence: "none"
            );
        }

        private static DeregistrationAttributionPalindromeDiagnostic AnalyzePalindrome(
            DeregistrationAttributionPalindromeSample sample,
            int timingTrials,
            bool jointTrialSelection,
            bool sameTrialArms,
            bool preparationDirectionAlternated,
            string trialSequence
        )
        {
            return new DeregistrationAttributionPalindromeDiagnostic(
                sample,
                timingTrials,
                jointTrialSelection,
                sameTrialArms,
                preparationDirectionAlternated,
                trialSequence
            );
        }

        internal static DeregistrationAttributionPalindromeDiagnostic RunPairedDiagnostic()
        {
            _ = ExecuteOnceForContract(
                DeregistrationAttributionOperation.DirectHandler,
                cardinality: 16
            );
            _ = ExecuteOnceForContract(
                DeregistrationAttributionOperation.DirectBus,
                cardinality: 16
            );

            // Select one joint H/B/B/H floor. All four fresh states in a trial are prepared
            // before the first stopwatch sample and timed back-to-back. Minimizing the complete
            // palindrome rejects interrupted trials without combining an arm from another host
            // phase. Alternate preparation direction so one endpoint is not always hottest.
            AllocationProbe.SettleHeapForMeasurement();
            DeregistrationAttributionPalindromeSample sample = MeasurePalindromeFloor(
                out string trialSequence
            );
            return AnalyzePalindrome(
                sample,
                PalindromeTimingTrials,
                jointTrialSelection: true,
                sameTrialArms: true,
                preparationDirectionAlternated: true,
                trialSequence: trialSequence
            );
        }

        internal static DeregistrationAttributionPalindromeSample SelectPalindromeFloor(
            DeregistrationAttributionPalindromeSample current,
            DeregistrationAttributionPalindromeSample candidate
        )
        {
            return candidate.TotalMs < current.TotalMs ? candidate : current;
        }

        internal static DeregistrationAttributionOperation PalindromeOperationAt(int armIndex)
        {
            return armIndex switch
            {
                0 => DeregistrationAttributionOperation.DirectHandler,
                1 => DeregistrationAttributionOperation.DirectBus,
                2 => DeregistrationAttributionOperation.DirectBus,
                3 => DeregistrationAttributionOperation.DirectHandler,
                _ => throw new ArgumentOutOfRangeException(nameof(armIndex), armIndex, null),
            };
        }

        private static DeregistrationAttributionPalindromeSample MeasurePalindromeFloor(
            out string trialSequence
        )
        {
            DeregistrationAttributionPalindromeSample best = default;
            bool hasBest = false;
            StringBuilder sequence = new();
            for (int trial = 0; trial < PalindromeTimingTrials; trial++)
            {
                bool prepareForward = (trial & 1) == 0;
                DeregistrationAttributionPalindromeSample candidate = MeasurePalindromeTrial(
                    trial,
                    prepareForward
                );
                if (trial > 0)
                {
                    sequence.Append(',');
                }
                sequence
                    .Append(trial)
                    .Append(':')
                    .Append(prepareForward ? 'F' : 'R')
                    .Append(':')
                    .Append(candidate.TotalMs.ToString("R", CultureInfo.InvariantCulture));
                best = hasBest ? SelectPalindromeFloor(best, candidate) : candidate;
                hasBest = true;
            }

            trialSequence = sequence.ToString();
            return best;
        }

        private static DeregistrationAttributionPalindromeSample MeasurePalindromeTrial(
            int trial,
            bool prepareForward
        )
        {
            if (prepareForward)
            {
                using DeregistrationAttributionState handlerA = NewState(
                    DeregistrationAttributionOperation.DirectHandler
                );
                using DeregistrationAttributionState busA = NewState(
                    DeregistrationAttributionOperation.DirectBus
                );
                using DeregistrationAttributionState busB = NewState(
                    DeregistrationAttributionOperation.DirectBus
                );
                using DeregistrationAttributionState handlerB = NewState(
                    DeregistrationAttributionOperation.DirectHandler
                );
                return MeasurePreparedPalindrome(
                    handlerA,
                    busA,
                    busB,
                    handlerB,
                    trial,
                    prepareForward
                );
            }

            using DeregistrationAttributionState reverseHandlerB = NewState(
                DeregistrationAttributionOperation.DirectHandler
            );
            using DeregistrationAttributionState reverseBusB = NewState(
                DeregistrationAttributionOperation.DirectBus
            );
            using DeregistrationAttributionState reverseBusA = NewState(
                DeregistrationAttributionOperation.DirectBus
            );
            using DeregistrationAttributionState reverseHandlerA = NewState(
                DeregistrationAttributionOperation.DirectHandler
            );
            return MeasurePreparedPalindrome(
                reverseHandlerA,
                reverseBusA,
                reverseBusB,
                reverseHandlerB,
                trial,
                prepareForward
            );
        }

        private static DeregistrationAttributionState NewState(
            DeregistrationAttributionOperation operation
        )
        {
            return CreateState(operation, Cardinality);
        }

        private static DeregistrationAttributionState CreateState(
            DeregistrationAttributionOperation operation,
            int cardinality
        )
        {
            IDisposable registryScope = MessageBus.IsolateIdleSweepRegistryForBenchmark();
            try
            {
                return new DeregistrationAttributionState(operation, cardinality, registryScope);
            }
            catch
            {
                registryScope.Dispose();
                throw;
            }
        }

        private static DeregistrationAttributionPalindromeSample MeasurePreparedPalindrome(
            DeregistrationAttributionState handlerA,
            DeregistrationAttributionState busA,
            DeregistrationAttributionState busB,
            DeregistrationAttributionState handlerB,
            int trial,
            bool prepareForward
        )
        {
            handlerA.VerifyPrepared();
            busA.VerifyPrepared();
            busB.VerifyPrepared();
            handlerB.VerifyPrepared();

            int nextArm = 0;
            long startTimestamp = Stopwatch.GetTimestamp();
            ExecutePalindromeArm(ref nextArm, handlerA);
            long handlerABoundary = Stopwatch.GetTimestamp();
            ExecutePalindromeArm(ref nextArm, busA);
            long busABoundary = Stopwatch.GetTimestamp();
            ExecutePalindromeArm(ref nextArm, busB);
            long busBBoundary = Stopwatch.GetTimestamp();
            ExecutePalindromeArm(ref nextArm, handlerB);
            long endTimestamp = Stopwatch.GetTimestamp();

            _ = handlerA.Verify();
            _ = busA.Verify();
            _ = busB.Verify();
            _ = handlerB.Verify();
            return new DeregistrationAttributionPalindromeSample(
                TimestampDeltaToMilliseconds(startTimestamp, handlerABoundary),
                TimestampDeltaToMilliseconds(handlerABoundary, busABoundary),
                TimestampDeltaToMilliseconds(busABoundary, busBBoundary),
                TimestampDeltaToMilliseconds(busBBoundary, endTimestamp),
                trial,
                prepareForward
            );
        }

        private static void ExecutePalindromeArm(
            ref int nextArm,
            DeregistrationAttributionState state
        )
        {
            DeregistrationAttributionOperation expected = PalindromeOperationAt(nextArm);
            if (state.Operation != expected)
            {
                throw new InvalidOperationException(
                    $"Deregistration palindrome arm {nextArm} must be {expected}, not {state.Operation}."
                );
            }

            state.Execute();
            nextArm++;
        }

        private static double TimestampDeltaToMilliseconds(long start, long end)
        {
            return (end - start) / (double)Stopwatch.Frequency * 1000d;
        }

        private static void ValidateCardinality(int cardinality)
        {
            if (cardinality <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(cardinality),
                    cardinality,
                    "Deregistration attribution cardinality must be positive."
                );
            }
        }

        private readonly struct AttributionMessage : IUntargetedMessage { }

        private sealed class AttributionCounter
        {
            public int Count { get; private set; }

            public void Increment(in AttributionMessage message)
            {
                Count++;
            }
        }

        private sealed class DeregistrationAttributionState : IDisposable
        {
            private readonly DeregistrationAttributionOperation _operation;
            private readonly int _cardinality;
            private readonly IDisposable _registryScope;
            private readonly MessageBus _bus;
            private readonly MessageHandler _handler;
            private readonly MessageBusRegistration[] _busRegistrations;
            private readonly MessageHandler.TypedHandler<AttributionMessage>.TypedHandlerDeregistrationState[] _handlerDeregistrations;
            private readonly MessageRegistrationHandle[] _tokenRegistrations;
            private readonly MessageHandler.FastHandler<AttributionMessage>[] _handlers;
            private readonly AttributionCounter _counter;
            private readonly MessageRegistrationToken _token;
            private int _directBusDeregistered;
            private int _directHandlerDeregistered;
            private bool _executed;
            private bool _disposed;

            public DeregistrationAttributionState(
                DeregistrationAttributionOperation operation,
                int cardinality,
                IDisposable registryScope
            )
            {
                _operation = operation;
                _cardinality = cardinality;
                _registryScope = registryScope;
                _bus = new MessageBus { DiagnosticsMode = false };
                _handler = new MessageHandler(new InstanceId(42001), _bus) { active = true };
                _counter = new AttributionCounter();

                if (operation == DeregistrationAttributionOperation.DirectBus)
                {
                    _busRegistrations = new MessageBusRegistration[cardinality];
                    for (int index = 0; index < cardinality; index++)
                    {
                        _busRegistrations[index] = _bus.RegisterUntargeted<AttributionMessage>(
                            _handler
                        );
                    }
                }
                else
                {
                    _handlers = new MessageHandler.FastHandler<AttributionMessage>[cardinality];
                    for (int index = 0; index < cardinality; index++)
                    {
                        int capturedIndex = index;
                        _handlers[index] = (in AttributionMessage message) =>
                        {
                            _ = capturedIndex;
                            _counter.Increment(ref message);
                        };
                    }

                    if (operation == DeregistrationAttributionOperation.DirectHandler)
                    {
                        _handlerDeregistrations =
                            new MessageHandler.TypedHandler<AttributionMessage>.TypedHandlerDeregistrationState[
                                cardinality
                            ];
                        for (int index = 0; index < cardinality; index++)
                        {
                            _handlerDeregistrations[index] =
                                _handler.RegisterUntargetedMessageHandler(
                                    _handlers[index],
                                    _handlers[index],
                                    messageBus: _bus
                                );
                        }
                    }
                    else
                    {
                        _token = MessageRegistrationToken.Create(_handler, _bus);
                        _token.DiagnosticMode = false;
                        _tokenRegistrations = new MessageRegistrationHandle[cardinality];
                        for (int index = 0; index < cardinality; index++)
                        {
                            _tokenRegistrations[index] =
                                _token.RegisterUntargeted<AttributionMessage>(_handlers[index]);
                        }
                        _token.Enable();
                    }
                }
            }

            public DeregistrationAttributionOperation Operation => _operation;

            public void VerifyPrepared()
            {
                Assert.AreEqual(
                    1,
                    _bus.RegisteredUntargeted,
                    $"{_operation}/{_cardinality}: preparation must produce one refcounted bus entry."
                );
                Assert.AreEqual(
                    _operation == DeregistrationAttributionOperation.DirectBus ? 0 : _cardinality,
                    CountHandlerRegistrations(),
                    $"{_operation}/{_cardinality}: preparation produced the wrong handler fan-out."
                );
            }

            public void Execute()
            {
                if (_executed)
                {
                    throw new InvalidOperationException(
                        $"{_operation}/{_cardinality}: the destructive operation ran twice."
                    );
                }

                _executed = true;
                switch (_operation)
                {
                    case DeregistrationAttributionOperation.DirectBus:
                        for (
                            ;
                            _directBusDeregistered < _busRegistrations.Length;
                            _directBusDeregistered++
                        )
                        {
                            MessageBusRegistration registration = _busRegistrations[
                                _directBusDeregistered
                            ];
                            _bus.Deregister<AttributionMessage>(in registration);
                        }
                        break;
                    case DeregistrationAttributionOperation.DirectHandler:
                        for (
                            ;
                            _directHandlerDeregistered < _handlerDeregistrations.Length;
                            _directHandlerDeregistered++
                        )
                        {
                            _handlerDeregistrations[_directHandlerDeregistered].Deregister();
                        }
                        break;
                    case DeregistrationAttributionOperation.TokenRemove:
                        for (int index = 0; index < _tokenRegistrations.Length; index++)
                        {
                            _token.RemoveRegistration(_tokenRegistrations[index]);
                        }
                        break;
                    case DeregistrationAttributionOperation.TokenDisable:
                        _token.Disable();
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(_operation), _operation, null);
                }
            }

            public DeregistrationAttributionObservation Verify()
            {
                Assert.IsTrue(
                    _executed,
                    $"{_operation}/{_cardinality}: verification requires one completed operation."
                );
                Assert.AreEqual(
                    0,
                    _bus.RegisteredUntargeted,
                    $"{_operation}/{_cardinality}: teardown left a bus registration live."
                );
                Assert.AreEqual(
                    0,
                    CountHandlerRegistrations(),
                    $"{_operation}/{_cardinality}: teardown left a handler registration live."
                );

                AttributionMessage message = default;
                _bus.UntargetedBroadcast(ref message);
                Assert.AreEqual(
                    0,
                    _counter.Count,
                    $"{_operation}/{_cardinality}: teardown still dispatched a handler."
                );
                return Observe();
            }

            public DeregistrationAttributionObservation Observe()
            {
                return new DeregistrationAttributionObservation(
                    _operation,
                    _cardinality,
                    _bus.RegisteredUntargeted,
                    CountHandlerRegistrations(),
                    _counter.Count
                );
            }

            private int CountHandlerRegistrations()
            {
                return _handler.CountFlatHandlers<AttributionMessage>(
                    _bus,
                    priority: 0,
                    fastIndex: TypedSlotIndex.UntargetedHandleFast,
                    defaultIndex: TypedSlotIndex.UntargetedHandleDefault
                );
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                try
                {
                    if (_operation == DeregistrationAttributionOperation.DirectBus)
                    {
                        while (_directBusDeregistered < _busRegistrations.Length)
                        {
                            MessageBusRegistration registration = _busRegistrations[
                                _directBusDeregistered++
                            ];
                            _bus.Deregister<AttributionMessage>(in registration);
                        }
                    }
                    else if (_operation == DeregistrationAttributionOperation.DirectHandler)
                    {
                        while (_directHandlerDeregistered < _handlerDeregistrations.Length)
                        {
                            _handlerDeregistrations[_directHandlerDeregistered++].Deregister();
                        }
                    }
                    else
                    {
                        _token.Dispose();
                    }
                }
                finally
                {
                    _registryScope.Dispose();
                }
            }
        }
    }

    internal readonly struct DeregistrationAttributionPalindromeSample
    {
        public DeregistrationAttributionPalindromeSample(
            double handlerA,
            double busA,
            double busB,
            double handlerB,
            int trial,
            bool prepareForward
        )
        {
            HandlerA = handlerA;
            BusA = busA;
            BusB = busB;
            HandlerB = handlerB;
            Trial = trial;
            PrepareForward = prepareForward;
        }

        public double HandlerA { get; }

        public double BusA { get; }

        public double BusB { get; }

        public double HandlerB { get; }

        public int Trial { get; }

        public bool PrepareForward { get; }

        public double TotalMs => HandlerA + BusA + BusB + HandlerB;
    }

    internal readonly struct DeregistrationAttributionPalindromeDiagnostic
    {
        public DeregistrationAttributionPalindromeDiagnostic(
            DeregistrationAttributionPalindromeSample sample,
            int timingTrials,
            bool jointTrialSelection,
            bool sameTrialArms,
            bool preparationDirectionAlternated,
            string trialSequence
        )
        {
            Sample = sample;
            TimingTrials = timingTrials;
            JointTrialSelection = jointTrialSelection;
            SameTrialArms = sameTrialArms;
            PreparationDirectionAlternated = preparationDirectionAlternated;
            TrialSequence = trialSequence;
        }

        public DeregistrationAttributionPalindromeSample Sample { get; }

        public int TimingTrials { get; }

        public bool JointTrialSelection { get; }

        public bool SameTrialArms { get; }

        public bool PreparationDirectionAlternated { get; }

        public string TrialSequence { get; }

        public double HandlerA => Sample.HandlerA;

        public double BusA => Sample.BusA;

        public double BusB => Sample.BusB;

        public double HandlerB => Sample.HandlerB;

        public double HandlerExcessA => HandlerA - BusA;

        public double HandlerExcessB => HandlerB - BusB;

        public double CenteredHandlerExcess => SafePositiveMean(HandlerExcessA, HandlerExcessB);

        public double HandlerDriftPercent => RelativeDriftPercent(HandlerA, HandlerB);

        public double BusDriftPercent => RelativeDriftPercent(BusA, BusB);

        public double HandlerExcessSpreadPercent =>
            HasFinitePositiveExcesses
                ? Math.Abs(HandlerExcessB - HandlerExcessA) / CenteredHandlerExcess * 100d
                : double.PositiveInfinity;

        public bool HandlerDriftWithinThreshold =>
            WithinSymmetricThreshold(
                HandlerA,
                HandlerB,
                DeregistrationAttributionBenchmarks.MaxSamePathDriftPercent
            );

        public bool BusDriftWithinThreshold =>
            WithinSymmetricThreshold(
                BusA,
                BusB,
                DeregistrationAttributionBenchmarks.MaxSamePathDriftPercent
            );

        public bool HandlerExcessSpreadWithinThreshold =>
            HasFinitePositiveExcesses
            && Math.Abs(HandlerExcessB - HandlerExcessA)
                <= CenteredHandlerExcess
                    * (DeregistrationAttributionBenchmarks.MaxHandlerExcessSpreadPercent / 100d);

        public bool HasFinitePositiveDurations =>
            IsFinitePositive(HandlerA)
            && IsFinitePositive(BusA)
            && IsFinitePositive(BusB)
            && IsFinitePositive(HandlerB);

        public bool HasFinitePositiveExcesses =>
            IsFinitePositive(HandlerExcessA)
            && IsFinitePositive(HandlerExcessB)
            && IsFinitePositive(CenteredHandlerExcess);

        public bool Interpretable =>
            HasFinitePositiveDurations
            && HasFinitePositiveExcesses
            && IsFiniteNonNegative(HandlerDriftPercent)
            && IsFiniteNonNegative(BusDriftPercent)
            && IsFiniteNonNegative(HandlerExcessSpreadPercent)
            && HandlerDriftWithinThreshold
            && BusDriftWithinThreshold
            && HandlerExcessSpreadWithinThreshold;

        public string ToStructuredLog()
        {
            return "DXM_DEREGISTRATION_ATTRIBUTION_PALINDROME "
                + $"handlerA_ms={Format(HandlerA)} busA_ms={Format(BusA)} "
                + $"busB_ms={Format(BusB)} handlerB_ms={Format(HandlerB)} "
                + $"handlerExcessA_ms={Format(HandlerExcessA)} "
                + $"handlerExcessB_ms={Format(HandlerExcessB)} "
                + $"centeredHandlerExcess_ms={Format(CenteredHandlerExcess)} "
                + $"handlerDriftPercent={Format(HandlerDriftPercent)} "
                + $"busDriftPercent={Format(BusDriftPercent)} "
                + $"handlerExcessSpreadPercent={Format(HandlerExcessSpreadPercent)} "
                + $"handlerDriftWithinThreshold={Format(HandlerDriftWithinThreshold)} "
                + $"busDriftWithinThreshold={Format(BusDriftWithinThreshold)} "
                + $"handlerExcessSpreadWithinThreshold={Format(HandlerExcessSpreadWithinThreshold)} "
                + $"maxSamePathDriftPercent={Format(DeregistrationAttributionBenchmarks.MaxSamePathDriftPercent)} "
                + $"maxHandlerExcessSpreadPercent={Format(DeregistrationAttributionBenchmarks.MaxHandlerExcessSpreadPercent)} "
                + $"finitePositiveDurations={Format(HasFinitePositiveDurations)} "
                + $"finitePositiveExcesses={Format(HasFinitePositiveExcesses)} "
                + $"handlerFirstPairTotal_ms={Format(HandlerA + BusA)} "
                + $"busFirstPairTotal_ms={Format(BusB + HandlerB)} "
                + $"palindromeTotal_ms={Format(Sample.TotalMs)} "
                + $"selectedTrial={Sample.Trial} prepareForward={Format(Sample.PrepareForward)} "
                + $"jointTrialSelection={Format(JointTrialSelection)} "
                + $"sameTrialArms={Format(SameTrialArms)} "
                + $"preparationDirectionAlternated={Format(PreparationDirectionAlternated)} "
                + $"timingTrials={TimingTrials} "
                + $"trialSequence={TrialSequence} "
                + $"diagnosticOnly=true acceptanceEvidence=false "
                + $"candidateCompared=false interpretable={Format(Interpretable)}";
        }

        private static string Format(double value) =>
            value.ToString("R", CultureInfo.InvariantCulture);

        private static string Format(bool value) => value ? "true" : "false";

        private static double RelativeDriftPercent(double first, double second)
        {
            if (!IsFinitePositive(first) || !IsFinitePositive(second))
            {
                return double.PositiveInfinity;
            }

            double minimum = Math.Min(first, second);
            double maximum = Math.Max(first, second);
            return (maximum - minimum) / minimum * 100d;
        }

        private static bool WithinSymmetricThreshold(
            double first,
            double second,
            double thresholdPercent
        )
        {
            if (!IsFinitePositive(first) || !IsFinitePositive(second))
            {
                return false;
            }

            double minimum = Math.Min(first, second);
            double maximum = Math.Max(first, second);
            return maximum - minimum <= minimum * (thresholdPercent / 100d);
        }

        private static double SafePositiveMean(double first, double second)
        {
            if (!IsFinitePositive(first) || !IsFinitePositive(second))
            {
                return double.PositiveInfinity;
            }

            double minimum = Math.Min(first, second);
            return minimum + (Math.Max(first, second) - minimum) / 2d;
        }

        private static bool IsFinitePositive(double value) =>
            value > 0d && !double.IsInfinity(value) && !double.IsNaN(value);

        private static bool IsFiniteNonNegative(double value) =>
            value >= 0d && !double.IsInfinity(value) && !double.IsNaN(value);
    }

    public readonly struct DeregistrationAttributionObservation
    {
        public DeregistrationAttributionObservation(
            DeregistrationAttributionOperation operation,
            int cardinality,
            int busRegistrations,
            int handlerRegistrations,
            int handlerInvocations
        )
        {
            Operation = operation;
            Cardinality = cardinality;
            BusRegistrations = busRegistrations;
            HandlerRegistrations = handlerRegistrations;
            HandlerInvocations = handlerInvocations;
        }

        public DeregistrationAttributionOperation Operation { get; }

        public int Cardinality { get; }

        public int BusRegistrations { get; }

        public int HandlerRegistrations { get; }

        public int HandlerInvocations { get; }
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
