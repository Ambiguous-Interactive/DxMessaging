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

        /// <summary>
        /// The COLD counterpart to <see cref="Measure"/>. Where <see cref="Measure"/>
        /// reports steady-state throughput over one warmed window, this runs
        /// <paramref name="trials"/> single-shot trials and reports the MEDIAN wall-clock
        /// and median allocation across them. Each trial is a single first-touch
        /// execution (JIT-inclusive under Mono), so there is no warm-up and no window:
        /// the timed operation runs exactly once per trial and is timed end to end. The
        /// median (not the mean) is the headline because cold latency is right-skewed --
        /// one GC or scheduler blip must not move the reported number.
        ///
        /// Each trial i prepares FRESH state via <paramref name="setUpTrial"/> (UNTIMED;
        /// the <c>i</c> argument lets the caller pick a DISTINCT closed generic type per
        /// trial so every trial JIT-compiles its own first-touch path), then times
        /// EXACTLY ONE <paramref name="timedOperation"/> on that state, then disposes the
        /// state via <paramref name="tearDownTrial"/> (UNTIMED). Both the wall clock and
        /// the allocation delta are sampled around only the timed operation here, so the
        /// caller cannot accidentally fold setup or teardown into the cold sample.
        /// </summary>
        /// <typeparam name="TState">Per-trial state produced by setup and consumed by the timed op + teardown.</typeparam>
        /// <param name="trials">Number of single-shot trials; the headline is the median across them.</param>
        /// <param name="setUpTrial">Builds fresh state for trial <c>i</c> (UNTIMED). Use <c>i</c> to pick a distinct closed type.</param>
        /// <param name="timedOperation">The ONE operation timed per trial.</param>
        /// <param name="tearDownTrial">Disposes the trial's state (UNTIMED).</param>
        public static ColdLatencyMeasurement MeasureColdLatency<TState>(
            int trials,
            Func<int, TState> setUpTrial,
            Action<TState> timedOperation,
            Action<TState> tearDownTrial
        )
        {
            if (trials <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(trials),
                    trials,
                    "Cold-latency trial count must be positive."
                );
            }

            if (setUpTrial == null)
            {
                throw new ArgumentNullException(nameof(setUpTrial));
            }

            if (timedOperation == null)
            {
                throw new ArgumentNullException(nameof(timedOperation));
            }

            if (tearDownTrial == null)
            {
                throw new ArgumentNullException(nameof(tearDownTrial));
            }

            double[] wallClockSamples = new double[trials];
            long[] allocatedSamples = new long[trials];
            for (int index = 0; index < trials; index++)
            {
                TState state = setUpTrial(index);
                try
                {
                    long startAllocated = GC.GetAllocatedBytesForCurrentThread();
                    long startTimestamp = Stopwatch.GetTimestamp();
                    timedOperation(state);
                    long endTimestamp = Stopwatch.GetTimestamp();
                    long endAllocated = GC.GetAllocatedBytesForCurrentThread();
                    wallClockSamples[index] =
                        (endTimestamp - startTimestamp) / (double)Stopwatch.Frequency * 1000d;
                    allocatedSamples[index] = endAllocated - startAllocated;
                }
                finally
                {
                    tearDownTrial(state);
                }
            }

            return new ColdLatencyMeasurement(
                Median(wallClockSamples),
                Median(allocatedSamples),
                trials
            );
        }

        /// <summary>
        /// Median of a non-empty wall-clock sample set. For an even count it averages the
        /// two middle elements so a two-trial set still rejects neither sample outright.
        /// The input is copied before sorting so the caller's array is left untouched.
        /// </summary>
        public static double Median(double[] samples)
        {
            if (samples == null)
            {
                throw new ArgumentNullException(nameof(samples));
            }

            if (samples.Length == 0)
            {
                throw new ArgumentException("Cannot take the median of an empty set.");
            }

            double[] sorted = (double[])samples.Clone();
            Array.Sort(sorted);
            int middle = sorted.Length / 2;
            if (sorted.Length % 2 == 1)
            {
                return sorted[middle];
            }

            return (sorted[middle - 1] + sorted[middle]) / 2d;
        }

        /// <summary>
        /// Median of a non-empty allocation sample set. For an even count it returns the
        /// overflow-safe integer midpoint of the two middle elements (lower + (upper - lower)
        /// / 2) so the reported allocation stays integral. The input is copied before sorting.
        /// </summary>
        public static long Median(long[] samples)
        {
            if (samples == null)
            {
                throw new ArgumentNullException(nameof(samples));
            }

            if (samples.Length == 0)
            {
                throw new ArgumentException("Cannot take the median of an empty set.");
            }

            long[] sorted = (long[])samples.Clone();
            Array.Sort(sorted);
            int middle = sorted.Length / 2;
            if (sorted.Length % 2 == 1)
            {
                return sorted[middle];
            }

            // Overflow-safe integer midpoint of the two middle samples. Allocation deltas are
            // non-negative and sorted ascending, so (upper - lower) cannot overflow and the
            // midpoint is exact without converting through double (which rounds large longs up).
            long lower = sorted[middle - 1];
            long upper = sorted[middle];
            return lower + ((upper - lower) / 2);
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
    /// Result of a <see cref="BenchmarkProtocol.MeasureColdLatency"/> run: the MEDIAN
    /// wall-clock milliseconds and median allocation delta across <see cref="Trials"/>
    /// single-shot cold trials. Cold latency is right-skewed, so the median is the
    /// reported headline rather than the mean.
    /// </summary>
    public readonly struct ColdLatencyMeasurement
    {
        public ColdLatencyMeasurement(
            double medianWallClockMs,
            long medianAllocatedBytesDelta,
            int trials
        )
        {
            MedianWallClockMs = medianWallClockMs;
            MedianAllocatedBytesDelta = medianAllocatedBytesDelta;
            Trials = trials;
        }

        public double MedianWallClockMs { get; }

        public long MedianAllocatedBytesDelta { get; }

        public int Trials { get; }
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
        /// registration flood and the four cold/warm-JIT latency scenarios, which measure
        /// one-time or first-touch cost and so perform no warm-up flood (0).
        /// </summary>
        public static int WarmupEmits(DispatchBenchmarkScenario scenario)
        {
            // The registration floods and cold-dispatch scenarios are cold/latency paths
            // measured outside the shared warm-up helper (MeasureRegistrationFlood,
            // MeasureRegistrationFloodWarmJit, MeasureColdFirstDispatch). The 0 branches
            // are defensive: they keep the contract correct (no warm-up flood for
            // first-touch / one-time cost) should a future caller route any of them
            // through the shared warm-up helper. The warm-JIT flood pre-warms the JIT by
            // registering on a throwaway bus, not by flooding emits, so it is 0 too.
            switch (scenario)
            {
                case DispatchBenchmarkScenario.RegistrationFlood1000TypesFromColdBus:
                case DispatchBenchmarkScenario.RegistrationFlood1000TypesWarmJit:
                case DispatchBenchmarkScenario.UntargetedFirstDispatchCold:
                case DispatchBenchmarkScenario.TargetedFirstDispatchCold:
                case DispatchBenchmarkScenario.BroadcastFirstDispatchCold:
                    return 0;
                default:
                    return BenchmarkProtocol.WarmupEmits;
            }
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
                DispatchBenchmarkScenario.RegistrationFlood1000TypesWarmJit =>
                    "RegistrationFlood_1000Types_WarmJit",
                DispatchBenchmarkScenario.UntargetedFirstDispatchCold =>
                    "UntargetedFirstDispatch_Cold",
                DispatchBenchmarkScenario.TargetedFirstDispatchCold => "TargetedFirstDispatch_Cold",
                DispatchBenchmarkScenario.BroadcastFirstDispatchCold =>
                    "BroadcastFirstDispatch_Cold",
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
                DispatchBenchmarkScenario.RegistrationFlood1000TypesWarmJit =>
                    "Registration Flood (1000 Types, Warm JIT)",
                DispatchBenchmarkScenario.UntargetedFirstDispatchCold =>
                    "Untargeted First Dispatch (Cold, Distinct Types)",
                DispatchBenchmarkScenario.TargetedFirstDispatchCold =>
                    "Targeted First Dispatch (Cold, Distinct Types)",
                DispatchBenchmarkScenario.BroadcastFirstDispatchCold =>
                    "Broadcast First Dispatch (Cold, Distinct Types)",
                _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
            };
        }
    }
}
#endif
