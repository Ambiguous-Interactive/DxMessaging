#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons.UnityAtoms
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Tests.Runtime.Benchmarks;
    using DxMessaging.Tests.Runtime.Comparisons;
    using NUnit.Framework;
    using UnityEngine;

    /// <summary>
    /// Fast contract suite for the gated Unity Atoms bridge. It runs the SAME identity +
    /// EmitOnce accounting checks as the zero-dependency <see cref="ComparisonContractTests"/>,
    /// but for the bridge that only compiles when Unity Atoms is present. It opens no benchmark
    /// window, so a fan-out that silently deduped (and would otherwise only surface as a
    /// fan-out mismatch deep in the performance run) fails here in milliseconds with a precise
    /// message. Kept in its OWN assembly so the Unity Atoms dependency can never break the other
    /// comparison bridges.
    /// </summary>
    [Category("ComparisonContract")]
    public sealed class UnityAtomsComparisonContractTests
    {
        private const int AllocationEmits = 1024;
        private const int AllocationAttempts = 16;

        private static IEnumerable<TestCaseData> BridgeCases() =>
            ComparisonBridgeContract.IdentityCases(UnityAtomsComparisonRoster.Bridges);

        private static IEnumerable<TestCaseData> BridgeScenarioCases() =>
            ComparisonBridgeContract.EmitOnceAccountingCases(UnityAtomsComparisonRoster.Bridges);

        private static IEnumerable<TestCaseData> SupportedScenarioCases()
        {
            using UnityAtomsBridge bridge = new();
            foreach (ComparisonScenario scenario in ComparisonScenarios.All)
            {
                if (bridge.Supports(scenario))
                {
                    yield return new TestCaseData(scenario);
                }
            }
        }

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

        [Test]
        [TestCaseSource(nameof(SupportedScenarioCases))]
        public void PreparedEventsDisableReplayBuffer(ComparisonScenario scenario)
        {
            using UnityAtomsBridge bridge = new();
            bridge.Prepare(scenario);

            Assert.IsNotEmpty(
                bridge.CreatedEvents,
                $"Unity Atoms '{scenario}' must create at least one event for dispatch."
            );
            foreach (ScriptableObject created in bridge.CreatedEvents)
            {
                System.Reflection.PropertyInfo replayBufferSize = created
                    .GetType()
                    .GetProperty("ReplayBufferSize");
                Assert.IsNotNull(
                    replayBufferSize,
                    $"Unity Atoms event type '{created.GetType().FullName}' must expose ReplayBufferSize."
                );
                Assert.AreEqual(
                    0,
                    replayBufferSize.GetValue(created),
                    $"Unity Atoms '{scenario}' must disable replay buffering on every created event."
                );
            }
        }

        [Test]
        [TestCaseSource(nameof(SupportedScenarioCases))]
        public void DisposeDestroysPreparedEventsSynchronously(ComparisonScenario scenario)
        {
            UnityAtomsBridge bridge = new();
            try
            {
                bridge.Prepare(scenario);
                List<ScriptableObject> createdEvents = new(bridge.CreatedEvents);
                Assert.IsNotEmpty(
                    createdEvents,
                    $"Unity Atoms '{scenario}' must create at least one event for dispatch."
                );

                bridge.Dispose();

                foreach (ScriptableObject created in createdEvents)
                {
                    Assert.IsTrue(
                        created == null,
                        $"Dispose must synchronously destroy every Unity Atoms event from '{scenario}'."
                    );
                }
            }
            finally
            {
                bridge.Dispose();
            }
        }

        [Test]
        [Category("Allocation")]
        public void StructDispatchAllocatesLessThanOneObjectPerEmit()
        {
            using UnityAtomsBridge bridge = new();
            bridge.Prepare(ComparisonScenario.StructMessageNoBoxing);
            for (int index = 0; index < AllocationEmits; index++)
            {
                bridge.EmitOnce();
            }

            long floor = AllocationProbe.MeasureMin(
                AllocationAttempts,
                prepare: null,
                operation: () =>
                {
                    for (int index = 0; index < AllocationEmits; index++)
                    {
                        bridge.EmitOnce();
                    }
                }
            );
            if (floor == AllocationProbe.Unmeasured)
            {
                Assert.Ignore("GC.Alloc probe is non-functional on this backend.");
            }

            Assert.Less(
                floor,
                AllocationEmits,
                $"Unity Atoms struct dispatch allocated a floor of {floor} managed objects over "
                    + $"{AllocationEmits} emits; a boxing-free path must allocate less than one object per emit."
            );
        }
    }
}
#endif
