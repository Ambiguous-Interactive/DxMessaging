#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using UnityEngine;

    /// <summary>
    /// Direct coverage for <see cref="MessageRegistrationToken.RetargetMessageBus"/>.
    /// The token owns two pieces of state that retargeting touches: the staged
    /// registration map (replayed on <c>Enable()</c>) and the live deregistration
    /// map (invoked when active registrations move). These tests pin the four
    /// lifecycle permutations: retarget while enabled (live registrations move
    /// immediately under <see cref="MessageBusRebindMode.RebindActive"/>),
    /// retarget while disabled (the swap is latent until the next
    /// <c>Enable()</c>), retarget to the same bus (observable no-op), and
    /// retarget from inside a handler mid-dispatch (the in-flight emission
    /// completes against its frozen snapshot, then routing moves).
    /// </summary>
    /// <remarks>
    /// All tests run against dedicated <see cref="MessageBus"/> instances (never
    /// the global bus) so registration counters are deterministic and the
    /// fixture cannot perturb other tests. Tokens are built via the
    /// <c>AlternateBusTests</c> idiom: a manual <see cref="MessageHandler"/>
    /// bound to a spawned GameObject's <see cref="InstanceId"/>.
    /// </remarks>
    public sealed class RetargetMessageBusTests : MessagingTestBase
    {
        [Test]
        public void RetargetWhileEnabledMovesLiveRegistrationsToNewBus(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(RetargetWhileEnabledMovesLiveRegistrationsToNewBus) + scenario.Kind
            );
            _spawned.Add(host);
            InstanceId hostId = host;

            MessageHandler handler = new(host) { active = true };
            MessageBus oldBus = new();
            MessageBus newBus = new();
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, oldBus);
            token.Enable();

            int handlerCount = 0;
            using (
                LeakWatcher oldBusWatcher = new(
                    bus: oldBus,
                    throwOnLeak: true,
                    label: scenario.DisplayName + "-OldBus"
                )
            )
            using (
                LeakWatcher newBusWatcher = new(
                    bus: newBus,
                    throwOnLeak: true,
                    label: scenario.DisplayName + "-NewBus"
                )
            )
            {
                _ = ScenarioCallbacks.RegisterCountingHandler(
                    scenario,
                    token,
                    hostId,
                    () => ++handlerCount
                );

                Assert.AreEqual(
                    1,
                    RegisteredCountForKind(oldBus, scenario.Kind),
                    "[{0}] Live registration must land on the old bus before retargeting.",
                    scenario.Kind
                );
                Assert.AreEqual(
                    0,
                    RegisteredCountForKind(newBus, scenario.Kind),
                    "[{0}] New bus must be empty before retargeting.",
                    scenario.Kind
                );

                ScenarioCallbacks.EmitForKind(scenario, oldBus, hostId);
                Assert.AreEqual(
                    1,
                    handlerCount,
                    "[{0}] Handler must receive emissions on the old bus before retargeting.",
                    scenario.Kind
                );

                token.RetargetMessageBus(newBus, MessageBusRebindMode.RebindActive);

                Assert.AreEqual(
                    0,
                    RegisteredCountForKind(oldBus, scenario.Kind),
                    "[{0}] RebindActive retarget must remove the live registration from the old bus.",
                    scenario.Kind
                );
                Assert.AreEqual(
                    1,
                    RegisteredCountForKind(newBus, scenario.Kind),
                    "[{0}] RebindActive retarget must re-register the live registration on the new bus.",
                    scenario.Kind
                );

                ScenarioCallbacks.EmitForKind(scenario, oldBus, hostId);
                Assert.AreEqual(
                    1,
                    handlerCount,
                    "[{0}] Old bus must stop reaching the handler after retargeting (count={1}).",
                    scenario.Kind,
                    handlerCount
                );

                ScenarioCallbacks.EmitForKind(scenario, newBus, hostId);
                Assert.AreEqual(
                    2,
                    handlerCount,
                    "[{0}] New bus must reach the handler after retargeting (count={1}).",
                    scenario.Kind,
                    handlerCount
                );

                token.UnregisterAll();
            }

            handler.active = false;
        }

        [Test]
        public void RetargetWhileDisabledLandsOnNewBusOnNextEnable(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(RetargetWhileDisabledLandsOnNewBusOnNextEnable) + scenario.Kind
            );
            _spawned.Add(host);
            InstanceId hostId = host;

            MessageHandler handler = new(host) { active = true };
            MessageBus oldBus = new();
            MessageBus newBus = new();
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, oldBus);
            token.Enable();

            int handlerCount = 0;
            using (
                LeakWatcher oldBusWatcher = new(
                    bus: oldBus,
                    throwOnLeak: true,
                    label: scenario.DisplayName + "-OldBus"
                )
            )
            using (
                LeakWatcher newBusWatcher = new(
                    bus: newBus,
                    throwOnLeak: true,
                    label: scenario.DisplayName + "-NewBus"
                )
            )
            {
                _ = ScenarioCallbacks.RegisterCountingHandler(
                    scenario,
                    token,
                    hostId,
                    () => ++handlerCount
                );
                Assert.AreEqual(
                    1,
                    RegisteredCountForKind(oldBus, scenario.Kind),
                    "[{0}] Sanity: live registration starts on the old bus.",
                    scenario.Kind
                );

                token.Disable();
                Assert.AreEqual(
                    0,
                    RegisteredCountForKind(oldBus, scenario.Kind),
                    "[{0}] Disable must tear down the old-bus registration before the retarget.",
                    scenario.Kind
                );

                // Retargeting a disabled token must not register anything: the
                // swap is latent until the next Enable().
                token.RetargetMessageBus(newBus, MessageBusRebindMode.RebindActive);
                Assert.AreEqual(
                    0,
                    RegisteredCountForKind(oldBus, scenario.Kind),
                    "[{0}] Retarget while disabled must leave the old bus empty.",
                    scenario.Kind
                );
                Assert.AreEqual(
                    0,
                    RegisteredCountForKind(newBus, scenario.Kind),
                    "[{0}] Retarget while disabled must NOT eagerly register on the new bus.",
                    scenario.Kind
                );

                token.Enable();
                Assert.AreEqual(
                    0,
                    RegisteredCountForKind(oldBus, scenario.Kind),
                    "[{0}] Enable after retarget must not touch the old bus.",
                    scenario.Kind
                );
                Assert.AreEqual(
                    1,
                    RegisteredCountForKind(newBus, scenario.Kind),
                    "[{0}] Enable after retarget must replay the staged registration on the new bus.",
                    scenario.Kind
                );

                ScenarioCallbacks.EmitForKind(scenario, oldBus, hostId);
                Assert.AreEqual(
                    0,
                    handlerCount,
                    "[{0}] Old bus emissions must not reach the handler after the latent retarget.",
                    scenario.Kind
                );

                ScenarioCallbacks.EmitForKind(scenario, newBus, hostId);
                Assert.AreEqual(
                    1,
                    handlerCount,
                    "[{0}] New bus emissions must reach the handler after Enable().",
                    scenario.Kind
                );

                token.UnregisterAll();
            }

            handler.active = false;
        }

        [Test]
        public void RetargetToSameBusIsObservableNoOp(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(nameof(RetargetToSameBusIsObservableNoOp) + scenario.Kind);
            _spawned.Add(host);
            InstanceId hostId = host;

            MessageHandler handler = new(host) { active = true };
            MessageBus bus = new();
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            token.Enable();

            int handlerCount = 0;
            using (
                LeakWatcher watcher = new(bus: bus, throwOnLeak: true, label: scenario.DisplayName)
            )
            {
                _ = ScenarioCallbacks.RegisterCountingHandler(
                    scenario,
                    token,
                    hostId,
                    () => ++handlerCount
                );
                Assert.AreEqual(
                    1,
                    RegisteredCountForKind(bus, scenario.Kind),
                    "[{0}] Sanity: exactly one live registration before retargeting.",
                    scenario.Kind
                );

                // PreserveRegistrations + same bus takes the strict early-return
                // path inside RetargetMessageBus.
                Assert.DoesNotThrow(
                    () => token.RetargetMessageBus(bus, MessageBusRebindMode.PreserveRegistrations),
                    "[{0}] Same-bus retarget (PreserveRegistrations) must not throw.",
                    scenario.Kind
                );
                Assert.AreEqual(
                    1,
                    RegisteredCountForKind(bus, scenario.Kind),
                    "[{0}] Same-bus retarget (PreserveRegistrations) must leave the registration count unchanged.",
                    scenario.Kind
                );
                ScenarioCallbacks.EmitForKind(scenario, bus, hostId);
                Assert.AreEqual(
                    1,
                    handlerCount,
                    "[{0}] Handler must fire exactly once after the PreserveRegistrations no-op (no dropped or duplicated registration).",
                    scenario.Kind
                );

                // RebindActive + same bus tears down and re-registers in place;
                // observably this must still be a no-op: same count, exactly one
                // delivery per emission.
                Assert.DoesNotThrow(
                    () => token.RetargetMessageBus(bus, MessageBusRebindMode.RebindActive),
                    "[{0}] Same-bus retarget (RebindActive) must not throw.",
                    scenario.Kind
                );
                Assert.AreEqual(
                    1,
                    RegisteredCountForKind(bus, scenario.Kind),
                    "[{0}] Same-bus retarget (RebindActive) must leave the registration count unchanged.",
                    scenario.Kind
                );
                ScenarioCallbacks.EmitForKind(scenario, bus, hostId);
                Assert.AreEqual(
                    2,
                    handlerCount,
                    "[{0}] Handler must fire exactly once per emission after the RebindActive same-bus retarget (no duplicated registration).",
                    scenario.Kind
                );

                token.UnregisterAll();
            }

            handler.active = false;
        }

        /// <summary>
        /// Pins mid-dispatch retargeting against the bus's snapshot semantics:
        /// the dispatch snapshot is frozen at emission start (the untargeted
        /// path resolves registrations into an immutable flat array at
        /// snapshot-build time; the other kinds pre-freeze the per-priority
        /// handler stacks), so a priority-0 handler that
        /// retargets its own token mid-dispatch deregisters the priority-1
        /// peer from the old bus WITHOUT removing it from the in-flight
        /// emission. This is the same contract pinned by
        /// <c>LifecycleEdgeCasesTests.TokenDisableMidDispatch</c>, plus the
        /// retarget-specific follow-ups: subsequent old-bus emissions find
        /// nothing, and new-bus emissions reach every moved handler.
        /// </summary>
        [Test]
        public void RetargetMidDispatchCompletesSnapshotThenMovesRouting(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(RetargetMidDispatchCompletesSnapshotThenMovesRouting) + scenario.Kind
            );
            _spawned.Add(host);
            InstanceId hostId = host;

            MessageHandler handler = new(host) { active = true };
            MessageBus oldBus = new();
            MessageBus newBus = new();
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, oldBus);
            token.Enable();

            int retargetingCount = 0;
            int trailingCount = 0;
            bool retargeted = false;

            using (
                LeakWatcher oldBusWatcher = new(
                    bus: oldBus,
                    throwOnLeak: true,
                    label: scenario.DisplayName + "-OldBus"
                )
            )
            using (
                LeakWatcher newBusWatcher = new(
                    bus: newBus,
                    throwOnLeak: true,
                    label: scenario.DisplayName + "-NewBus"
                )
            )
            {
                // Handler A (priority 0) retargets the token mid-dispatch;
                // handler B (priority 1) observes whether the in-flight
                // snapshot still completes.
                _ = ScenarioCallbacks.RegisterCountingHandler(
                    scenario,
                    token,
                    hostId,
                    () =>
                    {
                        ++retargetingCount;
                        if (!retargeted)
                        {
                            retargeted = true;
                            token.RetargetMessageBus(newBus, MessageBusRebindMode.RebindActive);
                        }
                    },
                    priority: 0
                );
                _ = ScenarioCallbacks.RegisterCountingHandler(
                    scenario,
                    token,
                    hostId,
                    () => ++trailingCount,
                    priority: 1
                );

                Assert.DoesNotThrow(
                    () => ScenarioCallbacks.EmitForKind(scenario, oldBus, hostId),
                    "[{0}] Retargeting from inside a handler must not throw mid-dispatch.",
                    scenario.Kind
                );
                Assert.AreEqual(
                    1,
                    retargetingCount,
                    "[{0}] Retargeting handler must run on the in-flight emission. retargeting={1}, trailing={2}.",
                    scenario.Kind,
                    retargetingCount,
                    trailingCount
                );
                Assert.AreEqual(
                    1,
                    trailingCount,
                    "[{0}] Snapshot semantics: the trailing handler must still run on the in-flight emission even though the retarget deregistered it from the old bus. retargeting={1}, trailing={2}.",
                    scenario.Kind,
                    retargetingCount,
                    trailingCount
                );

                Assert.AreEqual(
                    0,
                    RegisteredCountForKind(oldBus, scenario.Kind),
                    "[{0}] After the mid-dispatch retarget, no registration may remain on the old bus.",
                    scenario.Kind
                );
                Assert.AreEqual(
                    2,
                    RegisteredCountForKind(newBus, scenario.Kind),
                    "[{0}] After the mid-dispatch retarget, both registrations must live on the new bus.",
                    scenario.Kind
                );

                ScenarioCallbacks.EmitForKind(scenario, oldBus, hostId);
                Assert.AreEqual(
                    1,
                    retargetingCount,
                    "[{0}] Old bus emissions after the retarget must not reach the retargeting handler.",
                    scenario.Kind
                );
                Assert.AreEqual(
                    1,
                    trailingCount,
                    "[{0}] Old bus emissions after the retarget must not reach the trailing handler.",
                    scenario.Kind
                );

                ScenarioCallbacks.EmitForKind(scenario, newBus, hostId);
                Assert.AreEqual(
                    2,
                    retargetingCount,
                    "[{0}] New bus emissions must reach the retargeting handler exactly once.",
                    scenario.Kind
                );
                Assert.AreEqual(
                    2,
                    trailingCount,
                    "[{0}] New bus emissions must reach the trailing handler exactly once.",
                    scenario.Kind
                );

                token.UnregisterAll();
            }

            handler.active = false;
        }

        private static int RegisteredCountForKind(IMessageBus bus, MessageKind kind)
        {
            switch (kind)
            {
                case MessageKind.Untargeted:
                {
                    return bus.RegisteredUntargeted;
                }
                case MessageKind.Targeted:
                {
                    return bus.RegisteredTargeted;
                }
                case MessageKind.Broadcast:
                {
                    return bus.RegisteredBroadcast;
                }
                default:
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(kind),
                        kind,
                        "Unsupported message kind."
                    );
                }
            }
        }
    }
}
#endif
