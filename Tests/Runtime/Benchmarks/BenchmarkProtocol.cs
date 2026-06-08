#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Benchmarks
{
    using System;
    using System.Diagnostics;

    /// <summary>
    /// Single source of truth for benchmark measurement methodology shared by every
    /// DxMessaging benchmark suite (dispatch throughput, editor benchmarks, library
    /// comparisons). Warm up, then measure ONE continuous window of
    /// <see cref="MeasurementSeconds"/> seconds, counting total operations and the GC
    /// allocation delta over the same window. Throughput is total operations divided
    /// by the measured elapsed time, never a median of resampled sub-windows.
    /// </summary>
    public static class BenchmarkProtocol
    {
        public const int MeasurementSeconds = 5;
        public const int WarmupEmits = 10_000;
        public const int BatchSize = 10_000;

        public static readonly TimeSpan MeasurementWindow = TimeSpan.FromSeconds(
            MeasurementSeconds
        );

        private static readonly long MeasurementWindowTicks = (long)(
            Stopwatch.Frequency * MeasurementWindow.TotalSeconds
        );

        /// <summary>
        /// Run <paramref name="warmup"/> once, then invoke <paramref name="emitBatch"/>
        /// repeatedly until the measurement window elapses. <paramref name="emitBatch"/>
        /// returns the number of operations it performed; the sum is the total. GC bytes
        /// are sampled immediately before the first measured batch and immediately after
        /// the last, so allocation is attributed to the same window as throughput.
        /// </summary>
        public static BenchmarkMeasurement Measure(Action warmup, Func<int> emitBatch)
        {
            if (emitBatch == null)
            {
                throw new ArgumentNullException(nameof(emitBatch));
            }

            warmup?.Invoke();

            long startAllocated = GC.GetAllocatedBytesForCurrentThread();
            long startTimestamp = Stopwatch.GetTimestamp();
            long endTimestamp = startTimestamp;
            long totalOperations = 0;
            do
            {
                totalOperations += emitBatch();
                endTimestamp = Stopwatch.GetTimestamp();
            } while (endTimestamp - startTimestamp < MeasurementWindowTicks);
            long endAllocated = GC.GetAllocatedBytesForCurrentThread();

            double elapsedSeconds = (endTimestamp - startTimestamp) / (double)Stopwatch.Frequency;
            double operationsPerSecond = totalOperations / Math.Max(elapsedSeconds, double.Epsilon);
            return new BenchmarkMeasurement(
                totalOperations,
                elapsedSeconds,
                operationsPerSecond,
                endAllocated - startAllocated
            );
        }
    }

    /// <summary>Result of a single <see cref="BenchmarkProtocol.Measure"/> window.</summary>
    public readonly struct BenchmarkMeasurement
    {
        public BenchmarkMeasurement(
            long totalOperations,
            double elapsedSeconds,
            double operationsPerSecond,
            long allocatedBytesDelta
        )
        {
            TotalOperations = totalOperations;
            ElapsedSeconds = elapsedSeconds;
            OperationsPerSecond = operationsPerSecond;
            AllocatedBytesDelta = allocatedBytesDelta;
        }

        public long TotalOperations { get; }

        public double ElapsedSeconds { get; }

        public double OperationsPerSecond { get; }

        public long AllocatedBytesDelta { get; }
    }

    /// <summary>
    /// Metadata for the dispatch-throughput scenarios. <see cref="Key"/> returns the
    /// STABLE machine identifier used by the baseline CSV, the perf-doc renderer, and
    /// the regression gate; it must never change. <see cref="DisplayName"/> returns the
    /// human-readable label shown in rendered docs and PR comments; it is presentation
    /// only and is never used as a join key.
    /// </summary>
    public static class DispatchBenchmarkScenarios
    {
        public static readonly DispatchBenchmarkScenario[] All = (DispatchBenchmarkScenario[])
            Enum.GetValues(typeof(DispatchBenchmarkScenario));

        /// <summary>
        /// Per-scenario warm-up emit count. Every scenario keeps the shared
        /// <see cref="BenchmarkProtocol.WarmupEmits"/> default except the cold-bus
        /// registration flood, which must measure first-touch registration cost and so
        /// performs no warm-up flood (0).
        /// </summary>
        public static int WarmupEmits(DispatchBenchmarkScenario scenario)
        {
            // The registration flood is a cold path that bypasses warm-up entirely via
            // MeasureRegistrationFlood, so this 0 branch is defensive: it keeps the
            // contract correct (no warm-up flood for first-touch registration) should a
            // future caller route the flood through the shared warm-up helper.
            return scenario == DispatchBenchmarkScenario.RegistrationFlood1000TypesFromColdBus
                ? 0
                : BenchmarkProtocol.WarmupEmits;
        }

        public static string Key(DispatchBenchmarkScenario scenario)
        {
            return scenario switch
            {
                DispatchBenchmarkScenario.UntargetedFloodOneHandler => "UntargetedFlood_OneHandler",
                DispatchBenchmarkScenario.UntargetedFloodFourHandlersOnePriority =>
                    "UntargetedFlood_FourHandlers_OnePriority",
                DispatchBenchmarkScenario.UntargetedFloodFourHandlersFourPriorities =>
                    "UntargetedFlood_FourHandlers_FourPriorities",
                DispatchBenchmarkScenario.TargetedFloodOneListener => "TargetedFlood_OneListener",
                DispatchBenchmarkScenario.TargetedFloodSixteenListeners =>
                    "TargetedFlood_SixteenListeners",
                DispatchBenchmarkScenario.BroadcastFloodOneHandler => "BroadcastFlood_OneHandler",
                DispatchBenchmarkScenario.InterceptorHeavyFourInterceptors =>
                    "InterceptorHeavy_FourInterceptors",
                DispatchBenchmarkScenario.PostProcessingHeavyFourPostProcessors =>
                    "PostProcessingHeavy_FourPostProcessors",
                DispatchBenchmarkScenario.RegistrationFlood1000TypesFromColdBus =>
                    "RegistrationFlood_1000Types_FromColdBus",
                _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
            };
        }

        public static string DisplayName(DispatchBenchmarkScenario scenario)
        {
            return scenario switch
            {
                DispatchBenchmarkScenario.UntargetedFloodOneHandler =>
                    "Untargeted Flood (One Handler)",
                DispatchBenchmarkScenario.UntargetedFloodFourHandlersOnePriority =>
                    "Untargeted Flood (Four Handlers, One Priority)",
                DispatchBenchmarkScenario.UntargetedFloodFourHandlersFourPriorities =>
                    "Untargeted Flood (Four Handlers, Four Priorities)",
                DispatchBenchmarkScenario.TargetedFloodOneListener =>
                    "Targeted Flood (One Listener)",
                DispatchBenchmarkScenario.TargetedFloodSixteenListeners =>
                    "Targeted Flood (Sixteen Listeners)",
                DispatchBenchmarkScenario.BroadcastFloodOneHandler =>
                    "Broadcast Flood (One Handler)",
                DispatchBenchmarkScenario.InterceptorHeavyFourInterceptors =>
                    "Interceptor Heavy (Four Interceptors)",
                DispatchBenchmarkScenario.PostProcessingHeavyFourPostProcessors =>
                    "Post-Processing Heavy (Four Post-Processors)",
                DispatchBenchmarkScenario.RegistrationFlood1000TypesFromColdBus =>
                    "Registration Flood (1000 Types, Cold Bus)",
                _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
            };
        }
    }
}
#endif
