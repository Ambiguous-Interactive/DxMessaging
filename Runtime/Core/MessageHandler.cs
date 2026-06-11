namespace DxMessaging.Core
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Runtime.CompilerServices;
    using DxMessaging.Core.Internal;
    using Helper;
    using MessageBus;
    using Messages;
    using Pooling;

    /// <summary>
    /// Per-owner handler that executes registered message callbacks.
    /// </summary>
    /// <remarks>
    /// A <see cref="MessageHandler"/> is typically created and managed by <see cref="Unity.MessagingComponent"/> in Unity.
    /// Most user code interacts with the handler through <see cref="MessageRegistrationToken"/>, which stages
    /// registrations and ensures correct enable/disable lifecycles.
    /// </remarks>
    /// <example>
    /// <code>
    /// // Plain .NET usage without Unity
    /// var owner = new DxMessaging.Core.InstanceId(1);
    /// var handler = new DxMessaging.Core.MessageHandler(owner) { active = true };
    /// var token = DxMessaging.Core.MessageRegistrationToken.Create(handler);
    /// _ = token.RegisterUntargeted&lt;WorldRegenerated&gt;((ref WorldRegenerated m) =&gt; Console.WriteLine(m.seed));
    /// token.Enable();
    ///
    /// var bus = DxMessaging.Core.MessageHandler.MessageBus;
    /// var msg = new WorldRegenerated(42);
    /// bus.UntargetedBroadcast(ref msg);
    /// </code>
    /// </example>
    public sealed class MessageHandler
        : IEquatable<MessageHandler>,
            IComparable,
            IComparable<MessageHandler>
    {
        private static void PrefreezePriorityCache<TMessage, THandler>(
            TypedHandler<TMessage> handler,
            int slotIndex,
            int priority,
            long emissionId
        )
            where TMessage : IMessage
        {
            Dictionary<int, IHandlerActionCache> byPriority = handler.GetPriorityHandlers(
                slotIndex
            );
            if (
                byPriority != null
                && byPriority.TryGetValue(priority, out IHandlerActionCache erasedCache)
                && erasedCache is HandlerActionCache<THandler> cache
            )
            {
                _ = TypedHandler<TMessage>.GetOrAddNewHandlerStack(cache, emissionId);
                cache.prefreezeInvocationCount++;
            }
        }

        private static void PrefreezeContextCache<TMessage, THandler>(
            TypedHandler<TMessage> handler,
            int slotIndex,
            InstanceId context,
            int priority,
            long emissionId
        )
            where TMessage : IMessage
        {
            Dictionary<InstanceId, Dictionary<int, IHandlerActionCache>> byContext =
                handler.GetContextHandlers(slotIndex);
            if (
                byContext != null
                && byContext.TryGetValue(
                    context,
                    out Dictionary<int, IHandlerActionCache> byPriority
                )
                && byPriority.TryGetValue(priority, out IHandlerActionCache erasedCache)
                && erasedCache is HandlerActionCache<THandler> cache
            )
            {
                _ = TypedHandler<TMessage>.GetOrAddNewHandlerStack(cache, emissionId);
                cache.prefreezeInvocationCount++;
            }
        }

        private static int GetPriorityPrefreezeInvocationCount<TMessage, THandler>(
            TypedHandler<TMessage> handler,
            int slotIndex,
            int priority
        )
            where TMessage : IMessage
        {
            Dictionary<int, IHandlerActionCache> byPriority = handler.GetPriorityHandlers(
                slotIndex
            );
            if (
                byPriority != null
                && byPriority.TryGetValue(priority, out IHandlerActionCache erasedCache)
                && erasedCache is HandlerActionCache<THandler> cache
            )
            {
                return cache.prefreezeInvocationCount;
            }

            return 0;
        }

        /// <summary>
        /// Pre-freezes this handler's broadcast post-processor caches for the given message type, source, and priority
        /// for the specified emission id, so registrations during the same emission are not observed.
        /// </summary>
        /// <typeparam name="T">Broadcast message type.</typeparam>
        /// <param name="source">Source instance id.</param>
        /// <param name="priority">Priority bucket to freeze.</param>
        /// <param name="emissionId">Current emission id.</param>
        /// <param name="messageBus">Bus whose typed handler mapping to use.</param>
        internal void PrefreezeBroadcastPostProcessorsForEmission<T>(
            InstanceId source,
            int priority,
            long emissionId,
            IMessageBus messageBus
        )
            where T : IBroadcastMessage
        {
            if (!GetHandlerForType(messageBus, out TypedHandler<T> handler))
            {
                return;
            }

            PrefreezeContextCache<T, FastHandler<T>>(
                handler,
                TypedSlotIndex.BroadcastPostProcessFast,
                source,
                priority,
                emissionId
            );
            PrefreezeContextCache<T, Action<T>>(
                handler,
                TypedSlotIndex.BroadcastPostProcessDefault,
                source,
                priority,
                emissionId
            );
        }

        /// <summary>
        /// Pre-freezes this handler's targeted post-processor caches for the given message type, target, and priority
        /// for the specified emission id, so registrations during the same emission are not observed.
        /// </summary>
        /// <typeparam name="T">Targeted message type.</typeparam>
        /// <param name="target">Target instance id.</param>
        /// <param name="priority">Priority bucket to freeze.</param>
        /// <param name="emissionId">Current emission id.</param>
        /// <param name="messageBus">Bus whose typed handler mapping to use.</param>
        internal void PrefreezeTargetedPostProcessorsForEmission<T>(
            InstanceId target,
            int priority,
            long emissionId,
            IMessageBus messageBus
        )
            where T : ITargetedMessage
        {
            if (!GetHandlerForType(messageBus, out TypedHandler<T> handler))
            {
                return;
            }

            PrefreezeContextCache<T, FastHandler<T>>(
                handler,
                TypedSlotIndex.TargetedPostProcessFast,
                target,
                priority,
                emissionId
            );
            PrefreezeContextCache<T, Action<T>>(
                handler,
                TypedSlotIndex.TargetedPostProcessDefault,
                target,
                priority,
                emissionId
            );
        }

        /// <summary>
        /// Pre-freezes this handler's targeted-without-targeting handler caches for the given message type and priority
        /// so that removals/additions during the same emission are not observed.
        /// </summary>
        internal void PrefreezeTargetedWithoutTargetingHandlersForEmission<T>(
            int priority,
            long emissionId,
            IMessageBus messageBus
        )
            where T : ITargetedMessage
        {
            if (!GetHandlerForType(messageBus, out TypedHandler<T> handler))
            {
                return;
            }

            PrefreezePriorityCache<T, FastHandlerWithContext<T>>(
                handler,
                TypedSlotIndex.TargetedHandleWithoutContextFast,
                priority,
                emissionId
            );
            PrefreezePriorityCache<T, Action<InstanceId, T>>(
                handler,
                TypedSlotIndex.TargetedHandleWithoutContext,
                priority,
                emissionId
            );
        }

        /// <summary>
        /// Pre-freezes this handler's targeted-without-targeting post-processor caches for a given priority.
        /// </summary>
        internal void PrefreezeTargetedWithoutTargetingPostProcessorsForEmission<T>(
            int priority,
            long emissionId,
            IMessageBus messageBus
        )
            where T : ITargetedMessage
        {
            if (!GetHandlerForType(messageBus, out TypedHandler<T> handler))
            {
                return;
            }

            PrefreezePriorityCache<T, FastHandlerWithContext<T>>(
                handler,
                TypedSlotIndex.TargetedPostProcessWithoutContextFast,
                priority,
                emissionId
            );
            PrefreezePriorityCache<T, Action<InstanceId, T>>(
                handler,
                TypedSlotIndex.TargetedPostProcessWithoutContext,
                priority,
                emissionId
            );
        }

        /// <summary>
        /// Pre-freezes this handler's untargeted post-processor caches for a given priority.
        /// </summary>
        internal void PrefreezeUntargetedPostProcessorsForEmission<T>(
            int priority,
            long emissionId,
            IMessageBus messageBus
        )
            where T : IUntargetedMessage
        {
            if (!GetHandlerForType(messageBus, out TypedHandler<T> handler))
            {
                return;
            }

            PrefreezePriorityCache<T, FastHandler<T>>(
                handler,
                TypedSlotIndex.UntargetedPostProcessFast,
                priority,
                emissionId
            );
            PrefreezePriorityCache<T, Action<T>>(
                handler,
                TypedSlotIndex.UntargetedPostProcessDefault,
                priority,
                emissionId
            );
        }

        /// <summary>
        /// Pre-freezes this handler's broadcast-without-source post-processor caches for a given priority.
        /// </summary>
        internal void PrefreezeBroadcastWithoutSourcePostProcessorsForEmission<T>(
            int priority,
            long emissionId,
            IMessageBus messageBus
        )
            where T : IBroadcastMessage
        {
            if (!GetHandlerForType(messageBus, out TypedHandler<T> handler))
            {
                return;
            }

            PrefreezePriorityCache<T, FastHandlerWithContext<T>>(
                handler,
                TypedSlotIndex.BroadcastPostProcessWithoutContextFast,
                priority,
                emissionId
            );
            PrefreezePriorityCache<T, Action<InstanceId, T>>(
                handler,
                TypedSlotIndex.BroadcastPostProcessWithoutContext,
                priority,
                emissionId
            );
        }

        /// <summary>
        /// Pre-freezes this handler's broadcast-without-source handler caches for the given message type and priority
        /// for the specified emission id, so removals during the same emission are not observed.
        /// </summary>
        /// <typeparam name="T">Broadcast message type.</typeparam>
        /// <param name="priority">Priority bucket to freeze.</param>
        /// <param name="emissionId">Current emission id.</param>
        /// <param name="messageBus">Bus whose typed handler mapping to use.</param>
        internal void PrefreezeBroadcastWithoutSourceHandlersForEmission<T>(
            int priority,
            long emissionId,
            IMessageBus messageBus
        )
            where T : IBroadcastMessage
        {
            if (!GetHandlerForType(messageBus, out TypedHandler<T> handler))
            {
                return;
            }

            PrefreezePriorityCache<T, FastHandlerWithContext<T>>(
                handler,
                TypedSlotIndex.BroadcastHandleWithoutContextFast,
                priority,
                emissionId
            );
            PrefreezePriorityCache<T, Action<InstanceId, T>>(
                handler,
                TypedSlotIndex.BroadcastHandleWithoutContext,
                priority,
                emissionId
            );
        }

        /// <summary>
        /// Pre-freezes this handler's untargeted handler caches for the given message type and priority
        /// for the specified emission id, so removals during the same emission are not observed.
        /// </summary>
        /// <typeparam name="T">Untargeted message type.</typeparam>
        /// <param name="priority">Priority bucket to freeze.</param>
        /// <param name="emissionId">Current emission id.</param>
        /// <param name="messageBus">Bus whose typed handler mapping to use.</param>
        internal void PrefreezeUntargetedHandlersForEmission<T>(
            int priority,
            long emissionId,
            IMessageBus messageBus
        )
            where T : IUntargetedMessage
        {
            if (!GetHandlerForType(messageBus, out TypedHandler<T> handler))
            {
                return;
            }

            PrefreezePriorityCache<T, FastHandler<T>>(
                handler,
                TypedSlotIndex.UntargetedHandleFast,
                priority,
                emissionId
            );
            PrefreezePriorityCache<T, Action<T>>(
                handler,
                TypedSlotIndex.UntargetedHandleDefault,
                priority,
                emissionId
            );
        }

        /// <summary>
        /// Pre-freezes this handler's targeted handler caches for the given message type, target, and priority
        /// for the specified emission id, so removals during the same emission are not observed.
        /// </summary>
        /// <typeparam name="T">Targeted message type.</typeparam>
        /// <param name="target">Target instance id.</param>
        /// <param name="priority">Priority bucket to freeze.</param>
        /// <param name="emissionId">Current emission id.</param>
        /// <param name="messageBus">Bus whose typed handler mapping to use.</param>
        internal void PrefreezeTargetedHandlersForEmission<T>(
            InstanceId target,
            int priority,
            long emissionId,
            IMessageBus messageBus
        )
            where T : ITargetedMessage
        {
            if (!GetHandlerForType(messageBus, out TypedHandler<T> handler))
            {
                return;
            }

            PrefreezeContextCache<T, FastHandler<T>>(
                handler,
                TypedSlotIndex.TargetedHandleFast,
                target,
                priority,
                emissionId
            );
            PrefreezeContextCache<T, Action<T>>(
                handler,
                TypedSlotIndex.TargetedHandleDefault,
                target,
                priority,
                emissionId
            );
        }

        /// <summary>
        /// Pre-freezes this handler's broadcast handler caches for the given message type, source, and priority
        /// for the specified emission id, so removals during the same emission are not observed.
        /// </summary>
        /// <typeparam name="T">Broadcast message type.</typeparam>
        /// <param name="source">Source instance id.</param>
        /// <param name="priority">Priority bucket to freeze.</param>
        /// <param name="emissionId">Current emission id.</param>
        /// <param name="messageBus">Bus whose typed handler mapping to use.</param>
        internal void PrefreezeBroadcastHandlersForEmission<T>(
            InstanceId source,
            int priority,
            long emissionId,
            IMessageBus messageBus
        )
            where T : IBroadcastMessage
        {
            if (!GetHandlerForType(messageBus, out TypedHandler<T> handler))
            {
                return;
            }

            PrefreezeContextCache<T, FastHandler<T>>(
                handler,
                TypedSlotIndex.BroadcastHandleFast,
                source,
                priority,
                emissionId
            );
            PrefreezeContextCache<T, Action<T>>(
                handler,
                TypedSlotIndex.BroadcastHandleDefault,
                source,
                priority,
                emissionId
            );
        }

        /// <summary>
        /// High-performance handler that receives the message by reference (no boxing/copies).
        /// </summary>
        public delegate void FastHandler<TMessage>(ref TMessage message)
            where TMessage : IMessage;

        /// <summary>
        /// High-performance handler with an additional context value (e.g., target/source) by reference.
        /// </summary>
        public delegate void FastHandlerWithContext<TMessage>(
            ref InstanceId context,
            ref TMessage message
        )
            where TMessage : IMessage;

        private static readonly object GlobalResetLock = new object();

        /// <summary>
        /// Global message bus used when no explicit bus is provided.
        /// </summary>
        private static IMessageBus _globalMessageBus;

        private static MessageBus.MessageBus _defaultGlobalMessageBus = new MessageBus.MessageBus();

        /// <summary>
        /// Gets the process-wide <see cref="IMessageBus"/> used when no explicit bus is supplied.
        /// </summary>
        /// <remarks>
        /// This mirrors the legacy singleton so existing code continues to function. Use
        /// <see cref="SetGlobalMessageBus(Core.MessageBus.MessageBus)"/> to replace the instance (for example from a DI container) and
        /// <see cref="ResetGlobalMessageBus"/> to restore the stock configuration afterwards.
        /// </remarks>
        public static IMessageBus MessageBus => _globalMessageBus;

        /// <summary>
        /// Gets the baseline global <see cref="IMessageBus"/> instance used when no custom bus is configured.
        /// </summary>
        /// <remarks>
        /// The instance is recreated when <see cref="DxMessagingStaticState.Reset"/> runs so that domain-reload-disabled
        /// environments can obtain a clean slate.
        /// </remarks>
        public static IMessageBus InitialGlobalMessageBus => _defaultGlobalMessageBus;

        static MessageHandler()
        {
            ResetStatics();
        }

        /// <summary>
        /// Reclaims empty slots and pooled collections owned by the current global message bus.
        /// </summary>
        /// <param name="force">
        /// When true, ignores idle-age thresholds and drains shared pools to zero.
        /// When false, only slots past the configured idle threshold are eligible.
        /// </param>
        /// <returns>Counts describing what was reclaimed.</returns>
        public static IMessageBus.TrimResult TrimAll(bool force = false)
        {
            return MessageBus.Trim(force);
        }

        /// <summary>
        /// Replaces the global <see cref="Core.MessageBus.MessageBus"/> instance returned by <see cref="MessageBus"/>.
        /// </summary>
        /// <param name="messageBus">Instance to expose globally.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="messageBus"/> is <see langword="null"/>.
        /// </exception>
        /// <remarks>
        /// This is primarily intended for integration tests or dependency injection bootstrap code. Invoke
        /// <see cref="ResetGlobalMessageBus"/> when the customisation is no longer required.
        /// </remarks>
        public static void SetGlobalMessageBus(MessageBus.MessageBus messageBus)
        {
            if (messageBus == null)
            {
                throw new ArgumentNullException(nameof(messageBus));
            }

            _globalMessageBus = messageBus;
        }

        /// <summary>
        /// Replaces the global message bus with an arbitrary <see cref="IMessageBus"/> implementation.
        /// </summary>
        /// <param name="messageBus">Instance to expose globally.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="messageBus"/> is <see langword="null"/>.
        /// </exception>
        public static void SetGlobalMessageBus(IMessageBus messageBus)
        {
            if (messageBus == null)
            {
                throw new ArgumentNullException(nameof(messageBus));
            }

            _globalMessageBus = messageBus;
        }

        /// <summary>
        /// Restores the global <see cref="Core.MessageBus.MessageBus"/> to the built-in default instance.
        /// </summary>
        /// <remarks>
        /// The default instance is recreated by <see cref="ResetStatics"/> when the static state reset utility runs.
        /// </remarks>
        public static void ResetGlobalMessageBus()
        {
            lock (GlobalResetLock)
            {
                _globalMessageBus = _defaultGlobalMessageBus;
            }
        }

        /// <summary>
        /// Temporarily overrides the global message bus until the returned scope is disposed.
        /// </summary>
        /// <param name="messageBus">Message bus to expose for the duration of the scope.</param>
        /// <returns>An <see cref="IDisposable"/> scope that restores the previous bus on dispose.</returns>
        public static GlobalMessageBusScope OverrideGlobalMessageBus(IMessageBus messageBus)
        {
            return new GlobalMessageBusScope(messageBus);
        }

        /// <summary>
        /// Recreates the built-in global <see cref="Core.MessageBus.MessageBus"/> and assigns it as the active global bus.
        /// </summary>
        /// <remarks>
        /// Invoked by <see cref="DxMessagingStaticState.Reset"/> to provide a clean slate when domain reloads are disabled.
        /// </remarks>
        internal static void ResetStatics()
        {
            lock (GlobalResetLock)
            {
                _defaultGlobalMessageBus.ResetState();
                _globalMessageBus = _defaultGlobalMessageBus;
            }
        }

        /// <summary>
        /// Represents a disposable override scope for the global message bus.
        /// </summary>
        public struct GlobalMessageBusScope : IDisposable
        {
            private readonly IMessageBus _previous;
            private bool _disposed;

            internal GlobalMessageBusScope(IMessageBus messageBus)
            {
                if (messageBus == null)
                {
                    throw new ArgumentNullException(nameof(messageBus));
                }

                _previous = MessageBus;
                _disposed = false;

                if (messageBus is MessageBus.MessageBus concrete)
                {
                    SetGlobalMessageBus(concrete);
                }
                else
                {
                    SetGlobalMessageBus(messageBus);
                }
            }

            /// <summary>
            /// Restores the previously active global message bus when the scope ends.
            /// </summary>
            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                if (_previous is MessageBus.MessageBus concrete)
                {
                    SetGlobalMessageBus(concrete);
                }
                else if (_previous != null)
                {
                    SetGlobalMessageBus(_previous);
                }
                else
                {
                    ResetGlobalMessageBus();
                }

                _disposed = true;
            }
        }

        /// <summary>
        /// Whether this MessageHandler will process messages.
        /// </summary>
        public bool active;

        /// <summary>
        /// The Id of the GameObject that owns us.
        /// </summary>
        public readonly InstanceId owner;

        /// <summary>
        /// Maps Types to the corresponding Handler of that type.
        /// </summary>
        /// <note>
        /// Ideally, this would be something like a Dictionary[T, Handler[T]], but that can't be done with C#s type system.
        /// </note>
        internal readonly List<MessageCache<object>> _handlersByTypeByMessageBus;
        private IMessageBus _defaultMessageBus;

        /// <summary>
        /// Gets the <see cref="IMessageBus"/> that will be used when a registration does not specify one explicitly.
        /// </summary>
        /// <remarks>
        /// When no override has been provided via <see cref="SetDefaultMessageBus"/>, this value defers to the global
        /// <see cref="MessageBus"/> singleton.
        /// </remarks>
        public IMessageBus DefaultMessageBus => _defaultMessageBus ?? MessageBus;

        /// <summary>
        /// Initializes a message handler bound to the specified owner and optional default bus.
        /// </summary>
        /// <param name="owner">Identity of the object that owns this handler.</param>
        /// <param name="defaultMessageBus">
        /// Preferred bus to use when registrations do not specify one. Falls back to
        /// <see cref="MessageBus"/> if omitted.
        /// </param>
        public MessageHandler(InstanceId owner, IMessageBus defaultMessageBus = null)
        {
            this.owner = owner;
            _handlersByTypeByMessageBus = new List<MessageCache<object>>();
            _defaultMessageBus = defaultMessageBus;
        }

        /// <summary>
        /// Assigns an <see cref="IMessageBus"/> for registrations that omit an explicit bus parameter.
        /// </summary>
        /// <param name="messageBus">
        /// Bus to use; pass <see langword="null"/> to revert to the global <see cref="MessageBus"/> singleton.
        /// </param>
        /// <remarks>
        /// This allows a handler to participate in dependency injection scenarios without forcing every caller to supply
        /// a bus manually.
        /// </remarks>
        public void SetDefaultMessageBus(IMessageBus messageBus)
        {
            _defaultMessageBus = messageBus;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private IMessageBus ResolveMessageBus(IMessageBus messageBus)
        {
            return messageBus ?? _defaultMessageBus ?? MessageBus;
        }

        /// <summary>
        /// Callback from the MessageBus for handling UntargetedMessages - user code should generally never use this.
        /// </summary>
        /// <note>
        /// In this case, "UntargetedMessage" refers to Targeted without targeting, and UntargetedMessages, hence T : IMessage.
        /// </note>
        /// <param name="message">Message to handle.</param>
        /// <param name="messageBus">The specific MessageBus to use.</param>
        /// <param name="priority">Priority at which to run the handlers.</param>
        public void HandleUntargetedMessage<TMessage>(
            ref TMessage message,
            IMessageBus messageBus,
            int priority
        )
            where TMessage : IMessage
        {
            if (!active)
            {
                return;
            }

            if (GetHandlerForType(messageBus, out TypedHandler<TMessage> handler))
            {
                long emissionId = messageBus.EmissionId;
                handler.HandleUntargeted(ref message, priority, emissionId);
            }
        }

        /// <summary>
        /// Callback from the MessageBus for handling UntargetedMessages - user code should generally never use this.
        /// </summary>
        /// <note>
        /// In this case, "UntargetedMessage" refers to Targeted without targeting, and UntargetedMessages, hence T : IUntargetedMessage.
        /// </note>
        /// <param name="message">Message to handle.</param>
        /// <param name="messageBus">The specific MessageBus to use.</param>
        /// <param name="priority">Priority at which to run the handlers.</param>
        public void HandleUntargetedPostProcessing<TMessage>(
            ref TMessage message,
            IMessageBus messageBus,
            int priority
        )
            where TMessage : IUntargetedMessage
        {
            if (!active)
            {
                return;
            }

            if (GetHandlerForType(messageBus, out TypedHandler<TMessage> handler))
            {
                long emissionId = messageBus.EmissionId;
                handler.HandleUntargetedPostProcessing(ref message, priority, emissionId);
            }
        }

        /// <summary>
        /// Callback from the MessageBus for handling TargetedMessages when this MessageHandler has subscribed - user code should generally never use this.
        /// </summary>
        /// <note>
        /// TargetedMessage refers to those that are intended for the GameObject that owns this MessageHandler.
        /// </note>
        /// <param name="target">Target Id the message is for.</param>
        /// <param name="message">Message to handle.</param>
        /// <param name="messageBus">The specific MessageBus to use.</param>
        /// <param name="priority">Priority at which to run the handlers.</param>
        public void HandleTargeted<TMessage>(
            ref InstanceId target,
            ref TMessage message,
            IMessageBus messageBus,
            int priority
        )
            where TMessage : ITargetedMessage
        {
            if (!active)
            {
                return;
            }

            if (GetHandlerForType(messageBus, out TypedHandler<TMessage> handler))
            {
                long emissionId = messageBus.EmissionId;
                handler.HandleTargeted(ref target, ref message, priority, emissionId);
            }
        }

        /// <summary>
        /// Callback from the MessageBus for handling TargetedMessages without targeting when this MessageHandler has subscribed - user code should generally never use this.
        /// </summary>
        /// <note>
        /// Any TargetedMessage.
        /// </note>
        /// <param name="target">Target Id the message is for.</param>
        /// <param name="message">Message to handle.</param>
        /// <param name="messageBus">The specific MessageBus to use.</param>
        /// <param name="priority">Priority at which to run the handlers.</param>
        public void HandleTargetedWithoutTargeting<TMessage>(
            ref InstanceId target,
            ref TMessage message,
            IMessageBus messageBus,
            int priority
        )
            where TMessage : ITargetedMessage
        {
            if (!active)
            {
                return;
            }

            if (GetHandlerForType(messageBus, out TypedHandler<TMessage> handler))
            {
                long emissionId = messageBus.EmissionId;
                handler.HandleTargetedWithoutTargeting(
                    ref target,
                    ref message,
                    priority,
                    emissionId
                );
            }
        }

        /// <summary>
        /// Callback from the MessageBus for post-processing TargetedMessages when this MessageHandler has subscribed - user code should generally never use this.
        /// </summary>
        /// <note>
        /// TargetedMessage refers to those that are intended for the GameObject that owns this MessageHandler.
        /// </note>
        /// <param name="target">Target Id the message is for.</param>
        /// <param name="message">Message to handle.</param>
        /// <param name="messageBus">The specific MessageBus to use.</param>
        /// <param name="priority">Priority at which to run the handlers.</param>
        public void HandleTargetedPostProcessing<TMessage>(
            ref InstanceId target,
            ref TMessage message,
            IMessageBus messageBus,
            int priority
        )
            where TMessage : ITargetedMessage
        {
            if (!active)
            {
                return;
            }

            if (GetHandlerForType(messageBus, out TypedHandler<TMessage> handler))
            {
                long emissionId = messageBus.EmissionId;
                handler.HandleTargetedPostProcessing(ref target, ref message, priority, emissionId);
            }
        }

        /// <summary>
        /// Callback from the MessageBus for post-processing TargetedMessages when this MessageHandler has subscribed - user code should generally never use this.
        /// </summary>
        /// <note>
        /// TargetedMessage refers to those that are intended for the GameObject that owns this MessageHandler.
        /// </note>
        /// <param name="target">Target Id the message is for.</param>
        /// <param name="message">Message to handle.</param>
        /// <param name="messageBus">The specific MessageBus to use.</param>
        /// <param name="priority">Priority at which to run the handlers.</param>
        public void HandleTargetedWithoutTargetingPostProcessing<TMessage>(
            ref InstanceId target,
            ref TMessage message,
            IMessageBus messageBus,
            int priority
        )
            where TMessage : ITargetedMessage
        {
            if (!active)
            {
                return;
            }

            if (GetHandlerForType(messageBus, out TypedHandler<TMessage> handler))
            {
                long emissionId = messageBus.EmissionId;
                handler.HandleTargetedWithoutTargetingPostProcessing(
                    ref target,
                    ref message,
                    priority,
                    emissionId
                );
            }
        }

        /// <summary>
        /// Callback from the MessageBus for handling SourcedBroadcastMessages - user code should generally never use this.
        /// </summary>
        /// <note>
        /// SourcedBroadcastMessages generally refer to those that are sourced from the GameObject that owns this MessageHandler.
        /// </note>
        /// <param name="source">Source Id the broadcast message is from.</param>
        /// <param name="message">Message to handle</param>
        /// <param name="messageBus">The specific MessageBus to use.</param>
        /// <param name="priority">Priority at which to run the handlers.</param>
        public void HandleSourcedBroadcast<TMessage>(
            ref InstanceId source,
            ref TMessage message,
            IMessageBus messageBus,
            int priority
        )
            where TMessage : IBroadcastMessage
        {
            if (!active)
            {
                return;
            }

            if (GetHandlerForType(messageBus, out TypedHandler<TMessage> handler))
            {
                long emissionId = messageBus.EmissionId;
                handler.HandleSourcedBroadcast(ref source, ref message, priority, emissionId);
            }
        }

        /// <summary>
        /// Callback from the MessageBus for handling SourcedBroadcastMessages without source - user code should generally never use this.
        /// </summary>
        /// <note>
        /// Any SourcedBroadcastMessages.
        /// </note>
        /// <param name="source">Source Id the broadcast message is from.</param>
        /// <param name="message">Message to handle</param>
        /// <param name="messageBus">The specific MessageBus to use.</param>
        /// <param name="priority">Priority at which to run the handlers.</param>
        public void HandleSourcedBroadcastWithoutSource<TMessage>(
            ref InstanceId source,
            ref TMessage message,
            IMessageBus messageBus,
            int priority
        )
            where TMessage : IBroadcastMessage
        {
            if (!active)
            {
                return;
            }

            if (GetHandlerForType(messageBus, out TypedHandler<TMessage> handler))
            {
                long emissionId = messageBus.EmissionId;
                handler.HandleSourcedBroadcastWithoutSource(
                    ref source,
                    ref message,
                    priority,
                    emissionId
                );
            }
        }

        /// <summary>
        /// Callback from the MessageBus for handling SourcedBroadcastPostProcessing - user code should generally never use this.
        /// </summary>
        /// <note>
        /// SourcedBroadcastMessages generally refer to those that are sourced from the GameObject that owns this MessageHandler.
        /// </note>
        /// <param name="source">Source Id the broadcast message is from.</param>
        /// <param name="message">Message to handle</param>
        /// <param name="messageBus">The specific MessageBus to use.</param>
        /// <param name="priority">Priority at which to run the handlers.</param>
        public void HandleSourcedBroadcastPostProcessing<TMessage>(
            ref InstanceId source,
            ref TMessage message,
            IMessageBus messageBus,
            int priority
        )
            where TMessage : IBroadcastMessage
        {
            if (!active)
            {
                return;
            }

            if (GetHandlerForType(messageBus, out TypedHandler<TMessage> handler))
            {
                long emissionId = messageBus.EmissionId;
                handler.HandleSourcedBroadcastPostProcessing(
                    ref source,
                    ref message,
                    priority,
                    emissionId
                );
            }
        }

        /// <summary>
        /// Callback from the MessageBus for handling SourcedBroadcastPostProcessing - user code should generally never use this.
        /// </summary>
        /// <note>
        /// SourcedBroadcastMessages generally refer to those that are sourced from the GameObject that owns this MessageHandler.
        /// </note>
        /// <param name="source">Source Id the broadcast message is from.</param>
        /// <param name="message">Message to handle</param>
        /// <param name="messageBus">The specific MessageBus to use.</param>
        /// <param name="priority">Priority at which to run the handlers.</param>
        public void HandleSourcedBroadcastWithoutSourcePostProcessing<TMessage>(
            ref InstanceId source,
            ref TMessage message,
            IMessageBus messageBus,
            int priority
        )
            where TMessage : IBroadcastMessage
        {
            if (!active)
            {
                return;
            }

            if (GetHandlerForType(messageBus, out TypedHandler<TMessage> handler))
            {
                long emissionId = messageBus.EmissionId;
                handler.HandleBroadcastWithoutSourcePostProcessing(
                    ref source,
                    ref message,
                    priority,
                    emissionId
                );
            }
        }

        /// <summary>
        /// Callback from the MessageBus for handling Messages when this MessageHandler has subscribed to GlobalAcceptAll - user code should generally never use this.
        /// </summary>
        /// <param name="message">Message to handle.</param>
        /// <param name="messageBus">The specific MessageBus to use.</param>
        public void HandleGlobalUntargetedMessage(
            ref IUntargetedMessage message,
            IMessageBus messageBus
        )
        {
            if (!active)
            {
                return;
            }

            // Use the "IMessage" explicitly to indicate global messages, allowing us to multipurpose a single dictionary
            if (GetHandlerForType(messageBus, out TypedHandler<IMessage> handler))
            {
                long emissionId = messageBus.EmissionId;
                handler.HandleGlobalUntargeted(ref message, emissionId);
            }
        }

        /// <summary>
        /// Pre-freezes this handler's GlobalAcceptAll untargeted caches for this emission.
        /// </summary>
        internal void PrefreezeGlobalUntargetedForEmission(long emissionId, IMessageBus messageBus)
        {
            if (!GetHandlerForType(messageBus, out TypedHandler<IMessage> handler))
            {
                return;
            }

            HandlerActionCache<FastHandler<IUntargetedMessage>> fastCache = handler.GetGlobalCache<
                FastHandler<IUntargetedMessage>
            >(TypedGlobalSlotIndex.UntargetedFast);
            if (fastCache != null)
            {
                _ = TypedHandler<IMessage>.GetOrAddNewHandlerStack(fastCache, emissionId);
            }
            HandlerActionCache<Action<IUntargetedMessage>> cache = handler.GetGlobalCache<
                Action<IUntargetedMessage>
            >(TypedGlobalSlotIndex.UntargetedDefault);
            if (cache != null)
            {
                _ = TypedHandler<IMessage>.GetOrAddNewHandlerStack(cache, emissionId);
            }
        }

        /// <summary>
        /// Pre-freezes this handler's GlobalAcceptAll targeted caches for this emission.
        /// </summary>
        internal void PrefreezeGlobalTargetedForEmission(long emissionId, IMessageBus messageBus)
        {
            if (!GetHandlerForType(messageBus, out TypedHandler<IMessage> handler))
            {
                return;
            }

            HandlerActionCache<FastHandlerWithContext<ITargetedMessage>> fastCache =
                handler.GetGlobalCache<FastHandlerWithContext<ITargetedMessage>>(
                    TypedGlobalSlotIndex.TargetedFast
                );
            if (fastCache != null)
            {
                _ = TypedHandler<IMessage>.GetOrAddNewHandlerStack(fastCache, emissionId);
            }
            HandlerActionCache<Action<InstanceId, ITargetedMessage>> cache = handler.GetGlobalCache<
                Action<InstanceId, ITargetedMessage>
            >(TypedGlobalSlotIndex.TargetedDefault);
            if (cache != null)
            {
                _ = TypedHandler<IMessage>.GetOrAddNewHandlerStack(cache, emissionId);
            }
        }

        /// <summary>
        /// Pre-freezes this handler's GlobalAcceptAll broadcast caches for this emission.
        /// </summary>
        internal void PrefreezeGlobalBroadcastForEmission(long emissionId, IMessageBus messageBus)
        {
            if (!GetHandlerForType(messageBus, out TypedHandler<IMessage> handler))
            {
                return;
            }

            HandlerActionCache<FastHandlerWithContext<IBroadcastMessage>> fastCache =
                handler.GetGlobalCache<FastHandlerWithContext<IBroadcastMessage>>(
                    TypedGlobalSlotIndex.BroadcastFast
                );
            if (fastCache != null)
            {
                _ = TypedHandler<IMessage>.GetOrAddNewHandlerStack(fastCache, emissionId);
            }
            HandlerActionCache<Action<InstanceId, IBroadcastMessage>> cache =
                handler.GetGlobalCache<Action<InstanceId, IBroadcastMessage>>(
                    TypedGlobalSlotIndex.BroadcastDefault
                );
            if (cache != null)
            {
                _ = TypedHandler<IMessage>.GetOrAddNewHandlerStack(cache, emissionId);
            }
        }

        /// <summary>
        /// Callback from the MessageBus for handling Messages when this MessageHandler has subscribed to GlobalAcceptAll - user code should generally never use this.
        /// </summary>
        /// <param name="target">Target of the message.</param>
        /// <param name="message">Message to handle.</param>
        /// <param name="messageBus">The specific MessageBus to use.</param>
        public void HandleGlobalTargetedMessage(
            ref InstanceId target,
            ref ITargetedMessage message,
            IMessageBus messageBus
        )
        {
            if (!active)
            {
                return;
            }

            // Use the "IMessage" explicitly to indicate global messages, allowing us to multipurpose a single dictionary
            if (GetHandlerForType(messageBus, out TypedHandler<IMessage> handler))
            {
                long emissionId = messageBus.EmissionId;
                handler.HandleGlobalTargeted(ref target, ref message, emissionId);
            }
        }

        /// <summary>
        /// Callback from the MessageBus for handling Messages when this MessageHandler has subscribed to GlobalAcceptAll - user code should generally never use this.
        /// </summary>
        /// <param name="source">Source that this message is from.</param>
        /// <param name="message">Message to handle.</param>
        /// <param name="messageBus">The specific MessageBus to use.</param>
        public void HandleGlobalSourcedBroadcastMessage(
            ref InstanceId source,
            ref IBroadcastMessage message,
            IMessageBus messageBus
        )
        {
            if (!active)
            {
                return;
            }

            // Use the "IMessage" explicitly to indicate global messages, allowing us to multipurpose a single dictionary
            if (GetHandlerForType(messageBus, out TypedHandler<IMessage> handler))
            {
                long emissionId = messageBus.EmissionId;
                handler.HandleGlobalBroadcast(ref source, ref message, emissionId);
            }
        }

        /// <summary>
        /// Registers this MessageHandler to Globally Accept All Messages via the MessageBus, properly handling deregistration.
        /// </summary>
        /// <param name="untargetedMessageHandler">MessageHandler to accept all UntargetedMessages.</param>
        /// <param name="broadcastMessageHandler">MessageHandler to accept all TargetedMessages for all entities.</param>
        /// <param name="targetedMessageHandler">MessageHandler to accept all BroadcastMessages for all entities.</param>
        /// <param name="messageBus">IMessageBus override to register with, if any. Null/not provided defaults to the GlobalMessageBus.</param>
        /// <returns>The de-registration action.</returns>
        public Action RegisterGlobalAcceptAll(
            Action<IUntargetedMessage> originalUntargetedMessageHandler,
            Action<IUntargetedMessage> untargetedMessageHandler,
            Action<InstanceId, ITargetedMessage> originalTargetedMessageHandler,
            Action<InstanceId, ITargetedMessage> targetedMessageHandler,
            Action<InstanceId, IBroadcastMessage> originalBroadcastMessageHandler,
            Action<InstanceId, IBroadcastMessage> broadcastMessageHandler,
            IMessageBus messageBus = null
        )
        {
            messageBus = ResolveMessageBus(messageBus);
            Action messageBusDeregistration = messageBus.RegisterGlobalAcceptAll(this);
            TypedHandler<IMessage> typedHandler = GetOrCreateHandlerForType<IMessage>(messageBus);

            Action untargetedDeregistration = typedHandler.AddGlobalUntargetedHandler(
                originalUntargetedMessageHandler,
                untargetedMessageHandler,
                NullDeregistration,
                messageBus
            );
            Action targetedDeregistration = typedHandler.AddGlobalTargetedHandler(
                originalTargetedMessageHandler,
                targetedMessageHandler,
                NullDeregistration,
                messageBus
            );
            Action broadcastDeregistration = typedHandler.AddGlobalBroadcastHandler(
                originalBroadcastMessageHandler,
                broadcastMessageHandler,
                NullDeregistration,
                messageBus
            );

            return () =>
            {
                messageBusDeregistration?.Invoke();
                untargetedDeregistration();
                targetedDeregistration();
                broadcastDeregistration();
            };

            void NullDeregistration()
            {
                // No-op
            }
        }

        /// <summary>
        /// Registers this MessageHandler to Globally Accept All Messages via the MessageBus, properly handling deregistration.
        /// </summary>
        /// <param name="untargetedMessageHandler">MessageHandler to accept all UntargetedMessages.</param>
        /// <param name="broadcastMessageHandler">MessageHandler to accept all TargetedMessages for all entities.</param>
        /// <param name="targetedMessageHandler">MessageHandler to accept all BroadcastMessages for all entities.</param>
        /// <param name="messageBus">IMessageBus override to register with, if any. Null/not provided defaults to the GlobalMessageBus.</param>
        /// <returns>The de-registration action.</returns>
        public Action RegisterGlobalAcceptAll(
            FastHandler<IUntargetedMessage> originalUntargetedMessageHandler,
            FastHandler<IUntargetedMessage> untargetedMessageHandler,
            FastHandlerWithContext<ITargetedMessage> originalTargetedMessageHandler,
            FastHandlerWithContext<ITargetedMessage> targetedMessageHandler,
            FastHandlerWithContext<IBroadcastMessage> originalBroadcastMessageHandler,
            FastHandlerWithContext<IBroadcastMessage> broadcastMessageHandler,
            IMessageBus messageBus = null
        )
        {
            messageBus = ResolveMessageBus(messageBus);
            Action messageBusDeregistration = messageBus.RegisterGlobalAcceptAll(this);
            TypedHandler<IMessage> typedHandler = GetOrCreateHandlerForType<IMessage>(messageBus);

            Action untargetedDeregistration = typedHandler.AddGlobalUntargetedHandler(
                originalUntargetedMessageHandler,
                untargetedMessageHandler,
                NullDeregistration,
                messageBus
            );
            Action targetedDeregistration = typedHandler.AddGlobalTargetedHandler(
                originalTargetedMessageHandler,
                targetedMessageHandler,
                NullDeregistration,
                messageBus
            );
            Action broadcastDeregistration = typedHandler.AddGlobalBroadcastHandler(
                originalBroadcastMessageHandler,
                broadcastMessageHandler,
                NullDeregistration,
                messageBus
            );

            return () =>
            {
                messageBusDeregistration?.Invoke();
                untargetedDeregistration();
                targetedDeregistration();
                broadcastDeregistration();
            };

            void NullDeregistration()
            {
                // No-op
            }
        }

        /// <summary>
        /// Registers this MessageHandler to accept TargetedMessages via the MessageBus, properly handling deregistration.
        /// </summary>
        /// <typeparam name="T">Type of Message to be handled.</typeparam>
        /// <param name="target">Target Id of TargetedMessages to listen for.</param>
        /// <param name="messageHandler">Function that actually handles the message.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <param name="messageBus">IMessageBus override to register with, if any. Null/not provided defaults to the GlobalMessageBus.</param>
        /// <returns>The de-registration action.</returns>
        public Action RegisterTargetedMessageHandler<T>(
            InstanceId target,
            Action<T> originalHandler,
            Action<T> messageHandler,
            int priority = 0,
            IMessageBus messageBus = null
        )
            where T : ITargetedMessage
        {
            messageBus = ResolveMessageBus(messageBus);
            Action messageBusDeregistration = messageBus.RegisterTargeted<T>(
                target,
                this,
                priority: priority
            );
            TypedHandler<T> typedHandler = GetOrCreateHandlerForType<T>(messageBus);
            return typedHandler.AddTargetedHandler(
                target,
                originalHandler,
                messageHandler,
                messageBusDeregistration,
                priority,
                messageBus
            );
        }

        /// <summary>
        /// Registers this MessageHandler to accept fast TargetedMessages via the MessageBus, properly handling deregistration.
        /// </summary>
        /// <typeparam name="T">Type of Message to be handled.</typeparam>
        /// <param name="target">Target Id of TargetedMessages to listen for.</param>
        /// <param name="messageHandler">Function that actually handles the message.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <param name="messageBus">IMessageBus override to register with, if any. Null/not provided defaults to the GlobalMessageBus.</param>
        /// <returns>The de-registration action.</returns>
        public Action RegisterTargetedMessageHandler<T>(
            InstanceId target,
            FastHandler<T> originalHandler,
            FastHandler<T> messageHandler,
            int priority = 0,
            IMessageBus messageBus = null
        )
            where T : ITargetedMessage
        {
            messageBus = ResolveMessageBus(messageBus);
            Action messageBusDeregistration = messageBus.RegisterTargeted<T>(
                target,
                this,
                priority: priority
            );
            TypedHandler<T> typedHandler = GetOrCreateHandlerForType<T>(messageBus);
            return typedHandler.AddTargetedHandler(
                target,
                originalHandler,
                messageHandler,
                messageBusDeregistration,
                priority,
                messageBus
            );
        }

        /// <summary>
        /// Registers this MessageHandler to post process TargetedMessages via the MessageBus, properly handling deregistration.
        /// </summary>
        /// <typeparam name="T">Type of Message to be handled.</typeparam>
        /// <param name="target">Target Id of TargetedMessages to listen for.</param>
        /// <param name="messageHandler">Function that actually handles the message.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <param name="messageBus">IMessageBus override to register with, if any. Null/not provided defaults to the GlobalMessageBus.</param>
        /// <returns>The de-registration action.</returns>
        public Action RegisterTargetedPostProcessor<T>(
            InstanceId target,
            Action<T> originalHandler,
            Action<T> messageHandler,
            int priority = 0,
            IMessageBus messageBus = null
        )
            where T : ITargetedMessage
        {
            messageBus = ResolveMessageBus(messageBus);
            Action messageBusDeregistration = messageBus.RegisterTargetedPostProcessor<T>(
                target,
                this,
                priority
            );
            TypedHandler<T> typedHandler = GetOrCreateHandlerForType<T>(messageBus);
            return typedHandler.AddTargetedPostProcessor(
                target,
                originalHandler,
                messageHandler,
                messageBusDeregistration,
                priority,
                messageBus
            );
        }

        /// <summary>
        /// Registers this MessageHandler to post process fast TargetedMessages via the MessageBus, properly handling deregistration.
        /// </summary>
        /// <typeparam name="T">Type of Message to be handled.</typeparam>
        /// <param name="target">Target Id of TargetedMessages to listen for.</param>
        /// <param name="messageHandler">Function that actually handles the message.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <param name="messageBus">IMessageBus override to register with, if any. Null/not provided defaults to the GlobalMessageBus.</param>
        /// <returns>The de-registration action.</returns>
        public Action RegisterTargetedPostProcessor<T>(
            InstanceId target,
            FastHandler<T> originalHandler,
            FastHandler<T> messageHandler,
            int priority = 0,
            IMessageBus messageBus = null
        )
            where T : ITargetedMessage
        {
            messageBus = ResolveMessageBus(messageBus);
            Action messageBusDeregistration = messageBus.RegisterTargetedPostProcessor<T>(
                target,
                this,
                priority: priority
            );
            TypedHandler<T> typedHandler = GetOrCreateHandlerForType<T>(messageBus);
            return typedHandler.AddTargetedPostProcessor(
                target,
                originalHandler,
                messageHandler,
                messageBusDeregistration,
                priority,
                messageBus
            );
        }

        /// <summary>
        /// Registers this MessageHandler to post-process TargetedMessages for all messages of the provided type via the MessageBus, properly handling deregistration.
        /// </summary>
        /// <typeparam name="T">Type of Message to be handled.</typeparam>
        /// <param name="messageHandler">Function that actually handles the message.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <param name="messageBus">IMessageBus override to register with, if any. Null/not provided defaults to the GlobalMessageBus.</param>
        /// <returns>The de-registration action.</returns>
        public Action RegisterTargetedWithoutTargetingPostProcessor<T>(
            Action<InstanceId, T> originalHandler,
            Action<InstanceId, T> messageHandler,
            int priority = 0,
            IMessageBus messageBus = null
        )
            where T : ITargetedMessage
        {
            messageBus = ResolveMessageBus(messageBus);
            Action messageBusDeregistration =
                messageBus.RegisterTargetedWithoutTargetingPostProcessor<T>(
                    priority: priority,
                    messageHandler: this
                );
            TypedHandler<T> typedHandler = GetOrCreateHandlerForType<T>(messageBus);
            return typedHandler.AddTargetedWithoutTargetingPostProcessor(
                originalHandler,
                messageHandler,
                messageBusDeregistration,
                priority,
                messageBus
            );
        }

        /// <summary>
        /// Registers this MessageHandler to post process fast TargetedMessages for all messages of the provided type via the MessageBus, properly handling deregistration.
        /// </summary>
        /// <typeparam name="T">Type of Message to be handled.</typeparam>
        /// <param name="messageHandler">Function that actually handles the message.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <param name="messageBus">IMessageBus override to register with, if any. Null/not provided defaults to the GlobalMessageBus.</param>
        /// <returns>The de-registration action.</returns>
        public Action RegisterTargetedWithoutTargetingPostProcessor<T>(
            FastHandlerWithContext<T> originalHandler,
            FastHandlerWithContext<T> messageHandler,
            int priority = 0,
            IMessageBus messageBus = null
        )
            where T : ITargetedMessage
        {
            messageBus = ResolveMessageBus(messageBus);
            Action messageBusDeregistration =
                messageBus.RegisterTargetedWithoutTargetingPostProcessor<T>(
                    priority: priority,
                    messageHandler: this
                );
            TypedHandler<T> typedHandler = GetOrCreateHandlerForType<T>(messageBus);
            return typedHandler.AddTargetedWithoutTargetingPostProcessor(
                originalHandler,
                messageHandler,
                messageBusDeregistration,
                priority,
                messageBus
            );
        }

        /// <summary>
        /// Registers this MessageHandler to accept TargetedMessages without Targeting via the MessageBus, properly handling deregistration.
        /// </summary>
        /// <typeparam name="T">Type of Message to be handled.</typeparam>
        /// <param name="messageHandler">Function that actually handles the message.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <param name="messageBus">IMessageBus override to register with, if any. Null/not provided defaults to the GlobalMessageBus.</param>
        /// <returns>The de-registration action.</returns>
        public Action RegisterTargetedWithoutTargeting<T>(
            Action<InstanceId, T> originalHandler,
            Action<InstanceId, T> messageHandler,
            int priority = 0,
            IMessageBus messageBus = null
        )
            where T : ITargetedMessage
        {
            messageBus = ResolveMessageBus(messageBus);
            Action messageBusDeregistration = messageBus.RegisterTargetedWithoutTargeting<T>(
                this,
                priority: priority
            );
            TypedHandler<T> typedHandler = GetOrCreateHandlerForType<T>(messageBus);
            return typedHandler.AddTargetedWithoutTargetingHandler(
                originalHandler,
                messageHandler,
                messageBusDeregistration,
                priority,
                messageBus
            );
        }

        /// <summary>
        /// Registers this MessageHandler to accept fast TargetedMessages without Targeting via the MessageBus, properly handling deregistration.
        /// </summary>
        /// <typeparam name="T">Type of Message to be handled.</typeparam>
        /// <param name="messageHandler">Function that actually handles the message.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <param name="messageBus">IMessageBus override to register with, if any. Null/not provided defaults to the GlobalMessageBus.</param>
        /// <returns>The de-registration action.</returns>
        public Action RegisterTargetedWithoutTargeting<T>(
            FastHandlerWithContext<T> originalHandler,
            FastHandlerWithContext<T> messageHandler,
            int priority = 0,
            IMessageBus messageBus = null
        )
            where T : ITargetedMessage
        {
            messageBus = ResolveMessageBus(messageBus);
            Action messageBusDeregistration = messageBus.RegisterTargetedWithoutTargeting<T>(
                this,
                priority: priority
            );
            TypedHandler<T> typedHandler = GetOrCreateHandlerForType<T>(messageBus);
            return typedHandler.AddTargetedWithoutTargetingHandler(
                originalHandler,
                messageHandler,
                messageBusDeregistration,
                priority,
                messageBus
            );
        }

        /// <summary>
        /// Registers this MessageHandler to accept UntargetedMessages via the MessageBus, properly handling deregistration.
        /// </summary>
        /// <typeparam name="T">Type of Message to be handled.</typeparam>
        /// <param name="messageHandler">Function that actually handles the message.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <param name="messageBus">IMessageBus override to register with, if any. Null/not provided defaults to the GlobalMessageBus.</param>
        /// <returns>The de-registration action.</returns>
        public Action RegisterUntargetedMessageHandler<T>(
            Action<T> originalHandler,
            Action<T> messageHandler,
            int priority = 0,
            IMessageBus messageBus = null
        )
            where T : IUntargetedMessage
        {
            messageBus = ResolveMessageBus(messageBus);
            Action messageBusDeregistration = messageBus.RegisterUntargeted<T>(
                this,
                priority: priority
            );
            TypedHandler<T> typedHandler = GetOrCreateHandlerForType<T>(messageBus);
            return typedHandler.AddUntargetedHandler(
                originalHandler,
                messageHandler,
                messageBusDeregistration,
                priority,
                messageBus
            );
        }

        /// <summary>
        /// Registers this MessageHandler to accept fast UntargetedMessages via the MessageBus, properly handling deregistration.
        /// </summary>
        /// <typeparam name="T">Type of Message to be handled.</typeparam>
        /// <param name="messageHandler">Function that actually handles the message.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <param name="messageBus">IMessageBus override to register with, if any. Null/not provided defaults to the GlobalMessageBus.</param>
        /// <returns>The de-registration action.</returns>
        public Action RegisterUntargetedMessageHandler<T>(
            FastHandler<T> originalHandler,
            FastHandler<T> messageHandler,
            int priority = 0,
            IMessageBus messageBus = null
        )
            where T : IUntargetedMessage
        {
            messageBus = ResolveMessageBus(messageBus);
            Action messageBusDeregistration = messageBus.RegisterUntargeted<T>(
                this,
                priority: priority
            );
            TypedHandler<T> typedHandler = GetOrCreateHandlerForType<T>(messageBus);
            return typedHandler.AddUntargetedHandler(
                originalHandler,
                messageHandler,
                messageBusDeregistration,
                priority,
                messageBus
            );
        }

        /// <summary>
        /// Registers this MessageHandler to post-process UntargetedMessages via the MessageBus, properly handling deregistration.
        /// </summary>
        /// <typeparam name="T">Type of Message to be handled.</typeparam>
        /// <param name="messageHandler">Function that actually handles the message.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <param name="messageBus">IMessageBus override to register with, if any. Null/not provided defaults to the GlobalMessageBus.</param>
        /// <returns>The de-registration action.</returns>
        public Action RegisterUntargetedPostProcessor<T>(
            Action<T> originalHandler,
            Action<T> messageHandler,
            int priority = 0,
            IMessageBus messageBus = null
        )
            where T : IUntargetedMessage
        {
            messageBus = ResolveMessageBus(messageBus);
            Action messageBusDeregistration = messageBus.RegisterUntargetedPostProcessor<T>(
                priority: priority,
                messageHandler: this
            );
            TypedHandler<T> typedHandler = GetOrCreateHandlerForType<T>(messageBus);
            return typedHandler.AddUntargetedPostProcessor(
                originalHandler,
                messageHandler,
                messageBusDeregistration,
                priority,
                messageBus
            );
        }

        /// <summary>
        /// Registers this MessageHandler to post process fast UntargetedMessages via the MessageBus, properly handling deregistration.
        /// </summary>
        /// <typeparam name="T">Type of Message to be handled.</typeparam>
        /// <param name="messageHandler">Function that actually handles the message.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <param name="messageBus">IMessageBus override to register with, if any. Null/not provided defaults to the GlobalMessageBus.</param>
        /// <returns>The de-registration action.</returns>
        public Action RegisterUntargetedPostProcessor<T>(
            FastHandler<T> originalHandler,
            FastHandler<T> messageHandler,
            int priority = 0,
            IMessageBus messageBus = null
        )
            where T : IUntargetedMessage
        {
            messageBus = ResolveMessageBus(messageBus);
            Action messageBusDeregistration = messageBus.RegisterUntargetedPostProcessor<T>(
                priority: priority,
                messageHandler: this
            );
            TypedHandler<T> typedHandler = GetOrCreateHandlerForType<T>(messageBus);
            return typedHandler.AddUntargetedPostProcessor(
                originalHandler,
                messageHandler,
                messageBusDeregistration,
                priority,
                messageBus
            );
        }

        /// <summary>
        /// Registers this MessageHandler to accept BroadcastMessages via their MessageBus, properly handling deregistration.
        /// </summary>
        /// <typeparam name="T">Type of Message to be handled.</typeparam>
        /// <param name="source">Source Id of BroadcastMessages to listen for.</param>
        /// <param name="messageHandler">Function that actually handles the message.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <param name="messageBus">IMessageBus override to register with, if any. Null/not provided defaults to the GlobalMessageBus.</param>
        /// <returns>The de-registration action.</returns>
        public Action RegisterSourcedBroadcastMessageHandler<T>(
            InstanceId source,
            Action<T> originalHandler,
            Action<T> messageHandler,
            int priority = 0,
            IMessageBus messageBus = null
        )
            where T : IBroadcastMessage
        {
            messageBus = ResolveMessageBus(messageBus);
            Action messageBusDeregistration = messageBus.RegisterSourcedBroadcast<T>(
                source,
                this,
                priority: priority
            );
            TypedHandler<T> typedHandler = GetOrCreateHandlerForType<T>(messageBus);

            return typedHandler.AddSourcedBroadcastHandler(
                source,
                originalHandler,
                messageHandler,
                messageBusDeregistration,
                priority,
                messageBus
            );
        }

        /// <summary>
        /// Registers this MessageHandler to accept fast BroadcastMessages via their MessageBus, properly handling deregistration.
        /// </summary>
        /// <typeparam name="T">Type of Message to be handled.</typeparam>
        /// <param name="source">Source Id of BroadcastMessages to listen for.</param>
        /// <param name="messageHandler">Function that actually handles the message.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <param name="messageBus">IMessageBus override to register with, if any. Null/not provided defaults to the GlobalMessageBus.</param>
        /// <returns>The de-registration action.</returns>
        public Action RegisterSourcedBroadcastMessageHandler<T>(
            InstanceId source,
            FastHandler<T> originalHandler,
            FastHandler<T> messageHandler,
            int priority = 0,
            IMessageBus messageBus = null
        )
            where T : IBroadcastMessage
        {
            messageBus = ResolveMessageBus(messageBus);
            Action messageBusDeregistration = messageBus.RegisterSourcedBroadcast<T>(
                source,
                this,
                priority: priority
            );
            TypedHandler<T> typedHandler = GetOrCreateHandlerForType<T>(messageBus);
            return typedHandler.AddSourcedBroadcastHandler(
                source,
                originalHandler,
                messageHandler,
                messageBusDeregistration,
                priority,
                messageBus
            );
        }

        /// <summary>
        /// Registers this MessageHandler to accept BroadcastMessage regardless of source via their MessageBus, properly handling deregistration.
        /// </summary>
        /// <typeparam name="T">Type of Message to be handled.</typeparam>
        /// <param name="messageHandler">Function that actually handles the message.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <param name="messageBus">IMessageBus override to register with, if any. Null/not provided defaults to the GlobalMessageBus.</param>
        /// <returns>The de-registration action.</returns>
        public Action RegisterSourcedBroadcastWithoutSource<T>(
            Action<InstanceId, T> originalHandler,
            Action<InstanceId, T> messageHandler,
            int priority = 0,
            IMessageBus messageBus = null
        )
            where T : IBroadcastMessage
        {
            messageBus = ResolveMessageBus(messageBus);
            Action messageBusDeregistration = messageBus.RegisterSourcedBroadcastWithoutSource<T>(
                this,
                priority: priority
            );
            TypedHandler<T> typedHandler = GetOrCreateHandlerForType<T>(messageBus);
            return typedHandler.AddSourcedBroadcastWithoutSourceHandler(
                originalHandler,
                messageHandler,
                messageBusDeregistration,
                priority,
                messageBus
            );
        }

        /// <summary>
        /// Registers this MessageHandler to accept fast BroadcastMessage regardless of source via their MessageBus, properly handling deregistration.
        /// </summary>
        /// <typeparam name="T">Type of Message to be handled.</typeparam>
        /// <param name="messageHandler">Function that actually handles the message.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <param name="messageBus">IMessageBus override to register with, if any. Null/not provided defaults to the GlobalMessageBus.</param>
        /// <returns>The de-registration action.</returns>
        public Action RegisterSourcedBroadcastWithoutSource<T>(
            FastHandlerWithContext<T> originalHandler,
            FastHandlerWithContext<T> messageHandler,
            int priority = 0,
            IMessageBus messageBus = null
        )
            where T : IBroadcastMessage
        {
            messageBus = ResolveMessageBus(messageBus);
            Action messageBusDeregistration = messageBus.RegisterSourcedBroadcastWithoutSource<T>(
                this,
                priority: priority
            );
            TypedHandler<T> typedHandler = GetOrCreateHandlerForType<T>(messageBus);
            return typedHandler.AddSourcedBroadcastWithoutSourceHandler(
                originalHandler,
                messageHandler,
                messageBusDeregistration,
                priority,
                messageBus
            );
        }

        /// <summary>
        /// Registers this MessageHandler to post-processes BroadcastMessage messages.
        /// </summary>
        /// <typeparam name="T">Type of Message to be handled.</typeparam>
        /// <param name="source">Source object to listen for BroadcastMessages on.</param>
        /// <param name="messageHandler">Function that actually handles the message.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <param name="messageBus">IMessageBus override to register with, if any. Null/not provided defaults to the GlobalMessageBus.</param>
        /// <returns>The de-registration action.</returns>
        public Action RegisterSourcedBroadcastPostProcessor<T>(
            InstanceId source,
            Action<T> originalHandler,
            Action<T> messageHandler,
            int priority = 0,
            IMessageBus messageBus = null
        )
            where T : IBroadcastMessage
        {
            messageBus = ResolveMessageBus(messageBus);
            Action messageBusDeregistration = messageBus.RegisterBroadcastPostProcessor<T>(
                source,
                messageHandler: this,
                priority: priority
            );
            TypedHandler<T> typedHandler = GetOrCreateHandlerForType<T>(messageBus);
            return typedHandler.AddBroadcastPostProcessor(
                source,
                originalHandler,
                messageHandler,
                messageBusDeregistration,
                priority,
                messageBus
            );
        }

        /// <summary>
        /// Registers this MessageHandler to post processes fast BroadcastMessage messages.
        /// </summary>
        /// <typeparam name="T">Type of Message to be handled.</typeparam>
        /// <param name="source">Source object to listen for BroadcastMessages on.</param>
        /// <param name="messageHandler">Function that actually handles the message.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <param name="messageBus">IMessageBus override to register with, if any. Null/not provided defaults to the GlobalMessageBus.</param>
        /// <returns>The de-registration action.</returns>
        public Action RegisterSourcedBroadcastPostProcessor<T>(
            InstanceId source,
            FastHandler<T> originalHandler,
            FastHandler<T> messageHandler,
            int priority = 0,
            IMessageBus messageBus = null
        )
            where T : IBroadcastMessage
        {
            messageBus = ResolveMessageBus(messageBus);
            Action messageBusDeregistration = messageBus.RegisterBroadcastPostProcessor<T>(
                source,
                priority: priority,
                messageHandler: this
            );
            TypedHandler<T> typedHandler = GetOrCreateHandlerForType<T>(messageBus);
            return typedHandler.AddBroadcastPostProcessor(
                source,
                originalHandler,
                messageHandler,
                messageBusDeregistration,
                priority,
                messageBus
            );
        }

        /// <summary>
        /// Registers this MessageHandler to post-processes BroadcastMessage messages for all messages of the provided type.
        /// </summary>
        /// <typeparam name="T">Type of Message to be handled.</typeparam>
        /// <param name="messageHandler">Function that actually handles the message.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <param name="messageBus">IMessageBus override to register with, if any. Null/not provided defaults to the GlobalMessageBus.</param>
        /// <returns>The de-registration action.</returns>
        public Action RegisterSourcedBroadcastWithoutSourcePostProcessor<T>(
            Action<InstanceId, T> originalHandler,
            Action<InstanceId, T> messageHandler,
            int priority = 0,
            IMessageBus messageBus = null
        )
            where T : IBroadcastMessage
        {
            messageBus = ResolveMessageBus(messageBus);
            Action messageBusDeregistration =
                messageBus.RegisterBroadcastWithoutSourcePostProcessor<T>(
                    priority: priority,
                    messageHandler: this
                );
            TypedHandler<T> typedHandler = GetOrCreateHandlerForType<T>(messageBus);
            return typedHandler.AddBroadcastWithoutSourcePostProcessor(
                originalHandler,
                messageHandler,
                messageBusDeregistration,
                priority,
                messageBus
            );
        }

        /// <summary>
        /// Registers this MessageHandler to post processes fast BroadcastMessage messages for all messages of the provided type.
        /// </summary>
        /// <typeparam name="T">Type of Message to be handled.</typeparam>
        /// <param name="messageHandler">Function that actually handles the message.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <param name="messageBus">IMessageBus override to register with, if any. Null/not provided defaults to the GlobalMessageBus.</param>
        /// <returns>The de-registration action.</returns>
        public Action RegisterSourcedBroadcastWithoutSourcePostProcessor<T>(
            FastHandlerWithContext<T> originalHandler,
            FastHandlerWithContext<T> messageHandler,
            int priority = 0,
            IMessageBus messageBus = null
        )
            where T : IBroadcastMessage
        {
            messageBus = ResolveMessageBus(messageBus);
            Action messageBusDeregistration =
                messageBus.RegisterBroadcastWithoutSourcePostProcessor<T>(
                    priority: priority,
                    messageHandler: this
                );
            TypedHandler<T> typedHandler = GetOrCreateHandlerForType<T>(messageBus);
            return typedHandler.AddBroadcastWithoutSourcePostProcessor(
                originalHandler,
                messageHandler,
                messageBusDeregistration,
                priority,
                messageBus
            );
        }

        /// <summary>
        /// Registers an UntargetedInterceptor for messages of the provided type at the provided priority.
        /// </summary>
        /// <typeparam name="T">Type of the UntargetedMessage to intercept.</typeparam>
        /// <param name="interceptor">Interceptor to register.</param>
        /// <param name="priority">Priority to register the interceptor at (interceptors are run from low -> high priority)</param>
        /// <param name="messageBus">Message bus to register the interceptor on.</param>
        /// <returns>The de-registration action.</returns>
        public Action RegisterUntargetedInterceptor<T>(
            IMessageBus.UntargetedInterceptor<T> interceptor,
            int priority = 0,
            IMessageBus messageBus = null
        )
            where T : IUntargetedMessage
        {
            messageBus = ResolveMessageBus(messageBus);
            return messageBus.RegisterUntargetedInterceptor(interceptor, priority);
        }

        /// <summary>
        /// Registers a BroadcastInterceptor for messages of the provided type at the provided priority.
        /// </summary>
        /// <typeparam name="T">Type of the BroadcastMessage to intercept.</typeparam>
        /// <param name="interceptor">Interceptor to register.</param>
        /// <param name="priority">Priority to register the interceptor at (interceptors are run from low -> high priority)</param>
        /// <param name="messageBus">Message bus to register the interceptor on.</param>
        /// <returns>The de-registration action.</returns>
        public Action RegisterBroadcastInterceptor<T>(
            IMessageBus.BroadcastInterceptor<T> interceptor,
            int priority = 0,
            IMessageBus messageBus = null
        )
            where T : IBroadcastMessage
        {
            messageBus = ResolveMessageBus(messageBus);
            return messageBus.RegisterBroadcastInterceptor(interceptor, priority);
        }

        /// <summary>
        /// Registers a TargetedInterceptor for messages of the provided type at the provided priority.
        /// </summary>
        /// <typeparam name="T">Type of the TargetedMessage to intercept.</typeparam>
        /// <param name="interceptor">Interceptor to register.</param>
        /// <param name="priority">Priority to register the interceptor at (interceptors are run from low -> high priority)</param>
        /// <param name="messageBus">Message bus to register the interceptor on.</param>
        /// <returns>The de-registration action.</returns>
        public Action RegisterTargetedInterceptor<T>(
            IMessageBus.TargetedInterceptor<T> interceptor,
            int priority = 0,
            IMessageBus messageBus = null
        )
            where T : ITargetedMessage
        {
            messageBus = ResolveMessageBus(messageBus);
            return messageBus.RegisterTargetedInterceptor(interceptor, priority);
        }

        /// <summary>
        /// Checks equality against another object.
        /// </summary>
        /// <param name="obj">Object to compare.</param>
        /// <returns><c>true</c> when <paramref name="obj"/> is a <see cref="MessageHandler"/> with the same owner.</returns>
        public override bool Equals(object obj)
        {
            return Equals(obj as MessageHandler);
        }

        /// <summary>
        /// Checks equality against another handler instance.
        /// </summary>
        /// <param name="other">Handler to compare.</param>
        /// <returns><c>true</c> when both handlers share the same <see cref="owner"/>.</returns>
        public bool Equals(MessageHandler other)
        {
            if (other == null)
            {
                return false;
            }

            if (ReferenceEquals(other, this))
            {
                return true;
            }

            return owner.Equals(other.owner);
        }

        /// <summary>
        /// Produces a hash code based on the owning instance.
        /// </summary>
        /// <returns>Hash code derived from <see cref="owner"/>.</returns>
        public override int GetHashCode()
        {
            return owner.GetHashCode();
        }

        /// <summary>
        /// Compares this handler with another handler for ordering.
        /// </summary>
        /// <param name="other">Handler to compare.</param>
        /// <returns>Relative ordering based on <see cref="owner"/>.</returns>
        public int CompareTo(MessageHandler other)
        {
            if (other == null)
            {
                return -1;
            }

            return owner.CompareTo(other.owner);
        }

        /// <summary>
        /// Compares this handler with an arbitrary object.
        /// </summary>
        /// <param name="obj">Object to compare.</param>
        /// <returns>
        /// Relative ordering when <paramref name="obj"/> is a <see cref="MessageHandler"/>; otherwise <c>-1</c>.
        /// </returns>
        public int CompareTo(object obj)
        {
            return CompareTo(obj as MessageHandler);
        }

        /// <summary>
        /// Returns a human-readable representation containing the owner identifier.
        /// </summary>
        /// <returns>String describing the handler.</returns>
        public override string ToString()
        {
            return new { OwnerId = owner }.ToString();
        }

        /// <summary>
        /// Retrieves an existing Handler for the specific type if it exists, or creates a new Handler if none exist.
        /// </summary>
        /// <typeparam name="T">Type of Message to retrieve a Handler for.</typeparam>
        /// <returns>Non-Null Handler for the specific type.</returns>
        private TypedHandler<T> GetOrCreateHandlerForType<T>(IMessageBus messageBus)
            where T : IMessage
        {
            int messageBusIndex = messageBus.RegisteredGlobalSequentialIndex;
            while (_handlersByTypeByMessageBus.Count <= messageBusIndex)
            {
                _handlersByTypeByMessageBus.Add(new MessageCache<object>());
            }

            MessageCache<object> handlersByType = _handlersByTypeByMessageBus[messageBusIndex];
            if (handlersByType.TryGetValue<T>(out object untypedHandler))
            {
                return DxUnsafe.As<TypedHandler<T>>(untypedHandler);
            }

            TypedHandler<T> typedHandler = new();
            handlersByType.Set<T>(typedHandler);
            return typedHandler;
        }

        /// <summary>
        /// Gets an existing Handler for the specific type if it exists.
        /// </summary>
        /// <param name="messageBus">The specific MessageBus to use.</param>
        /// <param name="existingTypedHandler">Existing typed message handler, if one exists.</param>
        /// <returns>Existing handler for the specific type, or null if none exists.</returns>
        private bool GetHandlerForType<T>(
            IMessageBus messageBus,
            out TypedHandler<T> existingTypedHandler
        )
            where T : IMessage
        {
            int messageBusIndex = messageBus.RegisteredGlobalSequentialIndex;
            if (_handlersByTypeByMessageBus.Count <= messageBusIndex)
            {
                existingTypedHandler = default;
                return false;
            }

            if (
                _handlersByTypeByMessageBus[messageBusIndex]
                    .TryGetValue<T>(out object untypedHandler)
            )
            {
                existingTypedHandler = DxUnsafe.As<TypedHandler<T>>(untypedHandler);
                return true;
            }

            existingTypedHandler = default;
            return false;
        }

        /// <summary>
        /// Resets empty typed-handler slots associated with
        /// <paramref name="messageBus"/>. The eviction layer calls through
        /// this erased surface after bus-side slots prove idle and empty.
        /// </summary>
        /// <param name="messageBus">
        /// Bus whose typed-handler cache should be swept. Null resolves to
        /// this handler's default bus.
        /// </param>
        /// <returns>Number of typed or typed-global slots reset.</returns>
        internal int ResetEmptyTypedSlotsForSweep(IMessageBus messageBus = null)
        {
            messageBus = ResolveMessageBus(messageBus);
            int messageBusIndex = messageBus.RegisteredGlobalSequentialIndex;
            if (messageBusIndex < 0 || _handlersByTypeByMessageBus.Count <= messageBusIndex)
            {
                return 0;
            }

            int resetCount = 0;
            MessageCache<object> handlersByType = _handlersByTypeByMessageBus[messageBusIndex];
            foreach (object untypedHandler in _handlersByTypeByMessageBus[messageBusIndex])
            {
                if (untypedHandler is ITypedHandlerSlotSweeper sweeper)
                {
                    resetCount += sweeper.ResetEmptySlotsForSweep();
                    if (sweeper.MarkedForOuterRemoval)
                    {
                        handlersByType.RemoveAtIndex(sweeper.MessageTypeIndex);
                    }
                }
            }

            return resetCount;
        }

        internal int ResetAllTypedSlotsForBusReset(IMessageBus messageBus = null)
        {
            messageBus = ResolveMessageBus(messageBus);
            int messageBusIndex = messageBus.RegisteredGlobalSequentialIndex;
            if (messageBusIndex < 0 || _handlersByTypeByMessageBus.Count <= messageBusIndex)
            {
                return 0;
            }

            int resetCount = 0;
            foreach (object untypedHandler in _handlersByTypeByMessageBus[messageBusIndex])
            {
                if (untypedHandler is ITypedHandlerSlotSweeper sweeper)
                {
                    resetCount += sweeper.ResetAllSlotsForBusReset();
                }
            }

            return resetCount;
        }

        internal int CountEmptyTypedSlotsForSweep(IMessageBus messageBus = null)
        {
            messageBus = ResolveMessageBus(messageBus);
            int messageBusIndex = messageBus.RegisteredGlobalSequentialIndex;
            if (messageBusIndex < 0 || _handlersByTypeByMessageBus.Count <= messageBusIndex)
            {
                return 0;
            }

            int count = 0;
            foreach (object untypedHandler in _handlersByTypeByMessageBus[messageBusIndex])
            {
                if (untypedHandler is ITypedHandlerSlotSweeper sweeper)
                {
                    count += sweeper.CountEmptySlotsForSweep();
                }
            }

            return count;
        }

        internal bool HasTypedHandlersForBus(IMessageBus messageBus = null)
        {
            messageBus = ResolveMessageBus(messageBus);
            int messageBusIndex = messageBus.RegisteredGlobalSequentialIndex;
            if (messageBusIndex < 0 || _handlersByTypeByMessageBus.Count <= messageBusIndex)
            {
                return false;
            }

            foreach (object untypedHandler in _handlersByTypeByMessageBus[messageBusIndex])
            {
                if (untypedHandler != null)
                {
                    return true;
                }
            }

            return false;
        }

        internal int GetUntargetedPostProcessingPrefreezeCount<T>(
            IMessageBus messageBus,
            int priority
        )
            where T : IMessage
        {
            if (!GetHandlerForType(messageBus, out TypedHandler<T> handler))
            {
                return 0;
            }

            return GetPriorityPrefreezeInvocationCount<T, FastHandler<T>>(
                handler,
                TypedSlotIndex.UntargetedPostProcessFast,
                priority
            );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal UntargetedDispatchLink<T> GetOrCreateUntargetedDispatchLink<T>(
            IMessageBus messageBus
        )
            where T : IMessage
        {
            TypedHandler<T> typedHandler = GetOrCreateHandlerForType<T>(messageBus);
            return typedHandler.GetOrCreateUntargetedLink();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal UntargetedPostDispatchLink<T> GetOrCreateUntargetedPostDispatchLink<T>(
            IMessageBus messageBus
        )
            where T : IMessage
        {
            TypedHandler<T> typedHandler = GetOrCreateHandlerForType<T>(messageBus);
            return typedHandler.GetOrCreateUntargetedPostLink();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal TargetedDispatchLink<T> GetOrCreateTargetedDispatchLink<T>(IMessageBus messageBus)
            where T : IMessage
        {
            TypedHandler<T> typedHandler = GetOrCreateHandlerForType<T>(messageBus);
            return typedHandler.GetOrCreateTargetedLink();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal TargetedPostDispatchLink<T> GetOrCreateTargetedPostDispatchLink<T>(
            IMessageBus messageBus
        )
            where T : IMessage
        {
            TypedHandler<T> typedHandler = GetOrCreateHandlerForType<T>(messageBus);
            return typedHandler.GetOrCreateTargetedPostLink();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal TargetedWithoutTargetingDispatchLink<T> GetOrCreateTargetedWithoutTargetingDispatchLink<T>(
            IMessageBus messageBus
        )
            where T : IMessage
        {
            TypedHandler<T> typedHandler = GetOrCreateHandlerForType<T>(messageBus);
            return typedHandler.GetOrCreateTargetedWithoutTargetingLink();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal TargetedWithoutTargetingPostDispatchLink<T> GetOrCreateTargetedWithoutTargetingPostDispatchLink<T>(
            IMessageBus messageBus
        )
            where T : IMessage
        {
            TypedHandler<T> typedHandler = GetOrCreateHandlerForType<T>(messageBus);
            return typedHandler.GetOrCreateTargetedWithoutTargetingPostLink();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal BroadcastDispatchLink<T> GetOrCreateBroadcastDispatchLink<T>(
            IMessageBus messageBus
        )
            where T : IMessage
        {
            TypedHandler<T> typedHandler = GetOrCreateHandlerForType<T>(messageBus);
            return typedHandler.GetOrCreateBroadcastLink();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal BroadcastPostDispatchLink<T> GetOrCreateBroadcastPostDispatchLink<T>(
            IMessageBus messageBus
        )
            where T : IMessage
        {
            TypedHandler<T> typedHandler = GetOrCreateHandlerForType<T>(messageBus);
            return typedHandler.GetOrCreateBroadcastPostLink();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal BroadcastWithoutSourceDispatchLink<T> GetOrCreateBroadcastWithoutSourceDispatchLink<T>(
            IMessageBus messageBus
        )
            where T : IMessage
        {
            TypedHandler<T> typedHandler = GetOrCreateHandlerForType<T>(messageBus);
            return typedHandler.GetOrCreateBroadcastWithoutSourceLink();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal BroadcastWithoutSourcePostDispatchLink<T> GetOrCreateBroadcastWithoutSourcePostDispatchLink<T>(
            IMessageBus messageBus
        )
            where T : IMessage
        {
            TypedHandler<T> typedHandler = GetOrCreateHandlerForType<T>(messageBus);
            return typedHandler.GetOrCreateBroadcastWithoutSourcePostLink();
        }

        internal sealed class HandlerActionCache<T> : DxMessaging.Core.Internal.IHandlerActionCache
        {
            // Uses outer T as a field type -- reflection callers must close
            // via MakeGenericType(outer.GetGenericArguments()) before passing
            // this type to Activator.CreateInstance. See
            // Tests/Editor/Contract/ReflectionHelpers.cs::CloseNestedGeneric.
            internal readonly struct Entry
            {
                /// <summary>
                /// Initializes an entry used to track handler invocation counts.
                /// </summary>
                /// <param name="handler">Handler delegate being tracked.</param>
                /// <param name="count">Number of times the handler has been cached.</param>
                public Entry(T handler, int count)
                {
                    this.handler = handler;
                    this.count = count;
                }

                public readonly T handler;
                public readonly int count;
            }

            public readonly Dictionary<T, Entry> entries = new();

            // Original-handler keys in first-registration order. Dictionary
            // enumeration order is NOT stable across Remove/Add churn (.NET
            // reuses freed slots LIFO), so dispatch snapshots are rebuilt from
            // this list instead of from <see cref="entries"/> to honor the
            // documented "same priority uses registration order" contract.
            // Invariants: contains exactly the keys of <see cref="entries"/>;
            // a key is appended on its FIRST registration only (refcount
            // increments do not move it) and removed when its refcount drops
            // to zero. Maintained exclusively by the AddHandler* family and
            // <see cref="DxMessaging.Core.Internal.IHandlerActionCache.Reset"/>.
            public readonly List<T> insertionOrder = new();
            public readonly List<T> cache = new();
            public long version;
            public long lastSeenVersion = -1;
            public long lastSeenEmissionId = -1;
            internal int prefreezeInvocationCount;

            /// <summary>Monotonic version field, read-only on the interface surface.</summary>
            long DxMessaging.Core.Internal.IHandlerActionCache.Version
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => version;
            }

            /// <summary>Most recent dispatcher-observed version; mutable through the staged dispatch path.</summary>
            long DxMessaging.Core.Internal.IHandlerActionCache.LastSeenVersion
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => lastSeenVersion;
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                set => lastSeenVersion = value;
            }

            /// <summary>Most recent dispatcher-observed bus emission id.</summary>
            long DxMessaging.Core.Internal.IHandlerActionCache.LastSeenEmissionId
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => lastSeenEmissionId;
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                set => lastSeenEmissionId = value;
            }

            /// <summary>Prefreeze invocation counter mirror; maintained by the dispatchers.</summary>
            int DxMessaging.Core.Internal.IHandlerActionCache.PrefreezeInvocationCount
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => prefreezeInvocationCount;
            }

            /// <summary>True iff the entries dictionary holds zero handlers.</summary>
            bool DxMessaging.Core.Internal.IHandlerActionCache.IsEmpty
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => entries.Count == 0;
            }

            /// <summary>
            /// Eviction-driven full clear; bumps <see cref="version"/> as the LAST step
            /// so captured dispatch closures observe invalidation.
            /// </summary>
            void DxMessaging.Core.Internal.IHandlerActionCache.Reset()
            {
                entries.Clear();
                insertionOrder.Clear();
                cache.Clear();
                lastSeenVersion = -1;
                lastSeenEmissionId = -1;
                prefreezeInvocationCount = 0;
                unchecked
                {
                    ++version;
                }
            }
        }

        internal sealed class UntargetedDispatchLink<T>
            where T : IMessage
        {
            private readonly TypedHandler<T> typedHandler;
            internal readonly long capturedGeneration;

            internal UntargetedDispatchLink(TypedHandler<T> typedHandler, long capturedGeneration)
            {
                this.typedHandler = typedHandler;
                this.capturedGeneration = capturedGeneration;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal void Invoke(
                MessageHandler messageHandler,
                ref T message,
                int priority,
                long emissionId
            )
            {
                // Generation guard: 1 field read + 1 compare per dispatch on the hot path.
                // Sits at the top of Invoke so reclaimed wrappers return before handler-slot
                // walks when the outer wrapper has been reclaimed.
                if (typedHandler._outerGeneration != capturedGeneration)
                {
                    return;
                }

                if (!messageHandler.active)
                {
                    return;
                }

                typedHandler.HandleUntargeted(ref message, priority, emissionId);
            }
        }

        internal sealed class UntargetedPostDispatchLink<TMessage>
            where TMessage : IMessage
        {
            private readonly TypedHandler<TMessage> typedHandler;
            internal readonly long capturedGeneration;

            internal UntargetedPostDispatchLink(
                TypedHandler<TMessage> typedHandler,
                long capturedGeneration
            )
            {
                this.typedHandler = typedHandler;
                this.capturedGeneration = capturedGeneration;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal void Invoke(
                MessageHandler messageHandler,
                ref TMessage message,
                int priority,
                long emissionId
            )
            {
                // Generation guard: 1 field read + 1 compare per dispatch on the hot path.
                // Sits at the top of Invoke so reclaimed wrappers return before handler-slot
                // walks when the outer wrapper has been reclaimed.
                if (typedHandler._outerGeneration != capturedGeneration)
                {
                    return;
                }

                if (!messageHandler.active)
                {
                    return;
                }

                typedHandler.HandleUntargetedPostProcessing(ref message, priority, emissionId);
            }
        }

        internal sealed class TargetedDispatchLink<TMessage>
            where TMessage : IMessage
        {
            private readonly TypedHandler<TMessage> typedHandler;
            internal readonly long capturedGeneration;

            internal TargetedDispatchLink(
                TypedHandler<TMessage> typedHandler,
                long capturedGeneration
            )
            {
                this.typedHandler = typedHandler;
                this.capturedGeneration = capturedGeneration;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal void Invoke(
                MessageHandler messageHandler,
                ref InstanceId target,
                ref TMessage message,
                int priority,
                long emissionId
            )
            {
                // Generation guard: 1 field read + 1 compare per dispatch on the hot path.
                // Sits at the top of Invoke so reclaimed wrappers return before handler-slot
                // walks when the outer wrapper has been reclaimed.
                if (typedHandler._outerGeneration != capturedGeneration)
                {
                    return;
                }

                typedHandler.HandleTargeted(ref target, ref message, priority, emissionId);
            }
        }

        internal sealed class TargetedPostDispatchLink<TMessage>
            where TMessage : IMessage
        {
            private readonly TypedHandler<TMessage> typedHandler;
            internal readonly long capturedGeneration;

            internal TargetedPostDispatchLink(
                TypedHandler<TMessage> typedHandler,
                long capturedGeneration
            )
            {
                this.typedHandler = typedHandler;
                this.capturedGeneration = capturedGeneration;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal void Invoke(
                MessageHandler messageHandler,
                ref InstanceId target,
                ref TMessage message,
                int priority,
                long emissionId
            )
            {
                // Generation guard: 1 field read + 1 compare per dispatch on the hot path.
                // Sits at the top of Invoke so reclaimed wrappers return before handler-slot
                // walks when the outer wrapper has been reclaimed.
                if (typedHandler._outerGeneration != capturedGeneration)
                {
                    return;
                }

                typedHandler.HandleTargetedPostProcessing(
                    ref target,
                    ref message,
                    priority,
                    emissionId
                );
            }
        }

        internal sealed class TargetedWithoutTargetingDispatchLink<TMessage>
            where TMessage : IMessage
        {
            private readonly TypedHandler<TMessage> typedHandler;
            internal readonly long capturedGeneration;

            internal TargetedWithoutTargetingDispatchLink(
                TypedHandler<TMessage> typedHandler,
                long capturedGeneration
            )
            {
                this.typedHandler = typedHandler;
                this.capturedGeneration = capturedGeneration;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal void Invoke(
                MessageHandler messageHandler,
                ref InstanceId target,
                ref TMessage message,
                int priority,
                long emissionId
            )
            {
                // Generation guard: 1 field read + 1 compare per dispatch on the hot path.
                // Sits at the top of Invoke so reclaimed wrappers return before handler-slot
                // walks when the outer wrapper has been reclaimed.
                if (typedHandler._outerGeneration != capturedGeneration)
                {
                    return;
                }

                typedHandler.HandleTargetedWithoutTargeting(
                    ref target,
                    ref message,
                    priority,
                    emissionId
                );
            }
        }

        internal sealed class TargetedWithoutTargetingPostDispatchLink<TMessage>
            where TMessage : IMessage
        {
            private readonly TypedHandler<TMessage> typedHandler;
            internal readonly long capturedGeneration;

            internal TargetedWithoutTargetingPostDispatchLink(
                TypedHandler<TMessage> typedHandler,
                long capturedGeneration
            )
            {
                this.typedHandler = typedHandler;
                this.capturedGeneration = capturedGeneration;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal void Invoke(
                MessageHandler messageHandler,
                ref InstanceId target,
                ref TMessage message,
                int priority,
                long emissionId
            )
            {
                // Generation guard: 1 field read + 1 compare per dispatch on the hot path.
                // Sits at the top of Invoke so reclaimed wrappers return before handler-slot
                // walks when the outer wrapper has been reclaimed.
                if (typedHandler._outerGeneration != capturedGeneration)
                {
                    return;
                }

                typedHandler.HandleTargetedWithoutTargetingPostProcessing(
                    ref target,
                    ref message,
                    priority,
                    emissionId
                );
            }
        }

        internal sealed class BroadcastDispatchLink<TMessage>
            where TMessage : IMessage
        {
            private readonly TypedHandler<TMessage> typedHandler;
            internal readonly long capturedGeneration;

            internal BroadcastDispatchLink(
                TypedHandler<TMessage> typedHandler,
                long capturedGeneration
            )
            {
                this.typedHandler = typedHandler;
                this.capturedGeneration = capturedGeneration;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal void Invoke(
                MessageHandler messageHandler,
                ref InstanceId source,
                ref TMessage message,
                int priority,
                long emissionId
            )
            {
                // Generation guard: 1 field read + 1 compare per dispatch on the hot path.
                // Sits at the top of Invoke so reclaimed wrappers return before handler-slot
                // walks when the outer wrapper has been reclaimed.
                if (typedHandler._outerGeneration != capturedGeneration)
                {
                    return;
                }

                typedHandler.HandleSourcedBroadcast(ref source, ref message, priority, emissionId);
            }
        }

        internal sealed class BroadcastPostDispatchLink<TMessage>
            where TMessage : IMessage
        {
            private readonly TypedHandler<TMessage> typedHandler;
            internal readonly long capturedGeneration;

            internal BroadcastPostDispatchLink(
                TypedHandler<TMessage> typedHandler,
                long capturedGeneration
            )
            {
                this.typedHandler = typedHandler;
                this.capturedGeneration = capturedGeneration;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal void Invoke(
                MessageHandler messageHandler,
                ref InstanceId source,
                ref TMessage message,
                int priority,
                long emissionId
            )
            {
                // Generation guard: 1 field read + 1 compare per dispatch on the hot path.
                // Sits at the top of Invoke so reclaimed wrappers return before handler-slot
                // walks when the outer wrapper has been reclaimed.
                if (typedHandler._outerGeneration != capturedGeneration)
                {
                    return;
                }

                typedHandler.HandleSourcedBroadcastPostProcessing(
                    ref source,
                    ref message,
                    priority,
                    emissionId
                );
            }
        }

        internal sealed class BroadcastWithoutSourceDispatchLink<TMessage>
            where TMessage : IMessage
        {
            private readonly TypedHandler<TMessage> typedHandler;
            internal readonly long capturedGeneration;

            internal BroadcastWithoutSourceDispatchLink(
                TypedHandler<TMessage> typedHandler,
                long capturedGeneration
            )
            {
                this.typedHandler = typedHandler;
                this.capturedGeneration = capturedGeneration;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal void Invoke(
                MessageHandler messageHandler,
                ref InstanceId source,
                ref TMessage message,
                int priority,
                long emissionId
            )
            {
                // Generation guard: 1 field read + 1 compare per dispatch on the hot path.
                // Sits at the top of Invoke so reclaimed wrappers return before handler-slot
                // walks when the outer wrapper has been reclaimed.
                if (typedHandler._outerGeneration != capturedGeneration)
                {
                    return;
                }

                typedHandler.HandleSourcedBroadcastWithoutSource(
                    ref source,
                    ref message,
                    priority,
                    emissionId
                );
            }
        }

        internal sealed class BroadcastWithoutSourcePostDispatchLink<TMessage>
            where TMessage : IMessage
        {
            private readonly TypedHandler<TMessage> typedHandler;
            internal readonly long capturedGeneration;

            internal BroadcastWithoutSourcePostDispatchLink(
                TypedHandler<TMessage> typedHandler,
                long capturedGeneration
            )
            {
                this.typedHandler = typedHandler;
                this.capturedGeneration = capturedGeneration;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal void Invoke(
                MessageHandler messageHandler,
                ref InstanceId source,
                ref TMessage message,
                int priority,
                long emissionId
            )
            {
                // Generation guard: 1 field read + 1 compare per dispatch on the hot path.
                // Sits at the top of Invoke so reclaimed wrappers return before handler-slot
                // walks when the outer wrapper has been reclaimed.
                if (typedHandler._outerGeneration != capturedGeneration)
                {
                    return;
                }

                typedHandler.HandleBroadcastWithoutSourcePostProcessing(
                    ref source,
                    ref message,
                    priority,
                    emissionId
                );
            }
        }

        /// <summary>
        /// One-size-fits-all wrapper around all possible Messaging sinks for a particular MessageHandler & MessageType.
        /// </summary>
        /// <typeparam name="T">Message type that this Handler exists to serve.</typeparam>
        internal sealed class TypedHandler<T> : ITypedHandlerSlotSweeper
            where T : IMessage
        {
            // Typed storage: 20 typed slots + 6 global slots + 10 dispatch
            // links. The legacy named fields were deleted so new handler
            // variants must pick an explicit axis-indexed slot.
            internal readonly TypedSlot<T>[] _slots = new TypedSlot<T>[TypedSlotIndex.Length];
            internal readonly TypedGlobalSlot[] _globalSlots = new TypedGlobalSlot[
                TypedGlobalSlotIndex.Length
            ];
            internal readonly object[] _dispatchLinks = new object[TypedDispatchLinkIndex.Length];

            // Constructor exists solely so the [Conditional("DEBUG")]
            // validator below runs at construction time. In Release builds
            // the Conditional attribute strips the call site, leaving an
            // empty constructor body that the JIT collapses to the
            // equivalent of the implicit default. Mirrors the
            // MessageBus.ValidateSinkArrays() pattern.
            internal TypedHandler()
            {
                ValidateSlotArrays();
            }

            internal long _outerGeneration;
            internal bool _markedForOuterRemoval;

            int ITypedHandlerSlotSweeper.MessageTypeIndex
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => MessageHelperIndexer<T>.SequentialId;
            }

            bool ITypedHandlerSlotSweeper.MarkedForOuterRemoval
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => _markedForOuterRemoval;
            }

            [Conditional("DEBUG")]
            private void ValidateSlotArrays()
            {
                if (_slots.Length != TypedSlotIndex.Length)
                {
                    throw new InvalidOperationException(
                        $"_slots length is {_slots.Length} but TypedSlotIndex.Length is {TypedSlotIndex.Length}."
                    );
                }
                if (_globalSlots.Length != TypedGlobalSlotIndex.Length)
                {
                    throw new InvalidOperationException(
                        $"_globalSlots length is {_globalSlots.Length} but TypedGlobalSlotIndex.Length is {TypedGlobalSlotIndex.Length}."
                    );
                }
                if (_dispatchLinks.Length != TypedDispatchLinkIndex.Length)
                {
                    throw new InvalidOperationException(
                        $"_dispatchLinks length is {_dispatchLinks.Length} but TypedDispatchLinkIndex.Length is {TypedDispatchLinkIndex.Length}."
                    );
                }
                // Lazy registration writers update the slot arrays; this assertion still
                // holds at construction (slots populate on first register,
                // not on construction). The invariant flips meaning -- not
                // the message -- when writers land.
                for (int i = 0; i < _slots.Length; ++i)
                {
                    if (_slots[i] != null)
                    {
                        throw new InvalidOperationException(
                            $"_slots[{i}] is non-null at construction; expected null per TypedSlotIndex because slots populate lazily on first registration."
                        );
                    }
                }
                for (int i = 0; i < _globalSlots.Length; ++i)
                {
                    if (_globalSlots[i] != null)
                    {
                        throw new InvalidOperationException(
                            $"_globalSlots[{i}] is non-null at construction; expected null per TypedGlobalSlotIndex because slots populate lazily on first registration."
                        );
                    }
                }
                for (int i = 0; i < _dispatchLinks.Length; ++i)
                {
                    if (_dispatchLinks[i] != null)
                    {
                        throw new InvalidOperationException(
                            $"_dispatchLinks[{i}] is non-null at construction; expected null per TypedDispatchLinkIndex because links populate lazily on first dispatch-link request."
                        );
                    }
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private TypedSlot<T> GetOrCreateSlot(int index, bool requiresContext)
            {
                TypedSlot<T> slot = _slots[index];
                if (slot == null)
                {
                    slot = new TypedSlot<T>(requiresContext);
                    _slots[index] = slot;
                }

                return slot;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal Dictionary<int, IHandlerActionCache> GetOrCreatePriorityHandlers(
                int index,
                bool requiresContext
            )
            {
                return GetOrCreateSlot(index, requiresContext).byPriority;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal Dictionary<int, IHandlerActionCache> GetPriorityHandlers(int index)
            {
                return _slots[index]?.byPriority;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal Dictionary<
                InstanceId,
                Dictionary<int, IHandlerActionCache>
            > GetOrCreateContextHandlers(int index)
            {
                TypedSlot<T> slot = GetOrCreateSlot(index, requiresContext: true);
                slot.byContext ??= DxPools.TypedHandlerContextDicts.Rent();
                return slot.byContext;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal Dictionary<
                InstanceId,
                Dictionary<int, IHandlerActionCache>
            > GetContextHandlers(int index)
            {
                return _slots[index]?.byContext;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal TypedGlobalSlot GetOrCreateGlobalSlot(int index)
            {
                TypedGlobalSlot slot = _globalSlots[index];
                if (slot == null)
                {
                    slot = new TypedGlobalSlot();
                    _globalSlots[index] = slot;
                }

                return slot;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal HandlerActionCache<TU> GetGlobalCache<TU>(int index)
            {
                return _globalSlots[index]?.cache as HandlerActionCache<TU>;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private TypedSlot<T> FindPrioritySlot(Dictionary<int, IHandlerActionCache> handlers)
            {
                for (int i = 0; i < _slots.Length; ++i)
                {
                    TypedSlot<T> slot = _slots[i];
                    if (slot != null && ReferenceEquals(slot.byPriority, handlers))
                    {
                        return slot;
                    }
                }

                return null;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private TypedSlot<T> FindContextSlot(
                Dictionary<InstanceId, Dictionary<int, IHandlerActionCache>> handlersByContext
            )
            {
                for (int i = 0; i < _slots.Length; ++i)
                {
                    TypedSlot<T> slot = _slots[i];
                    if (slot != null && ReferenceEquals(slot.byContext, handlersByContext))
                    {
                        return slot;
                    }
                }

                return null;
            }

            int ITypedHandlerSlotSweeper.ResetEmptySlotsForSweep()
            {
                _markedForOuterRemoval = false;
                int resetCount = 0;
                for (int i = 0; i < _slots.Length; ++i)
                {
                    TypedSlot<T> slot = _slots[i];
                    if (slot != null && slot.IsEmpty)
                    {
                        slot.Reset();
                        _slots[i] = null;
                        resetCount++;
                    }
                }

                for (int i = 0; i < _globalSlots.Length; ++i)
                {
                    TypedGlobalSlot slot = _globalSlots[i];
                    if (slot != null && slot.IsEmpty)
                    {
                        slot.Reset();
                        _globalSlots[i] = null;
                        resetCount++;
                    }
                }

                MarkForOuterRemovalIfEmpty();
                return resetCount;
            }

            int ITypedHandlerSlotSweeper.ResetAllSlotsForBusReset()
            {
                _markedForOuterRemoval = false;
                int resetCount = 0;
                for (int i = 0; i < _slots.Length; ++i)
                {
                    TypedSlot<T> slot = _slots[i];
                    if (slot != null)
                    {
                        slot.Reset();
                        _slots[i] = null;
                        resetCount++;
                    }
                }

                for (int i = 0; i < _globalSlots.Length; ++i)
                {
                    TypedGlobalSlot slot = _globalSlots[i];
                    if (slot != null)
                    {
                        slot.Reset();
                        _globalSlots[i] = null;
                        resetCount++;
                    }
                }

                ClearDispatchLinks();
                unchecked
                {
                    ++_outerGeneration;
                }
                return resetCount;
            }

            int ITypedHandlerSlotSweeper.CountEmptySlotsForSweep()
            {
                int count = 0;
                for (int i = 0; i < _slots.Length; ++i)
                {
                    TypedSlot<T> slot = _slots[i];
                    if (slot != null && slot.IsEmpty)
                    {
                        count++;
                    }
                }

                for (int i = 0; i < _globalSlots.Length; ++i)
                {
                    TypedGlobalSlot slot = _globalSlots[i];
                    if (slot != null && slot.IsEmpty)
                    {
                        count++;
                    }
                }

                return count;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void MarkForOuterRemovalIfEmpty()
            {
                if (HasLiveSlots())
                {
                    return;
                }

                ClearDispatchLinks();
                _markedForOuterRemoval = true;
                unchecked
                {
                    ++_outerGeneration;
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private bool HasLiveSlots()
            {
                for (int i = 0; i < _slots.Length; ++i)
                {
                    if (_slots[i] != null)
                    {
                        return true;
                    }
                }

                for (int i = 0; i < _globalSlots.Length; ++i)
                {
                    if (_globalSlots[i] != null)
                    {
                        return true;
                    }
                }

                return false;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void ClearDispatchLinks()
            {
                for (int i = 0; i < _dispatchLinks.Length; ++i)
                {
                    _dispatchLinks[i] = null;
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal UntargetedDispatchLink<T> GetOrCreateUntargetedLink()
            {
                UntargetedDispatchLink<T> link =
                    _dispatchLinks[TypedDispatchLinkIndex.UntargetedHandle]
                    as UntargetedDispatchLink<T>;
                if (link == null)
                {
                    link = new UntargetedDispatchLink<T>(this, _outerGeneration);
                    _dispatchLinks[TypedDispatchLinkIndex.UntargetedHandle] = link;
                }

                return link;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal UntargetedPostDispatchLink<T> GetOrCreateUntargetedPostLink()
            {
                UntargetedPostDispatchLink<T> link =
                    _dispatchLinks[TypedDispatchLinkIndex.UntargetedPostProcess]
                    as UntargetedPostDispatchLink<T>;
                if (link == null)
                {
                    link = new UntargetedPostDispatchLink<T>(this, _outerGeneration);
                    _dispatchLinks[TypedDispatchLinkIndex.UntargetedPostProcess] = link;
                }

                return link;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal TargetedDispatchLink<T> GetOrCreateTargetedLink()
            {
                TargetedDispatchLink<T> link =
                    _dispatchLinks[TypedDispatchLinkIndex.TargetedHandle]
                    as TargetedDispatchLink<T>;
                if (link == null)
                {
                    link = new TargetedDispatchLink<T>(this, _outerGeneration);
                    _dispatchLinks[TypedDispatchLinkIndex.TargetedHandle] = link;
                }

                return link;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal TargetedPostDispatchLink<T> GetOrCreateTargetedPostLink()
            {
                TargetedPostDispatchLink<T> link =
                    _dispatchLinks[TypedDispatchLinkIndex.TargetedPostProcess]
                    as TargetedPostDispatchLink<T>;
                if (link == null)
                {
                    link = new TargetedPostDispatchLink<T>(this, _outerGeneration);
                    _dispatchLinks[TypedDispatchLinkIndex.TargetedPostProcess] = link;
                }

                return link;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal TargetedWithoutTargetingDispatchLink<T> GetOrCreateTargetedWithoutTargetingLink()
            {
                TargetedWithoutTargetingDispatchLink<T> link =
                    _dispatchLinks[TypedDispatchLinkIndex.TargetedHandleWithoutContext]
                    as TargetedWithoutTargetingDispatchLink<T>;
                if (link == null)
                {
                    link = new TargetedWithoutTargetingDispatchLink<T>(this, _outerGeneration);
                    _dispatchLinks[TypedDispatchLinkIndex.TargetedHandleWithoutContext] = link;
                }

                return link;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal TargetedWithoutTargetingPostDispatchLink<T> GetOrCreateTargetedWithoutTargetingPostLink()
            {
                TargetedWithoutTargetingPostDispatchLink<T> link =
                    _dispatchLinks[TypedDispatchLinkIndex.TargetedPostProcessWithoutContext]
                    as TargetedWithoutTargetingPostDispatchLink<T>;
                if (link == null)
                {
                    link = new TargetedWithoutTargetingPostDispatchLink<T>(this, _outerGeneration);
                    _dispatchLinks[TypedDispatchLinkIndex.TargetedPostProcessWithoutContext] = link;
                }

                return link;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal BroadcastDispatchLink<T> GetOrCreateBroadcastLink()
            {
                BroadcastDispatchLink<T> link =
                    _dispatchLinks[TypedDispatchLinkIndex.BroadcastHandle]
                    as BroadcastDispatchLink<T>;
                if (link == null)
                {
                    link = new BroadcastDispatchLink<T>(this, _outerGeneration);
                    _dispatchLinks[TypedDispatchLinkIndex.BroadcastHandle] = link;
                }

                return link;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal BroadcastPostDispatchLink<T> GetOrCreateBroadcastPostLink()
            {
                BroadcastPostDispatchLink<T> link =
                    _dispatchLinks[TypedDispatchLinkIndex.BroadcastPostProcess]
                    as BroadcastPostDispatchLink<T>;
                if (link == null)
                {
                    link = new BroadcastPostDispatchLink<T>(this, _outerGeneration);
                    _dispatchLinks[TypedDispatchLinkIndex.BroadcastPostProcess] = link;
                }

                return link;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal BroadcastWithoutSourceDispatchLink<T> GetOrCreateBroadcastWithoutSourceLink()
            {
                BroadcastWithoutSourceDispatchLink<T> link =
                    _dispatchLinks[TypedDispatchLinkIndex.BroadcastHandleWithoutContext]
                    as BroadcastWithoutSourceDispatchLink<T>;
                if (link == null)
                {
                    link = new BroadcastWithoutSourceDispatchLink<T>(this, _outerGeneration);
                    _dispatchLinks[TypedDispatchLinkIndex.BroadcastHandleWithoutContext] = link;
                }

                return link;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal BroadcastWithoutSourcePostDispatchLink<T> GetOrCreateBroadcastWithoutSourcePostLink()
            {
                BroadcastWithoutSourcePostDispatchLink<T> link =
                    _dispatchLinks[TypedDispatchLinkIndex.BroadcastPostProcessWithoutContext]
                    as BroadcastWithoutSourcePostDispatchLink<T>;
                if (link == null)
                {
                    link = new BroadcastWithoutSourcePostDispatchLink<T>(this, _outerGeneration);
                    _dispatchLinks[TypedDispatchLinkIndex.BroadcastPostProcessWithoutContext] =
                        link;
                }

                return link;
            }

            /// <summary>
            /// Emits the UntargetedMessage to all subscribed listeners.
            /// </summary>
            /// <param name="message">Message to emit.</param>
            /// <param name="priority">Priority at which to run the handlers.</param>
            public void HandleUntargeted(ref T message, int priority, long emissionId)
            {
                RunFastHandlers(
                    GetPriorityHandlers(TypedSlotIndex.UntargetedHandleFast),
                    ref message,
                    priority,
                    emissionId
                );
                RunHandlers(
                    GetPriorityHandlers(TypedSlotIndex.UntargetedHandleDefault),
                    ref message,
                    priority,
                    emissionId
                );
            }

            /// <summary>
            /// Emits the TargetedMessage to all subscribed listeners.
            /// </summary>
            /// <param name="target">Target the message is for.</param>
            /// <param name="message">Message to emit.</param>
            /// <param name="priority">Priority at which to run the handlers.</param>
            public void HandleTargeted(
                ref InstanceId target,
                ref T message,
                int priority,
                long emissionId
            )
            {
                RunFastHandlersWithContext(
                    ref target,
                    GetContextHandlers(TypedSlotIndex.TargetedHandleFast),
                    ref message,
                    priority,
                    emissionId
                );
                RunHandlersWithContext(
                    ref target,
                    GetContextHandlers(TypedSlotIndex.TargetedHandleDefault),
                    ref message,
                    priority,
                    emissionId
                );
            }

            /// <summary>
            /// Emits the TargetedMessage without targeting to all subscribed listeners.
            /// </summary>
            /// <param name="target">Target the message is for.</param>
            /// <param name="message">Message to emit.</param>
            /// <param name="priority">Priority at which to run the handlers.</param>
            public void HandleTargetedWithoutTargeting(
                ref InstanceId target,
                ref T message,
                int priority,
                long emissionId
            )
            {
                RunFastHandlers(
                    ref target,
                    GetPriorityHandlers(TypedSlotIndex.TargetedHandleWithoutContextFast),
                    ref message,
                    priority,
                    emissionId
                );
                RunHandlers(
                    ref target,
                    GetPriorityHandlers(TypedSlotIndex.TargetedHandleWithoutContext),
                    ref message,
                    priority,
                    emissionId
                );
            }

            /// <summary>
            /// Emits the BroadcastMessage to all subscribed listeners.
            /// </summary>
            /// <param name="source">Source the message is from.</param>
            /// <param name="message">Message to emit.</param>
            /// <param name="priority">Priority at which to run the handlers.</param>
            public void HandleSourcedBroadcast(
                ref InstanceId source,
                ref T message,
                int priority,
                long emissionId
            )
            {
                RunFastHandlersWithContext(
                    ref source,
                    GetContextHandlers(TypedSlotIndex.BroadcastHandleFast),
                    ref message,
                    priority,
                    emissionId
                );
                RunHandlersWithContext(
                    ref source,
                    GetContextHandlers(TypedSlotIndex.BroadcastHandleDefault),
                    ref message,
                    priority,
                    emissionId
                );
            }

            /// <summary>
            /// Emits the BroadcastMessage without a source to all subscribed listeners.
            /// </summary>
            /// <param name="source">Source the message is from.</param>
            /// <param name="message">Message to emit.</param>
            /// <param name="priority">Priority at which to run the handlers.</param>
            public void HandleSourcedBroadcastWithoutSource(
                ref InstanceId source,
                ref T message,
                int priority,
                long emissionId
            )
            {
                RunFastHandlers(
                    ref source,
                    GetPriorityHandlers(TypedSlotIndex.BroadcastHandleWithoutContextFast),
                    ref message,
                    priority,
                    emissionId
                );
                RunHandlers(
                    ref source,
                    GetPriorityHandlers(TypedSlotIndex.BroadcastHandleWithoutContext),
                    ref message,
                    priority,
                    emissionId
                );
            }

            /// <summary>
            /// Emits the UntargetedMessage to all global listeners.
            /// </summary>
            /// <param name="message">Message to emit.</param>
            public void HandleGlobalUntargeted(ref IUntargetedMessage message, long emissionId)
            {
                HandlerActionCache<FastHandler<IUntargetedMessage>> fastCache = GetGlobalCache<
                    FastHandler<IUntargetedMessage>
                >(TypedGlobalSlotIndex.UntargetedFast);
                RunFastHandlers(fastCache, ref message, emissionId);
                HandlerActionCache<Action<IUntargetedMessage>> cache = GetGlobalCache<
                    Action<IUntargetedMessage>
                >(TypedGlobalSlotIndex.UntargetedDefault);
                // Live-count fast path. Cross-handler in-flight snapshot
                // semantics do not apply to the global accept-all path: the
                // bus dispatch loop calls PrefreezeGlobalUntargetedForEmission
                // lazily per-entry inside InvokeGlobalUntargetedEntry, after
                // earlier-priority handlers have already run. A sibling
                // MessageHandler that removes this handler's entry mid-emit
                // drains cache.entries before the lazy prefreeze can capture
                // a snapshot, so cache.cache rebuilds from the now-empty
                // entries. Bailing on cache.entries.Count == 0 is therefore
                // equivalent to bailing after GetOrAddNewHandlerStack would
                // return an empty list, and is documented behavior for the
                // global path.
                if (cache?.entries is not { Count: > 0 })
                {
                    return;
                }

                List<Action<IUntargetedMessage>> handlers = GetOrAddNewHandlerStack(
                    cache,
                    emissionId
                );
                int handlersCount = handlers.Count;
                for (int i = 0; i < handlersCount && i < handlers.Count; ++i)
                {
                    handlers[i](message);
                }
            }

            /// <summary>
            /// Emits the TargetedMessage to all global listeners.
            /// </summary>
            /// <param name="target">Target that this message is intended for.</param>
            /// <param name="message">Message to emit.</param>
            public void HandleGlobalTargeted(
                ref InstanceId target,
                ref ITargetedMessage message,
                long emissionId
            )
            {
                HandlerActionCache<FastHandlerWithContext<ITargetedMessage>> fastCache =
                    GetGlobalCache<FastHandlerWithContext<ITargetedMessage>>(
                        TypedGlobalSlotIndex.TargetedFast
                    );
                RunFastHandlers(ref target, fastCache, ref message, emissionId);

                HandlerActionCache<Action<InstanceId, ITargetedMessage>> cache = GetGlobalCache<
                    Action<InstanceId, ITargetedMessage>
                >(TypedGlobalSlotIndex.TargetedDefault);
                // Live-count fast path. See comment in HandleGlobalUntargeted
                // for why the global accept-all path bails on
                // cache.entries.Count == 0 rather than reading the snapshot.
                if (cache?.entries is not { Count: > 0 })
                {
                    return;
                }

                List<Action<InstanceId, ITargetedMessage>> handlers = GetOrAddNewHandlerStack(
                    cache,
                    emissionId
                );
                int handlersCount = handlers.Count;
                for (int i = 0; i < handlersCount && i < handlers.Count; ++i)
                {
                    handlers[i](target, message);
                }
            }

            /// <summary>
            /// Emits the BroadcastMessage to all global listeners.
            /// </summary>
            /// <param name="source">Source that this message is from.</param>
            /// <param name="message">Message to emit.</param>
            public void HandleGlobalBroadcast(
                ref InstanceId source,
                ref IBroadcastMessage message,
                long emissionId
            )
            {
                HandlerActionCache<FastHandlerWithContext<IBroadcastMessage>> fastCache =
                    GetGlobalCache<FastHandlerWithContext<IBroadcastMessage>>(
                        TypedGlobalSlotIndex.BroadcastFast
                    );
                RunFastHandlers(ref source, fastCache, ref message, emissionId);

                HandlerActionCache<Action<InstanceId, IBroadcastMessage>> cache = GetGlobalCache<
                    Action<InstanceId, IBroadcastMessage>
                >(TypedGlobalSlotIndex.BroadcastDefault);
                // Live-count fast path. See comment in HandleGlobalUntargeted
                // for why the global accept-all path bails on
                // cache.entries.Count == 0 rather than reading the snapshot.
                if (cache?.entries is not { Count: > 0 })
                {
                    return;
                }

                List<Action<InstanceId, IBroadcastMessage>> handlers = GetOrAddNewHandlerStack(
                    cache,
                    emissionId
                );
                int handlersCount = handlers.Count;
                switch (handlersCount)
                {
                    case 1:
                    {
                        handlers[0](source, message);
                        return;
                    }
                    case 2:
                    {
                        handlers[0](source, message);
                        if (handlers.Count < 2)
                        {
                            return;
                        }
                        handlers[1](source, message);
                        return;
                    }
                    case 3:
                    {
                        handlers[0](source, message);
                        if (handlers.Count < 2)
                        {
                            return;
                        }
                        handlers[1](source, message);
                        if (handlers.Count < 3)
                        {
                            return;
                        }
                        handlers[2](source, message);
                        return;
                    }
                    case 4:
                    {
                        handlers[0](source, message);
                        if (handlers.Count < 2)
                        {
                            return;
                        }
                        handlers[1](source, message);
                        if (handlers.Count < 3)
                        {
                            return;
                        }
                        handlers[2](source, message);
                        if (handlers.Count < 4)
                        {
                            return;
                        }
                        handlers[3](source, message);
                        return;
                    }
                    case 5:
                    {
                        handlers[0](source, message);
                        if (handlers.Count < 2)
                        {
                            return;
                        }
                        handlers[1](source, message);
                        if (handlers.Count < 3)
                        {
                            return;
                        }
                        handlers[2](source, message);
                        if (handlers.Count < 4)
                        {
                            return;
                        }
                        handlers[3](source, message);
                        if (handlers.Count < 5)
                        {
                            return;
                        }
                        handlers[4](source, message);
                        return;
                    }
                }

                for (int i = 0; i < handlersCount && i < handlers.Count; ++i)
                {
                    handlers[i](source, message);
                }
            }

            /// <summary>
            /// Runs untargeted post-processing handlers for the supplied message.
            /// </summary>
            /// <param name="message">Message being processed.</param>
            /// <param name="priority">Priority bucket currently executing.</param>
            /// <param name="emissionId">Emission identifier used to cache handler stacks.</param>
            public void HandleUntargetedPostProcessing(ref T message, int priority, long emissionId)
            {
                RunFastHandlers(
                    GetPriorityHandlers(TypedSlotIndex.UntargetedPostProcessFast),
                    ref message,
                    priority,
                    emissionId
                );
                RunHandlers(
                    GetPriorityHandlers(TypedSlotIndex.UntargetedPostProcessDefault),
                    ref message,
                    priority,
                    emissionId
                );
            }

            /// <summary>
            /// Runs targeted post-processing handlers for the supplied message and recipient.
            /// </summary>
            /// <param name="target">Recipient of the message.</param>
            /// <param name="message">Message being processed.</param>
            /// <param name="priority">Priority bucket currently executing.</param>
            /// <param name="emissionId">Emission identifier used to cache handler stacks.</param>
            public void HandleTargetedPostProcessing(
                ref InstanceId target,
                ref T message,
                int priority,
                long emissionId
            )
            {
                RunFastHandlersWithContext(
                    ref target,
                    GetContextHandlers(TypedSlotIndex.TargetedPostProcessFast),
                    ref message,
                    priority,
                    emissionId
                );
                RunHandlersWithContext(
                    ref target,
                    GetContextHandlers(TypedSlotIndex.TargetedPostProcessDefault),
                    ref message,
                    priority,
                    emissionId
                );
            }

            /// <summary>
            /// Runs targeted post-processing handlers that do not require a <see cref="InstanceId"/> target binding.
            /// </summary>
            /// <param name="target">Recipient of the message.</param>
            /// <param name="message">Message being processed.</param>
            /// <param name="priority">Priority bucket currently executing.</param>
            /// <param name="emissionId">Emission identifier used to cache handler stacks.</param>
            public void HandleTargetedWithoutTargetingPostProcessing(
                ref InstanceId target,
                ref T message,
                int priority,
                long emissionId
            )
            {
                RunFastHandlersWithContext(
                    ref target,
                    GetPriorityHandlers(TypedSlotIndex.TargetedPostProcessWithoutContextFast),
                    ref message,
                    priority,
                    emissionId
                );
                RunHandlers(
                    ref target,
                    GetPriorityHandlers(TypedSlotIndex.TargetedPostProcessWithoutContext),
                    ref message,
                    priority,
                    emissionId
                );
            }

            /// <summary>
            /// Runs broadcast post-processing handlers that expect a concrete source identifier.
            /// </summary>
            /// <param name="source">Origin of the message.</param>
            /// <param name="message">Message being processed.</param>
            /// <param name="priority">Priority bucket currently executing.</param>
            /// <param name="emissionId">Emission identifier used to cache handler stacks.</param>
            public void HandleSourcedBroadcastPostProcessing(
                ref InstanceId source,
                ref T message,
                int priority,
                long emissionId
            )
            {
                RunFastHandlersWithContext(
                    ref source,
                    GetContextHandlers(TypedSlotIndex.BroadcastPostProcessFast),
                    ref message,
                    priority,
                    emissionId
                );
                RunHandlersWithContext(
                    ref source,
                    GetContextHandlers(TypedSlotIndex.BroadcastPostProcessDefault),
                    ref message,
                    priority,
                    emissionId
                );
            }

            /// <summary>
            /// Runs broadcast post-processing handlers that do not rely on a specific source identifier.
            /// </summary>
            /// <param name="source">Origin of the message.</param>
            /// <param name="message">Message being processed.</param>
            /// <param name="priority">Priority bucket currently executing.</param>
            /// <param name="emissionId">Emission identifier used to cache handler stacks.</param>
            public void HandleBroadcastWithoutSourcePostProcessing(
                ref InstanceId source,
                ref T message,
                int priority,
                long emissionId
            )
            {
                RunFastHandlersWithContext(
                    ref source,
                    GetPriorityHandlers(TypedSlotIndex.BroadcastPostProcessWithoutContextFast),
                    ref message,
                    priority,
                    emissionId
                );
                RunHandlers(
                    ref source,
                    GetPriorityHandlers(TypedSlotIndex.BroadcastPostProcessWithoutContext),
                    ref message,
                    priority,
                    emissionId
                );
            }

            /// <summary>
            /// Adds a TargetedHandler to listen to Messages of the given type, returning a deregistration action.
            /// </summary>
            /// <param name="target">Target the handler is for.</param>
            /// <param name="handler">Relevant MessageHandler.</param>
            /// <param name="deregistration">Deregistration action for the handler.</param>
            /// <param name="priority">Priority at which to add the handler.</param>
            /// <returns>De-registration action to unregister the handler.</returns>
            public Action AddTargetedHandler(
                InstanceId target,
                Action<T> originalHandler,
                Action<T> handler,
                Action deregistration,
                int priority,
                IMessageBus messageBus
            )
            {
                return AddHandlerPreservingPriorityKey(
                    target,
                    GetOrCreateContextHandlers(TypedSlotIndex.TargetedHandleDefault),
                    originalHandler,
                    handler,
                    deregistration,
                    priority,
                    messageBus
                );
            }

            /// <summary>
            /// Adds a fast TargetedHandler to listen to Messages of the given type, returning a deregistration action.
            /// </summary>
            /// <param name="target">Target the handler is for.</param>
            /// <param name="handler">Relevant MessageHandler.</param>
            /// <param name="deregistration">Deregistration action for the handler.</param>
            /// <param name="priority">Priority at which to add the handler.</param>
            /// <returns>De-registration action to unregister the handler.</returns>
            public Action AddTargetedHandler(
                InstanceId target,
                FastHandler<T> originalHandler,
                FastHandler<T> handler,
                Action deregistration,
                int priority,
                IMessageBus messageBus
            )
            {
                return AddHandlerPreservingPriorityKey(
                    target,
                    GetOrCreateContextHandlers(TypedSlotIndex.TargetedHandleFast),
                    originalHandler,
                    handler,
                    deregistration,
                    priority,
                    messageBus
                );
            }

            /// <summary>
            /// Adds a TargetedWithoutTargetingHandler to listen to Messages of the given type, returning a deregistration action.
            /// </summary>
            /// <param name="handler">Relevant MessageHandler.</param>
            /// <param name="deregistration">Deregistration action for the handler.</param>
            /// <param name="priority">Priority at which to add the handler.</param>
            /// <returns>De-registration action to unregister the handler.</returns>
            public Action AddTargetedWithoutTargetingHandler(
                Action<InstanceId, T> originalHandler,
                Action<InstanceId, T> handler,
                Action deregistration,
                int priority,
                IMessageBus messageBus
            )
            {
                return AddHandlerPreservingPriorityKey(
                    GetOrCreatePriorityHandlers(
                        TypedSlotIndex.TargetedHandleWithoutContext,
                        requiresContext: false
                    ),
                    originalHandler,
                    handler,
                    deregistration,
                    priority,
                    messageBus
                );
            }

            /// <summary>
            /// Adds a fast TargetedWithoutTargetingHandler to listen to Messages of the given type, returning a deregistration action.
            /// </summary>
            /// <param name="handler">Relevant MessageHandler.</param>
            /// <param name="deregistration">Deregistration action for the handler.</param>
            /// <param name="priority">Priority at which to add the handler.</param>
            /// <returns>De-registration action to unregister the handler.</returns>
            public Action AddTargetedWithoutTargetingHandler(
                FastHandlerWithContext<T> originalHandler,
                FastHandlerWithContext<T> handler,
                Action deregistration,
                int priority,
                IMessageBus messageBus
            )
            {
                return AddHandlerPreservingPriorityKey(
                    GetOrCreatePriorityHandlers(
                        TypedSlotIndex.TargetedHandleWithoutContextFast,
                        requiresContext: false
                    ),
                    originalHandler,
                    handler,
                    deregistration,
                    priority,
                    messageBus
                );
            }

            /// <summary>
            /// Adds a UntargetedHandler to listen to Messages of the given type, returning a deregistration action.
            /// </summary>
            /// <param name="handler">Relevant MessageHandler.</param>
            /// <param name="deregistration">Deregistration action for the handler.</param>
            /// <param name="priority">Priority at which to add the handler.</param>
            /// <returns>De-registration action to unregister the handler.</returns>
            public Action AddUntargetedHandler(
                Action<T> originalHandler,
                Action<T> handler,
                Action deregistration,
                int priority,
                IMessageBus messageBus
            )
            {
                return AddHandlerPreservingPriorityKey(
                    GetOrCreatePriorityHandlers(
                        TypedSlotIndex.UntargetedHandleDefault,
                        requiresContext: false
                    ),
                    originalHandler,
                    handler,
                    deregistration,
                    priority,
                    messageBus
                );
            }

            /// <summary>
            /// Adds a fast UntargetedHandler to listen to Messages of the given type, returning a deregistration action.
            /// </summary>
            /// <param name="handler">Relevant MessageHandler.</param>
            /// <param name="deregistration">Deregistration action for the handler.</param>
            /// <param name="priority">Priority at which to add the handler.</param>
            /// <returns>De-registration action to unregister the handler.</returns>
            public Action AddUntargetedHandler(
                FastHandler<T> originalHandler,
                FastHandler<T> handler,
                Action deregistration,
                int priority,
                IMessageBus messageBus
            )
            {
                return AddHandlerPreservingPriorityKey(
                    GetOrCreatePriorityHandlers(
                        TypedSlotIndex.UntargetedHandleFast,
                        requiresContext: false
                    ),
                    originalHandler,
                    handler,
                    deregistration,
                    priority,
                    messageBus
                );
            }

            /// <summary>
            /// Adds a SourcedBroadcastHandler to listen to Messages of the given type from an entity, returning a deregistration action.
            /// </summary>
            /// <param name="source">The Source of the handler is for.</param>
            /// <param name="handler">Relevant MessageHandler.</param>
            /// <param name="deregistration">Deregistration action for the handler.</param>
            /// <param name="priority">Priority at which to add the handler.</param>
            /// <returns>De-registration action to unregister the handler.</returns>
            public Action AddSourcedBroadcastHandler(
                InstanceId source,
                Action<T> originalHandler,
                Action<T> handler,
                Action deregistration,
                int priority,
                IMessageBus messageBus
            )
            {
                return AddHandlerPreservingPriorityKey(
                    source,
                    GetOrCreateContextHandlers(TypedSlotIndex.BroadcastHandleDefault),
                    originalHandler,
                    handler,
                    deregistration,
                    priority,
                    messageBus
                );
            }

            /// <summary>
            /// Adds a fast SourcedBroadcastHandler to listen to Messages of the given type from an entity, returning a deregistration action.
            /// </summary>
            /// <param name="source">The Source of the handler is for.</param>
            /// <param name="handler">Relevant MessageHandler.</param>
            /// <param name="deregistration">Deregistration action for the handler.</param>
            /// <param name="priority">Priority at which to add the handler.</param>
            /// <returns>De-registration action to unregister the handler.</returns>
            public Action AddSourcedBroadcastHandler(
                InstanceId source,
                FastHandler<T> originalHandler,
                FastHandler<T> handler,
                Action deregistration,
                int priority,
                IMessageBus messageBus
            )
            {
                return AddHandlerPreservingPriorityKey(
                    source,
                    GetOrCreateContextHandlers(TypedSlotIndex.BroadcastHandleFast),
                    originalHandler,
                    handler,
                    deregistration,
                    priority,
                    messageBus
                );
            }

            /// <summary>
            /// Adds a SourcedBroadcastWithoutSourceHandler to listen to Messages of the given type from an entity, returning a deregistration action.
            /// </summary>
            /// <param name="handler">Relevant MessageHandler.</param>
            /// <param name="deregistration">Deregistration action for the handler.</param>
            /// <param name="priority">Priority at which to add the handler.</param>
            /// <returns>De-registration action to unregister the handler.</returns>
            public Action AddSourcedBroadcastWithoutSourceHandler(
                Action<InstanceId, T> originalHandler,
                Action<InstanceId, T> handler,
                Action deregistration,
                int priority,
                IMessageBus messageBus
            )
            {
                // Preserve the priority bucket during the current emission so frozen snapshots remain valid
                return AddHandlerPreservingPriorityKey(
                    GetOrCreatePriorityHandlers(
                        TypedSlotIndex.BroadcastHandleWithoutContext,
                        requiresContext: false
                    ),
                    originalHandler,
                    handler,
                    deregistration,
                    priority,
                    messageBus
                );
            }

            /// <summary>
            /// Adds a fast SourcedBroadcastWithoutSourceHandler to listen to Messages of the given type from an entity, returning a deregistration action.
            /// </summary>
            /// <param name="handler">Relevant MessageHandler.</param>
            /// <param name="deregistration">Deregistration action for the handler.</param>
            /// <param name="priority">Priority at which to add the handler.</param>
            /// <returns>De-registration action to unregister the handler.</returns>
            public Action AddSourcedBroadcastWithoutSourceHandler(
                FastHandlerWithContext<T> originalHandler,
                FastHandlerWithContext<T> handler,
                Action deregistration,
                int priority,
                IMessageBus messageBus
            )
            {
                // Preserve the priority bucket during the current emission so frozen snapshots remain valid
                return AddHandlerPreservingPriorityKey(
                    GetOrCreatePriorityHandlers(
                        TypedSlotIndex.BroadcastHandleWithoutContextFast,
                        requiresContext: false
                    ),
                    originalHandler,
                    handler,
                    deregistration,
                    priority,
                    messageBus
                );
            }

            /// <summary>
            /// Adds a Global UntargetedHandler to listen to all Untargeted Messages of all types, returning the deregistration action.
            /// </summary>
            /// <param name="handler">Relevant MessageHandler.</param>
            /// <param name="deregistration">Deregistration action for the handler.</param>
            /// <returns>De-registration action to unregister the handler.</returns>
            public Action AddGlobalUntargetedHandler(
                Action<IUntargetedMessage> originalHandler,
                Action<IUntargetedMessage> handler,
                Action deregistration,
                IMessageBus messageBus
            )
            {
                return AddHandler(
                    GetOrCreateGlobalSlot(TypedGlobalSlotIndex.UntargetedDefault),
                    originalHandler,
                    handler,
                    deregistration,
                    messageBus
                );
            }

            /// <summary>
            /// Adds a Global fast UntargetedHandler to listen to all Untargeted Messages of all types, returning the deregistration action.
            /// </summary>
            /// <param name="handler">Relevant MessageHandler.</param>
            /// <param name="deregistration">Deregistration action for the handler.</param>
            /// <returns>De-registration action to unregister the handler.</returns>
            public Action AddGlobalUntargetedHandler(
                FastHandler<IUntargetedMessage> originalHandler,
                FastHandler<IUntargetedMessage> handler,
                Action deregistration,
                IMessageBus messageBus
            )
            {
                return AddHandler(
                    GetOrCreateGlobalSlot(TypedGlobalSlotIndex.UntargetedFast),
                    originalHandler,
                    handler,
                    deregistration,
                    messageBus
                );
            }

            /// <summary>
            /// Adds a Global TargetedHandler to listen to all Targeted Messages of all types for all entities, returning the deregistration action.
            /// </summary>
            /// <param name="handler">Relevant MessageHandler.</param>
            /// <param name="deregistration">Deregistration action for the handler.</param>
            /// <returns>De-registration action to unregister the handler.</returns>
            public Action AddGlobalTargetedHandler(
                Action<InstanceId, ITargetedMessage> originalHandler,
                Action<InstanceId, ITargetedMessage> handler,
                Action deregistration,
                IMessageBus messageBus
            )
            {
                return AddHandler(
                    GetOrCreateGlobalSlot(TypedGlobalSlotIndex.TargetedDefault),
                    originalHandler,
                    handler,
                    deregistration,
                    messageBus
                );
            }

            /// <summary>
            /// Adds a Global fast TargetedHandler to listen to all Targeted Messages of all types for all entities (along with the target instance id), returning the deregistration action.
            /// </summary>
            /// <param name="handler">Relevant MessageHandler.</param>
            /// <param name="deregistration">Deregistration action for the handler.</param>
            /// <returns>De-registration action to unregister the handler.</returns>
            public Action AddGlobalTargetedHandler(
                FastHandlerWithContext<ITargetedMessage> originalHandler,
                FastHandlerWithContext<ITargetedMessage> handler,
                Action deregistration,
                IMessageBus messageBus
            )
            {
                return AddHandler(
                    GetOrCreateGlobalSlot(TypedGlobalSlotIndex.TargetedFast),
                    originalHandler,
                    handler,
                    deregistration,
                    messageBus
                );
            }

            /// <summary>
            /// Adds a Global BroadcastHandler to listen to all Targeted Messages of all types for all entities, returning the deregistration action.
            /// </summary>
            /// <param name="handler">Relevant MessageHandler.</param>
            /// <param name="deregistration">Deregistration action for the handler.</param>
            /// <returns>De-registration action to unregister the handler.</returns>
            public Action AddGlobalBroadcastHandler(
                Action<InstanceId, IBroadcastMessage> originalHandler,
                Action<InstanceId, IBroadcastMessage> handler,
                Action deregistration,
                IMessageBus messageBus
            )
            {
                return AddHandler(
                    GetOrCreateGlobalSlot(TypedGlobalSlotIndex.BroadcastDefault),
                    originalHandler,
                    handler,
                    deregistration,
                    messageBus
                );
            }

            /// <summary>
            /// Adds a Global fast BroadcastHandler to listen to all Targeted Messages of all types for all entities (along with the source instance id), returning the deregistration action.
            /// </summary>
            /// <param name="handler">Relevant MessageHandler.</param>
            /// <param name="deregistration">Deregistration action for the handler.</param>
            /// <returns>De-registration action to unregister the handler.</returns>
            public Action AddGlobalBroadcastHandler(
                FastHandlerWithContext<IBroadcastMessage> originalHandler,
                FastHandlerWithContext<IBroadcastMessage> handler,
                Action deregistration,
                IMessageBus messageBus
            )
            {
                return AddHandler(
                    GetOrCreateGlobalSlot(TypedGlobalSlotIndex.BroadcastFast),
                    originalHandler,
                    handler,
                    deregistration,
                    messageBus
                );
            }

            /// <summary>
            /// Adds an Untargeted post-processor to be called after all other handlers have been called.
            /// </summary>
            /// <param name="handler">Relevant MessageHandler.</param>
            /// <param name="deregistration">Deregistration action for the handler.</param>
            /// <param name="priority">Priority at which to add the handler.</param>
            /// <returns>De-registration action to unregister the handler.</returns>
            public Action AddUntargetedPostProcessor(
                Action<T> originalHandler,
                Action<T> handler,
                Action deregistration,
                int priority,
                IMessageBus messageBus
            )
            {
                return AddHandlerPreservingPriorityKey(
                    GetOrCreatePriorityHandlers(
                        TypedSlotIndex.UntargetedPostProcessDefault,
                        requiresContext: false
                    ),
                    originalHandler,
                    handler,
                    deregistration,
                    priority,
                    messageBus
                );
            }

            /// <summary>
            /// Adds a fast Untargeted post-processor to be called after all other handlers have been called.
            /// </summary>
            /// <param name="handler">Relevant MessageHandler.</param>
            /// <param name="deregistration">Deregistration action for the handler.</param>
            /// <param name="priority">Priority at which to add the handler.</param>
            /// <returns>De-registration action to unregister the handler.</returns>
            public Action AddUntargetedPostProcessor(
                FastHandler<T> originalHandler,
                FastHandler<T> handler,
                Action deregistration,
                int priority,
                IMessageBus messageBus
            )
            {
                return AddHandlerPreservingPriorityKey(
                    GetOrCreatePriorityHandlers(
                        TypedSlotIndex.UntargetedPostProcessFast,
                        requiresContext: false
                    ),
                    originalHandler,
                    handler,
                    deregistration,
                    priority,
                    messageBus
                );
            }

            /// <summary>
            /// Adds a Targeted post-processor to be called after all other handlers have been called.
            /// </summary>
            /// <param name="target">Target the handler is for.</param>
            /// <param name="handler">Relevant MessageHandler.</param>
            /// <param name="deregistration">Deregistration action for the handler.</param>
            /// <param name="priority">Priority at which to add the handler.</param>
            /// <returns>De-registration action to unregister the handler.</returns>
            public Action AddTargetedPostProcessor(
                InstanceId target,
                Action<T> originalHandler,
                Action<T> handler,
                Action deregistration,
                int priority,
                IMessageBus messageBus
            )
            {
                return AddHandlerPreservingPriorityKey(
                    target,
                    GetOrCreateContextHandlers(TypedSlotIndex.TargetedPostProcessDefault),
                    originalHandler,
                    handler,
                    deregistration,
                    priority,
                    messageBus
                );
            }

            /// <summary>
            /// Adds a Targeted post-processor to be called after all other handlers have been called.
            /// </summary>
            /// <param name="target">Target the handler is for.</param>
            /// <param name="handler">Relevant MessageHandler.</param>
            /// <param name="deregistration">Deregistration action for the handler.</param>
            /// <param name="priority">Priority at which to add the handler.</param>
            /// <returns>De-registration action to unregister the handler.</returns>
            public Action AddTargetedPostProcessor(
                InstanceId target,
                FastHandler<T> originalHandler,
                FastHandler<T> handler,
                Action deregistration,
                int priority,
                IMessageBus messageBus
            )
            {
                return AddHandlerPreservingPriorityKey(
                    target,
                    GetOrCreateContextHandlers(TypedSlotIndex.TargetedPostProcessFast),
                    originalHandler,
                    handler,
                    deregistration,
                    priority,
                    messageBus
                );
            }

            /// <summary>
            /// Adds a Targeted post-processor to be called after all other handlers have been called after every message of the given type.
            /// </summary>
            /// <param name="handler">Relevant MessageHandler.</param>
            /// <param name="deregistration">Deregistration action for the handler.</param>
            /// <param name="priority">Priority at which to add the handler.</param>
            /// <returns>De-registration action to unregister the handler.</returns>
            public Action AddTargetedWithoutTargetingPostProcessor(
                Action<InstanceId, T> originalHandler,
                Action<InstanceId, T> handler,
                Action deregistration,
                int priority,
                IMessageBus messageBus
            )
            {
                return AddHandlerPreservingPriorityKey(
                    GetOrCreatePriorityHandlers(
                        TypedSlotIndex.TargetedPostProcessWithoutContext,
                        requiresContext: false
                    ),
                    originalHandler,
                    handler,
                    deregistration,
                    priority,
                    messageBus
                );
            }

            /// <summary>
            /// Adds a Targeted post-processor to be called after all other handlers have been called after every message of the given type.
            /// </summary>
            /// <param name="handler">Relevant MessageHandler.</param>
            /// <param name="deregistration">Deregistration action for the handler.</param>
            /// <param name="priority">Priority at which to add the handler.</param>
            /// <returns>De-registration action to unregister the handler.</returns>
            public Action AddTargetedWithoutTargetingPostProcessor(
                FastHandlerWithContext<T> originalHandler,
                FastHandlerWithContext<T> handler,
                Action deregistration,
                int priority,
                IMessageBus messageBus
            )
            {
                return AddHandlerPreservingPriorityKey(
                    GetOrCreatePriorityHandlers(
                        TypedSlotIndex.TargetedPostProcessWithoutContextFast,
                        requiresContext: false
                    ),
                    originalHandler,
                    handler,
                    deregistration,
                    priority,
                    messageBus
                );
            }

            /// <summary>
            /// Adds a Broadcast post-processor to be called after all other handlers have been called.
            /// </summary>
            /// <param name="source">The Source the handler is for.</param>
            /// <param name="handler">Relevant MessageHandler.</param>
            /// <param name="deregistration">Deregistration action for the handler.</param>
            /// <param name="priority">Priority at which to add the handler.</param>
            /// <returns>De-registration action to unregister the handler.</returns>
            public Action AddBroadcastPostProcessor(
                InstanceId source,
                Action<T> originalHandler,
                Action<T> handler,
                Action deregistration,
                int priority,
                IMessageBus messageBus
            )
            {
                return AddHandlerPreservingPriorityKey(
                    source,
                    GetOrCreateContextHandlers(TypedSlotIndex.BroadcastPostProcessDefault),
                    originalHandler,
                    handler,
                    deregistration,
                    priority,
                    messageBus
                );
            }

            /// <summary>
            /// Adds a fast Broadcast post-processor to be called after all other handlers have been called.
            /// </summary>
            /// <param name="source">The Source the handler is for.</param>
            /// <param name="handler">Relevant MessageHandler.</param>
            /// <param name="deregistration">Deregistration action for the handler.</param>
            /// <param name="priority">Priority at which to add the handler.</param>
            /// <returns>De-registration action to unregister the handler.</returns>
            public Action AddBroadcastPostProcessor(
                InstanceId source,
                FastHandler<T> originalHandler,
                FastHandler<T> handler,
                Action deregistration,
                int priority,
                IMessageBus messageBus
            )
            {
                return AddHandlerPreservingPriorityKey(
                    source,
                    GetOrCreateContextHandlers(TypedSlotIndex.BroadcastPostProcessFast),
                    originalHandler,
                    handler,
                    deregistration,
                    priority,
                    messageBus
                );
            }

            /// <summary>
            /// Adds a Broadcast post-processor to be called after all other handlers have been called for every message of the given type.
            /// </summary>
            /// <param name="handler">Relevant MessageHandler.</param>
            /// <param name="deregistration">Deregistration action for the handler.</param>
            /// <param name="priority">Priority at which to add the handler.</param>
            /// <returns>De-registration action to unregister the handler.</returns>
            public Action AddBroadcastWithoutSourcePostProcessor(
                Action<InstanceId, T> originalHandler,
                Action<InstanceId, T> handler,
                Action deregistration,
                int priority,
                IMessageBus messageBus
            )
            {
                return AddHandlerPreservingPriorityKey(
                    GetOrCreatePriorityHandlers(
                        TypedSlotIndex.BroadcastPostProcessWithoutContext,
                        requiresContext: false
                    ),
                    originalHandler,
                    handler,
                    deregistration,
                    priority,
                    messageBus
                );
            }

            /// <summary>
            /// Adds a fast Broadcast post-processor to be called after all other handlers have been called.
            /// </summary>
            /// <param name="handler">Relevant MessageHandler.</param>
            /// <param name="deregistration">Deregistration action for the handler.</param>
            /// <param name="priority">Priority at which to add the handler.</param>
            /// <returns>De-registration action to unregister the handler.</returns>
            public Action AddBroadcastWithoutSourcePostProcessor(
                FastHandlerWithContext<T> originalHandler,
                FastHandlerWithContext<T> handler,
                Action deregistration,
                int priority,
                IMessageBus messageBus
            )
            {
                return AddHandlerPreservingPriorityKey(
                    GetOrCreatePriorityHandlers(
                        TypedSlotIndex.BroadcastPostProcessWithoutContextFast,
                        requiresContext: false
                    ),
                    originalHandler,
                    handler,
                    deregistration,
                    priority,
                    messageBus
                );
            }

            // Context-aware variant that preserves the priority and context key
            // mappings on deregistration so frozen dispatch snapshots remain valid
            // for any in-flight emission. Trade-off: empty HandlerActionCache
            // entries (and their enclosing per-priority Dictionary) are not
            // reclaimed until either (a) a future registration at the same
            // (context, priority) pair reuses the cache, or (b) the owning
            // MessageHandler is destroyed. For typical Unity gameplay (a small
            // fixed set of priorities and a bounded set of long-lived target /
            // source InstanceIds) the residual footprint is on the order of
            // hundreds of bytes per MessageHandler. Code that interacts with
            // many transient InstanceIds (e.g. a global service that registers
            // handlers per ephemeral GameObject) should prefer recycling
            // MessageHandlers or routing through AddSourcedBroadcastWithoutSourceHandler /
            // AddTargetedWithoutTargetingHandler to avoid the per-(context,priority)
            // outer-dictionary growth.
            private Action AddHandlerPreservingPriorityKey<TU>(
                InstanceId context,
                Dictionary<InstanceId, Dictionary<int, IHandlerActionCache>> handlersByContext,
                TU originalHandler,
                TU augmentedHandler,
                Action deregistration,
                int priority,
                IMessageBus messageBus
            )
            {
                if (
                    !handlersByContext.TryGetValue(
                        context,
                        out Dictionary<int, IHandlerActionCache> sortedHandlers
                    )
                )
                {
                    sortedHandlers = DxPools.TypedHandlerPriorityDicts.Rent();
                    handlersByContext[context] = sortedHandlers;
                }

                if (
                    !sortedHandlers.TryGetValue(priority, out IHandlerActionCache erasedCache)
                    || erasedCache is not HandlerActionCache<TU> cache
                )
                {
                    cache = new HandlerActionCache<TU>();
                    sortedHandlers[priority] = cache;
                }

                if (
                    !cache.entries.TryGetValue(
                        originalHandler,
                        out HandlerActionCache<TU>.Entry entry
                    )
                )
                {
                    entry = new HandlerActionCache<TU>.Entry(augmentedHandler, 0);
                }

                bool firstRegistration = entry.count == 0;
                entry = firstRegistration
                    ? new HandlerActionCache<TU>.Entry(augmentedHandler, 1)
                    : new HandlerActionCache<TU>.Entry(entry.handler, entry.count + 1);

                cache.entries[originalHandler] = entry;
                if (firstRegistration)
                {
                    cache.insertionOrder.Add(originalHandler);
                }
                cache.version++;
                TypedSlot<T> slot = FindContextSlot(handlersByContext);
                if (slot != null)
                {
                    slot.lastTouchTicks =
                        global::DxMessaging.Core.MessageBus.MessageBus.GetCurrentTouchTick(
                            messageBus
                        );
                }
                if (firstRegistration && slot != null)
                {
                    slot.liveCount++;
                }

                Dictionary<
                    InstanceId,
                    Dictionary<int, IHandlerActionCache>
                > localHandlersByContext = handlersByContext;
                TypedSlot<T> localSlot = slot;
                long localSlotVersion = slot?.version ?? 0;
                long localResetGeneration =
                    global::DxMessaging.Core.MessageBus.MessageBus.GetResetGeneration(messageBus);

                return () =>
                {
                    if (
                        !global::DxMessaging.Core.MessageBus.MessageBus.IsResetGenerationCurrent(
                            messageBus,
                            localResetGeneration
                        )
                    )
                    {
                        return;
                    }

                    if (localSlot != null && localSlot.version != localSlotVersion)
                    {
                        return;
                    }

                    if (!localHandlersByContext.TryGetValue(context, out sortedHandlers))
                    {
                        return;
                    }

                    if (
                        !sortedHandlers.TryGetValue(
                            priority,
                            out IHandlerActionCache localErasedCache
                        ) || localErasedCache is not HandlerActionCache<TU> localCache
                    )
                    {
                        return;
                    }

                    if (
                        !localCache.entries.TryGetValue(
                            originalHandler,
                            out HandlerActionCache<TU>.Entry localEntry
                        )
                    )
                    {
                        return;
                    }

                    localCache.version++;

                    deregistration?.Invoke();
                    if (localSlot != null)
                    {
                        localSlot.lastTouchTicks =
                            global::DxMessaging.Core.MessageBus.MessageBus.GetCurrentTouchTick(
                                messageBus
                            );
                    }

                    if (localEntry.count <= 1)
                    {
                        _ = localCache.entries.Remove(originalHandler);
                        // List.Remove is O(n) over the same-priority bucket.
                        // Accepted tradeoff (here and at every sibling
                        // deregistration site): buckets are small in practice,
                        // removal is a cold churn path, and the list keeps
                        // steady-state dispatch allocation-free while
                        // preserving first-registration order, unlike
                        // Dictionary enumeration whose freed slots are reused
                        // LIFO.
                        _ = localCache.insertionOrder.Remove(originalHandler);
                        localCache.version++;
                        if (localSlot != null)
                        {
                            localSlot.liveCount--;
                        }
                        // Deliberately keep the priority and context mappings to preserve
                        // frozen snapshots for the current emission.
                        return;
                    }

                    localEntry = new HandlerActionCache<TU>.Entry(
                        localEntry.handler,
                        localEntry.count - 1
                    );

                    localCache.entries[originalHandler] = localEntry;
                };
            }

            private static Action AddHandlerPreservingPriorityKey<TU>(
                InstanceId context,
                ref Dictionary<
                    InstanceId,
                    Dictionary<int, HandlerActionCache<TU>>
                > handlersByContext,
                TU originalHandler,
                TU augmentedHandler,
                Action deregistration,
                int priority,
                IMessageBus messageBus
            )
            {
                handlersByContext ??=
                    new Dictionary<InstanceId, Dictionary<int, HandlerActionCache<TU>>>();

                if (
                    !handlersByContext.TryGetValue(
                        context,
                        out Dictionary<int, HandlerActionCache<TU>> sortedHandlers
                    )
                )
                {
                    sortedHandlers = new Dictionary<int, HandlerActionCache<TU>>();
                    handlersByContext[context] = sortedHandlers;
                }

                if (!sortedHandlers.TryGetValue(priority, out HandlerActionCache<TU> cache))
                {
                    cache = new HandlerActionCache<TU>();
                    sortedHandlers[priority] = cache;
                }

                if (
                    !cache.entries.TryGetValue(
                        originalHandler,
                        out HandlerActionCache<TU>.Entry entry
                    )
                )
                {
                    entry = new HandlerActionCache<TU>.Entry(augmentedHandler, 0);
                }

                bool firstRegistration = entry.count == 0;
                entry = firstRegistration
                    ? new HandlerActionCache<TU>.Entry(augmentedHandler, 1)
                    : new HandlerActionCache<TU>.Entry(entry.handler, entry.count + 1);

                cache.entries[originalHandler] = entry;
                if (firstRegistration)
                {
                    cache.insertionOrder.Add(originalHandler);
                }
                cache.version++;

                Dictionary<
                    InstanceId,
                    Dictionary<int, HandlerActionCache<TU>>
                > localHandlersByContext = handlersByContext;

                return () =>
                {
                    if (!localHandlersByContext.TryGetValue(context, out sortedHandlers))
                    {
                        return;
                    }

                    if (
                        !sortedHandlers.TryGetValue(priority, out HandlerActionCache<TU> localCache)
                    )
                    {
                        return;
                    }

                    if (
                        !localCache.entries.TryGetValue(
                            originalHandler,
                            out HandlerActionCache<TU>.Entry localEntry
                        )
                    )
                    {
                        return;
                    }

                    localCache.version++;

                    deregistration?.Invoke();

                    if (localEntry.count <= 1)
                    {
                        _ = localCache.entries.Remove(originalHandler);
                        _ = localCache.insertionOrder.Remove(originalHandler);
                        localCache.version++;
                        // Deliberately keep the priority and context mappings to preserve
                        // frozen snapshots for the current emission.
                        return;
                    }

                    localEntry = new HandlerActionCache<TU>.Entry(
                        localEntry.handler,
                        localEntry.count - 1
                    );

                    localCache.entries[originalHandler] = localEntry;
                };
            }

            private static void RunFastHandlersWithContext<TMessage>(
                ref InstanceId context,
                Dictionary<int, IHandlerActionCache> fastHandlers,
                ref TMessage message,
                int priority,
                long emissionId
            )
                where TMessage : IMessage
            {
                RunFastHandlers(ref context, fastHandlers, ref message, priority, emissionId);
            }

            private static void RunFastHandlersWithContext<TMessage>(
                ref InstanceId context,
                Dictionary<InstanceId, Dictionary<int, IHandlerActionCache>> fastHandlersByContext,
                ref TMessage message,
                int priority,
                long emissionId
            )
                where TMessage : IMessage
            {
                if (
                    fastHandlersByContext is not { Count: > 0 }
                    || !fastHandlersByContext.TryGetValue(
                        context,
                        out Dictionary<int, IHandlerActionCache> cache
                    )
                )
                {
                    return;
                }

                RunFastHandlers(cache, ref message, priority, emissionId);
            }

            private static void RunFastHandlersWithContext<TMessage>(
                ref InstanceId context,
                Dictionary<
                    int,
                    HandlerActionCache<FastHandlerWithContext<T>>
                > fastHandlersByContext,
                ref TMessage message,
                int priority,
                long emissionId
            )
                where TMessage : IMessage
            {
                if (fastHandlersByContext is not { Count: > 0 })
                {
                    return;
                }

                RunFastHandlers(
                    ref context,
                    fastHandlersByContext,
                    ref message,
                    priority,
                    emissionId
                );
            }

            private static void RunFastHandlersWithContext<TMessage>(
                ref InstanceId context,
                Dictionary<
                    InstanceId,
                    Dictionary<int, HandlerActionCache<FastHandler<T>>>
                > fastHandlersByContext,
                ref TMessage message,
                int priority,
                long emissionId
            )
                where TMessage : IMessage
            {
                if (
                    fastHandlersByContext is not { Count: > 0 }
                    || !fastHandlersByContext.TryGetValue(
                        context,
                        out Dictionary<int, HandlerActionCache<FastHandler<T>>> cache
                    )
                )
                {
                    return;
                }

                RunFastHandlers(cache, ref message, priority, emissionId);
            }

            private static void RunFastHandlers<TMessage>(
                Dictionary<int, IHandlerActionCache> fastHandlers,
                ref TMessage message,
                int priority,
                long emissionId
            )
                where TMessage : IMessage
            {
                if (fastHandlers is not { Count: > 0 })
                {
                    return;
                }

                if (
                    !fastHandlers.TryGetValue(priority, out IHandlerActionCache erasedCache)
                    || erasedCache is not HandlerActionCache<FastHandler<T>> cache
                )
                {
                    return;
                }

                ref T typedMessage = ref DxUnsafe.As<TMessage, T>(ref message);
                List<FastHandler<T>> handlers = GetOrAddNewHandlerStack(cache, emissionId);
                int handlersCount = handlers.Count;
                switch (handlersCount)
                {
                    case 1:
                    {
                        handlers[0](ref typedMessage);
                        return;
                    }
                    case 2:
                    {
                        handlers[0](ref typedMessage);
                        if (handlers.Count < 2)
                        {
                            return;
                        }
                        handlers[1](ref typedMessage);
                        return;
                    }
                    case 3:
                    {
                        handlers[0](ref typedMessage);
                        if (handlers.Count < 2)
                        {
                            return;
                        }
                        handlers[1](ref typedMessage);
                        if (handlers.Count < 3)
                        {
                            return;
                        }
                        handlers[2](ref typedMessage);
                        return;
                    }
                    case 4:
                    {
                        handlers[0](ref typedMessage);
                        if (handlers.Count < 2)
                        {
                            return;
                        }
                        handlers[1](ref typedMessage);
                        if (handlers.Count < 3)
                        {
                            return;
                        }
                        handlers[2](ref typedMessage);
                        if (handlers.Count < 4)
                        {
                            return;
                        }
                        handlers[3](ref typedMessage);
                        return;
                    }
                    case 5:
                    {
                        handlers[0](ref typedMessage);
                        if (handlers.Count < 2)
                        {
                            return;
                        }
                        handlers[1](ref typedMessage);
                        if (handlers.Count < 3)
                        {
                            return;
                        }
                        handlers[2](ref typedMessage);
                        if (handlers.Count < 4)
                        {
                            return;
                        }
                        handlers[3](ref typedMessage);
                        if (handlers.Count < 5)
                        {
                            return;
                        }
                        handlers[4](ref typedMessage);
                        return;
                    }
                }

                for (int i = 0; i < handlersCount && i < handlers.Count; ++i)
                {
                    handlers[i](ref typedMessage);
                }
            }

            private static void RunFastHandlers<TMessage>(
                Dictionary<int, HandlerActionCache<FastHandler<T>>> fastHandlers,
                ref TMessage message,
                int priority,
                long emissionId
            )
                where TMessage : IMessage
            {
                if (fastHandlers is not { Count: > 0 })
                {
                    return;
                }

                if (
                    !fastHandlers.TryGetValue(
                        priority,
                        out HandlerActionCache<FastHandler<T>> cache
                    )
                )
                {
                    return;
                }

                ref T typedMessage = ref DxUnsafe.As<TMessage, T>(ref message);
                List<FastHandler<T>> handlers = GetOrAddNewHandlerStack(cache, emissionId);
                int handlersCount = handlers.Count;
                switch (handlersCount)
                {
                    case 1:
                    {
                        handlers[0](ref typedMessage);
                        return;
                    }
                    case 2:
                    {
                        handlers[0](ref typedMessage);
                        if (handlers.Count < 2)
                        {
                            return;
                        }
                        handlers[1](ref typedMessage);
                        return;
                    }
                    case 3:
                    {
                        handlers[0](ref typedMessage);
                        if (handlers.Count < 2)
                        {
                            return;
                        }
                        handlers[1](ref typedMessage);
                        if (handlers.Count < 3)
                        {
                            return;
                        }
                        handlers[2](ref typedMessage);
                        return;
                    }
                    case 4:
                    {
                        handlers[0](ref typedMessage);
                        if (handlers.Count < 2)
                        {
                            return;
                        }
                        handlers[1](ref typedMessage);
                        if (handlers.Count < 3)
                        {
                            return;
                        }
                        handlers[2](ref typedMessage);
                        if (handlers.Count < 4)
                        {
                            return;
                        }
                        handlers[3](ref typedMessage);
                        return;
                    }
                    case 5:
                    {
                        handlers[0](ref typedMessage);
                        if (handlers.Count < 2)
                        {
                            return;
                        }
                        handlers[1](ref typedMessage);
                        if (handlers.Count < 3)
                        {
                            return;
                        }
                        handlers[2](ref typedMessage);
                        if (handlers.Count < 4)
                        {
                            return;
                        }
                        handlers[3](ref typedMessage);
                        if (handlers.Count < 5)
                        {
                            return;
                        }
                        handlers[4](ref typedMessage);
                        return;
                    }
                }

                for (int i = 0; i < handlersCount && i < handlers.Count; ++i)
                {
                    handlers[i](ref typedMessage);
                }
            }

            private static void RunFastHandlers<TMessage, TU>(
                HandlerActionCache<FastHandler<TU>> cache,
                ref TMessage message,
                long emissionId
            )
                where TMessage : IMessage
                where TU : IMessage
            {
                // Snapshot semantics: do not bail on the live entries dictionary
                // count. A mid-emit removal can drain entries while the pinned
                // emission snapshot in cache.cache still holds the handlers we
                // must invoke. Read the snapshot first and bail only if the
                // snapshot itself is empty.
                //
                // Perf note: GetOrAddNewHandlerStack is now invoked on every
                // call (including for empty caches that the previous fast-path
                // would have skipped). The cost is one dictionary
                // emission-id/version compare and -- only when the per-emission
                // snapshot has not been pinned yet -- a single pass over
                // cache.entries to materialise an empty list. The win is
                // correctness across cross-handler mid-emit removals where the
                // pinned snapshot in cache.cache still holds handlers the live
                // entries dictionary no longer reaches.
                if (cache == null)
                {
                    return;
                }

                ref TU typedMessage = ref DxUnsafe.As<TMessage, TU>(ref message);
                List<FastHandler<TU>> handlers = GetOrAddNewHandlerStack(cache, emissionId);
                int handlersCount = handlers.Count;
                if (handlersCount == 0)
                {
                    return;
                }
                switch (handlersCount)
                {
                    case 1:
                    {
                        handlers[0](ref typedMessage);
                        return;
                    }
                    case 2:
                    {
                        handlers[0](ref typedMessage);
                        if (handlers.Count < 2)
                        {
                            return;
                        }
                        handlers[1](ref typedMessage);
                        return;
                    }
                    case 3:
                    {
                        handlers[0](ref typedMessage);
                        if (handlers.Count < 2)
                        {
                            return;
                        }
                        handlers[1](ref typedMessage);
                        if (handlers.Count < 3)
                        {
                            return;
                        }
                        handlers[2](ref typedMessage);
                        return;
                    }
                    case 4:
                    {
                        handlers[0](ref typedMessage);
                        if (handlers.Count < 2)
                        {
                            return;
                        }
                        handlers[1](ref typedMessage);
                        if (handlers.Count < 3)
                        {
                            return;
                        }
                        handlers[2](ref typedMessage);
                        if (handlers.Count < 4)
                        {
                            return;
                        }
                        handlers[3](ref typedMessage);
                        return;
                    }
                    case 5:
                    {
                        handlers[0](ref typedMessage);
                        if (handlers.Count < 2)
                        {
                            return;
                        }
                        handlers[1](ref typedMessage);
                        if (handlers.Count < 3)
                        {
                            return;
                        }
                        handlers[2](ref typedMessage);
                        if (handlers.Count < 4)
                        {
                            return;
                        }
                        handlers[3](ref typedMessage);
                        if (handlers.Count < 5)
                        {
                            return;
                        }
                        handlers[4](ref typedMessage);
                        return;
                    }
                }

                for (int i = 0; i < handlersCount && i < handlers.Count; ++i)
                {
                    handlers[i](ref typedMessage);
                }
            }

            private static void RunFastHandlers<TMessage, TU>(
                ref InstanceId context,
                HandlerActionCache<FastHandlerWithContext<TU>> cache,
                ref TMessage message,
                long emissionId
            )
                where TMessage : IMessage
                where TU : IMessage
            {
                // Snapshot semantics: see comment on the FastHandler<TU> overload.
                // The pinned emission snapshot may still hold handlers even when
                // the live entries dictionary has been drained mid-emit.
                if (cache == null)
                {
                    return;
                }

                ref TU typedMessage = ref DxUnsafe.As<TMessage, TU>(ref message);
                List<FastHandlerWithContext<TU>> handlers = GetOrAddNewHandlerStack(
                    cache,
                    emissionId
                );
                int handlersCount = handlers.Count;
                if (handlersCount == 0)
                {
                    return;
                }
                switch (handlersCount)
                {
                    case 1:
                    {
                        handlers[0](ref context, ref typedMessage);
                        return;
                    }
                    case 2:
                    {
                        handlers[0](ref context, ref typedMessage);
                        if (handlers.Count < 2)
                        {
                            return;
                        }
                        handlers[1](ref context, ref typedMessage);
                        return;
                    }
                    case 3:
                    {
                        handlers[0](ref context, ref typedMessage);
                        if (handlers.Count < 2)
                        {
                            return;
                        }
                        handlers[1](ref context, ref typedMessage);
                        if (handlers.Count < 3)
                        {
                            return;
                        }
                        handlers[2](ref context, ref typedMessage);
                        return;
                    }
                    case 4:
                    {
                        handlers[0](ref context, ref typedMessage);
                        if (handlers.Count < 2)
                        {
                            return;
                        }
                        handlers[1](ref context, ref typedMessage);
                        if (handlers.Count < 3)
                        {
                            return;
                        }
                        handlers[2](ref context, ref typedMessage);
                        if (handlers.Count < 4)
                        {
                            return;
                        }
                        handlers[3](ref context, ref typedMessage);
                        return;
                    }
                    case 5:
                    {
                        handlers[0](ref context, ref typedMessage);
                        if (handlers.Count < 2)
                        {
                            return;
                        }
                        handlers[1](ref context, ref typedMessage);
                        if (handlers.Count < 3)
                        {
                            return;
                        }
                        handlers[2](ref context, ref typedMessage);
                        if (handlers.Count < 4)
                        {
                            return;
                        }
                        handlers[3](ref context, ref typedMessage);
                        if (handlers.Count < 5)
                        {
                            return;
                        }
                        handlers[4](ref context, ref typedMessage);
                        return;
                    }
                }

                for (int i = 0; i < handlersCount && i < handlers.Count; ++i)
                {
                    handlers[i](ref context, ref typedMessage);
                }
            }

            private static void RunFastHandlers<TMessage>(
                ref InstanceId context,
                Dictionary<int, IHandlerActionCache> fastHandlers,
                ref TMessage message,
                int priority,
                long emissionId
            )
                where TMessage : IMessage
            {
                if (fastHandlers is not { Count: > 0 })
                {
                    return;
                }

                if (
                    !fastHandlers.TryGetValue(priority, out IHandlerActionCache erasedCache)
                    || erasedCache is not HandlerActionCache<FastHandlerWithContext<T>> cache
                )
                {
                    return;
                }

                RunFastHandlers(ref context, cache, ref message, emissionId);
            }

            private static void RunFastHandlers<TMessage, TU>(
                ref InstanceId context,
                Dictionary<int, IHandlerActionCache> fastHandlers,
                ref TMessage message,
                int priority,
                long emissionId
            )
                where TMessage : IMessage
                where TU : IMessage
            {
                if (fastHandlers is not { Count: > 0 })
                {
                    return;
                }

                if (
                    !fastHandlers.TryGetValue(priority, out IHandlerActionCache erasedCache)
                    || erasedCache is not HandlerActionCache<FastHandlerWithContext<TU>> cache
                )
                {
                    return;
                }

                RunFastHandlers(ref context, cache, ref message, emissionId);
            }

            private static void RunFastHandlers<TMessage, TU>(
                ref InstanceId context,
                Dictionary<int, HandlerActionCache<FastHandlerWithContext<TU>>> fastHandlers,
                ref TMessage message,
                int priority,
                long emissionId
            )
                where TMessage : IMessage
                where TU : IMessage
            {
                if (fastHandlers is not { Count: > 0 })
                {
                    return;
                }

                if (
                    !fastHandlers.TryGetValue(
                        priority,
                        out HandlerActionCache<FastHandlerWithContext<TU>> cache
                    )
                )
                {
                    return;
                }

                ref TU typedMessage = ref DxUnsafe.As<TMessage, TU>(ref message);
                List<FastHandlerWithContext<TU>> handlers = GetOrAddNewHandlerStack(
                    cache,
                    emissionId
                );
                int handlersCount = handlers.Count;
                switch (handlersCount)
                {
                    case 1:
                    {
                        handlers[0](ref context, ref typedMessage);
                        return;
                    }
                    case 2:
                    {
                        handlers[0](ref context, ref typedMessage);
                        if (handlers.Count < 2)
                        {
                            return;
                        }
                        handlers[1](ref context, ref typedMessage);
                        return;
                    }
                    case 3:
                    {
                        handlers[0](ref context, ref typedMessage);
                        if (handlers.Count < 2)
                        {
                            return;
                        }
                        handlers[1](ref context, ref typedMessage);
                        if (handlers.Count < 3)
                        {
                            return;
                        }
                        handlers[2](ref context, ref typedMessage);
                        return;
                    }
                    case 4:
                    {
                        handlers[0](ref context, ref typedMessage);
                        if (handlers.Count < 2)
                        {
                            return;
                        }
                        handlers[1](ref context, ref typedMessage);
                        if (handlers.Count < 3)
                        {
                            return;
                        }
                        handlers[2](ref context, ref typedMessage);
                        if (handlers.Count < 4)
                        {
                            return;
                        }
                        handlers[3](ref context, ref typedMessage);
                        return;
                    }
                    case 5:
                    {
                        handlers[0](ref context, ref typedMessage);
                        if (handlers.Count < 2)
                        {
                            return;
                        }
                        handlers[1](ref context, ref typedMessage);
                        if (handlers.Count < 3)
                        {
                            return;
                        }
                        handlers[2](ref context, ref typedMessage);
                        if (handlers.Count < 4)
                        {
                            return;
                        }
                        handlers[3](ref context, ref typedMessage);
                        if (handlers.Count < 5)
                        {
                            return;
                        }
                        handlers[4](ref context, ref typedMessage);
                        return;
                    }
                }

                for (int i = 0; i < handlersCount && i < handlers.Count; ++i)
                {
                    handlers[i](ref context, ref typedMessage);
                }
            }

            private static void RunHandlersWithContext<TMessage>(
                ref InstanceId context,
                Dictionary<InstanceId, Dictionary<int, IHandlerActionCache>> handlersByContext,
                ref TMessage message,
                int priority,
                long emissionId
            )
                where TMessage : IMessage
            {
                if (
                    handlersByContext is not { Count: > 0 }
                    || !handlersByContext.TryGetValue(
                        context,
                        out Dictionary<int, IHandlerActionCache> cache
                    )
                )
                {
                    return;
                }

                RunHandlers(cache, ref message, priority, emissionId);
            }

            private static void RunHandlersWithContext<TMessage>(
                ref InstanceId context,
                Dictionary<
                    InstanceId,
                    Dictionary<int, HandlerActionCache<Action<T>>>
                > handlersByContext,
                ref TMessage message,
                int priority,
                long emissionId
            )
                where TMessage : IMessage
            {
                if (
                    handlersByContext is not { Count: > 0 }
                    || !handlersByContext.TryGetValue(
                        context,
                        out Dictionary<int, HandlerActionCache<Action<T>>> cache
                    )
                )
                {
                    return;
                }

                RunHandlers(cache, ref message, priority, emissionId);
            }

            private static void RunHandlers<TMessage>(
                Dictionary<int, IHandlerActionCache> sortedHandlers,
                ref TMessage message,
                int priority,
                long emissionId
            )
                where TMessage : IMessage
            {
                if (sortedHandlers is not { Count: > 0 })
                {
                    return;
                }

                if (
                    !sortedHandlers.TryGetValue(priority, out IHandlerActionCache erasedCache)
                    || erasedCache is not HandlerActionCache<Action<T>> cache
                )
                {
                    return;
                }

                List<Action<T>> handlers = GetOrAddNewHandlerStack(cache, emissionId);
                ref T typedMessage = ref DxUnsafe.As<TMessage, T>(ref message);
                int handlersCount = handlers.Count;
                switch (handlersCount)
                {
                    case 1:
                    {
                        handlers[0](typedMessage);
                        return;
                    }
                    case 2:
                    {
                        handlers[0](typedMessage);
                        if (handlers.Count < 2)
                        {
                            return;
                        }
                        handlers[1](typedMessage);
                        return;
                    }
                    case 3:
                    {
                        handlers[0](typedMessage);
                        if (handlers.Count < 2)
                        {
                            return;
                        }
                        handlers[1](typedMessage);
                        if (handlers.Count < 3)
                        {
                            return;
                        }
                        handlers[2](typedMessage);
                        return;
                    }
                    case 4:
                    {
                        handlers[0](typedMessage);
                        if (handlers.Count < 2)
                        {
                            return;
                        }
                        handlers[1](typedMessage);
                        if (handlers.Count < 3)
                        {
                            return;
                        }
                        handlers[2](typedMessage);
                        if (handlers.Count < 4)
                        {
                            return;
                        }
                        handlers[3](typedMessage);
                        return;
                    }
                    case 5:
                    {
                        handlers[0](typedMessage);
                        if (handlers.Count < 2)
                        {
                            return;
                        }
                        handlers[1](typedMessage);
                        if (handlers.Count < 3)
                        {
                            return;
                        }
                        handlers[2](typedMessage);
                        if (handlers.Count < 4)
                        {
                            return;
                        }
                        handlers[3](typedMessage);
                        if (handlers.Count < 5)
                        {
                            return;
                        }
                        handlers[4](typedMessage);
                        return;
                    }
                }

                for (int i = 0; i < handlersCount && i < handlers.Count; ++i)
                {
                    handlers[i](typedMessage);
                }
            }

            private static void RunHandlers<TMessage>(
                Dictionary<int, HandlerActionCache<Action<T>>> sortedHandlers,
                ref TMessage message,
                int priority,
                long emissionId
            )
                where TMessage : IMessage
            {
                if (sortedHandlers is not { Count: > 0 })
                {
                    return;
                }

                if (!sortedHandlers.TryGetValue(priority, out HandlerActionCache<Action<T>> cache))
                {
                    return;
                }

                List<Action<T>> handlers = GetOrAddNewHandlerStack(cache, emissionId);
                ref T typedMessage = ref DxUnsafe.As<TMessage, T>(ref message);
                int handlersCount = handlers.Count;
                switch (handlersCount)
                {
                    case 1:
                    {
                        handlers[0](typedMessage);
                        return;
                    }
                    case 2:
                    {
                        handlers[0](typedMessage);
                        if (handlers.Count < 2)
                        {
                            return;
                        }
                        handlers[1](typedMessage);
                        return;
                    }
                    case 3:
                    {
                        handlers[0](typedMessage);
                        if (handlers.Count < 2)
                        {
                            return;
                        }
                        handlers[1](typedMessage);
                        if (handlers.Count < 3)
                        {
                            return;
                        }
                        handlers[2](typedMessage);
                        return;
                    }
                    case 4:
                    {
                        handlers[0](typedMessage);
                        if (handlers.Count < 2)
                        {
                            return;
                        }
                        handlers[1](typedMessage);
                        if (handlers.Count < 3)
                        {
                            return;
                        }
                        handlers[2](typedMessage);
                        if (handlers.Count < 4)
                        {
                            return;
                        }
                        handlers[3](typedMessage);
                        return;
                    }
                    case 5:
                    {
                        handlers[0](typedMessage);
                        if (handlers.Count < 2)
                        {
                            return;
                        }
                        handlers[1](typedMessage);
                        if (handlers.Count < 3)
                        {
                            return;
                        }
                        handlers[2](typedMessage);
                        if (handlers.Count < 4)
                        {
                            return;
                        }
                        handlers[3](typedMessage);
                        if (handlers.Count < 5)
                        {
                            return;
                        }
                        handlers[4](typedMessage);
                        return;
                    }
                }

                for (int i = 0; i < handlersCount && i < handlers.Count; ++i)
                {
                    handlers[i](typedMessage);
                }
            }

            private static void RunHandlers<TMessage>(
                ref InstanceId context,
                Dictionary<int, IHandlerActionCache> handlers,
                ref TMessage message,
                int priority,
                long emissionId
            )
                where TMessage : IMessage
            {
                if (handlers is not { Count: > 0 })
                {
                    return;
                }

                if (
                    !handlers.TryGetValue(priority, out IHandlerActionCache erasedCache)
                    || erasedCache is not HandlerActionCache<Action<InstanceId, T>> cache
                )
                {
                    return;
                }

                List<Action<InstanceId, T>> typedHandlers = GetOrAddNewHandlerStack(
                    cache,
                    emissionId
                );
                ref T typedMessage = ref DxUnsafe.As<TMessage, T>(ref message);
                int handlersCount = typedHandlers.Count;
                switch (handlersCount)
                {
                    case 1:
                    {
                        typedHandlers[0](context, typedMessage);
                        return;
                    }
                    case 2:
                    {
                        typedHandlers[0](context, typedMessage);
                        if (typedHandlers.Count < 2)
                        {
                            return;
                        }
                        typedHandlers[1](context, typedMessage);
                        return;
                    }
                    case 3:
                    {
                        typedHandlers[0](context, typedMessage);
                        if (typedHandlers.Count < 2)
                        {
                            return;
                        }
                        typedHandlers[1](context, typedMessage);
                        if (typedHandlers.Count < 3)
                        {
                            return;
                        }
                        typedHandlers[2](context, typedMessage);
                        return;
                    }
                    case 4:
                    {
                        typedHandlers[0](context, typedMessage);
                        if (typedHandlers.Count < 2)
                        {
                            return;
                        }
                        typedHandlers[1](context, typedMessage);
                        if (typedHandlers.Count < 3)
                        {
                            return;
                        }
                        typedHandlers[2](context, typedMessage);
                        if (typedHandlers.Count < 4)
                        {
                            return;
                        }
                        typedHandlers[3](context, typedMessage);
                        return;
                    }
                    case 5:
                    {
                        typedHandlers[0](context, typedMessage);
                        if (typedHandlers.Count < 2)
                        {
                            return;
                        }
                        typedHandlers[1](context, typedMessage);
                        if (typedHandlers.Count < 3)
                        {
                            return;
                        }
                        typedHandlers[2](context, typedMessage);
                        if (typedHandlers.Count < 4)
                        {
                            return;
                        }
                        typedHandlers[3](context, typedMessage);
                        if (typedHandlers.Count < 5)
                        {
                            return;
                        }
                        typedHandlers[4](context, typedMessage);
                        return;
                    }
                }

                for (int i = 0; i < handlersCount && i < typedHandlers.Count; ++i)
                {
                    typedHandlers[i](context, typedMessage);
                }
            }

            private static void RunHandlers<TMessage>(
                ref InstanceId context,
                Dictionary<int, HandlerActionCache<Action<InstanceId, T>>> handlers,
                ref TMessage message,
                int priority,
                long emissionId
            )
                where TMessage : IMessage
            {
                if (handlers is not { Count: > 0 })
                {
                    return;
                }

                if (
                    !handlers.TryGetValue(
                        priority,
                        out HandlerActionCache<Action<InstanceId, T>> cache
                    )
                )
                {
                    return;
                }

                List<Action<InstanceId, T>> typedHandlers = GetOrAddNewHandlerStack(
                    cache,
                    emissionId
                );
                ref T typedMessage = ref DxUnsafe.As<TMessage, T>(ref message);
                int handlersCount = typedHandlers.Count;
                switch (handlersCount)
                {
                    case 1:
                    {
                        typedHandlers[0](context, typedMessage);
                        return;
                    }
                    case 2:
                    {
                        typedHandlers[0](context, typedMessage);
                        if (typedHandlers.Count < 2)
                        {
                            return;
                        }
                        typedHandlers[1](context, typedMessage);
                        return;
                    }
                    case 3:
                    {
                        typedHandlers[0](context, typedMessage);
                        if (typedHandlers.Count < 2)
                        {
                            return;
                        }
                        typedHandlers[1](context, typedMessage);
                        if (typedHandlers.Count < 3)
                        {
                            return;
                        }
                        typedHandlers[2](context, typedMessage);
                        return;
                    }
                    case 4:
                    {
                        typedHandlers[0](context, typedMessage);
                        if (typedHandlers.Count < 2)
                        {
                            return;
                        }
                        typedHandlers[1](context, typedMessage);
                        if (typedHandlers.Count < 3)
                        {
                            return;
                        }
                        typedHandlers[2](context, typedMessage);
                        if (typedHandlers.Count < 4)
                        {
                            return;
                        }
                        typedHandlers[3](context, typedMessage);
                        return;
                    }
                    case 5:
                    {
                        typedHandlers[0](context, typedMessage);
                        if (typedHandlers.Count < 2)
                        {
                            return;
                        }
                        typedHandlers[1](context, typedMessage);
                        if (typedHandlers.Count < 3)
                        {
                            return;
                        }
                        typedHandlers[2](context, typedMessage);
                        if (typedHandlers.Count < 4)
                        {
                            return;
                        }
                        typedHandlers[3](context, typedMessage);
                        if (typedHandlers.Count < 5)
                        {
                            return;
                        }
                        typedHandlers[4](context, typedMessage);
                        return;
                    }
                }

                for (int i = 0; i < handlersCount && i < typedHandlers.Count; ++i)
                {
                    typedHandlers[i](context, typedMessage);
                }
            }

            // Mid-dispatch clear contract: the List returned here is the LIVE
            // cache.cache list, not a copy. IHandlerActionCache.Reset() (bus
            // reset / sweep eviction) clears it IN PLACE, so every dispatch
            // loop that indexes the returned list re-checks list.Count before
            // each invocation past the first (and the >5 fallback loops bound
            // on the live Count). A reset fired from inside a handler then
            // cleanly stops the in-flight bucket: no peer delegate runs and
            // nothing throws. The re-check is a single inlined List.Count
            // field read on data already in cache, so steady-state dispatch
            // cost is unchanged.
            internal static List<TU> GetOrAddNewHandlerStack<TU>(
                HandlerActionCache<TU> actionCache,
                long emissionId
            )
            {
                DebugAssertInsertionOrderInSync(actionCache);
                if (actionCache.lastSeenEmissionId != emissionId)
                {
                    if (actionCache.version != actionCache.lastSeenVersion)
                    {
                        // Rebuild the dispatch snapshot from insertionOrder, NOT from
                        // the entries dictionary: dictionary enumeration order permutes
                        // after Remove/Add churn (freed slots are reused LIFO), while
                        // insertionOrder preserves the documented first-registration
                        // order for equal-priority handlers. This branch only runs on
                        // registration churn (version bump), never on steady-state
                        // dispatch, and allocates nothing (the pooled cache list is
                        // cleared and refilled in place).
                        List<TU> list = actionCache.cache;
                        list.Clear();
                        List<TU> orderedHandlers = actionCache.insertionOrder;
                        int orderedCount = orderedHandlers.Count;
                        for (int i = 0; i < orderedCount; ++i)
                        {
                            if (
                                actionCache.entries.TryGetValue(
                                    orderedHandlers[i],
                                    out HandlerActionCache<TU>.Entry entry
                                )
                            )
                            {
                                list.Add(entry.handler);
                            }
                        }
                        actionCache.lastSeenVersion = actionCache.version;
                    }
                    actionCache.lastSeenEmissionId = emissionId;
                }
                return actionCache.cache;
            }

            // Asserts insertionOrder stays in lockstep with the entries
            // dictionary at every dispatch-snapshot read. Drift indicates a
            // mutation site of HandlerActionCache.entries that forgot to
            // mirror the change into insertionOrder (AddHandler* family,
            // deregistration closures, IHandlerActionCache.Reset). Stripped
            // in Release builds via [Conditional("DEBUG")] -- zero hot-path
            // cost.
            [Conditional("DEBUG")]
            private static void DebugAssertInsertionOrderInSync<TU>(
                HandlerActionCache<TU> actionCache
            )
            {
                System.Diagnostics.Debug.Assert(
                    actionCache.insertionOrder.Count == actionCache.entries.Count,
                    "HandlerActionCache.insertionOrder must mirror entries: every first "
                        + "registration appends and every final deregistration removes. A "
                        + "count mismatch means a mutation site skipped the insertionOrder "
                        + "update and same-priority dispatch order is no longer trustworthy."
                );
            }

            private static void PrefreezeHandlersForEmission<THandler>(
                Dictionary<int, IHandlerActionCache> handlers,
                int priority,
                long emissionId
            )
            {
                if (
                    handlers != null
                    && handlers.TryGetValue(priority, out IHandlerActionCache erasedCache)
                    && erasedCache is HandlerActionCache<THandler> cache
                )
                {
                    cache.prefreezeInvocationCount++;
                    _ = GetOrAddNewHandlerStack(cache, emissionId);
                }
            }

            private static void PrefreezeHandlersForEmission<THandler>(
                Dictionary<int, HandlerActionCache<THandler>> handlers,
                int priority,
                long emissionId
            )
            {
                if (
                    handlers != null
                    && handlers.TryGetValue(priority, out HandlerActionCache<THandler> cache)
                )
                {
                    cache.prefreezeInvocationCount++;
                    _ = GetOrAddNewHandlerStack(cache, emissionId);
                }
            }

            private static Action AddHandler<TU>(
                TypedGlobalSlot slot,
                TU originalHandler,
                TU augmentedHandler,
                Action deregistration,
                IMessageBus messageBus
            )
            {
                slot.lastTouchTicks =
                    global::DxMessaging.Core.MessageBus.MessageBus.GetCurrentTouchTick(messageBus);
                HandlerActionCache<TU> cache = slot.cache as HandlerActionCache<TU>;
                if (cache == null)
                {
                    cache = new HandlerActionCache<TU>();
                    slot.cache = cache;
                }

                if (
                    !cache.entries.TryGetValue(
                        originalHandler,
                        out HandlerActionCache<TU>.Entry entry
                    )
                )
                {
                    entry = new HandlerActionCache<TU>.Entry(augmentedHandler, 0);
                }

                bool firstRegistration = entry.count == 0;
                entry = firstRegistration
                    ? new HandlerActionCache<TU>.Entry(augmentedHandler, 1)
                    : new HandlerActionCache<TU>.Entry(entry.handler, entry.count + 1);

                cache.entries[originalHandler] = entry;
                if (firstRegistration)
                {
                    cache.insertionOrder.Add(originalHandler);
                }
                cache.version++;
                if (firstRegistration)
                {
                    slot.liveCount++;
                }

                HandlerActionCache<TU> localCache = cache;
                TypedGlobalSlot localSlot = slot;
                long localSlotVersion = slot.version;
                long localResetGeneration =
                    global::DxMessaging.Core.MessageBus.MessageBus.GetResetGeneration(messageBus);

                return () =>
                {
                    if (
                        !global::DxMessaging.Core.MessageBus.MessageBus.IsResetGenerationCurrent(
                            messageBus,
                            localResetGeneration
                        )
                    )
                    {
                        return;
                    }

                    if (localSlot.version != localSlotVersion)
                    {
                        return;
                    }

                    if (
                        !localCache.entries.TryGetValue(
                            originalHandler,
                            out HandlerActionCache<TU>.Entry localEntry
                        )
                    )
                    {
                        return;
                    }

                    localCache.version++;

                    deregistration?.Invoke();
                    localSlot.lastTouchTicks =
                        global::DxMessaging.Core.MessageBus.MessageBus.GetCurrentTouchTick(
                            messageBus
                        );

                    if (localEntry.count <= 1)
                    {
                        _ = localCache.entries.Remove(originalHandler);
                        _ = localCache.insertionOrder.Remove(originalHandler);
                        localCache.version++;
                        localSlot.liveCount--;
                        return;
                    }

                    localEntry = new HandlerActionCache<TU>.Entry(
                        localEntry.handler,
                        localEntry.count - 1
                    );
                    localCache.entries[originalHandler] = localEntry;
                };
            }

            private static Action AddHandler<TU>(
                InstanceId context,
                ref Dictionary<
                    InstanceId,
                    Dictionary<int, HandlerActionCache<TU>>
                > handlersByContext,
                TU originalHandler,
                TU augmentedHandler,
                Action deregistration,
                int priority,
                IMessageBus messageBus
            )
            {
                handlersByContext ??=
                    new Dictionary<InstanceId, Dictionary<int, HandlerActionCache<TU>>>();

                if (
                    !handlersByContext.TryGetValue(
                        context,
                        out Dictionary<int, HandlerActionCache<TU>> sortedHandlers
                    )
                )
                {
                    sortedHandlers = new Dictionary<int, HandlerActionCache<TU>>();
                    handlersByContext[context] = sortedHandlers;
                }

                if (!sortedHandlers.TryGetValue(priority, out HandlerActionCache<TU> cache))
                {
                    cache = new HandlerActionCache<TU>();
                    sortedHandlers[priority] = cache;
                }

                if (
                    !cache.entries.TryGetValue(
                        originalHandler,
                        out HandlerActionCache<TU>.Entry entry
                    )
                )
                {
                    entry = new HandlerActionCache<TU>.Entry(augmentedHandler, 0);
                }

                bool firstRegistration = entry.count == 0;
                entry = firstRegistration
                    ? new HandlerActionCache<TU>.Entry(augmentedHandler, 1)
                    : new HandlerActionCache<TU>.Entry(entry.handler, entry.count + 1);

                cache.entries[originalHandler] = entry;
                if (firstRegistration)
                {
                    cache.insertionOrder.Add(originalHandler);
                }
                cache.version++;

                Dictionary<
                    InstanceId,
                    Dictionary<int, HandlerActionCache<TU>>
                > localHandlersByContext = handlersByContext;

                return () =>
                {
                    if (!localHandlersByContext.TryGetValue(context, out sortedHandlers))
                    {
                        return;
                    }

                    if (
                        !sortedHandlers.TryGetValue(priority, out HandlerActionCache<TU> localCache)
                    )
                    {
                        return;
                    }

                    if (
                        !localCache.entries.TryGetValue(
                            originalHandler,
                            out HandlerActionCache<TU>.Entry localEntry
                        )
                    )
                    {
                        return;
                    }

                    localCache.version++;

                    deregistration?.Invoke();

                    if (localEntry.count <= 1)
                    {
                        _ = localCache.entries.Remove(originalHandler);
                        _ = localCache.insertionOrder.Remove(originalHandler);
                        localCache.version++;
                        if (localCache.entries.Count == 0)
                        {
                            _ = sortedHandlers.Remove(priority);
                            if (sortedHandlers.Count == 0)
                            {
                                localHandlersByContext.Remove(context);
                            }
                        }

                        return;
                    }

                    localEntry = new HandlerActionCache<TU>.Entry(
                        localEntry.handler,
                        localEntry.count - 1
                    );

                    localCache.entries[originalHandler] = localEntry;
                };
            }

            private static Action AddHandler<TU>(
                ref HandlerActionCache<TU> cache,
                TU originalHandler,
                TU augmentedHandler,
                Action deregistration
            )
            {
                cache ??= new HandlerActionCache<TU>();

                if (
                    !cache.entries.TryGetValue(
                        originalHandler,
                        out HandlerActionCache<TU>.Entry entry
                    )
                )
                {
                    entry = new HandlerActionCache<TU>.Entry(augmentedHandler, 0);
                }

                bool firstRegistration = entry.count == 0;
                entry = firstRegistration
                    ? new HandlerActionCache<TU>.Entry(augmentedHandler, 1)
                    : new HandlerActionCache<TU>.Entry(entry.handler, entry.count + 1);

                cache.entries[originalHandler] = entry;
                if (firstRegistration)
                {
                    cache.insertionOrder.Add(originalHandler);
                }
                cache.version++;

                HandlerActionCache<TU> localCache = cache;

                return () =>
                {
                    if (
                        !localCache.entries.TryGetValue(
                            originalHandler,
                            out HandlerActionCache<TU>.Entry localEntry
                        )
                    )
                    {
                        return;
                    }

                    localCache.version++;

                    deregistration?.Invoke();

                    if (localEntry.count <= 1)
                    {
                        _ = localCache.entries.Remove(originalHandler);
                        _ = localCache.insertionOrder.Remove(originalHandler);
                        localCache.version++;
                        return;
                    }

                    localEntry = new HandlerActionCache<TU>.Entry(
                        localEntry.handler,
                        localEntry.count - 1
                    );
                    localCache.entries[originalHandler] = localEntry;
                };
            }

            private static Action AddHandler<TU>(
                ref Dictionary<int, HandlerActionCache<TU>> handlers,
                TU originalHandler,
                TU augmentedHandler,
                Action deregistration,
                int priority,
                long emissionId
            )
            {
                handlers ??= new Dictionary<int, HandlerActionCache<TU>>();

                if (!handlers.TryGetValue(priority, out HandlerActionCache<TU> cache))
                {
                    cache = new HandlerActionCache<TU>();
                    handlers[priority] = cache;
                }

                if (
                    !cache.entries.TryGetValue(
                        originalHandler,
                        out HandlerActionCache<TU>.Entry entry
                    )
                )
                {
                    entry = new HandlerActionCache<TU>.Entry(augmentedHandler, 0);
                }

                bool firstRegistration = entry.count == 0;
                entry = firstRegistration
                    ? new HandlerActionCache<TU>.Entry(augmentedHandler, 1)
                    : new HandlerActionCache<TU>.Entry(entry.handler, entry.count + 1);

                cache.entries[originalHandler] = entry;
                if (firstRegistration)
                {
                    cache.insertionOrder.Add(originalHandler);
                }
                cache.version++;

                Dictionary<int, HandlerActionCache<TU>> localHandlers = handlers;

                return () =>
                {
                    if (!localHandlers.TryGetValue(priority, out HandlerActionCache<TU> localCache))
                    {
                        return;
                    }

                    if (
                        !localCache.entries.TryGetValue(
                            originalHandler,
                            out HandlerActionCache<TU>.Entry localEntry
                        )
                    )
                    {
                        return;
                    }

                    localCache.version++;

                    deregistration?.Invoke();

                    if (localEntry.count <= 1)
                    {
                        _ = localCache.entries.Remove(originalHandler);
                        _ = localCache.insertionOrder.Remove(originalHandler);
                        localCache.version++;
                        if (localCache.entries.Count == 0)
                        {
                            _ = localHandlers.Remove(priority);
                        }

                        return;
                    }

                    localEntry = new HandlerActionCache<TU>.Entry(
                        localEntry.handler,
                        localEntry.count - 1
                    );

                    localCache.entries[originalHandler] = localEntry;
                };
            }

            // Variant of AddHandler that preserves the priority key in the dictionary when the last entry is removed.
            // This ensures that during an in-flight emission (where handler stacks are already frozen),
            // subsequent removals do not cause lookups to fail for the current pass.
            private Action AddHandlerPreservingPriorityKey<TU>(
                Dictionary<int, IHandlerActionCache> handlers,
                TU originalHandler,
                TU augmentedHandler,
                Action deregistration,
                int priority,
                IMessageBus messageBus
            )
            {
                if (
                    !handlers.TryGetValue(priority, out IHandlerActionCache erasedCache)
                    || erasedCache is not HandlerActionCache<TU> cache
                )
                {
                    cache = new HandlerActionCache<TU>();
                    handlers[priority] = cache;
                }

                if (
                    !cache.entries.TryGetValue(
                        originalHandler,
                        out HandlerActionCache<TU>.Entry entry
                    )
                )
                {
                    entry = new HandlerActionCache<TU>.Entry(augmentedHandler, 0);
                }

                bool firstRegistration = entry.count == 0;
                entry = firstRegistration
                    ? new HandlerActionCache<TU>.Entry(augmentedHandler, 1)
                    : new HandlerActionCache<TU>.Entry(entry.handler, entry.count + 1);

                cache.entries[originalHandler] = entry;
                if (firstRegistration)
                {
                    cache.insertionOrder.Add(originalHandler);
                }
                cache.version++;
                TypedSlot<T> slot = FindPrioritySlot(handlers);
                if (slot != null)
                {
                    slot.lastTouchTicks =
                        global::DxMessaging.Core.MessageBus.MessageBus.GetCurrentTouchTick(
                            messageBus
                        );
                }
                if (slot != null && !slot.orderedPriorities.Contains(priority))
                {
                    slot.orderedPriorities.Add(priority);
                }
                if (firstRegistration && slot != null)
                {
                    slot.liveCount++;
                }

                Dictionary<int, IHandlerActionCache> localHandlers = handlers;
                TypedSlot<T> localSlot = slot;
                long localSlotVersion = slot?.version ?? 0;
                long localResetGeneration =
                    global::DxMessaging.Core.MessageBus.MessageBus.GetResetGeneration(messageBus);

                return () =>
                {
                    if (
                        !global::DxMessaging.Core.MessageBus.MessageBus.IsResetGenerationCurrent(
                            messageBus,
                            localResetGeneration
                        )
                    )
                    {
                        return;
                    }

                    if (localSlot != null && localSlot.version != localSlotVersion)
                    {
                        return;
                    }

                    if (
                        !localHandlers.TryGetValue(
                            priority,
                            out IHandlerActionCache localErasedCache
                        ) || localErasedCache is not HandlerActionCache<TU> localCache
                    )
                    {
                        return;
                    }

                    if (
                        !localCache.entries.TryGetValue(
                            originalHandler,
                            out HandlerActionCache<TU>.Entry localEntry
                        )
                    )
                    {
                        return;
                    }

                    localCache.version++;

                    deregistration?.Invoke();
                    if (localSlot != null)
                    {
                        localSlot.lastTouchTicks =
                            global::DxMessaging.Core.MessageBus.MessageBus.GetCurrentTouchTick(
                                messageBus
                            );
                    }

                    if (localEntry.count <= 1)
                    {
                        _ = localCache.entries.Remove(originalHandler);
                        _ = localCache.insertionOrder.Remove(originalHandler);
                        localCache.version++;
                        if (localSlot != null)
                        {
                            localSlot.liveCount--;
                        }
                        // Intentionally DO NOT remove the priority key here to preserve
                        // the cache handle during an in-flight emission.
                        return;
                    }

                    localEntry = new HandlerActionCache<TU>.Entry(
                        localEntry.handler,
                        localEntry.count - 1
                    );

                    localCache.entries[originalHandler] = localEntry;
                };
            }

            private static Action AddHandlerPreservingPriorityKey<TU>(
                ref Dictionary<int, HandlerActionCache<TU>> handlers,
                TU originalHandler,
                TU augmentedHandler,
                Action deregistration,
                int priority,
                long emissionId
            )
            {
                handlers ??= new Dictionary<int, HandlerActionCache<TU>>();

                if (!handlers.TryGetValue(priority, out HandlerActionCache<TU> cache))
                {
                    cache = new HandlerActionCache<TU>();
                    handlers[priority] = cache;
                }

                if (
                    !cache.entries.TryGetValue(
                        originalHandler,
                        out HandlerActionCache<TU>.Entry entry
                    )
                )
                {
                    entry = new HandlerActionCache<TU>.Entry(augmentedHandler, 0);
                }

                bool firstRegistration = entry.count == 0;
                entry = firstRegistration
                    ? new HandlerActionCache<TU>.Entry(augmentedHandler, 1)
                    : new HandlerActionCache<TU>.Entry(entry.handler, entry.count + 1);

                cache.entries[originalHandler] = entry;
                if (firstRegistration)
                {
                    cache.insertionOrder.Add(originalHandler);
                }
                cache.version++;

                Dictionary<int, HandlerActionCache<TU>> localHandlers = handlers;

                return () =>
                {
                    if (!localHandlers.TryGetValue(priority, out HandlerActionCache<TU> localCache))
                    {
                        return;
                    }

                    if (
                        !localCache.entries.TryGetValue(
                            originalHandler,
                            out HandlerActionCache<TU>.Entry localEntry
                        )
                    )
                    {
                        return;
                    }

                    localCache.version++;

                    deregistration?.Invoke();

                    if (localEntry.count <= 1)
                    {
                        _ = localCache.entries.Remove(originalHandler);
                        _ = localCache.insertionOrder.Remove(originalHandler);
                        localCache.version++;
                        // Intentionally DO NOT remove the priority key here to preserve
                        // the cache handle during an in-flight emission.
                        return;
                    }

                    localEntry = new HandlerActionCache<TU>.Entry(
                        localEntry.handler,
                        localEntry.count - 1
                    );

                    localCache.entries[originalHandler] = localEntry;
                };
            }
        }
    }
}
