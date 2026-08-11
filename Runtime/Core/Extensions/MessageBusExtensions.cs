namespace DxMessaging.Core.Extensions
{
    using System;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
#if UNITY_2021_3_OR_NEWER
    using UnityEngine;
#endif

    /// <summary>
    /// <see cref="IMessageBus"/> helpers that mirror the message-centric shorthands in <see cref="MessageExtensions"/>.
    /// </summary>
    public static class MessageBusExtensions
    {
        /// <summary>
        /// Emits an untargeted message instance through the provided message bus.
        /// </summary>
        /// <typeparam name="TMessage">Concrete message type implementing <see cref="IUntargetedMessage"/>.</typeparam>
        /// <param name="messageBus">Bus that should dispatch the message.</param>
        /// <param name="message">Message instance to send.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="messageBus"/> is null.</exception>
        public static void EmitUntargeted<TMessage>(this IMessageBus messageBus, TMessage message)
            where TMessage : class, IUntargetedMessage
        {
            if (messageBus == null)
            {
                throw new ArgumentNullException(nameof(messageBus));
            }

            message.EmitUntargeted(messageBus);
        }

        /// <summary>
        /// Emits an untargeted struct message through the provided message bus without copying the payload.
        /// </summary>
        /// <typeparam name="TMessage">Concrete message type implementing <see cref="IUntargetedMessage"/>.</typeparam>
        /// <param name="messageBus">Bus that should dispatch the message.</param>
        /// <param name="message">Reference to the struct message to send.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="messageBus"/> is null.</exception>
        public static void EmitUntargeted<TMessage>(
            this IMessageBus messageBus,
            ref TMessage message
        )
            where TMessage : struct, IUntargetedMessage
        {
            if (messageBus == null)
            {
                throw new ArgumentNullException(nameof(messageBus));
            }

            message.EmitUntargeted(messageBus);
        }

        /// <summary>
        /// Emits a targeted message to the specified recipient.
        /// </summary>
        /// <typeparam name="TMessage">Concrete message type implementing <see cref="ITargetedMessage"/>.</typeparam>
        /// <param name="messageBus">Bus that should dispatch the message.</param>
        /// <param name="target">Recipient of the message.</param>
        /// <param name="message">Message instance to deliver.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="messageBus"/> is null.</exception>
        /// <example>
        /// <code>
        /// InstanceId playerId = GetPlayerId();
        /// var command = new AdjustHealth(5);
        /// bus.EmitTargeted(playerId, command);
        /// </code>
        /// </example>
        public static void EmitTargeted<TMessage>(
            this IMessageBus messageBus,
            InstanceId target,
            TMessage message
        )
            where TMessage : class, ITargetedMessage
        {
            if (messageBus == null)
            {
                throw new ArgumentNullException(nameof(messageBus));
            }

            message.EmitTargeted(target, messageBus);
        }

        /// <summary>
        /// Emits a targeted struct message to the specified recipient without copying the payload.
        /// </summary>
        /// <typeparam name="TMessage">Concrete message type implementing <see cref="ITargetedMessage"/>.</typeparam>
        /// <param name="messageBus">Bus that should dispatch the message.</param>
        /// <param name="target">Recipient of the message.</param>
        /// <param name="message">Reference to the struct message to deliver.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="messageBus"/> is null.</exception>
        public static void EmitTargeted<TMessage>(
            this IMessageBus messageBus,
            InstanceId target,
            ref TMessage message
        )
            where TMessage : struct, ITargetedMessage
        {
            if (messageBus == null)
            {
                throw new ArgumentNullException(nameof(messageBus));
            }

            message.EmitTargeted(target, messageBus);
        }

        /// <summary>
        /// Emits a broadcast message sourced from the specified instance.
        /// </summary>
        /// <typeparam name="TMessage">Concrete message type implementing <see cref="IBroadcastMessage"/>.</typeparam>
        /// <param name="messageBus">Bus that should dispatch the message.</param>
        /// <param name="source">Originating instance for the broadcast.</param>
        /// <param name="message">Message instance to deliver.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="messageBus"/> is null.</exception>
        public static void EmitBroadcast<TMessage>(
            this IMessageBus messageBus,
            InstanceId source,
            TMessage message
        )
            where TMessage : class, IBroadcastMessage
        {
            if (messageBus == null)
            {
                throw new ArgumentNullException(nameof(messageBus));
            }

            message.EmitBroadcast(source, messageBus);
        }

        /// <summary>
        /// Emits a broadcast struct message sourced from the specified instance without copying the payload.
        /// </summary>
        /// <typeparam name="TMessage">Concrete message type implementing <see cref="IBroadcastMessage"/>.</typeparam>
        /// <param name="messageBus">Bus that should dispatch the message.</param>
        /// <param name="source">Originating instance for the broadcast.</param>
        /// <param name="message">Reference to the struct message to deliver.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="messageBus"/> is null.</exception>
        public static void EmitBroadcast<TMessage>(
            this IMessageBus messageBus,
            InstanceId source,
            ref TMessage message
        )
            where TMessage : struct, IBroadcastMessage
        {
            if (messageBus == null)
            {
                throw new ArgumentNullException(nameof(messageBus));
            }

            message.EmitBroadcast(source, messageBus);
        }

#if UNITY_2021_3_OR_NEWER
        /// <summary>
        /// Emits a targeted message to the specified Unity <see cref="GameObject"/>.
        /// </summary>
        /// <typeparam name="TMessage">Concrete message type implementing <see cref="ITargetedMessage"/>.</typeparam>
        /// <param name="messageBus">Bus that should dispatch the message.</param>
        /// <param name="target">Unity object that will receive the message.</param>
        /// <param name="message">Message instance to deliver.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="messageBus"/> is null.</exception>
        public static void EmitGameObjectTargeted<TMessage>(
            this IMessageBus messageBus,
            GameObject target,
            TMessage message
        )
            where TMessage : class, ITargetedMessage
        {
            if (messageBus == null)
            {
                throw new ArgumentNullException(nameof(messageBus));
            }

            message.EmitGameObjectTargeted(target, messageBus);
        }

        /// <summary>
        /// Emits a targeted struct message to the specified Unity <see cref="GameObject"/> without copying the payload.
        /// </summary>
        /// <typeparam name="TMessage">Concrete message type implementing <see cref="ITargetedMessage"/>.</typeparam>
        /// <param name="messageBus">Bus that should dispatch the message.</param>
        /// <param name="target">Unity object that will receive the message.</param>
        /// <param name="message">Reference to the struct message to deliver.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="messageBus"/> is null.</exception>
        public static void EmitGameObjectTargeted<TMessage>(
            this IMessageBus messageBus,
            GameObject target,
            ref TMessage message
        )
            where TMessage : struct, ITargetedMessage
        {
            if (messageBus == null)
            {
                throw new ArgumentNullException(nameof(messageBus));
            }

            message.EmitGameObjectTargeted(target, messageBus);
        }

        /// <summary>
        /// Emits a targeted message to the specified Unity <see cref="Component"/>.
        /// </summary>
        /// <typeparam name="TMessage">Concrete message type implementing <see cref="ITargetedMessage"/>.</typeparam>
        /// <param name="messageBus">Bus that should dispatch the message.</param>
        /// <param name="target">Unity component that will receive the message.</param>
        /// <param name="message">Message instance to deliver.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="messageBus"/> is null.</exception>
        public static void EmitComponentTargeted<TMessage>(
            this IMessageBus messageBus,
            Component target,
            TMessage message
        )
            where TMessage : class, ITargetedMessage
        {
            if (messageBus == null)
            {
                throw new ArgumentNullException(nameof(messageBus));
            }

            message.EmitComponentTargeted(target, messageBus);
        }

        /// <summary>
        /// Emits a targeted struct message to the specified Unity <see cref="Component"/> without copying the payload.
        /// </summary>
        /// <typeparam name="TMessage">Concrete message type implementing <see cref="ITargetedMessage"/>.</typeparam>
        /// <param name="messageBus">Bus that should dispatch the message.</param>
        /// <param name="target">Unity component that will receive the message.</param>
        /// <param name="message">Reference to the struct message to deliver.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="messageBus"/> is null.</exception>
        public static void EmitComponentTargeted<TMessage>(
            this IMessageBus messageBus,
            Component target,
            ref TMessage message
        )
            where TMessage : struct, ITargetedMessage
        {
            if (messageBus == null)
            {
                throw new ArgumentNullException(nameof(messageBus));
            }

            message.EmitComponentTargeted(target, messageBus);
        }

        /// <summary>
        /// Emits a broadcast message from the specified Unity <see cref="GameObject"/>.
        /// </summary>
        /// <typeparam name="TMessage">Concrete message type implementing <see cref="IBroadcastMessage"/>.</typeparam>
        /// <param name="messageBus">Bus that should dispatch the message.</param>
        /// <param name="source">Unity object that is broadcasting the message.</param>
        /// <param name="message">Message instance to deliver.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="messageBus"/> is null.</exception>
        public static void EmitGameObjectBroadcast<TMessage>(
            this IMessageBus messageBus,
            GameObject source,
            TMessage message
        )
            where TMessage : class, IBroadcastMessage
        {
            if (messageBus == null)
            {
                throw new ArgumentNullException(nameof(messageBus));
            }

            message.EmitGameObjectBroadcast(source, messageBus);
        }

        /// <summary>
        /// Emits a broadcast struct message from the specified Unity <see cref="GameObject"/> without copying the payload.
        /// </summary>
        /// <typeparam name="TMessage">Concrete message type implementing <see cref="IBroadcastMessage"/>.</typeparam>
        /// <param name="messageBus">Bus that should dispatch the message.</param>
        /// <param name="source">Unity object that is broadcasting the message.</param>
        /// <param name="message">Reference to the struct message to deliver.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="messageBus"/> is null.</exception>
        public static void EmitGameObjectBroadcast<TMessage>(
            this IMessageBus messageBus,
            GameObject source,
            ref TMessage message
        )
            where TMessage : struct, IBroadcastMessage
        {
            if (messageBus == null)
            {
                throw new ArgumentNullException(nameof(messageBus));
            }

            message.EmitGameObjectBroadcast(source, messageBus);
        }

        /// <summary>
        /// Emits a broadcast message from the specified Unity <see cref="Component"/>.
        /// </summary>
        /// <typeparam name="TMessage">Concrete message type implementing <see cref="IBroadcastMessage"/>.</typeparam>
        /// <param name="messageBus">Bus that should dispatch the message.</param>
        /// <param name="source">Unity component that is broadcasting the message.</param>
        /// <param name="message">Message instance to deliver.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="messageBus"/> is null.</exception>
        public static void EmitComponentBroadcast<TMessage>(
            this IMessageBus messageBus,
            Component source,
            TMessage message
        )
            where TMessage : class, IBroadcastMessage
        {
            if (messageBus == null)
            {
                throw new ArgumentNullException(nameof(messageBus));
            }

            message.EmitComponentBroadcast(source, messageBus);
        }

        /// <summary>
        /// Emits a broadcast struct message from the specified Unity <see cref="Component"/> without copying the payload.
        /// </summary>
        /// <typeparam name="TMessage">Concrete message type implementing <see cref="IBroadcastMessage"/>.</typeparam>
        /// <param name="messageBus">Bus that should dispatch the message.</param>
        /// <param name="source">Unity component that is broadcasting the message.</param>
        /// <param name="message">Reference to the struct message to deliver.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="messageBus"/> is null.</exception>
        public static void EmitComponentBroadcast<TMessage>(
            this IMessageBus messageBus,
            Component source,
            ref TMessage message
        )
            where TMessage : struct, IBroadcastMessage
        {
            if (messageBus == null)
            {
                throw new ArgumentNullException(nameof(messageBus));
            }

            message.EmitComponentBroadcast(source, messageBus);
        }
#endif

        /// <summary>
        /// Emits a string payload using the string-message utility channel.
        /// </summary>
        /// <param name="messageBus">Bus that should dispatch the message.</param>
        /// <param name="payload">Message text to broadcast globally.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="messageBus"/> is null.</exception>
        /// <example>
        /// <code>
        /// bus.Emit("PlayerJoined");
        /// </code>
        /// </example>
        public static void Emit(this IMessageBus messageBus, string payload)
        {
            if (messageBus == null)
            {
                throw new ArgumentNullException(nameof(messageBus));
            }

            payload.Emit(messageBus);
        }

        /// <summary>
        /// Emits a string payload targeted at the specified instance.
        /// </summary>
        /// <param name="messageBus">Bus that should dispatch the message.</param>
        /// <param name="target">Intended recipient of the payload.</param>
        /// <param name="payload">Message text to deliver.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="messageBus"/> is null.</exception>
        public static void EmitAt(this IMessageBus messageBus, InstanceId target, string payload)
        {
            if (messageBus == null)
            {
                throw new ArgumentNullException(nameof(messageBus));
            }

            payload.Emit(target, messageBus);
        }

        /// <summary>
        /// Emits a string payload broadcast from the specified instance.
        /// </summary>
        /// <param name="messageBus">Bus that should dispatch the message.</param>
        /// <param name="source">Origin of the payload.</param>
        /// <param name="payload">Message text to deliver.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="messageBus"/> is null.</exception>
        public static void EmitFrom(this IMessageBus messageBus, InstanceId source, string payload)
        {
            if (messageBus == null)
            {
                throw new ArgumentNullException(nameof(messageBus));
            }

            payload.EmitFrom(source, messageBus);
        }
    }
}
