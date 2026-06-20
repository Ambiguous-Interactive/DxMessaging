#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System.Collections;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using DxMessaging.Unity;
    using NUnit.Framework;
    using UnityEngine;
    using UnityEngine.TestTools;

    public sealed class DefaultBusFallbackTests : MessagingTestBase
    {
        private readonly List<MessageRegistrationHandle> _handles = new();
        private readonly List<MessageRegistrationToken> _tokens = new();

        [TearDown]
        public void TearDown()
        {
            foreach (MessageRegistrationToken token in _tokens)
            {
                foreach (MessageRegistrationHandle handle in _handles)
                {
                    token.RemoveRegistration(handle);
                }
                token.Disable();
            }

            _handles.Clear();
            _tokens.Clear();
        }

        /// <summary>
        /// A handler whose token is created without an explicit bus (so it
        /// inherits the bus injected into its owning <see cref="MessageHandler"/>)
        /// must observe emissions on that injected bus and stay isolated from
        /// the global bus. Parameterized over every dispatch kind via
        /// <see cref="MessageScenarios.AllKinds"/>; the prior per-kind triplet
        /// (WhenTokenOmitsBus / ForTargetedMessages / ForBroadcastMessages)
        /// collapsed into this single method.
        /// The source-agnostic broadcast-without-source variant is covered by
        /// <see cref="HandlerUsesInjectedDefaultBusForBroadcastWithoutSource"/>.
        /// </summary>
        [Test]
        public void HandlerUsesInjectedDefaultBus(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            MessageBus customBus = new();
            MessageHandler handler = new(new InstanceId(42), customBus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler);
            _tokens.Add(token);

            InstanceId target = new(1234);
            int callCount = 0;
            MessageRegistrationHandle handle;
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    handle = ScenarioHarness.RegisterUntargeted<SimpleUntargetedMessage>(
                        scenario,
                        token,
                        (ref SimpleUntargetedMessage _) => ++callCount
                    );
                    break;
                }
                case MessageKind.Targeted:
                {
                    handle = ScenarioHarness.RegisterTargeted<SimpleTargetedMessage>(
                        scenario,
                        token,
                        target,
                        (ref SimpleTargetedMessage _) => ++callCount
                    );
                    break;
                }
                case MessageKind.Broadcast:
                {
                    handle = ScenarioHarness.RegisterBroadcast<SimpleBroadcastMessage>(
                        scenario,
                        token,
                        target,
                        (ref SimpleBroadcastMessage _) => ++callCount
                    );
                    break;
                }
                default:
                {
                    throw new System.ArgumentOutOfRangeException(
                        nameof(scenario),
                        scenario.Kind,
                        "Unsupported message kind."
                    );
                }
            }
            _handles.Add(handle);

            token.Enable();

            EmitForScenario(scenario, target, callOnCustomBus: true, customBus);
            Assert.AreEqual(
                1,
                callCount,
                "[{0}] Handler should observe messages emitted through its injected bus.",
                scenario.Kind
            );

            EmitForScenario(scenario, target, callOnCustomBus: false, customBus);
            Assert.AreEqual(
                1,
                callCount,
                "[{0}] Handler should remain isolated from the global bus when using an injected default.",
                scenario.Kind
            );
        }

        private static void EmitForScenario(
            MessageScenario scenario,
            InstanceId target,
            bool callOnCustomBus,
            MessageBus customBus
        )
        {
            IMessageBus bus = callOnCustomBus ? customBus : null;
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
                    ScenarioHarness.EmitTargeted(scenario, ref message, target, bus);
                    return;
                }
                case MessageKind.Broadcast:
                {
                    SimpleBroadcastMessage message = new();
                    ScenarioHarness.EmitBroadcast(scenario, ref message, target, bus);
                    return;
                }
                default:
                {
                    throw new System.ArgumentOutOfRangeException(
                        nameof(scenario),
                        scenario.Kind,
                        "Unsupported message kind."
                    );
                }
            }
        }

        /// <summary>
        /// Dedicated coverage for the source-agnostic broadcast path: a handler
        /// registered via <see cref="MessageRegistrationToken.RegisterBroadcastWithoutSource{T}(MessageHandler.FastHandlerWithContext{T}, int)"/>
        /// on a token whose bus is injected (omitted at create time) must observe
        /// a broadcast emitted from any source on that injected bus and stay
        /// isolated from the global bus. This reproduces the original
        /// HandlerUsesInjectedDefaultBusForBroadcastMessages assertions, which the
        /// data-driven <see cref="HandlerUsesInjectedDefaultBus"/> (register-with-target)
        /// does not exercise.
        /// </summary>
        [Test]
        public void HandlerUsesInjectedDefaultBusForBroadcastWithoutSource()
        {
            MessageBus customBus = new();
            MessageHandler handler = new(new InstanceId(1337), customBus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler);
            _tokens.Add(token);

            int callCount = 0;
            MessageRegistrationHandle handle =
                token.RegisterBroadcastWithoutSource<SimpleBroadcastMessage>(
                    (ref InstanceId _, ref SimpleBroadcastMessage _) => ++callCount
                );
            _handles.Add(handle);

            token.Enable();

            SimpleBroadcastMessage message = new();
            InstanceId source = new(777);
            message.EmitBroadcast(source, customBus);
            Assert.AreEqual(
                1,
                callCount,
                "Broadcast handlers should observe emissions on the injected bus."
            );

            message.EmitBroadcast(source);
            Assert.AreEqual(
                1,
                callCount,
                "Broadcast handler should remain isolated from global emissions when using an injected default bus."
            );
        }

        [Test]
        public void MessagingComponentConfigureRebindsExistingHandlerToCustomBus()
        {
            MessageBus customBus = new();
            GameObject go = new(
                nameof(MessagingComponentConfigureRebindsExistingHandlerToCustomBus),
                typeof(MessagingComponent),
                typeof(ManualListenerComponent)
            );
            _spawned.Add(go);

            MessagingComponent messagingComponent = go.GetComponent<MessagingComponent>();
            messagingComponent.Configure(customBus, MessageBusRebindMode.RebindActive);

            ManualListenerComponent listener = go.GetComponent<ManualListenerComponent>();
            MessageRegistrationToken token = listener.RequestToken(messagingComponent);
            _tokens.Add(token);

            int callCount = 0;
            MessageRegistrationHandle handle = token.RegisterUntargeted<SimpleUntargetedMessage>(
                _ => ++callCount
            );
            _handles.Add(handle);
            token.Enable();

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted(customBus);
            Assert.AreEqual(
                1,
                callCount,
                "Configure should redirect tokens to the provided MessageBus."
            );

            message.EmitUntargeted();
            Assert.AreEqual(
                1,
                callCount,
                "Global emissions should not reach tokens configured to use a custom bus."
            );
        }

        [Test]
        public void MessagingComponentConfigurePreserveDefersRebindUntilReenabled()
        {
            MessageBus initialBus = new();
            MessageBus newBus = new();
            GameObject go = new(
                nameof(MessagingComponentConfigurePreserveDefersRebindUntilReenabled),
                typeof(MessagingComponent),
                typeof(ManualListenerComponent)
            );
            _spawned.Add(go);

            MessagingComponent messagingComponent = go.GetComponent<MessagingComponent>();
            messagingComponent.Configure(initialBus, MessageBusRebindMode.RebindActive);

            ManualListenerComponent listener = go.GetComponent<ManualListenerComponent>();
            MessageRegistrationToken token = listener.RequestToken(messagingComponent);
            _tokens.Add(token);

            int callCount = 0;
            MessageRegistrationHandle handle = token.RegisterUntargeted<SimpleUntargetedMessage>(
                _ => ++callCount
            );
            _handles.Add(handle);
            token.Enable();

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted(initialBus);
            Assert.AreEqual(1, callCount, "Initial bus should deliver to the registered handler.");

            messagingComponent.Configure(newBus, MessageBusRebindMode.PreserveRegistrations);

            message.EmitUntargeted(newBus);
            Assert.AreEqual(
                1,
                callCount,
                "Preserve mode should avoid rebinding active registrations immediately."
            );

            message.EmitUntargeted(initialBus);
            Assert.AreEqual(
                2,
                callCount,
                "Handlers should continue observing the original bus until re-enabled."
            );

            token.Disable();
            token.Enable();

            message.EmitUntargeted(newBus);
            Assert.AreEqual(
                3,
                callCount,
                "Re-enabling the token should rebind handlers to the new bus."
            );
        }

        [UnityTest]
        public IEnumerator MessageAwareComponentConfigureMessageBusAppliesOverride()
        {
            MessageBus customBus = new();
            GameObject go = new(
                nameof(MessageAwareComponentConfigureMessageBusAppliesOverride),
                typeof(MessagingComponent),
                typeof(BusAwareComponent)
            );
            _spawned.Add(go);

            BusAwareComponent component = go.GetComponent<BusAwareComponent>();
            component.ConfigureMessageBus(customBus, MessageBusRebindMode.RebindActive);

            yield return null;

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted(customBus);
            Assert.AreEqual(
                1,
                component.Received,
                "MessageAwareComponent should route through the configured bus."
            );

            message.EmitUntargeted();
            Assert.AreEqual(
                1,
                component.Received,
                "Global bus should no longer deliver to the component after override."
            );
        }

        [UnityTest]
        public IEnumerator MessageAwareComponentConfigureMessageBusPreserveDefersUntilReenable()
        {
            MessageBus initialBus = new();
            MessageBus newBus = new();
            GameObject go = new(
                nameof(MessageAwareComponentConfigureMessageBusPreserveDefersUntilReenable),
                typeof(MessagingComponent),
                typeof(BusAwareComponent)
            );
            _spawned.Add(go);

            BusAwareComponent component = go.GetComponent<BusAwareComponent>();
            component.ConfigureMessageBus(initialBus, MessageBusRebindMode.RebindActive);

            yield return null;

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted(initialBus);
            Assert.AreEqual(
                1,
                component.Received,
                "Component should receive messages emitted via the initial bus."
            );

            component.ConfigureMessageBus(newBus, MessageBusRebindMode.PreserveRegistrations);

            message.EmitUntargeted(newBus);
            Assert.AreEqual(
                1,
                component.Received,
                "Preserve mode should not rebind active handlers immediately."
            );

            message.EmitUntargeted(initialBus);
            Assert.AreEqual(
                2,
                component.Received,
                "Existing registrations should continue to observe the original bus."
            );

            component.Token.Disable();
            component.Token.Enable();

            message.EmitUntargeted(newBus);
            Assert.AreEqual(
                3,
                component.Received,
                "Re-enabling the token should rebind handlers to the new bus."
            );
        }

        private sealed class BusAwareComponent : MessageAwareComponent
        {
            internal int Received { get; private set; }

            protected override bool RegisterForStringMessages => false;

            protected override void RegisterMessageHandlers()
            {
                base.RegisterMessageHandlers();
                _ = Token.RegisterUntargeted<SimpleUntargetedMessage>(_ => ++Received);
            }
        }
    }
}

#endif
