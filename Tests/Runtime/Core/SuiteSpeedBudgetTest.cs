#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections;
    using System.Diagnostics;
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using NUnit.Framework;
    using Scripts.Components;
    using Scripts.Messages;
    using UnityEngine;
    using UnityEngine.TestTools;

    /// <summary>
    /// In-default-suite speed guard rail. The default Unity Edit + Play mode
    /// test run is supposed to finish in under a minute once the gated suites
    /// are filtered out. The companion <see cref="SuiteWallClockBudgetTest"/>
    /// fixture measures the runtime assembly wall clock directly; this test
    /// keeps a small representative workload in the default suite so local
    /// per-test regressions fail close to the changed code.
    /// </summary>
    /// <remarks>
    /// This test runs as part of the default Unity test suite - it is a fast
    /// guard rail that fails when local performance regresses below the
    /// 60-second whole-suite budget. The category scheme used across the rest
    /// of the test suite is:
    /// <list type="bullet">
    /// <item><description><c>Stress</c> - high-volume registration / emission tests.</description></item>
    /// <item><description><c>Performance</c> - throughput / latency benchmarks.</description></item>
    /// <item><description><c>Allocation</c> - the zero-GC matrix.</description></item>
    /// <item><description><c>MemoryReclaim</c> - explicit trim and idle-sweep reclamation tests.</description></item>
    /// <item><description><c>UnityRuntime</c> - Unity-only runtime lifecycle tests.</description></item>
    /// </list>
    /// CI runs the default suite (tests outside the gated categories, including
    /// this guard rail) on every PR; the gated categories are opt-in.
    /// </remarks>
    public sealed class SuiteSpeedBudgetTest : MessagingTestBase
    {
        private const int RepresentativeCycles = 100;

        /// <summary>
        /// Soft timing target. A breach is logged as a warning (visible in CI
        /// logs as an early-warning perf signal) but does NOT fail the default
        /// suite, because a raw wall-clock threshold is runner-speed dependent
        /// and would flake on a slow CI runner (observed on Unity 2021.3
        /// PlayMode) for the same deterministic work.
        /// </summary>
        private static readonly TimeSpan RepresentativeSoftBudget = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Egregious hard ceiling. Only a catastrophic, unmistakable
        /// regression (a 6x blow-out over the 5s soft target) hard-fails the
        /// default suite. Justification for the deliberately wide multiplier:
        /// the workload below is 100 register/emit/deregister cycles that
        /// complete in well under a second on every supported runner; a 30s
        /// wall clock is so far outside the normal envelope that it can only
        /// mean a genuine O(n^2)-class defect, never mere runner slowness.
        /// Keeping the bound this wide preserves a hard backstop without
        /// reintroducing the flaky tight-threshold failure mode. The
        /// fine-grained perf signal lives in the soft warning above and in the
        /// gated Performance/PerfBench benchmark suite.
        /// </summary>
        private static readonly TimeSpan RepresentativeHardBudget = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Measures a representative default-suite registration / emit /
        /// deregistration workload. The cycle-count correctness assertion is a
        /// hard, blocking check in the default suite; the wall-clock judgment
        /// is a soft warning plus an egregiously-wide hard backstop so it
        /// cannot flake on a slow runner.
        /// </summary>
        [UnityTest]
        public IEnumerator RepresentativeSubsetCompletesUnderBudget()
        {
            GameObject host = new(
                nameof(RepresentativeSubsetCompletesUnderBudget),
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);

            // Warm up so JIT and bus internals do not skew the measurement.
            MessageRegistrationHandle warmupHandle =
                token.RegisterUntargeted<SimpleUntargetedMessage>(_ => { });
            SimpleUntargetedMessage warmup = new();
            warmup.EmitUntargeted();
            token.RemoveRegistration(warmupHandle);

            int total = 0;
            Stopwatch timer = Stopwatch.StartNew();
            for (int round = 0; round < RepresentativeCycles; ++round)
            {
                MessageRegistrationHandle handle =
                    token.RegisterUntargeted<SimpleUntargetedMessage>(_ => ++total);
                SimpleUntargetedMessage message = new();
                message.EmitUntargeted();
                token.RemoveRegistration(handle);
            }

            timer.Stop();
            TimeSpan elapsed = timer.Elapsed;

            // CORRECTNESS (hard, blocking in the default suite): every cycle
            // must have registered, emitted, and incremented exactly once. This
            // is the runner-speed-independent guarantee that the workload
            // actually ran; it is separated from the timing judgment per the
            // zero-flaky policy (never let a wall-clock threshold mask or
            // manufacture a correctness failure).
            Assert.AreEqual(
                RepresentativeCycles,
                total,
                "Representative cycle count drifted (expected {0}, got {1}); the speed budget proxy "
                    + "is no longer representative. Seed={2}. {3}",
                RepresentativeCycles,
                total,
                TestSeed,
                DescribeMessageBusState(MessageHandler.MessageBus, includeLog: true)
            );

            // TIMING (soft): a breach of the 5s target is an early-warning perf
            // signal only. Logged, never failed, because a tight wall-clock
            // bound flakes on slower CI runners for identical deterministic work.
            if (elapsed > RepresentativeSoftBudget)
            {
                UnityEngine.Debug.LogWarning(
                    $"SuiteSpeedBudgetTest: representative load took {elapsed.TotalSeconds:0.00}s "
                        + $"(soft target {RepresentativeSoftBudget.TotalSeconds:0.00}s, hard backstop "
                        + $"{RepresentativeHardBudget.TotalSeconds:0.00}s). Seed={TestSeed}. This is a "
                        + "non-blocking perf warning; if it regresses further the default Unity Edit+Play "
                        + "suite may approach its 60s wall-clock budget. "
                        + DescribeMessageBusState(MessageHandler.MessageBus)
                );
            }

            // TIMING (egregious hard backstop): only a catastrophic blow-out
            // fails the suite. See RepresentativeHardBudget for the
            // wide-multiplier justification.
            Assert.That(
                elapsed,
                Is.LessThan(RepresentativeHardBudget),
                $"Representative load took {elapsed.TotalSeconds:0.00}s, exceeding the egregious hard "
                    + $"backstop of {RepresentativeHardBudget.TotalSeconds:0.00}s (6x the "
                    + $"{RepresentativeSoftBudget.TotalSeconds:0.00}s soft target). A blow-out this large "
                    + $"indicates a genuine algorithmic regression, not runner slowness. Seed={TestSeed}. "
                    + DescribeMessageBusState(MessageHandler.MessageBus, includeLog: true)
            );
            yield break;
        }
    }
}

#endif
