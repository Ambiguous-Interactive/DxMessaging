#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Benchmarks
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;
    using UnityEngine;

    public enum StickyBenchmarkOperation
    {
        OrdinaryEmit,
        StickyEmit,
        LatestPresent,
        LatestAbsent,
        LatestMissingType,
    }

    public enum StickyBenchmarkCallback
    {
        None,
        Action,
        Fast,
    }

    public readonly struct StickyBenchmarkCase
    {
        public StickyBenchmarkCase(
            StickyBenchmarkOperation operation,
            bool referencePayload,
            StickyBenchmarkCallback callback
        )
        {
            Operation = operation;
            ReferencePayload = referencePayload;
            Callback = callback;
        }

        public StickyBenchmarkOperation Operation { get; }
        public bool ReferencePayload { get; }
        public StickyBenchmarkCallback Callback { get; }
        public string Key =>
            $"Sticky_Warm_{Operation}_{(ReferencePayload ? "Class" : "Struct")}_{Callback}";
    }

    /// <summary>
    /// Measures warmed public sticky operations. Ordinary emission controls retain the same
    /// prepared value and run the same callbacks as sticky emission. First publication,
    /// registration and replay are outside these steady-state rows.
    /// </summary>
    public sealed class StickyMessageBenchmarks
    {
        internal const int PayloadValue = 7;

        [Test, Performance, Category("PerfBench")]
        [TestCaseSource(nameof(Cases))]
        public void WarmedStickyOperation(StickyBenchmarkCase benchmarkCase)
        {
            DispatchBenchmarkResult result = RunScenario(benchmarkCase);
            Debug.Log(result.ToStructuredLog());
            TestContext.Out.WriteLine(result.ToCsvRow());
        }

        public static IEnumerable<TestCaseData> Cases()
        {
            foreach (bool referencePayload in new[] { false, true })
            {
                foreach (
                    StickyBenchmarkOperation operation in new[]
                    {
                        StickyBenchmarkOperation.OrdinaryEmit,
                        StickyBenchmarkOperation.StickyEmit,
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
                        StickyBenchmarkCase benchmarkCase = new(
                            operation,
                            referencePayload,
                            callback
                        );
                        yield return new TestCaseData(benchmarkCase).SetName(benchmarkCase.Key);
                    }
                }
                foreach (
                    StickyBenchmarkOperation operation in new[]
                    {
                        StickyBenchmarkOperation.LatestPresent,
                        StickyBenchmarkOperation.LatestAbsent,
                        StickyBenchmarkOperation.LatestMissingType,
                    }
                )
                {
                    StickyBenchmarkCase benchmarkCase = new(
                        operation,
                        referencePayload,
                        StickyBenchmarkCallback.None
                    );
                    yield return new TestCaseData(benchmarkCase).SetName(benchmarkCase.Key);
                }
            }
        }

        public static DispatchBenchmarkResult RunScenario(StickyBenchmarkCase benchmarkCase)
        {
            return benchmarkCase.ReferencePayload
                ? Measure(benchmarkCase, new ReferenceMessage(PayloadValue))
                : Measure(benchmarkCase, new ValueMessage(PayloadValue));
        }

        private static DispatchBenchmarkResult Measure<TMessage>(
            StickyBenchmarkCase benchmarkCase,
            TMessage message
        )
            where TMessage : IValue
        {
            using IDisposable registry = BenchmarkProtocol.IsolateIdleSweepRegistry();
            MessageBus bus = new() { DiagnosticsMode = false };
            using Workload<TMessage> state = new(benchmarkCase, message, bus);
            BenchmarkMeasurement measurement = BenchmarkProtocol.Measure(
                () => state.RunMany(BenchmarkProtocol.WarmupEmits),
                () =>
                {
                    state.RunMany(BenchmarkProtocol.BatchSize);
                    return BenchmarkProtocol.BatchSize;
                }
            );
            state.Verify(BenchmarkProtocol.WarmupEmits + measurement.TotalEmittedOperations);
            return DispatchBenchmarkResult.ForEmitScenario(
                benchmarkCase.Key,
                runIndex: -1,
                measurement.OperationsPerSecond,
                measurement.GcAllocations,
                measurement.GcAllocatedBytes,
                measurement.ElapsedSeconds * 1000d
            );
        }

        internal static StickyWorkloadObservation RunForContract(StickyBenchmarkCase benchmarkCase)
        {
            return benchmarkCase.ReferencePayload
                ? RunForContract(benchmarkCase, new ReferenceMessage(PayloadValue))
                : RunForContract(benchmarkCase, new ValueMessage(PayloadValue));
        }

        private static StickyWorkloadObservation RunForContract<TMessage>(
            StickyBenchmarkCase benchmarkCase,
            TMessage message
        )
            where TMessage : IValue
        {
            using IDisposable registry = BenchmarkProtocol.IsolateIdleSweepRegistry();
            MessageBus bus = new() { DiagnosticsMode = false };
            using LeakWatcher leaks = new(bus, label: benchmarkCase.Key);
            using Workload<TMessage> state = new(benchmarkCase, message, bus);
            state.RunMany(3);
            state.RunMany(7);
            state.Verify(10);
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

        private readonly struct OtherMessage : IUntargetedMessage<OtherMessage> { }

        private sealed class Workload<TMessage> : IDisposable
            where TMessage : IValue
        {
            private readonly StickyBenchmarkCase _case;
            private readonly MessageBus _bus;
            private readonly MessageRegistrationToken _token;
            private TMessage _message;
            private TMessage _observed;
            private long _calls;
            private long _sum;
            private long _found;

            internal Workload(StickyBenchmarkCase benchmarkCase, TMessage message, MessageBus bus)
            {
                _case = benchmarkCase;
                _message = message;
                _bus = bus;
                MessageHandler handler = new(new InstanceId(74901), _bus) { active = true };
                _token = MessageRegistrationToken.Create(handler, _bus);
                _token.DiagnosticMode = false;
                if (_case.Operation == StickyBenchmarkOperation.LatestMissingType)
                {
                    OtherMessage other = default;
                    _bus.EmitSticky(ref other);
                }
                else if (_case.Operation != StickyBenchmarkOperation.LatestAbsent)
                {
                    // Both publication arms begin with the same retained state.
                    _bus.EmitSticky(ref _message);
                }
                if (_case.Callback == StickyBenchmarkCallback.Action)
                {
                    _token.RegisterUntargeted<TMessage>(OnAction);
                }
                else if (_case.Callback == StickyBenchmarkCallback.Fast)
                {
                    _token.RegisterUntargeted<TMessage>(OnFast);
                }
                _token.Enable();
            }

            private void OnAction(TMessage message)
            {
                ++_calls;
                _sum += message.Value;
            }

            private void OnFast(in TMessage message)
            {
                ++_calls;
                _sum += message.Value;
            }

            internal void RunMany(int count)
            {
                switch (_case.Operation)
                {
                    case StickyBenchmarkOperation.OrdinaryEmit:
                        for (int index = 0; index < count; ++index)
                        {
                            _bus.UntargetedBroadcast(ref _message);
                        }
                        break;
                    case StickyBenchmarkOperation.StickyEmit:
                        for (int index = 0; index < count; ++index)
                        {
                            _bus.EmitSticky(ref _message);
                        }
                        break;
                    case StickyBenchmarkOperation.LatestPresent:
                    case StickyBenchmarkOperation.LatestAbsent:
                    case StickyBenchmarkOperation.LatestMissingType:
                        for (int index = 0; index < count; ++index)
                        {
                            if (_bus.TryGetLatest(out _observed))
                            {
                                ++_found;
                            }
                        }
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(_case.Operation));
                }
            }

            internal void Verify(long operations)
            {
                string context = _case.Key;
                bool lookup =
                    _case.Operation == StickyBenchmarkOperation.LatestPresent
                    || _case.Operation == StickyBenchmarkOperation.LatestAbsent
                    || _case.Operation == StickyBenchmarkOperation.LatestMissingType;
                long expectedCalls =
                    !lookup && _case.Callback != StickyBenchmarkCallback.None ? operations : 0;
                Assert.That(_calls, Is.EqualTo(expectedCalls), context);
                Assert.That(_sum, Is.EqualTo(expectedCalls * PayloadValue), context);
                Assert.That(
                    _found,
                    Is.EqualTo(
                        _case.Operation == StickyBenchmarkOperation.LatestPresent ? operations : 0
                    ),
                    context
                );
                bool present = _bus.TryGetLatest(out TMessage latest);
                Assert.That(
                    present,
                    Is.EqualTo(
                        _case.Operation != StickyBenchmarkOperation.LatestAbsent
                            && _case.Operation != StickyBenchmarkOperation.LatestMissingType
                    ),
                    context
                );
                Assert.That(
                    _bus.TryGetLatest<OtherMessage>(out _),
                    Is.EqualTo(_case.Operation == StickyBenchmarkOperation.LatestMissingType),
                    context
                );
                if (present)
                {
                    Assert.That(latest.Value, Is.EqualTo(PayloadValue), context);
                    Assert.That(latest, Is.EqualTo(_message), context);
                }
                if (lookup)
                {
                    Assert.That(
                        _observed,
                        Is.EqualTo(present ? _message : default(TMessage)),
                        context
                    );
                }
                Assert.That(_bus.DiagnosticsMode, Is.False, context);
                Assert.That(_token.DiagnosticMode, Is.False, context);
                Assert.That(
                    BenchmarkProtocol.CountRegistrations(_token),
                    Is.EqualTo(_case.Callback == StickyBenchmarkCallback.None ? 0 : 1),
                    context
                );
            }

            internal StickyWorkloadObservation Observe() =>
                new(_calls, _sum, _found, _bus.RegisteredUntargeted);

            public void Dispose()
            {
                try
                {
                    _token.Dispose();
                }
                finally
                {
                    _bus.ClearLatest<TMessage>();
                    _bus.ClearLatest<OtherMessage>();
                }
            }
        }
    }

    internal readonly struct StickyWorkloadObservation
    {
        internal StickyWorkloadObservation(long calls, long sum, long found, int registrations)
        {
            Calls = calls;
            Sum = sum;
            Found = found;
            Registrations = registrations;
        }

        internal long Calls { get; }
        internal long Sum { get; }
        internal long Found { get; }
        internal int Registrations { get; }
    }
}
#endif
