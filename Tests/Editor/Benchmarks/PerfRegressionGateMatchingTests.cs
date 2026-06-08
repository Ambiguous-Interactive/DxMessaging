#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Editor.Benchmarks
{
    using System;
    using System.Collections.Generic;
    using NUnit.Framework;
    using BaselineRow = PerfRegressionSmokeTests.BaselineRow;

    /// <summary>
    /// Covers the baseline-row matching policy of the permanent perf regression gate.
    /// A committed master baseline reflects a single historical commit while CI runs at
    /// HEAD, so when <c>DX_PERF_BASELINE_COMMIT</c> is unset the gate must match a
    /// baseline row on (scenario, platform) only and ignore the commit column. When the
    /// environment variable IS set, the original commit-exact match is preserved for
    /// local and historical workflows. These EditMode tests drive the matching logic
    /// directly with in-memory baseline rows so they do not run a measurement window.
    /// </summary>
    public sealed class PerfRegressionGateMatchingTests
    {
        private const string Scenario = "UntargetedFlood_OneHandler";
        private const string Platform =
            "Editor PlayMode Mono x64 Release (WindowsEditor; Unity 6000.3.16f1)";

        [Test]
        public void FindBaselineWithUnsetCommitMatchesOnScenarioAndPlatform()
        {
            BaselineRow expected = BaselineRow.ForTest(
                Scenario,
                Platform,
                "baseline",
                emitsPerSecond: 12_000_000d,
                allocatedBytesDelta: 0,
                wallClockMs: 5000d
            );
            List<BaselineRow> rows = new()
            {
                BaselineRow.ForTest(
                    "TargetedFlood_OneListener",
                    Platform,
                    "baseline",
                    emitsPerSecond: 9_000_000d,
                    allocatedBytesDelta: 0,
                    wallClockMs: 5000d
                ),
                expected,
            };

            BaselineRow match = PerfRegressionSmokeTests.FindBaseline(
                rows,
                Scenario,
                Platform,
                baselineCommit: null
            );

            Assert.AreEqual(expected.Scenario, match.Scenario);
            Assert.AreEqual(expected.Platform, match.Platform);
            Assert.AreEqual(expected.EmitsPerSecond, match.EmitsPerSecond);
        }

        [Test]
        public void FindBaselineWithUnsetCommitIgnoresCommitWhenRowCommitDiffersFromHead()
        {
            // The committed baseline carries a placeholder/old commit; the run is at HEAD.
            // With the commit column ignored, the row still matches on scenario+platform.
            BaselineRow expected = BaselineRow.ForTest(
                Scenario,
                Platform,
                "0123456789abcdef0123456789abcdef01234567",
                emitsPerSecond: 12_000_000d,
                allocatedBytesDelta: 0,
                wallClockMs: 5000d
            );
            List<BaselineRow> rows = new() { expected };

            BaselineRow match = PerfRegressionSmokeTests.FindBaseline(
                rows,
                Scenario,
                Platform,
                baselineCommit: string.Empty
            );

            Assert.AreEqual(expected.Commit, match.Commit);
            Assert.AreEqual(expected.EmitsPerSecond, match.EmitsPerSecond);
        }

        [Test]
        public void FindBaselineWithConfiguredCommitMatchesThatCommitExactly()
        {
            const string TargetCommit = "feedface00000000000000000000000000000000";
            BaselineRow other = BaselineRow.ForTest(
                Scenario,
                Platform,
                "0000000000000000000000000000000000000000",
                emitsPerSecond: 5_000_000d,
                allocatedBytesDelta: 0,
                wallClockMs: 5000d
            );
            BaselineRow expected = BaselineRow.ForTest(
                Scenario,
                Platform,
                TargetCommit,
                emitsPerSecond: 12_000_000d,
                allocatedBytesDelta: 0,
                wallClockMs: 5000d
            );
            List<BaselineRow> rows = new() { other, expected };

            BaselineRow match = PerfRegressionSmokeTests.FindBaseline(
                rows,
                Scenario,
                Platform,
                TargetCommit
            );

            Assert.AreEqual(TargetCommit, match.Commit);
            Assert.AreEqual(expected.EmitsPerSecond, match.EmitsPerSecond);
        }

        [Test]
        public void FindBaselineWithConfiguredCommitIsCaseInsensitive()
        {
            const string RowCommit = "ABCDEF1234567890ABCDEF1234567890ABCDEF12";
            BaselineRow expected = BaselineRow.ForTest(
                Scenario,
                Platform,
                RowCommit,
                emitsPerSecond: 12_000_000d,
                allocatedBytesDelta: 0,
                wallClockMs: 5000d
            );
            List<BaselineRow> rows = new() { expected };

            BaselineRow match = PerfRegressionSmokeTests.FindBaseline(
                rows,
                Scenario,
                Platform,
                RowCommit.ToLowerInvariant()
            );

            Assert.AreEqual(RowCommit, match.Commit);
        }

        [Test]
        public void FindBaselineWithUnsetCommitSkipsWhenNoRowMatchesScenarioAndPlatform()
        {
            // A contributor on a different Unity version/OS than the captured baseline
            // has no matching row. For this LOCAL tool that must SKIP, not FAIL, so the
            // gate surfaces as ignored rather than a spurious red.
            List<BaselineRow> rows = new()
            {
                BaselineRow.ForTest(
                    "TargetedFlood_OneListener",
                    Platform,
                    "baseline",
                    emitsPerSecond: 9_000_000d,
                    allocatedBytesDelta: 0,
                    wallClockMs: 5000d
                ),
            };

            Assert.Throws<IgnoreException>(() =>
                PerfRegressionSmokeTests.FindBaseline(
                    rows,
                    Scenario,
                    Platform,
                    baselineCommit: null
                )
            );
        }

        [Test]
        public void FindBaselineWithConfiguredCommitSkipsWhenNoRowMatchesThatCommit()
        {
            // Commit-exact matching is still enforced when DX_PERF_BASELINE_COMMIT is set,
            // but a missing match now skips gracefully instead of failing.
            const string TargetCommit = "feedface00000000000000000000000000000000";
            List<BaselineRow> rows = new()
            {
                BaselineRow.ForTest(
                    Scenario,
                    Platform,
                    "0000000000000000000000000000000000000000",
                    emitsPerSecond: 12_000_000d,
                    allocatedBytesDelta: 0,
                    wallClockMs: 5000d
                ),
            };

            Assert.Throws<IgnoreException>(() =>
                PerfRegressionSmokeTests.FindBaseline(rows, Scenario, Platform, TargetCommit)
            );
        }

        [Test]
        public void GetBaselineCommitReturnsNullWhenEnvironmentVariableIsUnset()
        {
            string original = Environment.GetEnvironmentVariable(
                PerfRegressionSmokeTests.BaselineCommitEnvVar
            );
            try
            {
                Environment.SetEnvironmentVariable(
                    PerfRegressionSmokeTests.BaselineCommitEnvVar,
                    null
                );

                Assert.IsNull(PerfRegressionSmokeTests.GetBaselineCommit());
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    PerfRegressionSmokeTests.BaselineCommitEnvVar,
                    original
                );
            }
        }

        [Test]
        public void GetBaselineCommitReturnsNullWhenEnvironmentVariableIsWhitespace()
        {
            string original = Environment.GetEnvironmentVariable(
                PerfRegressionSmokeTests.BaselineCommitEnvVar
            );
            try
            {
                Environment.SetEnvironmentVariable(
                    PerfRegressionSmokeTests.BaselineCommitEnvVar,
                    "   "
                );

                Assert.IsNull(PerfRegressionSmokeTests.GetBaselineCommit());
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    PerfRegressionSmokeTests.BaselineCommitEnvVar,
                    original
                );
            }
        }

        [Test]
        public void GetBaselineCommitReturnsConfiguredValueWhenEnvironmentVariableIsSet()
        {
            const string ConfiguredCommit = "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef";
            string original = Environment.GetEnvironmentVariable(
                PerfRegressionSmokeTests.BaselineCommitEnvVar
            );
            try
            {
                Environment.SetEnvironmentVariable(
                    PerfRegressionSmokeTests.BaselineCommitEnvVar,
                    ConfiguredCommit
                );

                Assert.AreEqual(ConfiguredCommit, PerfRegressionSmokeTests.GetBaselineCommit());
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    PerfRegressionSmokeTests.BaselineCommitEnvVar,
                    original
                );
            }
        }
    }
}
#endif
