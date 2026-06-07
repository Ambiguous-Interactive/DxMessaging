#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons.UnityAtoms
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Tests.Runtime.Comparisons;
    using NUnit.Framework;

    /// <summary>
    /// Fast contract suite for the gated Unity Atoms bridge. It runs the SAME identity +
    /// EmitOnce accounting checks as the zero-dependency <see cref="ComparisonContractTests"/>,
    /// but for the bridge that only compiles when Unity Atoms is present. It opens no benchmark
    /// window, so a fan-out that silently deduped (and would otherwise only surface as a
    /// fan-out mismatch deep in the performance run) fails here in milliseconds with a precise
    /// message. Kept in its OWN assembly so the Unity Atoms dependency can never break the other
    /// comparison bridges.
    /// </summary>
    [Category("Comparison")]
    public sealed class UnityAtomsComparisonContractTests
    {
        private static readonly (string key, Func<IMessagingTechBridge> factory)[] Bridges =
        {
            ("UnityAtoms", () => new UnityAtomsBridge()),
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
    }
}
#endif
