#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using BusType = DxMessaging.Core.MessageBus.MessageBus;

    [TestFixture]
    public sealed class TokenPostProcessorBusRoutingTests
    {
        private const int ContextInstanceId = 37;

        [SetUp]
        public void ResetBeforeTest()
        {
            DxMessagingStaticState.Reset();
        }

        [TearDown]
        public void ResetAfterTest()
        {
            DxMessagingStaticState.Reset();
        }

        [Test]
        public void PostProcessorRegisteredViaTokenRunsOnTokenBusOnly(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.AllKindsIncludingWithoutContext)
            )]
                MessageScenario scenario
        )
        {
            using TokenBusRoutingScope scope = TokenBusRoutingScope.Create();
            using TokenBusRoutingScope customHandlerScope = TokenBusRoutingScope.Create(
                bus: scope.Bus
            );
            using TokenBusRoutingScope globalHandlerScope = TokenBusRoutingScope.Create(
                bus: MessageHandler.MessageBus
            );
            InstanceId context = new(ContextInstanceId);
            int processed = 0;

            using (
                LeakWatcher customWatcher = new(
                    bus: scope.Bus,
                    throwOnLeak: true,
                    label: scenario.DisplayName + "_Custom"
                )
            )
            using (
                LeakWatcher globalWatcher = new(
                    throwOnLeak: true,
                    label: scenario.DisplayName + "_Global"
                )
            )
            {
                try
                {
                    _ = RegisterHandler(scenario, customHandlerScope.Token, context);
                    _ = RegisterHandler(scenario, globalHandlerScope.Token, context);
                    _ = ScenarioCallbacks.RegisterCountingPostProcessor(
                        scenario,
                        scope.Token,
                        context,
                        () => ++processed
                    );

                    ScenarioCallbacks.EmitForKind(scenario, scope.Bus, context);
                    Assert.AreEqual(
                        1,
                        processed,
                        "[{0}] A post-processor registered through a custom-bus token must run on that bus.",
                        scenario.Kind
                    );

                    ScenarioCallbacks.EmitForKind(scenario, messageBus: null, context);
                    Assert.AreEqual(
                        1,
                        processed,
                        "[{0}] A custom-bus token post-processor must not run on the global bus.",
                        scenario.Kind
                    );
                }
                finally
                {
                    scope.Token.UnregisterAll();
                    customHandlerScope.Token.UnregisterAll();
                    globalHandlerScope.Token.UnregisterAll();
                }
            }
        }

        [Test]
        public void PostProcessorStagedWhileDisabledFollowsRetargetedBusAtEnable(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.AllKindsIncludingWithoutContext)
            )]
                MessageScenario scenario
        )
        {
            BusType originalBus = new();
            BusType retargetedBus = new();
            using TokenBusRoutingScope scope = TokenBusRoutingScope.Create(
                enable: false,
                bus: originalBus
            );
            using TokenBusRoutingScope originalHandlerScope = TokenBusRoutingScope.Create(
                bus: originalBus
            );
            using TokenBusRoutingScope retargetedHandlerScope = TokenBusRoutingScope.Create(
                bus: retargetedBus
            );
            using TokenBusRoutingScope globalHandlerScope = TokenBusRoutingScope.Create(
                bus: MessageHandler.MessageBus
            );
            InstanceId context = new(ContextInstanceId);
            int processed = 0;

            using (
                LeakWatcher originalWatcher = new(
                    bus: originalBus,
                    throwOnLeak: true,
                    label: scenario.DisplayName + "_Disabled_Original"
                )
            )
            using (
                LeakWatcher retargetedWatcher = new(
                    bus: retargetedBus,
                    throwOnLeak: true,
                    label: scenario.DisplayName + "_Disabled_Retargeted"
                )
            )
            using (
                LeakWatcher globalWatcher = new(
                    throwOnLeak: true,
                    label: scenario.DisplayName + "_Disabled_Global"
                )
            )
            {
                try
                {
                    _ = RegisterHandler(scenario, originalHandlerScope.Token, context);
                    _ = RegisterHandler(scenario, retargetedHandlerScope.Token, context);
                    _ = RegisterHandler(scenario, globalHandlerScope.Token, context);
                    _ = ScenarioCallbacks.RegisterCountingPostProcessor(
                        scenario,
                        scope.Token,
                        context,
                        () => ++processed
                    );

                    ScenarioCallbacks.EmitForKind(scenario, originalBus, context);
                    ScenarioCallbacks.EmitForKind(scenario, retargetedBus, context);
                    ScenarioCallbacks.EmitForKind(scenario, messageBus: null, context);
                    Assert.AreEqual(
                        0,
                        processed,
                        "[{0}] A post-processor staged on a disabled token must stay inert.",
                        scenario.Kind
                    );

                    scope.Token.RetargetMessageBus(
                        retargetedBus,
                        MessageBusRebindMode.RebindActive
                    );
                    scope.Token.Enable();
                    ScenarioCallbacks.EmitForKind(scenario, originalBus, context);
                    Assert.AreEqual(
                        0,
                        processed,
                        "[{0}] A disabled retarget must not replay the post-processor on the original bus.",
                        scenario.Kind
                    );

                    ScenarioCallbacks.EmitForKind(scenario, retargetedBus, context);
                    Assert.AreEqual(
                        1,
                        processed,
                        "[{0}] Enable must register the staged post-processor on the retargeted bus.",
                        scenario.Kind
                    );

                    ScenarioCallbacks.EmitForKind(scenario, messageBus: null, context);
                    Assert.AreEqual(
                        1,
                        processed,
                        "[{0}] Enabling a custom-bus token must not stage the post-processor on the global bus.",
                        scenario.Kind
                    );
                }
                finally
                {
                    scope.Token.UnregisterAll();
                    originalHandlerScope.Token.UnregisterAll();
                    retargetedHandlerScope.Token.UnregisterAll();
                    globalHandlerScope.Token.UnregisterAll();
                }
            }
        }

        [Test]
        public void RebindActiveMovesLivePostProcessorToNewBus(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.AllKindsIncludingWithoutContext)
            )]
                MessageScenario scenario
        )
        {
            BusType originalBus = new();
            BusType retargetedBus = new();
            using TokenBusRoutingScope scope = TokenBusRoutingScope.Create(bus: originalBus);
            using TokenBusRoutingScope originalHandlerScope = TokenBusRoutingScope.Create(
                bus: originalBus
            );
            using TokenBusRoutingScope retargetedHandlerScope = TokenBusRoutingScope.Create(
                bus: retargetedBus
            );
            InstanceId context = new(ContextInstanceId);
            int processed = 0;

            using (
                LeakWatcher originalWatcher = new(
                    bus: originalBus,
                    throwOnLeak: true,
                    label: scenario.DisplayName + "_Original"
                )
            )
            using (
                LeakWatcher retargetedWatcher = new(
                    bus: retargetedBus,
                    throwOnLeak: true,
                    label: scenario.DisplayName + "_Retargeted"
                )
            )
            {
                try
                {
                    _ = RegisterHandler(scenario, originalHandlerScope.Token, context);
                    _ = RegisterHandler(scenario, retargetedHandlerScope.Token, context);
                    _ = ScenarioCallbacks.RegisterCountingPostProcessor(
                        scenario,
                        scope.Token,
                        context,
                        () => ++processed
                    );

                    ScenarioCallbacks.EmitForKind(scenario, originalBus, context);
                    Assert.AreEqual(
                        1,
                        processed,
                        "[{0}] Control failed: the live post-processor did not run on its original bus.",
                        scenario.Kind
                    );

                    scope.Token.RetargetMessageBus(
                        retargetedBus,
                        MessageBusRebindMode.RebindActive
                    );

                    ScenarioCallbacks.EmitForKind(scenario, originalBus, context);
                    Assert.AreEqual(
                        1,
                        processed,
                        "[{0}] RebindActive must remove the live post-processor from the original bus.",
                        scenario.Kind
                    );

                    ScenarioCallbacks.EmitForKind(scenario, retargetedBus, context);
                    Assert.AreEqual(
                        2,
                        processed,
                        "[{0}] RebindActive must register the live post-processor on the new bus.",
                        scenario.Kind
                    );
                }
                finally
                {
                    scope.Token.UnregisterAll();
                    originalHandlerScope.Token.UnregisterAll();
                    retargetedHandlerScope.Token.UnregisterAll();
                }
            }
        }

        private static MessageRegistrationHandle RegisterHandler(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId context
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                    return token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) => { }
                    );
                case MessageKind.Targeted:
                    return token.RegisterTargeted<SimpleTargetedMessage>(
                        context,
                        (ref SimpleTargetedMessage _) => { }
                    );
                case MessageKind.Broadcast:
                    return token.RegisterBroadcast<SimpleBroadcastMessage>(
                        context,
                        (ref SimpleBroadcastMessage _) => { }
                    );
                case MessageKind.TargetedWithoutTargeting:
                    return token.RegisterTargetedWithoutTargeting<SimpleTargetedMessage>(
                        (ref InstanceId _, ref SimpleTargetedMessage __) => { }
                    );
                case MessageKind.BroadcastWithoutSource:
                    return token.RegisterBroadcastWithoutSource<SimpleBroadcastMessage>(
                        (ref InstanceId _, ref SimpleBroadcastMessage __) => { }
                    );
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(scenario),
                        scenario.Kind,
                        "Unsupported message kind."
                    );
            }
        }
    }
}
#endif
