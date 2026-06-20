#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
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
