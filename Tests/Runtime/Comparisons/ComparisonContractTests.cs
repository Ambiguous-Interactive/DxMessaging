#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using NUnit.Framework;

    /// <summary>
    /// Fast contract test that locks the cross-library comparison conventions in place
    /// forever. It inspects metadata and runs one EmitOnce smoke check per supported
    /// zero-dependency bridge/scenario, but never opens a benchmark measurement window.
    /// Adding a comparison scenario or zero-dependency bridge without correct metadata or
    /// dispatch accounting fails this suite automatically.
    /// </summary>
    [Category("Comparison")]
    public sealed class ComparisonContractTests
    {
        private static IEnumerable<TestCaseData> ComparisonScenarioCases()
        {
            foreach (ComparisonScenario scenario in ComparisonScenarios.All)
            {
                yield return new TestCaseData(scenario).SetName($"ComparisonScenario_{scenario}");
            }
        }

        // Case generation lives in ComparisonBridgeContract so the zero-dependency roster and the
        // gated External/UnityAtoms rosters all enumerate cases from ONE source of truth.
        private static IEnumerable<TestCaseData> RosterCases() =>
            ComparisonBridgeContract.IdentityCases(ZeroDependencyComparisonRoster.Bridges);

        private static IEnumerable<TestCaseData> RosterScenarioCases() =>
            ComparisonBridgeContract.EmitOnceAccountingCases(
                ZeroDependencyComparisonRoster.Bridges
            );

        [Test]
        [TestCaseSource(nameof(ComparisonScenarioCases))]
        public void ComparisonScenarioHasNonEmptyKeyAndDisplayName(ComparisonScenario scenario)
        {
            Assert.IsNotEmpty(
                ComparisonScenarios.Key(scenario),
                $"Comparison scenario '{scenario}' must declare a non-empty stable Key."
            );
            Assert.IsNotEmpty(
                ComparisonScenarios.DisplayName(scenario),
                $"Comparison scenario '{scenario}' must declare a non-empty DisplayName."
            );
        }

        [Test]
        public void ComparisonScenarioKeysAreUnique()
        {
            string[] keys = ComparisonScenarios.All.Select(ComparisonScenarios.Key).ToArray();
            CollectionAssert.AllItemsAreUnique(
                keys,
                "Comparison scenario Keys must be unique; they are baked into the row scenario id and joined on by the renderer."
            );
        }

        [Test]
        public void ComparisonScenarioDisplayNamesAreUnique()
        {
            string[] displayNames = ComparisonScenarios
                .All.Select(ComparisonScenarios.DisplayName)
                .ToArray();
            CollectionAssert.AllItemsAreUnique(
                displayNames,
                "Comparison scenario DisplayNames must be unique so matrix column headers never collide."
            );
        }

        [Test]
        [TestCaseSource(nameof(RosterCases))]
        public void RosterBridgeHasNonEmptyTechMetadata(
            string rosterKey,
            Func<IMessagingTechBridge> factory
        )
        {
            ComparisonBridgeContract.AssertTechIdentity(rosterKey, factory);
        }

        [Test]
        public void RosterTechKeysAreUnique()
        {
            List<string> techKeys = new();
            foreach (
                (
                    string _,
                    Func<IMessagingTechBridge> factory
                ) in ZeroDependencyComparisonRoster.Bridges
            )
            {
                using IMessagingTechBridge bridge = factory();
                techKeys.Add(bridge.TechKey);
            }

            CollectionAssert.AllItemsAreUnique(
                techKeys,
                "Roster TechKeys must be unique; they are the matrix row identity."
            );
        }

        [Test]
        [TestCaseSource(nameof(RosterCases))]
        public void SupportedScenarioDeclaresPositiveFanOut(
            string rosterKey,
            Func<IMessagingTechBridge> factory
        )
        {
            using IMessagingTechBridge bridge = factory();
            foreach (ComparisonScenario scenario in ComparisonScenarios.All)
            {
                if (!bridge.Supports(scenario))
                {
                    continue;
                }

                Assert.Greater(
                    bridge.InvocationsPerOperation(scenario),
                    0,
                    $"Bridge '{rosterKey}' supports '{scenario}' so it must declare a positive fan-out "
                        + "(InvocationsPerOperation > 0)."
                );
            }
        }

        [Test]
        [TestCaseSource(nameof(RosterScenarioCases))]
        public void SupportedScenarioEmitOnceAdvancesProgressByDeclaredFanOut(
            string rosterKey,
            Func<IMessagingTechBridge> factory,
            ComparisonScenario scenario
        )
        {
            ComparisonBridgeContract.AssertEmitOnceAccounting(rosterKey, factory, scenario);
        }

        [Test]
        [TestCaseSource(nameof(RosterScenarioCases))]
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

        [Test]
        public void DxMessagingSupportsEveryComparisonScenario()
        {
            using IMessagingTechBridge dxMessaging = new DxMessagingBridge();
            foreach (ComparisonScenario scenario in ComparisonScenarios.All)
            {
                Assert.IsTrue(
                    dxMessaging.Supports(scenario),
                    $"DxMessaging is the full-featured baseline and must support every comparison scenario, including '{scenario}'."
                );
            }
        }

        [Test]
        public void RosterIncludesAllZeroDependencyBaselines()
        {
            HashSet<string> rosterKeys = new();
            foreach (
                (
                    string _,
                    Func<IMessagingTechBridge> factory
                ) in ZeroDependencyComparisonRoster.Bridges
            )
            {
                using IMessagingTechBridge bridge = factory();
                rosterKeys.Add(bridge.TechKey);
            }

            foreach (
                string requiredKey in new[]
                {
                    "DxMessaging",
                    "CsEvent",
                    "UnityEvent",
                    "ScriptableObject",
                    "UnitySendMessage",
                }
            )
            {
                Assert.IsTrue(
                    rosterKeys.Contains(requiredKey),
                    $"The zero-dependency roster must always include the '{requiredKey}' baseline; found [{string.Join(", ", rosterKeys)}]."
                );
            }
        }
    }
}
#endif
