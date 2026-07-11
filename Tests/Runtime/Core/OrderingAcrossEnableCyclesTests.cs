#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using UnityEngine;
    using UnityEngine.TestTools;

    /// <summary>
    /// Pins equal-priority handler ordering across MessageRegistrationToken
    /// Disable()/Enable() cycles. The registration docs promise that handlers at
    /// the same priority run in registration order ("Lower runs earlier; same
    /// priority uses registration order" - MessageRegistrationToken), and nothing
    /// in the public surface scopes that promise to the first enable cycle, so
    /// these tests pin original registration order as the contract for every
    /// cycle.
    /// </summary>
    /// <remarks>
    /// Implementation hazard being pinned: Enable() replays staged registrations
    /// by enumerating a Dictionary&lt;MessageRegistrationHandle, Action&gt;
    /// (MessageRegistrationToken._registrations), and the per-priority dispatch
    /// list must be rebuilt from HandlerActionCache entries in first-registration
    /// order (MessageHandler.GetOrAddNewHandlerStack). Remove/Add churn must append
    /// the re-added entry at the tail rather than leak backing-map slot reuse into
    /// the dispatch contract.
    /// </remarks>
    public sealed class OrderingAcrossEnableCyclesTests : MessagingTestBase
    {
        [Test]
        public void SamePriorityOrderPreservedAcrossDisableEnableCycle(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(SamePriorityOrderPreservedAcrossDisableEnableCycle) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            List<string> order = new();
            _ = RegisterOrderedHandler(scenario, token, hostId, order, "A");
            _ = RegisterOrderedHandler(scenario, token, hostId, order, "B");
            _ = RegisterOrderedHandler(scenario, token, hostId, order, "C");

            ScenarioCallbacks.EmitForKind(scenario, hostId);
            Assert.AreEqual(
                new[] { "A", "B", "C" },
                order.ToArray(),
                "Anchor: equal-priority handlers must run in registration order on the first emission."
            );

            order.Clear();
            token.Disable();
            ScenarioCallbacks.EmitForKind(scenario, hostId);
            Assert.AreEqual(0, order.Count, "No handler may run while the token is disabled.");

            token.Enable();
            ScenarioCallbacks.EmitForKind(scenario, hostId);
            Assert.AreEqual(
                new[] { "A", "B", "C" },
                order.ToArray(),
                "Equal-priority handlers must run in ORIGINAL registration order after a "
                    + "Disable()/Enable() cycle. The token docs promise 'same priority uses "
                    + "registration order' and do not scope that promise to the first enable "
                    + "cycle; Enable() must replay staged registrations in registration order."
            );

            order.Clear();
            token.Disable();
            token.Enable();
            ScenarioCallbacks.EmitForKind(scenario, hostId);
            Assert.AreEqual(
                new[] { "A", "B", "C" },
                order.ToArray(),
                "Equal-priority handlers must keep original registration order after a SECOND "
                    + "Disable()/Enable() cycle; order must be stable for every cycle, not "
                    + "merely restored on even-numbered cycles."
            );
        }

        [Test]
        public void SamePriorityOrderAfterRemovalAndReRegistrationWhileEnabled(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(SamePriorityOrderAfterRemovalAndReRegistrationWhileEnabled) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            List<string> order = new();
            _ = RegisterOrderedHandler(scenario, token, hostId, order, "A");
            MessageRegistrationHandle handleB = RegisterOrderedHandler(
                scenario,
                token,
                hostId,
                order,
                "B"
            );
            _ = RegisterOrderedHandler(scenario, token, hostId, order, "C");

            ScenarioCallbacks.EmitForKind(scenario, hostId);
            Assert.AreEqual(
                new[] { "A", "B", "C" },
                order.ToArray(),
                "Anchor: equal-priority handlers must run in registration order before churn."
            );

            token.RemoveRegistration(handleB);
            _ = RegisterOrderedHandler(scenario, token, hostId, order, "D");

            order.Clear();
            ScenarioCallbacks.EmitForKind(scenario, hostId);
            Assert.AreEqual(
                new[] { "A", "C", "D" },
                order.ToArray(),
                "After removing B and registering D while enabled, equal-priority dispatch must "
                    + "follow the registration order of the LIVE registrations (A, C, D). D was "
                    + "registered last and must not be dispatched into B's old position."
            );
        }

        [Test]
        public void SamePriorityOrderAfterChurnPreservedAcrossDisableEnableCycle(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(SamePriorityOrderAfterChurnPreservedAcrossDisableEnableCycle)
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            List<string> order = new();
            _ = RegisterOrderedHandler(scenario, token, hostId, order, "A");
            MessageRegistrationHandle handleB = RegisterOrderedHandler(
                scenario,
                token,
                hostId,
                order,
                "B"
            );
            _ = RegisterOrderedHandler(scenario, token, hostId, order, "C");

            ScenarioCallbacks.EmitForKind(scenario, hostId);
            Assert.AreEqual(
                new[] { "A", "B", "C" },
                order.ToArray(),
                "Anchor: equal-priority handlers must run in registration order before churn."
            );

            token.RemoveRegistration(handleB);
            _ = RegisterOrderedHandler(scenario, token, hostId, order, "D");

            token.Disable();
            token.Enable();

            order.Clear();
            ScenarioCallbacks.EmitForKind(scenario, hostId);
            Assert.AreEqual(
                new[] { "A", "C", "D" },
                order.ToArray(),
                "After RemoveRegistration churn (remove B, register D) followed by a "
                    + "Disable()/Enable() cycle, equal-priority dispatch must still follow the "
                    + "registration order of the live registrations (A, C, D). Enable() replays "
                    + "staged registrations from a Dictionary, so slot reuse after churn must "
                    + "not be allowed to permute the documented registration order."
            );
        }

        /// <summary>
        /// Pins equal-priority ordering ACROSS components under handler
        /// churn. Three distinct components on three distinct GameObjects
        /// (three distinct MessageHandlers in the bus-side per-priority
        /// bucket) each register one delegate at priority 0; the middle one
        /// is destroyed outright (its MessageHandler leaves the bus bucket
        /// entirely), then a brand-new component registers at the same
        /// type/priority. The documented "same priority uses registration
        /// order" contract demands the live handlers dispatch as A, C, D:
        /// D registered LAST and must run LAST.
        /// </summary>
        /// <remarks>
        /// HISTORICAL (this test was written red-first against a real gap):
        /// the bus-side per-priority buckets used to enumerate a
        /// Dictionary&lt;MessageHandler, int&gt; directly, so removing B's
        /// MessageHandler freed a Dictionary slot that D's MessageHandler
        /// then reused, dispatching D in B's old position and violating the
        /// documented cross-component registration order. Bucket/flat builds
        /// now iterate the bus-side <c>insertionOrder</c> list
        /// (<c>MessageBus.FillDispatchEntries</c> and the
        /// <c>BuildFlatDispatch</c> family), which this test pins.
        /// </remarks>
        [UnityTest]
        public IEnumerator SamePriorityCrossComponentOrderPreservedAfterHandlerChurn(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            // Shared context every component listens on, so Targeted and
            // Broadcast emissions reach all handlers in a single emission.
            // It is deliberately NOT one of the handler hosts so destroying
            // host B cannot disturb the emission context.
            GameObject contextHost = new(
                nameof(SamePriorityCrossComponentOrderPreservedAfterHandlerChurn)
                    + "Context"
                    + scenario.Kind
            );
            _spawned.Add(contextHost);
            InstanceId contextId = contextHost;

            GameObject hostA = new(
                nameof(SamePriorityCrossComponentOrderPreservedAfterHandlerChurn)
                    + "A"
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(hostA);
            MessageRegistrationToken tokenA = GetToken(
                hostA.GetComponent<EmptyMessageAwareComponent>()
            );

            GameObject hostB = new(
                nameof(SamePriorityCrossComponentOrderPreservedAfterHandlerChurn)
                    + "B"
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(hostB);
            MessageRegistrationToken tokenB = GetToken(
                hostB.GetComponent<EmptyMessageAwareComponent>()
            );

            GameObject hostC = new(
                nameof(SamePriorityCrossComponentOrderPreservedAfterHandlerChurn)
                    + "C"
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(hostC);
            MessageRegistrationToken tokenC = GetToken(
                hostC.GetComponent<EmptyMessageAwareComponent>()
            );

            List<string> order = new();
            _ = RegisterOrderedHandler(scenario, tokenA, contextId, order, "A");
            _ = RegisterOrderedHandler(scenario, tokenB, contextId, order, "B");
            _ = RegisterOrderedHandler(scenario, tokenC, contextId, order, "C");

            ScenarioCallbacks.EmitForKind(scenario, contextId);
            Assert.AreEqual(
                new[] { "A", "B", "C" },
                order.ToArray(),
                "Anchor: equal-priority handlers on three DISTINCT components must run in "
                    + "registration order on the first emission."
            );

            // Destroy B outright: its component deregisters and its
            // MessageHandler must leave the bus-side per-priority bucket
            // entirely (not merely drop one delegate), freeing its slot in
            // the bucket's handler Dictionary. Deferred destroy needs a
            // frame to flush OnDisable/OnDestroy.
            UnityEngine.Object.Destroy(hostB);
            yield return null;

            GameObject hostD = new(
                nameof(SamePriorityCrossComponentOrderPreservedAfterHandlerChurn)
                    + "D"
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(hostD);
            MessageRegistrationToken tokenD = GetToken(
                hostD.GetComponent<EmptyMessageAwareComponent>()
            );
            _ = RegisterOrderedHandler(scenario, tokenD, contextId, order, "D");

            order.Clear();
            ScenarioCallbacks.EmitForKind(scenario, contextId);
            Assert.AreEqual(
                new[] { "A", "C", "D" },
                order.ToArray(),
                "After destroying component B and registering brand-new component D at the same "
                    + "priority, cross-component equal-priority dispatch must follow the "
                    + "registration order of the LIVE handlers (A, C, D). D registered last and "
                    + "must run LAST; Dictionary free-slot reuse in the bus-side per-priority "
                    + "bucket must not dispatch D into B's vacated position (A, D, C)."
            );

            // The post-churn order must be stable on every emission, not
            // merely the first one after churn.
            order.Clear();
            ScenarioCallbacks.EmitForKind(scenario, contextId);
            Assert.AreEqual(
                new[] { "A", "C", "D" },
                order.ToArray(),
                "Cross-component equal-priority order after churn must remain stable on "
                    + "subsequent emissions (A, C, D), independent of any per-emission cache "
                    + "rebuild."
            );
            yield break;
        }

        /// <summary>
        /// Registers a counting handler that appends <paramref name="label"/> to
        /// <paramref name="order"/> so emission order can be asserted.
        /// </summary>
        private static MessageRegistrationHandle RegisterOrderedHandler(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId context,
            List<string> order,
            string label
        )
        {
            return ScenarioCallbacks.RegisterCountingHandler(
                scenario,
                token,
                context,
                () => order.Add(label)
            );
        }
    }
}
#endif
