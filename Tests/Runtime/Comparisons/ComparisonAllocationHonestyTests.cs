#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Tests.Runtime.Benchmarks;
    using NUnit.Framework;
    using UnityEngine;

    /// <summary>
    /// Guards the HONESTY of the cross-library GC-allocation column. The comparison matrices
    /// exist to surface each technology's per-dispatch allocation cost, so a bridge must not
    /// flatter its own number by caching an allocation that idiomatic usage pays on every call.
    ///
    /// <para>
    /// Two directions are pinned, both via the real <see cref="AllocationProbe"/> over a batch
    /// (the minimum across attempts rejects warm-editor spikes; the floor is the true cost):
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// A bridge whose idiomatic API forces the value payload to BOX on every dispatch -- Unity
    /// <c>SendMessage</c>'s <c>(string, object)</c> signature has no generic overload -- must
    /// measure that box: the dispatch floor is at LEAST one managed allocation per call. A floor
    /// of zero means a pre-boxed/cached payload is hiding SendMessage's real cost and making the
    /// GC column read a misleading zero. Proven on the host editor (Unity 6000.4, PlayMode): a
    /// per-call box reads 1 allocation / ~20 bytes per dispatch, a pre-boxed payload 0/0.
    /// </description></item>
    /// <item><description>
    /// A bridge that advertises boxing-free struct dispatch (the no-boxing struct scenario,
    /// supported through a GENERIC delegate path) must actually allocate ZERO on that path, so
    /// the "no boxing" column is not silently lying in the other direction.
    /// </description></item>
    /// </list>
    /// This is the C# complement to the renderer surfacing a GC-allocated-BYTES comparison matrix:
    /// together they make the boxing cost visible AND impossible to hide.
    /// </summary>
    [Category("Comparison"), Category("Allocation")]
    public sealed class ComparisonAllocationHonestyTests
    {
        // The discriminator is per-dispatch allocation RATE: forced boxing is a DETERMINISTIC one
        // allocation per call (floor == Emits), while warm-editor / PlayMode background allocation
        // stays far under one per call. Measured on the host editor as per-call RATES (so they are
        // batch-independent): a non-boxing path floors near 0/call; even pre-boxed SendMessage
        // floored at ~0.27/call. So the honest threshold is "at least one allocation per dispatch"
        // (floor >= Emits) for a forced-boxing path and "fewer than one per dispatch" (floor <
        // Emits) for a boxing-free path -- the 1.0 vs ~0.27 per-call gap is a >= 3.7x margin. A large
        // batch keeps the box signal unambiguous; many attempts feed AllocationProbe.MeasureMin,
        // whose MINIMUM rejects the intermittent warm-editor spikes (a spike only ADDS, so it never
        // lowers the floor). Both stay synchronous (no 5-second window), so the fixture stays fast.
        private const int Emits = 1024;
        private const int Attempts = 16;

        private static IEnumerable<TestCaseData> SendMessageBoxingScenarios()
        {
            using IMessagingTechBridge probe = new UnitySendMessageBridge();
            foreach (ComparisonScenario scenario in ComparisonScenarios.All)
            {
                if (!probe.Supports(scenario))
                {
                    continue;
                }
                yield return new TestCaseData(scenario).SetName(
                    $"SendMessageBoxesEveryCall_{ComparisonScenarios.Key(scenario)}"
                );
            }
        }

        private static IEnumerable<TestCaseData> BoxingFreeStructBridges()
        {
            foreach (
                (
                    string key,
                    Func<IMessagingTechBridge> factory
                ) in ZeroDependencyComparisonRoster.Bridges
            )
            {
                // DxMessaging's zero-allocation dispatch is pinned exhaustively by the editor
                // allocation matrix; here we pin the OTHER zero-dependency bridges that claim a
                // boxing-free struct path so the comparison's "no boxing" column cannot drift.
                if (string.Equals(key, "DxMessaging", StringComparison.Ordinal))
                {
                    continue;
                }
                using IMessagingTechBridge probe = factory();
                if (!probe.Supports(ComparisonScenario.StructMessageNoBoxing))
                {
                    continue;
                }
                yield return new TestCaseData(factory).SetName($"BoxingFreeStructDispatch_{key}");
            }
        }

        [Test]
        [TestCaseSource(nameof(SendMessageBoxingScenarios))]
        public void UnitySendMessageDispatchBoxesValuePayloadEveryCall(ComparisonScenario scenario)
        {
            if (!Application.isPlaying)
            {
                Assert.Ignore(
                    "Unity SendMessage requires PlayMode for a faithful allocation measurement "
                        + "(EditMode adds editor-only reflection instrumentation that is not shipped)."
                );
            }

            long floor = MeasureDispatchAllocationFloor(new UnitySendMessageBridge(), scenario);
            if (floor == AllocationProbe.Unmeasured)
            {
                Assert.Ignore("GC.Alloc probe is non-functional on this backend.");
            }

            // SendMessage(string, object) boxes the value payload on EVERY call -- there is no
            // generic overload -- so a batch of Emits dispatches must allocate at least one managed
            // object per dispatch. A floor below the dispatch count means the bridge is reusing a
            // pre-boxed payload, which hides SendMessage's unavoidable per-call boxing and makes the
            // comparison GC-allocation column report a misleading zero. RED-GREEN: caching
            // "private static readonly object PingPayload = 0;" drops this floor to 0; passing the
            // value so it boxes per call restores it to ~Emits.
            Assert.GreaterOrEqual(
                floor,
                Emits,
                $"Unity SendMessage '{ComparisonScenarios.DisplayName(scenario)}' allocated a floor of "
                    + $"{floor} managed objects over {Emits} dispatches; expected at least {Emits} (one box per "
                    + "call). A floor below the dispatch count means the bridge is reusing a pre-boxed payload, "
                    + "which hides SendMessage's unavoidable per-call boxing and makes the comparison "
                    + "GC-allocation column report a misleading zero. Pass the value payload so it boxes on every "
                    + "call instead of caching one boxed object."
            );
        }

        [Test]
        [TestCaseSource(nameof(BoxingFreeStructBridges))]
        public void BoxingFreeBridgeStructDispatchAllocatesNothing(
            Func<IMessagingTechBridge> factory
        )
        {
            using IMessagingTechBridge identity = factory();
            string techKey = identity.TechKey;

            long floor = MeasureDispatchAllocationFloor(
                factory(),
                ComparisonScenario.StructMessageNoBoxing
            );
            if (floor == AllocationProbe.Unmeasured)
            {
                Assert.Ignore("GC.Alloc probe is non-functional on this backend.");
            }

            // The no-boxing struct scenario dispatches a non-primitive struct through a generic
            // delegate, so the steady-state path must NOT box: fewer than one managed allocation
            // per dispatch (floor < Emits; in practice 0-4 over the batch). A floor of at least one
            // per dispatch means the bridge is silently boxing (or otherwise allocating) the struct
            // it claims to carry boxing-free -- the "no boxing" column lying in the opposite
            // direction from the SendMessage case above. The < Emits bound tolerates warm-editor
            // background noise while still catching a real per-call boxing regression (which would
            // floor at >= Emits, exactly like SendMessage).
            Assert.Less(
                floor,
                Emits,
                $"Bridge '{techKey}' advertises boxing-free struct dispatch but allocated a floor of {floor} "
                    + $"managed objects over {Emits} dispatches -- at least one per dispatch -- so it is boxing "
                    + "(or otherwise allocating) the struct it claims to carry boxing-free. A boxing-free struct "
                    + "path must allocate well under one object per dispatch; either fix the dispatch path or mark "
                    + "the scenario unsupported."
            );
        }

        // Prepares the bridge, warms the dispatch path so reflection/JIT caches settle, then returns
        // the MINIMUM managed-allocation count over Attempts batches of Emits dispatches (or
        // AllocationProbe.Unmeasured when no reliable probe exists on this backend). The minimum is
        // the true per-batch floor: warm-editor spikes only ADD, so they never lower it.
        private static long MeasureDispatchAllocationFloor(
            IMessagingTechBridge bridge,
            ComparisonScenario scenario
        )
        {
            using (bridge)
            {
                bridge.Prepare(scenario);
                for (int i = 0; i < Emits; i++)
                {
                    bridge.EmitOnce();
                }

                return AllocationProbe.MeasureMin(
                    Attempts,
                    prepare: null,
                    operation: () =>
                    {
                        for (int i = 0; i < Emits; i++)
                        {
                            bridge.EmitOnce();
                        }
                    }
                );
            }
        }
    }
}
#endif
