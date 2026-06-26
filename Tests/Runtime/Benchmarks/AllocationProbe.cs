#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Benchmarks
{
    using System;
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

        // 0 = not yet probed, 1 = recorder confirmed functional, -1 = non-functional.
        private static int s_state;

        // Anchors the self-test allocation so a release-build optimizer cannot elide it.
        private static object s_selfTestSink;

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
            if (!IsFunctional)
            {
                return default;
            }

            Recorder recorder = Recorder.Get(GcAllocMarker);
            // Toggling off then on resets sampleBlockCount for a fresh window. Enabling
            // is the LAST thing we do so nothing above is counted against the window.
            recorder.enabled = false;
            recorder.enabled = true;
            return new Window(recorder);
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
        /// Runs <paramref name="operation"/> <paramref name="attempts"/> times -- each
        /// preceded by <paramref name="prepare"/> (the per-attempt precondition setup,
        /// which is NOT counted) -- after a single settling collection before the loop --
        /// and returns the MINIMUM
        /// managed-allocation CALL count observed, or <see cref="Unmeasured"/> when the
        /// probe is non-functional on this backend.
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

            if (!IsFunctional)
            {
                return Unmeasured;
            }

            // Settle ONCE before the loop, matching a single test's pre-measurement
            // collection cost. We deliberately do NOT force a collection per attempt: a
            // per-attempt GC.Collect storm (attempts x parameterizations) grows and
            // fragments the long-lived editor heap enough to perturb OTHER allocation
            // tests that run afterward. The minimum already rejects the windows where an
            // organic collection fires mid-measurement, so per-attempt forcing is both
            // unnecessary and harmful.
            GC.Collect();
            GC.WaitForPendingFinalizers();

            long min = long.MaxValue;
            for (int attempt = 0; attempt < attempts; ++attempt)
            {
                prepare?.Invoke();
                using Window window = BeginWindow();
                operation();
                long count = window.Sample();
                if (count < min)
                {
                    min = count;
                }
            }

            // Reclaim the garbage these repeated attempts produced BEFORE returning, so a
            // collection it would otherwise trigger does not fire inside a later test's
            // measurement window and inflate that test's count. Without this, repeating an
            // operation N times leaves N times the garbage, which is what turns a robust
            // single-window neighbor test flaky after a MeasureMin test runs.
            GC.Collect();
            GC.WaitForPendingFinalizers();

            return min;
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
            // Null when the probe is non-functional (a no-op window). Recorder is a plain
            // managed class (not a UnityEngine.Object), so == null is a true reference check.
            private readonly Recorder _recorder;

            internal Window(Recorder recorder)
            {
                _recorder = recorder;
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
            /// Ensures the recorder is disabled. Idempotent and exception-safe; running
            /// via <c>using</c> guarantees the recorder is released even if the measured
            /// body throws before <see cref="Sample"/> is reached.
            /// </summary>
            public void Dispose()
            {
                if (_recorder != null)
                {
                    _recorder.enabled = false;
                }
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
