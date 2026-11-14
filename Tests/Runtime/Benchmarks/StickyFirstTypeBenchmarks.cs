#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Benchmarks
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;
    using UnityEngine;

    /// <summary>
    /// Measures one publication per distinct closed message type after untimed state preparation.
    /// Types can share generated code. Type materialization and payload construction are untimed.
    /// </summary>
    public sealed class StickyFirstTypeBenchmarks
    {
        internal const int Trials = 32;
        private static readonly HashSet<Type> MeasuredTypes = new();

        [Test, Performance, Category("PerfBench")]
        [TestCase(false, false, TestName = "Sticky_FirstType_OrdinaryEmit_Struct")]
        [TestCase(false, true, TestName = "Sticky_FirstType_OrdinaryEmit_Class")]
        [TestCase(true, false, TestName = "Sticky_FirstType_StickyEmit_Struct")]
        [TestCase(true, true, TestName = "Sticky_FirstType_StickyEmit_Class")]
        public void DistinctTypeFirstPublication(bool sticky, bool referencePayload)
        {
            using IDisposable registry = BenchmarkProtocol.IsolateIdleSweepRegistry();
            TrialFactory[] factories = sticky
                ? BuildFactories<MeasuredSticky>(sticky, referencePayload)
                : BuildFactories<MeasuredOrdinary>(sticky, referencePayload);
            Type[] types = new Type[factories.Length];
            for (int index = 0; index < factories.Length; ++index)
            {
                types[index] = factories[index].MessageType;
            }
            ReserveTypes(MeasuredTypes, types);
            AllocationProbe.SettleHeapForMeasurement();
            ColdLatencyMeasurement measurement = BenchmarkProtocol.MeasureColdLatency(
                Trials,
                index => factories[index].Create(),
                state => state.Execute(),
                state =>
                {
                    try
                    {
                        state.Verify();
                    }
                    finally
                    {
                        state.Dispose();
                    }
                }
            );
            string key = Key(sticky, referencePayload);
            Assert.That(measurement.RawTrials.Count, Is.EqualTo(Trials), key);
            foreach (ColdLatencyTrial trial in measurement.RawTrials)
            {
                TestContext.Out.WriteLine(
                    $"DXM_STICKY_FIRST_TYPE_TRIAL scenario={key} trial={trial.Index} "
                        + $"messageType={types[trial.Index]} firstUse=true "
                        + $"wallClockMs={trial.WallClockMs.ToString("R", CultureInfo.InvariantCulture)} "
                        + $"gcAllocations={trial.GcAllocations} gcAllocatedBytes={trial.GcAllocatedBytes}"
                );
            }
            DispatchBenchmarkResult result = DispatchBenchmarkResult.ForColdLatencyScenario(
                key,
                -1,
                measurement.MedianGcAllocations,
                measurement.MedianGcAllocatedBytes,
                measurement.MedianWallClockMs
            );
            Debug.Log(result.ToStructuredLog());
            TestContext.Out.WriteLine(result.ToCsvRow());
        }

        internal static string Key(bool sticky, bool referencePayload) =>
            $"Sticky_FirstType_{(sticky ? "StickyEmit" : "OrdinaryEmit")}_{(referencePayload ? "Class" : "Struct")}";

        internal static void ReserveTypes(HashSet<Type> used, Type[] requested)
        {
            if (used == null)
            {
                throw new ArgumentNullException(nameof(used));
            }
            if (requested == null)
            {
                throw new ArgumentNullException(nameof(requested));
            }
            if (requested.Length == 0)
            {
                throw new ArgumentException(
                    "A first-type row must reserve its message types.",
                    nameof(requested)
                );
            }
            HashSet<Type> pending = new();
            foreach (Type type in requested)
            {
                if (type == null || used.Contains(type) || !pending.Add(type))
                {
                    throw new InvalidOperationException(
                        $"First-type measurement requires unused, distinct message types: {type}."
                    );
                }
            }
            used.UnionWith(pending);
        }

        internal static Type RunContract(bool sticky, bool referencePayload, int index)
        {
            using IDisposable registry = BenchmarkProtocol.IsolateIdleSweepRegistry();
            TrialFactory[] factories = sticky
                ? BuildFactories<ContractSticky>(sticky, referencePayload)
                : BuildFactories<ContractOrdinary>(sticky, referencePayload);
            TrialFactory factory = factories[index];
            using ITrial state = factory.Create();
            Assert.That(
                state.MessageType,
                Is.EqualTo(factory.MessageType),
                "Factory type must match the timed state."
            );
            state.Execute();
            state.Verify();
            return state.MessageType;
        }

        private static TrialFactory[] BuildFactories<TArm>(bool sticky, bool referencePayload) =>
            new[]
            {
                CreateFactory<TArm, Trial00>(sticky, referencePayload),
                CreateFactory<TArm, Trial01>(sticky, referencePayload),
                CreateFactory<TArm, Trial02>(sticky, referencePayload),
                CreateFactory<TArm, Trial03>(sticky, referencePayload),
                CreateFactory<TArm, Trial04>(sticky, referencePayload),
                CreateFactory<TArm, Trial05>(sticky, referencePayload),
                CreateFactory<TArm, Trial06>(sticky, referencePayload),
                CreateFactory<TArm, Trial07>(sticky, referencePayload),
                CreateFactory<TArm, Trial08>(sticky, referencePayload),
                CreateFactory<TArm, Trial09>(sticky, referencePayload),
                CreateFactory<TArm, Trial10>(sticky, referencePayload),
                CreateFactory<TArm, Trial11>(sticky, referencePayload),
                CreateFactory<TArm, Trial12>(sticky, referencePayload),
                CreateFactory<TArm, Trial13>(sticky, referencePayload),
                CreateFactory<TArm, Trial14>(sticky, referencePayload),
                CreateFactory<TArm, Trial15>(sticky, referencePayload),
                CreateFactory<TArm, Trial16>(sticky, referencePayload),
                CreateFactory<TArm, Trial17>(sticky, referencePayload),
                CreateFactory<TArm, Trial18>(sticky, referencePayload),
                CreateFactory<TArm, Trial19>(sticky, referencePayload),
                CreateFactory<TArm, Trial20>(sticky, referencePayload),
                CreateFactory<TArm, Trial21>(sticky, referencePayload),
                CreateFactory<TArm, Trial22>(sticky, referencePayload),
                CreateFactory<TArm, Trial23>(sticky, referencePayload),
                CreateFactory<TArm, Trial24>(sticky, referencePayload),
                CreateFactory<TArm, Trial25>(sticky, referencePayload),
                CreateFactory<TArm, Trial26>(sticky, referencePayload),
                CreateFactory<TArm, Trial27>(sticky, referencePayload),
                CreateFactory<TArm, Trial28>(sticky, referencePayload),
                CreateFactory<TArm, Trial29>(sticky, referencePayload),
                CreateFactory<TArm, Trial30>(sticky, referencePayload),
                CreateFactory<TArm, Trial31>(sticky, referencePayload),
            };

        private static TrialFactory CreateFactory<TArm, TMarker>(bool sticky, bool referencePayload)
        {
            return referencePayload
                ? new TrialFactory(typeof(ReferenceMessage<TArm, TMarker>), () =>
                    new Trial<ReferenceMessage<TArm, TMarker>>(
                        sticky,
                        new ReferenceMessage<TArm, TMarker>(7)
                    )
                )
                : new TrialFactory(typeof(ValueMessage<TArm, TMarker>), () =>
                    new Trial<ValueMessage<TArm, TMarker>>(
                        sticky,
                        new ValueMessage<TArm, TMarker>(7)
                    )
                );
        }

        private readonly struct TrialFactory
        {
            internal TrialFactory(Type messageType, Func<ITrial> create)
            {
                MessageType = messageType;
                Create = create;
            }

            internal Type MessageType { get; }
            internal Func<ITrial> Create { get; }
        }

        private interface ITrial : IDisposable
        {
            Type MessageType { get; }
            void Execute();
            void Verify();
        }

        private interface IValue : IUntargetedMessage
        {
            int Value { get; }
        }

        private readonly struct ValueMessage<TArm, TMarker>
            : IValue,
                IUntargetedMessage<ValueMessage<TArm, TMarker>>
        {
            internal ValueMessage(int value) => Value = value;

            public int Value { get; }
        }

        private sealed class ReferenceMessage<TArm, TMarker>
            : IValue,
                IUntargetedMessage<ReferenceMessage<TArm, TMarker>>
        {
            internal ReferenceMessage(int value) => Value = value;

            public int Value { get; }
        }

        private sealed class Trial<TMessage> : ITrial
            where TMessage : IValue
        {
            private readonly bool _sticky;
            private readonly MessageBus _bus = new() { DiagnosticsMode = false };
            private TMessage _message;
            private int _executions;

            internal Trial(bool sticky, TMessage message)
            {
                _sticky = sticky;
                _message = message;
            }

            public Type MessageType => typeof(TMessage);

            public void Execute()
            {
                if (_sticky)
                {
                    _bus.EmitSticky(ref _message);
                }
                else
                {
                    _bus.UntargetedBroadcast(ref _message);
                }
                ++_executions;
            }

            public void Verify()
            {
                string context = $"sticky={_sticky}, type={typeof(TMessage)}";
                Assert.That(_executions, Is.EqualTo(1), context);
                Assert.That(_message.Value, Is.EqualTo(7), context);
                bool present = _bus.TryGetLatest(out TMessage latest);
                Assert.That(present, Is.EqualTo(_sticky), context);
                if (present)
                {
                    Assert.That(latest, Is.EqualTo(_message), context);
                    Assert.That(latest.Value, Is.EqualTo(7), context);
                    if (typeof(TMessage).IsClass)
                    {
                        Assert.That(latest, Is.SameAs(_message), context);
                    }
                }
                else
                {
                    Assert.That(latest, Is.EqualTo(default(TMessage)), context);
                }
                MessageBus other = new() { DiagnosticsMode = false };
                Assert.That(other.TryGetLatest(out TMessage _), Is.False, context);
                Assert.That(_bus.DiagnosticsMode, Is.False, context);
                Assert.That(_bus.ClearLatest<TMessage>(), Is.EqualTo(_sticky), context);
                Assert.That(_bus.TryGetLatest(out TMessage _), Is.False, context);
            }

            public void Dispose()
            {
                _bus.ClearLatest<TMessage>();
                _message = default;
            }
        }

        private readonly struct MeasuredOrdinary { }

        private readonly struct MeasuredSticky { }

        private readonly struct ContractOrdinary { }

        private readonly struct ContractSticky { }

        private readonly struct Trial00 { }

        private readonly struct Trial01 { }

        private readonly struct Trial02 { }

        private readonly struct Trial03 { }

        private readonly struct Trial04 { }

        private readonly struct Trial05 { }

        private readonly struct Trial06 { }

        private readonly struct Trial07 { }

        private readonly struct Trial08 { }

        private readonly struct Trial09 { }

        private readonly struct Trial10 { }

        private readonly struct Trial11 { }

        private readonly struct Trial12 { }

        private readonly struct Trial13 { }

        private readonly struct Trial14 { }

        private readonly struct Trial15 { }

        private readonly struct Trial16 { }

        private readonly struct Trial17 { }

        private readonly struct Trial18 { }

        private readonly struct Trial19 { }

        private readonly struct Trial20 { }

        private readonly struct Trial21 { }

        private readonly struct Trial22 { }

        private readonly struct Trial23 { }

        private readonly struct Trial24 { }

        private readonly struct Trial25 { }

        private readonly struct Trial26 { }

        private readonly struct Trial27 { }

        private readonly struct Trial28 { }

        private readonly struct Trial29 { }

        private readonly struct Trial30 { }

        private readonly struct Trial31 { }
    }
}
#endif
