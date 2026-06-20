#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using DxMessaging.Core;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class MutationDedupeTests : MessagingTestBase
    {
        /// <summary>
        /// Registering the SAME handler delegate a second time (here, from
        /// inside the handler's own first invocation) must not duplicate the
        /// handler's invocation: each emission still increments the counter
        /// exactly once. Parameterized over the kinds that carry a component
        /// target (Targeted, Broadcast) via
        /// <see cref="MessageScenarios.KindsWithComponentTarget"/>; the prior
        /// Targeted/Broadcast pair collapsed into this single method. The
        /// self-referential typed delegate is set up per kind so the identical
        /// instance is registered both times (the bus dedupes on the delegate
        /// instance, so a wrapper-per-call would defeat the test).
        /// </summary>
        [Test]
        public void AddSameDelegateDoesNotDuplicateInvocation(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.KindsWithComponentTarget)
            )]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(AddSameDelegateDoesNotDuplicateInvocation) + "_" + scenario,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            MessageRegistrationToken token = GetToken(
                host.GetComponent<EmptyMessageAwareComponent>()
            );

            int count = 0;
            MessageRegistrationHandle firstHandle;
            MessageRegistrationHandle? secondHandle = null;

            switch (scenario.Kind)
            {
                case MessageKind.Targeted:
                {
                    MessageHandler.FastHandler<SimpleTargetedMessage> local = null;
                    local = (ref SimpleTargetedMessage _) =>
                    {
                        count++;
                        if (secondHandle == null)
                        {
                            secondHandle = ScenarioHarness.RegisterTargeted<SimpleTargetedMessage>(
                                scenario,
                                token,
                                host,
                                local
                            );
                        }
                    };
                    firstHandle = ScenarioHarness.RegisterTargeted<SimpleTargetedMessage>(
                        scenario,
                        token,
                        host,
                        local
                    );
                    break;
                }
                case MessageKind.Broadcast:
                {
                    MessageHandler.FastHandler<SimpleBroadcastMessage> local = null;
                    local = (ref SimpleBroadcastMessage _) =>
                    {
                        count++;
                        if (secondHandle == null)
                        {
                            secondHandle =
                                ScenarioHarness.RegisterBroadcast<SimpleBroadcastMessage>(
                                    scenario,
                                    token,
                                    host,
                                    local
                                );
                        }
                    };
                    firstHandle = ScenarioHarness.RegisterBroadcast<SimpleBroadcastMessage>(
                        scenario,
                        token,
                        host,
                        local
                    );
                    break;
                }
                default:
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(scenario),
                        scenario.Kind,
                        "MutationDedupeTests only covers component-target kinds."
                    );
                }
            }

            EmitForScenario(scenario, host);
            Assert.AreEqual(
                1,
                count,
                "[{0}] First emission should invoke the handler once.",
                scenario.Kind
            );

            EmitForScenario(scenario, host);
            Assert.AreEqual(
                2,
                count,
                "[{0}] Re-registering the same delegate must not duplicate invocation.",
                scenario.Kind
            );

            token.RemoveRegistration(firstHandle);
            if (secondHandle.HasValue)
            {
                token.RemoveRegistration(secondHandle.Value);
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
                        "MutationDedupeTests only covers component-target kinds."
                    );
                }
            }
        }
    }
}

#endif
