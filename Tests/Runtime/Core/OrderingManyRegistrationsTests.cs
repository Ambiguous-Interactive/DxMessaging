#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using UnityEngine;

    [Category("Stress")]
    public sealed class OrderingManyRegistrationsTests : MessagingTestBase
    {
        private const int ManyRegistrationCount = 32;

        private static int[] BuildSequentialExpected(int count)
        {
            int[] expected = new int[count];
            for (int i = 0; i < count; i++)
            {
                expected[i] = i;
            }

            return expected;
        }

        private static void AssertSequence(IList<int> actual, string message)
        {
            int[] expected = BuildSequentialExpected(actual.Count);
            int[] actualCopy = new int[actual.Count];
            for (int i = 0; i < actual.Count; i++)
            {
                actualCopy[i] = actual[i];
            }

            Assert.AreEqual(expected, actualCopy, message);
        }

        [Test]
        public void HandlersManyRegistrationsMaintainOrder(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(HandlersManyRegistrationsMaintainOrder) + "_" + scenario,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId targetId = component;

            List<int> fastOrder = new(ManyRegistrationCount);
            List<int> actionOrder = new(ManyRegistrationCount);

            for (int i = 0; i < ManyRegistrationCount; i++)
            {
                int index = i;
                _ = RegisterFastHandler(scenario, token, targetId, () => fastOrder.Add(index));
            }

            for (int i = 0; i < ManyRegistrationCount; i++)
            {
                int index = i;
                _ = RegisterActionHandler(scenario, token, targetId, () => actionOrder.Add(index));
            }

            EmitForScenario(scenario, targetId);

            AssertSequence(
                fastOrder,
                $"{scenario.Kind} fast handlers should run in registration order even with many entries."
            );
            AssertSequence(
                actionOrder,
                $"{scenario.Kind} action handlers should run in registration order even with many entries."
            );
        }

        [Test]
        public void UntargetedPostProcessorsManyRegistrationsMaintainOrder()
        {
            GameObject host = new(
                nameof(UntargetedPostProcessorsManyRegistrationsMaintainOrder),
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);

            List<int> postProcessorOrder = new(ManyRegistrationCount);

            for (int i = 0; i < ManyRegistrationCount; i++)
            {
                int index = i;
                _ = token.RegisterUntargetedPostProcessor(
                    (ref SimpleUntargetedMessage _) => postProcessorOrder.Add(index)
                );
            }

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted();

            AssertSequence(
                postProcessorOrder,
                "Untargeted post-processors should run in registration order even with many entries."
            );
        }

        [Test]
        public void TargetedPostProcessorsManyRegistrationsMaintainOrder()
        {
            GameObject host = new(
                nameof(TargetedPostProcessorsManyRegistrationsMaintainOrder),
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);

            List<int> fastOrder = new(ManyRegistrationCount);
            List<int> actionOrder = new(ManyRegistrationCount);

            InstanceId target = component;

            for (int i = 0; i < ManyRegistrationCount; i++)
            {
                int index = i;
                _ = token.RegisterTargetedPostProcessor(
                    target,
                    (ref SimpleTargetedMessage _) => fastOrder.Add(index)
                );
            }

            for (int i = 0; i < ManyRegistrationCount; i++)
            {
                int index = i;
                _ = token.RegisterTargetedPostProcessor(
                    target,
                    (SimpleTargetedMessage _) => actionOrder.Add(index)
                );
            }

            SimpleTargetedMessage message = new();
            message.EmitComponentTargeted(component);

            AssertSequence(
                fastOrder,
                "Targeted fast post-processors should run in registration order even with many entries."
            );
            AssertSequence(
                actionOrder,
                "Targeted action post-processors should run in registration order even with many entries."
            );
        }

        [Test]
        public void TargetedWithoutTargetingManyRegistrationsMaintainOrder()
        {
            GameObject host = new(
                nameof(TargetedWithoutTargetingManyRegistrationsMaintainOrder),
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);

            List<int> fastOrder = new(ManyRegistrationCount);
            List<int> actionOrder = new(ManyRegistrationCount);

            for (int i = 0; i < ManyRegistrationCount; i++)
            {
                int index = i;
                _ = token.RegisterTargetedWithoutTargeting(
                    (ref InstanceId _, ref SimpleTargetedMessage _) => fastOrder.Add(index)
                );
            }

            for (int i = 0; i < ManyRegistrationCount; i++)
            {
                int index = i;
                _ = token.RegisterTargetedWithoutTargeting(
                    (InstanceId _, SimpleTargetedMessage _) => actionOrder.Add(index)
                );
            }

            SimpleTargetedMessage message = new();
            message.EmitComponentTargeted(component);

            AssertSequence(
                fastOrder,
                "Targeted-without-targeting fast handlers should run in registration order even with many entries."
            );
            AssertSequence(
                actionOrder,
                "Targeted-without-targeting action handlers should run in registration order even with many entries."
            );
        }

        [Test]
        public void TargetedWithoutTargetingPostProcessorsManyRegistrationsMaintainOrder()
        {
            GameObject host = new(
                nameof(TargetedWithoutTargetingPostProcessorsManyRegistrationsMaintainOrder),
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);

            List<int> fastOrder = new(ManyRegistrationCount);
            List<int> actionOrder = new(ManyRegistrationCount);

            for (int i = 0; i < ManyRegistrationCount; i++)
            {
                int index = i;
                _ = token.RegisterTargetedWithoutTargetingPostProcessor(
                    (ref InstanceId _, ref SimpleTargetedMessage _) => fastOrder.Add(index)
                );
            }

            for (int i = 0; i < ManyRegistrationCount; i++)
            {
                int index = i;
                _ = token.RegisterTargetedWithoutTargetingPostProcessor(
                    (InstanceId _, SimpleTargetedMessage _) => actionOrder.Add(index)
                );
            }

            SimpleTargetedMessage message = new();
            message.EmitComponentTargeted(component);

            AssertSequence(
                fastOrder,
                "Targeted-without-targeting fast post-processors should run in registration order even with many entries."
            );
            AssertSequence(
                actionOrder,
                "Targeted-without-targeting action post-processors should run in registration order even with many entries."
            );
        }

        [Test]
        public void BroadcastPostProcessorsManyRegistrationsMaintainOrder()
        {
            GameObject host = new(
                nameof(BroadcastPostProcessorsManyRegistrationsMaintainOrder),
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);

            List<int> fastOrder = new(ManyRegistrationCount);
            List<int> actionOrder = new(ManyRegistrationCount);

            InstanceId source = component;

            for (int i = 0; i < ManyRegistrationCount; i++)
            {
                int index = i;
                _ = token.RegisterBroadcastPostProcessor(
                    source,
                    (ref SimpleBroadcastMessage _) => fastOrder.Add(index)
                );
            }

            for (int i = 0; i < ManyRegistrationCount; i++)
            {
                int index = i;
                _ = token.RegisterBroadcastPostProcessor(
                    source,
                    (SimpleBroadcastMessage _) => actionOrder.Add(index)
                );
            }

            SimpleBroadcastMessage message = new();
            message.EmitComponentBroadcast(component);

            AssertSequence(
                fastOrder,
                "Broadcast fast post-processors should run in registration order even with many entries."
            );
            AssertSequence(
                actionOrder,
                "Broadcast action post-processors should run in registration order even with many entries."
            );
        }

        [Test]
        public void BroadcastWithoutSourceManyRegistrationsMaintainOrder()
        {
            GameObject host = new(
                nameof(BroadcastWithoutSourceManyRegistrationsMaintainOrder),
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);

            List<int> fastOrder = new(ManyRegistrationCount);
            List<int> actionOrder = new(ManyRegistrationCount);

            for (int i = 0; i < ManyRegistrationCount; i++)
            {
                int index = i;
                _ = token.RegisterBroadcastWithoutSource(
                    (ref InstanceId _, ref SimpleBroadcastMessage _) => fastOrder.Add(index)
                );
            }

            for (int i = 0; i < ManyRegistrationCount; i++)
            {
                int index = i;
                _ = token.RegisterBroadcastWithoutSource(
                    (InstanceId _, SimpleBroadcastMessage _) => actionOrder.Add(index)
                );
            }

            SimpleBroadcastMessage message = new();
            message.EmitComponentBroadcast(component);

            AssertSequence(
                fastOrder,
                "Broadcast-without-source fast handlers should run in registration order even with many entries."
            );
            AssertSequence(
                actionOrder,
                "Broadcast-without-source action handlers should run in registration order even with many entries."
            );
        }

        [Test]
        public void BroadcastWithoutSourcePostProcessorsManyRegistrationsMaintainOrder()
        {
            GameObject host = new(
                nameof(BroadcastWithoutSourcePostProcessorsManyRegistrationsMaintainOrder),
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);

            List<int> fastOrder = new(ManyRegistrationCount);
            List<int> actionOrder = new(ManyRegistrationCount);

            for (int i = 0; i < ManyRegistrationCount; i++)
            {
                int index = i;
                _ = token.RegisterBroadcastWithoutSourcePostProcessor(
                    (ref InstanceId _, ref SimpleBroadcastMessage _) => fastOrder.Add(index)
                );
            }

            for (int i = 0; i < ManyRegistrationCount; i++)
            {
                int index = i;
                _ = token.RegisterBroadcastWithoutSourcePostProcessor(
                    (InstanceId _, SimpleBroadcastMessage _) => actionOrder.Add(index)
                );
            }

            SimpleBroadcastMessage message = new();
            message.EmitComponentBroadcast(component);

            AssertSequence(
                fastOrder,
                "Broadcast-without-source fast post-processors should run in registration order even with many entries."
            );
            AssertSequence(
                actionOrder,
                "Broadcast-without-source action post-processors should run in registration order even with many entries."
            );
        }

        [Test]
        public void GlobalAcceptAllActionsManyRegistrationsMaintainOrder()
        {
            GameObject host = new(
                nameof(GlobalAcceptAllActionsManyRegistrationsMaintainOrder),
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);

            List<int> untargetedOrder = new(ManyRegistrationCount);
            List<int> targetedOrder = new(ManyRegistrationCount);
            List<int> broadcastOrder = new(ManyRegistrationCount);

            for (int i = 0; i < ManyRegistrationCount; i++)
            {
                int index = i;
                _ = token.RegisterGlobalAcceptAll(
                    _ => untargetedOrder.Add(index),
                    (_, _) => targetedOrder.Add(index),
                    (_, _) => broadcastOrder.Add(index)
                );
            }

            SimpleUntargetedMessage untargeted = new();
            untargeted.EmitUntargeted();

            SimpleTargetedMessage targeted = new();
            targeted.EmitComponentTargeted(component);

            SimpleBroadcastMessage broadcast = new();
            broadcast.EmitComponentBroadcast(component);

            AssertSequence(
                untargetedOrder,
                "Global accept-all action handlers for untargeted messages should run in registration order even with many entries."
            );
            AssertSequence(
                targetedOrder,
                "Global accept-all action handlers for targeted messages should run in registration order even with many entries."
            );
            AssertSequence(
                broadcastOrder,
                "Global accept-all action handlers for broadcast messages should run in registration order even with many entries."
            );
        }

        [Test]
        public void GlobalAcceptAllFastManyRegistrationsMaintainOrder()
        {
            GameObject host = new(
                nameof(GlobalAcceptAllFastManyRegistrationsMaintainOrder),
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);

            List<int> untargetedOrder = new(ManyRegistrationCount);
            List<int> targetedOrder = new(ManyRegistrationCount);
            List<int> broadcastOrder = new(ManyRegistrationCount);

            for (int i = 0; i < ManyRegistrationCount; i++)
            {
                int index = i;
                _ = token.RegisterGlobalAcceptAll(
                    (ref IUntargetedMessage _) => untargetedOrder.Add(index),
                    (ref InstanceId _, ref ITargetedMessage _) => targetedOrder.Add(index),
                    (ref InstanceId _, ref IBroadcastMessage _) => broadcastOrder.Add(index)
                );
            }

            SimpleUntargetedMessage untargeted = new();
            untargeted.EmitUntargeted();

            SimpleTargetedMessage targeted = new();
            targeted.EmitComponentTargeted(component);

            SimpleBroadcastMessage broadcast = new();
            broadcast.EmitComponentBroadcast(component);

            AssertSequence(
                untargetedOrder,
                "Global accept-all fast handlers for untargeted messages should run in registration order even with many entries."
            );
            AssertSequence(
                targetedOrder,
                "Global accept-all fast handlers for targeted messages should run in registration order even with many entries."
            );
            AssertSequence(
                broadcastOrder,
                "Global accept-all fast handlers for broadcast messages should run in registration order even with many entries."
            );
        }

        [Test]
        public void InterceptorsManyRegistrationsMaintainOrder(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(InterceptorsManyRegistrationsMaintainOrder) + "_" + scenario,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId targetId = component;

            List<int> order = new(ManyRegistrationCount);

            for (int i = 0; i < ManyRegistrationCount; i++)
            {
                int index = i;
                _ = RegisterInterceptor(
                    scenario,
                    token,
                    () =>
                    {
                        order.Add(index);
                        return true;
                    }
                );
            }

            EmitForScenario(scenario, targetId);

            AssertSequence(
                order,
                $"{scenario.Kind} interceptors should run in registration order even with many entries."
            );
        }

        private static MessageRegistrationHandle RegisterFastHandler(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId target,
            Action onInvoked
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    return token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) => onInvoked()
                    );
                }
                case MessageKind.Targeted:
                {
                    return token.RegisterTargeted<SimpleTargetedMessage>(
                        target,
                        (ref SimpleTargetedMessage _) => onInvoked()
                    );
                }
                case MessageKind.Broadcast:
                {
                    return token.RegisterBroadcast<SimpleBroadcastMessage>(
                        target,
                        (ref SimpleBroadcastMessage _) => onInvoked()
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

        private static MessageRegistrationHandle RegisterActionHandler(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId target,
            Action onInvoked
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    return token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (SimpleUntargetedMessage _) => onInvoked()
                    );
                }
                case MessageKind.Targeted:
                {
                    return token.RegisterTargeted<SimpleTargetedMessage>(
                        target,
                        (SimpleTargetedMessage _) => onInvoked()
                    );
                }
                case MessageKind.Broadcast:
                {
                    return token.RegisterBroadcast<SimpleBroadcastMessage>(
                        target,
                        (SimpleBroadcastMessage _) => onInvoked()
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

        private static MessageRegistrationHandle RegisterInterceptor(
            MessageScenario scenario,
            MessageRegistrationToken token,
            Func<bool> body
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    return ScenarioHarness.RegisterUntargetedInterceptor<SimpleUntargetedMessage>(
                        scenario,
                        token,
                        (ref SimpleUntargetedMessage _) => body()
                    );
                }
                case MessageKind.Targeted:
                {
                    return ScenarioHarness.RegisterTargetedInterceptor<SimpleTargetedMessage>(
                        scenario,
                        token,
                        (ref InstanceId _, ref SimpleTargetedMessage __) => body()
                    );
                }
                case MessageKind.Broadcast:
                {
                    return ScenarioHarness.RegisterBroadcastInterceptor<SimpleBroadcastMessage>(
                        scenario,
                        token,
                        (ref InstanceId _, ref SimpleBroadcastMessage __) => body()
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
