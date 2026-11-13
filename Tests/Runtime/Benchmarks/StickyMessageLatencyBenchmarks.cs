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

    public enum StickyLatencyOperation
    {
        FirstOrdinaryEmit,
        FirstStickyEmit,
        Register,
    }

    public readonly struct StickyLatencyCase
    {
        public StickyLatencyCase(
            StickyLatencyOperation operation,
            bool referencePayload,
            StickyBenchmarkCallback callback,
            bool hasLatest = false,
            bool replay = false
        )
        {
            Operation = operation;
            ReferencePayload = referencePayload;
            Callback = callback;
            HasLatest = hasLatest;
            Replay = replay;
        }

        public StickyLatencyOperation Operation { get; }
        public bool ReferencePayload { get; }
        public StickyBenchmarkCallback Callback { get; }
        public bool HasLatest { get; }
        public bool Replay { get; }
        public string Key =>
            Operation == StickyLatencyOperation.Register
                ? $"Sticky_Register_{(ReferencePayload ? "Class" : "Struct")}_{Callback}_{(HasLatest ? "Present" : "Absent")}_{(Replay ? "Replay" : "Ordinary")}"
                : $"Sticky_FirstBus_{Operation}_{(ReferencePayload ? "Class" : "Struct")}_{Callback}";
    }

    /// <summary>
    /// Separates warmed-code first publication on a fresh bus from marginal registration.
    /// Setup and teardown remain outside each single-shot sample. These are latency rows,
    /// with the cold protocol's allocation-probe overhead on profiler-bearing backends.
    /// </summary>
    public sealed class StickyMessageLatencyBenchmarks
    {
        internal const int Trials = 32;
        private const int PayloadValue = 7;

        [Test, Performance, Category("PerfBench")]
        [TestCaseSource(nameof(Cases))]
        public void StickySingleShotLatency(StickyLatencyCase benchmarkCase)
        {
            ColdLatencyMeasurement measurement = benchmarkCase.ReferencePayload
                ? Measure(benchmarkCase, new ReferenceMessage(PayloadValue))
                : Measure(benchmarkCase, new ValueMessage(PayloadValue));
            Assert.That(measurement.Trials, Is.EqualTo(Trials), benchmarkCase.Key);
            Assert.That(measurement.RawTrials.Count, Is.EqualTo(Trials), benchmarkCase.Key);
            foreach (ColdLatencyTrial trial in measurement.RawTrials)
            {
                TestContext.Out.WriteLine(
                    $"DXM_STICKY_LATENCY_TRIAL scenario={benchmarkCase.Key} trial={trial.Index} "
                        + $"wallClockMs={trial.WallClockMs.ToString("R", CultureInfo.InvariantCulture)} "
                        + $"gcAllocations={trial.GcAllocations} gcAllocatedBytes={trial.GcAllocatedBytes}"
                );
            }
            DispatchBenchmarkResult result =
                benchmarkCase.Operation == StickyLatencyOperation.Register
                    ? DispatchBenchmarkResult.ForRegistrationScenario(
                        benchmarkCase.Key,
                        -1,
                        measurement.MedianGcAllocations,
                        measurement.MedianGcAllocatedBytes,
                        measurement.MedianWallClockMs
                    )
                    : DispatchBenchmarkResult.ForColdLatencyScenario(
                        benchmarkCase.Key,
                        -1,
                        measurement.MedianGcAllocations,
                        measurement.MedianGcAllocatedBytes,
                        measurement.MedianWallClockMs
                    );
            Debug.Log(result.ToStructuredLog());
            TestContext.Out.WriteLine(result.ToCsvRow());
        }

        public static IEnumerable<TestCaseData> Cases()
        {
            foreach (bool referencePayload in new[] { false, true })
            {
                foreach (
                    StickyLatencyOperation operation in new[]
                    {
                        StickyLatencyOperation.FirstOrdinaryEmit,
                        StickyLatencyOperation.FirstStickyEmit,
                    }
                )
                {
                    foreach (
                        StickyBenchmarkCallback callback in new[]
                        {
                            StickyBenchmarkCallback.None,
                            StickyBenchmarkCallback.Action,
                            StickyBenchmarkCallback.Fast,
                        }
                    )
                    {
                        StickyLatencyCase benchmarkCase = new(
                            operation,
                            referencePayload,
                            callback
                        );
                        yield return new TestCaseData(benchmarkCase).SetName(benchmarkCase.Key);
                    }
                }
                foreach (
                    StickyBenchmarkCallback callback in new[]
                    {
                        StickyBenchmarkCallback.Action,
                        StickyBenchmarkCallback.Fast,
                    }
                )
                {
                    foreach (bool hasLatest in new[] { false, true })
                    {
                        foreach (bool replay in new[] { false, true })
                        {
                            StickyLatencyCase benchmarkCase = new(
                                StickyLatencyOperation.Register,
                                referencePayload,
                                callback,
                                hasLatest,
                                replay
                            );
                            yield return new TestCaseData(benchmarkCase).SetName(benchmarkCase.Key);
                        }
                    }
                }
            }
        }

        private static ColdLatencyMeasurement Measure<TMessage>(
            StickyLatencyCase benchmarkCase,
            TMessage message
        )
            where TMessage : IValue
        {
            using IDisposable registry = BenchmarkProtocol.IsolateIdleSweepRegistry();
            bool completed = false;
            Action<State<TMessage>> operation = state =>
            {
                state.Execute();
                completed = true;
            };
            using (
                State<TMessage> warmup = new(
                    benchmarkCase,
                    message,
                    new MessageBus { DiagnosticsMode = false }
                )
            )
            {
                operation(warmup);
                warmup.Verify();
            }
            AllocationProbe.SettleHeapForMeasurement();
            return BenchmarkProtocol.MeasureColdLatency(
                Trials,
                _ =>
                {
                    completed = false;
                    return new State<TMessage>(
                        benchmarkCase,
                        message,
                        new MessageBus { DiagnosticsMode = false }
                    );
                },
                operation,
                state =>
                {
                    try
                    {
                        if (completed)
                        {
                            state.Verify();
                        }
                    }
                    finally
                    {
                        state.Dispose();
                    }
                }
            );
        }

        internal static StickyLatencyObservation RunForContract(StickyLatencyCase benchmarkCase)
        {
            return benchmarkCase.ReferencePayload
                ? RunForContract(benchmarkCase, new ReferenceMessage(PayloadValue))
                : RunForContract(benchmarkCase, new ValueMessage(PayloadValue));
        }

        private static StickyLatencyObservation RunForContract<TMessage>(
            StickyLatencyCase benchmarkCase,
            TMessage message
        )
            where TMessage : IValue
        {
            using IDisposable registry = BenchmarkProtocol.IsolateIdleSweepRegistry();
            MessageBus bus = new() { DiagnosticsMode = false };
            using LeakWatcher leaks = new(bus, label: benchmarkCase.Key);
            using State<TMessage> state = new(benchmarkCase, message, bus);
            state.Execute();
            state.Verify();
            return state.Observe();
        }

        private interface IValue : IUntargetedMessage
        {
            int Value { get; }
        }

        private readonly struct ValueMessage : IValue, IUntargetedMessage<ValueMessage>
        {
            public ValueMessage(int value)
            {
                Value = value;
            }

            public int Value { get; }
        }

        private sealed class ReferenceMessage : IValue, IUntargetedMessage<ReferenceMessage>
        {
            public ReferenceMessage(int value)
            {
                Value = value;
            }

            public int Value { get; }
        }

        private sealed class State<TMessage> : IDisposable
            where TMessage : IValue
        {
            private readonly StickyLatencyCase _case;
            private readonly MessageBus _bus;
            private readonly MessageRegistrationToken _existing;
            private readonly MessageRegistrationToken _added;
            private readonly Action<TMessage> _action;
            private readonly MessageHandler.FastHandler<TMessage> _fast;
            private TMessage _message;
            private int _executions;
            private int _existingCalls;
            private int _newCalls;
            private long _sum;

            internal State(StickyLatencyCase benchmarkCase, TMessage message, MessageBus bus)
            {
                _case = benchmarkCase;
                _message = message;
                _bus = bus;
                MessageHandler existingHandler = new(new InstanceId(74920), bus) { active = true };
                MessageHandler addedHandler = new(new InstanceId(74921), bus) { active = true };
                _action = OnAction;
                _fast = OnFast;
                try
                {
                    _existing = MessageRegistrationToken.Create(existingHandler, bus);
                    _added = MessageRegistrationToken.Create(addedHandler, bus);
                    _existing.DiagnosticMode = false;
                    _added.DiagnosticMode = false;
                    if (_case.Operation == StickyLatencyOperation.Register)
                    {
                        if (_case.HasLatest)
                        {
                            bus.EmitSticky(ref _message);
                        }
                        _existing.RegisterUntargeted<TMessage>(OnExisting);
                        _existing.Enable();
                    }
                    else if (_case.Callback == StickyBenchmarkCallback.Action)
                    {
                        _added.RegisterUntargeted(_action);
                    }
                    else if (_case.Callback == StickyBenchmarkCallback.Fast)
                    {
                        _added.RegisterUntargeted(_fast);
                    }
                    _added.Enable();
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            private void OnExisting(TMessage message)
            {
                ++_existingCalls;
            }

            private void OnAction(TMessage message)
            {
                ++_newCalls;
                _sum += message.Value;
            }

            private void OnFast(in TMessage message)
            {
                ++_newCalls;
                _sum += message.Value;
            }

            internal void Execute()
            {
                switch (_case.Operation)
                {
                    case StickyLatencyOperation.FirstOrdinaryEmit:
                        _bus.UntargetedBroadcast(ref _message);
                        break;
                    case StickyLatencyOperation.FirstStickyEmit:
                        _bus.EmitSticky(ref _message);
                        break;
                    case StickyLatencyOperation.Register:
                        if (_case.Callback == StickyBenchmarkCallback.Action)
                        {
                            _added.RegisterUntargeted(_action, _case.Replay);
                        }
                        else
                        {
                            _added.RegisterUntargeted(_fast, _case.Replay);
                        }
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(_case.Operation));
                }
                ++_executions;
            }

            internal void Verify()
            {
                string context = _case.Key;
                bool registration = _case.Operation == StickyLatencyOperation.Register;
                int expectedCalls = registration
                    ? (_case.HasLatest && _case.Replay ? 1 : 0)
                    : (_case.Callback == StickyBenchmarkCallback.None ? 0 : 1);
                Assert.That(_executions, Is.EqualTo(1), context);
                Assert.That(_existingCalls, Is.Zero, context);
                Assert.That(_newCalls, Is.EqualTo(expectedCalls), context);
                Assert.That(_sum, Is.EqualTo(expectedCalls * PayloadValue), context);
                Assert.That(
                    BenchmarkProtocol.CountRegistrations(_existing),
                    Is.EqualTo(registration ? 1 : 0),
                    context
                );
                Assert.That(
                    BenchmarkProtocol.CountRegistrations(_added),
                    Is.EqualTo(
                        registration || _case.Callback != StickyBenchmarkCallback.None ? 1 : 0
                    ),
                    context
                );
                bool present = _bus.TryGetLatest(out TMessage latest);
                Assert.That(
                    present,
                    Is.EqualTo(
                        registration
                            ? _case.HasLatest
                            : _case.Operation == StickyLatencyOperation.FirstStickyEmit
                    ),
                    context
                );
                if (present)
                {
                    Assert.That(latest, Is.EqualTo(_message), context);
                    Assert.That(latest.Value, Is.EqualTo(PayloadValue), context);
                }
                Assert.That(_bus.DiagnosticsMode, Is.False, context);
                Assert.That(_existing.DiagnosticMode, Is.False, context);
                Assert.That(_added.DiagnosticMode, Is.False, context);
            }

            internal StickyLatencyObservation Observe() => new(_newCalls, _existingCalls, _sum);

            public void Dispose()
            {
                try
                {
                    _added?.Dispose();
                }
                finally
                {
                    try
                    {
                        _existing?.Dispose();
                    }
                    finally
                    {
                        _bus.ClearLatest<TMessage>();
                    }
                }
            }
        }
    }

    internal readonly struct StickyLatencyObservation
    {
        internal StickyLatencyObservation(int addedCalls, int existingCalls, long sum)
        {
            AddedCalls = addedCalls;
            ExistingCalls = existingCalls;
            Sum = sum;
        }

        internal int AddedCalls { get; }
        internal int ExistingCalls { get; }
        internal long Sum { get; }
    }
}
#endif
