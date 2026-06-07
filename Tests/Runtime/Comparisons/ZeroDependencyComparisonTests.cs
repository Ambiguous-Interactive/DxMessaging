#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons
{
    using System;
    using System.Collections.Generic;
    using NUnit.Framework;

    /// <summary>
    /// Data-driven entry point for the zero-dependency messaging technologies. One NUnit
    /// case is generated per (tech, scenario) pair; unsupported pairs are ignored by the
    /// shared <see cref="ComparisonHarness"/> so the renderer shows N/A for them. Gated
    /// package bridges (Zenject, MessagePipe, UniRx, ...) arrive in a later slice and
    /// reuse the same harness from their own assembly.
    /// </summary>
    [Category("Performance"), Category("PerfBench"), Category("PerfComparison")]
    public sealed class ZeroDependencyComparisonTests
    {
        private static IEnumerable<TestCaseData> Cases()
        {
            foreach (
                (
                    string key,
                    Func<IMessagingTechBridge> factory
                ) in ZeroDependencyComparisonRoster.Bridges
            )
            {
                foreach (ComparisonScenario scenario in ComparisonScenarios.All)
                {
                    yield return new TestCaseData(factory, scenario).SetName(
                        $"Comparison_{key}_{ComparisonScenarios.Key(scenario)}"
                    );
                }
            }
        }

        [Test, Category("PerfBench"), Category("PerfComparison")]
        [TestCaseSource(nameof(Cases))]
        public void Benchmark(Func<IMessagingTechBridge> bridgeFactory, ComparisonScenario scenario)
        {
            ComparisonHarness.Run(bridgeFactory, scenario);
        }
    }
}
#endif
