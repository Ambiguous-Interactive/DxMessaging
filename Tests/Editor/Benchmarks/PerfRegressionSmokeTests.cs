#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Editor.Benchmarks
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using DxMessaging.Tests.Runtime.Benchmarks;
    using NUnit.Framework;

    public sealed class PerfRegressionSmokeTests
    {
        private const string PerfGateEnvVar = "DX_PERF_GATE";
        private const string BaselinePathEnvVar = "DX_PERF_BASELINE";
        internal const string BaselineCommitEnvVar = "DX_PERF_BASELINE_COMMIT";
        private const double RegressionMultiplier = 1.5d;

        [Test, Explicit, Category("PerfGate")]
        public void UntargetedFloodOneHandler()
        {
            RunGate(DispatchBenchmarkScenario.UntargetedFloodOneHandler);
        }

        [Test, Explicit, Category("PerfGate")]
        public void UntargetedFloodFourHandlersOnePriority()
        {
            RunGate(DispatchBenchmarkScenario.UntargetedFloodFourHandlersOnePriority);
        }

        [Test, Explicit, Category("PerfGate")]
        public void UntargetedFloodFourHandlersFourPriorities()
        {
            RunGate(DispatchBenchmarkScenario.UntargetedFloodFourHandlersFourPriorities);
        }

        [Test, Explicit, Category("PerfGate")]
        public void TargetedFloodOneListener()
        {
            RunGate(DispatchBenchmarkScenario.TargetedFloodOneListener);
        }

        [Test, Explicit, Category("PerfGate")]
        public void TargetedFloodSixteenListeners()
        {
            RunGate(DispatchBenchmarkScenario.TargetedFloodSixteenListeners);
        }

        [Test, Explicit, Category("PerfGate")]
        public void BroadcastFloodOneHandler()
        {
            RunGate(DispatchBenchmarkScenario.BroadcastFloodOneHandler);
        }

        [Test, Explicit, Category("PerfGate")]
        public void InterceptorHeavyFourInterceptors()
        {
            RunGate(DispatchBenchmarkScenario.InterceptorHeavyFourInterceptors);
        }

        [Test, Explicit, Category("PerfGate")]
        public void PostProcessingHeavyFourPostProcessors()
        {
            RunGate(DispatchBenchmarkScenario.PostProcessingHeavyFourPostProcessors);
        }

        [Test, Explicit, Category("PerfGate")]
        public void RegistrationFlood1000TypesFromColdBus()
        {
            RunGate(DispatchBenchmarkScenario.RegistrationFlood1000TypesFromColdBus);
        }

        private static void RunGate(DispatchBenchmarkScenario scenario)
        {
            if (Environment.GetEnvironmentVariable(PerfGateEnvVar) != "1")
            {
                Assert.Ignore($"{PerfGateEnvVar}=1 is required to run the perf smoke gate.");
            }

            DispatchBenchmarkResult current = DispatchThroughputBenchmarks.RunScenario(scenario);
            IReadOnlyList<BaselineRow> baselines = LoadBaselines();
            string scenarioName = DispatchThroughputBenchmarks.GetScenarioName(scenario);
            string baselineCommit = GetBaselineCommit();
            BaselineRow baseline = FindBaseline(
                baselines,
                scenarioName,
                current.Platform,
                baselineCommit
            );

            if (current.IsRegistrationScenario)
            {
                Assert.LessOrEqual(
                    current.WallClockMs,
                    baseline.WallClockMs * RegressionMultiplier,
                    $"{scenarioName} registration wall-clock regressed more than {RegressionMultiplier:0.0}x."
                );
                return;
            }

            double minimumAllowedEmitsPerSecond = baseline.EmitsPerSecond / RegressionMultiplier;
            Assert.GreaterOrEqual(
                current.EmitsPerSecond,
                minimumAllowedEmitsPerSecond,
                $"{scenarioName} throughput regressed more than {RegressionMultiplier:0.0}x."
            );

            long allocationBudgetBytes = Math.Max(0, baseline.AllocatedBytesDelta);
            Assert.LessOrEqual(
                current.AllocatedBytesDelta,
                allocationBudgetBytes,
                $"{scenarioName} allocated {current.AllocatedBytesDelta.ToString(CultureInfo.InvariantCulture)} bytes, exceeding the baseline allocation budget of {allocationBudgetBytes.ToString(CultureInfo.InvariantCulture)} bytes."
            );
        }

        private static IReadOnlyList<BaselineRow> LoadBaselines()
        {
            string configuredPath = Environment.GetEnvironmentVariable(BaselinePathEnvVar);
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                Assert.Ignore(
                    $"{BaselinePathEnvVar}=<baseline.csv> is required to run the perf smoke gate."
                );
            }

            string path = ResolvePath(configuredPath);
            if (!File.Exists(path))
            {
                Assert.Ignore(
                    $"Performance baseline file not found: {configuredPath}. Capture a dispatch throughput baseline before enforcing PerfGate."
                );
            }

            List<BaselineRow> rows = new();
            foreach (string line in File.ReadAllLines(path))
            {
                if (
                    string.IsNullOrWhiteSpace(line)
                    || line.StartsWith("scenario,", StringComparison.OrdinalIgnoreCase)
                )
                {
                    continue;
                }

                rows.Add(BaselineRow.Parse(line));
            }

            Assert.Greater(rows.Count, 0, "Performance baseline file contains no data rows.");
            return rows;
        }

        internal static BaselineRow FindBaseline(
            IReadOnlyList<BaselineRow> rows,
            string scenario,
            string platform,
            string baselineCommit
        )
        {
            // A null/empty baselineCommit means DX_PERF_BASELINE_COMMIT was unset, so the
            // commit column is ignored and matching is on (scenario, platform) only. When
            // a commit IS configured, it must match exactly (case-insensitive).
            bool matchCommit = !string.IsNullOrWhiteSpace(baselineCommit);
            for (int index = 0; index < rows.Count; index++)
            {
                BaselineRow row = rows[index];
                if (
                    string.Equals(row.Scenario, scenario, StringComparison.Ordinal)
                    && string.Equals(row.Platform, platform, StringComparison.Ordinal)
                    && (
                        !matchCommit
                        || string.Equals(
                            row.Commit,
                            baselineCommit,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                )
                {
                    return row;
                }
            }

            // This is a LOCAL/manual tool. A contributor running on a different Unity
            // version or OS than the captured baseline will have no matching row, which
            // is expected rather than a failure, so skip gracefully. The commit-exact
            // path (DX_PERF_BASELINE_COMMIT set) likewise skips when no row matches.
            string commitQualifier = matchCommit ? $"{baselineCommit} " : string.Empty;
            Assert.Ignore(
                $"No {commitQualifier}baseline row found for scenario {scenario} on platform {platform}. "
                    + "Capture a baseline on this Unity version and platform to enable the local perf smoke gate."
            );
            return default;
        }

        internal static string GetBaselineCommit()
        {
            // When DX_PERF_BASELINE_COMMIT is unset or empty, the gate matches the
            // baseline row on (scenario, platform) only. A committed master baseline
            // reflects one historical commit while CI runs at HEAD, so commit-exact
            // matching would make a permanent gate impossible. Returning null here is
            // the signal to FindBaseline to ignore the commit column. When the env var
            // IS set, the original commit-exact path is preserved for local and
            // historical workflows.
            string configuredCommit = Environment.GetEnvironmentVariable(BaselineCommitEnvVar);
            return string.IsNullOrWhiteSpace(configuredCommit) ? null : configuredCommit;
        }

        private static string ResolvePath(string configuredPath)
        {
            if (Path.IsPathRooted(configuredPath))
            {
                return configuredPath;
            }

            DirectoryInfo current = new(Directory.GetCurrentDirectory());
            while (current != null)
            {
                string candidate = Path.Combine(current.FullName, configuredPath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                current = current.Parent;
            }

            return Path.Combine(Directory.GetCurrentDirectory(), configuredPath);
        }

        internal readonly struct BaselineRow
        {
            private BaselineRow(
                string scenario,
                string platform,
                string commit,
                double emitsPerSecond,
                long allocatedBytesDelta,
                double wallClockMs
            )
            {
                Scenario = scenario;
                Platform = platform;
                Commit = commit;
                EmitsPerSecond = emitsPerSecond;
                AllocatedBytesDelta = allocatedBytesDelta;
                WallClockMs = wallClockMs;
            }

            public string Scenario { get; }

            public string Platform { get; }

            public string Commit { get; }

            public double EmitsPerSecond { get; }

            public long AllocatedBytesDelta { get; }

            public double WallClockMs { get; }

            /// <summary>
            /// Test-only factory that builds a row directly from field values, so the
            /// gate's matching logic can be exercised in EditMode without going through
            /// CSV text or running a measurement window.
            /// </summary>
            internal static BaselineRow ForTest(
                string scenario,
                string platform,
                string commit,
                double emitsPerSecond,
                long allocatedBytesDelta,
                double wallClockMs
            )
            {
                return new BaselineRow(
                    scenario,
                    platform,
                    commit,
                    emitsPerSecond,
                    allocatedBytesDelta,
                    wallClockMs
                );
            }

            public static BaselineRow Parse(string line)
            {
                string[] parts = ParseCsvFields(line);
                if (parts.Length < 7)
                {
                    throw new FormatException($"Invalid baseline row: {line}");
                }

                return new BaselineRow(
                    parts[0],
                    parts[1],
                    parts[2],
                    double.Parse(parts[4], CultureInfo.InvariantCulture),
                    long.Parse(parts[5], CultureInfo.InvariantCulture),
                    double.Parse(parts[6], CultureInfo.InvariantCulture)
                );
            }

            private static string[] ParseCsvFields(string line)
            {
                List<string> fields = new();
                System.Text.StringBuilder builder = new();
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
        }
    }
}
#endif
