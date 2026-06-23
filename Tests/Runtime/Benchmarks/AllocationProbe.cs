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
    /// to surface.
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
        /// Runs <paramref name="body"/> and returns the number of managed allocation
        /// calls it triggered, or <see cref="Unmeasured"/> when the recorder is
        /// non-functional on this backend. <paramref name="body"/> always runs (the
        /// measurement is non-intrusive when the probe is unavailable).
        /// </summary>
        public static long Measure(Action body)
        {
            if (body == null)
            {
                throw new ArgumentNullException(nameof(body));
            }

            if (!IsFunctional)
            {
                body();
                return Unmeasured;
            }

            Recorder recorder = Recorder.Get(GcAllocMarker);
            // Toggling off then on resets sampleBlockCount for a fresh window.
            recorder.enabled = false;
            recorder.enabled = true;
            body();
            recorder.enabled = false;
            return recorder.sampleBlockCount;
        }

        /// <summary>
        /// Begins an allocation-counting window for callers that must interleave the
        /// count with their own timing (the cold-latency path times one operation and
        /// counts its allocations over the SAME region). Pair with <see cref="End"/>.
        /// A no-op when the probe is non-functional.
        /// </summary>
        public static void Begin()
        {
            if (!IsFunctional)
            {
                return;
            }

            Recorder recorder = Recorder.Get(GcAllocMarker);
            recorder.enabled = false;
            recorder.enabled = true;
        }

        /// <summary>
        /// Ends the window opened by <see cref="Begin"/> and returns the allocation
        /// count, or <see cref="Unmeasured"/> when the probe is non-functional.
        /// </summary>
        public static long End()
        {
            if (!IsFunctional)
            {
                return Unmeasured;
            }

            Recorder recorder = Recorder.Get(GcAllocMarker);
            recorder.enabled = false;
            return recorder.sampleBlockCount;
        }

        private static bool ComputeFunctional()
        {
            try
            {
                Recorder recorder = Recorder.Get(GcAllocMarker);
                if (recorder == null || !recorder.isValid)
                {
                    return false;
                }

                recorder.enabled = false;
                recorder.enabled = true;
                // A guaranteed managed allocation the recorder must observe if it works.
                s_selfTestSink = new byte[64];
                recorder.enabled = false;
                return recorder.sampleBlockCount > 0;
            }
            catch
            {
                // Any recorder/profiler unavailability => fall back to the honest
                // "unmeasured" sentinel rather than risk a misleading zero.
                return false;
            }
        }
    }
}
#endif
