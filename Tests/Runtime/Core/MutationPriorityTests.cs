#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class MutationPriorityTests : MessagingTestBase
    {
        /// <summary>
        /// A lower-priority handler registered from inside a higher-priority
        /// handler's body must not run during the current emission (snapshot
        /// semantics) but must run - and at the correct earlier slot - on the
        /// next emission. Parameterized over the kinds that carry a component
        /// target (Targeted, Broadcast) via
        /// <see cref="MessageScenarios.KindsWithComponentTarget"/>; the prior
        /// Targeted/Broadcast pair collapsed into this single method.
        /// </summary>
        [Test]
        public void PriorityInsertedNextEmissionOrder(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.KindsWithComponentTarget)
            )]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(PriorityInsertedNextEmissionOrder) + "_" + scenario,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            MessageRegistrationToken token = GetToken(
                host.GetComponent<EmptyMessageAwareComponent>()
            );

            List<int> order = new();
            bool added = false;
            MessageRegistrationHandle low = default;

            MessageRegistrationHandle high = RegisterCounter(
                scenario,
                token,
                host,
                () =>
                {
                    order.Add(1);
                    if (!added)
                    {
                        added = true;
                        low = RegisterCounter(
                            scenario,
                            token,
                            host,
                            () => order.Add(0),
                            priority: 0
                        );
                    }
                },
                priority: 1
            );

            EmitForScenario(scenario, host);
            CollectionAssert.AreEqual(
                new[] { 1 },
                order,
                "[{0}] Lower-priority handler added mid-emission must not run in the same emission.",
                scenario.Kind
            );

            order.Clear();
            EmitForScenario(scenario, host);
            CollectionAssert.AreEqual(
                new[] { 0, 1 },
                order,
                "[{0}] Lower-priority handler must run first on the next emission.",
                scenario.Kind
            );

            token.RemoveRegistration(high);
            if (low != default)
            {
                token.RemoveRegistration(low);
            }
        }

        private static MessageRegistrationHandle RegisterCounter(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId target,
            Action onInvoked,
            int priority = 0
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
                        "MutationPriorityTests only covers component-target kinds."
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
                        "MutationPriorityTests only covers component-target kinds."
                    );
                }
            }
        }
    }
}

#endif
