#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Benchmarks
{
    using System;
    using global::Unity.Profiling;
    using UnityEngine.Profiling;

    /// <summary>
    /// Reliable managed-allocation probe for the benchmark + comparison harness.
    ///
    /// <para>
    /// WHY THIS EXISTS: <c>GC.GetAllocatedBytesForCurrentThread()</c> returns <c>0</c>
    /// for EVERY allocation under Unity's Boehm GC (editor Mono AND IL2CPP players).
    /// This was proven on the host editor: a forced 1 MB array allocation
    /// (1000 x <c>byte[1000]</c>) read back as a <c>0</c>-byte delta, and boxing a
    /// struct one million times also read back <c>0</c>. The benchmark and comparison
    /// "allocated bytes" column was therefore VACUOUSLY zero for every technology,
    /// silently hiding real per-operation allocations (for example struct boxing in
    /// reflection-based or DI-based dispatch). Every "0 B" the harness ever reported
    /// was an artifact of a dead API, not a measured zero.
    /// </para>
    ///
    /// <para>
    /// THE RELIABLE SIGNAL: this probe replaces the broken byte counter with the
    /// Unity <c>GC.Alloc</c> profiler recorder (<see cref="Recorder"/>), which counts
    /// managed allocation CALLS precisely and is immune to GC timing (a long window
    /// that the GC collects mid-run does not erase the count, unlike
    /// <c>GC.GetTotalMemory</c> heap deltas). It is the same mechanism behind
    /// NUnit-Unity's <c>Is.Not.AllocatingGCMemory()</c> constraint that the repo's
    /// editor allocation suites already trust. A count of allocation calls is also a
    /// SHARPER zero-alloc signal than a byte delta: "0 allocations per emit" versus
    /// "1 allocation per emit (a box)" is exactly the distinction this harness exists
    /// to surface. The recorder requires the profiler, so it is functional in the
    /// editor and in development players, but NOT in a non-development (Release)
    /// player where the profiler is stripped -- see the HONESTY GUARANTEE below.
    /// </para>
    ///
    /// <para>
    /// HONESTY GUARANTEE: the probe SELF-VALIDATES once per domain by forcing a known
    /// allocation and confirming the recorder observed it. If the recorder is
    /// non-functional on the current backend (for example a non-development player
    /// with the profiler stripped), every measurement returns the
    /// <see cref="Unmeasured"/> sentinel instead of a fabricated <c>0</c>, so a report
    /// can never again claim "zero" where it simply could not measure. Callers and
    /// renderers MUST treat <see cref="Unmeasured"/> distinctly from <c>0</c>.
    /// </para>
    ///
    /// <para>
    /// BYTES, NOT JUST COUNTS: the same window ALSO measures the total BYTES of managed
    /// allocations, via a SECOND mechanism -- the live <c>"GC Allocated In Frame"</c>
    /// profiler counter (<see cref="ProfilerRecorder"/>). Reading its <c>CurrentValue</c>
    /// before and after the measured region yields the exact bytes that region's
    /// <c>GC.Alloc</c> hooks reported, because that counter accumulates LIVE within the
    /// frame. This was proven on the host editor (6000.4): a region allocating
    /// <c>100 x byte[10000]</c> read back a byte-exact, run-to-run-identical
    /// <c>1,003,200</c>; a genuinely zero-alloc region read <c>0</c>; and -- crucially --
    /// it is COLLECTION-IMMUNE, where a <c>GC.GetTotalMemory</c> heap delta is not: a
    /// heavy-churn region that fires mid-window collections (which made
    /// <c>GC.GetTotalMemory</c> swing to <c>-133 MB</c>) read a rock-stable
    /// <c>8,000,000</c> here, because the counter sums allocation-hook bytes rather than a
    /// heap-size difference. <c>GC.GetAllocatedBytesForCurrentThread()</c> is unusable
    /// (returns <c>0</c> under Boehm) and <c>GC.GetTotalMemory</c> is dominated by
    /// warm-editor heap noise for sub-megabyte regions -- this counter is the ONLY
    /// mechanism that is exact AND collection-immune AND synchronous. It shares the count
    /// probe's HONESTY GUARANTEE: it self-validates once per domain and reports
    /// <see cref="Unmeasured"/> (never a fabricated <c>0</c>) on a backend where the
    /// counter is absent (a non-development Release player with the profiler stripped).
    /// A region that crosses a frame boundary (the counter resets per frame) yields a
    /// negative delta, which is reported as <see cref="Unmeasured"/> -- the benchmark
    /// batches are synchronous and never cross a frame, so this is a guard, not a normal
    /// path.
    /// </para>
    ///
    /// <para>
    /// EXCEPTION SAFETY: a measurement window ENABLES a global profiler recorder that
    /// MUST be disabled again afterwards -- a recorder left enabled adds profiler
    /// overhead to every allocation that runs for the rest of the domain and can
    /// distort later timing measurements. To make that release automatic and
    /// impossible to forget, the only way to open a window is <see cref="BeginWindow"/>,
    /// which returns a <see cref="Window"/> that disables the recorder on
    /// <see cref="Window.Dispose"/>. ALWAYS open it with <c>using</c> so the recorder
    /// is released even when the measured body throws. <see cref="Measure"/> wraps that
    /// pattern for the common "just count this block" case. There is deliberately no
    /// raw enable/disable pair to misuse.
    /// </para>
    /// </summary>
    public static class AllocationProbe
    {
        /// <summary>
        /// Sentinel returned when no reliable allocation probe is available on the
        /// current backend. Distinct from a measured <c>0</c> (a real zero-alloc
        /// result). Renderers surface this as "n/a", never as a zero.
        /// </summary>
        public const long Unmeasured = -1;

        private const string GcAllocMarker = "GC.Alloc";

        // The live, per-frame managed-allocation BYTE counter. Reading its CurrentValue
        // before/after a synchronous region yields that region's allocated bytes exactly.
        private const string GcAllocatedInFrameCounter = "GC Allocated In Frame";

        // 0 = not yet probed, 1 = recorder confirmed functional, -1 = non-functional.
        private static int s_state;

        // Anchors the self-test allocation so a release-build optimizer cannot elide it.
        private static object s_selfTestSink;

        // 0 = not yet probed, 1 = byte counter confirmed functional, -1 = non-functional.
        private static int s_bytesState;

        // The domain-lived byte counter recorder. Created lazily by the byte self-test and
        // kept running for the rest of the domain (a single always-on counter, like the
        // count probe's global GC.Alloc Recorder), so opening a window reads CurrentValue
        // without allocating a fresh recorder -- which would itself pollute the count.
        private static ProfilerRecorder s_byteRecorder;

        // Anchors the byte self-test allocation against a release-build optimizer.
        private static object s_byteSelfTestSink;

        /// <summary>
        /// True when the <c>GC.Alloc</c> recorder is confirmed to observe a known
        /// allocation on this backend. Computed once per domain (the result is a
        /// backend property, not a per-call one) and cached. When false, all measured
        /// values are <see cref="Unmeasured"/>.
        /// </summary>
        public static bool IsFunctional
        {
            get
            {
                if (s_state == 0)
                {
                    s_state = ComputeFunctional() ? 1 : -1;
                }

                return s_state == 1;
            }
        }

        /// <summary>
        /// True when the <c>"GC Allocated In Frame"</c> byte counter is confirmed to
        /// observe a known allocation on this backend. Computed once per domain and
        /// cached. When false, every measured byte value is <see cref="Unmeasured"/>.
        /// This is INDEPENDENT of <see cref="IsFunctional"/> (the count probe): a backend
        /// could in principle support one and not the other, so each is validated and
        /// reported separately.
        /// </summary>
        public static bool BytesFunctional
        {
            get
            {
                if (s_bytesState == 0)
                {
                    s_bytesState = ComputeBytesFunctional() ? 1 : -1;
                }

                return s_bytesState == 1;
            }
        }

        /// <summary>
        /// Opens an allocation-counting window. ALWAYS open it with <c>using</c>: the
        /// returned <see cref="Window"/> disables the underlying profiler recorder on
        /// <see cref="Window.Dispose"/>, so the recorder is released even if the
        /// measured body throws. Read the count with <see cref="Window.Sample"/> before
        /// the <c>using</c> scope ends. When the probe is non-functional this returns a
        /// no-op window whose <see cref="Window.Sample"/> is <see cref="Unmeasured"/>.
        ///
        /// <para>
        /// Callers that interleave their own timing with the count (the cold-latency
        /// path times one operation and counts its allocations over the SAME region)
        /// capture their end timestamp BEFORE calling <see cref="Window.Sample"/>, so
        /// the sample/disable overhead is excluded from the timing.
        /// </para>
        ///
        /// <para>
        /// NOT reentrant: every window shares the one global <c>GC.Alloc</c> recorder,
        /// so a window must be sampled and disposed before another opens -- windows must
        /// not nest or overlap. The benchmarks are single-threaded by contract, so this
        /// is a usage rule, not a locking concern.
        /// </para>
        /// </summary>
        public static Window BeginWindow()
        {
            bool countFunctional = IsFunctional;
            bool bytesFunctional = BytesFunctional;
            if (!countFunctional && !bytesFunctional)
            {
                return default;
            }

            Recorder recorder = null;
            if (countFunctional)
            {
                recorder = Recorder.Get(GcAllocMarker);
                // Toggling off then on resets sampleBlockCount for a fresh window. Enabling
                // is the LAST thing we do so nothing above is counted against the window.
                recorder.enabled = false;
                recorder.enabled = true;
            }

            // Capture the byte baseline LAST -- after the count recorder is enabled -- so
            // the count-recorder toggle is not attributed to this window's byte delta. The
            // counter accumulates live within the frame, so (CurrentValue at Sample) minus
            // this baseline is exactly the bytes the measured region allocated.
            long byteStart = bytesFunctional ? s_byteRecorder.CurrentValue : Unmeasured;
            return new Window(recorder, bytesFunctional, byteStart);
        }

        /// <summary>
        /// Runs <paramref name="body"/> and returns the number of managed allocation
        /// calls it triggered, or <see cref="Unmeasured"/> when the recorder is
        /// non-functional on this backend. <paramref name="body"/> always runs (the
        /// measurement is non-intrusive when the probe is unavailable). The recorder is
        /// ALWAYS released, even when <paramref name="body"/> throws -- the exception
        /// propagates after the window is disposed.
        /// </summary>
        public static long Measure(Action body)
        {
            if (body == null)
            {
                throw new ArgumentNullException(nameof(body));
            }

            using Window window = BeginWindow();
            body();
            return window.Sample();
        }

        /// <summary>
        /// Runs <paramref name="body"/> and returns BOTH the managed allocation CALL count
        /// and the total allocated BYTES it triggered (each <see cref="Unmeasured"/> when
        /// its respective probe is non-functional on this backend). <paramref name="body"/>
        /// always runs. The recorder is ALWAYS released, even when <paramref name="body"/>
        /// throws -- the exception propagates after the window is disposed.
        /// </summary>
        public static AllocationSample MeasureWithBytes(Action body)
        {
            if (body == null)
            {
                throw new ArgumentNullException(nameof(body));
            }

            using Window window = BeginWindow();
            body();
            return window.SampleBoth();
        }

        /// <summary>
        /// Runs <paramref name="operation"/> <paramref name="attempts"/> times -- each
        /// preceded by <paramref name="prepare"/> (the per-attempt precondition setup,
        /// which is NOT counted) -- after a single settling collection before the loop --
        /// and returns the MINIMUM managed-allocation CALL count observed, or
        /// <see cref="Unmeasured"/> when the probe is non-functional on this backend.
        ///
        /// <para>
        /// WHY A MINIMUM: a single allocation window in a warm, long-lived editor domain
        /// intermittently spikes far above the operation's true cost. Measured on the host
        /// editor, a fixed <c>bus.Trim()</c>x32 block read a median of ~57 allocations with
        /// a floor of ~9 and rare spikes past 4000 -- the spike is a GC/heap-state-dependent
        /// pool miss or backing-array resize that fires inside one window and not the next
        /// (a no-GC region does NOT suppress it). Because the operation executes a fixed
        /// sequence of <c>new</c>s, those spikes only ADD to the floor; the minimum over a
        /// handful of attempts therefore converges to the true per-operation cost and is
        /// stable even on a busy editor, while still rising -- and tripping a budget -- on a
        /// genuine regression that raises the floor. This is the allocation-count analogue
        /// of taking the minimum of repeated timing samples to reject scheduler noise.
        /// Cold CI legs run a fresh domain per assembly and do not exhibit the spikes, so a
        /// single attempt there already reads the floor; the extra attempts are harmless.
        /// </para>
        /// </summary>
        /// <param name="attempts">How many times to measure (and take the min of). Must be positive.</param>
        /// <param name="prepare">Per-attempt precondition setup, run before each window and never counted. May be <c>null</c>.</param>
        /// <param name="operation">The operation whose allocation floor is measured.</param>
        public static long MeasureMin(int attempts, Action prepare, Action operation)
        {
            if (attempts <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(attempts));
            }

            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            return MeasureMinCore(
                attempts,
                prepare,
                () =>
                {
                    operation();
                    return default(NoDiagnostics);
                }
            ).GcAllocations;
        }

        /// <summary>
        /// Runs <paramref name="operation"/> repeatedly and returns both the minimum
        /// managed-allocation CALL count and the diagnostics produced by the same attempt
        /// that yielded that minimum.
        ///
        /// <para>
        /// Use this overload when the caller needs to assert or report side effects from
        /// the measured operation. Diagnostics accumulated in outer variables across all
        /// attempts can drift away from the minimum window and produce misleading failure
        /// messages or false positives. Keep <typeparamref name="TDiagnostics"/>
        /// allocation-free; constructing the diagnostic value happens inside the measured
        /// window.
        /// </para>
        /// </summary>
        /// <param name="attempts">How many times to measure (and take the min of). Must be positive.</param>
        /// <param name="prepare">Per-attempt precondition setup, run before each window and never counted. May be <c>null</c>.</param>
        /// <param name="operation">The operation whose allocation floor is measured. Returns diagnostics for that attempt.</param>
        public static MinimumMeasurement<TDiagnostics> MeasureMinWithDiagnostics<TDiagnostics>(
            int attempts,
            Action prepare,
            Func<TDiagnostics> operation
        )
        {
            if (attempts <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(attempts));
            }

            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            return MeasureMinCore(attempts, prepare, operation);
        }

        private static MinimumMeasurement<TDiagnostics> MeasureMinCore<TDiagnostics>(
            int attempts,
            Action prepare,
            Func<TDiagnostics> operation
        )
        {
            if (attempts <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(attempts));
            }

            if (!IsFunctional && !BytesFunctional)
            {
                return new MinimumMeasurement<TDiagnostics>(Unmeasured, Unmeasured, -1, default);
            }

            // Settle ONCE before the loop, matching a single test's pre-measurement
            // collection cost. We deliberately do NOT force a collection per attempt: a
            // per-attempt GC.Collect storm (attempts x parameterizations) grows and
            // fragments the long-lived editor heap enough to perturb OTHER allocation
            // tests that run afterward. The minimum already rejects the windows where an
            // organic collection fires mid-measurement, so per-attempt forcing is both
            // unnecessary and harmful.
            SettleHeapForMeasurement();

            long min = long.MaxValue;
            long minBytes = Unmeasured;
            int minAttemptIndex = -1;
            TDiagnostics minDiagnostics = default;
            try
            {
                for (int attempt = 0; attempt < attempts; ++attempt)
                {
                    prepare?.Invoke();
                    using Window window = BeginWindow();
                    TDiagnostics diagnostics = operation();
                    // Bytes are exact per attempt (not noisy like counts), so the bytes
                    // reported are those of the same attempt that produced the minimum
                    // count -- a single self-consistent sample, never a min/attempt mix.
                    AllocationSample sample = window.SampleBoth();
                    long count = sample.Allocations;
                    if (count < min)
                    {
                        min = count;
                        minBytes = sample.Bytes;
                        minAttemptIndex = attempt;
                        minDiagnostics = diagnostics;
                    }
                }
            }
            finally
            {
                // Reclaim the garbage these repeated attempts produced BEFORE returning,
                // so a collection it would otherwise trigger does not fire inside a later
                // test's measurement window and inflate that test's count. Without this,
                // repeating an operation N times leaves N times the garbage, which is what
                // turns a robust single-window neighbor test flaky after a MeasureMin test
                // runs. The finally keeps that hygiene even when an attempt throws.
                SettleHeapForMeasurement();
            }

            return new MinimumMeasurement<TDiagnostics>(
                min,
                minBytes,
                minAttemptIndex,
                minDiagnostics
            );
        }

        private readonly struct NoDiagnostics { }

        /// <summary>
        /// Minimum allocation count plus the caller-provided diagnostics from the same
        /// attempt that produced that count.
        /// </summary>
        public readonly struct MinimumMeasurement<TDiagnostics>
        {
            internal MinimumMeasurement(
                long gcAllocations,
                long gcAllocatedBytes,
                int attemptIndex,
                TDiagnostics diagnostics
            )
            {
                GcAllocations = gcAllocations;
                GcAllocatedBytes = gcAllocatedBytes;
                AttemptIndex = attemptIndex;
                Diagnostics = diagnostics;
            }

            /// <summary>
            /// Minimum managed-allocation CALL count, or <see cref="Unmeasured"/> when
            /// the recorder is not functional on this backend.
            /// </summary>
            public long GcAllocations { get; }

            /// <summary>
            /// Total managed allocation BYTES from the same attempt that produced
            /// <see cref="GcAllocations"/>, or <see cref="Unmeasured"/> when bytes could not
            /// be measured for that attempt -- EITHER the byte counter is non-functional on
            /// this backend OR that specific attempt crossed a frame boundary (the live byte
            /// counter resets per frame; see <see cref="Window.SampleBytes"/>). Bytes are
            /// exact per attempt, so this is a self-consistent companion to the minimum count,
            /// not a separate minimum -- but a consumer that renders this value MUST treat
            /// <see cref="Unmeasured"/> as "n/a", never as a real magnitude.
            /// </summary>
            public long GcAllocatedBytes { get; }

            /// <summary>
            /// Zero-based attempt index that produced <see cref="GcAllocations"/>, or -1
            /// when <see cref="GcAllocations"/> is <see cref="Unmeasured"/>.
            /// </summary>
            public int AttemptIndex { get; }

            /// <summary>
            /// Diagnostics returned by the operation during the winning attempt.
            /// </summary>
            public TDiagnostics Diagnostics { get; }
        }

        /// <summary>
        /// Forces a full collection, waits for finalizers, then forces a second
        /// full collection so objects made unreachable by finalizers are reclaimed
        /// before an allocation window begins or before the next test runs.
        /// </summary>
        public static void SettleHeapForMeasurement()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        /// <summary>
        /// A scoped allocation-counting window. Created by <see cref="BeginWindow"/> and
        /// released by <c>using</c>. <see cref="Dispose"/> disables the underlying
        /// profiler recorder; it is idempotent and safe to call after
        /// <see cref="Sample"/>. A <c>default</c> window (probe non-functional) is a
        /// harmless no-op: <see cref="Sample"/> returns <see cref="Unmeasured"/> and
        /// <see cref="Dispose"/> does nothing. This is a value type, so opening a window
        /// allocates nothing of its own.
        /// </summary>
        public readonly struct Window : IDisposable
        {
            // Null when the count probe is non-functional (a no-op count window). Recorder
            // is a plain managed class (not a UnityEngine.Object), so == null is a true
            // reference check.
            private readonly Recorder _recorder;

            // True when the byte counter is functional for this window. The counter itself
            // is the domain-static s_byteRecorder (always running); the window only needs
            // the baseline it captured at BeginWindow.
            private readonly bool _bytesFunctional;
            private readonly long _byteStart;

            internal Window(Recorder recorder, bool bytesFunctional, long byteStart)
            {
                _recorder = recorder;
                _bytesFunctional = bytesFunctional;
                _byteStart = byteStart;
            }

            /// <summary>
            /// Disables the recorder and returns the managed allocation CALL count
            /// observed since the window opened, or <see cref="Unmeasured"/> when the
            /// probe is non-functional. Disabling BEFORE the read freezes the count and
            /// ensures the read itself is never counted. Calling more than once is safe
            /// (the recorder is already disabled, so it returns the same frozen count).
            /// </summary>
            public long Sample()
            {
                if (_recorder == null)
                {
                    return Unmeasured;
                }

                _recorder.enabled = false;
                return _recorder.sampleBlockCount;
            }

            /// <summary>
            /// Returns the total managed allocation BYTES observed since the window opened
            /// (the live per-frame counter's delta), or <see cref="Unmeasured"/> when the
            /// byte counter is non-functional on this backend. A NEGATIVE delta means the
            /// measured region crossed a frame boundary (the counter resets per frame) and
            /// the bytes cannot be attributed -- that is reported as <see cref="Unmeasured"/>
            /// too, never a misleading value. Safe to call more than once (the counter is
            /// monotonic within a frame).
            /// </summary>
            public long SampleBytes()
            {
                if (!_bytesFunctional)
                {
                    return Unmeasured;
                }

                long delta = s_byteRecorder.CurrentValue - _byteStart;
                return delta < 0 ? Unmeasured : delta;
            }

            /// <summary>
            /// Samples BOTH the allocation CALL count and the allocated BYTES for this
            /// window in one call. Bytes are read FIRST so the count recorder's disable
            /// overhead is never attributed to the byte delta.
            /// </summary>
            public AllocationSample SampleBoth()
            {
                long bytes = SampleBytes();
                long count = Sample();
                return new AllocationSample(count, bytes);
            }

            /// <summary>
            /// Ensures the recorder is disabled. Idempotent and exception-safe; running
            /// via <c>using</c> guarantees the recorder is released even if the measured
            /// body throws before <see cref="Sample"/> is reached. The byte counter is a
            /// domain-static always-on counter, so there is nothing to release for bytes.
            /// </summary>
            public void Dispose()
            {
                if (_recorder != null)
                {
                    _recorder.enabled = false;
                }
            }
        }

        /// <summary>
        /// A paired allocation measurement: the managed allocation CALL
        /// <see cref="Allocations"/> count and the total allocated <see cref="Bytes"/>.
        /// Either field is <see cref="Unmeasured"/> when its probe is non-functional on
        /// this backend; the two are validated independently.
        /// </summary>
        public readonly struct AllocationSample
        {
            internal AllocationSample(long allocations, long bytes)
            {
                Allocations = allocations;
                Bytes = bytes;
            }

            /// <summary>
            /// Managed allocation CALL count, or <see cref="Unmeasured"/>.
            /// </summary>
            public long Allocations { get; }

            /// <summary>
            /// Total managed allocation BYTES, or <see cref="Unmeasured"/>.
            /// </summary>
            public long Bytes { get; }
        }

        private static bool ComputeBytesFunctional()
        {
            try
            {
                if (!s_byteRecorder.Valid)
                {
                    s_byteRecorder = ProfilerRecorder.StartNew(
                        ProfilerCategory.Memory,
                        GcAllocatedInFrameCounter
                    );
                }

                if (!s_byteRecorder.Valid)
                {
                    return false;
                }

                // Confirm the live counter actually observes a known allocation. byte[4096]
                // is large enough that even with Boehm's size-class rounding the delta must
                // be at least the requested 4096 bytes if the counter is working. A backend
                // that strips the profiler counter (a non-development Release player) reads a
                // flat 0 here, so we fall back to the honest Unmeasured sentinel.
                //
                // Retried a few times because the counter resets per frame: a single frame
                // boundary landing between the before/after reads would make the delta
                // negative and false-negative the WHOLE domain (the verdict is cached). One
                // clean attempt is enough to confirm the counter works; an all-stripped
                // backend never produces one and correctly stays non-functional.
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    long before = s_byteRecorder.CurrentValue;
                    s_byteSelfTestSink = new byte[4096];
                    long after = s_byteRecorder.CurrentValue;
                    if (after - before >= 4096)
                    {
                        return true;
                    }
                }

                return false;
            }
            catch
            {
                // Any recorder/profiler unavailability => honest "unmeasured" sentinel
                // rather than a misleading byte value.
                return false;
            }
        }

        private static bool ComputeFunctional()
        {
            Recorder recorder;
            try
            {
                recorder = Recorder.Get(GcAllocMarker);
            }
            catch
            {
                // Any recorder/profiler unavailability => fall back to the honest
                // "unmeasured" sentinel rather than risk a misleading zero.
                return false;
            }

            if (recorder == null || !recorder.isValid)
            {
                return false;
            }

            try
            {
                recorder.enabled = false;
                recorder.enabled = true;
                // A guaranteed managed allocation the recorder must observe if it works.
                s_selfTestSink = new byte[64];
                recorder.enabled = false;
                return recorder.sampleBlockCount > 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                // Never leave the self-test recorder enabled, even if reading the count
                // throws: a leaked-enabled recorder adds profiler overhead to every
                // subsequent allocation in the domain and can distort later timings.
                recorder.enabled = false;
            }
        }
    }
}
#endif
