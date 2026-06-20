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

    public sealed class AlternateBusTests : MessagingTestBase
    {
        [Test]
        public void CustomMessageBusIsolatedFromGlobalBus()
        {
            GameObject globalObject = new(
                nameof(CustomMessageBusIsolatedFromGlobalBus) + "_Global",
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(globalObject);
            EmptyMessageAwareComponent globalComponent =
                globalObject.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken globalToken = GetToken(globalComponent);

            int globalUntargetedCount = 0;
            MessageRegistrationHandle globalHandle =
                globalToken.RegisterUntargeted<SimpleUntargetedMessage>(_ =>
                    ++globalUntargetedCount
                );

            GameObject customObject = new(
                nameof(CustomMessageBusIsolatedFromGlobalBus) + "_Custom"
            );
            _spawned.Add(customObject);
            MessageHandler customHandler = new(customObject) { active = true };
            MessageBus customBus = new();
            MessageRegistrationToken customToken = MessageRegistrationToken.Create(
                customHandler,
                customBus
            );
            customToken.Enable();

            int customUntargetedCount = 0;
            MessageRegistrationHandle customUntargetedHandle =
                customToken.RegisterUntargeted<SimpleUntargetedMessage>(_ =>
                    ++customUntargetedCount
                );

            SimpleUntargetedMessage untargetedMessage = new();
            untargetedMessage.EmitUntargeted(customBus);
            Assert.AreEqual(1, customUntargetedCount);
            Assert.AreEqual(0, globalUntargetedCount);

            untargetedMessage.EmitUntargeted();
            Assert.AreEqual(1, customUntargetedCount);
            Assert.AreEqual(1, globalUntargetedCount);

            int customTargetedCount = 0;
            MessageRegistrationHandle customTargetedHandle =
                customToken.RegisterGameObjectTargeted<SimpleTargetedMessage>(
                    customObject,
                    _ => ++customTargetedCount
                );
            SimpleTargetedMessage targetedMessage = new();
            targetedMessage.EmitGameObjectTargeted(customObject, customBus);
            Assert.AreEqual(1, customTargetedCount);

            targetedMessage.EmitGameObjectTargeted(globalObject);
            Assert.AreEqual(1, customTargetedCount);

            customToken.RemoveRegistration(customTargetedHandle);
            customToken.RemoveRegistration(customUntargetedHandle);
            customToken.UnregisterAll();
            customHandler.active = false;

            globalToken.RemoveRegistration(globalHandle);
        }

        [Test]
        public void CrossBusReentrantEmissionsCompleteWithoutCorruption(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            MessageBus busA = new();
            MessageBus busB = new();
            MessageHandler handlerA = new(new InstanceId(101), busA) { active = true };
            MessageHandler handlerB = new(new InstanceId(102), busB) { active = true };
            MessageRegistrationToken tokenA = MessageRegistrationToken.Create(handlerA, busA);
            MessageRegistrationToken tokenB = MessageRegistrationToken.Create(handlerB, busB);
            tokenA.Enable();
            tokenB.Enable();
            InstanceId context = new(7);

            using LeakWatcher watcherA = new(
                bus: busA,
                throwOnLeak: true,
                label: nameof(CrossBusReentrantEmissionsCompleteWithoutCorruption) + "_A"
            );
            using LeakWatcher watcherB = new(
                bus: busB,
                throwOnLeak: true,
                label: nameof(CrossBusReentrantEmissionsCompleteWithoutCorruption) + "_B"
            );

            int countA = 0;
            int countB = 0;

            // Handler on bus A re-emits on bus B mid-dispatch; bus B's handler
            // re-emits back on bus A mid-dispatch. Each side only bounces on its
            // first invocation so the chain terminates deterministically:
            // emit(A) -> countA=1 -> emit(B) -> countB=1 -> emit(A) -> countA=2.
            _ = RegisterReentrantHandler(
                scenario,
                tokenA,
                context,
                () =>
                {
                    ++countA;
                    if (countA == 1)
                    {
                        EmitOnBus(scenario, context, busB);
                    }
                }
            );
            _ = RegisterReentrantHandler(
                scenario,
                tokenB,
                context,
                () =>
                {
                    ++countB;
                    if (countB == 1)
                    {
                        EmitOnBus(scenario, context, busA);
                    }
                }
            );

            EmitOnBus(scenario, context, busA);
            Assert.AreEqual(
                2,
                countA,
                "Bus A must observe the initial emission plus exactly one bounce-back "
                    + "from bus B for scenario {0}.",
                scenario
            );
            Assert.AreEqual(
                1,
                countB,
                "Bus B must observe exactly one cross-bus emission for scenario {0}.",
                scenario
            );

            // Post-reentrancy sanity: both buses keep dispatching normally and
            // emissions never leak to the other bus.
            EmitOnBus(scenario, context, busB);
            Assert.AreEqual(
                2,
                countB,
                "Bus B must keep dispatching normally after the reentrant exchange "
                    + "for scenario {0}.",
                scenario
            );
            Assert.AreEqual(
                2,
                countA,
                "A plain bus B emission must not leak to bus A for scenario {0}.",
                scenario
            );

            EmitOnBus(scenario, context, busA);
            Assert.AreEqual(
                3,
                countA,
                "Bus A must keep dispatching normally after the reentrant exchange "
                    + "for scenario {0}.",
                scenario
            );
            Assert.AreEqual(
                2,
                countB,
                "A plain bus A emission must not leak to bus B for scenario {0}.",
                scenario
            );

            tokenA.Dispose();
            tokenB.Dispose();
            handlerA.active = false;
            handlerB.active = false;
        }

        private static MessageRegistrationHandle RegisterReentrantHandler(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId context,
            Action onInvoked
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    return ScenarioHarness.RegisterUntargeted<SimpleUntargetedMessage>(
                        scenario,
                        token,
                        (ref SimpleUntargetedMessage _) => onInvoked()
                    );
                }
                case MessageKind.Targeted:
                {
                    return ScenarioHarness.RegisterTargeted<SimpleTargetedMessage>(
                        scenario,
                        token,
                        context,
                        (ref SimpleTargetedMessage _) => onInvoked()
                    );
                }
                case MessageKind.Broadcast:
                {
                    return ScenarioHarness.RegisterBroadcast<SimpleBroadcastMessage>(
                        scenario,
                        token,
                        context,
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

        private static void EmitOnBus(MessageScenario scenario, InstanceId context, MessageBus bus)
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    SimpleUntargetedMessage message = new();
                    ScenarioHarness.EmitUntargeted(scenario, ref message, bus);
                    return;
                }
                case MessageKind.Targeted:
                {
                    SimpleTargetedMessage message = new();
                    ScenarioHarness.EmitTargeted(scenario, ref message, context, bus);
                    return;
                }
                case MessageKind.Broadcast:
                {
                    SimpleBroadcastMessage message = new();
                    ScenarioHarness.EmitBroadcast(scenario, ref message, context, bus);
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
