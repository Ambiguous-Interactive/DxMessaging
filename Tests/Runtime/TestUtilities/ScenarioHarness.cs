namespace DxMessaging.Tests.Runtime
{
    using System;
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;

    /// <summary>
    /// Picks the correct register / emit overload for a given
    /// <see cref="MessageScenario"/>. Three method families are exposed (one per
    /// message kind) because the underlying interfaces are different and
    /// generic-only dispatch is not expressible without runtime reflection.
    /// </summary>
    public static class ScenarioHarness
    {
        /// <summary>
        /// Registers a handler for an untargeted message. The scenario's
        /// <see cref="MessageScenario.Kind"/> must be <see cref="MessageKind.Untargeted"/>.
        /// </summary>
        public static MessageRegistrationHandle RegisterUntargeted<TMessage>(
            MessageScenario scenario,
            MessageRegistrationToken token,
            MessageHandler.FastHandler<TMessage> handler,
            int priority = 0
        )
            where TMessage : IUntargetedMessage
        {
            if (scenario == null)
            {
                throw new ArgumentNullException(nameof(scenario));
            }

            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            if (scenario.Kind != MessageKind.Untargeted)
            {
                throw new ArgumentException(
                    $"RegisterUntargeted requires Kind=Untargeted but got {scenario.Kind}.",
                    nameof(scenario)
                );
            }

            return token.RegisterUntargeted<TMessage>(handler, priority: priority);
        }

        /// <summary>
        /// Registers a handler for a targeted message. The scenario's
        /// <see cref="MessageScenario.Kind"/> must be <see cref="MessageKind.Targeted"/>.
        /// </summary>
        public static MessageRegistrationHandle RegisterTargeted<TMessage>(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId target,
            MessageHandler.FastHandler<TMessage> handler,
            int priority = 0
        )
            where TMessage : ITargetedMessage
        {
            if (scenario == null)
            {
                throw new ArgumentNullException(nameof(scenario));
            }

            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            if (scenario.Kind != MessageKind.Targeted)
            {
                throw new ArgumentException(
                    $"RegisterTargeted requires Kind=Targeted but got {scenario.Kind}.",
                    nameof(scenario)
                );
            }

            return token.RegisterTargeted<TMessage>(target, handler, priority: priority);
        }

        /// <summary>
        /// Registers a handler for a broadcast message. The scenario's
        /// <see cref="MessageScenario.Kind"/> must be <see cref="MessageKind.Broadcast"/>.
        /// </summary>
        public static MessageRegistrationHandle RegisterBroadcast<TMessage>(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId source,
            MessageHandler.FastHandler<TMessage> handler,
            int priority = 0
        )
            where TMessage : IBroadcastMessage
        {
            if (scenario == null)
            {
                throw new ArgumentNullException(nameof(scenario));
            }

            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            if (scenario.Kind != MessageKind.Broadcast)
            {
                throw new ArgumentException(
                    $"RegisterBroadcast requires Kind=Broadcast but got {scenario.Kind}.",
                    nameof(scenario)
                );
            }

            return token.RegisterBroadcast<TMessage>(source, handler, priority: priority);
        }

        /// <summary>
        /// Emits an untargeted struct message via the canonical extension method.
        /// </summary>
        public static void EmitUntargeted<TMessage>(
            MessageScenario scenario,
            ref TMessage message,
            IMessageBus messageBus = null
        )
            where TMessage : struct, IUntargetedMessage
        {
            if (scenario == null)
            {
                throw new ArgumentNullException(nameof(scenario));
            }

            if (scenario.Kind != MessageKind.Untargeted)
            {
                throw new ArgumentException(
                    $"EmitUntargeted requires Kind=Untargeted but got {scenario.Kind}.",
                    nameof(scenario)
                );
            }

            message.EmitUntargeted(messageBus);
        }

        /// <summary>
        /// Emits an untargeted reference-type message via the canonical extension method.
        /// </summary>
        public static void EmitUntargeted<TMessage>(
            MessageScenario scenario,
            TMessage message,
            IMessageBus messageBus = null
        )
            where TMessage : class, IUntargetedMessage
        {
            if (scenario == null)
            {
                throw new ArgumentNullException(nameof(scenario));
            }

            if (scenario.Kind != MessageKind.Untargeted)
            {
                throw new ArgumentException(
                    $"EmitUntargeted requires Kind=Untargeted but got {scenario.Kind}.",
                    nameof(scenario)
                );
            }

            message.EmitUntargeted(messageBus);
        }

        /// <summary>
        /// Emits a targeted struct message via the canonical extension method.
        /// </summary>
        public static void EmitTargeted<TMessage>(
            MessageScenario scenario,
            ref TMessage message,
            InstanceId target,
            IMessageBus messageBus = null
        )
            where TMessage : struct, ITargetedMessage
        {
            if (scenario == null)
            {
                throw new ArgumentNullException(nameof(scenario));
            }

            if (scenario.Kind != MessageKind.Targeted)
            {
                throw new ArgumentException(
                    $"EmitTargeted requires Kind=Targeted but got {scenario.Kind}.",
                    nameof(scenario)
                );
            }

            message.EmitTargeted(target, messageBus);
        }

        /// <summary>
        /// Emits a targeted reference-type message via the canonical extension method.
        /// </summary>
        public static void EmitTargeted<TMessage>(
            MessageScenario scenario,
            TMessage message,
            InstanceId target,
            IMessageBus messageBus = null
        )
            where TMessage : class, ITargetedMessage
        {
            if (scenario == null)
            {
                throw new ArgumentNullException(nameof(scenario));
            }

            if (scenario.Kind != MessageKind.Targeted)
            {
                throw new ArgumentException(
                    $"EmitTargeted requires Kind=Targeted but got {scenario.Kind}.",
                    nameof(scenario)
                );
            }

            message.EmitTargeted(target, messageBus);
        }

        /// <summary>
        /// Emits a broadcast struct message via the canonical extension method.
        /// </summary>
        public static void EmitBroadcast<TMessage>(
            MessageScenario scenario,
            ref TMessage message,
            InstanceId source,
            IMessageBus messageBus = null
        )
            where TMessage : struct, IBroadcastMessage
        {
            if (scenario == null)
            {
                throw new ArgumentNullException(nameof(scenario));
            }

            if (scenario.Kind != MessageKind.Broadcast)
            {
                throw new ArgumentException(
                    $"EmitBroadcast requires Kind=Broadcast but got {scenario.Kind}.",
                    nameof(scenario)
                );
            }

            message.EmitBroadcast(source, messageBus);
        }

        /// <summary>
        /// Emits a broadcast reference-type message via the canonical extension method.
        /// </summary>
        public static void EmitBroadcast<TMessage>(
            MessageScenario scenario,
            TMessage message,
            InstanceId source,
            IMessageBus messageBus = null
        )
            where TMessage : class, IBroadcastMessage
        {
            if (scenario == null)
            {
                throw new ArgumentNullException(nameof(scenario));
            }

            if (scenario.Kind != MessageKind.Broadcast)
            {
                throw new ArgumentException(
                    $"EmitBroadcast requires Kind=Broadcast but got {scenario.Kind}.",
                    nameof(scenario)
                );
            }

            message.EmitBroadcast(source, messageBus);
        }

        /// <summary>
        /// Registers an interceptor for an untargeted message. The scenario's
        /// <see cref="MessageScenario.Kind"/> must be <see cref="MessageKind.Untargeted"/>.
        /// </summary>
        public static MessageRegistrationHandle RegisterUntargetedInterceptor<TMessage>(
            MessageScenario scenario,
            MessageRegistrationToken token,
            IMessageBus.UntargetedInterceptor<TMessage> interceptor,
            int priority = 0
        )
            where TMessage : IUntargetedMessage
        {
            if (scenario == null)
            {
                throw new ArgumentNullException(nameof(scenario));
            }

            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            if (interceptor == null)
            {
                throw new ArgumentNullException(nameof(interceptor));
            }

            if (scenario.Kind != MessageKind.Untargeted)
            {
                throw new ArgumentException(
                    $"RegisterUntargetedInterceptor requires Kind=Untargeted but got {scenario.Kind}.",
                    nameof(scenario)
                );
            }

            return token.RegisterUntargetedInterceptor<TMessage>(interceptor, priority: priority);
        }

        /// <summary>
        /// Registers an interceptor for a targeted message. The scenario's
        /// <see cref="MessageScenario.Kind"/> must be <see cref="MessageKind.Targeted"/>.
        /// </summary>
        public static MessageRegistrationHandle RegisterTargetedInterceptor<TMessage>(
            MessageScenario scenario,
            MessageRegistrationToken token,
            IMessageBus.TargetedInterceptor<TMessage> interceptor,
            int priority = 0
        )
            where TMessage : ITargetedMessage
        {
            if (scenario == null)
            {
                throw new ArgumentNullException(nameof(scenario));
            }

            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            if (interceptor == null)
            {
                throw new ArgumentNullException(nameof(interceptor));
            }

            if (scenario.Kind != MessageKind.Targeted)
            {
                throw new ArgumentException(
                    $"RegisterTargetedInterceptor requires Kind=Targeted but got {scenario.Kind}.",
                    nameof(scenario)
                );
            }

            return token.RegisterTargetedInterceptor<TMessage>(interceptor, priority: priority);
        }

        /// <summary>
        /// Registers an interceptor for a broadcast message. The scenario's
        /// <see cref="MessageScenario.Kind"/> must be <see cref="MessageKind.Broadcast"/>.
        /// </summary>
        public static MessageRegistrationHandle RegisterBroadcastInterceptor<TMessage>(
            MessageScenario scenario,
            MessageRegistrationToken token,
            IMessageBus.BroadcastInterceptor<TMessage> interceptor,
            int priority = 0
        )
            where TMessage : IBroadcastMessage
        {
            if (scenario == null)
            {
                throw new ArgumentNullException(nameof(scenario));
            }

            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            if (interceptor == null)
            {
                throw new ArgumentNullException(nameof(interceptor));
            }

            if (scenario.Kind != MessageKind.Broadcast)
            {
                throw new ArgumentException(
                    $"RegisterBroadcastInterceptor requires Kind=Broadcast but got {scenario.Kind}.",
                    nameof(scenario)
                );
            }

            return token.RegisterBroadcastInterceptor<TMessage>(interceptor, priority: priority);
        }

        /// <summary>
        /// Registers a post-processor for an untargeted message. The scenario's
        /// <see cref="MessageScenario.Kind"/> must be <see cref="MessageKind.Untargeted"/>.
        /// </summary>
        public static MessageRegistrationHandle RegisterUntargetedPostProcessor<TMessage>(
            MessageScenario scenario,
            MessageRegistrationToken token,
            MessageHandler.FastHandler<TMessage> postProcessor,
            int priority = 0
        )
            where TMessage : IUntargetedMessage
        {
            if (scenario == null)
            {
                throw new ArgumentNullException(nameof(scenario));
            }

            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            if (postProcessor == null)
            {
                throw new ArgumentNullException(nameof(postProcessor));
            }

            if (scenario.Kind != MessageKind.Untargeted)
            {
                throw new ArgumentException(
                    $"RegisterUntargetedPostProcessor requires Kind=Untargeted but got {scenario.Kind}.",
                    nameof(scenario)
                );
            }

            return token.RegisterUntargetedPostProcessor<TMessage>(
                postProcessor,
                priority: priority
            );
        }

        /// <summary>
        /// Registers a post-processor for a targeted message. The scenario's
        /// <see cref="MessageScenario.Kind"/> must be <see cref="MessageKind.Targeted"/>.
        /// </summary>
        public static MessageRegistrationHandle RegisterTargetedPostProcessor<TMessage>(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId target,
            MessageHandler.FastHandler<TMessage> postProcessor,
            int priority = 0
        )
            where TMessage : ITargetedMessage
        {
            if (scenario == null)
            {
                throw new ArgumentNullException(nameof(scenario));
            }

            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            if (postProcessor == null)
            {
                throw new ArgumentNullException(nameof(postProcessor));
            }

            if (scenario.Kind != MessageKind.Targeted)
            {
                throw new ArgumentException(
                    $"RegisterTargetedPostProcessor requires Kind=Targeted but got {scenario.Kind}.",
                    nameof(scenario)
                );
            }

            return token.RegisterTargetedPostProcessor<TMessage>(
                target,
                postProcessor,
                priority: priority
            );
        }

        /// <summary>
        /// Registers a post-processor for a broadcast message. The scenario's
        /// <see cref="MessageScenario.Kind"/> must be <see cref="MessageKind.Broadcast"/>.
        /// </summary>
        public static MessageRegistrationHandle RegisterBroadcastPostProcessor<TMessage>(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId source,
            MessageHandler.FastHandler<TMessage> postProcessor,
            int priority = 0
        )
            where TMessage : IBroadcastMessage
        {
            if (scenario == null)
            {
                throw new ArgumentNullException(nameof(scenario));
            }

            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            if (postProcessor == null)
            {
                throw new ArgumentNullException(nameof(postProcessor));
            }

            if (scenario.Kind != MessageKind.Broadcast)
            {
                throw new ArgumentException(
                    $"RegisterBroadcastPostProcessor requires Kind=Broadcast but got {scenario.Kind}.",
                    nameof(scenario)
                );
            }

            return token.RegisterBroadcastPostProcessor<TMessage>(
                source,
                postProcessor,
                priority: priority
            );
        }
    }
}
