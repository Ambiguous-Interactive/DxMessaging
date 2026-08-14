#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class MutationInterceptorTests : MessagingTestBase
    {
        [Test]
        public void AddBlockingInterceptorDuringInterceptor(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(AddBlockingInterceptorDuringInterceptor) + "_" + scenario,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent comp = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(comp);
            InstanceId hostId = host;

            int first = 0;
            int second = 0;
            MessageRegistrationHandle? secondHandle = null;

            MessageRegistrationHandle firstHandle = RegisterInterceptor(
                scenario,
                token,
                () =>
                {
                    first++;
                    if (secondHandle == null)
                    {
                        secondHandle = RegisterInterceptor(
                            scenario,
                            token,
                            () =>
                            {
                                second++;
                                return true;
                            }
                        );
                    }

                    return false; // block pipeline
                }
            );

            EmitForScenario(scenario, hostId);
            Assert.AreEqual(1, first);
            Assert.AreEqual(
                0,
                second,
                "New interceptor must not run in the same emission when blocked."
            );

            EmitForScenario(scenario, hostId);
            Assert.AreEqual(2, first);
            Assert.AreEqual(0, second, "Pipeline remains blocked by first; second not invoked.");

            // Unblock by removing the first
            token.RemoveRegistration(firstHandle);
            EmitForScenario(scenario, hostId);
            Assert.AreEqual(2, first);
            Assert.AreEqual(1, second, "Second runs once first no longer blocks.");

            token.RemoveRegistration(firstHandle);
            if (secondHandle.HasValue)
            {
                token.RemoveRegistration(secondHandle.Value);
            }
        }

        [Test]
        public void ResetFromInterceptorStopsSnapshotAndAllowsFreshRegistration(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            using LeakWatcher watcher = LeakWatcher.Watch(scenario.DisplayName);
            GameObject host = new(
                nameof(ResetFromInterceptorStopsSnapshotAndAllowsFreshRegistration)
                    + "_"
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;
            IMessageBus bus = MessageHandler.MessageBus;
            int resettingCount = 0;
            int trailingCount = 0;
            int handlerCount = 0;

            _ = RegisterInterceptor(
                scenario,
                token,
                () =>
                {
                    ++resettingCount;
                    DxMessagingStaticState.Reset();
                    return true;
                },
                priority: 0
            );
            _ = RegisterInterceptor(
                scenario,
                token,
                () =>
                {
                    ++trailingCount;
                    return true;
                },
                priority: 0
            );
            _ = ScenarioCallbacks.RegisterCountingHandler(
                scenario,
                token,
                hostId,
                () => ++handlerCount
            );

            Assert.DoesNotThrow(
                () => EmitForScenario(scenario, hostId),
                "[{0}] Reset from an interceptor must stop dispatch without throwing.",
                scenario.Kind
            );
            Assert.AreEqual(
                1,
                resettingCount,
                "[{0}] The resetting interceptor must run exactly once.",
                scenario.Kind
            );
            Assert.AreEqual(
                0,
                trailingCount,
                "[{0}] Reset must stop later interceptors from the in-flight interceptor walk.",
                scenario.Kind
            );
            Assert.AreEqual(
                0,
                handlerCount,
                "[{0}] Reset in the interceptor phase must stop the handler phase.",
                scenario.Kind
            );
            Assert.AreEqual(0, bus.RegisteredUntargeted, "[{0}] Untargeted leak.", scenario.Kind);
            Assert.AreEqual(0, bus.RegisteredTargeted, "[{0}] Targeted leak.", scenario.Kind);
            Assert.AreEqual(0, bus.RegisteredBroadcast, "[{0}] Broadcast leak.", scenario.Kind);
            Assert.AreEqual(
                0,
                bus.RegisteredInterceptors,
                "[{0}] Interceptor leak.",
                scenario.Kind
            );
            Assert.AreEqual(
                0,
                bus.RegisteredPostProcessors,
                "[{0}] Post-processor leak.",
                scenario.Kind
            );
            Assert.AreEqual(
                0,
                bus.RegisteredGlobalAcceptAll,
                "[{0}] Global handler leak.",
                scenario.Kind
            );

            Assert.DoesNotThrow(
                () => EmitForScenario(scenario, hostId),
                "[{0}] An emission immediately after Reset must be a silent no-op.",
                scenario.Kind
            );
            Assert.AreEqual(
                1,
                resettingCount,
                "[{0}] Reset interceptor survived Reset.",
                scenario.Kind
            );
            Assert.AreEqual(
                0,
                trailingCount,
                "[{0}] Trailing interceptor survived Reset.",
                scenario.Kind
            );
            Assert.AreEqual(0, handlerCount, "[{0}] Handler survived Reset.", scenario.Kind);

            GameObject rebornHost = new(
                nameof(ResetFromInterceptorStopsSnapshotAndAllowsFreshRegistration)
                    + "_Reborn_"
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(rebornHost);
            EmptyMessageAwareComponent rebornComponent =
                rebornHost.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken rebornToken = GetToken(rebornComponent);
            InstanceId rebornId = rebornHost;
            int rebornCount = 0;
            _ = ScenarioCallbacks.RegisterCountingHandler(
                scenario,
                rebornToken,
                rebornId,
                () => ++rebornCount
            );

            EmitForScenario(scenario, rebornId);
            Assert.AreEqual(
                1,
                rebornCount,
                "[{0}] A fresh post-reset registration must dispatch normally.",
                scenario.Kind
            );
            rebornToken.UnregisterAll();
        }

        /// <summary>
        /// The documented snapshot contract
        /// (docs/concepts/interceptors-and-ordering.md) says a listener added
        /// during an emission never runs for that emission, and names
        /// interceptors explicitly.
        /// <see cref="MutationDuringEmissionTests.AddInterceptorDuringInterceptorDoesNotRunInSameEmission"/>
        /// pins that for an interceptor added to the SAME priority group. This
        /// case pins the one the group-at-a-time walk used to get wrong: an
        /// interceptor added to a LATER priority group, whose group had not
        /// been reached yet and so was picked up mid-emission.
        /// </summary>
        [Test]
        public void InterceptorAddedToLaterPriorityGroupDoesNotRunInSameEmission(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(InterceptorAddedToLaterPriorityGroupDoesNotRunInSameEmission)
                    + "_"
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            // Opened after the host's own registrations exist so the baseline
            // covers only the interceptors this test adds and removes.
            using LeakWatcher watcher = LeakWatcher.Watch(scenario.DisplayName);

            int earlyCount = 0;
            int lateCount = 0;
            MessageRegistrationHandle? lateHandle = null;

            MessageRegistrationHandle earlyHandle = RegisterInterceptor(
                scenario,
                token,
                () =>
                {
                    ++earlyCount;
                    lateHandle ??= RegisterInterceptor(
                        scenario,
                        token,
                        () =>
                        {
                            ++lateCount;
                            return true;
                        },
                        priority: 10
                    );
                    return true;
                },
                priority: 0
            );

            EmitForScenario(scenario, hostId);
            Assert.AreEqual(
                1,
                earlyCount,
                "[{0}] The priority-0 interceptor must run exactly once.",
                scenario.Kind
            );
            Assert.AreEqual(
                0,
                lateCount,
                "[{0}] An interceptor registered into a later priority group must NOT run in the "
                    + "emission that registered it; the interceptor walk is frozen at emission start.",
                scenario.Kind
            );

            EmitForScenario(scenario, hostId);
            Assert.AreEqual(
                2,
                earlyCount,
                "[{0}] The priority-0 interceptor must run again on the second emission.",
                scenario.Kind
            );
            Assert.AreEqual(
                1,
                lateCount,
                "[{0}] The later-group interceptor must start running on the NEXT emission.",
                scenario.Kind
            );

            token.RemoveRegistration(earlyHandle);
            if (lateHandle.HasValue)
            {
                token.RemoveRegistration(lateHandle.Value);
            }
        }

        /// <summary>
        /// Removal counterpart of
        /// <see cref="InterceptorAddedToLaterPriorityGroupDoesNotRunInSameEmission"/>: a listener
        /// removed during an emission still completes its execution for that
        /// message. The group-at-a-time walk honoured a same-group removal but
        /// dropped a later-group one mid-emission; the frozen walk runs it.
        /// </summary>
        [Test]
        public void InterceptorRemovedFromLaterPriorityGroupStillRunsInSameEmission(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(InterceptorRemovedFromLaterPriorityGroupStillRunsInSameEmission)
                    + "_"
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            // Opened after the host's own registrations exist so the baseline
            // covers only the interceptors this test adds and removes.
            using LeakWatcher watcher = LeakWatcher.Watch(scenario.DisplayName);

            int earlyCount = 0;
            int lateCount = 0;
            bool removed = false;

            MessageRegistrationHandle lateHandle = RegisterInterceptor(
                scenario,
                token,
                () =>
                {
                    ++lateCount;
                    return true;
                },
                priority: 10
            );
            MessageRegistrationHandle earlyHandle = RegisterInterceptor(
                scenario,
                token,
                () =>
                {
                    ++earlyCount;
                    if (!removed)
                    {
                        removed = true;
                        token.RemoveRegistration(lateHandle);
                    }

                    return true;
                },
                priority: 0
            );

            EmitForScenario(scenario, hostId);
            Assert.AreEqual(
                1,
                earlyCount,
                "[{0}] The priority-0 interceptor must run exactly once.",
                scenario.Kind
            );
            Assert.AreEqual(
                1,
                lateCount,
                "[{0}] An interceptor removed from a later priority group must still run for the "
                    + "in-flight message; the interceptor walk is frozen at emission start.",
                scenario.Kind
            );

            EmitForScenario(scenario, hostId);
            Assert.AreEqual(
                2,
                earlyCount,
                "[{0}] The priority-0 interceptor must run again on the second emission.",
                scenario.Kind
            );
            Assert.AreEqual(
                1,
                lateCount,
                "[{0}] The removed interceptor must not run on the next emission.",
                scenario.Kind
            );

            token.RemoveRegistration(earlyHandle);
        }

        private static MessageRegistrationHandle RegisterInterceptor(
            MessageScenario scenario,
            MessageRegistrationToken token,
            Func<bool> body,
            int priority = 0
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    return ScenarioHarness.RegisterUntargetedInterceptor<SimpleUntargetedMessage>(
                        scenario,
                        token,
                        (ref SimpleUntargetedMessage _) => body(),
                        priority
                    );
                }
                case MessageKind.Targeted:
                {
                    return ScenarioHarness.RegisterTargetedInterceptor<SimpleTargetedMessage>(
                        scenario,
                        token,
                        (ref InstanceId _, ref SimpleTargetedMessage __) => body(),
                        priority
                    );
                }
                case MessageKind.Broadcast:
                {
                    return ScenarioHarness.RegisterBroadcastInterceptor<SimpleBroadcastMessage>(
                        scenario,
                        token,
                        (ref InstanceId _, ref SimpleBroadcastMessage __) => body(),
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
