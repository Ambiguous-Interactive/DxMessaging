#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using DxMessaging.Core;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class MutationGlobalAddTests : MessagingTestBase
    {
        /// <summary>
        /// A global-accept-all listener registered from inside a handler's body
        /// during emission must not fire in the current emission (snapshot
        /// semantics) but must fire on the next emission. Parameterized over the
        /// kinds that carry a component target (Targeted, Broadcast) via
        /// <see cref="MessageScenarios.KindsWithComponentTarget"/>; the prior
        /// Targeted/Broadcast pair collapsed into this single method.
        /// </summary>
        [Test]
        public void GlobalAcceptAllAddDuringEmission(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.KindsWithComponentTarget)
            )]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(GlobalAcceptAllAddDuringEmission) + "_" + scenario,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent comp = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(comp);

            int[] counts = new int[2];
            MessageRegistrationHandle adder = RegisterCounter(
                scenario,
                token,
                host,
                () =>
                {
                    if (counts[1] == 0)
                    {
                        token.RegisterGlobalAcceptAll(
                            (ref IUntargetedMessage _) => counts[1]++,
                            (ref InstanceId _, ref ITargetedMessage _) => counts[1]++,
                            (ref InstanceId _, ref IBroadcastMessage _) => counts[1]++
                        );
                    }
                    counts[0]++;
                }
            );

            EmitForScenario(scenario, host);
            Assert.AreEqual(
                1,
                counts[0],
                "[{0}] Handler should run once on the first emission.",
                scenario.Kind
            );
            Assert.AreEqual(
                0,
                counts[1],
                "[{0}] Global-accept-all added mid-emission must not fire in the same emission.",
                scenario.Kind
            );

            EmitForScenario(scenario, host);
            Assert.AreEqual(
                2,
                counts[0],
                "[{0}] Handler should run again on the second emission.",
                scenario.Kind
            );
            Assert.AreEqual(
                1,
                counts[1],
                "[{0}] Global-accept-all must fire once on the next emission.",
                scenario.Kind
            );

            token.RemoveRegistration(adder);
        }

        private static MessageRegistrationHandle RegisterCounter(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId target,
            Action onInvoked
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Targeted:
                {
                    return ScenarioHarness.RegisterTargeted<SimpleTargetedMessage>(
                        scenario,
                        token,
                        target,
                        (ref SimpleTargetedMessage _) => onInvoked()
                    );
                }
                case MessageKind.Broadcast:
                {
                    return ScenarioHarness.RegisterBroadcast<SimpleBroadcastMessage>(
                        scenario,
                        token,
                        target,
                        (ref SimpleBroadcastMessage _) => onInvoked()
                    );
                }
                default:
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(scenario),
                        scenario.Kind,
                        "MutationGlobalAddTests only covers component-target kinds."
                    );
                }
            }
        }

        private static void EmitForScenario(MessageScenario scenario, InstanceId target)
        {
            switch (scenario.Kind)
            {
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
                        "MutationGlobalAddTests only covers component-target kinds."
                    );
                }
            }
        }
    }
}

#endif
