#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Benchmarks
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Reflection;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Text.RegularExpressions;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using UnityEngine;
    using Debug = UnityEngine.Debug;

    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class PerformanceAttribute : CategoryAttribute
    {
        public PerformanceAttribute()
            : base("Performance") { }
    }

    public enum DispatchBenchmarkScenario
    {
        EmptyBusDispatch,
        UntargetedFloodOneHandler,
        UntargetedFloodTwoHandlersOnePriority,
        UntargetedFloodThreeHandlersOnePriority,
        UntargetedFloodFourHandlersOnePriority,
        UntargetedFloodFourHandlersFourPriorities,
        UntargetedFloodSixteenHandlersOnePriority,
        UntargetedFloodOneInactiveHandler,
        TargetedFloodNoMatchingTarget,
        TargetedFloodOneListener,
        TargetedFloodSixteenListeners,
        BroadcastFloodOneHandler,
        InterceptorHeavyFourInterceptors,
        PostProcessingHeavyFourPostProcessors,
        MessageBusConstruction1000,
        MessageRegistrationTokenConstruction1000,
        RegistrationFlood1000TypesFromColdBus,
        RegistrationFlood1000TypesWarmJit,
        UntargetedRegistrationMarginal,
        TargetedRegistrationMarginal,
        BroadcastRegistrationMarginal,
        DeregistrationFlood1000TypesCold,
        DeregistrationFlood1000TypesWarmJit,
        UntargetedFirstDispatchCold,
        TargetedFirstDispatchCold,
        BroadcastFirstDispatchCold,
    }

    public sealed class DispatchThroughputBenchmarks
    {
        [Test, Performance, Category("PerfBench")]
        public void MessageRegistrationHandlePhysicalSize()
        {
            int sizeBytes = Marshal.SizeOf(typeof(MessageRegistrationHandle));
            Assert.AreEqual(
                16,
                sizeBytes,
                "The { long id; int slot/hash } handle layout must remain 16 bytes after alignment."
            );
            Debug.Log($"DX_STRUCTURE_SIZE type=MessageRegistrationHandle bytes={sizeBytes}");
            TestContext.Out.WriteLine($"message-registration-handle-size,{sizeBytes}");
        }

        private const string BaselineOutputEnvVar = "DX_PERF_BASELINE";
        private const string BaselineModeEnvVar = "DX_PERF_BASELINE_MODE";
        private const string PackageName = "com.wallstop-studios.dxmessaging";
        private const string BaselineCsvHeader =
            "scenario,platform,commit,runIndex,emitsPerSecond,gcAllocations,wallClockMs,gcAllocatedBytes";
        private static readonly InstanceId Target = new(31001);
        private static readonly InstanceId Source = new(31002);
        private static readonly InstanceId MissingTarget = new(31003);
        private static Action<MessageRegistrationToken>[] _registrationFloodBuilders;

        // Marginal registration scenarios register this many additional handlers of a
        // SINGLE already-warmed message type, then report the allocation count/bytes for
        // the batch (so per-registration cost ~= the reported count / this value). A large
        // batch keeps the per-operation allocation floor well above warm-editor ambient
        // GC.Alloc noise, mirroring the "total over a window" benchmark methodology the
        // flood scenarios use.
        internal const int RegistrationMarginalCount = 1000;

        // A single marginal-registration batch completes in less than a millisecond on
        // IL2CPP and is too short to distinguish scheduler noise from a runtime change. Run
        // several fresh trials after one heap settle and report their minimum: the repeatable
        // floor estimator used by the warm registration/deregistration floods. Do not combine
        // these trials into one long window: retaining several live 1000-registration
        // populations forces collections into the clock because registration allocates.
        internal const int RegistrationMarginalTimingTrials = 7;

        // Allocation windows in a profiler-bearing Mono editor see additive ambient spikes.
        // Measure fresh, identically warmed populations and keep the minimum exact count,
        // with bytes from that same attempt. Stripped IL2CPP reports Unmeasured and skips
        // these allocation-only attempts; they never wrap the latency clock.
        internal const int RegistrationMarginalAllocationAttempts = 8;

        // Construction is a short, one-time operation, so measure a sufficiently large fixed
        // batch in one Stopwatch + AllocationProbe window. Arrays and required dependencies are
        // prepared outside that window; every constructed object is retained until it closes.
        internal const int ConstructionBatchSize = 1000;

        // Untimed warm-up registrations that remain live until the whole warm-up set has
        // been registered, then are removed together. Keeping them live grows each revision's
        // handler and token storage before the measured region, so the window captures marginal
        // same-type registration rather than first-growth setup.
        private const int RegistrationMarginalWarmup = 16;

        // Repeated trials for the WARM (JIT pre-warmed) flood scenarios. A single one-shot
        // wall-clock sample of a ~1 ms operation on a shared CI runner swings run-to-run by
        // tens of percent (scheduler preemption, a GC landing mid-window). The warm floods are
        // repeatable (the JIT is already paid, the population is rebuilt per trial), so they run
        // several trials and report the MINIMUM wall clock -- the floor when the CPU was not
        // interrupted, the most reproducible estimator (the same philosophy as
        // AllocationProbe.MeasureMin). The COLD floods stay single-shot because they
        // deliberately measure one-time first-touch JIT cost, which cannot be re-measured cold.
        private const int WarmFloodTrials = 7;

        [Test, Performance, Category("PerfBench")]
        [TestCaseSource(nameof(DispatchBenchmarkCases))]
        public void DispatchBenchmark(DispatchBenchmarkScenario scenario)
        {
            _ = RunScenario(scenario);
        }

        [Test, Explicit, Performance, Category("PerfBaseline")]
        public void UpdateDispatchThroughputBaseline()
        {
            string outputPath = ResolveBaselineOutputPath();
            bool replaceAllRows = string.Equals(
                Environment.GetEnvironmentVariable(BaselineModeEnvVar),
                "replace",
                StringComparison.OrdinalIgnoreCase
            );

            List<DispatchBenchmarkResult> results = new();
            foreach (DispatchBenchmarkScenario scenario in DispatchBenchmarkScenarios.All)
            {
                results.Add(RunScenario(scenario));
            }

            WriteBaselineRows(outputPath, results, replaceAllRows);
            TestContext.Out.WriteLine($"Updated performance baseline: {outputPath}");
        }

        public static DispatchBenchmarkResult RunScenario(
            DispatchBenchmarkScenario scenario,
            bool logResult = true
        )
        {
            DispatchBenchmarkResult result = MeasureScenario(scenario);
            if (logResult)
            {
                Debug.Log(result.ToStructuredLog());
                TestContext.Out.WriteLine(result.ToCsvRow());
            }

            return result;
        }

        // Route each scenario to its measurement methodology. Most scenarios are warm/hot
        // throughput windows (MeasureEmitScenario). The cold/warm-JIT registration and
        // deregistration floods, plus the three cold first-dispatch scenarios, are latency
        // measurements handled by dedicated helpers.
        private static DispatchBenchmarkResult MeasureScenario(DispatchBenchmarkScenario scenario)
        {
            switch (scenario)
            {
                case DispatchBenchmarkScenario.MessageBusConstruction1000:
                    return MeasureMessageBusConstruction();
                case DispatchBenchmarkScenario.MessageRegistrationTokenConstruction1000:
                    return MeasureMessageRegistrationTokenConstruction();
                case DispatchBenchmarkScenario.RegistrationFlood1000TypesFromColdBus:
                    return MeasureRegistrationFlood();
                case DispatchBenchmarkScenario.RegistrationFlood1000TypesWarmJit:
                    return MeasureRegistrationFloodWarmJit();
                case DispatchBenchmarkScenario.UntargetedRegistrationMarginal:
                case DispatchBenchmarkScenario.TargetedRegistrationMarginal:
                case DispatchBenchmarkScenario.BroadcastRegistrationMarginal:
                    return MeasureRegistrationMarginal(scenario);
                case DispatchBenchmarkScenario.DeregistrationFlood1000TypesCold:
                    return MeasureDeregistrationFlood();
                case DispatchBenchmarkScenario.DeregistrationFlood1000TypesWarmJit:
                    return MeasureDeregistrationFloodWarmJit();
                case DispatchBenchmarkScenario.UntargetedFirstDispatchCold:
                case DispatchBenchmarkScenario.TargetedFirstDispatchCold:
                case DispatchBenchmarkScenario.BroadcastFirstDispatchCold:
                    return MeasureColdFirstDispatch(scenario);
                default:
                    return MeasureEmitScenario(scenario);
            }
        }

        public static string GetScenarioName(DispatchBenchmarkScenario scenario)
        {
            return DispatchBenchmarkScenarios.Key(scenario);
        }

        private static IEnumerable<TestCaseData> DispatchBenchmarkCases()
        {
            foreach (DispatchBenchmarkScenario scenario in DispatchBenchmarkScenarios.All)
            {
                yield return new TestCaseData(scenario).SetName(scenario.ToString());
            }
        }

        private static DispatchBenchmarkResult MeasureEmitScenario(
            DispatchBenchmarkScenario scenario
        )
        {
            using BenchmarkRegistrationScope scope = new();
            Assert.IsFalse(
                scope.Bus.DiagnosticsMode,
                "Dispatch throughput rows must isolate the diagnostics-off production path."
            );
            Assert.IsFalse(
                scope.PrimaryToken.DiagnosticMode,
                "Dispatch throughput rows must not record per-registration diagnostics."
            );
            InvocationCounter handlerInvocations = new();
            ConfigureScenario(scope, scenario, handlerInvocations);

            BenchmarkMeasurement measurement = BenchmarkProtocol.Measure(
                () =>
                    EmitMany(scope.Bus, scenario, DispatchBenchmarkScenarios.WarmupEmits(scenario)),
                () =>
                {
                    EmitMany(scope.Bus, scenario, BenchmarkProtocol.BatchSize);
                    return BenchmarkProtocol.BatchSize;
                }
            );

            int perEmit = ExpectedHandlerInvocationsPerEmit(scenario);
            int warmupEmits = DispatchBenchmarkScenarios.WarmupEmits(scenario);
            // Defense-in-depth fan-out check (mirrors ComparisonHarness): reconcile the handler
            // invocation count against the CONTRACT fan-out per emit times EVERY emit the protocol
            // drove -- warmup + timed window + the untimed allocation-probe batch
            // (TotalEmittedOperations, never TotalOperations). Exact equality catches a dropped or
            // duplicated dispatch as well as the probe-batch accounting bug; the loose ">0" it
            // replaced caught neither.
            long expectedHandlerInvocations =
                (long)perEmit * (warmupEmits + measurement.TotalEmittedOperations);
            long observedHandlerInvocations = handlerInvocations.Count;
            long deltaHandlerInvocations = observedHandlerInvocations - expectedHandlerInvocations;
            Assert.AreEqual(
                expectedHandlerInvocations,
                observedHandlerInvocations,
                $"Dispatch scenario '{scenario}' fan-out mismatch: expected {expectedHandlerInvocations} "
                    + $"handler invocations, observed {observedHandlerInvocations} "
                    + $"(delta {BenchmarkProtocol.DescribeInvocationDelta(deltaHandlerInvocations, perEmit)}). "
                    + $"Breakdown: perEmit={perEmit}, "
                    + $"warmupEmits={warmupEmits}, timedOps={measurement.TotalOperations}, "
                    + $"allocationProbeOps={measurement.AllocationProbeOperations}, "
                    + $"totalEmittedOps={measurement.TotalEmittedOperations} (= timed + probe; warmup listed separately). "
                    + $"A delta of exactly +{(long)perEmit * BenchmarkProtocol.BatchSize} invocations "
                    + $"(+{BenchmarkProtocol.BatchSize} ops) points at post-window allocation-probe "
                    + "batch accounting; any other delta is a real "
                    + "dispatch fan-out defect (dropped/duplicated handler invocation)."
            );
            return DispatchBenchmarkResult.ForEmitScenario(
                GetScenarioName(scenario),
                runIndex: -1,
                measurement.OperationsPerSecond,
                measurement.GcAllocations,
                measurement.GcAllocatedBytes,
                measurement.ElapsedSeconds * 1000d
            );
        }

        // The CONTRACT fan-out per emit for each throughput (emit) scenario: how many
        // handlerInvocations increments a SINGLE emit must produce. Kept as an explicit,
        // implementation-independent contract that mirrors ConfigureScenario so a dispatch
        // regression that drops or duplicates an invocation makes the exact fan-out check in
        // MeasureEmitScenario fail (observed != expected) instead of passing silently.
        // Interceptors do NOT count (AllowUntargeted only gates, it never increments);
        // post-processors DO count (CountPostProcessed increments), plus the one terminal handler.
        internal static int ExpectedHandlerInvocationsPerEmit(DispatchBenchmarkScenario scenario)
        {
            switch (scenario)
            {
                case DispatchBenchmarkScenario.EmptyBusDispatch:
                case DispatchBenchmarkScenario.UntargetedFloodOneInactiveHandler:
                case DispatchBenchmarkScenario.TargetedFloodNoMatchingTarget:
                    return 0;
                case DispatchBenchmarkScenario.UntargetedFloodOneHandler:
                case DispatchBenchmarkScenario.TargetedFloodOneListener:
                case DispatchBenchmarkScenario.BroadcastFloodOneHandler:
                case DispatchBenchmarkScenario.InterceptorHeavyFourInterceptors:
                    return 1;
                case DispatchBenchmarkScenario.UntargetedFloodTwoHandlersOnePriority:
                    return 2;
                case DispatchBenchmarkScenario.UntargetedFloodThreeHandlersOnePriority:
                    return 3;
                case DispatchBenchmarkScenario.UntargetedFloodFourHandlersOnePriority:
                case DispatchBenchmarkScenario.UntargetedFloodFourHandlersFourPriorities:
                    return 4;
                case DispatchBenchmarkScenario.UntargetedFloodSixteenHandlersOnePriority:
                case DispatchBenchmarkScenario.TargetedFloodSixteenListeners:
                    return 16;
                case DispatchBenchmarkScenario.PostProcessingHeavyFourPostProcessors:
                    return 5; // four post-processors + one terminal handler
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
            }
        }

        // Force a full garbage collection to quiescence so a pending collection cannot land
        // INSIDE the next timed region and inflate the sample. Call this strictly BEFORE the
        // measurement stopwatch starts (it is itself untimed); it is the single biggest blip
        // remover for the ms-scale flood scenarios, which churn many short-lived objects.
        private static void QuiesceGarbageCollector()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        private static DispatchBenchmarkResult MeasureMessageBusConstruction()
        {
            // Pay first-touch constructor/JIT costs outside both measurement passes.
            MessageBus warmupBus;
            using (IDisposable registry = MessageBus.IsolateIdleSweepRegistryForBenchmark())
            {
                warmupBus = new MessageBus();
            }
            MessageBus[] timedBuses = new MessageBus[ConstructionBatchSize];
            long startTimestamp;
            long endTimestamp;
            AllocationProbe.SettleHeapForMeasurement();
            using (IDisposable registry = MessageBus.IsolateIdleSweepRegistryForBenchmark())
            {
                startTimestamp = Stopwatch.GetTimestamp();
                for (int index = 0; index < timedBuses.Length; index++)
                {
                    timedBuses[index] = new MessageBus();
                }
                endTimestamp = Stopwatch.GetTimestamp();
            }
            Assert.AreEqual(
                ConstructionBatchSize,
                CountConstructed(timedBuses),
                "The construction benchmark must retain every MessageBus through the timed pass."
            );
            Array.Clear(timedBuses, 0, timedBuses.Length);

            // Allocation uses a fresh registry and separate pass so GC.Alloc recorder overhead
            // never distorts the Mono timing result.
            MessageBus[] allocationBuses = new MessageBus[ConstructionBatchSize];
            AllocationProbe.SettleHeapForMeasurement();
            AllocationProbe.AllocationSample sample;
            using (IDisposable registry = MessageBus.IsolateIdleSweepRegistryForBenchmark())
            {
                sample = AllocationProbe.MeasureWithBytes(() =>
                {
                    for (int index = 0; index < allocationBuses.Length; index++)
                    {
                        allocationBuses[index] = new MessageBus();
                    }
                });
            }
            Assert.AreEqual(ConstructionBatchSize, CountConstructed(allocationBuses));
            GC.KeepAlive(warmupBus);

            return DispatchBenchmarkResult.ForWallClockScenario(
                GetScenarioName(DispatchBenchmarkScenario.MessageBusConstruction1000),
                runIndex: -1,
                sample.Allocations,
                sample.Bytes,
                TimestampDeltaToSeconds(startTimestamp, endTimestamp) * 1000d
            );
        }

        private static DispatchBenchmarkResult MeasureMessageRegistrationTokenConstruction()
        {
            using IDisposable registry = MessageBus.IsolateIdleSweepRegistryForBenchmark();

            // Token creation requires both a handler and a bus. Build those dependencies before
            // the measured region so this row isolates token construction and labels that setup
            // distinction explicitly. Retain and dispose all tokens outside the timing window.
            MessageBus[] buses = new MessageBus[ConstructionBatchSize];
            MessageHandler[] handlers = new MessageHandler[ConstructionBatchSize];
            MessageRegistrationToken[] tokens = new MessageRegistrationToken[ConstructionBatchSize];
            for (int index = 0; index < ConstructionBatchSize; index++)
            {
                MessageBus bus = new();
                buses[index] = bus;
                handlers[index] = new MessageHandler(new InstanceId(33000 + index), bus);
            }

            MessageBus warmupBus = new();
            MessageHandler warmupHandler = new(new InstanceId(34001), warmupBus);
            MessageRegistrationToken warmupToken = MessageRegistrationToken.Create(
                warmupHandler,
                warmupBus
            );
            warmupToken.Dispose();

            long startTimestamp = 0;
            long endTimestamp = 0;
            try
            {
                AllocationProbe.SettleHeapForMeasurement();
                startTimestamp = Stopwatch.GetTimestamp();
                for (int index = 0; index < tokens.Length; index++)
                {
                    tokens[index] = MessageRegistrationToken.Create(handlers[index], buses[index]);
                }
                endTimestamp = Stopwatch.GetTimestamp();
                Assert.AreEqual(
                    ConstructionBatchSize,
                    CountConstructed(tokens),
                    "The construction benchmark must retain every MessageRegistrationToken through the timed pass."
                );
            }
            finally
            {
                DisposeTokens(tokens);
            }

            AllocationProbe.SettleHeapForMeasurement();
            AllocationProbe.AllocationSample sample;
            try
            {
                sample = AllocationProbe.MeasureWithBytes(() =>
                {
                    for (int index = 0; index < tokens.Length; index++)
                    {
                        tokens[index] = MessageRegistrationToken.Create(
                            handlers[index],
                            buses[index]
                        );
                    }
                });
                Assert.AreEqual(
                    ConstructionBatchSize,
                    CountConstructed(tokens),
                    "The construction benchmark must retain every MessageRegistrationToken through the allocation pass."
                );
            }
            finally
            {
                DisposeTokens(tokens);
            }

            return DispatchBenchmarkResult.ForWallClockScenario(
                GetScenarioName(DispatchBenchmarkScenario.MessageRegistrationTokenConstruction1000),
                runIndex: -1,
                sample.Allocations,
                sample.Bytes,
                TimestampDeltaToSeconds(startTimestamp, endTimestamp) * 1000d
            );
        }

        private static void DisposeTokens(MessageRegistrationToken[] tokens)
        {
            Exception firstException = null;
            for (int index = tokens.Length - 1; index >= 0; index--)
            {
                MessageRegistrationToken token = tokens[index];
                tokens[index] = null;
                if (token == null)
                {
                    continue;
                }

                try
                {
                    token.Dispose();
                }
                catch (Exception exception)
                {
                    firstException ??= exception;
                }
            }

            if (firstException != null)
            {
                throw firstException;
            }
        }

        private static int CountConstructed<T>(T[] instances)
            where T : class
        {
            int count = 0;
            for (int index = 0; index < instances.Length; index++)
            {
                if (instances[index] != null)
                {
                    count++;
                }
            }

            return count;
        }

        // Runs a warm, repeatable flood operation over <see cref="WarmFloodTrials"/> trials and
        // returns the MINIMUM wall clock (ms) plus the first trial's allocation count/bytes
        // (deterministic across trials, since each trial does identical work). Each trial builds
        // fresh scope state UNTIMED, GC-quiesces, then times exactly one operation. Used by the
        // warm-JIT floods to replace a noisy single-shot sample with a reproducible floor.
        private static (double minMilliseconds, long allocations, long bytes) MeasureWarmFloodMin(
            Func<BenchmarkRegistrationScope> setUpTrial,
            Action<BenchmarkRegistrationScope> timedOperation
        )
        {
            double minMilliseconds = double.MaxValue;
            long allocations = AllocationProbe.Unmeasured;
            long bytes = AllocationProbe.Unmeasured;
            for (int trial = 0; trial < WarmFloodTrials; trial++)
            {
                using BenchmarkRegistrationScope scope = setUpTrial();
                QuiesceGarbageCollector();
                using AllocationProbe.Window window = AllocationProbe.BeginWindow();
                long startTimestamp = Stopwatch.GetTimestamp();
                timedOperation(scope);
                long endTimestamp = Stopwatch.GetTimestamp();
                AllocationProbe.AllocationSample sample = window.SampleBoth();
                double milliseconds = TimestampDeltaToSeconds(startTimestamp, endTimestamp) * 1000d;
                if (milliseconds < minMilliseconds)
                {
                    minMilliseconds = milliseconds;
                }
                if (trial == 0)
                {
                    allocations = sample.Allocations;
                    bytes = sample.Bytes;
                }
            }

            return (minMilliseconds, allocations, bytes);
        }

        private static DispatchBenchmarkResult MeasureRegistrationFlood()
        {
            Action<MessageRegistrationToken>[] builders = GetRegistrationFloodBuilders();
            // Count managed allocations via the reliable GC.Alloc recorder (the
            // recorder spans the timed region; its overhead is negligible against the
            // JIT-dominated flood). NEVER GC.GetAllocatedBytesForCurrentThread(): it
            // returns 0 for every allocation under Unity's Boehm GC (see AllocationProbe).
            // Single-shot by design (first-touch JIT cannot be re-measured cold); GC-quiesce
            // first so a pending collection cannot land inside the timed window.
            QuiesceGarbageCollector();
            using AllocationProbe.Window window = AllocationProbe.BeginWindow();
            long startTimestamp = Stopwatch.GetTimestamp();
            using (BenchmarkRegistrationScope scope = new())
            {
                for (int index = 0; index < builders.Length; index++)
                {
                    builders[index](scope.PrimaryToken);
                }
            }
            long endTimestamp = Stopwatch.GetTimestamp();
            AllocationProbe.AllocationSample sample = window.SampleBoth();

            return DispatchBenchmarkResult.ForRegistrationScenario(
                GetScenarioName(DispatchBenchmarkScenario.RegistrationFlood1000TypesFromColdBus),
                runIndex: -1,
                sample.Allocations,
                sample.Bytes,
                TimestampDeltaToSeconds(startTimestamp, endTimestamp) * 1000d
            );
        }

        // The JIT-pre-warmed complement to MeasureRegistrationFlood. The cold flood times
        // BOTH the Mono JIT compile of each closed generic AND the registration
        // data-structure work; this scenario isolates the data-structure cost by paying
        // the JIT bill FIRST on a throwaway bus, then timing a fresh-bus registration of
        // the same 1000 builders. Only the JIT-compiled code survives the throwaway scope;
        // the registration state is torn down, so the timed pass registers from a genuinely
        // empty bus -- same shape as MeasureRegistrationFlood, just warm. Under IL2CPP/AOT
        // the generics are precompiled so warm and cold are ~equal; under Mono the warm
        // number is the registration cost with the JIT hitch removed.
        private static DispatchBenchmarkResult MeasureRegistrationFloodWarmJit()
        {
            Action<MessageRegistrationToken>[] builders = GetRegistrationFloodBuilders();

            // JIT pre-warm: register all 1000 builders once on a throwaway bus so the
            // per-closed-generic Mono JIT compile happens here, OUTSIDE the timed region.
            // Dispose tears the registrations down; only the compiled code persists.
            using (BenchmarkRegistrationScope warmupScope = new())
            {
                for (int index = 0; index < builders.Length; index++)
                {
                    builders[index](warmupScope.PrimaryToken);
                }
            }

            // Warm + repeatable: run several trials (fresh empty scope each) and report the
            // MINIMUM wall clock so a single scheduler/GC blip cannot dominate the published
            // number. Each trial times only the 1000-builder registration pass.
            (double minMilliseconds, long allocations, long bytes) = MeasureWarmFloodMin(
                static () => new BenchmarkRegistrationScope(),
                scope =>
                {
                    for (int index = 0; index < builders.Length; index++)
                    {
                        builders[index](scope.PrimaryToken);
                    }
                }
            );

            return DispatchBenchmarkResult.ForRegistrationScenario(
                GetScenarioName(DispatchBenchmarkScenario.RegistrationFlood1000TypesWarmJit),
                runIndex: -1,
                allocations,
                bytes,
                minMilliseconds
            );
        }

        // The per-kind MARGINAL registration cost: how much an ADDITIONAL registration of
        // an already-registered (warm) message type allocates -- the steady-state cost a
        // component pays when it registers another handler. The measured surface includes the
        // registration object, revision-specific token and teardown storage, typed handler
        // storage, and bus refcount updates.
        // Distinct no-op handler delegates are pre-built OUTSIDE the measured window (each
        // captures its index so the compiler cannot fold them to one cached delegate), which
        // (a) keeps the user's handler-delegate allocation out of the measured number and
        // (b) avoids any same-handler refcount-bump fast path, so every measured call is a
        // genuine new registration. The published Standalone IL2CPP leg strips the GC.Alloc
        // profiler, so its allocation columns read n/a; the in-editor PlayMode (Mono) leg
        // supplies the real per-kind registration allocation numbers in the rendered doc.
        private static DispatchBenchmarkResult MeasureRegistrationMarginal(
            DispatchBenchmarkScenario scenario
        )
        {
            switch (scenario)
            {
                case DispatchBenchmarkScenario.UntargetedRegistrationMarginal:
                    return MeasureRegistrationMarginal<SimpleUntargetedMessage>(
                        scenario,
                        static (token, handler) => token.RegisterUntargeted(handler)
                    );
                case DispatchBenchmarkScenario.TargetedRegistrationMarginal:
                    return MeasureRegistrationMarginal<SimpleTargetedMessage>(
                        scenario,
                        static (token, handler) => token.RegisterTargeted(Target, handler)
                    );
                case DispatchBenchmarkScenario.BroadcastRegistrationMarginal:
                    return MeasureRegistrationMarginal<SimpleBroadcastMessage>(
                        scenario,
                        static (token, handler) => token.RegisterBroadcast(Source, handler)
                    );
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
            }
        }

        private static DispatchBenchmarkResult MeasureRegistrationMarginal<T>(
            DispatchBenchmarkScenario scenario,
            Func<
                MessageRegistrationToken,
                MessageHandler.FastHandler<T>,
                MessageRegistrationHandle
            > register
        )
            where T : DxMessaging.Core.IMessage
        {
            int total = RegistrationMarginalWarmup + RegistrationMarginalCount;
            using IDisposable registry = MessageBus.IsolateIdleSweepRegistryForBenchmark();

            // Pre-build distinct handler delegates OUTSIDE the measured window. Each captures
            // its index so the C# compiler cannot collapse them into a single cached static
            // delegate, guaranteeing every registration is a genuine new one.
            MessageHandler.FastHandler<T>[] handlers = new MessageHandler.FastHandler<T>[total];
            for (int index = 0; index < total; index++)
            {
                int captured = index;
                handlers[index] = (ref T message) =>
                {
                    // Reference the captured index so each delegate is a distinct closure
                    // instance (the compiler cannot fold them to one cached static delegate),
                    // guaranteeing every registration is genuinely new rather than a
                    // same-delegate refcount bump. The message is intentionally ignored.
                    _ = captured;
                };
            }
            MessageRegistrationHandle[] warmupHandles = new MessageRegistrationHandle[
                RegistrationMarginalWarmup
            ];

            // Pre-warm the complete registration path, including token-arena growth and the
            // handler-map spill path. This throwaway population is outside both measurement
            // windows; only compiled code and reusable global pool state survive disposal.
            using (BenchmarkRegistrationScope warmupScope = new())
            {
                WarmRegistrationMarginalScope(warmupScope, handlers, warmupHandles, register);
                RegisterMarginalBatch(warmupScope.PrimaryToken, handlers, register);
            }

            // A long window is actively misleading here: each population allocates enough
            // that retaining several of them forces a collection into the clock. Instead,
            // measure fresh, identically warmed populations independently after one heap
            // settle and keep the minimum floor. A collection in any later trial becomes a
            // slow outlier instead of requiring another expensive full-editor collection.
            double milliseconds = double.MaxValue;
            int completedTimingTrials = 0;
            QuiesceGarbageCollector();
            for (int trial = 0; trial < RegistrationMarginalTimingTrials; trial++)
            {
                using BenchmarkRegistrationScope timingScope = new();
                WarmRegistrationMarginalScope(timingScope, handlers, warmupHandles, register);
                long startTimestamp = Stopwatch.GetTimestamp();
                RegisterMarginalBatch(timingScope.PrimaryToken, handlers, register);
                long endTimestamp = Stopwatch.GetTimestamp();
                AssertRegistrationMarginalPopulation(
                    ObserveRegistrationMarginalPopulation(timingScope)
                );
                completedTimingTrials++;
                double trialMilliseconds =
                    TimestampDeltaToSeconds(startTimestamp, endTimestamp) * 1000d;
                if (trialMilliseconds < milliseconds)
                {
                    milliseconds = trialMilliseconds;
                }
            }
            Assert.AreEqual(
                RegistrationMarginalTimingTrials,
                completedTimingTrials,
                "Marginal latency must execute every fresh timing population."
            );

            // Allocation instrumentation is deliberately separate from the latency clock.
            // On Mono the GC.Alloc recorder has measurable hook overhead; timing inside its
            // window made backend comparisons include profiler cost. Repeated fresh
            // populations reject additive warm-editor noise while pairing bytes with the
            // same attempt that produced the minimum exact count. A stripped IL2CPP player
            // has no functional allocation recorder and honestly skips this allocation-only
            // pass while retaining the validated seven-trial latency result.
            AllocationProbe.MinimumMeasurement<RegistrationMarginalPopulation> sample =
                MeasureRegistrationMarginalAllocation(handlers, warmupHandles, register);

            return DispatchBenchmarkResult.ForRegistrationScenario(
                GetScenarioName(scenario),
                runIndex: -1,
                sample.GcAllocations,
                sample.GcAllocatedBytes,
                milliseconds
            );
        }

        private static void WarmRegistrationMarginalScope<T>(
            BenchmarkRegistrationScope scope,
            MessageHandler.FastHandler<T>[] handlers,
            MessageRegistrationHandle[] handles,
            Func<
                MessageRegistrationToken,
                MessageHandler.FastHandler<T>,
                MessageRegistrationHandle
            > register
        )
            where T : DxMessaging.Core.IMessage
        {
            for (int index = 0; index < handles.Length; index++)
            {
                handles[index] = register(scope.PrimaryToken, handlers[index]);
            }
            for (int index = handles.Length - 1; index >= 0; index--)
            {
                scope.PrimaryToken.RemoveRegistration(handles[index]);
            }
        }

        private static void RegisterMarginalBatch<T>(
            MessageRegistrationToken token,
            MessageHandler.FastHandler<T>[] handlers,
            Func<
                MessageRegistrationToken,
                MessageHandler.FastHandler<T>,
                MessageRegistrationHandle
            > register
        )
            where T : DxMessaging.Core.IMessage
        {
            int end = RegistrationMarginalWarmup + RegistrationMarginalCount;
            for (int index = RegistrationMarginalWarmup; index < end; index++)
            {
                _ = register(token, handlers[index]);
            }
        }

        private static AllocationProbe.MinimumMeasurement<RegistrationMarginalPopulation> MeasureRegistrationMarginalAllocation<T>(
            MessageHandler.FastHandler<T>[] handlers,
            MessageRegistrationHandle[] warmupHandles,
            Func<
                MessageRegistrationToken,
                MessageHandler.FastHandler<T>,
                MessageRegistrationHandle
            > register
        )
            where T : DxMessaging.Core.IMessage
        {
            // Reclaim the seven timing populations on every backend. On stripped IL2CPP this
            // is the only cleanup in this helper because allocation probing is unavailable.
            AllocationProbe.SettleHeapForMeasurement();
            if (!AllocationProbe.IsFunctional)
            {
                return new AllocationProbe.MinimumMeasurement<RegistrationMarginalPopulation>(
                    AllocationProbe.Unmeasured,
                    AllocationProbe.Unmeasured,
                    -1,
                    default
                );
            }

            long minimumCount = long.MaxValue;
            long minimumBytes = AllocationProbe.Unmeasured;
            int minimumAttempt = -1;
            int completedAttempts = 0;
            RegistrationMarginalPopulation minimumPopulation = default;
            try
            {
                for (int attempt = 0; attempt < RegistrationMarginalAllocationAttempts; attempt++)
                {
                    using BenchmarkRegistrationScope scope = new();
                    WarmRegistrationMarginalScope(scope, handlers, warmupHandles, register);
                    RegistrationMarginalPopulation population;
                    AllocationProbe.AllocationSample allocation;
                    using (AllocationProbe.Window window = AllocationProbe.BeginWindow())
                    {
                        RegisterMarginalBatch(scope.PrimaryToken, handlers, register);
                        allocation = window.SampleBoth();
                    }
                    population = ObserveRegistrationMarginalPopulation(scope);
                    AssertRegistrationMarginalPopulation(population);
                    completedAttempts++;
                    if (
                        AllocationProbe.ShouldReplaceMinimumAttempt(
                            allocation.Allocations,
                            allocation.Bytes,
                            minimumCount,
                            minimumBytes
                        )
                    )
                    {
                        minimumCount = allocation.Allocations;
                        minimumBytes = allocation.Bytes;
                        minimumAttempt = attempt;
                        minimumPopulation = population;
                    }
                }
            }
            finally
            {
                // Every attempt scope has already been disposed, so this collection reclaims
                // the full registration graphs instead of retaining the final population.
                AllocationProbe.SettleHeapForMeasurement();
            }

            Assert.AreEqual(
                RegistrationMarginalAllocationAttempts,
                completedAttempts,
                "Marginal allocation must execute every fresh measurement population."
            );
            AssertRegistrationMarginalPopulation(minimumPopulation);
            return new AllocationProbe.MinimumMeasurement<RegistrationMarginalPopulation>(
                minimumCount,
                minimumBytes,
                minimumAttempt,
                minimumPopulation
            );
        }

        private static RegistrationMarginalPopulation ObserveRegistrationMarginalPopulation(
            BenchmarkRegistrationScope scope
        )
        {
            return new RegistrationMarginalPopulation(
                scope.PrimaryToken._metadata.Count,
                scope.Bus.RegisteredUntargeted
                    + scope.Bus.RegisteredTargeted
                    + scope.Bus.RegisteredBroadcast
            );
        }

        private static void AssertRegistrationMarginalPopulation(
            RegistrationMarginalPopulation population
        )
        {
            Assert.AreEqual(
                RegistrationMarginalCount,
                population.TokenRegistrations,
                "The measured marginal batch must leave 1,000 live token registrations."
            );
            Assert.AreEqual(
                1,
                population.BusHandlerEntries,
                "The measured marginal batch must reach the bus's single refcounted handler entry."
            );
        }

        private readonly struct RegistrationMarginalPopulation
        {
            internal RegistrationMarginalPopulation(int tokenRegistrations, int busHandlerEntries)
            {
                TokenRegistrations = tokenRegistrations;
                BusHandlerEntries = busHandlerEntries;
            }

            internal int TokenRegistrations { get; }

            internal int BusHandlerEntries { get; }
        }

        // The cold deregistration flood: the JIT-inclusive first-touch cost of DISMANTLING
        // 1000 live registrations -- the teardown counterpart to MeasureRegistrationFlood.
        // The 1000 registrations are staged UNTIMED on a live token (their build cost is what
        // the registration flood measures, not this scenario), then the timed region runs
        // token.UnregisterAll() -- the production deregistration path (InvokeDeregistrationQueue
        // drains one deregistration per staged handler off the bus). On a fresh domain the
        // first UnregisterAll JIT-compiles that path, so the cold flood captures the Mono JIT
        // compile AND the data-structure teardown together; the warm-JIT complement isolates
        // the teardown cost. The scope's own Dispose calls UnregisterAll again, but that is
        // idempotent (the queue is already drained) and untimed.
        private static DispatchBenchmarkResult MeasureDeregistrationFlood()
        {
            Action<MessageRegistrationToken>[] builders = GetRegistrationFloodBuilders();
            using (BenchmarkRegistrationScope scope = new())
            {
                for (int index = 0; index < builders.Length; index++)
                {
                    builders[index](scope.PrimaryToken);
                }

                // Single-shot by design (first-touch JIT of the teardown path); GC-quiesce so a
                // pending collection cannot land inside the timed UnregisterAll.
                QuiesceGarbageCollector();
                using AllocationProbe.Window window = AllocationProbe.BeginWindow();
                long startTimestamp = Stopwatch.GetTimestamp();
                scope.PrimaryToken.UnregisterAll();
                long endTimestamp = Stopwatch.GetTimestamp();
                AllocationProbe.AllocationSample sample = window.SampleBoth();

                return DispatchBenchmarkResult.ForRegistrationScenario(
                    GetScenarioName(DispatchBenchmarkScenario.DeregistrationFlood1000TypesCold),
                    runIndex: -1,
                    sample.Allocations,
                    sample.Bytes,
                    TimestampDeltaToSeconds(startTimestamp, endTimestamp) * 1000d
                );
            }
        }

        // The JIT-pre-warmed complement to MeasureDeregistrationFlood. The cold flood times
        // BOTH the Mono JIT compile of the deregistration path AND the teardown work; this
        // scenario isolates the teardown cost by paying the JIT bill FIRST on a throwaway bus
        // (register then UnregisterAll), then timing UnregisterAll on a fresh, fully-populated
        // token. Only the JIT-compiled code survives the throwaway scope; its registration
        // state is torn down, so the timed pass deregisters a genuinely fresh population --
        // same shape as MeasureDeregistrationFlood, just warm. Under IL2CPP/AOT the generics
        // are precompiled so warm and cold are ~equal; under Mono the warm number is the
        // teardown cost with the JIT hitch removed.
        private static DispatchBenchmarkResult MeasureDeregistrationFloodWarmJit()
        {
            Action<MessageRegistrationToken>[] builders = GetRegistrationFloodBuilders();

            // JIT pre-warm: register AND deregister all 1000 builders once on a throwaway bus
            // so the per-closed-generic Mono JIT compile of BOTH paths happens here, OUTSIDE
            // the timed region. Dispose tears the rest down; only the compiled code persists.
            using (BenchmarkRegistrationScope warmupScope = new())
            {
                for (int index = 0; index < builders.Length; index++)
                {
                    builders[index](warmupScope.PrimaryToken);
                }
                warmupScope.PrimaryToken.UnregisterAll();
            }

            // Warm + repeatable: run several trials and report the MINIMUM wall clock. Each trial
            // registers a fresh 1000-handler population UNTIMED, then times only UnregisterAll.
            (double minMilliseconds, long allocations, long bytes) = MeasureWarmFloodMin(
                () =>
                {
                    BenchmarkRegistrationScope scope = new();
                    for (int index = 0; index < builders.Length; index++)
                    {
                        builders[index](scope.PrimaryToken);
                    }
                    return scope;
                },
                scope => scope.PrimaryToken.UnregisterAll()
            );

            return DispatchBenchmarkResult.ForRegistrationScenario(
                GetScenarioName(DispatchBenchmarkScenario.DeregistrationFlood1000TypesWarmJit),
                runIndex: -1,
                allocations,
                bytes,
                minMilliseconds
            );
        }

        // The cold dispatch flood: the JIT-inclusive first-touch dispatch hitch, stabilized
        // via distinct types. A SINGLE first emit of one message type is pure JIT noise -- it
        // is dominated by the one-time compile of that type's dispatch path and the shared
        // dispatch infrastructure, and a single sample cannot be trusted. So, symmetric with
        // the registration flood, this routes through BenchmarkProtocol.MeasureColdLatency
        // (the cold counterpart to Measure) over 32 trials, one per RegistrationFloodMarkerTypes
        // marker. Each trial spins up a FRESH bus, registers a BY-REF (FastHandler<T>) no-op
        // handler for a DISTINCT closed generic message type (UNTIMED), then times EXACTLY ONE
        // emit of that type -- which JIT-compiles that closed type's fast dispatch path
        // (RunFastHandlers), the SAME path the warm/hot scenarios measure. The MEDIAN of the
        // 32 per-emit samples rejects the single outlier the very first trial carries (the
        // one-time compile of the SHARED dispatch infrastructure lands on whichever type runs
        // first). Registration and scope teardown are untimed: only the first dispatch counts.
        private static DispatchBenchmarkResult MeasureColdFirstDispatch(
            DispatchBenchmarkScenario scenario
        )
        {
            ColdDispatchKind kind = ColdDispatchKindFor(scenario);
            Type[] markerTypes = RegistrationFloodMarkerTypes.All;

            // Build the per-type set-up + single-emit closures BEFORE the trial loop so the
            // reflection (MakeGenericMethod + CreateDelegate) and the delegate allocation
            // never count against any cold sample. Each marker yields a distinct closed
            // generic whose fast dispatch path JIT-compiles on its first emit. This mirrors
            // the GetRegistrationFloodBuilders reflection pattern.
            Func<ColdTrialState>[] setUpActions = new Func<ColdTrialState>[markerTypes.Length];
            BuildColdDispatchClosures(kind, markerTypes, setUpActions);

            ColdLatencyMeasurement measurement = BenchmarkProtocol.MeasureColdLatency(
                markerTypes.Length,
                trialIndex => setUpActions[trialIndex](),
                state => state.Emit(),
                state => state.Dispose()
            );

            return DispatchBenchmarkResult.ForColdLatencyScenario(
                GetScenarioName(scenario),
                runIndex: -1,
                measurement.MedianGcAllocations,
                measurement.MedianGcAllocatedBytes,
                measurement.MedianWallClockMs
            );
        }

        // Build, per closed message type, an UNTIMED set-up delegate that creates a fresh
        // scope, registers a by-ref no-op handler for that closed type, and returns a
        // ColdTrialState whose Emit performs EXACTLY ONE first dispatch. The helper is
        // generic over the closed message type, so each marker yields a distinct closed
        // generic whose fast dispatch path JIT-compiles on its first emit.
        private static void BuildColdDispatchClosures(
            ColdDispatchKind kind,
            Type[] markerTypes,
            Func<ColdTrialState>[] setUpActions
        )
        {
            string setUpHelperName;
            switch (kind)
            {
                case ColdDispatchKind.Untargeted:
                    setUpHelperName = nameof(SetUpColdUntargetedTrial);
                    break;
                case ColdDispatchKind.Targeted:
                    setUpHelperName = nameof(SetUpColdTargetedTrial);
                    break;
                case ColdDispatchKind.Broadcast:
                    setUpHelperName = nameof(SetUpColdBroadcastTrial);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
            }

            MethodInfo setUpHelper = GetColdDispatchHelper(setUpHelperName);
            for (int index = 0; index < markerTypes.Length; index++)
            {
                MethodInfo closedSetUp = setUpHelper.MakeGenericMethod(markerTypes[index]);
                setUpActions[index] =
                    (Func<ColdTrialState>)
                        Delegate.CreateDelegate(typeof(Func<ColdTrialState>), closedSetUp);
            }
        }

        private static MethodInfo GetColdDispatchHelper(string methodName)
        {
            MethodInfo method = typeof(DispatchThroughputBenchmarks).GetMethod(
                methodName,
                BindingFlags.NonPublic | BindingFlags.Static
            );
            if (method == null)
            {
                throw new MissingMethodException(methodName);
            }

            return method;
        }

        private static ColdDispatchKind ColdDispatchKindFor(DispatchBenchmarkScenario scenario)
        {
            switch (scenario)
            {
                case DispatchBenchmarkScenario.UntargetedFirstDispatchCold:
                    return ColdDispatchKind.Untargeted;
                case DispatchBenchmarkScenario.TargetedFirstDispatchCold:
                    return ColdDispatchKind.Targeted;
                case DispatchBenchmarkScenario.BroadcastFirstDispatchCold:
                    return ColdDispatchKind.Broadcast;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
            }
        }

        private static double TimestampDeltaToSeconds(long startTimestamp, long endTimestamp)
        {
            return (endTimestamp - startTimestamp) / (double)Stopwatch.Frequency;
        }

        private static void ConfigureScenario(
            BenchmarkRegistrationScope scope,
            DispatchBenchmarkScenario scenario,
            InvocationCounter handlerInvocations
        )
        {
            switch (scenario)
            {
                case DispatchBenchmarkScenario.EmptyBusDispatch:
                    return;
                case DispatchBenchmarkScenario.UntargetedFloodOneHandler:
                    RegisterUntargeted(scope, handlerInvocations, 0);
                    return;
                case DispatchBenchmarkScenario.UntargetedFloodTwoHandlersOnePriority:
                    for (int index = 0; index < 2; index++)
                    {
                        RegisterUntargeted(scope, handlerInvocations, 0);
                    }
                    return;
                case DispatchBenchmarkScenario.UntargetedFloodThreeHandlersOnePriority:
                    for (int index = 0; index < 3; index++)
                    {
                        RegisterUntargeted(scope, handlerInvocations, 0);
                    }
                    return;
                case DispatchBenchmarkScenario.UntargetedFloodFourHandlersOnePriority:
                    for (int index = 0; index < 4; index++)
                    {
                        RegisterUntargeted(scope, handlerInvocations, 0);
                    }
                    return;
                case DispatchBenchmarkScenario.UntargetedFloodFourHandlersFourPriorities:
                    for (int priority = 0; priority < 4; priority++)
                    {
                        RegisterUntargeted(scope, handlerInvocations, priority);
                    }
                    return;
                case DispatchBenchmarkScenario.UntargetedFloodSixteenHandlersOnePriority:
                    for (int index = 0; index < 16; index++)
                    {
                        RegisterUntargeted(scope, handlerInvocations, 0);
                    }
                    return;
                case DispatchBenchmarkScenario.UntargetedFloodOneInactiveHandler:
                    RegisterUntargeted(scope, handlerInvocations, 0, active: false);
                    return;
                case DispatchBenchmarkScenario.TargetedFloodNoMatchingTarget:
                    RegisterTargeted(scope, handlerInvocations, 0);
                    return;
                case DispatchBenchmarkScenario.TargetedFloodOneListener:
                    RegisterTargeted(scope, handlerInvocations, 0);
                    return;
                case DispatchBenchmarkScenario.TargetedFloodSixteenListeners:
                    for (int index = 0; index < 16; index++)
                    {
                        RegisterTargeted(scope, handlerInvocations, 0);
                    }
                    return;
                case DispatchBenchmarkScenario.BroadcastFloodOneHandler:
                    RegisterBroadcast(scope, handlerInvocations, 0);
                    return;
                case DispatchBenchmarkScenario.InterceptorHeavyFourInterceptors:
                    for (int priority = 0; priority < 4; priority++)
                    {
                        _ =
                            scope.PrimaryToken.RegisterUntargetedInterceptor<SimpleUntargetedMessage>(
                                AllowUntargeted,
                                priority
                            );
                    }
                    RegisterUntargeted(scope, handlerInvocations, 0);
                    return;
                case DispatchBenchmarkScenario.PostProcessingHeavyFourPostProcessors:
                    for (int priority = 0; priority < 4; priority++)
                    {
                        _ =
                            scope.PrimaryToken.RegisterUntargetedPostProcessor<SimpleUntargetedMessage>(
                                CountPostProcessed,
                                priority
                            );
                    }
                    RegisterUntargeted(scope, handlerInvocations, 0);
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
            }

            void CountPostProcessed(ref SimpleUntargetedMessage message)
            {
                handlerInvocations.Increment();
            }
        }

        private static void RegisterUntargeted(
            BenchmarkRegistrationScope scope,
            InvocationCounter handlerInvocations,
            int priority,
            bool active = true
        )
        {
            MessageRegistrationToken token = scope.CreateToken(active);
            _ = token.RegisterUntargeted<SimpleUntargetedMessage>(
                (ref SimpleUntargetedMessage message) => handlerInvocations.Increment(),
                priority
            );
        }

        private static void RegisterTargeted(
            BenchmarkRegistrationScope scope,
            InvocationCounter handlerInvocations,
            int priority
        )
        {
            MessageRegistrationToken token = scope.CreateToken();
            _ = token.RegisterTargeted<SimpleTargetedMessage>(
                Target,
                (ref SimpleTargetedMessage message) => handlerInvocations.Increment(),
                priority
            );
        }

        private static void RegisterBroadcast(
            BenchmarkRegistrationScope scope,
            InvocationCounter handlerInvocations,
            int priority
        )
        {
            MessageRegistrationToken token = scope.CreateToken();
            _ = token.RegisterBroadcast<SimpleBroadcastMessage>(
                Source,
                (ref SimpleBroadcastMessage message) => handlerInvocations.Increment(),
                priority
            );
        }

        private static void EmitMany(MessageBus bus, DispatchBenchmarkScenario scenario, int count)
        {
            switch (scenario)
            {
                case DispatchBenchmarkScenario.EmptyBusDispatch:
                case DispatchBenchmarkScenario.UntargetedFloodOneHandler:
                case DispatchBenchmarkScenario.UntargetedFloodTwoHandlersOnePriority:
                case DispatchBenchmarkScenario.UntargetedFloodThreeHandlersOnePriority:
                case DispatchBenchmarkScenario.UntargetedFloodFourHandlersOnePriority:
                case DispatchBenchmarkScenario.UntargetedFloodFourHandlersFourPriorities:
                case DispatchBenchmarkScenario.UntargetedFloodSixteenHandlersOnePriority:
                case DispatchBenchmarkScenario.UntargetedFloodOneInactiveHandler:
                case DispatchBenchmarkScenario.InterceptorHeavyFourInterceptors:
                case DispatchBenchmarkScenario.PostProcessingHeavyFourPostProcessors:
                    SimpleUntargetedMessage untargeted = new();
                    for (int index = 0; index < count; index++)
                    {
                        bus.UntargetedBroadcast(ref untargeted);
                    }
                    return;
                case DispatchBenchmarkScenario.TargetedFloodNoMatchingTarget:
                    SimpleTargetedMessage missingTargetMessage = new();
                    InstanceId missingTarget = MissingTarget;
                    for (int index = 0; index < count; index++)
                    {
                        bus.TargetedBroadcast(ref missingTarget, ref missingTargetMessage);
                    }
                    return;
                case DispatchBenchmarkScenario.TargetedFloodOneListener:
                case DispatchBenchmarkScenario.TargetedFloodSixteenListeners:
                    SimpleTargetedMessage targeted = new();
                    InstanceId target = Target;
                    for (int index = 0; index < count; index++)
                    {
                        bus.TargetedBroadcast(ref target, ref targeted);
                    }
                    return;
                case DispatchBenchmarkScenario.BroadcastFloodOneHandler:
                    SimpleBroadcastMessage broadcast = new();
                    InstanceId source = Source;
                    for (int index = 0; index < count; index++)
                    {
                        bus.SourcedBroadcast(ref source, ref broadcast);
                    }
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
            }
        }

        internal static DispatchScenarioContractObservation ConfigureAndEmitOnceForContract(
            DispatchBenchmarkScenario scenario
        )
        {
            using BenchmarkRegistrationScope scope = new();
            InvocationCounter handlerInvocations = new();
            ConfigureScenario(scope, scenario, handlerInvocations);
            int registrationBuckets =
                scope.Bus.RegisteredUntargeted
                + scope.Bus.RegisteredTargeted
                + scope.Bus.RegisteredBroadcast
                + scope.Bus.RegisteredInterceptors
                + scope.Bus.RegisteredPostProcessors
                + scope.Bus.RegisteredGlobalAcceptAll;
            EmitMany(scope.Bus, scenario, 1);
            long scenarioFanOut = handlerInvocations.Count;

            handlerInvocations.Reset();
            switch (scenario)
            {
                case DispatchBenchmarkScenario.TargetedFloodNoMatchingTarget:
                    SimpleTargetedMessage targeted = new();
                    InstanceId target = Target;
                    scope.Bus.TargetedBroadcast(ref target, ref targeted);
                    break;
                case DispatchBenchmarkScenario.UntargetedFloodOneInactiveHandler:
                    scope.SetAllHandlersActive(true);
                    EmitMany(scope.Bus, scenario, 1);
                    break;
                default:
                    EmitMany(scope.Bus, scenario, 1);
                    break;
            }

            return new DispatchScenarioContractObservation(
                scenarioFanOut,
                handlerInvocations.Count,
                registrationBuckets
            );
        }

        internal readonly struct DispatchScenarioContractObservation
        {
            internal DispatchScenarioContractObservation(
                long scenarioFanOut,
                long controlFanOut,
                int registrationBuckets
            )
            {
                ScenarioFanOut = scenarioFanOut;
                ControlFanOut = controlFanOut;
                RegistrationBuckets = registrationBuckets;
            }

            internal long ScenarioFanOut { get; }

            internal long ControlFanOut { get; }

            internal int RegistrationBuckets { get; }
        }

        private static bool AllowUntargeted(ref SimpleUntargetedMessage message)
        {
            return true;
        }

        private static Action<MessageRegistrationToken>[] GetRegistrationFloodBuilders()
        {
            if (_registrationFloodBuilders != null)
            {
                return _registrationFloodBuilders;
            }

            MethodInfo builderMethod = typeof(DispatchThroughputBenchmarks).GetMethod(
                nameof(RegisterFloodMessage),
                BindingFlags.NonPublic | BindingFlags.Static
            );
            if (builderMethod == null)
            {
                throw new MissingMethodException(nameof(RegisterFloodMessage));
            }

            Type[] markerTypes = RegistrationFloodMarkerTypes.All;
            List<Action<MessageRegistrationToken>> builders = new(capacity: 1000);
            for (int outerIndex = 0; outerIndex < markerTypes.Length; outerIndex++)
            {
                for (int innerIndex = 0; innerIndex < markerTypes.Length; innerIndex++)
                {
                    Type markerType = typeof(RegistrationFloodMarker<,>).MakeGenericType(
                        markerTypes[outerIndex],
                        markerTypes[innerIndex]
                    );
                    MethodInfo closedMethod = builderMethod.MakeGenericMethod(markerType);
                    builders.Add(
                        (Action<MessageRegistrationToken>)
                            Delegate.CreateDelegate(
                                typeof(Action<MessageRegistrationToken>),
                                closedMethod
                            )
                    );
                    if (builders.Count == 1000)
                    {
                        break;
                    }
                }

                if (builders.Count == 1000)
                {
                    break;
                }
            }

            if (builders.Count < 1000)
            {
                throw new InvalidOperationException(
                    $"Expected at least 1000 marker types for the registration flood, found {builders.Count}."
                );
            }

            _registrationFloodBuilders = builders.ToArray();
            return _registrationFloodBuilders;
        }

        private static string ResolveBaselineOutputPath()
        {
            string configuredPath = Environment.GetEnvironmentVariable(BaselineOutputEnvVar);
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                configuredPath = ".artifacts/perf-baseline.csv";
            }

            if (Path.IsPathRooted(configuredPath))
            {
                return configuredPath;
            }

            string packageRoot = ResolvePackageRoot();
            string baseDirectory = packageRoot ?? ResolveUnityProjectRoot();
            return Path.GetFullPath(Path.Combine(baseDirectory, configuredPath));
        }

        internal static string ResolvePackageRoot()
        {
#if UNITY_EDITOR
            string packageInfoRoot = ResolvePackageInfoRoot(
                typeof(DispatchThroughputBenchmarks).Assembly
            );
            if (packageInfoRoot != null)
            {
                return packageInfoRoot;
            }

            packageInfoRoot = ResolvePackageInfoRoot(typeof(MessageBus).Assembly);
            if (packageInfoRoot != null)
            {
                return packageInfoRoot;
            }
#endif

            string[] roots = { Directory.GetCurrentDirectory(), Application.dataPath };
            for (int index = 0; index < roots.Length; index++)
            {
                string packageRoot = FindPackageRoot(roots[index]);
                if (packageRoot != null)
                {
                    return packageRoot;
                }
            }

            return null;
        }

#if UNITY_EDITOR
        private static string ResolvePackageInfoRoot(Assembly assembly)
        {
            UnityEditor.PackageManager.PackageInfo packageInfo =
                UnityEditor.PackageManager.PackageInfo.FindForAssembly(assembly);
            if (
                packageInfo != null
                && string.Equals(packageInfo.name, PackageName, StringComparison.Ordinal)
                && Directory.Exists(packageInfo.resolvedPath)
            )
            {
                return FindPackageRoot(packageInfo.resolvedPath);
            }

            return null;
        }
#endif

        private static string FindPackageRoot(string startDirectory)
        {
            if (string.IsNullOrWhiteSpace(startDirectory))
            {
                return null;
            }

            DirectoryInfo current = new(startDirectory);
            while (current != null)
            {
                if (IsPackageRoot(current.FullName))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            return null;
        }

        private static bool IsPackageRoot(string directory)
        {
            string packageJsonPath = Path.Combine(directory, "package.json");
            if (!File.Exists(packageJsonPath))
            {
                return false;
            }

            string packageJson = File.ReadAllText(packageJsonPath);
            return Regex.IsMatch(packageJson, $"\"name\"\\s*:\\s*\"{Regex.Escape(PackageName)}\"");
        }

        private static string ResolveUnityProjectRoot()
        {
            string assetsPath = Application.dataPath;
            if (string.IsNullOrWhiteSpace(assetsPath))
            {
                return Directory.GetCurrentDirectory();
            }

            return Directory.GetParent(assetsPath)?.FullName ?? Directory.GetCurrentDirectory();
        }

        private static void WriteBaselineRows(
            string outputPath,
            IReadOnlyList<DispatchBenchmarkResult> results,
            bool replaceAllRows
        )
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

            List<string> rows = replaceAllRows
                ? new List<string>()
                : ReadExistingBaselineRows(outputPath);
            for (int index = 0; index < results.Count; index++)
            {
                DispatchBenchmarkResult result = results[index];
                RemoveMatchingBaselineRow(rows, result);
                rows.Add(result.ToCsvRow());
            }

            rows.Sort(CompareBaselineRows);

            StringBuilder builder = new();
            builder.AppendLine(BaselineCsvHeader);
            for (int index = 0; index < rows.Count; index++)
            {
                builder.AppendLine(rows[index]);
            }

            File.WriteAllText(outputPath, builder.ToString(), new UTF8Encoding(false));
        }

        private static List<string> ReadExistingBaselineRows(string outputPath)
        {
            List<string> rows = new();
            if (!File.Exists(outputPath))
            {
                return rows;
            }

            string[] lines = File.ReadAllLines(outputPath);
            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index];
                if (
                    string.IsNullOrWhiteSpace(line)
                    || line.StartsWith("scenario,", StringComparison.OrdinalIgnoreCase)
                )
                {
                    continue;
                }

                rows.Add(line);
            }

            return rows;
        }

        private static void RemoveMatchingBaselineRow(
            List<string> rows,
            DispatchBenchmarkResult result
        )
        {
            for (int index = rows.Count - 1; index >= 0; index--)
            {
                string[] fields = ParseCsvFields(rows[index]);
                if (
                    fields.Length >= 3
                    && string.Equals(fields[0], result.Scenario, StringComparison.Ordinal)
                    && string.Equals(fields[1], result.Platform, StringComparison.Ordinal)
                    && string.Equals(fields[2], result.Commit, StringComparison.OrdinalIgnoreCase)
                )
                {
                    rows.RemoveAt(index);
                }
            }
        }

        private static int CompareBaselineRows(string left, string right)
        {
            string[] leftFields = ParseCsvFields(left);
            string[] rightFields = ParseCsvFields(right);
            for (int index = 2; index >= 0; index--)
            {
                string leftValue = index < leftFields.Length ? leftFields[index] : string.Empty;
                string rightValue = index < rightFields.Length ? rightFields[index] : string.Empty;
                int comparison = string.CompareOrdinal(leftValue, rightValue);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return string.CompareOrdinal(left, right);
        }

        private static string[] ParseCsvFields(string line)
        {
            List<string> fields = new();
            StringBuilder builder = new();
            bool inQuotes = false;

            for (int index = 0; index < line.Length; index++)
            {
                char value = line[index];
                if (value == '"')
                {
                    if (inQuotes && index + 1 < line.Length && line[index + 1] == '"')
                    {
                        builder.Append('"');
                        index++;
                        continue;
                    }

                    inQuotes = !inQuotes;
                    continue;
                }

                if (value == ',' && !inQuotes)
                {
                    fields.Add(builder.ToString());
                    builder.Clear();
                    continue;
                }

                builder.Append(value);
            }

            fields.Add(builder.ToString());
            return fields.ToArray();
        }

        private static void RegisterFloodMessage<TMarker>(MessageRegistrationToken token)
        {
            _ = token.RegisterUntargeted<RegistrationFloodMessage<TMarker>>(NoOpFloodHandler);
        }

        private static void NoOpFloodHandler<TMarker>(
            ref RegistrationFloodMessage<TMarker> message
        ) { }

        private readonly struct RegistrationFloodMessage<TMarker>
            : DxMessaging.Core.Messages.IUntargetedMessage { }

        // Per-kind cold first-dispatch set-up helpers. Each is generic over the closed
        // message type, so reflection (MakeGenericMethod over a marker) yields a distinct
        // closed generic whose fast dispatch path JIT-compiles on its first emit. The
        // helper (UNTIMED) creates a fresh bus, registers a BY-REF (FastHandler<T>) no-op
        // handler -- the (ref ...) lambda binds the FastHandler<T> overload, so the timed
        // emit JIT-compiles RunFastHandlers, the SAME path the warm/hot scenarios measure --
        // and returns a ColdTrialState whose Emit performs EXACTLY ONE emit (the single
        // timed cold sample). The static Target/Source are read here, OUTSIDE the timed
        // emit, so only the dispatch is measured (parallel to how warm EmitMany hoists them
        // out of the loop). The message structs are nested PRIVATE generics (like
        // RegistrationFloodMessage) so they can never leak into the comparison roster.
        private enum ColdDispatchKind
        {
            Untargeted,
            Targeted,
            Broadcast,
        }

        // Carries one cold trial's fresh scope plus the single-emit delegate the timed
        // operation invokes. Disposing tears the scope (and its registrations) down, untimed.
        private sealed class ColdTrialState : IDisposable
        {
            private readonly BenchmarkRegistrationScope _scope;
            private readonly Action _emit;

            public ColdTrialState(BenchmarkRegistrationScope scope, Action emit)
            {
                _scope = scope;
                _emit = emit;
            }

            public void Emit()
            {
                _emit();
            }

            public void Dispose()
            {
                _scope.Dispose();
            }
        }

        private static ColdTrialState SetUpColdUntargetedTrial<TMarker>()
        {
            BenchmarkRegistrationScope scope = new();
            _ = scope.PrimaryToken.RegisterUntargeted<ColdDispatchUntargetedMessage<TMarker>>(
                (ref ColdDispatchUntargetedMessage<TMarker> message) => { }
            );
            MessageBus bus = scope.Bus;
            return new ColdTrialState(
                scope,
                () =>
                {
                    ColdDispatchUntargetedMessage<TMarker> message = new();
                    bus.UntargetedBroadcast(ref message);
                }
            );
        }

        private static ColdTrialState SetUpColdTargetedTrial<TMarker>()
        {
            BenchmarkRegistrationScope scope = new();
            InstanceId target = Target;
            _ = scope.PrimaryToken.RegisterTargeted<ColdDispatchTargetedMessage<TMarker>>(
                target,
                (ref ColdDispatchTargetedMessage<TMarker> message) => { }
            );
            MessageBus bus = scope.Bus;
            return new ColdTrialState(
                scope,
                () =>
                {
                    ColdDispatchTargetedMessage<TMarker> message = new();
                    InstanceId localTarget = target;
                    bus.TargetedBroadcast(ref localTarget, ref message);
                }
            );
        }

        private static ColdTrialState SetUpColdBroadcastTrial<TMarker>()
        {
            BenchmarkRegistrationScope scope = new();
            InstanceId source = Source;
            _ = scope.PrimaryToken.RegisterBroadcast<ColdDispatchBroadcastMessage<TMarker>>(
                source,
                (ref ColdDispatchBroadcastMessage<TMarker> message) => { }
            );
            MessageBus bus = scope.Bus;
            return new ColdTrialState(
                scope,
                () =>
                {
                    ColdDispatchBroadcastMessage<TMarker> message = new();
                    InstanceId localSource = source;
                    bus.SourcedBroadcast(ref localSource, ref message);
                }
            );
        }

        private readonly struct ColdDispatchUntargetedMessage<TMarker>
            : DxMessaging.Core.Messages.IUntargetedMessage { }

        private readonly struct ColdDispatchTargetedMessage<TMarker>
            : DxMessaging.Core.Messages.ITargetedMessage { }

        private readonly struct ColdDispatchBroadcastMessage<TMarker>
            : DxMessaging.Core.Messages.IBroadcastMessage { }

        private readonly struct RegistrationFloodMarker<TOuter, TInner> { }

        private static class RegistrationFloodMarkerTypes
        {
            public static readonly Type[] All =
            {
                typeof(Marker00),
                typeof(Marker01),
                typeof(Marker02),
                typeof(Marker03),
                typeof(Marker04),
                typeof(Marker05),
                typeof(Marker06),
                typeof(Marker07),
                typeof(Marker08),
                typeof(Marker09),
                typeof(Marker10),
                typeof(Marker11),
                typeof(Marker12),
                typeof(Marker13),
                typeof(Marker14),
                typeof(Marker15),
                typeof(Marker16),
                typeof(Marker17),
                typeof(Marker18),
                typeof(Marker19),
                typeof(Marker20),
                typeof(Marker21),
                typeof(Marker22),
                typeof(Marker23),
                typeof(Marker24),
                typeof(Marker25),
                typeof(Marker26),
                typeof(Marker27),
                typeof(Marker28),
                typeof(Marker29),
                typeof(Marker30),
                typeof(Marker31),
            };

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

            private readonly struct Marker16 { }

            private readonly struct Marker17 { }

            private readonly struct Marker18 { }

            private readonly struct Marker19 { }

            private readonly struct Marker20 { }

            private readonly struct Marker21 { }

            private readonly struct Marker22 { }

            private readonly struct Marker23 { }

            private readonly struct Marker24 { }

            private readonly struct Marker25 { }

            private readonly struct Marker26 { }

            private readonly struct Marker27 { }

            private readonly struct Marker28 { }

            private readonly struct Marker29 { }

            private readonly struct Marker30 { }

            private readonly struct Marker31 { }
        }

        private sealed class InvocationCounter
        {
            public long Count { get; private set; }

            public void Increment()
            {
                Count++;
            }

            public void Reset()
            {
                Count = 0;
            }
        }

        private sealed class BenchmarkRegistrationScope : IDisposable
        {
            private readonly List<MessageRegistrationToken> _tokens = new();
            private readonly List<MessageHandler> _handlers = new();
            private int _nextOwner = 32000;

            public BenchmarkRegistrationScope()
            {
                // Benchmark the production diagnostics-off path regardless of the host
                // editor's current global diagnostics setting. Editor preferences are
                // mutable and otherwise turn a zero-allocation dispatch benchmark into
                // a measurement of diagnostic history recording.
                Bus = new MessageBus { DiagnosticsMode = false };
                PrimaryToken = CreateToken();
            }

            public MessageBus Bus { get; }

            public MessageRegistrationToken PrimaryToken { get; }

            public MessageRegistrationToken CreateToken(bool active = true)
            {
                MessageHandler handler = new(new InstanceId(_nextOwner++), Bus) { active = active };
                MessageRegistrationToken token = MessageRegistrationToken.Create(handler, Bus);
                token.DiagnosticMode = false;
                token.Enable();
                _handlers.Add(handler);
                _tokens.Add(token);
                return token;
            }

            public void SetAllHandlersActive(bool active)
            {
                for (int index = 0; index < _handlers.Count; index++)
                {
                    _handlers[index].active = active;
                }
            }

            public void Dispose()
            {
                for (int index = _tokens.Count - 1; index >= 0; index--)
                {
                    _tokens[index].UnregisterAll();
                    _tokens[index].Dispose();
                }
            }
        }
    }

    public readonly struct DispatchBenchmarkResult
    {
        private DispatchBenchmarkResult(
            string scenario,
            string platform,
            string commit,
            int runIndex,
            double emitsPerSecond,
            long gcAllocations,
            long gcAllocatedBytes,
            double wallClockMs,
            bool isRegistrationScenario
        )
        {
            Scenario = scenario;
            Platform = platform;
            Commit = commit;
            RunIndex = runIndex;
            EmitsPerSecond = emitsPerSecond;
            GcAllocations = gcAllocations;
            GcAllocatedBytes = gcAllocatedBytes;
            WallClockMs = wallClockMs;
            IsRegistrationScenario = isRegistrationScenario;
        }

        public string Scenario { get; }

        public string Platform { get; }

        public string Commit { get; }

        public int RunIndex { get; }

        public double EmitsPerSecond { get; }

        /// <summary>
        /// Managed allocation CALL count for this row (over one steady-state batch, or
        /// the median across cold trials), or <see cref="AllocationProbe.Unmeasured"/>
        /// (<c>-1</c>) when no reliable allocation probe exists on this backend. A
        /// reported <c>0</c> is a measured zero, never a fabricated one. Renamed from
        /// the former <c>AllocatedBytesDelta</c>, whose
        /// <c>GC.GetAllocatedBytesForCurrentThread()</c> source returns <c>0</c> for
        /// every allocation under Unity's Boehm GC (see <see cref="AllocationProbe"/>).
        /// </summary>
        public long GcAllocations { get; }

        /// <summary>
        /// Total managed allocation BYTES for this row (over one steady-state batch, or the
        /// median across cold trials), or <see cref="AllocationProbe.Unmeasured"/>
        /// (<c>-1</c>) when no reliable byte probe exists on this backend (rendered
        /// <c>n/a</c>, never a fabricated <c>0</c>). Measured via the live
        /// <c>"GC Allocated In Frame"</c> profiler counter delta (see
        /// <see cref="AllocationProbe"/>): byte-exact and collection-immune. The byte
        /// companion to <see cref="GcAllocations"/>; the count remains the canonical
        /// zero-alloc signal and the regression gate, bytes are reported for magnitude.
        /// </summary>
        public long GcAllocatedBytes { get; }

        public double WallClockMs { get; }

        public bool IsRegistrationScenario { get; }

        /// <summary>
        /// True when this result carries a wall-clock (latency) metric rather than a
        /// throughput metric -- that is, whenever <see cref="EmitsPerSecond"/> is 0. This
        /// is the unifying predicate renderers and the regression gate use to identify
        /// wall-clock rows (the cold/warm registration and deregistration floods AND the
        /// cold first-dispatch scenarios). <see cref="IsRegistrationScenario"/> is the
        /// narrower flag retained for the registration/deregistration floods specifically;
        /// every flood is a wall-clock scenario, but not every wall-clock scenario is a
        /// flood (the cold first-dispatch scenarios are not).
        /// </summary>
        public bool IsWallClockScenario => EmitsPerSecond == 0;

        public static DispatchBenchmarkResult ForEmitScenario(
            string scenario,
            int runIndex,
            double emitsPerSecond,
            long gcAllocations,
            long gcAllocatedBytes,
            double wallClockMs
        )
        {
            return new DispatchBenchmarkResult(
                scenario,
                ResolvePlatform(),
                ResolveCommit(),
                runIndex,
                emitsPerSecond,
                gcAllocations,
                gcAllocatedBytes,
                wallClockMs,
                isRegistrationScenario: false
            );
        }

        public static DispatchBenchmarkResult ForRegistrationScenario(
            string scenario,
            int runIndex,
            long gcAllocations,
            long gcAllocatedBytes,
            double wallClockMs
        )
        {
            return new DispatchBenchmarkResult(
                scenario,
                ResolvePlatform(),
                ResolveCommit(),
                runIndex,
                emitsPerSecond: 0,
                gcAllocations,
                gcAllocatedBytes,
                wallClockMs,
                isRegistrationScenario: true
            );
        }

        /// <summary>
        /// Builds a generic wall-clock result for one-time work that is not itself a
        /// registration scenario, such as bus and token construction.
        /// </summary>
        public static DispatchBenchmarkResult ForWallClockScenario(
            string scenario,
            int runIndex,
            long gcAllocations,
            long gcAllocatedBytes,
            double wallClockMs
        )
        {
            return new DispatchBenchmarkResult(
                scenario,
                ResolvePlatform(),
                ResolveCommit(),
                runIndex,
                emitsPerSecond: 0,
                gcAllocations,
                gcAllocatedBytes,
                wallClockMs,
                isRegistrationScenario: false
            );
        }

        /// <summary>
        /// Builds a cold-latency result: a wall-clock (median) measurement with no
        /// throughput, used by the cold first-dispatch scenarios. Like the registration
        /// scenarios it sets <c>emitsPerSecond = 0</c>, so it is a wall-clock row
        /// (<see cref="IsWallClockScenario"/>) that the JS regression gate auto-excludes
        /// (<c>render-perf-deltas.js</c> treats <c>baselineEmits &lt;= 0</c> as non-gating).
        /// It is NOT a registration scenario, so <see cref="IsRegistrationScenario"/> stays
        /// false.
        /// </summary>
        public static DispatchBenchmarkResult ForColdLatencyScenario(
            string scenario,
            int runIndex,
            long medianGcAllocations,
            long medianGcAllocatedBytes,
            double medianWallClockMs
        )
        {
            return new DispatchBenchmarkResult(
                scenario,
                ResolvePlatform(),
                ResolveCommit(),
                runIndex,
                emitsPerSecond: 0,
                medianGcAllocations,
                medianGcAllocatedBytes,
                medianWallClockMs,
                isRegistrationScenario: false
            );
        }

        public string ToCsvRow()
        {
            return string.Join(
                ",",
                EscapeCsv(Scenario),
                EscapeCsv(Platform),
                EscapeCsv(Commit),
                RunIndex.ToString(CultureInfo.InvariantCulture),
                EmitsPerSecond.ToString("F3", CultureInfo.InvariantCulture),
                GcAllocations.ToString(CultureInfo.InvariantCulture),
                WallClockMs.ToString("F3", CultureInfo.InvariantCulture),
                GcAllocatedBytes.ToString(CultureInfo.InvariantCulture)
            );
        }

        public string ToStructuredLog()
        {
            return "{"
                + $"scenario:\"{Scenario}\", "
                + $"platform:\"{Platform}\", "
                + $"commit:\"{Commit}\", "
                + $"runIndex:{RunIndex.ToString(CultureInfo.InvariantCulture)}, "
                + $"emitsPerSec:{EmitsPerSecond.ToString("F3", CultureInfo.InvariantCulture)}, "
                + $"gcAllocations:{GcAllocations.ToString(CultureInfo.InvariantCulture)}, "
                + $"wallClockMs:{WallClockMs.ToString("F3", CultureInfo.InvariantCulture)}, "
                + $"gcAllocatedBytes:{GcAllocatedBytes.ToString(CultureInfo.InvariantCulture)}"
                + "}";
        }

        private static string ResolvePlatform()
        {
            return $"{ResolveExecutionTarget()} {ResolveScriptingBackend()} {ResolveArchitecture()} {ResolveBuildConfiguration()} ({Application.platform}; Unity {Application.unityVersion})";
        }

        private static string ResolveExecutionTarget()
        {
#if UNITY_EDITOR
            return UnityEngine.Application.isPlaying ? "Editor PlayMode" : "Editor EditMode";
#elif UNITY_STANDALONE
            return "Standalone";
#else
            return Application.platform.ToString();
#endif
        }

        private static string ResolveScriptingBackend()
        {
#if ENABLE_IL2CPP
            return "IL2CPP";
#elif ENABLE_MONO
            return "Mono";
#else
            return Type.GetType("Mono.Runtime", throwOnError: false) == null
                ? "UnknownBackend"
                : "Mono";
#endif
        }

        private static string ResolveArchitecture()
        {
            return IntPtr.Size == 8 ? "x64" : "x86";
        }

        private static string ResolveBuildConfiguration()
        {
#if UNITY_EDITOR
            return
                UnityEditor.Compilation.CompilationPipeline.codeOptimization
                == UnityEditor.Compilation.CodeOptimization.Release
                ? "Release"
                : "Debug";
#else
            return Debug.isDebugBuild ? "Debug" : "Release";
#endif
        }

        private static string ResolveCommit()
        {
            string commit = Environment.GetEnvironmentVariable("DX_PERF_COMMIT");
            if (!string.IsNullOrWhiteSpace(commit))
            {
                return commit;
            }

            commit = Environment.GetEnvironmentVariable("GITHUB_SHA");
            if (!string.IsNullOrWhiteSpace(commit))
            {
                return commit;
            }

            commit = ResolveGitHeadCommit(DispatchThroughputBenchmarks.ResolvePackageRoot());
            return string.IsNullOrWhiteSpace(commit) ? "local" : commit;
        }

        private static string ResolveGitHeadCommit(string packageRoot)
        {
            if (string.IsNullOrWhiteSpace(packageRoot))
            {
                return null;
            }

            string gitPath = Path.Combine(packageRoot, ".git");
            if (File.Exists(gitPath))
            {
                string gitFile = File.ReadAllText(gitPath).Trim();
                const string GitDirPrefix = "gitdir:";
                if (gitFile.StartsWith(GitDirPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    gitPath = gitFile.Substring(GitDirPrefix.Length).Trim();
                    if (!Path.IsPathRooted(gitPath))
                    {
                        gitPath = Path.GetFullPath(Path.Combine(packageRoot, gitPath));
                    }
                }
            }

            string headPath = Path.Combine(gitPath, "HEAD");
            if (!File.Exists(headPath))
            {
                return null;
            }

            string head = File.ReadAllText(headPath).Trim();
            string commonGitPath = ResolveCommonGitPath(gitPath);
            const string RefPrefix = "ref:";
            if (!head.StartsWith(RefPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrWhiteSpace(head) ? null : head;
            }

            string refName = head.Substring(RefPrefix.Length).Trim();
            string commit =
                ReadGitRefCommit(gitPath, refName) ?? ReadGitRefCommit(commonGitPath, refName);
            return string.IsNullOrWhiteSpace(commit) ? null : commit;
        }

        private static string ResolveCommonGitPath(string gitPath)
        {
            string commonDirPath = Path.Combine(gitPath, "commondir");
            if (!File.Exists(commonDirPath))
            {
                return gitPath;
            }

            string commonDir = File.ReadAllText(commonDirPath).Trim();
            if (string.IsNullOrWhiteSpace(commonDir))
            {
                return gitPath;
            }

            return Path.IsPathRooted(commonDir)
                ? commonDir
                : Path.GetFullPath(Path.Combine(gitPath, commonDir));
        }

        private static string ReadGitRefCommit(string gitPath, string refName)
        {
            if (string.IsNullOrWhiteSpace(gitPath))
            {
                return null;
            }

            string normalizedRefName = refName.Replace('/', Path.DirectorySeparatorChar);
            string refPath = Path.Combine(gitPath, normalizedRefName);
            if (File.Exists(refPath))
            {
                string commit = File.ReadAllText(refPath).Trim();
                if (!string.IsNullOrWhiteSpace(commit))
                {
                    return commit;
                }
            }

            string packedRefsPath = Path.Combine(gitPath, "packed-refs");
            if (!File.Exists(packedRefsPath))
            {
                return null;
            }

            string[] packedRefs = File.ReadAllLines(packedRefsPath);
            for (int index = 0; index < packedRefs.Length; index++)
            {
                string line = packedRefs[index];
                if (line.Length == 0 || line[0] == '#' || line[0] == '^')
                {
                    continue;
                }

                int separatorIndex = line.IndexOf(' ');
                if (
                    separatorIndex > 0
                    && string.Equals(
                        line.Substring(separatorIndex + 1),
                        refName,
                        StringComparison.Ordinal
                    )
                )
                {
                    return line.Substring(0, separatorIndex);
                }
            }

            return null;
        }

        private static string EscapeCsv(string value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0)
            {
                return value;
            }

            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
#endif
