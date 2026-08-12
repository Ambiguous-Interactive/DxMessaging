#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using UnityEngine;

    /// <summary>
    /// Pins the v4 opaque-handle contract for <see cref="MessageBusRegistration"/>: the
    /// <see cref="MessageBusRegistration.None"/> sentinel, the external/DIY constructor
    /// round-trip, value equality, and the "foreign / empty handle deregistration is a silent
    /// no-op" guarantee on the built-in <see cref="MessageBus"/>.
    /// </summary>
    public sealed class MessageBusRegistrationContractTests
    {
        [Test]
        public void NoneHandleIsInvalidAndEqualsDefault()
        {
            MessageBusRegistration none = MessageBusRegistration.None;
            Assert.IsFalse(none.IsValid, "None must be invalid.");
            Assert.AreEqual(default(MessageBusRegistration), none, "None must equal default.");
            Assert.IsTrue(none == default, "None == default must hold.");
            Assert.IsFalse(none != default, "None != default must be false.");
            Assert.AreEqual(none.GetHashCode(), default(MessageBusRegistration).GetHashCode());
        }

        [Test]
        public void ExternalConstructorRoundTripsIdAndState()
        {
            object state = new();
            MessageBusRegistration external = new(42L, state);
            Assert.IsTrue(external.IsValid, "An external handle is a live (valid) handle.");
            Assert.AreEqual(42L, external.ExternalId);
            Assert.AreSame(state, external.ExternalState);
        }

        [Test]
        public void NonExternalHandleExposesNoExternalPayload()
        {
            MessageBusRegistration none = MessageBusRegistration.None;
            Assert.AreEqual(0L, none.ExternalId);
            Assert.IsNull(none.ExternalState);
        }

        [Test]
        public void EqualityIsValueBased()
        {
            object state = new();
            MessageBusRegistration a = new(7L, state);
            MessageBusRegistration b = new(7L, state);
            MessageBusRegistration c = new(8L, state);
            Assert.AreEqual(a, b, "Same id + same state ref must be equal.");
            Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
            Assert.AreNotEqual(a, c, "Different id must be unequal.");
            Assert.IsTrue(a.Equals((object)b));
            Assert.IsFalse(a.Equals("not a handle"));
        }

        [Test]
        public void DeregisterNoneOnMessageBusIsSilentNoOp()
        {
            IMessageBus bus = MessageHandler.MessageBus;
            MessageBusRegistration none = MessageBusRegistration.None;
            Assert.DoesNotThrow(() => bus.Deregister<SimpleUntargetedMessage>(in none));
        }

        [Test]
        public void DeregisterExternalHandleOnMessageBusIsSilentNoOp()
        {
            IMessageBus bus = MessageHandler.MessageBus;
            MessageBusRegistration external = new(123L, "owned-by-a-foreign-bus");
            // A handle minted by a custom IMessageBus (kind == External) owns no store on the
            // built-in MessageBus, so deregistering it here must be a no-op (no throw).
            Assert.DoesNotThrow(() => bus.Deregister<SimpleUntargetedMessage>(in external));
        }

        [Test]
        public void RefcountRegistrationsMintDistinctHandles()
        {
            GameObject go = new(nameof(RefcountRegistrationsMintDistinctHandles));
            try
            {
                MessageHandler handler = new(go) { active = true };
                IMessageBus bus = MessageHandler.MessageBus;

                // Two distinct Register* calls for the SAME (handler, type, priority) bump the
                // handler's refcount. Pre-v4 each returned a distinct deregistration delegate;
                // the v4 handles must likewise be UNEQUAL so a consumer that stores handles in a
                // set/map and deregisters per-unique-handle does not under-deregister (leak).
                MessageBusRegistration first = bus.RegisterUntargeted<SimpleUntargetedMessage>(
                    handler
                );
                MessageBusRegistration second = bus.RegisterUntargeted<SimpleUntargetedMessage>(
                    handler
                );
                try
                {
                    Assert.AreNotEqual(
                        first,
                        second,
                        "Two refcount registrations must mint unequal handles."
                    );
                    Assert.IsTrue(first != second);
                }
                finally
                {
                    bus.Deregister<SimpleUntargetedMessage>(in first);
                    bus.Deregister<SimpleUntargetedMessage>(in second);
                    handler.active = false;
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void DispatchStateStaysLazyUntilFirstEmission(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.WithAndWithoutPostProcessorIncludingWithoutContext)
            )]
                MessageScenario scenario
        )
        {
            MessageBus bus = new MessageBus { DiagnosticsMode = false };
            MessageHandler handler = new MessageHandler(new InstanceId(404), bus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            token.Enable();
            int calls = 0;
            InstanceId context = new InstanceId(405);

            using (
                new LeakWatcher(
                    bus,
                    label: nameof(DispatchStateStaysLazyUntilFirstEmission) + ":" + scenario
                )
            )
            {
                try
                {
                    _ = RegisterCountingSink(scenario, token, context, () => ++calls);
                    Assert.IsFalse(
                        HasDispatchState(bus, scenario, context),
                        "[{0}] Registration must not allocate dispatch state before a matching emission.",
                        scenario
                    );

                    EmitForScenario(scenario, bus, context);
                    Assert.AreEqual(
                        1,
                        calls,
                        "[{0}] The first emission must build and use the snapshot.",
                        scenario
                    );
                    Assert.IsTrue(
                        HasDispatchState(bus, scenario, context),
                        "[{0}] The first emission must materialize dispatch state.",
                        scenario
                    );

                    _ = RegisterCountingSink(scenario, token, context, () => calls += 10);
                    EmitForScenario(scenario, bus, context);
                    Assert.AreEqual(
                        12,
                        calls,
                        "[{0}] A registration after first emission must dirty and rebuild the existing state.",
                        scenario
                    );
                }
                finally
                {
                    token.UnregisterAll();
                    token.Dispose();
                    handler.active = false;
                }
            }
        }

        private static bool HasDispatchState(
            MessageBus bus,
            MessageScenario scenario,
            InstanceId context
        )
        {
            RegistrationMethod method = GetRegistrationMethod(scenario);
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                    return bus.HasDispatchStateForTesting<SimpleUntargetedMessage>(method, context);
                case MessageKind.Targeted:
                case MessageKind.TargetedWithoutTargeting:
                    return bus.HasDispatchStateForTesting<SimpleTargetedMessage>(method, context);
                case MessageKind.Broadcast:
                case MessageKind.BroadcastWithoutSource:
                    return bus.HasDispatchStateForTesting<SimpleBroadcastMessage>(method, context);
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(scenario),
                        scenario.Kind,
                        "Unsupported message kind."
                    );
            }
        }

        private static MessageRegistrationHandle RegisterCountingSink(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId context,
            Action onInvoked
        )
        {
            if (scenario.UsePostProcessor)
            {
                switch (scenario.Kind)
                {
                    case MessageKind.Untargeted:
                        return token.RegisterUntargetedPostProcessor<SimpleUntargetedMessage>(
                            (ref SimpleUntargetedMessage _) => onInvoked()
                        );
                    case MessageKind.Targeted:
                        return token.RegisterTargetedPostProcessor<SimpleTargetedMessage>(
                            context,
                            (ref SimpleTargetedMessage _) => onInvoked()
                        );
                    case MessageKind.Broadcast:
                        return token.RegisterBroadcastPostProcessor<SimpleBroadcastMessage>(
                            context,
                            (ref SimpleBroadcastMessage _) => onInvoked()
                        );
                    case MessageKind.TargetedWithoutTargeting:
                        return token.RegisterTargetedWithoutTargetingPostProcessor<SimpleTargetedMessage>(
                            (ref InstanceId _, ref SimpleTargetedMessage __) => onInvoked()
                        );
                    case MessageKind.BroadcastWithoutSource:
                        return token.RegisterBroadcastWithoutSourcePostProcessor<SimpleBroadcastMessage>(
                            (ref InstanceId _, ref SimpleBroadcastMessage __) => onInvoked()
                        );
                    default:
                        throw UnsupportedScenario(scenario);
                }
            }

            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                    return token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) => onInvoked()
                    );
                case MessageKind.Targeted:
                    return token.RegisterTargeted<SimpleTargetedMessage>(
                        context,
                        (ref SimpleTargetedMessage _) => onInvoked()
                    );
                case MessageKind.Broadcast:
                    return token.RegisterBroadcast<SimpleBroadcastMessage>(
                        context,
                        (ref SimpleBroadcastMessage _) => onInvoked()
                    );
                case MessageKind.TargetedWithoutTargeting:
                    return token.RegisterTargetedWithoutTargeting<SimpleTargetedMessage>(
                        (ref InstanceId _, ref SimpleTargetedMessage __) => onInvoked()
                    );
                case MessageKind.BroadcastWithoutSource:
                    return token.RegisterBroadcastWithoutSource<SimpleBroadcastMessage>(
                        (ref InstanceId _, ref SimpleBroadcastMessage __) => onInvoked()
                    );
                default:
                    throw UnsupportedScenario(scenario);
            }
        }

        private static RegistrationMethod GetRegistrationMethod(MessageScenario scenario)
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                    return scenario.UsePostProcessor
                        ? RegistrationMethod.UntargetedPostProcessor
                        : RegistrationMethod.Untargeted;
                case MessageKind.Targeted:
                    return scenario.UsePostProcessor
                        ? RegistrationMethod.TargetedPostProcessor
                        : RegistrationMethod.Targeted;
                case MessageKind.Broadcast:
                    return scenario.UsePostProcessor
                        ? RegistrationMethod.BroadcastPostProcessor
                        : RegistrationMethod.Broadcast;
                case MessageKind.TargetedWithoutTargeting:
                    return scenario.UsePostProcessor
                        ? RegistrationMethod.TargetedWithoutTargetingPostProcessor
                        : RegistrationMethod.TargetedWithoutTargeting;
                case MessageKind.BroadcastWithoutSource:
                    return scenario.UsePostProcessor
                        ? RegistrationMethod.BroadcastWithoutSourcePostProcessor
                        : RegistrationMethod.BroadcastWithoutSource;
                default:
                    throw UnsupportedScenario(scenario);
            }
        }

        private static void EmitForScenario(
            MessageScenario scenario,
            IMessageBus bus,
            InstanceId context
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                    SimpleUntargetedMessage untargeted = new();
                    untargeted.EmitUntargeted(bus);
                    return;
                case MessageKind.Targeted:
                case MessageKind.TargetedWithoutTargeting:
                    SimpleTargetedMessage targeted = new();
                    targeted.EmitTargeted(context, bus);
                    return;
                case MessageKind.Broadcast:
                case MessageKind.BroadcastWithoutSource:
                    SimpleBroadcastMessage broadcast = new();
                    broadcast.EmitBroadcast(context, bus);
                    return;
                default:
                    throw UnsupportedScenario(scenario);
            }
        }

        private static ArgumentOutOfRangeException UnsupportedScenario(MessageScenario scenario)
        {
            return new ArgumentOutOfRangeException(
                nameof(scenario),
                scenario.Kind,
                "Unsupported message kind."
            );
        }
    }
}
#endif
