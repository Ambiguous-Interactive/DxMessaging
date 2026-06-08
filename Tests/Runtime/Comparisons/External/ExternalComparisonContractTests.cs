#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons.External
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Tests.Runtime.Comparisons;
    using NUnit.Framework;

    /// <summary>
    /// Fast contract suite for the gated external-package bridges (MessagePipe, UniRx,
    /// Zenject SignalBus). It runs the SAME identity + EmitOnce accounting checks as the
    /// zero-dependency <see cref="ComparisonContractTests"/>, but for the bridges that only
    /// compile when their packages are present. It opens no benchmark window, so a broken
    /// bridge -- e.g. a fan-out that Zenject's value-equality dedup collapsed -- fails here in
    /// milliseconds with a precise message instead of inside the multi-minute performance run.
    /// </summary>
    [Category("Comparison")]
    public sealed class ExternalComparisonContractTests
    {
        private static readonly (string key, Func<IMessagingTechBridge> factory)[] Bridges =
        {
            ("MessagePipe", () => new MessagePipeBridge()),
            ("UniRx", () => new UniRxBridge()),
            ("ZenjectSignalBus", () => new ZenjectSignalBusBridge()),
        };

        private static IEnumerable<TestCaseData> BridgeCases() =>
            ComparisonBridgeContract.IdentityCases(Bridges);

        private static IEnumerable<TestCaseData> BridgeScenarioCases() =>
            ComparisonBridgeContract.EmitOnceAccountingCases(Bridges);

        [Test]
        [TestCaseSource(nameof(BridgeCases))]
        public void BridgeHasConsistentTechIdentity(
            string rosterKey,
            Func<IMessagingTechBridge> factory
        )
        {
            ComparisonBridgeContract.AssertTechIdentity(rosterKey, factory);
        }

        [Test]
        [TestCaseSource(nameof(BridgeScenarioCases))]
        public void SupportedScenarioEmitOnceAdvancesProgressByDeclaredFanOut(
            string rosterKey,
            Func<IMessagingTechBridge> factory,
            ComparisonScenario scenario
        )
        {
            ComparisonBridgeContract.AssertEmitOnceAccounting(rosterKey, factory, scenario);
        }

        [Test]
        [TestCaseSource(nameof(BridgeScenarioCases))]
        public void StructScenarioDispatchesNonPrimitiveStructPayload(
            string rosterKey,
            Func<IMessagingTechBridge> factory,
            ComparisonScenario scenario
        )
        {
            ComparisonBridgeContract.AssertStructScenarioPayloadFidelity(
                rosterKey,
                factory,
                scenario
            );
        }
    }
}
#endif
