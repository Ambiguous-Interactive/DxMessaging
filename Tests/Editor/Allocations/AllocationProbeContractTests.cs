#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Editor.Allocations
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using DxMessaging.Tests.Runtime.Benchmarks;
    using NUnit.Framework;
    using UnityEngine.Profiling;

    /// <summary>
    /// Contract + regression guard for <see cref="AllocationProbe"/>'s recorder lifecycle.
    ///
    /// <para>
    /// A measurement window ENABLES a GLOBAL <c>GC.Alloc</c> profiler recorder that MUST be
    /// disabled afterwards. A recorder left enabled adds profiler overhead to every managed
    /// allocation that runs for the rest of the domain and silently distorts later timing
    /// measurements. These tests prove the recorder is ALWAYS released -- including when the
    /// measured body throws -- and lock the RAII shape (<see cref="AllocationProbe.Window"/>
    /// is a disposable value type, with no raw enable/disable pair to misuse) that makes the
    /// leak impossible to reintroduce.
    /// </para>
    ///
    /// <para>
    /// Runs in EditMode where the Mono profiler is present, so the recorder is functional
    /// (the same assumption the editor allocation suite already relies on). The
    /// functional-probe tests Ignore rather than fail on a backend with no probe; the
    /// default-window and type-shape tests hold on every backend.
    /// </para>
    /// </summary>
    [Category("Performance")]
    public sealed class AllocationProbeContractTests
    {
        private const string GcAllocMarker = "GC.Alloc";

        private static bool RecorderEnabled => Recorder.Get(GcAllocMarker).enabled;

        [TearDown]
        public void EnsureRecorderDisabled()
        {
            // Defensive hygiene: never let a failing test leak an enabled recorder into the
            // next test. Assertions run BEFORE teardown, so this never masks a real leak.
            Recorder.Get(GcAllocMarker).enabled = false;
        }

        [Test]
        public void MeasureReleasesRecorderWhenBodyThrows()
        {
            if (!AllocationProbe.IsFunctional)
            {
                Assert.Ignore("GC.Alloc recorder is non-functional on this backend.");
            }

            InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() =>
                AllocationProbe.Measure(() => throw new InvalidOperationException("boom"))
            );
            Assert.AreEqual("boom", thrown.Message, "Measure must propagate the body's exception.");
            Assert.IsFalse(
                RecorderEnabled,
                "Measure must disable the GC.Alloc recorder even when the body throws; a "
                    + "leaked-enabled recorder adds profiler overhead to every later allocation "
                    + "in the domain and distorts subsequent timing measurements."
            );
        }

        [Test]
        public void BeginWindowReleasesRecorderWhenBodyThrows()
        {
            if (!AllocationProbe.IsFunctional)
            {
                Assert.Ignore("GC.Alloc recorder is non-functional on this backend.");
            }

            Assert.Throws<InvalidOperationException>(() =>
            {
                using AllocationProbe.Window window = AllocationProbe.BeginWindow();
                throw new InvalidOperationException("boom");
            });
            Assert.IsFalse(
                RecorderEnabled,
                "A using-scoped AllocationProbe.Window must disable the recorder on Dispose even "
                    + "when the measured body throws before Sample() is reached."
            );
        }

        [Test]
        public void MeasureColdLatencyReleasesRecorderWhenTimedOperationThrows()
        {
            if (!AllocationProbe.IsFunctional)
            {
                Assert.Ignore("GC.Alloc recorder is non-functional on this backend.");
            }

            // Guards the cold-latency INTEGRATION path -- the literal scenario class of the
            // original bug. Each trial opens a window with `using` INSIDE a try whose finally
            // tears the trial down; a throwing timed operation must still release the recorder
            // (the using disposes as the try unwinds, before tearDownTrial runs).
            Assert.Throws<InvalidOperationException>(() =>
                BenchmarkProtocol.MeasureColdLatency<object>(
                    trials: 1,
                    setUpTrial: index => new object(),
                    timedOperation: state => throw new InvalidOperationException("boom"),
                    tearDownTrial: state => { }
                )
            );
            Assert.IsFalse(
                RecorderEnabled,
                "MeasureColdLatency must release the GC.Alloc recorder when the timed operation "
                    + "throws; the per-trial using-window disposes before tearDownTrial runs."
            );
        }

        [Test]
        public void MeasureReleasesRecorderOnSuccess()
        {
            if (!AllocationProbe.IsFunctional)
            {
                Assert.Ignore("GC.Alloc recorder is non-functional on this backend.");
            }

            object sink = null;
            long count = AllocationProbe.Measure(() => sink = new byte[64]);
            Assert.IsNotNull(sink, "The measured body must have run.");
            Assert.GreaterOrEqual(
                count,
                1,
                "A functional probe must count the byte[64] allocation as at least one call."
            );
            Assert.IsFalse(
                RecorderEnabled,
                "Measure must disable the recorder on the success path."
            );
        }

        [Test]
        public void MeasureStaysAccurateAfterAThrowingMeasure()
        {
            if (!AllocationProbe.IsFunctional)
            {
                Assert.Ignore("GC.Alloc recorder is non-functional on this backend.");
            }

            // A prior throwing measurement must not corrupt or inflate the next one.
            try
            {
                AllocationProbe.Measure(() => throw new InvalidOperationException("boom"));
            }
            catch (InvalidOperationException)
            {
                // Expected.
            }

            // The REAL regression assertion (FAILS on the old leaking code): the recorder is
            // disabled the instant the throwing measurement returns control -- not merely
            // "eventually" once the next measurement's toggle-reset self-heals the count. The
            // leak's true harm is the profiler overhead a left-enabled recorder adds to any
            // unrelated code that runs before the next window opens.
            Assert.IsFalse(
                RecorderEnabled,
                "The recorder must be disabled immediately after a throwing Measure."
            );

            object sink = null;
            long count = AllocationProbe.Measure(() => sink = new byte[64]);
            Assert.IsNotNull(sink);
            Assert.GreaterOrEqual(
                count,
                1,
                "The measurement after a throwing measurement must still observe the allocation."
            );
            Assert.IsFalse(RecorderEnabled);
        }

        [Test]
        public void WindowIsDisposableValueType()
        {
            Type windowType = typeof(AllocationProbe.Window);
            Assert.IsTrue(
                windowType.IsValueType,
                "AllocationProbe.Window must be a value type so opening a window allocates nothing "
                    + "of its own (these are allocation measurements)."
            );
            Assert.IsTrue(
                typeof(IDisposable).IsAssignableFrom(windowType),
                "AllocationProbe.Window must implement IDisposable so `using` always releases the "
                    + "recorder. This is the contract that makes the recorder leak impossible."
            );
        }

        [Test]
        public void ProbeExposesNoRawEnableDisablePair()
        {
            Type probe = typeof(AllocationProbe);
            Assert.IsNull(
                probe.GetMethod("Begin", BindingFlags.Public | BindingFlags.Static),
                "AllocationProbe must not expose a raw Begin() recorder-enable method: the "
                    + "enable/disable pair is the exact footgun that leaked the recorder when a "
                    + "measured body threw. Use the using-scoped BeginWindow()/Window instead."
            );
            Assert.IsNull(
                probe.GetMethod("End", BindingFlags.Public | BindingFlags.Static),
                "AllocationProbe must not expose a raw End() method paired with Begin(); use the "
                    + "using-scoped BeginWindow()/Window so cleanup is automatic and exception-safe."
            );
        }

        [Test]
        public void DefaultWindowIsHarmlessNoOp()
        {
            // A default (probe-non-functional) window must be safe: Sample returns the
            // Unmeasured sentinel and Dispose does nothing. This is the contract the
            // non-functional backends (for example a Release IL2CPP player) rely on.
            AllocationProbe.Window window = default;
            Assert.AreEqual(AllocationProbe.Unmeasured, window.Sample());
            Assert.DoesNotThrow(() => window.Dispose());
        }

        [Test]
        public void MeasureMinThrowsOnNonPositiveAttempts()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                AllocationProbe.MeasureMin(0, prepare: null, operation: () => { })
            );
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                AllocationProbe.MeasureMin(-1, prepare: null, operation: () => { })
            );
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                AllocationProbe.MeasureMinWithDiagnostics(0, prepare: null, operation: () => 0)
            );
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                AllocationProbe.MeasureMinWithDiagnostics(-1, prepare: null, operation: () => 0)
            );
        }

        [Test]
        public void MeasureMinThrowsOnNullOperation()
        {
            Assert.Throws<ArgumentNullException>(() =>
                AllocationProbe.MeasureMin(4, prepare: null, operation: null)
            );
            Assert.Throws<ArgumentNullException>(() =>
                AllocationProbe.MeasureMinWithDiagnostics(
                    4,
                    prepare: null,
                    operation: (Func<int>)null
                )
            );
        }

        [Test]
        public void MeasureMinInvokesPrepareAndOperationOncePerAttempt()
        {
            if (!AllocationProbe.IsFunctional)
            {
                Assert.Ignore("GC.Alloc recorder is non-functional on this backend.");
            }

            const int attempts = 5;
            int prepareCount = 0;
            int operationCount = 0;
            _ = AllocationProbe.MeasureMin(
                attempts,
                prepare: () => ++prepareCount,
                operation: () => ++operationCount
            );
            Assert.AreEqual(attempts, prepareCount, "prepare must run once per attempt.");
            Assert.AreEqual(attempts, operationCount, "operation must run once per attempt.");
        }

        [Test]
        public void MeasureMinReturnsTheSmallestAttempt()
        {
            if (!AllocationProbe.IsFunctional)
            {
                Assert.Ignore("GC.Alloc recorder is non-functional on this backend.");
            }

            // Each attempt allocates a DESCENDING number of arrays: attempt i allocates
            // (8 - i) * 100, i.e. 800, 700, ... 100. The descending step (100) is chosen far
            // larger than the warm-editor per-window background-allocation floor (a handful
            // to a few dozen), so the minimum reads ~100 (the smallest attempt) while a
            // max/first selection would read ~800. This pins the min-selection the
            // warm-editor denoising relies on without assuming a noise-free window.
            int attempt = 0;
            object sink = null;
            long min = AllocationProbe.MeasureMin(
                8,
                prepare: null,
                operation: () =>
                {
                    int allocations = (8 - attempt) * 100;
                    ++attempt;
                    for (int i = 0; i < allocations; ++i)
                    {
                        sink = new byte[16];
                    }
                }
            );
            Assert.That(
                min,
                Is.GreaterThanOrEqualTo(100),
                $"The smallest attempt allocated 100 arrays, so the floor is >= 100 (was {min})."
            );
            Assert.That(
                min,
                Is.LessThanOrEqualTo(400),
                $"MeasureMin returned {min}; it must take the MINIMUM (~100, the smallest "
                    + "attempt), not the maximum or first attempt (~800). Anchoring sink: "
                    + (sink == null ? "null" : "set")
            );
        }

        [Test]
        public void MeasureMinWithDiagnosticsReturnsTheSmallestAttemptsDiagnostics()
        {
            if (!AllocationProbe.IsFunctional)
            {
                Assert.Ignore("GC.Alloc recorder is non-functional on this backend.");
            }

            // The first attempt allocates thousands of arrays while the second attempt
            // performs no explicit allocation. This gives a large enough gap that editor
            // background noise cannot make the first attempt the minimum. The diagnostic
            // payload must come from the second attempt, not from the final outer state or
            // an aggregate across attempts.
            const int allocationsInFirstAttempt = 10_000;
            int attempt = 0;
            object sink = null;
            AllocationProbe.MinimumMeasurement<int> measurement =
                AllocationProbe.MeasureMinWithDiagnostics(
                    2,
                    prepare: null,
                    operation: () =>
                    {
                        int currentAttempt = attempt;
                        ++attempt;
                        if (currentAttempt == 0)
                        {
                            for (int i = 0; i < allocationsInFirstAttempt; ++i)
                            {
                                sink = new byte[16];
                            }
                        }

                        return currentAttempt;
                    }
                );

            Assert.That(
                measurement.GcAllocations,
                Is.LessThan(allocationsInFirstAttempt),
                "The allocation count must come from the low-allocation second attempt."
            );
            Assert.AreEqual(
                1,
                measurement.AttemptIndex,
                "AttemptIndex must identify the zero-based attempt that produced the minimum."
            );
            Assert.AreEqual(
                1,
                measurement.Diagnostics,
                "Diagnostics must come from the same attempt that produced the minimum. "
                    + "Anchoring sink: "
                    + (sink == null ? "null" : "set")
            );
        }

        [Test]
        public void MeasureMinReleasesRecorderAfterMeasuring()
        {
            if (!AllocationProbe.IsFunctional)
            {
                Assert.Ignore("GC.Alloc recorder is non-functional on this backend.");
            }

            object sink = null;
            _ = AllocationProbe.MeasureMin(3, prepare: null, operation: () => sink = new byte[16]);
            Assert.IsFalse(
                RecorderEnabled,
                "MeasureMin must leave the GC.Alloc recorder disabled. Anchoring sink: "
                    + (sink == null ? "null" : "set")
            );
        }

        [Test]
        public void SettleHeapForMeasurementUsesCompleteCollectWaitCollectSequence()
        {
            string method = ExtractMethodBody(
                ReadAllocationProbeSource(),
                "public static void SettleHeapForMeasurement()"
            );
            string collectCall = "GC.Collect();";
            string waitCall = "GC.WaitForPending" + "Finalizers();";
            int firstCollect = method.IndexOf(collectCall, StringComparison.Ordinal);
            int wait = method.IndexOf(waitCall, StringComparison.Ordinal);
            int secondCollect = method.IndexOf(
                collectCall,
                firstCollect + collectCall.Length,
                StringComparison.Ordinal
            );

            Assert.That(
                CountOccurrences(method, collectCall),
                Is.EqualTo(2),
                "SettleHeapForMeasurement must perform exactly two GC.Collect calls."
            );
            Assert.That(
                CountOccurrences(method, waitCall),
                Is.EqualTo(1),
                "SettleHeapForMeasurement must wait for pending finalizers exactly once."
            );
            Assert.That(
                firstCollect,
                Is.GreaterThanOrEqualTo(0),
                "SettleHeapForMeasurement must start with GC.Collect()."
            );
            Assert.That(
                wait,
                Is.GreaterThan(firstCollect),
                "SettleHeapForMeasurement must wait for finalizers after the first collection."
            );
            Assert.That(
                secondCollect,
                Is.GreaterThan(wait),
                "SettleHeapForMeasurement must run a second GC.Collect after waiting for finalizers."
            );
        }

        [Test]
        public void MeasureMinUsesCentralizedSettlementBeforeAttemptsAndBeforeReturn()
        {
            string method = ExtractMethodBody(
                ReadAllocationProbeSource(),
                "private static MinimumMeasurement<TDiagnostics> MeasureMinCore<TDiagnostics>("
            );
            string settleCall = "SettleHeapForMeasurement();";
            int firstSettle = method.IndexOf(settleCall, StringComparison.Ordinal);
            int attemptLoop = method.IndexOf(
                "for (int attempt = 0; attempt < attempts; ++attempt)",
                StringComparison.Ordinal
            );
            int secondSettle = method.IndexOf(
                settleCall,
                firstSettle + settleCall.Length,
                StringComparison.Ordinal
            );
            int returnMeasurement = method.IndexOf(
                "return new MinimumMeasurement<TDiagnostics>(min",
                StringComparison.Ordinal
            );

            Assert.That(
                CountOccurrences(method, settleCall),
                Is.EqualTo(2),
                "The shared MeasureMin core must call the centralized complete GC settlement "
                    + "helper before attempts and before returning."
            );
            Assert.That(
                firstSettle,
                Is.GreaterThanOrEqualTo(0),
                "The shared MeasureMin core must settle the heap before opening any "
                    + "measurement window."
            );
            Assert.That(
                attemptLoop,
                Is.GreaterThan(firstSettle),
                "The shared MeasureMin core must settle before the measurement attempt loop."
            );
            Assert.That(
                secondSettle,
                Is.GreaterThan(attemptLoop),
                "The shared MeasureMin core must settle again after the repeated attempts."
            );
            Assert.That(
                returnMeasurement,
                Is.GreaterThan(secondSettle),
                "The shared MeasureMin core must reclaim attempt garbage before returning."
            );
        }

        [Test]
        public void AllocationEditorTestsUseCentralizedGcSettlementForAllocationWindows()
        {
            string bannedCall = "WaitForPending" + "Finalizers(";
            List<string> offenders = new();
            foreach (string root in ResolveAllocationEditorTestRootsFallback())
            {
                foreach (
                    string file in System.IO.Directory.EnumerateFiles(
                        root,
                        "*.cs",
                        System.IO.SearchOption.AllDirectories
                    )
                )
                {
                    string text = System.IO.File.ReadAllText(file);
                    if (text.Contains(bannedCall))
                    {
                        offenders.Add(NormalizeForDisplay(file));
                    }
                }
            }

            Assert.That(
                offenders,
                Is.Empty,
                "Editor allocation-test GC settling must go through "
                    + "AllocationProbe.SettleHeapForMeasurement(), which performs "
                    + "Collect, WaitForPendingFinalizers, Collect. Direct finalizer waits "
                    + "are easy to leave incomplete:\n"
                    + string.Join("\n", offenders)
            );
        }

        private static List<string> ResolveAllocationEditorTestRootsFallback()
        {
            List<string> roots = new();
            foreach (string testsRoot in ResolveTestsTreeRootsFallback())
            {
                string allocationRoot = System.IO.Path.Combine(testsRoot, "Editor", "Allocations");
                string full = System.IO.Path.GetFullPath(allocationRoot);
                if (System.IO.Directory.Exists(full) && !roots.Contains(full))
                {
                    roots.Add(full);
                }
            }

            return roots;
        }

        private static List<string> ResolveTestsTreeRootsFallback()
        {
            List<string> roots = new();
            string[] candidates =
            {
                System.IO.Path.Combine(
                    UnityEngine.Application.dataPath,
                    "..",
                    "Packages",
                    "com.wallstop-studios.dxmessaging",
                    "Tests"
                ),
                System.IO.Path.Combine(UnityEngine.Application.dataPath, "..", "Tests"),
                System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "Tests"),
            };

            foreach (string candidate in candidates)
            {
                string full = System.IO.Path.GetFullPath(candidate);
                if (System.IO.Directory.Exists(full) && !roots.Contains(full))
                {
                    roots.Add(full);
                }
            }

            return roots;
        }

        private static string ReadAllocationProbeSource()
        {
            foreach (string root in ResolveTestsTreeRootsFallback())
            {
                string candidate = System.IO.Path.Combine(
                    root,
                    "Runtime",
                    "Benchmarks",
                    "AllocationProbe.cs"
                );
                if (System.IO.File.Exists(candidate))
                {
                    return System.IO.File.ReadAllText(candidate);
                }
            }

            Assert.Fail("Could not locate Tests/Runtime/Benchmarks/AllocationProbe.cs on disk.");
            return string.Empty;
        }

        private static string ExtractMethodBody(string source, string signature)
        {
            int signatureIndex = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.That(
                signatureIndex,
                Is.GreaterThanOrEqualTo(0),
                "Could not find method signature: " + signature
            );

            int openBrace = source.IndexOf('{', signatureIndex);
            Assert.That(openBrace, Is.GreaterThan(signatureIndex));

            int depth = 0;
            for (int i = openBrace; i < source.Length; ++i)
            {
                if (source[i] == '{')
                {
                    ++depth;
                }
                else if (source[i] == '}')
                {
                    --depth;
                    if (depth == 0)
                    {
                        return source.Substring(openBrace, i - openBrace + 1);
                    }
                }
            }

            Assert.Fail("Could not parse method body for signature: " + signature);
            return string.Empty;
        }

        private static int CountOccurrences(string text, string pattern)
        {
            int count = 0;
            int index = 0;
            while (true)
            {
                index = text.IndexOf(pattern, index, StringComparison.Ordinal);
                if (index < 0)
                {
                    return count;
                }

                ++count;
                index += pattern.Length;
            }
        }

        private static string NormalizeForDisplay(string file)
        {
            string full = System.IO.Path.GetFullPath(file);
            string cwd = System.IO.Path.GetFullPath(System.IO.Directory.GetCurrentDirectory());
            if (!cwd.EndsWith(System.IO.Path.DirectorySeparatorChar.ToString()))
            {
                cwd += System.IO.Path.DirectorySeparatorChar;
            }

            string display = full.StartsWith(cwd, StringComparison.Ordinal)
                ? full.Substring(cwd.Length)
                : full;
            return display.Replace('\\', '/');
        }
    }
}
#endif
