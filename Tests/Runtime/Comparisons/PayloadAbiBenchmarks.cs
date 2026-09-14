#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Runtime.InteropServices;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Runtime.Benchmarks;
    using NUnit.Framework;
    using UnityEngine;

    public enum PayloadAbiKind
    {
        None,
        ByValue,
        ReadonlyIn,
    }

    public enum PayloadAbiShape
    {
        EmptyRoute,
        OneHandler,
        MonomorphicSixteen,
        HeterogeneousSixteen,
    }

    public enum PayloadAbiPayload
    {
        Reference,
        CanonicalStruct,
        Readonly0,
        Readonly16,
        Readonly64,
        Readonly256,
        Word1,
        Word2,
        Word4,
        Word8,
    }

    /// <summary>
    /// Manual-only payload-size and callback-ABI factor screen. The fixture is intentionally
    /// absent from ComparisonScenario and carries only PerfBench, so neither ordinary correctness
    /// CI nor the recurring comparison roster executes these five-second diagnostic rows.
    /// </summary>
    public sealed class PayloadAbiBenchmarks
    {
        [Test, Category("PerfBench")]
        [TestCaseSource(nameof(MeasurementCases))]
        public void PayloadSizeAndCallbackAbiScreen(PayloadAbiMeasurementCase measurementCase)
        {
            if (measurementCase.IsEmptyRoute)
            {
                RunEmpty(measurementCase);
                return;
            }

            RunPair(measurementCase);
        }

        internal static PayloadAbiObservation ObserveOnceForContract(PayloadAbiWorkload workload)
        {
            using IPayloadAbiState state = CreateState(workload);
            state.EmitMany(1);
            return state.Observe();
        }

        private static void RunEmpty(PayloadAbiMeasurementCase measurementCase)
        {
            PayloadAbiWorkload workload = measurementCase.First;
            using IPayloadAbiState state = CreateState(workload);
            BenchmarkMeasurement measurement = BenchmarkProtocol.Measure(
                () => state.EmitMany(BenchmarkProtocol.WarmupEmits),
                () =>
                {
                    state.EmitMany(BenchmarkProtocol.BatchSize);
                    return BenchmarkProtocol.BatchSize;
                }
            );
            PayloadAbiObservation observation = state.Observe();
            AssertZeroAllocation(workload, measurement.GcAllocations, measurement.GcAllocatedBytes);
            AssertObservation(
                workload,
                observation,
                expectedEmits: measurement.TotalEmittedOperations + BenchmarkProtocol.WarmupEmits
            );
            PayloadAbiResult result = PayloadAbiResult.Empty(workload, measurement, observation);
            Debug.Log(result.ToStructuredLog());
            TestContext.Out.WriteLine(PayloadAbiResult.CsvHeader);
            TestContext.Out.WriteLine(result.ToCsvRow());
        }

        private static void RunPair(PayloadAbiMeasurementCase measurementCase)
        {
            PayloadAbiWorkload byValue = measurementCase.ByValue;
            PayloadAbiWorkload readonlyIn = measurementCase.ReadonlyIn;
            using IPayloadAbiState byValueState = CreateState(byValue);
            using IPayloadAbiState readonlyState = CreateState(readonlyIn);

            IPayloadAbiState first = measurementCase.ReadonlyFirst ? readonlyState : byValueState;
            IPayloadAbiState second = measurementCase.ReadonlyFirst ? byValueState : readonlyState;
            PairedBenchmarkMeasurement measurement = BenchmarkProtocol.MeasurePaired(
                () => first.EmitMany(BenchmarkProtocol.WarmupEmits),
                () =>
                {
                    first.EmitMany(BenchmarkProtocol.BatchSize);
                    return BenchmarkProtocol.BatchSize;
                },
                () => second.EmitMany(BenchmarkProtocol.WarmupEmits),
                () =>
                {
                    second.EmitMany(BenchmarkProtocol.BatchSize);
                    return BenchmarkProtocol.BatchSize;
                }
            );

            AllocationProbe.AllocationSample byValueAllocation = AllocationProbe.MeasureWithBytes(
                () =>
                    byValueState.EmitMany(BenchmarkProtocol.BatchSize)
            );
            AllocationProbe.AllocationSample readonlyAllocation = AllocationProbe.MeasureWithBytes(
                () =>
                    readonlyState.EmitMany(BenchmarkProtocol.BatchSize)
            );
            AssertZeroAllocation(byValue, byValueAllocation.Allocations, byValueAllocation.Bytes);
            AssertZeroAllocation(
                readonlyIn,
                readonlyAllocation.Allocations,
                readonlyAllocation.Bytes
            );

            long byValueTimedOperations = measurementCase.ReadonlyFirst
                ? measurement.Second.TotalOperations
                : measurement.First.TotalOperations;
            long readonlyTimedOperations = measurementCase.ReadonlyFirst
                ? measurement.First.TotalOperations
                : measurement.Second.TotalOperations;
            AssertObservation(
                byValue,
                byValueState.Observe(),
                BenchmarkProtocol.WarmupEmits + byValueTimedOperations + BenchmarkProtocol.BatchSize
            );
            AssertObservation(
                readonlyIn,
                readonlyState.Observe(),
                BenchmarkProtocol.WarmupEmits
                    + readonlyTimedOperations
                    + BenchmarkProtocol.BatchSize
            );

            IReadOnlyList<double> normalizedCycles = NormalizeCycles(
                measurement,
                measurementCase.ReadonlyFirst
            );
            double readonlyToByValueRatio = measurementCase.ReadonlyFirst
                ? measurement.FirstToSecondRatio
                : 1d / measurement.FirstToSecondRatio;
            double byValueRate = measurementCase.ReadonlyFirst
                ? measurement.Second.OperationsPerSecond
                : measurement.First.OperationsPerSecond;
            double readonlyRate = measurementCase.ReadonlyFirst
                ? measurement.First.OperationsPerSecond
                : measurement.Second.OperationsPerSecond;
            PayloadAbiResult byValueResult = PayloadAbiResult.Paired(
                byValue,
                byValueRate,
                byValueTimedOperations,
                byValueAllocation,
                byValueState.Observe(),
                measurementCase.ReadonlyFirst,
                readonlyToByValueRatio,
                measurement.CycleRatioSpreadPercent,
                normalizedCycles
            );
            PayloadAbiResult readonlyResult = PayloadAbiResult.Paired(
                readonlyIn,
                readonlyRate,
                readonlyTimedOperations,
                readonlyAllocation,
                readonlyState.Observe(),
                measurementCase.ReadonlyFirst,
                readonlyToByValueRatio,
                measurement.CycleRatioSpreadPercent,
                normalizedCycles
            );
            Debug.Log(byValueResult.ToStructuredLog());
            Debug.Log(readonlyResult.ToStructuredLog());
            TestContext.Out.WriteLine(PayloadAbiResult.CsvHeader);
            TestContext.Out.WriteLine(byValueResult.ToCsvRow());
            TestContext.Out.WriteLine(readonlyResult.ToCsvRow());
        }

        private static void AssertObservation(
            PayloadAbiWorkload workload,
            PayloadAbiObservation observation,
            long expectedEmits
        )
        {
            Assert.AreEqual(
                (long)workload.HandlerCount * expectedEmits,
                observation.Progress,
                $"[{workload.Key}] Every emit must preserve the declared callback fan-out."
            );
            Assert.AreEqual(
                workload.HandlerCount,
                observation.LiveRegistrations,
                $"[{workload.Key}] The live registration topology changed during measurement."
            );
            Assert.AreEqual(
                workload.HandlerCount == 0 ? 0 : 1,
                observation.OccupiedTypeSlots,
                $"[{workload.Key}] The workload must own exactly its declared message-type slot."
            );
            Assert.IsFalse(
                observation.BusDiagnostics,
                $"[{workload.Key}] Bus diagnostics must stay off."
            );
            Assert.IsFalse(
                observation.TokenDiagnostics,
                $"[{workload.Key}] Token diagnostics must stay off."
            );
        }

        private static void AssertZeroAllocation(
            PayloadAbiWorkload workload,
            long allocations,
            long bytes
        )
        {
            if (allocations != AllocationProbe.Unmeasured)
            {
                Assert.Zero(
                    allocations,
                    $"[{workload.Key}] Warm steady-state dispatch allocated managed objects."
                );
            }
            if (bytes != AllocationProbe.Unmeasured)
            {
                Assert.Zero(
                    bytes,
                    $"[{workload.Key}] Warm steady-state dispatch allocated managed bytes."
                );
            }
        }

        private static IReadOnlyList<double> NormalizeCycles(
            PairedBenchmarkMeasurement measurement,
            bool readonlyFirst
        )
        {
            double[] ratios = new double[measurement.CycleRatios.Count];
            for (int index = 0; index < ratios.Length; ++index)
            {
                ratios[index] = readonlyFirst
                    ? measurement.CycleRatios[index]
                    : 1d / measurement.CycleRatios[index];
            }
            return Array.AsReadOnly(ratios);
        }

        private static IPayloadAbiState CreateState(PayloadAbiWorkload workload)
        {
            return workload.Payload switch
            {
                PayloadAbiPayload.Reference => new PayloadAbiState<ReferencePayload>(
                    workload,
                    new ReferencePayload()
                ),
                PayloadAbiPayload.CanonicalStruct => new PayloadAbiState<ComparisonStructPayload>(
                    workload,
                    new ComparisonStructPayload(1)
                ),
                PayloadAbiPayload.Readonly0 => new PayloadAbiState<Readonly0Payload>(
                    workload,
                    default
                ),
                PayloadAbiPayload.Readonly16 => new PayloadAbiState<Readonly16Payload>(
                    workload,
                    default
                ),
                PayloadAbiPayload.Readonly64 => new PayloadAbiState<Readonly64Payload>(
                    workload,
                    default
                ),
                PayloadAbiPayload.Readonly256 => new PayloadAbiState<Readonly256Payload>(
                    workload,
                    default
                ),
                PayloadAbiPayload.Word1 => new PayloadAbiState<Word1Payload>(workload, default),
                PayloadAbiPayload.Word2 => new PayloadAbiState<Word2Payload>(workload, default),
                PayloadAbiPayload.Word4 => new PayloadAbiState<Word4Payload>(workload, default),
                PayloadAbiPayload.Word8 => new PayloadAbiState<Word8Payload>(workload, default),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(workload),
                    workload.Payload,
                    null
                ),
            };
        }

        private static IEnumerable<TestCaseData> MeasurementCases()
        {
            foreach (PayloadAbiMeasurementCase measurementCase in PayloadAbiScenarios.Measurements)
            {
                yield return new TestCaseData(measurementCase).SetName(measurementCase.Key);
            }
        }

        private interface IPayloadAbiState : IDisposable
        {
            void EmitMany(int count);

            PayloadAbiObservation Observe();
        }

        private sealed class PayloadAbiState<T> : IPayloadAbiState
            where T : IUntargetedMessage
        {
            private readonly MessageBus _bus;
            private readonly DiagnosticsScope _diagnostics;
            private readonly MessageHandler _handler;
            private readonly IDisposable _registry;
            private readonly MessageRegistrationToken _token;
            private readonly CounterSink _sink;
            private readonly Delegate[] _delegates;
            private readonly int _receiverTypeCount;
            private T _message;
            private bool _disposed;

            public PayloadAbiState(PayloadAbiWorkload workload, T message)
            {
                _diagnostics = new DiagnosticsScope(
                    DiagnosticsTarget.Off,
                    diagnosticsStackTraces: false
                );
                _registry = BenchmarkProtocol.IsolateIdleSweepRegistry();
                MessageRegistrationToken token = null;
                try
                {
                    _message = message;
                    _sink = new CounterSink();
                    _bus = new MessageBus { DiagnosticsMode = false };
                    _handler = new MessageHandler(new InstanceId(0x5041_0001), _bus)
                    {
                        active = true,
                    };
                    token = MessageRegistrationToken.Create(_handler, _bus);
                    _token = token;
                    _token.DiagnosticMode = false;
                    _token.Enable();
                    (_delegates, _receiverTypeCount) = Register(workload);
                }
                catch
                {
                    try
                    {
                        token?.Dispose();
                    }
                    finally
                    {
                        _registry.Dispose();
                        _diagnostics.Dispose();
                    }
                    throw;
                }
            }

            public void EmitMany(int count)
            {
                for (int index = 0; index < count; ++index)
                {
                    _bus.UntargetedBroadcast(ref _message);
                }
            }

            public PayloadAbiObservation Observe()
            {
                return new PayloadAbiObservation(
                    _sink.Progress,
                    BenchmarkProtocol.CountRegistrations(_token),
                    _bus.OccupiedTypeSlots,
                    _bus.DiagnosticsMode,
                    _token.DiagnosticMode,
                    _token.Enabled,
                    _handler.active,
                    DistinctDelegateCount(),
                    DistinctMethodCount(),
                    _receiverTypeCount
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
                    try
                    {
                        _registry.Dispose();
                    }
                    finally
                    {
                        _diagnostics.Dispose();
                    }
                }
            }

            private (Delegate[] Delegates, int ReceiverTypes) Register(PayloadAbiWorkload workload)
            {
                if (workload.HandlerCount == 0)
                {
                    return (Array.Empty<Delegate>(), 0);
                }
                if (workload.Shape == PayloadAbiShape.HeterogeneousSixteen)
                {
                    return RegisterHeterogeneous(workload.Abi);
                }

                Delegate[] delegates = new Delegate[workload.HandlerCount];
                for (int index = 0; index < delegates.Length; ++index)
                {
                    MonoReceiver<T> receiver = new(_sink);
                    delegates[index] = RegisterReceiver(
                        workload.Abi,
                        receiver.HandleValue,
                        receiver.HandleFast
                    );
                }
                return (delegates, 1);
            }

            private Delegate RegisterReceiver(
                PayloadAbiKind abi,
                Action<T> byValue,
                MessageHandler.FastHandler<T> readonlyIn
            )
            {
                switch (abi)
                {
                    case PayloadAbiKind.ByValue:
                        _token.RegisterUntargeted(byValue);
                        return byValue;
                    case PayloadAbiKind.ReadonlyIn:
                        _token.RegisterUntargeted(readonlyIn);
                        return readonlyIn;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(abi), abi, null);
                }
            }

            private (Delegate[] Delegates, int ReceiverTypes) RegisterHeterogeneous(
                PayloadAbiKind abi
            )
            {
                HeterogeneousReceiver<T>[] receivers =
                {
                    new Receiver00<T>(_sink),
                    new Receiver01<T>(_sink),
                    new Receiver02<T>(_sink),
                    new Receiver03<T>(_sink),
                    new Receiver04<T>(_sink),
                    new Receiver05<T>(_sink),
                    new Receiver06<T>(_sink),
                    new Receiver07<T>(_sink),
                    new Receiver08<T>(_sink),
                    new Receiver09<T>(_sink),
                    new Receiver10<T>(_sink),
                    new Receiver11<T>(_sink),
                    new Receiver12<T>(_sink),
                    new Receiver13<T>(_sink),
                    new Receiver14<T>(_sink),
                    new Receiver15<T>(_sink),
                };
                Delegate[] delegates = new Delegate[receivers.Length];
                for (int index = 0; index < receivers.Length; ++index)
                {
                    delegates[index] = RegisterReceiver(
                        abi,
                        receivers[index].HandleValue,
                        receivers[index].HandleFast
                    );
                }
                HashSet<Type> receiverTypes = new();
                for (int index = 0; index < receivers.Length; ++index)
                {
                    receiverTypes.Add(receivers[index].GetType());
                }
                return (delegates, receiverTypes.Count);
            }

            private int DistinctDelegateCount()
            {
                HashSet<Delegate> distinct = new();
                for (int index = 0; index < _delegates.Length; ++index)
                {
                    distinct.Add(_delegates[index]);
                }
                return distinct.Count;
            }

            private int DistinctMethodCount()
            {
                HashSet<System.Reflection.MethodInfo> distinct = new();
                for (int index = 0; index < _delegates.Length; ++index)
                {
                    distinct.Add(_delegates[index].Method);
                }
                return distinct.Count;
            }
        }

        private sealed class CounterSink
        {
            public long Progress { get; private set; }

            public void Mark()
            {
                ++Progress;
            }
        }

        private sealed class MonoReceiver<T>
        {
            private readonly CounterSink _sink;

            public MonoReceiver(CounterSink sink) => _sink = sink;

            public void HandleValue(T message) => _sink.Mark();

            public void HandleFast(in T message) => _sink.Mark();
        }

        private abstract class HeterogeneousReceiver<T>
        {
            protected HeterogeneousReceiver(CounterSink sink) => Sink = sink;

            protected CounterSink Sink { get; }

            public abstract void HandleValue(T message);

            public abstract void HandleFast(in T message);
        }

        private sealed class Receiver00<T> : HeterogeneousReceiver<T>
        {
            public Receiver00(CounterSink s)
                : base(s) { }

            public override void HandleValue(T m) => Sink.Mark();

            public override void HandleFast(in T m) => Sink.Mark();
        }

        private sealed class Receiver01<T> : HeterogeneousReceiver<T>
        {
            public Receiver01(CounterSink s)
                : base(s) { }

            public override void HandleValue(T m) => Sink.Mark();

            public override void HandleFast(in T m) => Sink.Mark();
        }

        private sealed class Receiver02<T> : HeterogeneousReceiver<T>
        {
            public Receiver02(CounterSink s)
                : base(s) { }

            public override void HandleValue(T m) => Sink.Mark();

            public override void HandleFast(in T m) => Sink.Mark();
        }

        private sealed class Receiver03<T> : HeterogeneousReceiver<T>
        {
            public Receiver03(CounterSink s)
                : base(s) { }

            public override void HandleValue(T m) => Sink.Mark();

            public override void HandleFast(in T m) => Sink.Mark();
        }

        private sealed class Receiver04<T> : HeterogeneousReceiver<T>
        {
            public Receiver04(CounterSink s)
                : base(s) { }

            public override void HandleValue(T m) => Sink.Mark();

            public override void HandleFast(in T m) => Sink.Mark();
        }

        private sealed class Receiver05<T> : HeterogeneousReceiver<T>
        {
            public Receiver05(CounterSink s)
                : base(s) { }

            public override void HandleValue(T m) => Sink.Mark();

            public override void HandleFast(in T m) => Sink.Mark();
        }

        private sealed class Receiver06<T> : HeterogeneousReceiver<T>
        {
            public Receiver06(CounterSink s)
                : base(s) { }

            public override void HandleValue(T m) => Sink.Mark();

            public override void HandleFast(in T m) => Sink.Mark();
        }

        private sealed class Receiver07<T> : HeterogeneousReceiver<T>
        {
            public Receiver07(CounterSink s)
                : base(s) { }

            public override void HandleValue(T m) => Sink.Mark();

            public override void HandleFast(in T m) => Sink.Mark();
        }

        private sealed class Receiver08<T> : HeterogeneousReceiver<T>
        {
            public Receiver08(CounterSink s)
                : base(s) { }

            public override void HandleValue(T m) => Sink.Mark();

            public override void HandleFast(in T m) => Sink.Mark();
        }

        private sealed class Receiver09<T> : HeterogeneousReceiver<T>
        {
            public Receiver09(CounterSink s)
                : base(s) { }

            public override void HandleValue(T m) => Sink.Mark();

            public override void HandleFast(in T m) => Sink.Mark();
        }

        private sealed class Receiver10<T> : HeterogeneousReceiver<T>
        {
            public Receiver10(CounterSink s)
                : base(s) { }

            public override void HandleValue(T m) => Sink.Mark();

            public override void HandleFast(in T m) => Sink.Mark();
        }

        private sealed class Receiver11<T> : HeterogeneousReceiver<T>
        {
            public Receiver11(CounterSink s)
                : base(s) { }

            public override void HandleValue(T m) => Sink.Mark();

            public override void HandleFast(in T m) => Sink.Mark();
        }

        private sealed class Receiver12<T> : HeterogeneousReceiver<T>
        {
            public Receiver12(CounterSink s)
                : base(s) { }

            public override void HandleValue(T m) => Sink.Mark();

            public override void HandleFast(in T m) => Sink.Mark();
        }

        private sealed class Receiver13<T> : HeterogeneousReceiver<T>
        {
            public Receiver13(CounterSink s)
                : base(s) { }

            public override void HandleValue(T m) => Sink.Mark();

            public override void HandleFast(in T m) => Sink.Mark();
        }

        private sealed class Receiver14<T> : HeterogeneousReceiver<T>
        {
            public Receiver14(CounterSink s)
                : base(s) { }

            public override void HandleValue(T m) => Sink.Mark();

            public override void HandleFast(in T m) => Sink.Mark();
        }

        private sealed class Receiver15<T> : HeterogeneousReceiver<T>
        {
            public Receiver15(CounterSink s)
                : base(s) { }

            public override void HandleValue(T m) => Sink.Mark();

            public override void HandleFast(in T m) => Sink.Mark();
        }
    }

    public sealed class PayloadAbiWorkload
    {
        internal PayloadAbiWorkload(
            PayloadAbiPayload payload,
            PayloadAbiShape shape,
            PayloadAbiKind abi,
            int logicalBytes,
            int physicalBytes,
            bool locator
        )
        {
            Payload = payload;
            Shape = shape;
            Abi = abi;
            LogicalBytes = logicalBytes;
            PhysicalBytes = physicalBytes;
            IsLocator = locator;
        }

        public PayloadAbiPayload Payload { get; }
        public PayloadAbiShape Shape { get; }
        public PayloadAbiKind Abi { get; }
        public int LogicalBytes { get; }
        public int PhysicalBytes { get; }
        public bool IsLocator { get; }
        public int HandlerCount =>
            Shape == PayloadAbiShape.EmptyRoute ? 0
            : Shape == PayloadAbiShape.OneHandler ? 1
            : 16;
        public string Key => $"PayloadAbi_{Payload}_{Shape}_{Abi}";

        public override string ToString() => Key;
    }

    public sealed class PayloadAbiMeasurementCase
    {
        internal PayloadAbiMeasurementCase(
            PayloadAbiWorkload first,
            PayloadAbiWorkload second,
            bool readonlyFirst
        )
        {
            First = first;
            Second = second;
            ReadonlyFirst = readonlyFirst;
        }

        public PayloadAbiWorkload First { get; }
        public PayloadAbiWorkload Second { get; }
        public bool ReadonlyFirst { get; }
        public bool IsEmptyRoute => Second == null;
        public PayloadAbiWorkload ByValue => First.Abi == PayloadAbiKind.ByValue ? First : Second;
        public PayloadAbiWorkload ReadonlyIn =>
            First.Abi == PayloadAbiKind.ReadonlyIn ? First : Second;
        public string Key =>
            IsEmptyRoute ? First.Key : $"PayloadAbiPair_{First.Payload}_{First.Shape}";

        public override string ToString() => Key;
    }

    public static class PayloadAbiScenarios
    {
        public static readonly IReadOnlyList<PayloadAbiWorkload> All;
        public static readonly IReadOnlyList<PayloadAbiMeasurementCase> Measurements;

        static PayloadAbiScenarios()
        {
            List<PayloadAbiWorkload> workloads = new();
            List<PayloadAbiMeasurementCase> measurements = new();
            PayloadAbiPayload[] primary =
            {
                PayloadAbiPayload.Reference,
                PayloadAbiPayload.CanonicalStruct,
                PayloadAbiPayload.Readonly0,
                PayloadAbiPayload.Readonly16,
                PayloadAbiPayload.Readonly64,
                PayloadAbiPayload.Readonly256,
            };
            int pairIndex = 0;
            foreach (PayloadAbiPayload payload in primary)
            {
                (int logical, int physical) = Sizes(payload);
                PayloadAbiWorkload empty = new(
                    payload,
                    PayloadAbiShape.EmptyRoute,
                    PayloadAbiKind.None,
                    logical,
                    physical,
                    false
                );
                workloads.Add(empty);
                measurements.Add(new PayloadAbiMeasurementCase(empty, null, false));
                foreach (
                    PayloadAbiShape shape in new[]
                    {
                        PayloadAbiShape.OneHandler,
                        PayloadAbiShape.MonomorphicSixteen,
                        PayloadAbiShape.HeterogeneousSixteen,
                    }
                )
                {
                    PayloadAbiWorkload byValue = new(
                        payload,
                        shape,
                        PayloadAbiKind.ByValue,
                        logical,
                        physical,
                        false
                    );
                    PayloadAbiWorkload readonlyIn = new(
                        payload,
                        shape,
                        PayloadAbiKind.ReadonlyIn,
                        logical,
                        physical,
                        false
                    );
                    workloads.Add(byValue);
                    workloads.Add(readonlyIn);
                    bool readonlyFirst = pairIndex++ % 2 == 1;
                    measurements.Add(
                        new PayloadAbiMeasurementCase(byValue, readonlyIn, readonlyFirst)
                    );
                }
            }
            foreach (
                PayloadAbiPayload payload in new[]
                {
                    PayloadAbiPayload.Word1,
                    PayloadAbiPayload.Word2,
                    PayloadAbiPayload.Word4,
                    PayloadAbiPayload.Word8,
                }
            )
            {
                (int logical, int physical) = Sizes(payload);
                PayloadAbiWorkload byValue = new(
                    payload,
                    PayloadAbiShape.OneHandler,
                    PayloadAbiKind.ByValue,
                    logical,
                    physical,
                    true
                );
                PayloadAbiWorkload readonlyIn = new(
                    payload,
                    PayloadAbiShape.OneHandler,
                    PayloadAbiKind.ReadonlyIn,
                    logical,
                    physical,
                    true
                );
                workloads.Add(byValue);
                workloads.Add(readonlyIn);
                bool readonlyFirst = pairIndex++ % 2 == 1;
                measurements.Add(new PayloadAbiMeasurementCase(byValue, readonlyIn, readonlyFirst));
            }
            All = workloads.AsReadOnly();
            Measurements = measurements.AsReadOnly();
        }

        internal static Type PayloadType(PayloadAbiPayload payload) =>
            payload switch
            {
                PayloadAbiPayload.Reference => typeof(ReferencePayload),
                PayloadAbiPayload.CanonicalStruct => typeof(ComparisonStructPayload),
                PayloadAbiPayload.Readonly0 => typeof(Readonly0Payload),
                PayloadAbiPayload.Readonly16 => typeof(Readonly16Payload),
                PayloadAbiPayload.Readonly64 => typeof(Readonly64Payload),
                PayloadAbiPayload.Readonly256 => typeof(Readonly256Payload),
                PayloadAbiPayload.Word1 => typeof(Word1Payload),
                PayloadAbiPayload.Word2 => typeof(Word2Payload),
                PayloadAbiPayload.Word4 => typeof(Word4Payload),
                PayloadAbiPayload.Word8 => typeof(Word8Payload),
                _ => throw new ArgumentOutOfRangeException(nameof(payload), payload, null),
            };

        private static (int Logical, int Physical) Sizes(PayloadAbiPayload payload)
        {
            Type type = PayloadType(payload);
            int physical = type.IsValueType ? Marshal.SizeOf(type) : -1;
            int logical = payload switch
            {
                PayloadAbiPayload.Reference => -1,
                PayloadAbiPayload.CanonicalStruct => sizeof(int),
                PayloadAbiPayload.Readonly0 => 0,
                PayloadAbiPayload.Readonly16 => 16,
                PayloadAbiPayload.Readonly64 => 64,
                PayloadAbiPayload.Readonly256 => 256,
                PayloadAbiPayload.Word1 => IntPtr.Size,
                PayloadAbiPayload.Word2 => IntPtr.Size * 2,
                PayloadAbiPayload.Word4 => IntPtr.Size * 4,
                PayloadAbiPayload.Word8 => IntPtr.Size * 8,
                _ => throw new ArgumentOutOfRangeException(nameof(payload), payload, null),
            };
            return (logical, physical);
        }
    }

    public readonly struct PayloadAbiObservation
    {
        public PayloadAbiObservation(
            long progress,
            int liveRegistrations,
            int occupiedTypeSlots,
            bool busDiagnostics,
            bool tokenDiagnostics,
            bool tokenEnabled,
            bool handlerActive,
            int distinctDelegates,
            int distinctMethods,
            int receiverTypes
        )
        {
            Progress = progress;
            LiveRegistrations = liveRegistrations;
            OccupiedTypeSlots = occupiedTypeSlots;
            BusDiagnostics = busDiagnostics;
            TokenDiagnostics = tokenDiagnostics;
            TokenEnabled = tokenEnabled;
            HandlerActive = handlerActive;
            DistinctDelegates = distinctDelegates;
            DistinctMethods = distinctMethods;
            ReceiverTypes = receiverTypes;
        }

        public long Progress { get; }
        public int LiveRegistrations { get; }
        public int OccupiedTypeSlots { get; }
        public bool BusDiagnostics { get; }
        public bool TokenDiagnostics { get; }
        public bool TokenEnabled { get; }
        public bool HandlerActive { get; }
        public int DistinctDelegates { get; }
        public int DistinctMethods { get; }
        public int ReceiverTypes { get; }
    }

    public readonly struct PayloadAbiResult
    {
        public const string CsvHeader =
            "scenario,payload,logicalBytes,physicalBytes,handlers,shape,abi,timedOperations,operationsPerSecond,gcAllocations,gcAllocatedBytes,readonlyFirst,readonlyToByValueRatio,cycleRatioSpreadPercent,cycleRatios,progress";

        private PayloadAbiResult(
            PayloadAbiWorkload workload,
            double rate,
            long timedOperations,
            long allocations,
            long bytes,
            bool readonlyFirst,
            double ratio,
            double spread,
            IReadOnlyList<double> cycles,
            long progress
        )
        {
            Workload = workload;
            OperationsPerSecond = rate;
            TimedOperations = timedOperations;
            GcAllocations = allocations;
            GcAllocatedBytes = bytes;
            ReadonlyFirst = readonlyFirst;
            ReadonlyToByValueRatio = ratio;
            CycleRatioSpreadPercent = spread;
            CycleRatios = cycles;
            Progress = progress;
        }

        public PayloadAbiWorkload Workload { get; }
        public long TimedOperations { get; }
        public double OperationsPerSecond { get; }
        public long GcAllocations { get; }
        public long GcAllocatedBytes { get; }
        public bool ReadonlyFirst { get; }
        public double ReadonlyToByValueRatio { get; }
        public double CycleRatioSpreadPercent { get; }
        public IReadOnlyList<double> CycleRatios { get; }
        public long Progress { get; }

        internal static PayloadAbiResult Empty(
            PayloadAbiWorkload workload,
            BenchmarkMeasurement measurement,
            PayloadAbiObservation observation
        ) =>
            new(
                workload,
                measurement.OperationsPerSecond,
                measurement.TotalOperations,
                measurement.GcAllocations,
                measurement.GcAllocatedBytes,
                false,
                double.NaN,
                double.NaN,
                Array.Empty<double>(),
                observation.Progress
            );

        internal static PayloadAbiResult Paired(
            PayloadAbiWorkload workload,
            double rate,
            long timedOperations,
            AllocationProbe.AllocationSample allocation,
            PayloadAbiObservation observation,
            bool readonlyFirst,
            double ratio,
            double spread,
            IReadOnlyList<double> cycles
        ) =>
            new(
                workload,
                rate,
                timedOperations,
                allocation.Allocations,
                allocation.Bytes,
                readonlyFirst,
                ratio,
                spread,
                cycles,
                observation.Progress
            );

        public string ToCsvRow() =>
            string.Join(
                ",",
                Workload.Key,
                Workload.Payload,
                Workload.LogicalBytes.ToString(CultureInfo.InvariantCulture),
                Workload.PhysicalBytes.ToString(CultureInfo.InvariantCulture),
                Workload.HandlerCount.ToString(CultureInfo.InvariantCulture),
                Workload.Shape,
                Workload.Abi,
                TimedOperations.ToString(CultureInfo.InvariantCulture),
                OperationsPerSecond.ToString("R", CultureInfo.InvariantCulture),
                GcAllocations.ToString(CultureInfo.InvariantCulture),
                GcAllocatedBytes.ToString(CultureInfo.InvariantCulture),
                ReadonlyFirst ? "true" : "false",
                Format(ReadonlyToByValueRatio),
                Format(CycleRatioSpreadPercent),
                FormatCycles(CycleRatios),
                Progress.ToString(CultureInfo.InvariantCulture)
            );

        public string ToStructuredLog() =>
            $"PERF_PAYLOAD_ABI scenario={Workload.Key} payload={Workload.Payload} logicalBytes={Workload.LogicalBytes} physicalBytes={Workload.PhysicalBytes} handlers={Workload.HandlerCount} shape={Workload.Shape} abi={Workload.Abi} timedOperations={TimedOperations} operationsPerSecond={Format(OperationsPerSecond)} gcAllocations={GcAllocations} gcAllocatedBytes={GcAllocatedBytes} readonlyFirst={ReadonlyFirst.ToString().ToLowerInvariant()} readonlyToByValueRatio={Format(ReadonlyToByValueRatio)} cycleRatioSpreadPercent={Format(CycleRatioSpreadPercent)} cycleRatios={FormatCycles(CycleRatios)} progress={Progress}";

        private static string Format(double value) =>
            value.ToString("R", CultureInfo.InvariantCulture);

        private static string FormatCycles(IReadOnlyList<double> cycles)
        {
            string[] values = new string[cycles.Count];
            for (int index = 0; index < cycles.Count; ++index)
            {
                values[index] = Format(cycles[index]);
            }
            return string.Join("|", values);
        }
    }

    public sealed class ReferencePayload : IUntargetedMessage<ReferencePayload> { }

    [StructLayout(LayoutKind.Sequential)]
    public readonly struct Readonly0Payload : IUntargetedMessage<Readonly0Payload> { }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    public readonly struct Readonly16Payload : IUntargetedMessage<Readonly16Payload>
    {
        [FieldOffset(0)]
        public readonly long Value;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public readonly struct Readonly64Payload : IUntargetedMessage<Readonly64Payload>
    {
        [FieldOffset(0)]
        public readonly long Value;
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    public readonly struct Readonly256Payload : IUntargetedMessage<Readonly256Payload>
    {
        [FieldOffset(0)]
        public readonly long Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    public readonly struct Word1Payload : IUntargetedMessage<Word1Payload>
    {
        public readonly IntPtr A;
    }

    [StructLayout(LayoutKind.Sequential)]
    public readonly struct Word2Payload : IUntargetedMessage<Word2Payload>
    {
        public readonly IntPtr A;
        public readonly IntPtr B;
    }

    [StructLayout(LayoutKind.Sequential)]
    public readonly struct Word4Payload : IUntargetedMessage<Word4Payload>
    {
        public readonly IntPtr A;
        public readonly IntPtr B;
        public readonly IntPtr C;
        public readonly IntPtr D;
    }

    [StructLayout(LayoutKind.Sequential)]
    public readonly struct Word8Payload : IUntargetedMessage<Word8Payload>
    {
        public readonly IntPtr A;
        public readonly IntPtr B;
        public readonly IntPtr C;
        public readonly IntPtr D;
        public readonly IntPtr E;
        public readonly IntPtr F;
        public readonly IntPtr G;
        public readonly IntPtr H;
    }
}
#endif
