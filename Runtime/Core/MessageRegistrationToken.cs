namespace DxMessaging.Core
{
    using System;
    using System.Collections.Generic;
    using DataStructure;
    using Diagnostics;
    using MessageBus;
    using Messages;

    /// <summary>
    /// Collects and manages registrations for a specific <see cref="MessageHandler"/>.
    /// </summary>
    /// <remarks>
    /// Staged registrations are created via the various <c>Register*</c> methods and are activated when
    /// <see cref="Enable"/> is called; they are torn down on <see cref="Disable"/>.
    /// This pattern works especially well with Unity lifecycles.
    /// </remarks>
    /// <example>
    /// <code>
    /// // Unity usage
    /// public sealed class InventoryUI : UnityEngine.MonoBehaviour
    /// {
    ///     private DxMessaging.Core.MessageRegistrationToken _token;
    ///     private DxMessaging.Unity.MessagingComponent _messaging;
    ///
    ///     private void Awake()
    ///     {
    ///         _messaging = GetComponent&lt;DxMessaging.Unity.MessagingComponent&gt;();
    ///         _token = _messaging.Create(this);
    ///         _ = _token.RegisterUntargeted&lt;InventoryChanged&gt;(OnInventoryChanged);
    ///         _ = _token.RegisterComponentTargeted&lt;EquipItem&gt;(this, OnEquipItem);
    ///     }
    ///
    ///     private void OnEnable() =&gt; _token.Enable();
    ///     private void OnDisable() =&gt; _token.Disable();
    ///
    ///     private void OnInventoryChanged(ref InventoryChanged msg) { /* update UI */ }
    ///     private void OnEquipItem(ref EquipItem msg) { /* play animation */ }
    /// }
    /// </code>
    /// </example>
    public sealed class MessageRegistrationToken : IDisposable
    {
        /// <summary>
        /// Whether the token is currently enabled (registrations are active).
        /// </summary>
        public bool Enabled => _enabled;

        /// <summary>
        /// When <c>true</c>, collects per-registration call counts and emission history.
        /// </summary>
        public bool DiagnosticMode
        {
            get => _diagnosticMode;
            set => _diagnosticMode = value;
        }

        private readonly MessageHandler _messageHandler;

        private readonly Dictionary<MessageRegistrationHandle, Action> _registrations = new();
        private readonly Dictionary<MessageRegistrationHandle, Action> _deregistrations = new();
        private readonly List<Action> _actionQueue = new();
        internal readonly Dictionary<
            MessageRegistrationHandle,
            MessageRegistrationMetadata
        > _metadata = new();
        internal readonly Dictionary<MessageRegistrationHandle, int> _callCounts = new();
        internal readonly CyclicBuffer<MessageEmissionData> _emissionBuffer = new(
            IMessageBus.GlobalMessageBufferSize
        );

        private IMessageBus _messageBus;
        private bool _enabled;
        private bool _diagnosticMode = IMessageBus.ShouldEnableDiagnostics();

        private MessageRegistrationToken(MessageHandler messageHandler, IMessageBus messageBus)
        {
            _enabled = false;
            _messageHandler =
                messageHandler ?? throw new ArgumentNullException(nameof(messageHandler));
            _messageBus = messageBus;
        }

        private MessageRegistrationHandle RegisterTargetedInternal<T>(
            InstanceId target,
            Action<T> targetedHandler,
            int priority = 0
        )
            where T : ITargetedMessage
        {
            if (_messageHandler == null) // Unity has a bug
            {
                return MessageRegistrationHandle.CreateMessageRegistrationHandle();
            }
            return InternalRegister(
                handle =>
                {
                    return _messageHandler.RegisterTargetedMessageHandler(
                        target,
                        targetedHandler,
                        AugmentedHandler,
                        priority: priority,
                        messageBus: _messageBus
                    );

                    void AugmentedHandler(T message)
                    {
                        targetedHandler(message);
                        if (_diagnosticMode)
                        {
                            _callCounts[handle] = _callCounts.GetValueOrDefault(handle) + 1;
                            _emissionBuffer.Add(new MessageEmissionData(message, target));
                        }
                    }
                },
                () =>
                    new MessageRegistrationMetadata(
                        target,
                        typeof(T),
                        MessageRegistrationType.Targeted,
                        priority
                    )
            );
        }

        private MessageRegistrationHandle RegisterTargetedInternal<T>(
            InstanceId target,
            MessageHandler.FastHandler<T> targetedHandler,
            int priority = 0
        )
            where T : ITargetedMessage
        {
            if (_messageHandler == null) // Unity has a bug
            {
                return MessageRegistrationHandle.CreateMessageRegistrationHandle();
            }

            return InternalRegister(
                handle =>
                {
                    return _messageHandler.RegisterTargetedMessageHandler(
                        target,
                        targetedHandler,
                        AugmentedHandler,
                        priority: priority,
                        messageBus: _messageBus
                    );

                    void AugmentedHandler(ref T message)
                    {
                        targetedHandler(ref message);
                        if (_diagnosticMode)
                        {
                            _callCounts[handle] = _callCounts.GetValueOrDefault(handle) + 1;
                            _emissionBuffer.Add(new MessageEmissionData(message, target));
                        }
                    }
                },
                () =>
                    new MessageRegistrationMetadata(
                        target,
                        typeof(T),
                        MessageRegistrationType.Targeted,
                        priority
                    )
            );
        }

#if UNITY_2021_3_OR_NEWER
        /// <summary>
        /// Stages a registration to accept targeted messages of type <typeparamref name="T"/> directed at the given GameObject.
        /// </summary>
        /// <note>
        /// DOES NOT ACTUALLY REGISTER THE HANDLER IF NOT ENABLED. To register, a call to Enable() is needed.
        /// </note>
        /// <typeparam name="T">Type of message that the handler accepts.</typeparam>
        /// <param name="target">Target of the TargetedMessages to consume.</param>
        /// <param name="targetedHandler">Actual handler functionality.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        public MessageRegistrationHandle RegisterGameObjectTargeted<T>(
            UnityEngine.GameObject target,
            Action<T> targetedHandler,
            int priority = 0
        )
            where T : ITargetedMessage
        {
            return RegisterTargetedInternal(target, targetedHandler, priority: priority);
        }

        /// <summary>
        /// Stages a registration of the provided MessageHandler to accept TargetedMessages of the given type targeted towards the provided target.
        /// </summary>
        /// <note>
        /// DOES NOT ACTUALLY REGISTER THE HANDLER IF NOT ENABLED. To register, a call to Enable() is needed.
        /// </note>
        /// <typeparam name="T">Type of message to handle.</typeparam>
        /// <param name="target">Target GameObject to receive messages for.</param>
        /// <param name="targetedHandler">High-performance handler receiving <typeparamref name="T"/> by ref.</param>
        /// <param name="priority">Execution order. Lower runs earlier; same priority uses registration order.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        /// <example>
        /// <code>
        /// _ = token.RegisterGameObjectTargeted&lt;TookDamage&gt;(gameObject, (ref TookDamage m) =&gt; Apply(m));
        /// token.Enable();
        /// </code>
        /// </example>
        public MessageRegistrationHandle RegisterGameObjectTargeted<T>(
            UnityEngine.GameObject target,
            MessageHandler.FastHandler<T> targetedHandler,
            int priority = 0
        )
            where T : ITargetedMessage
        {
            return RegisterTargetedInternal(target, targetedHandler, priority: priority);
        }

        /// <summary>
        /// Stages a registration to accept targeted messages of type <typeparamref name="T"/> directed at the given Component.
        /// </summary>
        /// <note>
        /// DOES NOT ACTUALLY REGISTER THE HANDLER IF NOT ENABLED. To register, a call to Enable() is needed.
        /// </note>
        /// <typeparam name="T">Type of message to handle.</typeparam>
        /// <param name="target">Target Component to receive messages for.</param>
        /// <param name="targetedHandler">Action-based handler (boxing may occur for structs).</param>
        /// <param name="priority">Execution order. Lower runs earlier; same priority uses registration order.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        /// <example>
        /// <code>
        /// _ = token.RegisterComponentTargeted&lt;TookDamage&gt;(this, OnDamage);
        /// token.Enable();
        /// </code>
        /// </example>
        public MessageRegistrationHandle RegisterComponentTargeted<T>(
            UnityEngine.Component target,
            Action<T> targetedHandler,
            int priority = 0
        )
            where T : ITargetedMessage
        {
            return RegisterTargetedInternal(target, targetedHandler, priority: priority);
        }

        /// <summary>
        /// Stages a registration of the provided MessageHandler to accept TargetedMessages of the given type targeted towards the provided target.
        /// </summary>
        /// <note>
        /// DOES NOT ACTUALLY REGISTER THE HANDLER IF NOT ENABLED. To register, a call to Enable() is needed.
        /// </note>
        /// <typeparam name="T">Type of message that the handler accepts.</typeparam>
        /// <param name="target">Target of the TargetedMessages to consume.</param>
        /// <param name="targetedHandler">Actual handler functionality.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        public MessageRegistrationHandle RegisterComponentTargeted<T>(
            UnityEngine.Component target,
            MessageHandler.FastHandler<T> targetedHandler,
            int priority = 0
        )
            where T : ITargetedMessage
        {
            return RegisterTargetedInternal(target, targetedHandler, priority: priority);
        }

        /// <summary>
        /// Stages a post-processor for targeted messages of type <typeparamref name="T"/> for the given GameObject.
        /// </summary>
        /// <typeparam name="T">Type of message that the handler accepts.</typeparam>
        /// <param name="target">Target GameObject for which to post-process messages.</param>
        /// <param name="targetedPostProcessor">Post-processor invoked after all handlers.</param>
        /// <param name="priority">Execution order. Lower runs earlier; same priority uses registration order.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        /// <example>
        /// <code>
        /// _ = token.RegisterGameObjectTargetedPostProcessor&lt;TookDamage&gt;(gameObject, (ref TookDamage m) =&gt; Log(m));
        /// </code>
        /// </example>
        public MessageRegistrationHandle RegisterGameObjectTargetedPostProcessor<T>(
            UnityEngine.GameObject target,
            MessageHandler.FastHandler<T> targetedPostProcessor,
            int priority = 0
        )
            where T : ITargetedMessage
        {
            return InternalRegister(
                handle =>
                {
                    return _messageHandler.RegisterTargetedPostProcessor(
                        target,
                        targetedPostProcessor,
                        AugmentedHandler,
                        priority,
                        _messageBus
                    );

                    void AugmentedHandler(ref T message)
                    {
                        targetedPostProcessor(ref message);
                        if (_diagnosticMode)
                        {
                            _callCounts[handle] = _callCounts.GetValueOrDefault(handle) + 1;
                            _emissionBuffer.Add(new MessageEmissionData(message, target));
                        }
                    }
                },
                () =>
                    new MessageRegistrationMetadata(
                        target,
                        typeof(T),
                        MessageRegistrationType.TargetedPostProcessor,
                        priority
                    )
            );
        }

        /// <summary>
        /// Stages a registration of the provided PostProcessor to post process TargetedMessages of the given type for the provided target.
        /// </summary>
        /// <typeparam name="T">Type of message that the handler accepts.</typeparam>
        /// <param name="target">Target to post process messages for.</param>
        /// <param name="targetedPostProcessor">Actual post processor functionality.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        public MessageRegistrationHandle RegisterComponentTargetedPostProcessor<T>(
            UnityEngine.Component target,
            MessageHandler.FastHandler<T> targetedPostProcessor,
            int priority = 0
        )
            where T : ITargetedMessage
        {
            return InternalRegister(
                handle =>
                {
                    return _messageHandler.RegisterTargetedPostProcessor(
                        target,
                        targetedPostProcessor,
                        AugmentedHandler,
                        priority,
                        _messageBus
                    );

                    void AugmentedHandler(ref T message)
                    {
                        targetedPostProcessor(ref message);
                        if (_diagnosticMode)
                        {
                            _callCounts[handle] = _callCounts.GetValueOrDefault(handle) + 1;
                            _emissionBuffer.Add(new MessageEmissionData(message, target));
                        }
                    }
                },
                () =>
                    new MessageRegistrationMetadata(
                        target,
                        typeof(T),
                        MessageRegistrationType.TargetedPostProcessor,
                        priority
                    )
            );
        }
#endif

        /// <summary>
        /// Stages a registration of the provided MessageHandler to accept TargetedMessages of the given type targeted towards the provided target.
        /// </summary>
        /// <note>
        /// DOES NOT ACTUALLY REGISTER THE HANDLER IF NOT ENABLED. To register, a call to Enable() is needed.
        /// </note>
        /// <typeparam name="T">Type of message that the handler accepts.</typeparam>
        /// <param name="target">Target of the TargetedMessages to consume.</param>
        /// <param name="targetedHandler">Actual handler functionality.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        public MessageRegistrationHandle RegisterTargeted<T>(
            InstanceId target,
            Action<T> targetedHandler,
            int priority = 0
        )
            where T : ITargetedMessage
        {
            return RegisterTargetedInternal(target, targetedHandler, priority: priority);
        }

        /// <summary>
        /// Stages a registration of the provided MessageHandler to accept TargetedMessages of the given type targeted towards the provided target.
        /// </summary>
        /// <note>
        /// DOES NOT ACTUALLY REGISTER THE HANDLER IF NOT ENABLED. To register, a call to Enable() is needed.
        /// </note>
        /// <typeparam name="T">Type of message that the handler accepts.</typeparam>
        /// <param name="target">Target of the TargetedMessages to consume.</param>
        /// <param name="targetedHandler">Actual handler functionality.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        public MessageRegistrationHandle RegisterTargeted<T>(
            InstanceId target,
            MessageHandler.FastHandler<T> targetedHandler,
            int priority = 0
        )
            where T : ITargetedMessage
        {
            return RegisterTargetedInternal(target, targetedHandler, priority: priority);
        }

        /// <summary>
        /// Stages a registration of the provided PostProcessor to post process TargetedMessages of the given type for the provided target.
        /// </summary>
        /// <typeparam name="T">Type of message that the handler accepts.</typeparam>
        /// <param name="target">Target to post process messages for.</param>
        /// <param name="targetedPostProcessor">Actual post processor functionality.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        public MessageRegistrationHandle RegisterTargetedPostProcessor<T>(
            InstanceId target,
            MessageHandler.FastHandler<T> targetedPostProcessor,
            int priority = 0
        )
            where T : ITargetedMessage
        {
            return InternalRegister(
                handle =>
                {
                    return _messageHandler.RegisterTargetedPostProcessor(
                        target,
                        targetedPostProcessor,
                        AugmentedHandler,
                        priority,
                        _messageBus
                    );

                    void AugmentedHandler(ref T message)
                    {
                        targetedPostProcessor(ref message);
                        if (_diagnosticMode)
                        {
                            _callCounts[handle] = _callCounts.GetValueOrDefault(handle) + 1;
                            _emissionBuffer.Add(new MessageEmissionData(message, target));
                        }
                    }
                },
                () =>
                    new MessageRegistrationMetadata(
                        target,
                        typeof(T),
                        MessageRegistrationType.TargetedPostProcessor,
                        priority
                    )
            );
        }

        /// <summary>
        /// Stages a registration of the provided PostProcessor to post process TargetedMessages of the given type for the provided target.
        /// </summary>
        /// <typeparam name="T">Type of message that the handler accepts.</typeparam>
        /// <param name="target">Target to post process messages for.</param>
        /// <param name="targetedPostProcessor">Actual post processor functionality.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        public MessageRegistrationHandle RegisterTargetedPostProcessor<T>(
            InstanceId target,
            Action<T> targetedPostProcessor,
            int priority = 0
        )
            where T : ITargetedMessage
        {
            return InternalRegister(
                handle =>
                {
                    return _messageHandler.RegisterTargetedPostProcessor(
                        target,
                        targetedPostProcessor,
                        AugmentedHandler,
                        priority,
                        _messageBus
                    );

                    void AugmentedHandler(T message)
                    {
                        targetedPostProcessor(message);
                        if (_diagnosticMode)
                        {
                            _callCounts[handle] = _callCounts.GetValueOrDefault(handle) + 1;
                            _emissionBuffer.Add(new MessageEmissionData(message, target));
                        }
                    }
                },
                () =>
                    new MessageRegistrationMetadata(
                        target,
                        typeof(T),
                        MessageRegistrationType.TargetedPostProcessor,
                        priority
                    )
            );
        }

        /// <summary>
        /// Stages a registration of the provided MessageHandler to accept TargetedMessages of the given type targeted towards anything (including itself).
        /// </summary>
        /// <note>
        /// DOES NOT ACTUALLY REGISTER THE HANDLER IF NOT ENABLED. To register, a call to Enable() is needed.
        /// </note>
        /// <typeparam name="T">Type of message that the handler accepts.</typeparam>
        /// <param name="messageHandler">Actual handler functionality.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        public MessageRegistrationHandle RegisterTargetedWithoutTargeting<T>(
            Action<InstanceId, T> messageHandler,
            int priority = 0
        )
            where T : ITargetedMessage
        {
            if (_messageHandler == null) // Unity has a bug
            {
                return MessageRegistrationHandle.CreateMessageRegistrationHandle();
            }
            return InternalRegister(
                handle =>
                {
                    return _messageHandler.RegisterTargetedWithoutTargeting(
                        messageHandler,
                        AugmentedHandler,
                        priority: priority,
                        messageBus: _messageBus
                    );

                    void AugmentedHandler(InstanceId target, T message)
                    {
                        messageHandler(target, message);
                        if (_diagnosticMode)
                        {
                            _callCounts[handle] = _callCounts.GetValueOrDefault(handle) + 1;
                            _emissionBuffer.Add(new MessageEmissionData(message, target));
                        }
                    }
                },
                () =>
                    new MessageRegistrationMetadata(
                        null,
                        typeof(T),
                        MessageRegistrationType.TargetedWithoutTargeting,
                        priority
                    )
            );
        }

        /// <summary>
        /// Stages a registration of the provided MessageHandler to accept TargetedMessages of the given type targeted towards anything (including itself).
        /// </summary>
        /// <note>
        /// DOES NOT ACTUALLY REGISTER THE HANDLER IF NOT ENABLED. To register, a call to Enable() is needed.
        /// </note>
        /// <typeparam name="T">Type of message that the handler accepts.</typeparam>
        /// <param name="messageHandler">Actual handler functionality.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        public MessageRegistrationHandle RegisterTargetedWithoutTargeting<T>(
            MessageHandler.FastHandlerWithContext<T> messageHandler,
            int priority = 0
        )
            where T : ITargetedMessage
        {
            if (_messageHandler == null) // Unity has a bug
            {
                return MessageRegistrationHandle.CreateMessageRegistrationHandle();
            }
            return InternalRegister(
                handle =>
                {
                    return _messageHandler.RegisterTargetedWithoutTargeting(
                        messageHandler,
                        AugmentedHandler,
                        priority: priority,
                        messageBus: _messageBus
                    );

                    void AugmentedHandler(ref InstanceId target, ref T message)
                    {
                        messageHandler(ref target, ref message);
                        if (_diagnosticMode)
                        {
                            _callCounts[handle] = _callCounts.GetValueOrDefault(handle) + 1;
                            _emissionBuffer.Add(new MessageEmissionData(message, target));
                        }
                    }
                },
                () =>
                    new MessageRegistrationMetadata(
                        null,
                        typeof(T),
                        MessageRegistrationType.TargetedWithoutTargeting,
                        priority
                    )
            );
        }

        /// <summary>
        /// Stages a registration of the provided MessageHandler to post process TargetedMessages of the given type targeted towards anything (including itself).
        /// </summary>
        /// <note>
        /// DOES NOT ACTUALLY REGISTER THE HANDLER IF NOT ENABLED. To register, a call to Enable() is needed.
        /// </note>
        /// <typeparam name="T">Type of message that the handler accepts.</typeparam>
        /// <param name="postProcessor">Actual handler functionality.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        public MessageRegistrationHandle RegisterTargetedWithoutTargetingPostProcessor<T>(
            Action<InstanceId, T> postProcessor,
            int priority = 0
        )
            where T : ITargetedMessage
        {
            if (_messageHandler == null) // Unity has a bug
            {
                return MessageRegistrationHandle.CreateMessageRegistrationHandle();
            }
            return InternalRegister(
                handle =>
                {
                    return _messageHandler.RegisterTargetedWithoutTargetingPostProcessor(
                        postProcessor,
                        AugmentedHandler,
                        priority,
                        _messageBus
                    );

                    void AugmentedHandler(InstanceId target, T message)
                    {
                        postProcessor(target, message);
                        if (_diagnosticMode)
                        {
                            _callCounts[handle] = _callCounts.GetValueOrDefault(handle) + 1;
                            _emissionBuffer.Add(new MessageEmissionData(message, target));
                        }
                    }
                },
                () =>
                    new MessageRegistrationMetadata(
                        null,
                        typeof(T),
                        MessageRegistrationType.TargetedWithoutTargetingPostProcessor,
                        priority
                    )
            );
        }

        /// <summary>
        /// Stages a registration of the provided MessageHandler to post process TargetedMessages of the given type targeted towards anything (including itself).
        /// </summary>
        /// <note>
        /// DOES NOT ACTUALLY REGISTER THE HANDLER IF NOT ENABLED. To register, a call to Enable() is needed.
        /// </note>
        /// <typeparam name="T">Type of message that the handler accepts.</typeparam>
        /// <param name="postProcessor">Actual post processor functionality.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        public MessageRegistrationHandle RegisterTargetedWithoutTargetingPostProcessor<T>(
            MessageHandler.FastHandlerWithContext<T> postProcessor,
            int priority = 0
        )
            where T : ITargetedMessage
        {
            if (_messageHandler == null) // Unity has a bug
            {
                return MessageRegistrationHandle.CreateMessageRegistrationHandle();
            }
            return InternalRegister(
                handle =>
                {
                    return _messageHandler.RegisterTargetedWithoutTargetingPostProcessor(
                        postProcessor,
                        AugmentedHandler,
                        priority,
                        _messageBus
                    );

                    void AugmentedHandler(ref InstanceId target, ref T message)
                    {
                        postProcessor(ref target, ref message);
                        if (_diagnosticMode)
                        {
                            _callCounts[handle] = _callCounts.GetValueOrDefault(handle) + 1;
                            _emissionBuffer.Add(new MessageEmissionData(message, target));
                        }
                    }
                },
                () =>
                    new MessageRegistrationMetadata(
                        null,
                        typeof(T),
                        MessageRegistrationType.TargetedWithoutTargetingPostProcessor,
                        priority
                    )
            );
        }

        /// <summary>
        /// Stages a registration to accept untargeted messages of type <typeparamref name="T"/>.
        /// </summary>
        /// <note>
        /// DOES NOT ACTUALLY REGISTER THE HANDLER IF NOT ENABLED. To register, a call to Enable() is needed.
        /// </note>
        /// <typeparam name="T">Type of message that the handler accepts.</typeparam>
        /// <param name="untargetedHandler">Handler invoked for each emitted <typeparamref name="T"/>.</param>
        /// <param name="priority">Execution order. Lower runs earlier; same priority uses registration order.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        /// <example>
        /// <code>
        /// _ = token.RegisterUntargeted&lt;VideoSettingsChanged&gt;(OnSettingsChanged);
        /// token.Enable();
        /// void OnSettingsChanged(ref VideoSettingsChanged m) { /* refresh UI */ }
        /// </code>
        /// </example>
        public MessageRegistrationHandle RegisterUntargeted<T>(
            Action<T> untargetedHandler,
            int priority = 0
        )
            where T : IUntargetedMessage
        {
            if (_messageHandler == null) // Unity has a bug
            {
                return MessageRegistrationHandle.CreateMessageRegistrationHandle();
            }
            return InternalRegister(
                handle =>
                {
                    return _messageHandler.RegisterUntargetedMessageHandler(
                        untargetedHandler,
                        AugmentedHandler,
                        priority: priority,
                        messageBus: _messageBus
                    );

                    void AugmentedHandler(T message)
                    {
                        untargetedHandler(message);
                        if (_diagnosticMode)
                        {
                            _callCounts[handle] = _callCounts.GetValueOrDefault(handle) + 1;
                            _emissionBuffer.Add(new MessageEmissionData(message));
                        }
                    }
                },
                () =>
                    new MessageRegistrationMetadata(
                        null,
                        typeof(T),
                        MessageRegistrationType.Untargeted,
                        priority
                    )
            );
        }

        /// <summary>
        /// Stages a registration to accept untargeted messages of type <typeparamref name="T"/> (by-ref fast path).
        /// </summary>
        /// <note>
        /// DOES NOT ACTUALLY REGISTER THE HANDLER IF NOT ENABLED. To register, a call to Enable() is needed.
        /// </note>
        /// <typeparam name="T">Type of message that the handler accepts.</typeparam>
        /// <param name="untargetedHandler">High-performance handler that receives <typeparamref name="T"/> by ref.</param>
        /// <param name="priority">Execution order. Lower runs earlier; same priority uses registration order.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        /// <example>
        /// <code>
        /// _ = token.RegisterUntargeted&lt;WorldRegenerated&gt;((ref WorldRegenerated m) =&gt; { /* ... */ });
        /// token.Enable();
        /// </code>
        /// </example>
        public MessageRegistrationHandle RegisterUntargeted<T>(
            MessageHandler.FastHandler<T> untargetedHandler,
            int priority = 0
        )
            where T : IUntargetedMessage
        {
            if (_messageHandler == null) // Unity has a bug
            {
                return MessageRegistrationHandle.CreateMessageRegistrationHandle();
            }
            return InternalRegister(
                handle =>
                {
                    return _messageHandler.RegisterUntargetedMessageHandler(
                        untargetedHandler,
                        AugmentedHandler,
                        priority: priority,
                        messageBus: _messageBus
                    );

                    void AugmentedHandler(ref T message)
                    {
                        untargetedHandler(ref message);
                        if (_diagnosticMode)
                        {
                            _callCounts[handle] = _callCounts.GetValueOrDefault(handle) + 1;
                            _emissionBuffer.Add(new MessageEmissionData(message));
                        }
                    }
                },
                () =>
                    new MessageRegistrationMetadata(
                        null,
                        typeof(T),
                        MessageRegistrationType.Untargeted,
                        priority
                    )
            );
        }

        /// <summary>
        /// Stages a registration of the provided PostProcessor to post process UntargetedMessages of the given type.
        /// </summary>
        /// <typeparam name="T">Type of message that the handler accepts.</typeparam>
        /// <param name="untargetedPostProcessor">Actual post processor functionality.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        public MessageRegistrationHandle RegisterUntargetedPostProcessor<T>(
            MessageHandler.FastHandler<T> untargetedPostProcessor,
            int priority = 0
        )
            where T : IUntargetedMessage
        {
            if (_messageHandler == null)
            {
                return MessageRegistrationHandle.CreateMessageRegistrationHandle();
            }
            return InternalRegister(
                handle =>
                {
                    return _messageHandler.RegisterUntargetedPostProcessor(
                        untargetedPostProcessor,
                        AugmentedHandler,
                        priority,
                        _messageBus
                    );

                    void AugmentedHandler(ref T message)
                    {
                        untargetedPostProcessor(ref message);
                        if (_diagnosticMode)
                        {
                            _callCounts[handle] = _callCounts.GetValueOrDefault(handle) + 1;
                            _emissionBuffer.Add(new MessageEmissionData(message));
                        }
                    }
                },
                () =>
                    new MessageRegistrationMetadata(
                        null,
                        typeof(T),
                        MessageRegistrationType.UntargetedPostProcessor,
                        priority
                    )
            );
        }

        private MessageRegistrationHandle RegisterBroadcastInternal<T>(
            InstanceId source,
            Action<T> broadcastHandler,
            int priority = 0
        )
            where T : IBroadcastMessage
        {
            if (_messageHandler == null) // Unity has a bug
            {
                return MessageRegistrationHandle.CreateMessageRegistrationHandle();
            }
            return InternalRegister(
                handle =>
                {
                    return _messageHandler.RegisterSourcedBroadcastMessageHandler(
                        source,
                        broadcastHandler,
                        AugmentedHandler,
                        priority: priority,
                        messageBus: _messageBus
                    );

                    void AugmentedHandler(T message)
                    {
                        broadcastHandler(message);
                        if (_diagnosticMode)
                        {
                            _callCounts[handle] = _callCounts.GetValueOrDefault(handle) + 1;
                            _emissionBuffer.Add(new MessageEmissionData(message, source));
                        }
                    }
                },
                () =>
                    new MessageRegistrationMetadata(
                        source,
                        typeof(T),
                        MessageRegistrationType.Broadcast,
                        priority
                    )
            );
        }

        private MessageRegistrationHandle RegisterBroadcastInternal<T>(
            InstanceId source,
            MessageHandler.FastHandler<T> broadcastHandler,
            int priority = 0
        )
            where T : IBroadcastMessage
        {
            if (_messageHandler == null) // Unity has a bug
            {
                return MessageRegistrationHandle.CreateMessageRegistrationHandle();
            }
            return InternalRegister(
                handle =>
                {
                    return _messageHandler.RegisterSourcedBroadcastMessageHandler(
                        source,
                        broadcastHandler,
                        AugmentedHandler,
                        priority: priority,
                        messageBus: _messageBus
                    );

                    void AugmentedHandler(ref T message)
                    {
                        broadcastHandler(ref message);
                        if (_diagnosticMode)
                        {
                            _callCounts[handle] = _callCounts.GetValueOrDefault(handle) + 1;
                            _emissionBuffer.Add(new MessageEmissionData(message, source));
                        }
                    }
                },
                () =>
                    new MessageRegistrationMetadata(
                        source,
                        typeof(T),
                        MessageRegistrationType.Broadcast,
                        priority
                    )
            );
        }

        private MessageRegistrationHandle RegisterBroadcastPostProcessorInternal<T>(
            InstanceId source,
            Action<T> broadcastPostProcessor,
            int priority
        )
            where T : IBroadcastMessage
        {
            if (_messageHandler == null)
            {
                return MessageRegistrationHandle.CreateMessageRegistrationHandle();
            }

            return InternalRegister(
                handle =>
                {
                    return _messageHandler.RegisterSourcedBroadcastPostProcessor(
                        source,
                        broadcastPostProcessor,
                        AugmentedHandler,
                        priority,
                        _messageBus
                    );

                    void AugmentedHandler(T message)
                    {
                        broadcastPostProcessor(message);
                        if (_diagnosticMode)
                        {
                            _callCounts[handle] = _callCounts.GetValueOrDefault(handle) + 1;
                            _emissionBuffer.Add(new MessageEmissionData(message, source));
                        }
                    }
                },
                () =>
                    new MessageRegistrationMetadata(
                        source,
                        typeof(T),
                        MessageRegistrationType.BroadcastPostProcessor,
                        priority
                    )
            );
        }

        private MessageRegistrationHandle RegisterBroadcastPostProcessorInternal<T>(
            InstanceId source,
            MessageHandler.FastHandler<T> broadcastPostProcessor,
            int priority
        )
            where T : IBroadcastMessage
        {
            if (_messageHandler == null)
            {
                return MessageRegistrationHandle.CreateMessageRegistrationHandle();
            }
            return InternalRegister(
                handle =>
                {
                    return _messageHandler.RegisterSourcedBroadcastPostProcessor(
                        source,
                        broadcastPostProcessor,
                        AugmentedHandler,
                        priority,
                        _messageBus
                    );

                    void AugmentedHandler(ref T message)
                    {
                        broadcastPostProcessor(ref message);
                        if (_diagnosticMode)
                        {
                            _callCounts[handle] = _callCounts.GetValueOrDefault(handle) + 1;
                            _emissionBuffer.Add(new MessageEmissionData(message, source));
                        }
                    }
                },
                () =>
                    new MessageRegistrationMetadata(
                        source,
                        typeof(T),
                        MessageRegistrationType.BroadcastPostProcessor,
                        priority
                    )
            );
        }

#if UNITY_2021_3_OR_NEWER
        /// <summary>
        /// Stages a registration to accept broadcast messages of type <typeparamref name="T"/> from a given source.
        /// </summary>
        /// <note>
        /// DOES NOT ACTUALLY REGISTER THE HANDLER IF NOT ENABLED. To register, a call to Enable() is needed.
        /// </note>
        /// <typeparam name="T">Type of the message that the handler accepts.</typeparam>
        /// <param name="source">Id of the source for BroadcastMessages to listen for.</param>
        /// <param name="broadcastHandler">Actual handler functionality.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        public MessageRegistrationHandle RegisterGameObjectBroadcast<T>(
            UnityEngine.GameObject source,
            Action<T> broadcastHandler,
            int priority = 0
        )
            where T : IBroadcastMessage
        {
            return RegisterBroadcastInternal(source, broadcastHandler, priority: priority);
        }

        /// <summary>
        /// Stages a registration to accept broadcast messages of type <typeparamref name="T"/> regardless of source.
        /// </summary>
        /// <note>
        /// DOES NOT ACTUALLY REGISTER THE HANDLER IF NOT ENABLED. To register, a call to Enable() is needed.
        /// </note>
        /// <typeparam name="T">Type of the message that the handler accepts.</typeparam>
        /// <param name="source">Id of the source for BroadcastMessages to listen for.</param>
        /// <param name="broadcastHandler">Actual handler functionality.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        public MessageRegistrationHandle RegisterGameObjectBroadcast<T>(
            UnityEngine.GameObject source,
            MessageHandler.FastHandler<T> broadcastHandler,
            int priority = 0
        )
            where T : IBroadcastMessage
        {
            return RegisterBroadcastInternal(source, broadcastHandler, priority: priority);
        }

        /// <summary>
        /// Stages a registration of the provided PostProcessor to post process BroadcastMessages of the given type for the given GameObject.
        /// </summary>
        /// <typeparam name="T">Type of message that the handler accepts.</typeparam>
        /// <param name="source">Source of the messages.</param>
        /// <param name="broadcastPostProcessor">Actual post processor logic.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        public MessageRegistrationHandle RegisterGameObjectBroadcastPostProcessor<T>(
            UnityEngine.GameObject source,
            Action<T> broadcastPostProcessor,
            int priority = 0
        )
            where T : IBroadcastMessage
        {
            return RegisterBroadcastPostProcessorInternal(source, broadcastPostProcessor, priority);
        }

        /// <summary>
        /// Stages a registration of the provided PostProcessor to post process BroadcastMessages of the given type for the given GameObject.
        /// </summary>
        /// <typeparam name="T">Type of message that the handler accepts.</typeparam>
        /// <param name="source">Source of the messages.</param>
        /// <param name="broadcastPostProcessor">Actual post processor logic.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        public MessageRegistrationHandle RegisterGameObjectBroadcastPostProcessor<T>(
            UnityEngine.GameObject source,
            MessageHandler.FastHandler<T> broadcastPostProcessor,
            int priority = 0
        )
            where T : IBroadcastMessage
        {
            return RegisterBroadcastPostProcessorInternal(source, broadcastPostProcessor, priority);
        }

        /// <summary>
        /// Stages a registration of the provided MessageHandler to accept BroadcastMessages of the given type.
        /// </summary>
        /// <note>
        /// DOES NOT ACTUALLY REGISTER THE HANDLER IF NOT ENABLED. To register, a call to Enable() is needed.
        /// </note>
        /// <typeparam name="T">Type of the message that the handler accepts.</typeparam>
        /// <param name="source">The component source for BroadcastMessages to listen for.</param>
        /// <param name="broadcastHandler">Actual handler functionality.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        public MessageRegistrationHandle RegisterComponentBroadcast<T>(
            UnityEngine.Component source,
            Action<T> broadcastHandler,
            int priority = 0
        )
            where T : IBroadcastMessage
        {
            return RegisterBroadcastInternal(source, broadcastHandler, priority);
        }

        /// <summary>
        /// Stages a registration of the provided MessageHandler to accept BroadcastMessages of the given type.
        /// </summary>
        /// <note>
        /// DOES NOT ACTUALLY REGISTER THE HANDLER IF NOT ENABLED. To register, a call to Enable() is needed.
        /// </note>
        /// <typeparam name="T">Type of the message that the handler accepts.</typeparam>
        /// <param name="source">The component source for BroadcastMessages to listen for.</param>
        /// <param name="broadcastHandler">Actual handler functionality.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        public MessageRegistrationHandle RegisterComponentBroadcast<T>(
            UnityEngine.Component source,
            MessageHandler.FastHandler<T> broadcastHandler,
            int priority = 0
        )
            where T : IBroadcastMessage
        {
            return RegisterBroadcastInternal(source, broadcastHandler, priority);
        }

        /// <summary>
        /// Stages a registration of the provided PostProcessor to post process BroadcastMessages of the given type for the given component.
        /// </summary>
        /// <typeparam name="T">Type of message that the handler accepts.</typeparam>
        /// <param name="source">Source of the messages.</param>
        /// <param name="broadcastPostProcessor">Actual post processor logic.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        public MessageRegistrationHandle RegisterComponentBroadcastPostProcessor<T>(
            UnityEngine.Component source,
            Action<T> broadcastPostProcessor,
            int priority = 0
        )
            where T : IBroadcastMessage
        {
            if (_messageHandler == null)
            {
                return MessageRegistrationHandle.CreateMessageRegistrationHandle();
            }
            return InternalRegister(
                handle =>
                {
                    return _messageHandler.RegisterSourcedBroadcastPostProcessor(
                        source,
                        broadcastPostProcessor,
                        AugmentedHandler,
                        priority: priority,
                        _messageBus
                    );

                    void AugmentedHandler(T message)
                    {
                        broadcastPostProcessor(message);
                        if (_diagnosticMode)
                        {
                            _callCounts[handle] = _callCounts.GetValueOrDefault(handle) + 1;
                            _emissionBuffer.Add(new MessageEmissionData(message, source));
                        }
                    }
                },
                () =>
                    new MessageRegistrationMetadata(
                        source,
                        typeof(T),
                        MessageRegistrationType.BroadcastPostProcessor,
                        priority
                    )
            );
        }

        /// <summary>
        /// Stages a registration of the provided PostProcessor to post process BroadcastMessages of the given type for the given component.
        /// </summary>
        /// <typeparam name="T">Type of message that the handler accepts.</typeparam>
        /// <param name="source">Source of the messages.</param>
        /// <param name="broadcastPostProcessor">Actual post processor logic.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        public MessageRegistrationHandle RegisterComponentBroadcastPostProcessor<T>(
            UnityEngine.Component source,
            MessageHandler.FastHandler<T> broadcastPostProcessor,
            int priority = 0
        )
            where T : IBroadcastMessage
        {
            if (_messageHandler == null)
            {
                return MessageRegistrationHandle.CreateMessageRegistrationHandle();
            }
            return InternalRegister(
                handle =>
                {
                    return _messageHandler.RegisterSourcedBroadcastPostProcessor(
                        source,
                        broadcastPostProcessor,
                        AugmentedHandler,
                        priority: priority,
                        _messageBus
                    );

                    void AugmentedHandler(ref T message)
                    {
                        broadcastPostProcessor(ref message);
                        if (_diagnosticMode)
                        {
                            _callCounts[handle] = _callCounts.GetValueOrDefault(handle) + 1;
                            _emissionBuffer.Add(new MessageEmissionData(message, source));
                        }
                    }
                },
                () =>
                    new MessageRegistrationMetadata(
                        source,
                        typeof(T),
                        MessageRegistrationType.BroadcastPostProcessor,
                        priority
                    )
            );
        }
#endif

        /// <summary>
        /// Stages a registration of the provided MessageHandler to accept BroadcastMessages of the given type.
        /// </summary>
        /// <note>
        /// DOES NOT ACTUALLY REGISTER THE HANDLER IF NOT ENABLED. To register, a call to Enable() is needed.
        /// </note>
        /// <typeparam name="T">Type of the message to handle.</typeparam>
        /// <param name="source">Source <see cref="InstanceId"/> to listen to.</param>
        /// <param name="broadcastHandler">Handler invoked for messages from <paramref name="source"/>.</param>
        /// <param name="priority">Execution order. Lower runs earlier; same priority uses registration order.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        /// <example>
        /// <code>
        /// var enemy = (DxMessaging.Core.InstanceId)enemyGameObject;
        /// _ = token.RegisterBroadcast&lt;TookDamage&gt;(enemy, (ref TookDamage m) =&gt; OnEnemyDamaged(m));
        /// </code>
        /// </example>
        public MessageRegistrationHandle RegisterBroadcast<T>(
            InstanceId source,
            Action<T> broadcastHandler,
            int priority = 0
        )
            where T : IBroadcastMessage
        {
            return RegisterBroadcastInternal(source, broadcastHandler, priority: priority);
        }

        /// <summary>
        /// Stages a registration of the provided MessageHandler to accept BroadcastMessages of the given type.
        /// </summary>
        /// <note>
        /// DOES NOT ACTUALLY REGISTER THE HANDLER IF NOT ENABLED. To register, a call to Enable() is needed.
        /// </note>
        /// <typeparam name="T">Type of the message that the handler accepts.</typeparam>
        /// <param name="source">Source of the messages.</param>
        /// <param name="broadcastHandler">Actual handler functionality.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        public MessageRegistrationHandle RegisterBroadcast<T>(
            InstanceId source,
            MessageHandler.FastHandler<T> broadcastHandler,
            int priority = 0
        )
            where T : IBroadcastMessage
        {
            return RegisterBroadcastInternal(source, broadcastHandler, priority: priority);
        }

        /// <summary>
        /// Stages a registration of the provided PostProcessor to post process BroadcastMessages of the given type for the given source.
        /// </summary>
        /// <typeparam name="T">Type of message that the handler accepts.</typeparam>
        /// <param name="source">Source of the messages.</param>
        /// <param name="broadcastPostProcessor">Actual post processor logic.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        public MessageRegistrationHandle RegisterBroadcastPostProcessor<T>(
            InstanceId source,
            Action<T> broadcastPostProcessor,
            int priority = 0
        )
            where T : IBroadcastMessage
        {
            return RegisterBroadcastPostProcessorInternal(source, broadcastPostProcessor, priority);
        }

        /// <summary>
        /// Stages a registration of the provided PostProcessor to post process BroadcastMessages of the given type for the given source.
        /// </summary>
        /// <typeparam name="T">Type of message that the handler accepts.</typeparam>
        /// <param name="source">Source of the messages.</param>
        /// <param name="broadcastPostProcessor">Actual post processor logic.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        public MessageRegistrationHandle RegisterBroadcastPostProcessor<T>(
            InstanceId source,
            MessageHandler.FastHandler<T> broadcastPostProcessor,
            int priority = 0
        )
            where T : IBroadcastMessage
        {
            return RegisterBroadcastPostProcessorInternal(source, broadcastPostProcessor, priority);
        }

        /// <summary>
        /// Stages a registration of the provided MessageHandler to accept BroadcastMessages of the given type.
        /// </summary>
        /// <note>
        /// DOES NOT ACTUALLY REGISTER THE HANDLER IF NOT ENABLED. To register, a call to Enable() is needed.
        /// </note>
        /// <typeparam name="T">Type of the message that the handler accepts.</typeparam>
        /// <param name="broadcastHandler">Handler invoked for each message; receives the source context.</param>
        /// <param name="priority">Execution order. Lower runs earlier; same priority uses registration order.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        /// <example>
        /// <code>
        /// _ = token.RegisterBroadcastWithoutSource&lt;TookDamage&gt;((DxMessaging.Core.InstanceId src, TookDamage m) =&gt; TrackDamage(src, m));
        /// </code>
        /// </example>
        public MessageRegistrationHandle RegisterBroadcastWithoutSource<T>(
            Action<InstanceId, T> broadcastHandler,
            int priority = 0
        )
            where T : IBroadcastMessage
        {
            if (_messageHandler == null) // Unity has a bug
            {
                return MessageRegistrationHandle.CreateMessageRegistrationHandle();
            }

            return InternalRegister(
                handle =>
                {
                    return _messageHandler.RegisterSourcedBroadcastWithoutSource(
                        broadcastHandler,
                        AugmentedHandler,
                        priority: priority,
                        messageBus: _messageBus
                    );

                    void AugmentedHandler(InstanceId source, T message)
                    {
                        broadcastHandler(source, message);
                        if (_diagnosticMode)
                        {
                            _callCounts[handle] = _callCounts.GetValueOrDefault(handle) + 1;
                            _emissionBuffer.Add(new MessageEmissionData(message, source));
                        }
                    }
                },
                () =>
                    new MessageRegistrationMetadata(
                        null,
                        typeof(T),
                        MessageRegistrationType.BroadcastWithoutSource,
                        priority
                    )
            );
        }

        /// <summary>
        /// Stages a registration of the provided MessageHandler to accept BroadcastMessages of the given type.
        /// </summary>
        /// <note>
        /// DOES NOT ACTUALLY REGISTER THE HANDLER IF NOT ENABLED. To register, a call to Enable() is needed.
        /// </note>
        /// <typeparam name="T">Type of the message that the handler accepts.</typeparam>
        /// <param name="broadcastHandler">Action handler functionality.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        public MessageRegistrationHandle RegisterBroadcastWithoutSource<T>(
            MessageHandler.FastHandlerWithContext<T> broadcastHandler,
            int priority = 0
        )
            where T : IBroadcastMessage
        {
            if (_messageHandler == null) // Unity has a bug
            {
                return MessageRegistrationHandle.CreateMessageRegistrationHandle();
            }

            return InternalRegister(
                handle =>
                {
                    return _messageHandler.RegisterSourcedBroadcastWithoutSource(
                        broadcastHandler,
                        AugmentedHandler,
                        priority: priority,
                        messageBus: _messageBus
                    );

                    void AugmentedHandler(ref InstanceId source, ref T message)
                    {
                        broadcastHandler(ref source, ref message);
                        if (_diagnosticMode)
                        {
                            _callCounts[handle] = _callCounts.GetValueOrDefault(handle) + 1;
                            _emissionBuffer.Add(new MessageEmissionData(message, source));
                        }
                    }
                },
                () =>
                    new MessageRegistrationMetadata(
                        null,
                        typeof(T),
                        MessageRegistrationType.BroadcastWithoutSource,
                        priority
                    )
            );
        }

        /// <summary>
        /// Stages a post-processor for broadcast messages of type <typeparamref name="T"/> regardless of source.
        /// </summary>
        /// <note>
        /// DOES NOT ACTUALLY REGISTER THE HANDLER IF NOT ENABLED. To register, a call to Enable() is needed.
        /// </note>
        /// <typeparam name="T">Type of the message that the handler accepts.</typeparam>
        /// <param name="broadcastHandler">Post-processor invoked after all handlers; receives the source context.</param>
        /// <param name="priority">Execution order. Lower runs earlier; same priority uses registration order.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        /// <example>
        /// <code>
        /// _ = token.RegisterBroadcastWithoutSourcePostProcessor&lt;TookDamage&gt;((DxMessaging.Core.InstanceId src, TookDamage m) =&gt; Log(src, m));
        /// </code>
        /// </example>
        public MessageRegistrationHandle RegisterBroadcastWithoutSourcePostProcessor<T>(
            Action<InstanceId, T> broadcastHandler,
            int priority = 0
        )
            where T : IBroadcastMessage
        {
            if (_messageHandler == null) // Unity has a bug
            {
                return MessageRegistrationHandle.CreateMessageRegistrationHandle();
            }

            return InternalRegister(
                handle =>
                {
                    return _messageHandler.RegisterSourcedBroadcastWithoutSourcePostProcessor(
                        broadcastHandler,
                        AugmentedHandler,
                        priority: priority,
                        _messageBus
                    );

                    void AugmentedHandler(InstanceId source, T message)
                    {
                        broadcastHandler(source, message);
                        if (_diagnosticMode)
                        {
                            _callCounts[handle] = _callCounts.GetValueOrDefault(handle) + 1;
                            _emissionBuffer.Add(new MessageEmissionData(message, source));
                        }
                    }
                },
                () =>
                    new MessageRegistrationMetadata(
                        null,
                        typeof(T),
                        MessageRegistrationType.BroadcastWithoutSourcePostProcessor,
                        priority
                    )
            );
        }

        /// <summary>
        /// Stages a registration of the provided MessageHandler to post post process BroadcastMessages of the given type.
        /// </summary>
        /// <note>
        /// DOES NOT ACTUALLY REGISTER THE HANDLER IF NOT ENABLED. To register, a call to Enable() is needed.
        /// </note>
        /// <typeparam name="T">Type of the message that the handler accepts.</typeparam>
        /// <param name="broadcastHandler">Actual post process functionality.</param>
        /// <param name="priority">Priority at which to run the handler, lower runs earlier than higher.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        public MessageRegistrationHandle RegisterBroadcastWithoutSourcePostProcessor<T>(
            MessageHandler.FastHandlerWithContext<T> broadcastHandler,
            int priority = 0
        )
            where T : IBroadcastMessage
        {
            if (_messageHandler == null) // Unity has a bug
            {
                return MessageRegistrationHandle.CreateMessageRegistrationHandle();
            }

            return InternalRegister(
                handle =>
                {
                    return _messageHandler.RegisterSourcedBroadcastWithoutSourcePostProcessor(
                        broadcastHandler,
                        AugmentedHandler,
                        priority: priority,
                        _messageBus
                    );

                    void AugmentedHandler(ref InstanceId source, ref T message)
                    {
                        broadcastHandler(ref source, ref message);
                        if (_diagnosticMode)
                        {
                            _callCounts[handle] = _callCounts.GetValueOrDefault(handle) + 1;
                            _emissionBuffer.Add(new MessageEmissionData(message, source));
                        }
                    }
                },
                () =>
                    new MessageRegistrationMetadata(
                        null,
                        typeof(T),
                        MessageRegistrationType.BroadcastWithoutSourcePostProcessor,
                        priority
                    )
            );
        }

        /// <summary>
        /// Stages a registration to accept all messages (global observer).
        /// </summary>
        /// <note>
        /// DOES NOT ACTUALLY REGISTER THE HANDLER IF NOT ENABLED. To register, a call to Enable() is needed.
        /// </note>
        /// <param name="acceptAllUntargeted">Action handler functionality for UntargetedMessages.</param>
        /// <param name="acceptAllTargeted">Action handler functionality for TargetedMessages.</param>
        /// <param name="acceptAllBroadcast">Action handler functionality for BroadcastMessages.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        public MessageRegistrationHandle RegisterGlobalAcceptAll(
            Action<IUntargetedMessage> acceptAllUntargeted,
            Action<InstanceId, ITargetedMessage> acceptAllTargeted,
            Action<InstanceId, IBroadcastMessage> acceptAllBroadcast
        )
        {
            if (_messageHandler == null) // Unity has a bug
            {
                return MessageRegistrationHandle.CreateMessageRegistrationHandle();
            }
            return InternalRegister(
                handle =>
                {
                    return _messageHandler.RegisterGlobalAcceptAll(
                        acceptAllUntargeted,
                        AugmentedUntargeted,
                        acceptAllTargeted,
                        AugmentedTargeted,
                        acceptAllBroadcast,
                        AugmentedBroadcast,
                        _messageBus
                    );

                    void AugmentedUntargeted(IUntargetedMessage message)
                    {
                        acceptAllUntargeted(message);
                        if (_diagnosticMode)
                        {
                            _callCounts[handle] = _callCounts.GetValueOrDefault(handle) + 1;
                            _emissionBuffer.Add(new MessageEmissionData(message));
                        }
                    }

                    void AugmentedTargeted(InstanceId target, ITargetedMessage message)
                    {
                        acceptAllTargeted(target, message);
                        if (_diagnosticMode)
                        {
                            _callCounts[handle] = _callCounts.GetValueOrDefault(handle) + 1;
                            _emissionBuffer.Add(new MessageEmissionData(message, target));
                        }
                    }

                    void AugmentedBroadcast(InstanceId source, IBroadcastMessage message)
                    {
                        acceptAllBroadcast(source, message);
                        if (_diagnosticMode)
                        {
                            _callCounts[handle] = _callCounts.GetValueOrDefault(handle) + 1;
                            _emissionBuffer.Add(new MessageEmissionData(message, source));
                        }
                    }
                },
                () =>
                    new MessageRegistrationMetadata(
                        null,
                        typeof(IMessage),
                        MessageRegistrationType.GlobalAcceptAll,
                        0
                    )
            );
        }

        /// <summary>
        /// Stages a registration of the provided MessageHandler to accept every message that is broadcast.
        /// </summary>
        /// <note>
        /// DOES NOT ACTUALLY REGISTER THE HANDLER IF NOT ENABLED. To register, a call to Enable() is needed.
        /// </note>
        /// <param name="acceptAllUntargeted">Handler for any untargeted message.</param>
        /// <param name="acceptAllTargeted">Handler for any targeted message with target context.</param>
        /// <param name="acceptAllBroadcast">Handler for any broadcast message with source context.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        /// <example>
        /// <code>
        /// _ = token.RegisterGlobalAcceptAll(
        ///     (ref DxMessaging.Core.IUntargetedMessage m) =&gt; UnityEngine.Debug.Log(m.MessageType),
        ///     (ref DxMessaging.Core.InstanceId t, ref DxMessaging.Core.ITargetedMessage m) =&gt; UnityEngine.Debug.Log($"{m.MessageType} to {t}"),
        ///     (ref DxMessaging.Core.InstanceId s, ref DxMessaging.Core.IBroadcastMessage m) =&gt; UnityEngine.Debug.Log($"{m.MessageType} from {s}")
        /// );
        /// </code>
        /// </example>
        public MessageRegistrationHandle RegisterGlobalAcceptAll(
            MessageHandler.FastHandler<IUntargetedMessage> acceptAllUntargeted,
            MessageHandler.FastHandlerWithContext<ITargetedMessage> acceptAllTargeted,
            MessageHandler.FastHandlerWithContext<IBroadcastMessage> acceptAllBroadcast
        )
        {
            if (_messageHandler == null) // Unity has a bug
            {
                return MessageRegistrationHandle.CreateMessageRegistrationHandle();
            }
            return InternalRegister(
                handle =>
                {
                    return _messageHandler.RegisterGlobalAcceptAll(
                        acceptAllUntargeted,
                        AugmentedUntargeted,
                        acceptAllTargeted,
                        AugmentedTargeted,
                        acceptAllBroadcast,
                        AugmentedBroadcast,
                        _messageBus
                    );

                    void AugmentedUntargeted(ref IUntargetedMessage message)
                    {
                        acceptAllUntargeted(ref message);
                        if (_diagnosticMode)
                        {
                            _callCounts[handle] = _callCounts.GetValueOrDefault(handle) + 1;
                            _emissionBuffer.Add(new MessageEmissionData(message));
                        }
                    }

                    void AugmentedTargeted(ref InstanceId target, ref ITargetedMessage message)
                    {
                        acceptAllTargeted(ref target, ref message);
                        if (_diagnosticMode)
                        {
                            _callCounts[handle] = _callCounts.GetValueOrDefault(handle) + 1;
                            _emissionBuffer.Add(new MessageEmissionData(message, target));
                        }
                    }

                    void AugmentedBroadcast(ref InstanceId source, ref IBroadcastMessage message)
                    {
                        acceptAllBroadcast(ref source, ref message);
                        if (_diagnosticMode)
                        {
                            _callCounts[handle] = _callCounts.GetValueOrDefault(handle) + 1;
                            _emissionBuffer.Add(new MessageEmissionData(message, source));
                        }
                    }
                },
                () =>
                    new MessageRegistrationMetadata(
                        null,
                        typeof(IMessage),
                        MessageRegistrationType.GlobalAcceptAll,
                        0
                    )
            );
        }

        /// <summary>
        /// Stages an interceptor that can mutate or cancel untargeted messages of type <typeparamref name="T"/>.
        /// </summary>
        /// <typeparam name="T">Message type to intercept.</typeparam>
        /// <param name="interceptor">Function receiving the message by ref; return false to cancel.</param>
        /// <param name="priority">Execution order; lower runs earlier.</param>
        /// <returns>Registration handle.</returns>
        public MessageRegistrationHandle RegisterUntargetedInterceptor<T>(
            IMessageBus.UntargetedInterceptor<T> interceptor,
            int priority = 0
        )
            where T : IUntargetedMessage
        {
            if (_messageHandler == null)
            {
                return MessageRegistrationHandle.CreateMessageRegistrationHandle();
            }

            return InternalRegister(
                _ => _messageHandler.RegisterUntargetedInterceptor(interceptor, priority),
                () =>
                    new MessageRegistrationMetadata(
                        null,
                        typeof(T),
                        MessageRegistrationType.UntargetedInterceptor,
                        priority
                    )
            );
        }

        /// <summary>
        /// Stages an interceptor that can mutate or cancel broadcast messages of type <typeparamref name="T"/>.
        /// </summary>
        /// <typeparam name="T">Message type to intercept.</typeparam>
        /// <param name="interceptor">Function receiving the source and message by ref; return false to cancel.</param>
        /// <param name="priority">Execution order; lower runs earlier.</param>
        /// <returns>Registration handle.</returns>
        public MessageRegistrationHandle RegisterBroadcastInterceptor<T>(
            IMessageBus.BroadcastInterceptor<T> interceptor,
            int priority = 0
        )
            where T : IBroadcastMessage
        {
            if (_messageHandler == null)
            {
                return MessageRegistrationHandle.CreateMessageRegistrationHandle();
            }

            return InternalRegister(
                _ => _messageHandler.RegisterBroadcastInterceptor(interceptor, priority),
                () =>
                    new MessageRegistrationMetadata(
                        null,
                        typeof(T),
                        MessageRegistrationType.BroadcastInterceptor,
                        priority
                    )
            );
        }

        /// <summary>
        /// Stages an interceptor that can mutate or cancel targeted messages of type <typeparamref name="T"/>.
        /// </summary>
        /// <typeparam name="T">Message type to intercept.</typeparam>
        /// <param name="interceptor">Function receiving the target and message by ref; return false to cancel.</param>
        /// <param name="priority">Execution order; lower runs earlier.</param>
        /// <returns>Registration handle.</returns>
        public MessageRegistrationHandle RegisterTargetedInterceptor<T>(
            IMessageBus.TargetedInterceptor<T> interceptor,
            int priority = 0
        )
            where T : ITargetedMessage
        {
            if (_messageHandler == null)
            {
                return MessageRegistrationHandle.CreateMessageRegistrationHandle();
            }

            return InternalRegister(
                _ => _messageHandler.RegisterTargetedInterceptor(interceptor, priority),
                () =>
                    new MessageRegistrationMetadata(
                        null,
                        typeof(T),
                        MessageRegistrationType.TargetedInterceptor,
                        priority
                    )
            );
        }

        /// <summary>
        /// Handles the actual [de]registration wrapping and (potential) lazy execution.
        /// </summary>
        /// <param name="registerAndGetDeregistration">Proxied registration function that returns a de-registration function.</param>
        /// <param name="metadataProducer">Opaque metadata producer function.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        private MessageRegistrationHandle InternalRegister(
            Func<MessageRegistrationHandle, Action> registerAndGetDeregistration,
            Func<MessageRegistrationMetadata> metadataProducer
        )
        {
            MessageRegistrationHandle handle =
                MessageRegistrationHandle.CreateMessageRegistrationHandle();

            _registrations[handle] = Registration;
            _metadata[handle] = metadataProducer();

            // Generally, registrations should take place before all calls to enable. Just in case, though...
            if (_enabled)
            {
                Registration();
            }

            return handle;

            // We don't want to actually register at this time (might not be awake/enabled) - so we wrap that shit up, to lazy register when we're enabled.
            void Registration()
            {
                Action actualDeregistration = registerAndGetDeregistration(handle);
                _deregistrations[handle] = actualDeregistration;
            }
        }

        /// <summary>
        /// Enables the token if not already enabled. Executes all staged registrations.
        /// </summary>
        /// <note>
        /// Idempotent.
        /// </note>
        /// <example>
        /// <code>
        /// _ = token.RegisterUntargeted&lt;SceneLoaded&gt;(OnScene);
        /// token.Enable(); // handlers now active
        /// </code>
        /// </example>
        public void Enable()
        {
            if (_enabled)
            {
                return;
            }

            if (_registrations is { Count: > 0 })
            {
                _actionQueue.Clear();
                _actionQueue.AddRange(_registrations.Values);
                foreach (Action action in _actionQueue)
                {
                    action();
                }
            }

            _enabled = true;
        }

        /// <summary>
        /// Disables the token if not already disabled. Executes all staged de-registrations.
        /// </summary>
        /// <note>
        /// Idempotent.
        /// </note>
        /// <example>
        /// <code>
        /// token.Disable(); // handlers no longer receive messages
        /// </code>
        /// </example>
        public void Disable()
        {
            if (!_enabled)
            {
                return;
            }

            if (_deregistrations is { Count: > 0 })
            {
                _actionQueue.Clear();
                _actionQueue.AddRange(_deregistrations.Values);
                foreach (Action deregistration in _actionQueue)
                {
                    deregistration?.Invoke();
                }
            }

            // ReSharper disable once ForCanBeConvertedToForeach

            _enabled = false;
        }

        /// <summary>
        /// Disables the token and clears all registrations and de-registrations.
        /// </summary>
        /// <example>
        /// <code>
        /// var h = token.RegisterUntargeted&lt;SceneLoaded&gt;(OnScene);
        /// token.Enable();
        /// token.UnregisterAll(); // clears everything
        /// </code>
        /// </example>
        public void UnregisterAll()
        {
            if (_deregistrations is { Count: > 0 })
            {
                _actionQueue.Clear();
                _actionQueue.AddRange(_deregistrations.Values);
                foreach (Action deregistration in _actionQueue)
                {
                    deregistration?.Invoke();
                }
            }

            _enabled = false;
            _registrations?.Clear();
            _deregistrations?.Clear();
        }

        /// <summary>
        /// Retargets staged registrations to use a new message bus, re-registering active handlers if needed.
        /// </summary>
        /// <param name="messageBus">Bus override to apply. Pass <c>null</c> to resume using the handler default.</param>
        /// <param name="rebindMode">Determines whether existing registrations should move to the supplied bus immediately.</param>
        public void RetargetMessageBus(IMessageBus messageBus, MessageBusRebindMode rebindMode)
        {
#pragma warning disable CS0618 // Type or member is obsolete
            MessageBusRebindMode effectiveMode =
                rebindMode == MessageBusRebindMode.Unknown
#pragma warning restore CS0618 // Type or member is obsolete
                    ? MessageBusRebindMode.RebindActive
                    : rebindMode;

            bool sameBus = ReferenceEquals(_messageBus, messageBus);
            bool rebindActiveRegistrations =
                effectiveMode == MessageBusRebindMode.RebindActive
                && _enabled
                && _deregistrations is { Count: > 0 };
            if (sameBus && !rebindActiveRegistrations)
            {
                return;
            }

            if (rebindActiveRegistrations)
            {
                _actionQueue.Clear();
                _actionQueue.AddRange(_deregistrations.Values);
                foreach (Action deregistration in _actionQueue)
                {
                    deregistration?.Invoke();
                }
            }

            _messageBus = messageBus;

            if (rebindActiveRegistrations && _registrations is { Count: > 0 })
            {
                _actionQueue.Clear();
                _actionQueue.AddRange(_registrations.Values);
                foreach (Action registration in _actionQueue)
                {
                    registration?.Invoke();
                }
            }
        }

        /// <summary>
        /// Removes a single staged registration by handle.
        /// </summary>
        /// <param name="handle">Handle returned from a Register* method.</param>
        /// <example>
        /// <code>
        /// var h = token.RegisterUntargeted&lt;SceneLoaded&gt;(OnScene);
        /// token.RemoveRegistration(h); // de-register just this one
        /// </code>
        /// </example>
        public void RemoveRegistration(MessageRegistrationHandle handle)
        {
            if (_deregistrations.Remove(handle, out Action deregistrationAction))
            {
                deregistrationAction?.Invoke();
            }

            // Drop the matching staged registration and metadata so a later
            // Disable()/Enable() cycle does not silently re-register the
            // handler we were just asked to remove.
            _ = _registrations?.Remove(handle);
            _ = _metadata?.Remove(handle);
            _ = _callCounts?.Remove(handle);
        }

        /// <summary>
        /// Wraps a registration handle in an <see cref="IDisposable"/> that removes it on dispose.
        /// </summary>
        /// <param name="handle">The registration handle to remove when disposed.</param>
        /// <returns>An <see cref="IDisposable"/> that calls <see cref="RemoveRegistration"/> once.</returns>
        public RegistrationDisposable AsDisposable(MessageRegistrationHandle handle)
        {
            return new RegistrationDisposable(this, handle);
        }

        public struct RegistrationDisposable : IDisposable
        {
            private readonly MessageRegistrationToken _token;
            private readonly MessageRegistrationHandle _handle;
            private bool _valid;

            /// <summary>
            /// Creates a disposable wrapper that removes a registration when disposed.
            /// </summary>
            /// <param name="token">Token that owns the registration.</param>
            /// <param name="handle">Handle to remove when disposed.</param>
            public RegistrationDisposable(
                MessageRegistrationToken token,
                MessageRegistrationHandle handle
            )
            {
                _token = token;
                _handle = handle;
                _valid = true;
            }

            /// <summary>
            /// Removes the wrapped registration the first time it is invoked.
            /// </summary>
            public void Dispose()
            {
                // Best-effort idempotence; AsDisposable instances are short-lived and immutable
                if (_valid)
                {
                    _token.RemoveRegistration(_handle);
                }

                _valid = false;
            }
        }

        /// <summary>
        /// Creates a MessagingRegistrationToken that operates on the given handler.
        /// </summary>
        /// <param name="messageHandler">Message handler to register handlers to.</param>
        /// <param name="messageBus">MessageBus to use for this MessageRegistrationToken. Uses the GlobalMessageBus if left null.</param>
        /// <returns>MessagingRegistrationToken bound to the MessageHandler.</returns>
        public static MessageRegistrationToken Create(
            MessageHandler messageHandler,
            IMessageBus messageBus = null
        )
        {
            if (messageHandler == null)
            {
                throw new ArgumentNullException(nameof(messageHandler));
            }

            return new MessageRegistrationToken(messageHandler, messageBus);
        }

        /// <summary>
        /// Removes all staged registrations and releases references to the handler.
        /// </summary>
        public void Dispose()
        {
            UnregisterAll();
        }
    }
}
