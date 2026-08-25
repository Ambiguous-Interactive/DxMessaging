#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons
{
    using System;
    using System.Globalization;
    using System.Text;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Tests.Runtime.Benchmarks;
    using NUnit.Framework;
    using UnityEngine;
    using Debug = UnityEngine.Debug;

    /// <summary>
    /// Runs two comparison bridges in counterbalanced blocks inside one player process. The
    /// second bridge acts as a common-mode control for host movement that changes absolute rates.
    /// </summary>
    public static class PairedComparisonHarness
    {
        private const string ScenarioPrefix = "PairedComparison";

        public static PairedBenchmarkMeasurement Run(
            Func<IMessagingTechBridge> firstFactory,
            Func<IMessagingTechBridge> secondFactory,
            ComparisonScenario scenario
        )
        {
            if (firstFactory == null)
            {
                throw new ArgumentNullException(nameof(firstFactory));
            }

            if (secondFactory == null)
            {
                throw new ArgumentNullException(nameof(secondFactory));
            }

            Assert.AreEqual(
                DiagnosticsTarget.Off,
                IMessageBus.GlobalDiagnosticsTargets,
                $"Paired comparison '{scenario}' requires global diagnostics targets to be off."
            );
            Assert.IsFalse(
                IMessageBus.GlobalDiagnosticsStackTraces,
                $"Paired comparison '{scenario}' requires global diagnostics stack traces to be off."
            );

            using IMessagingTechBridge first = firstFactory();
            using IMessagingTechBridge second = secondFactory();
            Assert.IsFalse(
                first.RequiresPlayMode && !Application.isPlaying,
                $"Paired comparison first bridge '{first.TechKey}' requires PlayMode."
            );
            Assert.IsFalse(
                second.RequiresPlayMode && !Application.isPlaying,
                $"Paired comparison second bridge '{second.TechKey}' requires PlayMode."
            );
            Assert.IsTrue(
                first.Supports(scenario),
                $"Paired comparison first bridge '{first.TechKey}' does not support '{scenario}'."
            );
            Assert.IsTrue(
                second.Supports(scenario),
                $"Paired comparison second bridge '{second.TechKey}' does not support '{scenario}'."
            );
            Assert.AreNotEqual(
                first.TechKey,
                second.TechKey,
                $"Paired comparison '{scenario}' requires two distinct technology keys."
            );

            long expectedFanOut = ComparisonScenarios.ExpectedInvocationsPerOperation(scenario);
            Assert.AreEqual(
                expectedFanOut,
                first.InvocationsPerOperation(scenario),
                $"Paired comparison first bridge '{first.TechKey}' declared the wrong fan-out for '{scenario}'."
            );
            Assert.AreEqual(
                expectedFanOut,
                second.InvocationsPerOperation(scenario),
                $"Paired comparison second bridge '{second.TechKey}' declared the wrong fan-out for '{scenario}'."
            );

            first.Prepare(scenario);
            second.Prepare(scenario);
            AllocationProbe.SettleHeapForMeasurement();
            long firstStartProgress = first.ProgressMarker;
            long secondStartProgress = second.ProgressMarker;
            int warmupEmits = ComparisonScenarios.WarmupEmits(scenario);

            PairedBenchmarkMeasurement measurement = BenchmarkProtocol.MeasurePaired(
                () => EmitMany(first, warmupEmits),
                () => EmitBatch(first),
                () => EmitMany(second, warmupEmits),
                () => EmitBatch(second)
            );

            AssertProgress(
                first,
                scenario,
                firstStartProgress,
                expectedFanOut,
                warmupEmits,
                measurement.First.TotalOperations
            );
            AssertProgress(
                second,
                scenario,
                secondStartProgress,
                expectedFanOut,
                warmupEmits,
                measurement.Second.TotalOperations
            );
            WriteEvidence(first, second, scenario, measurement);
            return measurement;
        }

        private static int EmitBatch(IMessagingTechBridge bridge)
        {
            EmitMany(bridge, BenchmarkProtocol.BatchSize);
            return BenchmarkProtocol.BatchSize;
        }

        private static void EmitMany(IMessagingTechBridge bridge, int count)
        {
            for (int index = 0; index < count; index++)
            {
                bridge.EmitOnce();
            }
        }

        private static void AssertProgress(
            IMessagingTechBridge bridge,
            ComparisonScenario scenario,
            long startProgress,
            long expectedFanOut,
            int warmupEmits,
            long measuredOperations
        )
        {
            long expectedDelta = expectedFanOut * (warmupEmits + measuredOperations);
            long observedDelta = bridge.ProgressMarker - startProgress;
            Assert.AreEqual(
                expectedDelta,
                observedDelta,
                $"Paired comparison '{scenario}' bridge '{bridge.TechKey}' fan-out mismatch: "
                    + $"expected delta {expectedDelta}, observed {observedDelta}. "
                    + $"fanOut={expectedFanOut}, warmup={warmupEmits}, measuredOps={measuredOperations}."
            );
        }

        private static void WriteEvidence(
            IMessagingTechBridge first,
            IMessagingTechBridge second,
            ComparisonScenario scenario,
            PairedBenchmarkMeasurement measurement
        )
        {
            string scenarioKey = ComparisonScenarios.Key(scenario);
            DispatchBenchmarkResult firstResult = DispatchBenchmarkResult.ForEmitScenario(
                $"{ScenarioPrefix}_{first.TechKey}_{scenarioKey}",
                runIndex: -1,
                measurement.First.OperationsPerSecond,
                AllocationProbe.Unmeasured,
                AllocationProbe.Unmeasured,
                measurement.First.ElapsedSeconds * 1000d
            );
            DispatchBenchmarkResult secondResult = DispatchBenchmarkResult.ForEmitScenario(
                $"{ScenarioPrefix}_{second.TechKey}_{scenarioKey}",
                runIndex: -1,
                measurement.Second.OperationsPerSecond,
                AllocationProbe.Unmeasured,
                AllocationProbe.Unmeasured,
                measurement.Second.ElapsedSeconds * 1000d
            );
            Debug.Log(firstResult.ToStructuredLog());
            Debug.Log(secondResult.ToStructuredLog());
            TestContext.Out.WriteLine(firstResult.ToCsvRow());
            TestContext.Out.WriteLine(secondResult.ToCsvRow());

            string evidence = BuildEvidenceJson(
                first,
                second,
                scenarioKey,
                firstResult,
                measurement
            );
            if (
                measurement.CycleRatioSpreadPercent > BenchmarkProtocol.PairedMaterialityBandPercent
            )
            {
                Debug.LogWarning($"DXM_PAIRED_COMPARISON {evidence}");
            }
            else
            {
                Debug.Log($"DXM_PAIRED_COMPARISON {evidence}");
            }
            TestContext.Out.WriteLine($"DXM_PAIRED_COMPARISON {evidence}");
        }

        private static string BuildEvidenceJson(
            IMessagingTechBridge first,
            IMessagingTechBridge second,
            string scenarioKey,
            DispatchBenchmarkResult result,
            PairedBenchmarkMeasurement measurement
        )
        {
            StringBuilder builder = new();
            builder.Append('{');
            AppendJsonString(builder, "scenario", scenarioKey);
            builder.Append(',');
            AppendJsonString(builder, "first", first.TechKey);
            builder.Append(',');
            AppendJsonString(builder, "second", second.TechKey);
            builder.Append(',');
            AppendJsonString(builder, "platform", result.Platform);
            builder.Append(',');
            AppendJsonString(builder, "commit", result.Commit);
            builder.Append(',');
            AppendJsonString(builder, "protocol", BenchmarkProtocol.PairedProtocolId);
            builder.Append(",\"cycles\":");
            builder.Append(BenchmarkProtocol.PairedMeasurementCycles);
            builder.Append(",\"minimumCycleActiveMilliseconds\":");
            builder.Append(BenchmarkProtocol.PairedMinimumCycleActiveMilliseconds);
            builder.Append(",\"batchOperations\":");
            builder.Append(BenchmarkProtocol.BatchSize);
            builder.Append(",\"firstToSecondRatio\":");
            builder.Append(
                measurement.FirstToSecondRatio.ToString("R", CultureInfo.InvariantCulture)
            );
            builder.Append(",\"aggregateRateRatio\":");
            builder.Append(
                measurement.AggregateRateRatio.ToString("R", CultureInfo.InvariantCulture)
            );
            builder.Append(",\"cycleRatioSpreadPercent\":");
            builder.Append(
                measurement.CycleRatioSpreadPercent.ToString("R", CultureInfo.InvariantCulture)
            );
            builder.Append(",\"cycleRatios\":[");
            for (int index = 0; index < measurement.CycleRatios.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }
                builder.Append(
                    measurement.CycleRatios[index].ToString("R", CultureInfo.InvariantCulture)
                );
            }
            builder.Append("],\"cycleMeasurements\":[");
            for (int index = 0; index < measurement.Cycles.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                PairedCycleMeasurement cycle = measurement.Cycles[index];
                builder.Append("{\"firstOperations\":");
                builder.Append(cycle.First.TotalOperations);
                builder.Append(",\"firstActiveSeconds\":");
                builder.Append(
                    cycle.First.ElapsedSeconds.ToString("R", CultureInfo.InvariantCulture)
                );
                builder.Append(",\"secondOperations\":");
                builder.Append(cycle.Second.TotalOperations);
                builder.Append(",\"secondActiveSeconds\":");
                builder.Append(
                    cycle.Second.ElapsedSeconds.ToString("R", CultureInfo.InvariantCulture)
                );
                builder.Append(",\"firstToSecondRatio\":");
                builder.Append(
                    cycle.FirstToSecondRatio.ToString("R", CultureInfo.InvariantCulture)
                );
                builder.Append('}');
            }
            builder.Append("]}");
            return builder.ToString();
        }

        private static void AppendJsonString(StringBuilder builder, string name, string value)
        {
            builder.Append('\"');
            AppendJsonEscaped(builder, name);
            builder.Append("\":\"");
            AppendJsonEscaped(builder, value);
            builder.Append('\"');
        }

        private static void AppendJsonEscaped(StringBuilder builder, string value)
        {
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                switch (character)
                {
                    case '\"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\b':
                        builder.Append("\\b");
                        break;
                    case '\f':
                        builder.Append("\\f");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (character < ' ')
                        {
                            builder.Append("\\u");
                            builder.Append(
                                ((int)character).ToString("x4", CultureInfo.InvariantCulture)
                            );
                        }
                        else
                        {
                            builder.Append(character);
                        }
                        break;
                }
            }
        }
    }
}
#endif
