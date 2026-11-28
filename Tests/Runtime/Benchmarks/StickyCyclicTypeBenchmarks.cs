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

    /// <summary>Cycles distinct warmed types through preallocated interface wrappers.</summary>
    /// <remarks>Wrapper dispatch, rotation and operation selection are part of each measured operation.</remarks>
    public sealed class StickyCyclicTypeBenchmarks
    {
        [Test, Performance, Category("PerfBench")]
        [TestCase(false, false, 3, TestName = "Sticky_Cycle3_OrdinaryEmit_Struct")]
        [TestCase(false, true, 3, TestName = "Sticky_Cycle3_OrdinaryEmit_Class")]
        [TestCase(true, false, 3, TestName = "Sticky_Cycle3_StickyEmit_Struct")]
        [TestCase(true, true, 3, TestName = "Sticky_Cycle3_StickyEmit_Class")]
        [TestCase(false, false, 16, TestName = "Sticky_Cycle16_OrdinaryEmit_Struct")]
        [TestCase(false, true, 16, TestName = "Sticky_Cycle16_OrdinaryEmit_Class")]
        [TestCase(true, false, 16, TestName = "Sticky_Cycle16_StickyEmit_Struct")]
        [TestCase(true, true, 16, TestName = "Sticky_Cycle16_StickyEmit_Class")]
        public void CyclicTypePublication(bool sticky, bool referencePayload, int cardinality)
        {
            using IDisposable registry = BenchmarkProtocol.IsolateIdleSweepRegistry();
            using Workload state = Create(sticky, referencePayload, cardinality, false);
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
                Key(sticky, referencePayload, cardinality),
                -1,
                measurement.OperationsPerSecond,
                measurement.GcAllocations,
                measurement.GcAllocatedBytes,
                measurement.ElapsedSeconds * 1000d
            );
            Debug.Log(result.ToStructuredLog());
            TestContext.Out.WriteLine(result.ToCsvRow());
        }

        internal static string Key(bool sticky, bool referencePayload, int cardinality) =>
            $"Sticky_Cycle{cardinality}_{(sticky ? "StickyEmit" : "OrdinaryEmit")}_{(referencePayload ? "Class" : "Struct")}";

        internal static long RunContract(bool sticky, bool referencePayload, int cardinality, int first, int second)
        {
            using IDisposable registry = BenchmarkProtocol.IsolateIdleSweepRegistry();
            using Workload state = Create(sticky, referencePayload, cardinality, true);
            state.RunMany(first);
            state.Verify(first);
            state.RunMany(second);
            state.Verify((long)first + second);
            return state.Emissions;
        }

        private static Workload Create(bool sticky, bool referencePayload, int cardinality, bool observe)
        {
            if (cardinality != 3 && cardinality != 16)
            {
                throw new ArgumentOutOfRangeException(nameof(cardinality));
            }
            return new Workload(sticky, referencePayload, cardinality, observe);
        }

        private interface IEntry
        {
            Type MessageType { get; }
            void Seed();
            void Emit(bool sticky);
            void Observe(MessageRegistrationToken token, List<Type> observed);
            void VerifyRetained();
            void VerifyAbsent(MessageBus bus);
            void Clear();
            void DropMessage();
        }

        private sealed class Entry<TMessage> : IEntry
            where TMessage : IUntargetedMessage
        {
            private readonly MessageBus _bus;
            private TMessage _message;

            internal Entry(MessageBus bus, TMessage message)
            {
                _bus = bus;
                _message = message;
            }

            public Type MessageType => typeof(TMessage);

            public void Seed() => _bus.EmitSticky(ref _message);

            public void Emit(bool sticky)
            {
                if (sticky)
                {
                    _bus.EmitSticky(ref _message);
                }
                else
                {
                    _bus.UntargetedBroadcast(ref _message);
                }
            }

            public void Observe(MessageRegistrationToken token, List<Type> observed)
            {
                token.RegisterUntargeted<TMessage>(message =>
                {
                    Assert.That(message, Is.EqualTo(_message), "Deliver the offered cyclic payload.");
                    observed.Add(typeof(TMessage));
                });
            }

            public void VerifyRetained()
            {
                Assert.That(_bus.TryGetLatest(out TMessage latest), Is.True, $"Retain {typeof(TMessage)}.");
                Assert.That(latest, Is.EqualTo(_message), $"Keep the offered {typeof(TMessage)} value.");
                if (typeof(TMessage).IsClass)
                {
                    Assert.That(latest, Is.SameAs(_message), $"Preserve {typeof(TMessage)} identity.");
                }
            }

            public void VerifyAbsent(MessageBus bus) => Assert.That(bus.TryGetLatest(out TMessage _), Is.False, $"No foreign {typeof(TMessage)} state.");

            public void Clear()
            {
                Assert.That(_bus.ClearLatest<TMessage>(), Is.True, $"Clear {typeof(TMessage)} exactly once.");
                VerifyAbsent(_bus);
            }

            public void DropMessage() => _message = default;
        }

        private sealed class Workload : IDisposable
        {
            private readonly bool _sticky;
            private readonly MessageBus _bus = new() { DiagnosticsMode = false };
            private readonly IEntry[] _entries;
            private readonly long _initialEmissionId;
            private readonly List<Type> _observed;
            private readonly MessageRegistrationToken _token;
            private readonly LeakWatcher _leaks;
            private int _next;

            internal Workload(bool sticky, bool referencePayload, int cardinality, bool observe)
            {
                _sticky = sticky;
                _entries = new IEntry[cardinality];
                for (int index = 0; index < cardinality; ++index)
                {
                    IEntry entry = CreateEntry(_bus, referencePayload, index);
                    for (int prior = 0; prior < index; ++prior)
                    {
                        Assert.That(
                            ReferenceEquals(entry.MessageType, _entries[prior].MessageType),
                            Is.False,
                            $"cardinality={cardinality}, index={index}, prior={prior}: distinct message types."
                        );
                    }
                    _entries[index] = entry;
                    entry.Seed();
                }
                _initialEmissionId = _bus.EmissionId;
                if (observe)
                {
                    _leaks = new LeakWatcher(_bus, label: "Cyclic publication contract");
                    _observed = new List<Type>();
                    MessageHandler handler = new(new InstanceId(749316), _bus) { active = true };
                    _token = MessageRegistrationToken.Create(handler, _bus);
                    _token.DiagnosticMode = false;
                    foreach (IEntry entry in _entries)
                    {
                        entry.Observe(_token, _observed);
                    }
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
                for (int index = 0; index < count; ++index)
                {
                    _entries[_next].Emit(_sticky);
                    ++_next;
                    if (_next == _entries.Length)
                    {
                        _next = 0;
                    }
                }
            }

            public void Verify(long expected)
            {
                string context = $"sticky={_sticky}, cardinality={_entries.Length}, expected={expected}";
                Assert.That(_initialEmissionId, Is.EqualTo(_entries.Length), "Seed every type: " + context);
                Assert.That(Emissions, Is.EqualTo(expected), "Count actual bus emissions: " + context);
                Assert.That(_next, Is.EqualTo(expected % _entries.Length), "Preserve rotation: " + context);
                if (_observed != null)
                {
                    Assert.That(_observed.Count, Is.EqualTo(expected), "Deliver once per operation: " + context);
                    for (int index = 0; index < _observed.Count; ++index)
                    {
                        Assert.That(_observed[index], Is.SameAs(_entries[index % _entries.Length].MessageType), "Keep actual type order: " + context);
                    }
                }
                MessageBus other = new() { DiagnosticsMode = false };
                foreach (IEntry entry in _entries)
                {
                    entry.VerifyRetained();
                    entry.VerifyAbsent(other);
                }
                Assert.That(_bus.DiagnosticsMode, Is.False, "Keep diagnostics disabled: " + context);
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
                        foreach (IEntry entry in _entries)
                        {
                            entry.Clear();
                        }
                    }
                    finally
                    {
                        _bus.ResetState();
                        foreach (IEntry entry in _entries)
                        {
                            entry.DropMessage();
                        }
                        _leaks?.Dispose();
                    }
                }
            }
        }

        private static IEntry CreateEntry(MessageBus bus, bool referencePayload, int index)
        {
            switch (index)
            {
                case 0:
                    return CreateEntry<Marker00>(bus, referencePayload, index);
                case 1:
                    return CreateEntry<Marker01>(bus, referencePayload, index);
                case 2:
                    return CreateEntry<Marker02>(bus, referencePayload, index);
                case 3:
                    return CreateEntry<Marker03>(bus, referencePayload, index);
                case 4:
                    return CreateEntry<Marker04>(bus, referencePayload, index);
                case 5:
                    return CreateEntry<Marker05>(bus, referencePayload, index);
                case 6:
                    return CreateEntry<Marker06>(bus, referencePayload, index);
                case 7:
                    return CreateEntry<Marker07>(bus, referencePayload, index);
                case 8:
                    return CreateEntry<Marker08>(bus, referencePayload, index);
                case 9:
                    return CreateEntry<Marker09>(bus, referencePayload, index);
                case 10:
                    return CreateEntry<Marker10>(bus, referencePayload, index);
                case 11:
                    return CreateEntry<Marker11>(bus, referencePayload, index);
                case 12:
                    return CreateEntry<Marker12>(bus, referencePayload, index);
                case 13:
                    return CreateEntry<Marker13>(bus, referencePayload, index);
                case 14:
                    return CreateEntry<Marker14>(bus, referencePayload, index);
                case 15:
                    return CreateEntry<Marker15>(bus, referencePayload, index);
                default:
                    throw new ArgumentOutOfRangeException(nameof(index));
            }
        }

        private static IEntry CreateEntry<TMarker>(MessageBus bus, bool referencePayload, int index) =>
            referencePayload
                ? new Entry<ReferenceMessage<TMarker>>(bus, new ReferenceMessage<TMarker>(17 + index))
                : new Entry<ValueMessage<TMarker>>(bus, new ValueMessage<TMarker>(17 + index));

        private readonly struct ValueMessage<TMarker> : IUntargetedMessage<ValueMessage<TMarker>>
        {
            internal ValueMessage(int value) => Value = value;

            public int Value { get; }
        }

        private sealed class ReferenceMessage<TMarker> : IUntargetedMessage<ReferenceMessage<TMarker>>
        {
            internal ReferenceMessage(int value) => Value = value;

            public int Value { get; }
        }

        private readonly struct Marker00 { }

        private readonly struct Marker01 { }

        private readonly struct Marker02 { }

        private readonly struct Marker03 { }

        private readonly struct Marker04 { }

        private readonly struct Marker05 { }

        private readonly struct Marker06 { }

        private readonly struct Marker07 { }

        private readonly struct Marker08 { }

        private readonly struct Marker09 { }

        private readonly struct Marker10 { }

        private readonly struct Marker11 { }

        private readonly struct Marker12 { }

        private readonly struct Marker13 { }

        private readonly struct Marker14 { }

        private readonly struct Marker15 { }
    }

    [Category("PerfBench")]
    public sealed class StickyCyclicTypeBenchmarkTests
    {
        [Test]
        public void CyclicWorkloadPreservesEveryTypeAndOperation(
            [Values(false, true)] bool sticky,
            [Values(false, true)] bool referencePayload,
            [Values(3, 16)] int cardinality,
            [Values(0, 1, 17)] int first,
            [Values(0, 2, 31)] int second
        )
        {
            long emissions = StickyCyclicTypeBenchmarks.RunContract(sticky, referencePayload, cardinality, first, second);
            Assert.That(
                emissions,
                Is.EqualTo((long)first + second),
                $"sticky={sticky},referencePayload={referencePayload},cardinality={cardinality},first={first},second={second}: exact bus count."
            );
        }

        [TestCase(0)]
        [TestCase(2)]
        [TestCase(17)]
        public void UnsupportedCyclicCardinalityIsRejected(int cardinality)
        {
            ArgumentOutOfRangeException failure = Assert.Throws<ArgumentOutOfRangeException>(() => StickyCyclicTypeBenchmarks.RunContract(true, false, cardinality, 0, 0));
            Assert.That(failure.ParamName, Is.EqualTo("cardinality"), $"cardinality={cardinality}: reject before construction.");
        }

        [TestCase(-1, 0)]
        [TestCase(0, -1)]
        public void NegativeCyclicOperationCountIsRejected(int first, int second)
        {
            ArgumentOutOfRangeException failure = Assert.Throws<ArgumentOutOfRangeException>(() => StickyCyclicTypeBenchmarks.RunContract(true, false, 3, first, second));
            Assert.That(failure.ParamName, Is.EqualTo("count"), $"first={first},second={second}: reject a negative count.");
        }
    }
}
#endif
