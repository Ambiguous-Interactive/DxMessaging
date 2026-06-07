#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons.UnityAtoms
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Tests.Runtime.Comparisons;
    using NUnit.Framework;

    /// <summary>
    /// Data-driven entry point for the GATED Unity Atoms messaging comparison. One NUnit
    /// case is generated per (Unity Atoms, scenario) pair and is driven through the shared
    /// <see cref="ComparisonHarness"/>, so unsupported pairs are ignored and emit no row
    /// (the renderer shows N/A). This assembly only compiles when Unity Atoms is present
    /// (see the asmdef's defineConstraints), so the bridge can be referenced directly here.
    /// Kept in its OWN assembly so the Unity Atoms dependency can never break the proven
    /// zero-dependency or other external-package bridges.
    /// </summary>
    [Category("Performance"), Category("PerfBench"), Category("PerfComparison")]
    public sealed class UnityAtomsComparisonTests
    {
        private static IEnumerable<TestCaseData> Cases()
        {
            (string key, Func<IMessagingTechBridge> factory)[] techs =
            {
                ("UnityAtoms", () => new UnityAtomsBridge()),
            };
            foreach ((string key, Func<IMessagingTechBridge> factory) in techs)
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
