#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Benchmarks
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using DxMessaging.Core;
    using DxMessaging.Core.Internal;
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
    public sealed class DeregistrationAttributionBenchmarks
    {
        internal const int Cardinality = 131_072;
        private const int TimingTrials = 7;

        [Test, Performance, Category("PerfBench")]
        [TestCaseSource(nameof(BenchmarkCases))]
        public void DeregistrationAttributionBenchmark(DeregistrationAttributionOperation operation)
        {
            DispatchBenchmarkResult result = RunScenario(operation);
            Debug.Log(result.ToStructuredLog());
            TestContext.Out.WriteLine(result.ToCsvRow());
        }

        internal static DeregistrationAttributionObservation ExecuteOnceForContract(
            DeregistrationAttributionOperation operation,
            int cardinality
        )
        {
            ValidateCardinality(cardinality);
            using DeregistrationAttributionState state = new(operation, cardinality);
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
                using DeregistrationAttributionState state = new(operation, Cardinality);
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

        private static IEnumerable<TestCaseData> BenchmarkCases()
        {
            foreach (
                DeregistrationAttributionOperation operation in Enum.GetValues(
                    typeof(DeregistrationAttributionOperation)
                )
            )
            {
                yield return new TestCaseData(operation).SetName(
                    $"DeregistrationAttribution_{operation}_{Cardinality}"
                );
            }
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

            public void Increment(ref AttributionMessage message)
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
                int cardinality
            )
            {
                _operation = operation;
                _cardinality = cardinality;
                _registryScope = MessageBus.IsolateIdleSweepRegistryForBenchmark();
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
                        _handlers[index] = (ref AttributionMessage message) =>
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
