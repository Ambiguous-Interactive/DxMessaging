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

    /// <summary>
    /// Alternates two message types on one bus with identical retained state in both arms.
    /// Publication count includes both types; there are no callbacks.
    /// </summary>
    public sealed class StickyAlternatingTypeBenchmarks
    {
        [Test, Performance, Category("PerfBench")]
        [TestCase(false, false, TestName = "Sticky_Alternating_OrdinaryEmit_Struct")]
        [TestCase(false, true, TestName = "Sticky_Alternating_OrdinaryEmit_Class")]
        [TestCase(true, false, TestName = "Sticky_Alternating_StickyEmit_Struct")]
        [TestCase(true, true, TestName = "Sticky_Alternating_StickyEmit_Class")]
        public void AlternatingTypePublication(bool sticky, bool referencePayload)
        {
            using IDisposable registry = BenchmarkProtocol.IsolateIdleSweepRegistry();
            using IWorkload state = Create(sticky, referencePayload, false);
            BenchmarkMeasurement measurement = BenchmarkProtocol.Measure(
                () => state.RunMany(BenchmarkProtocol.WarmupEmits),
                () =>
                {
                    state.RunMany(BenchmarkProtocol.BatchSize);
                    return BenchmarkProtocol.BatchSize;
                }
            );
            state.Verify(BenchmarkProtocol.WarmupEmits + measurement.TotalEmittedOperations);
            DispatchBenchmarkResult result = DispatchBenchmarkResult.ForEmitScenario(
                Key(sticky, referencePayload),
                -1,
                measurement.OperationsPerSecond,
                measurement.GcAllocations,
                measurement.GcAllocatedBytes,
                measurement.ElapsedSeconds * 1000d
            );
            Debug.Log(result.ToStructuredLog());
            TestContext.Out.WriteLine(result.ToCsvRow());
        }

        internal static string Key(bool sticky, bool referencePayload) =>
            $"Sticky_Alternating_{(sticky ? "StickyEmit" : "OrdinaryEmit")}_{(referencePayload ? "Class" : "Struct")}";

        internal static long RunContract(bool sticky, bool referencePayload, int first, int second)
        {
            using IDisposable registry = BenchmarkProtocol.IsolateIdleSweepRegistry();
            using IWorkload state = Create(sticky, referencePayload, true);
            state.RunMany(first);
            state.Verify(first);
            state.RunMany(second);
            state.Verify(first + second);
            return state.Emissions;
        }

        private static IWorkload Create(bool sticky, bool referencePayload, bool observe) =>
            referencePayload
                ? new Workload<ReferenceMessage<First>, ReferenceMessage<Second>>(
                    sticky,
                    new ReferenceMessage<First>(7),
                    new ReferenceMessage<Second>(11),
                    observe
                )
                : new Workload<ValueMessage<First>, ValueMessage<Second>>(
                    sticky,
                    new ValueMessage<First>(7),
                    new ValueMessage<Second>(11),
                    observe
                );

        private interface IWorkload : IDisposable
        {
            long Emissions { get; }
            void RunMany(int count);
            void Verify(long expected);
        }

        private sealed class Workload<TFirst, TSecond> : IWorkload
            where TFirst : IUntargetedMessage
            where TSecond : IUntargetedMessage
        {
            private readonly bool _sticky;
            private readonly MessageBus _bus = new() { DiagnosticsMode = false };
            private readonly long _initialEmissionId;
            private readonly List<Type> _observed;
            private readonly MessageRegistrationToken _token;
            private readonly LeakWatcher _leaks;
            private TFirst _first;
            private TSecond _second;
            private bool _nextIsFirst = true;

            internal Workload(bool sticky, TFirst first, TSecond second, bool observe)
            {
                _sticky = sticky;
                _first = first;
                _second = second;
                _bus.EmitSticky(ref _first);
                _bus.EmitSticky(ref _second);
                _initialEmissionId = _bus.EmissionId;
                if (observe)
                {
                    _leaks = new LeakWatcher(_bus, label: "Alternating publication contract");
                    _observed = new List<Type>();
                    MessageHandler handler = new(new InstanceId(74931), _bus) { active = true };
                    _token = MessageRegistrationToken.Create(handler, _bus);
                    _token.DiagnosticMode = false;
                    _token.RegisterUntargeted<TFirst>(_ => _observed.Add(typeof(TFirst)));
                    _token.RegisterUntargeted<TSecond>(_ => _observed.Add(typeof(TSecond)));
                    _token.Enable();
                }
            }

            public long Emissions => _bus.EmissionId - _initialEmissionId;

            public void RunMany(int count)
            {
                if (count < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(count));
                }
                if (_sticky)
                {
                    for (int index = 0; index < count; ++index)
                    {
                        if (_nextIsFirst)
                        {
                            _bus.EmitSticky(ref _first);
                        }
                        else
                        {
                            _bus.EmitSticky(ref _second);
                        }
                        _nextIsFirst = !_nextIsFirst;
                    }
                }
                else
                {
                    for (int index = 0; index < count; ++index)
                    {
                        if (_nextIsFirst)
                        {
                            _bus.UntargetedBroadcast(ref _first);
                        }
                        else
                        {
                            _bus.UntargetedBroadcast(ref _second);
                        }
                        _nextIsFirst = !_nextIsFirst;
                    }
                }
            }

            public void Verify(long expected)
            {
                string context =
                    $"sticky={_sticky}, first={typeof(TFirst)}, second={typeof(TSecond)}, operations={expected}";
                Assert.That(_initialEmissionId, Is.EqualTo(2), context);
                Assert.That(Emissions, Is.EqualTo(expected), context);
                Assert.That(_nextIsFirst, Is.EqualTo((expected & 1) == 0), context);
                if (_observed != null)
                {
                    Assert.That(_observed.Count, Is.EqualTo(expected), context);
                    for (int index = 0; index < _observed.Count; ++index)
                    {
                        Assert.That(
                            _observed[index],
                            Is.SameAs((index & 1) == 0 ? typeof(TFirst) : typeof(TSecond)),
                            context
                        );
                    }
                }
                Assert.That(_bus.TryGetLatest(out TFirst first), Is.True, context);
                Assert.That(_bus.TryGetLatest(out TSecond second), Is.True, context);
                Assert.That(first, Is.EqualTo(_first), context);
                Assert.That(second, Is.EqualTo(_second), context);
                if (typeof(TFirst).IsClass)
                {
                    Assert.That(first, Is.SameAs(_first), context);
                    Assert.That(second, Is.SameAs(_second), context);
                }
                MessageBus other = new() { DiagnosticsMode = false };
                Assert.That(other.TryGetLatest(out TFirst _), Is.False, context);
                Assert.That(other.TryGetLatest(out TSecond _), Is.False, context);
                Assert.That(_bus.DiagnosticsMode, Is.False, context);
            }

            public void Dispose()
            {
                try
                {
                    _token?.Dispose();
                }
                finally
                {
                    try
                    {
                        _bus.ClearLatest<TFirst>();
                        _bus.ClearLatest<TSecond>();
                        Assert.That(
                            _bus.TryGetLatest(out TFirst _),
                            Is.False,
                            $"Clear must remove {typeof(TFirst)}."
                        );
                        Assert.That(
                            _bus.TryGetLatest(out TSecond _),
                            Is.False,
                            $"Clear must remove {typeof(TSecond)}."
                        );
                    }
                    finally
                    {
                        _first = default;
                        _second = default;
                        _leaks?.Dispose();
                    }
                }
            }
        }

        private readonly struct First { }

        private readonly struct Second { }

        private readonly struct ValueMessage<TMarker> : IUntargetedMessage<ValueMessage<TMarker>>
        {
            internal ValueMessage(int value) => Value = value;

            public int Value { get; }
        }

        private sealed class ReferenceMessage<TMarker>
            : IUntargetedMessage<ReferenceMessage<TMarker>>
        {
            internal ReferenceMessage(int value) => Value = value;

            public int Value { get; }
        }
    }

    [Category("PerfBench")]
    public sealed class StickyAlternatingTypeBenchmarkTests
    {
        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void AlternatingWorkloadPreservesEveryPublicationAcrossOddBatchBoundaries(
            bool sticky,
            bool referencePayload
        )
        {
            long emissions = StickyAlternatingTypeBenchmarks.RunContract(
                sticky,
                referencePayload,
                3,
                7
            );
            Assert.That(
                emissions,
                Is.EqualTo(10),
                $"sticky={sticky}, reference={referencePayload}"
            );
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void AlternatingWorkloadHandlesEmptyAndSingleOperationBatches(
            bool sticky,
            bool referencePayload
        )
        {
            long emissions = StickyAlternatingTypeBenchmarks.RunContract(
                sticky,
                referencePayload,
                0,
                1
            );
            Assert.That(emissions, Is.EqualTo(1), $"sticky={sticky}, reference={referencePayload}");
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void AlternatingWorkloadRejectsNegativeCountsWithoutPublishing(
            bool sticky,
            bool referencePayload
        )
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => StickyAlternatingTypeBenchmarks.RunContract(sticky, referencePayload, -1, 0),
                $"sticky={sticky}, reference={referencePayload}"
            );
        }
    }
}
#endif
