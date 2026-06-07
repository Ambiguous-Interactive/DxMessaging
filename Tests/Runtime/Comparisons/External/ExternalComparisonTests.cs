#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons.External
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Tests.Runtime.Comparisons;
    using NUnit.Framework;

    /// <summary>
    /// Data-driven entry point for the GATED external-package messaging technologies
    /// (MessagePipe, UniRx, Zenject SignalBus). One NUnit case is generated per
    /// (tech, scenario) pair and is driven through the shared <see cref="ComparisonHarness"/>,
    /// so unsupported pairs are ignored and emit no row (the renderer shows N/A). This
    /// assembly only compiles when every external package is present (see the asmdef's
    /// defineConstraints), so each bridge can be referenced directly here.
    /// </summary>
    [Category("Performance"), Category("PerfBench"), Category("PerfComparison")]
    public sealed class ExternalComparisonTests
    {
        private static IEnumerable<TestCaseData> Cases()
        {
            (string key, Func<IMessagingTechBridge> factory)[] techs =
            {
                ("MessagePipe", () => new MessagePipeBridge()),
                ("UniRx", () => new UniRxBridge()),
                ("ZenjectSignalBus", () => new ZenjectSignalBusBridge()),
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
