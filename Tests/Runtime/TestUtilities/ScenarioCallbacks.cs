#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime
{
    using System;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Tests.Runtime.Scripts.Messages;

    /// <summary>
    /// Callback-only companions to <see cref="ScenarioHarness"/>. Each helper hides the
    /// per-kind switch over the canonical Simple* message types so fixtures that only
    /// need "the callback ran" semantics (counting handlers, counting post-processors,
    /// boolean interceptors, single emissions) do not hand-roll the same
    /// switch(scenario.Kind) blocks. Fixtures that need custom message types, custom
    /// buses, or payload access should keep using <see cref="ScenarioHarness"/> directly.
    /// </summary>
    public static class ScenarioCallbacks
    {
        /// <summary>
        /// Registers a fast-handler (ref-message) shaped handler for the scenario's kind
        /// that invokes <paramref name="onInvoked"/> and ignores the message payload.
        /// <paramref name="context"/> is used as the target (Targeted) or source
        /// (Broadcast) and is ignored for Untargeted scenarios.
        /// </summary>
        public static MessageRegistrationHandle RegisterCountingHandler(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId context,
            Action onInvoked,
            int priority = 0
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    return ScenarioHarness.RegisterUntargeted<SimpleUntargetedMessage>(
                        scenario,
                        token,
                        (ref SimpleUntargetedMessage _) => onInvoked(),
                        priority
                    );
                }
                case MessageKind.Targeted:
                {
                    return ScenarioHarness.RegisterTargeted<SimpleTargetedMessage>(
                        scenario,
                        token,
                        context,
                        (ref SimpleTargetedMessage _) => onInvoked(),
                        priority
                    );
                }
                case MessageKind.Broadcast:
                {
                    return ScenarioHarness.RegisterBroadcast<SimpleBroadcastMessage>(
                        scenario,
                        token,
                        context,
                        (ref SimpleBroadcastMessage _) => onInvoked(),
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

        /// <summary>
        /// Registers a default-handler (Action&lt;T&gt;) shaped handler for the scenario's
        /// kind that invokes <paramref name="onInvoked"/> and ignores the message payload.
        /// Use this instead of <see cref="RegisterCountingHandler"/> when the test pins
        /// behavior of the Action-shaped registration overloads specifically.
        /// </summary>
        public static MessageRegistrationHandle RegisterDefaultHandler(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId context,
            Action onInvoked,
            int priority = 0
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    return token.RegisterUntargeted<SimpleUntargetedMessage>(
                        _ => onInvoked(),
                        priority: priority
                    );
                }
                case MessageKind.Targeted:
                {
                    return token.RegisterTargeted<SimpleTargetedMessage>(
                        context,
                        _ => onInvoked(),
                        priority: priority
                    );
                }
                case MessageKind.Broadcast:
                {
                    return token.RegisterBroadcast<SimpleBroadcastMessage>(
                        context,
                        _ => onInvoked(),
                        priority: priority
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

        /// <summary>
        /// Registers a post-processor for the scenario's kind that invokes
        /// <paramref name="onInvoked"/> and ignores the message payload.
        /// </summary>
        public static MessageRegistrationHandle RegisterCountingPostProcessor(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId context,
            Action onInvoked,
            int priority = 0
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    return ScenarioHarness.RegisterUntargetedPostProcessor<SimpleUntargetedMessage>(
                        scenario,
                        token,
                        (ref SimpleUntargetedMessage _) => onInvoked(),
                        priority
                    );
                }
                case MessageKind.Targeted:
                {
                    return ScenarioHarness.RegisterTargetedPostProcessor<SimpleTargetedMessage>(
                        scenario,
                        token,
                        context,
                        (ref SimpleTargetedMessage _) => onInvoked(),
                        priority
                    );
                }
                case MessageKind.Broadcast:
                {
                    return ScenarioHarness.RegisterBroadcastPostProcessor<SimpleBroadcastMessage>(
                        scenario,
                        token,
                        context,
                        (ref SimpleBroadcastMessage _) => onInvoked(),
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

        /// <summary>
        /// Registers an interceptor for the scenario's kind. The interceptor first runs
        /// <paramref name="onIntercepted"/> (when provided) and then returns the value of
        /// <paramref name="result"/>; the message payload is ignored. Pass a throwing
        /// <paramref name="result"/> to model an interceptor that faults mid-dispatch.
        /// </summary>
        public static MessageRegistrationHandle RegisterCountingInterceptor(
            MessageScenario scenario,
            MessageRegistrationToken token,
            Func<bool> result,
            Action onIntercepted = null,
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
                        (ref SimpleUntargetedMessage _) =>
                        {
                            onIntercepted?.Invoke();
                            return result();
                        },
                        priority
                    );
                }
                case MessageKind.Targeted:
                {
                    return ScenarioHarness.RegisterTargetedInterceptor<SimpleTargetedMessage>(
                        scenario,
                        token,
                        (ref InstanceId _, ref SimpleTargetedMessage __) =>
                        {
                            onIntercepted?.Invoke();
                            return result();
                        },
                        priority
                    );
                }
                case MessageKind.Broadcast:
                {
                    return ScenarioHarness.RegisterBroadcastInterceptor<SimpleBroadcastMessage>(
                        scenario,
                        token,
                        (ref InstanceId _, ref SimpleBroadcastMessage __) =>
                        {
                            onIntercepted?.Invoke();
                            return result();
                        },
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

        /// <summary>
        /// Emits a single default-constructed Simple* message of the scenario's kind on
        /// the global bus. <paramref name="context"/> is used as the target (Targeted) or
        /// source (Broadcast) and is ignored for Untargeted scenarios.
        /// </summary>
        public static void EmitForKind(MessageScenario scenario, InstanceId context)
        {
            EmitForKind(scenario, messageBus: null, context: context);
        }

        /// <summary>
        /// Emits a single default-constructed Simple* message of the scenario's kind on
        /// <paramref name="messageBus"/>, or on the global bus when
        /// <paramref name="messageBus"/> is null. <paramref name="context"/> is used as
        /// the target (Targeted) or source (Broadcast) and is ignored for Untargeted
        /// scenarios.
        /// </summary>
        public static void EmitForKind(
            MessageScenario scenario,
            IMessageBus messageBus,
            InstanceId context
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    SimpleUntargetedMessage message = new();
                    ScenarioHarness.EmitUntargeted(scenario, ref message, messageBus);
                    return;
                }
                case MessageKind.Targeted:
                {
                    SimpleTargetedMessage message = new();
                    ScenarioHarness.EmitTargeted(scenario, ref message, context, messageBus);
                    return;
                }
                case MessageKind.Broadcast:
                {
                    SimpleBroadcastMessage message = new();
                    ScenarioHarness.EmitBroadcast(scenario, ref message, context, messageBus);
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
