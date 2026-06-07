#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using NUnit.Framework;

    /// <summary>
    /// Fast STATIC contract test that locks the cross-library comparison conventions in place
    /// forever. It never runs a measurement window; it only inspects metadata
    /// (Key/DisplayName/TechKey/TechName/Supports/InvocationsPerOperation) so it stays in the
    /// quick gate. Adding a comparison scenario or zero-dependency bridge without correct
    /// metadata fails this suite automatically.
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

        private static IEnumerable<TestCaseData> RosterCases()
        {
            foreach (
                (
                    string key,
                    Func<IMessagingTechBridge> factory
                ) in ZeroDependencyComparisonRoster.Bridges
            )
            {
                yield return new TestCaseData(key, factory).SetName($"Bridge_{key}");
            }
        }

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
            using IMessagingTechBridge bridge = factory();
            Assert.IsNotEmpty(
                bridge.TechKey,
                $"Roster bridge '{rosterKey}' must declare a non-empty TechKey."
            );
            Assert.IsNotEmpty(
                bridge.TechName,
                $"Roster bridge '{rosterKey}' must declare a non-empty TechName."
            );
            Assert.AreEqual(
                rosterKey,
                bridge.TechKey,
                $"Roster key '{rosterKey}' must match the bridge's own TechKey '{bridge.TechKey}'."
            );
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
