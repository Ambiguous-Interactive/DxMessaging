#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using UnityEngine;
    using UnityEngine.TestTools;

    /// <summary>
    /// Pins what happens when <see cref="DxMessagingStaticState.Reset"/> is
    /// invoked from INSIDE a handler while an emission is in flight. This is
    /// the mid-dispatch sibling of
    /// <c>LifecycleEdgeCasesTests.EmitImmediatelyAfterResetIsSilentNoOp</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Investigated semantics (see <c>MessageBus.ResetState</c> in
    /// <c>Runtime/Core/MessageBus/MessageBus.cs</c>): the reset first walks
    /// every referenced <see cref="MessageHandler"/> and resets its typed
    /// slots, which increments the per-handler dispatch-link generation
    /// (<c>TypedHandler._outerGeneration</c>). Every dispatch link captured by
    /// the in-flight emission's frozen snapshot guards on that generation, so
    /// the remaining handlers of the current emission short-circuit silently:
    /// the in-flight emission STOPS cleanly at the resetting handler rather
    /// than completing the snapshot. After the reset, the bus's reset
    /// generation has been bumped, all sinks are empty, and emissions are
    /// silent no-ops until fresh registrations are made.
    /// </para>
    /// </remarks>
    public sealed class ResetMidDispatchTests : MessagingTestBase
    {
        /// <summary>
        /// Reset mid-dispatch with the resetting handler and a trailing peer
        /// at DIFFERENT priorities (separate dispatch buckets). The emission
        /// must not throw, the trailing peer must NOT run (the reset halts the
        /// remaining in-flight dispatch via the generation guard), post-reset
        /// emissions are silent no-ops, and a freshly created component can
        /// register and receive again afterwards.
        /// </summary>
        [UnityTest]
        public IEnumerator ResetFromInsideHandlerStopsInFlightEmissionCleanly(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(ResetFromInsideHandlerStopsInFlightEmissionCleanly) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            // Trailing handler lives on a separate component (separate
            // MessageHandler) so the test observes cross-handler behavior,
            // not same-wrapper short-circuiting.
            GameObject auxHost = new(
                nameof(ResetFromInsideHandlerStopsInFlightEmissionCleanly) + "Aux" + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(auxHost);
            EmptyMessageAwareComponent auxComponent =
                auxHost.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken auxToken = GetToken(auxComponent);

            int resettingCount = 0;
            int trailingCount = 0;

            _ = RegisterCountingHandler(
                scenario,
                token,
                hostId,
                () =>
                {
                    ++resettingCount;
                    if (resettingCount == 1)
                    {
                        DxMessagingStaticState.Reset();
                    }
                },
                priority: 0
            );
            _ = RegisterCountingHandler(
                scenario,
                auxToken,
                hostId,
                () => ++trailingCount,
                priority: 1
            );

            Assert.DoesNotThrow(
                () => EmitForScenario(scenario, hostId),
                "[{0}] Reset from inside a handler must not throw mid-dispatch.",
                scenario.Kind
            );
            Assert.AreEqual(
                1,
                resettingCount,
                "[{0}] The resetting handler must run exactly once on the in-flight emission. resetting={1}, trailing={2}.",
                scenario.Kind,
                resettingCount,
                trailingCount
            );
            Assert.AreEqual(
                0,
                trailingCount,
                "[{0}] Reset mid-dispatch halts the remaining in-flight dispatch: the trailing handler's dispatch link short-circuits on the bumped generation and must NOT run. resetting={1}, trailing={2}.",
                scenario.Kind,
                resettingCount,
                trailingCount
            );

            // Post-reset emissions are silent no-ops: nothing is registered,
            // nothing may fire, nothing may throw.
            Assert.DoesNotThrow(
                () => EmitForScenario(scenario, hostId),
                "[{0}] Emitting after the mid-dispatch Reset must not throw.",
                scenario.Kind
            );
            Assert.AreEqual(
                1,
                resettingCount,
                "[{0}] Pre-reset handlers must NOT fire on post-reset emissions.",
                scenario.Kind
            );
            Assert.AreEqual(
                0,
                trailingCount,
                "[{0}] Pre-reset trailing handlers must NOT fire on post-reset emissions.",
                scenario.Kind
            );

            // The reset must leave the bus with zero registrations on every
            // public counter (no corruption / phantom registrations).
            IMessageBus bus = MessageHandler.MessageBus;
            Assert.AreEqual(
                0,
                bus.RegisteredUntargeted,
                "[{0}] Untargeted counter must be zero after a mid-dispatch Reset.",
                scenario.Kind
            );
            Assert.AreEqual(
                0,
                bus.RegisteredTargeted,
                "[{0}] Targeted counter must be zero after a mid-dispatch Reset.",
                scenario.Kind
            );
            Assert.AreEqual(
                0,
                bus.RegisteredBroadcast,
                "[{0}] Broadcast counter must be zero after a mid-dispatch Reset.",
                scenario.Kind
            );
            Assert.AreEqual(
                0,
                bus.RegisteredGlobalAcceptAll,
                "[{0}] GlobalAcceptAll counter must be zero after a mid-dispatch Reset.",
                scenario.Kind
            );

            // Re-setup afterwards works: a freshly created component can
            // register on the reset bus and receive emissions normally.
            GameObject rebornHost = new(
                nameof(ResetFromInsideHandlerStopsInFlightEmissionCleanly)
                    + "Reborn"
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(rebornHost);
            EmptyMessageAwareComponent rebornComponent =
                rebornHost.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken rebornToken = GetToken(rebornComponent);
            InstanceId rebornId = rebornHost;

            int rebornCount = 0;
            MessageRegistrationHandle rebornHandle = RegisterCountingHandler(
                scenario,
                rebornToken,
                rebornId,
                () => ++rebornCount
            );

            EmitForScenario(scenario, rebornId);
            Assert.AreEqual(
                1,
                rebornCount,
                "[{0}] A handler registered after the mid-dispatch Reset must receive emissions normally.",
                scenario.Kind
            );

            rebornToken.RemoveRegistration(rebornHandle);
            yield break;
        }

        /// <summary>
        /// Reset mid-dispatch with the resetting handler and a trailing peer
        /// at the SAME priority (same dispatch bucket, two entries). The
        /// contract is identical to the different-priority case: no
        /// exceptions, the trailing peer does not run, and the bus is usable
        /// afterwards.
        /// </summary>
        /// <remarks>
        /// EXPECTED PRODUCTION GAP (left pinning the documented contract):
        /// for Targeted and Broadcast dispatch, <c>MessageBus.ResetState</c>
        /// routes through <c>ReturnContextMap</c>
        /// (<c>Runtime/Core/MessageBus/MessageBus.cs</c>, the
        /// <c>handlers?.Clear()</c> loop), which calls
        /// <c>HandlerCache.Clear()</c> -&gt; <c>dispatchState.Reset()</c> and
        /// thereby RELEASES the in-flight emission's active
        /// <c>DispatchSnapshot</c> back to its pools while the dispatch loop
        /// is still iterating it. A trailing entry in the SAME bucket is then
        /// read from a cleared entries array (default <c>DispatchEntry</c>),
        /// and the invoke helper dereferences <c>entry.handler.active</c>
        /// without a null check - a NullReferenceException. The sweep and
        /// eviction paths guard against exactly this via
        /// <c>HasActiveDispatchSnapshot</c>; <c>ResetState</c> does not.
        /// Untargeted dispatch is unaffected because its scalar sink is
        /// orphaned without clearing (only <c>MessageCache.Clear()</c> runs),
        /// leaving the snapshot intact.
        /// </remarks>
        [UnityTest]
        public IEnumerator ResetFromInsideHandlerWithSamePriorityPeerDoesNotThrow(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(ResetFromInsideHandlerWithSamePriorityPeerDoesNotThrow) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            // The peer must be a SEPARATE MessageHandler registered at the
            // SAME priority so it occupies a distinct entry in the same
            // dispatch bucket. Registration order makes the resetting
            // handler run first within the bucket.
            GameObject peerHost = new(
                nameof(ResetFromInsideHandlerWithSamePriorityPeerDoesNotThrow)
                    + "Peer"
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(peerHost);
            EmptyMessageAwareComponent peerComponent =
                peerHost.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken peerToken = GetToken(peerComponent);

            int resettingCount = 0;
            int peerCount = 0;

            _ = RegisterCountingHandler(
                scenario,
                token,
                hostId,
                () =>
                {
                    ++resettingCount;
                    if (resettingCount == 1)
                    {
                        DxMessagingStaticState.Reset();
                    }
                },
                priority: 0
            );
            _ = RegisterCountingHandler(
                scenario,
                peerToken,
                hostId,
                () => ++peerCount,
                priority: 0
            );

            Assert.DoesNotThrow(
                () => EmitForScenario(scenario, hostId),
                "[{0}] Reset from inside a handler must not throw even when a same-priority peer entry follows it in the same dispatch bucket.",
                scenario.Kind
            );
            Assert.AreEqual(
                1,
                resettingCount,
                "[{0}] The resetting handler must run exactly once. resetting={1}, peer={2}.",
                scenario.Kind,
                resettingCount,
                peerCount
            );
            Assert.AreEqual(
                0,
                peerCount,
                "[{0}] Reset mid-dispatch must cleanly stop the remaining in-flight dispatch: the same-bucket peer must NOT run. resetting={1}, peer={2}.",
                scenario.Kind,
                resettingCount,
                peerCount
            );

            // The bus must remain usable afterwards.
            Assert.DoesNotThrow(
                () => EmitForScenario(scenario, hostId),
                "[{0}] Emitting after the mid-dispatch Reset must not throw.",
                scenario.Kind
            );
            Assert.AreEqual(
                1,
                resettingCount,
                "[{0}] Pre-reset handlers must NOT fire on post-reset emissions.",
                scenario.Kind
            );

            yield break;
        }

        /// <summary>
        /// Reset mid-dispatch with the resetting delegate and a trailing peer
        /// delegate registered on the SAME component (same
        /// <see cref="MessageHandler"/>, same token) at the SAME priority for
        /// the SAME message type. Both delegates therefore occupy adjacent
        /// entries in the same per-handler typed-handler list, and the
        /// in-flight emission is indexing that very list when the reset runs.
        /// The contract mirrors the cross-component cases above: no
        /// exceptions, the trailing peer does NOT run (the reset halts the
        /// remaining in-flight dispatch), and the bus is usable afterwards.
        /// </summary>
        /// <remarks>
        /// EXPECTED PRODUCTION GAP (this test is written red-first):
        /// <c>MessageBus.ResetState</c> calls
        /// <c>ResetTypedSlotsForReferencedHandlers</c> INLINE during dispatch.
        /// The chain <c>ResetAllTypedSlotsForBusReset</c> -&gt;
        /// <c>TypedSlot&lt;T&gt;.Reset()</c> -&gt;
        /// <c>IHandlerActionCache.Reset()</c>
        /// (<c>Runtime/Core/MessageHandler.cs</c>) performs
        /// <c>cache.Clear()</c> IN PLACE on the very List the in-flight inner
        /// dispatch loop is indexing (the loop snapshots the List reference
        /// and its count, then indexes unrolled). With two same-priority
        /// delegates on one MessageHandler, delegate[0] resets, the List is
        /// cleared in place, and indexing <c>typedHandlers[1]</c> throws
        /// ArgumentOutOfRangeException. The peer-visibility expectation
        /// pinned here matches the two tests above: once Reset runs, no
        /// further pre-reset delegate of the in-flight emission may fire.
        /// </remarks>
        [UnityTest]
        public IEnumerator ResetFromInsideHandlerWithSameComponentSamePriorityPeerDoesNotThrow(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(ResetFromInsideHandlerWithSameComponentSamePriorityPeerDoesNotThrow)
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int resettingCount = 0;
            int peerCount = 0;

            // BOTH delegates are registered on the SAME token at the SAME
            // priority for the SAME message type, so they land in the same
            // per-handler typed-handler list. Registration order makes the
            // resetting delegate run first within that list.
            _ = RegisterCountingHandler(
                scenario,
                token,
                hostId,
                () =>
                {
                    ++resettingCount;
                    if (resettingCount == 1)
                    {
                        DxMessagingStaticState.Reset();
                    }
                },
                priority: 0
            );
            _ = RegisterCountingHandler(scenario, token, hostId, () => ++peerCount, priority: 0);

            Assert.DoesNotThrow(
                () => EmitForScenario(scenario, hostId),
                "[{0}] Reset from inside a handler must not throw even when a same-component, same-priority peer delegate follows it in the same typed-handler list.",
                scenario.Kind
            );
            Assert.AreEqual(
                1,
                resettingCount,
                "[{0}] The resetting delegate must run exactly once on the in-flight emission. resetting={1}, peer={2}.",
                scenario.Kind,
                resettingCount,
                peerCount
            );
            Assert.AreEqual(
                0,
                peerCount,
                "[{0}] Reset mid-dispatch must cleanly stop the remaining in-flight dispatch: the same-component, same-priority peer delegate must NOT run (mirrors the cross-component same-priority contract above). resetting={1}, peer={2}.",
                scenario.Kind,
                resettingCount,
                peerCount
            );

            // The bus must remain usable afterwards: post-reset emissions are
            // silent no-ops for the pre-reset delegates.
            Assert.DoesNotThrow(
                () => EmitForScenario(scenario, hostId),
                "[{0}] Emitting after the mid-dispatch Reset must not throw.",
                scenario.Kind
            );
            Assert.AreEqual(
                1,
                resettingCount,
                "[{0}] Pre-reset delegates must NOT fire on post-reset emissions. resetting={1}, peer={2}.",
                scenario.Kind,
                resettingCount,
                peerCount
            );
            Assert.AreEqual(
                0,
                peerCount,
                "[{0}] The pre-reset peer delegate must NOT fire on post-reset emissions. resetting={1}, peer={2}.",
                scenario.Kind,
                resettingCount,
                peerCount
            );

            yield break;
        }

        private static MessageRegistrationHandle RegisterCountingHandler(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId target,
            Action onInvoked,
            int priority = 0
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    return ScenarioHarness.RegisterUntargeted<SimpleUntargetedMessage>(
                        scenario,
                        token,
                        (ref SimpleUntargetedMessage _) => onInvoked(),
                        priority
                    );
                }
                case MessageKind.Targeted:
                {
                    return ScenarioHarness.RegisterTargeted<SimpleTargetedMessage>(
                        scenario,
                        token,
                        target,
                        (ref SimpleTargetedMessage _) => onInvoked(),
                        priority
                    );
                }
                case MessageKind.Broadcast:
                {
                    return ScenarioHarness.RegisterBroadcast<SimpleBroadcastMessage>(
                        scenario,
                        token,
                        target,
                        (ref SimpleBroadcastMessage _) => onInvoked(),
                        priority
                    );
                }
                default:
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(scenario),
                        scenario.Kind,
                        "Unsupported message kind."
                    );
                }
            }
        }

        private static void EmitForScenario(MessageScenario scenario, InstanceId target)
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    SimpleUntargetedMessage message = new();
                    ScenarioHarness.EmitUntargeted(scenario, ref message);
                    return;
                }
                case MessageKind.Targeted:
                {
                    SimpleTargetedMessage message = new();
                    ScenarioHarness.EmitTargeted(scenario, ref message, target);
                    return;
                }
                case MessageKind.Broadcast:
                {
                    SimpleBroadcastMessage message = new();
                    ScenarioHarness.EmitBroadcast(scenario, ref message, target);
                    return;
                }
                default:
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(scenario),
                        scenario.Kind,
                        "Unsupported message kind."
                    );
                }
            }
        }
    }
}
#endif
