namespace DxMessaging.Core.Extensions
{
    using Core;
    using MessageBus;
    using Messages;

    /// <summary>
    /// Convenience extension methods for emitting messages.
    /// </summary>
    /// <remarks>
    /// These helpers select the correct dispatch overloads, handle by-ref messages for structs, and default to the
    /// global bus when none is provided. Overloads exist for Unity <see cref="UnityEngine.GameObject"/> and
    /// <see cref="UnityEngine.Component"/> to convert to <see cref="Core.InstanceId"/> implicitly.
    /// </remarks>
    /// <example>
    /// <code>
    /// // Untargeted (global)
    /// var msg0 = new WorldRegenerated(42);
    /// msg0.Emit();
    ///
    /// // Targeted (InstanceId)
    /// var target = (DxMessaging.Core.InstanceId)gameObject;
    /// var heal = new Heal(10);
    /// heal.EmitTargeted(target);
    ///
    /// // Broadcast (from source)
    /// var source = (DxMessaging.Core.InstanceId)gameObject;
    /// var dmg = new TookDamage(5);
    /// dmg.EmitBroadcast(source);
    ///
    /// // Unity conveniences
    /// var hello = new StringMessage("Hello");
    /// hello.EmitGameObjectTargeted(gameObject);
    /// var saved = new GlobalStringMessage("Saved");
    /// saved.Emit();
    /// </code>
    /// </example>
    public static class MessageExtensions
    {
#if UNITY_2021_3_OR_NEWER
        /// <summary>
        /// Emits a TargetedMessage of the given type.
        /// </summary>
        /// <param name="message">TargetedMessage to emit.</param>
        /// <param name="target">Target that this message is intended for.</param>
        /// <param name="messageBus">MessageBus to emit to. If null, uses the GlobalMessageBus.</param>
        public static void EmitGameObjectTargeted<TMessage>(
            this TMessage message,
            UnityEngine.GameObject target,
            IMessageBus messageBus = null,
            IMessageBusProvider messageBusProvider = null
        )
            where TMessage : class, ITargetedMessage
        {
            InstanceId targetId = target;
            IMessageBus resolvedBus = ResolveMessageBus(messageBus, messageBusProvider);
            if (typeof(TMessage) == typeof(ITargetedMessage))
            {
                resolvedBus.UntypedTargetedBroadcast(targetId, message);
                return;
            }

            resolvedBus.TargetedBroadcast(ref targetId, ref message);
        }

        /// <summary>
        /// Emits a TargetedMessage of the given type.
        /// </summary>
        /// <param name="message">TargetedMessage to emit.</param>
        /// <param name="target">Target that this message is intended for.</param>
        /// <param name="messageBus">MessageBus to emit to. If null, uses the GlobalMessageBus.</param>
        public static void EmitGameObjectTargeted<TMessage>(
            this ref TMessage message,
            UnityEngine.GameObject target,
            IMessageBus messageBus = null,
            IMessageBusProvider messageBusProvider = null
        )
            where TMessage : struct, ITargetedMessage
        {
            InstanceId targetId = target;
            IMessageBus resolvedBus = ResolveMessageBus(messageBus, messageBusProvider);
            resolvedBus.TargetedBroadcast(ref targetId, ref message);
        }

        /// <summary>
        /// Emits a TargetedMessage of the given type.
        /// </summary>
        /// <param name="message">TargetedMessage to emit.</param>
        /// <param name="target">Target that this message is intended for.</param>
        /// <param name="messageBus">MessageBus to emit to. If null, uses the GlobalMessageBus.</param>
        public static void EmitComponentTargeted<TMessage>(
            this TMessage message,
            UnityEngine.Component target,
            IMessageBus messageBus = null,
            IMessageBusProvider messageBusProvider = null
        )
            where TMessage : class, ITargetedMessage
        {
            InstanceId targetId = target;
            IMessageBus resolvedBus = ResolveMessageBus(messageBus, messageBusProvider);
            if (typeof(TMessage) == typeof(ITargetedMessage))
            {
                resolvedBus.UntypedTargetedBroadcast(targetId, message);
                return;
            }

            resolvedBus.TargetedBroadcast(ref targetId, ref message);
        }

        /// <summary>
        /// Emits a TargetedMessage of the given type.
        /// </summary>
        /// <param name="message">TargetedMessage to emit.</param>
        /// <param name="target">Target that this message is intended for.</param>
        /// <param name="messageBus">MessageBus to emit to. If null, uses the GlobalMessageBus.</param>
        public static void EmitComponentTargeted<TMessage>(
            this ref TMessage message,
            UnityEngine.Component target,
            IMessageBus messageBus = null,
            IMessageBusProvider messageBusProvider = null
        )
            where TMessage : struct, ITargetedMessage
        {
            InstanceId targetId = target;
            IMessageBus resolvedBus = ResolveMessageBus(messageBus, messageBusProvider);
            resolvedBus.TargetedBroadcast(ref targetId, ref message);
        }
#endif

        /// <summary>
        /// Emits a TargetedMessage of the given type.
        /// </summary>
        /// <param name="message">TargetedMessage to emit.</param>
        /// <param name="target">Target that this message is intended for.</param>
        /// <param name="messageBus">MessageBus to emit to. If null, uses the GlobalMessageBus.</param>
        public static void EmitTargeted<TMessage>(
            this TMessage message,
            InstanceId target,
            IMessageBus messageBus = null,
            IMessageBusProvider messageBusProvider = null
        )
            where TMessage : class, ITargetedMessage
        {
            IMessageBus resolvedBus = ResolveMessageBus(messageBus, messageBusProvider);
            if (typeof(TMessage) == typeof(ITargetedMessage))
            {
                resolvedBus.UntypedTargetedBroadcast(target, message);
                return;
            }

            resolvedBus.TargetedBroadcast(ref target, ref message);
        }

        /// <summary>
        /// Emits a TargetedMessage of the given type.
        /// </summary>
        /// <param name="message">TargetedMessage to emit.</param>
        /// <param name="target">Target that this message is intended for.</param>
        /// <param name="messageBus">MessageBus to emit to. If null, uses the GlobalMessageBus.</param>
        public static void EmitTargeted<TMessage>(
            this ref TMessage message,
            InstanceId target,
            IMessageBus messageBus = null,
            IMessageBusProvider messageBusProvider = null
        )
            where TMessage : struct, ITargetedMessage
        {
            IMessageBus resolvedBus = ResolveMessageBus(messageBus, messageBusProvider);
            resolvedBus.TargetedBroadcast(ref target, ref message);
        }

        /// <summary>
        /// Emits an UntargetedMessage of the given type.
        /// </summary>
        /// <param name="message">UntargetedMessage to emit.</param>
        /// <param name="messageBus">MessageBus to emit to. If null, uses the GlobalMessageBus.</param>
        public static void EmitUntargeted<TMessage>(
            this TMessage message,
            IMessageBus messageBus = null,
            IMessageBusProvider messageBusProvider = null
        )
            where TMessage : class, IUntargetedMessage
        {
            IMessageBus resolvedBus = ResolveMessageBus(messageBus, messageBusProvider);
            if (typeof(TMessage) == typeof(IUntargetedMessage))
            {
                resolvedBus.UntypedUntargetedBroadcast(message);
                return;
            }

            resolvedBus.UntargetedBroadcast(ref message);
        }

        /// <summary>
        /// Emits an untargeted message (global broadcast).
        /// </summary>
        /// <remarks>
        /// This is a convenience shorthand for <c>EmitUntargeted</c> that improves readability when emitting
        /// messages globally. If <paramref name="messageBus"/> is <c>null</c>, the global bus is used.
        /// </remarks>
        /// <example>
        /// <code>
        /// [DxUntargetedMessage]
        /// public readonly partial struct SceneLoaded { public readonly int buildIndex; }
        ///
        /// var m = new SceneLoaded(3);
        /// m.Emit();                     // shorthand
        /// m.EmitUntargeted();           // equivalent
        /// m.Emit(MessageHandler.MessageBus); // explicit bus
        /// </code>
        /// </example>
        /// <param name="message">Untargeted message to emit.</param>
        /// <param name="messageBus">MessageBus to emit to. If null, uses the GlobalMessageBus.</param>
        public static void Emit<TMessage>(
            this TMessage message,
            IMessageBus messageBus = null,
            IMessageBusProvider messageBusProvider = null
        )
            where TMessage : class, IUntargetedMessage
        {
            IMessageBus resolvedBus = ResolveMessageBus(messageBus, messageBusProvider);
            if (typeof(TMessage) == typeof(IUntargetedMessage))
            {
                resolvedBus.UntypedUntargetedBroadcast(message);
                return;
            }

            resolvedBus.UntargetedBroadcast(ref message);
        }

        /// <summary>
        /// Emits an UntargetedMessage of the given type.
        /// </summary>
        /// <param name="message">UntargetedMessage to emit.</param>
        /// <param name="messageBus">MessageBus to emit to. If null, uses the GlobalMessageBus.</param>
        public static void EmitUntargeted<TMessage>(
            this ref TMessage message,
            IMessageBus messageBus = null,
            IMessageBusProvider messageBusProvider = null
        )
            where TMessage : struct, IUntargetedMessage
        {
            IMessageBus resolvedBus = ResolveMessageBus(messageBus, messageBusProvider);
            resolvedBus.UntargetedBroadcast(ref message);
        }

        /// <summary>
        /// Emits an untargeted message (global broadcast).
        /// </summary>
        /// <remarks>
        /// This is a convenience shorthand for <c>EmitUntargeted</c> that improves readability when emitting
        /// struct messages globally by reference. If <paramref name="messageBus"/> is <c>null</c>, the global
        /// bus is used.
        /// </remarks>
        /// <example>
        /// <code>
        /// [DxUntargetedMessage]
        /// public readonly partial struct SettingsChanged { public readonly float volume; }
        ///
        /// var m = new SettingsChanged(0.8f);
        /// m.Emit();           // shorthand
        /// m.EmitUntargeted(); // equivalent
        /// </code>
        /// </example>
        /// <param name="message">Untargeted message to emit.</param>
        /// <param name="messageBus">MessageBus to emit to. If null, uses the GlobalMessageBus.</param>
        public static void Emit<TMessage>(
            this ref TMessage message,
            IMessageBus messageBus = null,
            IMessageBusProvider messageBusProvider = null
        )
            where TMessage : struct, IUntargetedMessage
        {
            IMessageBus resolvedBus = ResolveMessageBus(messageBus, messageBusProvider);
            resolvedBus.UntargetedBroadcast(ref message);
        }

        /// <summary>
        /// Emits a targeted message to the specified <see cref="InstanceId"/>.
        /// </summary>
        /// <remarks>
        /// Simple shorthand for targeted emission using an <see cref="InstanceId"/>. In Unity, <see cref="InstanceId"/>
        /// is implicitly convertible from both <see cref="UnityEngine.GameObject"/> and <see cref="UnityEngine.Component"/>.
        /// Be explicit about what you intend to target:
        /// - If your listeners registered via <c>RegisterGameObjectTargeted</c>, emit at a GameObject.
        /// - If your listeners registered via <c>RegisterComponentTargeted</c>, emit at that Component.
        ///
        /// Caution: passing <c>this</c> from a <c>MonoBehaviour</c> targets the Component, not its GameObject. If your
        /// handlers were registered with <c>RegisterGameObjectTargeted</c>, they will not receive a Component-targeted
        /// emission. Prefer the explicit helpers (<c>EmitGameObjectTargeted</c>/<c>EmitComponentTargeted</c>) when in doubt.
        /// </remarks>
        /// <example>
        /// <code>
        /// [DxTargetedMessage]
        /// public readonly partial struct Heal { public readonly int amount; }
        ///
        /// var heal = new Heal(10);
        /// heal.EmitAt((InstanceId)gameObject);     // OK: targets GameObject
        /// heal.EmitGameObjectTargeted(gameObject); // explicit, preferred in Unity code
        ///
        /// // Pitfall: targets the Component, not the GameObject
        /// heal.EmitAt((InstanceId)this);           // GameObject-targeted listeners will not receive this
        /// </code>
        /// </example>
        public static void EmitAt<TMessage>(
            this ref TMessage message,
            InstanceId target,
            IMessageBus messageBus = null,
            IMessageBusProvider messageBusProvider = null
        )
            where TMessage : struct, ITargetedMessage
        {
            IMessageBus resolvedBus = ResolveMessageBus(messageBus, messageBusProvider);
            resolvedBus.TargetedBroadcast(ref target, ref message);
        }

        /// <summary>
        /// Emits a broadcast message from the specified <see cref="InstanceId"/> source.
        /// </summary>
        /// <remarks>
        /// Simple shorthand for sourced emission using an <see cref="InstanceId"/>. In Unity, <see cref="InstanceId"/>
        /// is implicitly convertible from both <see cref="UnityEngine.GameObject"/> and <see cref="UnityEngine.Component"/>.
        /// Be explicit about what you intend to identify as the source:
        /// - If listeners registered via <c>RegisterGameObjectBroadcast</c>, emit from a GameObject.
        /// - If listeners registered via <c>RegisterComponentBroadcast</c>, emit from that Component.
        ///
        /// Caution: passing <c>this</c> from a <c>MonoBehaviour</c> identifies the Component as the source. If listeners
        /// registered for GameObject sources, they will not receive it. Prefer the explicit helpers
        /// (<c>EmitGameObjectBroadcast</c>/<c>EmitComponentBroadcast</c>) when in doubt.
        /// </remarks>
        /// <example>
        /// <code>
        /// [DxBroadcastMessage]
        /// public readonly partial struct TookDamage { public readonly int amount; }
        ///
        /// var dmg = new TookDamage(5);
        /// dmg.EmitFrom((InstanceId)gameObject);        // OK: from GameObject
        /// dmg.EmitGameObjectBroadcast(gameObject);     // explicit, preferred in Unity code
        ///
        /// // Pitfall: identifies the Component as source (won't match GO-source registrations)
        /// dmg.EmitFrom((InstanceId)this);
        /// </code>
        /// </example>
        public static void EmitFrom<TMessage>(
            this ref TMessage message,
            InstanceId source,
            IMessageBus messageBus = null,
            IMessageBusProvider messageBusProvider = null
        )
            where TMessage : struct, IBroadcastMessage
        {
            IMessageBus resolvedBus = ResolveMessageBus(messageBus, messageBusProvider);
            resolvedBus.SourcedBroadcast(ref source, ref message);
        }

#if UNITY_2021_3_OR_NEWER
        /// <summary>
        /// Emits a BroadcastMessage of the given type.
        /// </summary>
        /// <param name="message">BroadcastMessage to emit.</param>
        /// <param name="source">Source of this message.</param>
        /// <param name="messageBus">MessageBus to emit to. If null, uses the GlobalMessageBus.</param>
        public static void EmitGameObjectBroadcast<TMessage>(
            this TMessage message,
            UnityEngine.GameObject source,
            IMessageBus messageBus = null,
            IMessageBusProvider messageBusProvider = null
        )
            where TMessage : class, IBroadcastMessage
        {
            InstanceId sourceId = source;
            IMessageBus resolvedBus = ResolveMessageBus(messageBus, messageBusProvider);
            if (typeof(TMessage) == typeof(IBroadcastMessage))
            {
                resolvedBus.UntypedSourcedBroadcast(sourceId, message);
                return;
            }

            resolvedBus.SourcedBroadcast(ref sourceId, ref message);
        }

        /// <summary>
        /// Emits a BroadcastMessage of the given type.
        /// </summary>
        /// <param name="message">BroadcastMessage to emit.</param>
        /// <param name="source">Source of this message.</param>
        /// <param name="messageBus">MessageBus to emit to. If null, uses the GlobalMessageBus.</param>
        public static void EmitGameObjectBroadcast<TMessage>(
            this ref TMessage message,
            UnityEngine.GameObject source,
            IMessageBus messageBus = null,
            IMessageBusProvider messageBusProvider = null
        )
            where TMessage : struct, IBroadcastMessage
        {
            InstanceId sourceId = source;
            IMessageBus resolvedBus = ResolveMessageBus(messageBus, messageBusProvider);
            resolvedBus.SourcedBroadcast(ref sourceId, ref message);
        }

        /// <summary>
        /// Emits a BroadcastMessage of the given type from the specified component.
        /// </summary>
        /// <param name="message">BroadcastMessage to emit.</param>
        /// <param name="source">Source of this message.</param>
        /// <param name="messageBus">MessageBus to emit to. If null, uses the GlobalMessageBus.</param>
        public static void EmitComponentBroadcast<TMessage>(
            this TMessage message,
            UnityEngine.Component source,
            IMessageBus messageBus = null,
            IMessageBusProvider messageBusProvider = null
        )
            where TMessage : class, IBroadcastMessage
        {
            InstanceId sourceId = source;
            IMessageBus resolvedBus = ResolveMessageBus(messageBus, messageBusProvider);
            if (typeof(TMessage) == typeof(IBroadcastMessage))
            {
                resolvedBus.UntypedSourcedBroadcast(sourceId, message);
                return;
            }

            resolvedBus.SourcedBroadcast(ref sourceId, ref message);
        }

        /// <summary>
        /// Emits a BroadcastMessage of the given type from the specified component.
        /// </summary>
        /// <param name="message">BroadcastMessage to emit.</param>
        /// <param name="source">Source of this message.</param>
        /// <param name="messageBus">MessageBus to emit to. If null, uses the GlobalMessageBus.</param>
        public static void EmitComponentBroadcast<TMessage>(
            this ref TMessage message,
            UnityEngine.Component source,
            IMessageBus messageBus = null,
            IMessageBusProvider messageBusProvider = null
        )
            where TMessage : struct, IBroadcastMessage
        {
            InstanceId sourceId = source;
            IMessageBus resolvedBus = ResolveMessageBus(messageBus, messageBusProvider);
            resolvedBus.SourcedBroadcast(ref sourceId, ref message);
        }
#endif

        /// <summary>
        /// Emits a BroadcastMessage of the given type from the specified component.
        /// </summary>
        /// <param name="message">BroadcastMessage to emit.</param>
        /// <param name="source">Source of this message.</param>
        /// <param name="messageBus">MessageBus to emit to. If null, uses the GlobalMessageBus.</param>
        public static void EmitBroadcast<TMessage>(
            this TMessage message,
            InstanceId source,
            IMessageBus messageBus = null,
            IMessageBusProvider messageBusProvider = null
        )
            where TMessage : class, IBroadcastMessage
        {
            IMessageBus resolvedBus = ResolveMessageBus(messageBus, messageBusProvider);
            if (typeof(TMessage) == typeof(IBroadcastMessage))
            {
                resolvedBus.UntypedSourcedBroadcast(source, message);
                return;
            }

            resolvedBus.SourcedBroadcast(ref source, ref message);
        }

        /// <summary>
        /// Emits a BroadcastMessage of the given type from the specified component.
        /// </summary>
        /// <param name="message">BroadcastMessage to emit.</param>
        /// <param name="source">Source of this message.</param>
        /// <param name="messageBus">MessageBus to emit to. If null, uses the GlobalMessageBus.</param>
        public static void EmitBroadcast<TMessage>(
            this ref TMessage message,
            InstanceId source,
            IMessageBus messageBus = null,
            IMessageBusProvider messageBusProvider = null
        )
            where TMessage : struct, IBroadcastMessage
        {
            IMessageBus resolvedBus = ResolveMessageBus(messageBus, messageBusProvider);
            resolvedBus.SourcedBroadcast(ref source, ref message);
        }

        /// <summary>
        /// Emits a targeted <see cref="StringMessage"/> to the specified <see cref="InstanceId"/>.
        /// </summary>
        /// <remarks>
        /// Shorthand helper for quick prototyping or diagnostics. In Unity, passing a <c>GameObject</c> vs
        /// a <c>Component</c> will yield different targets. Ensure the target form matches your registration
        /// (<c>RegisterGameObjectTargeted&lt;StringMessage&gt;</c> vs <c>RegisterComponentTargeted&lt;StringMessage&gt;</c>).
        /// </remarks>
        /// <param name="message">Message to send to the target.</param>
        /// <param name="target">Target to send the message to.</param>
        /// <param name="messageBus">MessageBus to emit to. If null, uses the GlobalMessageBus.</param>
        public static void Emit(
            this string message,
            InstanceId target,
            IMessageBus messageBus = null,
            IMessageBusProvider messageBusProvider = null
        )
        {
            StringMessage stringMessage = new(message);
            IMessageBus resolvedBus = ResolveMessageBus(messageBus, messageBusProvider);
            resolvedBus.TargetedBroadcast(ref target, ref stringMessage);
        }

        /// <summary>
        /// Emits a targeted <see cref="StringMessage"/> to the specified <see cref="InstanceId"/>.
        /// </summary>
        /// <remarks>
        /// This is a naming-aligned shorthand identical to <c>Emit(string, InstanceId)</c>. Prefer explicit Unity
        /// helpers when possible: <c>EmitGameObjectTargeted</c> vs <c>EmitComponentTargeted</c> on <see cref="StringMessage"/>.
        /// Caution: passing a <c>Component</c> identifies the component, not its GameObject. Ensure your registration
        /// side matches the target form.
        /// </remarks>
        /// <param name="message">String payload to send.</param>
        /// <param name="target">Target to send the message to.</param>
        /// <param name="messageBus">MessageBus to emit to. If null, uses the GlobalMessageBus.</param>
        public static void EmitAt(
            this string message,
            InstanceId target,
            IMessageBus messageBus = null,
            IMessageBusProvider messageBusProvider = null
        )
        {
            StringMessage stringMessage = new(message);
            IMessageBus resolvedBus = ResolveMessageBus(messageBus, messageBusProvider);
            resolvedBus.TargetedBroadcast(ref target, ref stringMessage);
        }

        /// <summary>
        /// Emits an untargeted <see cref="GlobalStringMessage"/> containing the provided string.
        /// </summary>
        /// <remarks>
        /// Shorthand helper for quick prototyping or diagnostics. For production code, prefer strongly typed
        /// messages over strings for compile-time safety and discoverability.
        /// </remarks>
        /// <param name="message">Message to send globally.</param>
        /// <param name="messageBus">MessageBus to emit to. If null, uses the GlobalMessageBus.</param>
        public static void Emit(
            this string message,
            IMessageBus messageBus = null,
            IMessageBusProvider messageBusProvider = null
        )
        {
            GlobalStringMessage stringMessage = new(message);
            IMessageBus resolvedBus = ResolveMessageBus(messageBus, messageBusProvider);
            resolvedBus.UntargetedBroadcast(ref stringMessage);
        }

        /// <summary>
        /// Emits a broadcast <see cref="SourcedStringMessage"/> from the specified <see cref="InstanceId"/> source.
        /// </summary>
        /// <remarks>
        /// Naming-aligned shorthand for sourced/broadcast string messages. In Unity, <see cref="InstanceId"/> can be
        /// created from a <c>GameObject</c> or a <c>Component</c>. Make sure the form matches listener registration:
        /// - <c>RegisterGameObjectBroadcast&lt;SourcedStringMessage&gt;</c> receives GameObject sources only
        /// - <c>RegisterComponentBroadcast&lt;SourcedStringMessage&gt;</c> receives Component sources only
        ///
        /// Caution: using <c>this</c> inside a <c>MonoBehaviour</c> identifies the Component, not its GameObject.
        /// </remarks>
        /// <param name="message">String payload to broadcast.</param>
        /// <param name="source">Source of the message.</param>
        /// <param name="messageBus">MessageBus to emit to. If null, uses the GlobalMessageBus.</param>
        public static void EmitFrom(
            this string message,
            InstanceId source,
            IMessageBus messageBus = null,
            IMessageBusProvider messageBusProvider = null
        )
        {
            SourcedStringMessage stringMessage = new(message);
            IMessageBus resolvedBus = ResolveMessageBus(messageBus, messageBusProvider);
            resolvedBus.SourcedBroadcast(ref source, ref stringMessage);
        }

        private static IMessageBus ResolveMessageBus(
            IMessageBus explicitBus,
            IMessageBusProvider messageBusProvider
        )
        {
            if (explicitBus != null)
            {
                return explicitBus;
            }

            if (messageBusProvider != null)
            {
                IMessageBus providedBus = messageBusProvider.Resolve();
                if (providedBus != null)
                {
                    return providedBus;
                }
            }

            return MessageHandler.MessageBus;
        }
    }
}
