namespace DxMessaging.Core
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.ExceptionServices;
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

        // Staged-registration handles in registration order. Dictionary
        // enumeration order is NOT stable across Remove/Add churn, so Enable()
        // (and RetargetMessageBus) replay staged registrations by walking this
        // list instead of _registrations.Values; otherwise a Disable()/Enable()
        // cycle after churn would silently permute the documented
        // "same priority uses registration order" dispatch contract.
        // Invariant: contains exactly the keys of _registrations, in the order
        // InternalRegister staged them (handle ids are monotonically
        // increasing, so this list is also sorted by handle id).
        private readonly List<MessageRegistrationHandle> _registrationOrder = new();
        private readonly Dictionary<
            MessageRegistrationHandle,
            PendingDeregistration
        > _deregistrations = new();
        private readonly List<Action> _actionQueue = new();
        private readonly List<MessageRegistrationHandle> _handleQueue = new();
        internal readonly Dictionary<
            MessageRegistrationHandle,
            MessageRegistrationMetadata
        > _metadata = new();

        // Diagnostics-only collections, allocated lazily on first use. A token whose
        // owner never enables diagnostics (the default -- GlobalDiagnosticsTargets is
        // Off -- and the common player case) never materializes them, saving the
        // dictionary, the cyclic buffer, and the buffer's two backing lists per token.
        // These are exposed as properties under their original field names so the
        // inspector overlay and the diagnostics tests (which read token._callCounts /
        // token._emissionBuffer) compile and behave unchanged; the getter caches into
        // the backing field, so repeated reads return the same instance and an editor
        // read simply materializes an empty collection. The only production writers are
        // the dispatch-time AugmentedHandler bodies, all guarded by _diagnosticMode, so
        // production-with-diagnostics-off never triggers the allocation. Teardown
        // (ClearDiagnosticState / RemoveRegistrationState /
        // PruneRegistrationStateToFailedDeregistrations) clears through the backing
        // fields to avoid materializing a collection just to empty it.
        private Dictionary<MessageRegistrationHandle, int> _callCountsBacking;
        private CyclicBuffer<MessageEmissionData> _emissionBufferBacking;

        internal Dictionary<MessageRegistrationHandle, int> _callCounts =>
            _callCountsBacking ??= new Dictionary<MessageRegistrationHandle, int>();

        internal CyclicBuffer<MessageEmissionData> _emissionBuffer =>
            _emissionBufferBacking ??= new CyclicBuffer<MessageEmissionData>(
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
                    // Diagnostics folded into a single by-ref FastHandler handed
                    // down as the flat invoker; the user's handler is the identity
                    // key and is never invoked for the default slot.
                    return _messageHandler.RegisterTargetedMessageHandler(
                        target,
                        targetedHandler,
                        (MessageHandler.FastHandler<T>)AugmentedHandler,
                        priority: priority,
                        messageBus: _messageBus
                    );

                    void AugmentedHandler(ref T message)
                    {
                        targetedHandler(message);
                        if (_diagnosticMode)
                        {
                            _callCounts[handle] = _callCounts.GetValueOrDefault(handle) + 1;
                            _emissionBuffer.Add(new MessageEmissionData(message, target));
                        }
                    }
                },
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
                    // Diagnostics folded into a single by-ref FastHandler handed
                    // down as the flat invoker; the user's handler is the identity
                    // key and is never invoked for the default slot.
                    return _messageHandler.RegisterTargetedPostProcessor(
                        target,
                        targetedPostProcessor,
                        (MessageHandler.FastHandler<T>)AugmentedHandler,
                        priority,
                        _messageBus
                    );

                    void AugmentedHandler(ref T message)
                    {
                        targetedPostProcessor(message);
                        if (_diagnosticMode)
                        {
                            _callCounts[handle] = _callCounts.GetValueOrDefault(handle) + 1;
                            _emissionBuffer.Add(new MessageEmissionData(message, target));
                        }
                    }
                },
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
                    // Diagnostics folded into a single by-ref-with-context
                    // FastHandlerWithContext handed down as the flat invoker; the
                    // user's handler is the identity key and is never invoked for
                    // the default slot.
                    return _messageHandler.RegisterTargetedWithoutTargeting(
                        messageHandler,
                        (MessageHandler.FastHandlerWithContext<T>)AugmentedHandler,
                        priority: priority,
                        messageBus: _messageBus
                    );

                    void AugmentedHandler(ref InstanceId target, ref T message)
                    {
                        messageHandler(target, message);
                        if (_diagnosticMode)
                        {
                            _callCounts[handle] = _callCounts.GetValueOrDefault(handle) + 1;
                            _emissionBuffer.Add(new MessageEmissionData(message, target));
                        }
                    }
                },
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
                    // Diagnostics folded into a single by-ref-with-context
                    // FastHandlerWithContext handed down as the flat invoker; the
                    // user's handler is the identity key and is never invoked for the
                    // default slot.
                    return _messageHandler.RegisterTargetedWithoutTargetingPostProcessor(
                        postProcessor,
                        (MessageHandler.FastHandlerWithContext<T>)AugmentedHandler,
                        priority,
                        _messageBus
                    );

                    void AugmentedHandler(ref InstanceId target, ref T message)
                    {
                        postProcessor(target, message);
                        if (_diagnosticMode)
                        {
                            _callCounts[handle] = _callCounts.GetValueOrDefault(handle) + 1;
                            _emissionBuffer.Add(new MessageEmissionData(message, target));
                        }
                    }
                },
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
                    // The diagnostics-augmented handler is built once as a by-ref
                    // FastHandler and handed down as the flat invoker, so the
                    // default slot stores a single closure (this one) instead of
                    // an Action wrapper plus a separately-allocated FastHandler
                    // adapter. The user's handler is the identity key; default-slot
                    // dispatch uses the flat invoker, including the legacy Handle*
                    // callback path.
                    return _messageHandler.RegisterUntargetedMessageHandler(
                        untargetedHandler,
                        (MessageHandler.FastHandler<T>)AugmentedHandler,
                        priority: priority,
                        messageBus: _messageBus
                    );

                    void AugmentedHandler(ref T message)
                    {
                        untargetedHandler(message);
                        if (_diagnosticMode)
                        {
                            _callCounts[handle] = _callCounts.GetValueOrDefault(handle) + 1;
                            _emissionBuffer.Add(new MessageEmissionData(message));
                        }
                    }
                },
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
                    // Diagnostics folded into a single by-ref FastHandler handed
                    // down as the flat invoker; the user's handler is the identity
                    // key and is never invoked for the default slot.
                    return _messageHandler.RegisterSourcedBroadcastMessageHandler(
                        source,
                        broadcastHandler,
                        (MessageHandler.FastHandler<T>)AugmentedHandler,
                        priority: priority,
                        messageBus: _messageBus
                    );

                    void AugmentedHandler(ref T message)
                    {
                        broadcastHandler(message);
                        if (_diagnosticMode)
                        {
                            _callCounts[handle] = _callCounts.GetValueOrDefault(handle) + 1;
                            _emissionBuffer.Add(new MessageEmissionData(message, source));
                        }
                    }
                },
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
                    // Diagnostics folded into a single by-ref FastHandler handed
                    // down as the flat invoker; the user's handler is the identity
                    // key and is never invoked for the default slot.
                    return _messageHandler.RegisterSourcedBroadcastPostProcessor(
                        source,
                        broadcastPostProcessor,
                        (MessageHandler.FastHandler<T>)AugmentedHandler,
                        priority,
                        _messageBus
                    );

                    void AugmentedHandler(ref T message)
                    {
                        broadcastPostProcessor(message);
                        if (_diagnosticMode)
                        {
                            _callCounts[handle] = _callCounts.GetValueOrDefault(handle) + 1;
                            _emissionBuffer.Add(new MessageEmissionData(message, source));
                        }
                    }
                },
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
                    // Diagnostics folded into a single by-ref FastHandler handed
                    // down as the flat invoker; the user's handler is the identity
                    // key and is never invoked for the default slot.
                    return _messageHandler.RegisterSourcedBroadcastPostProcessor(
                        source,
                        broadcastPostProcessor,
                        (MessageHandler.FastHandler<T>)AugmentedHandler,
                        priority: priority,
                        _messageBus
                    );

                    void AugmentedHandler(ref T message)
                    {
                        broadcastPostProcessor(message);
                        if (_diagnosticMode)
                        {
                            _callCounts[handle] = _callCounts.GetValueOrDefault(handle) + 1;
                            _emissionBuffer.Add(new MessageEmissionData(message, source));
                        }
                    }
                },
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
                    // Diagnostics folded into a single by-ref-with-context
                    // FastHandlerWithContext handed down as the flat invoker; the
                    // user's handler is the identity key and is never invoked for
                    // the default slot.
                    return _messageHandler.RegisterSourcedBroadcastWithoutSource(
                        broadcastHandler,
                        (MessageHandler.FastHandlerWithContext<T>)AugmentedHandler,
                        priority: priority,
                        messageBus: _messageBus
                    );

                    void AugmentedHandler(ref InstanceId source, ref T message)
                    {
                        broadcastHandler(source, message);
                        if (_diagnosticMode)
                        {
                            _callCounts[handle] = _callCounts.GetValueOrDefault(handle) + 1;
                            _emissionBuffer.Add(new MessageEmissionData(message, source));
                        }
                    }
                },
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
                    // Diagnostics folded into a single by-ref-with-context
                    // FastHandlerWithContext handed down as the flat invoker; the
                    // user's handler is the identity key and is never invoked for the
                    // default slot.
                    return _messageHandler.RegisterSourcedBroadcastWithoutSourcePostProcessor(
                        broadcastHandler,
                        (MessageHandler.FastHandlerWithContext<T>)AugmentedHandler,
                        priority: priority,
                        _messageBus
                    );

                    void AugmentedHandler(ref InstanceId source, ref T message)
                    {
                        broadcastHandler(source, message);
                        if (_diagnosticMode)
                        {
                            _callCounts[handle] = _callCounts.GetValueOrDefault(handle) + 1;
                            _emissionBuffer.Add(new MessageEmissionData(message, source));
                        }
                    }
                },
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
                _ =>
                    _messageHandler.RegisterUntargetedInterceptor(
                        interceptor,
                        priority: priority,
                        messageBus: _messageBus
                    ),
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
                _ =>
                    _messageHandler.RegisterBroadcastInterceptor(
                        interceptor,
                        priority: priority,
                        messageBus: _messageBus
                    ),
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
                _ =>
                    _messageHandler.RegisterTargetedInterceptor(
                        interceptor,
                        priority: priority,
                        messageBus: _messageBus
                    ),
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
        /// <param name="metadata">Registration metadata recorded for the diagnostics inspector overlay and the registration-count warning.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        private MessageRegistrationHandle InternalRegister(
            Func<MessageRegistrationHandle, Action> registerAndGetDeregistration,
            MessageRegistrationMetadata metadata
        )
        {
            MessageRegistrationHandle handle =
                MessageRegistrationHandle.CreateMessageRegistrationHandle();

            _registrations[handle] = Registration;
            _registrationOrder.Add(handle);
            // Metadata is passed by value (a readonly struct) instead of through a
            // Func<MessageRegistrationMetadata> factory: the factory was invoked
            // immediately and unconditionally here, so it never deferred any work --
            // it only cost one delegate (plus its display class) allocation per
            // registration. Constructing the struct at the call site is identical
            // work without that closure. _metadata stays populated for every
            // registration because the inspector overlay and the registration-count
            // warning read it regardless of diagnostics mode.
            _metadata[handle] = metadata;

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
                AddDeregistration(handle, actualDeregistration);
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
                // Replay staged registrations in original registration order
                // (via _registrationOrder) rather than in
                // _registrations.Values enumeration order, which permutes
                // after Remove/Add churn. This preserves the documented
                // equal-priority "registration order" dispatch contract across
                // Disable()/Enable() cycles. Snapshot into _actionQueue first
                // so replay tolerates re-entrant registration mutation.
                QueueRegistrationsInOrder();
                InvokeRegistrationQueueWithRollback();
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
            if (!_enabled && _deregistrations.Count == 0)
            {
                return;
            }

            Exception deregistrationException = InvokeDeregistrationQueue();
            _enabled = _deregistrations.Count > 0;
            if (deregistrationException != null)
            {
                ExceptionDispatchInfo.Capture(deregistrationException).Throw();
            }
        }

        /// <summary>
        /// Disables the token and clears all registrations, de-registrations, and token-local diagnostic state.
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
            Exception deregistrationException = InvokeDeregistrationQueue();
            if (deregistrationException == null)
            {
                _enabled = false;
                _registrations?.Clear();
                _registrationOrder?.Clear();
                ClearDiagnosticState();
                return;
            }

            _enabled = _deregistrations.Count > 0;
            PruneRegistrationStateToFailedDeregistrations();
            ExceptionDispatchInfo.Capture(deregistrationException).Throw();
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

            IMessageBus previousMessageBus = _messageBus;
            List<MessageRegistrationHandle> activeRetargetHandles = rebindActiveRegistrations
                ? new List<MessageRegistrationHandle>(_deregistrations.Keys)
                : null;
            if (rebindActiveRegistrations)
            {
                Exception deregistrationException = InvokeDeregistrationQueue();
                if (deregistrationException != null)
                {
                    RestoreMissingRegistrationsAfterRetargetDeregistrationFailure(
                        previousMessageBus,
                        activeRetargetHandles
                    );
                    _enabled = _deregistrations.Count > 0;
                    ExceptionDispatchInfo.Capture(deregistrationException).Throw();
                }

                _enabled = false;
            }

            _messageBus = messageBus;

            if (rebindActiveRegistrations && _registrations is { Count: > 0 })
            {
                // Mirror Enable(): rebind in original registration order so the
                // equal-priority dispatch order survives a bus retarget.
                QueueRegistrationsInOrder();
                try
                {
                    InvokeRegistrationQueueWithRollback();
                    _enabled = true;
                }
                catch (Exception exception)
                {
                    RestoreRegistrationsAfterRetargetFailure(previousMessageBus);
                    ExceptionDispatchInfo.Capture(exception).Throw();
                    throw;
                }
            }
        }

        private void AddDeregistration(MessageRegistrationHandle handle, Action deregistration)
        {
            if (!_deregistrations.TryGetValue(handle, out PendingDeregistration pending))
            {
                pending = new PendingDeregistration();
                _deregistrations[handle] = pending;
            }

            pending.Add(deregistration);
        }

        private Dictionary<MessageRegistrationHandle, int> SnapshotDeregistrationCounts()
        {
            Dictionary<MessageRegistrationHandle, int> snapshot = new(_deregistrations.Count);
            foreach (
                KeyValuePair<
                    MessageRegistrationHandle,
                    PendingDeregistration
                > entry in _deregistrations
            )
            {
                snapshot[entry.Key] = entry.Value.Count;
            }

            return snapshot;
        }

        private void RestoreMissingRegistrationsAfterRetargetDeregistrationFailure(
            IMessageBus previousMessageBus,
            List<MessageRegistrationHandle> activeRetargetHandles
        )
        {
            _messageBus = previousMessageBus;
            if (activeRetargetHandles == null || activeRetargetHandles.Count == 0)
            {
                return;
            }

            QueueMissingRegistrationsInOrder(activeRetargetHandles);
            try
            {
                InvokeRegistrationQueueWithRollback();
            }
            catch (Exception exception)
            {
                if (MessagingDebug.enabled)
                {
                    MessagingDebug.Log(
                        LogLevel.Error,
                        "Failed to restore registrations after retarget deregistration failure: {0}",
                        exception
                    );
                }
            }
        }

        private void RestoreRegistrationsAfterRetargetFailure(IMessageBus previousMessageBus)
        {
            _messageBus = previousMessageBus;
            if (_registrations.Count == 0)
            {
                _enabled = false;
                return;
            }

            QueueRegistrationsWithoutRetryableDeregistrationsInOrder();
            try
            {
                InvokeRegistrationQueueWithRollback();
                _enabled = true;
            }
            catch (Exception restoreException)
            {
                _enabled = _deregistrations.Count > 0;
                if (MessagingDebug.enabled)
                {
                    MessagingDebug.Log(
                        LogLevel.Error,
                        "Failed to restore registrations after retarget replay failure: {0}",
                        restoreException
                    );
                }
            }
        }

        private void QueueRegistrationsInOrder()
        {
            _actionQueue.Clear();
            int registrationCount = _registrationOrder.Count;
            for (int i = 0; i < registrationCount; ++i)
            {
                if (_registrations.TryGetValue(_registrationOrder[i], out Action registration))
                {
                    _actionQueue.Add(registration);
                }
            }
        }

        private void QueueMissingRegistrationsInOrder(
            List<MessageRegistrationHandle> activeRetargetHandles
        )
        {
            _actionQueue.Clear();
            int registrationCount = _registrationOrder.Count;
            for (int i = 0; i < registrationCount; ++i)
            {
                MessageRegistrationHandle handle = _registrationOrder[i];
                if (
                    !activeRetargetHandles.Contains(handle)
                    || _deregistrations.ContainsKey(handle)
                    || !_registrations.TryGetValue(handle, out Action registration)
                )
                {
                    continue;
                }

                _actionQueue.Add(registration);
            }
        }

        private void QueueRegistrationsWithoutRetryableDeregistrationsInOrder()
        {
            _actionQueue.Clear();
            int registrationCount = _registrationOrder.Count;
            for (int i = 0; i < registrationCount; ++i)
            {
                MessageRegistrationHandle handle = _registrationOrder[i];
                if (
                    _deregistrations.ContainsKey(handle)
                    || !_registrations.TryGetValue(handle, out Action registration)
                )
                {
                    continue;
                }

                _actionQueue.Add(registration);
            }
        }

        private void InvokeActionQueue()
        {
            try
            {
                foreach (Action action in _actionQueue)
                {
                    action?.Invoke();
                }
            }
            finally
            {
                _actionQueue.Clear();
            }
        }

        private Exception InvokeDeregistrationQueue(
            Dictionary<MessageRegistrationHandle, int> baselineCounts = null
        )
        {
            if (_deregistrations.Count == 0)
            {
                return null;
            }

            bool scopedToAddedDeregistrations = baselineCounts != null;
            _handleQueue.Clear();
            _handleQueue.AddRange(_deregistrations.Keys);
            Exception firstException = null;
            try
            {
                foreach (MessageRegistrationHandle handle in _handleQueue)
                {
                    if (!_deregistrations.TryGetValue(handle, out PendingDeregistration pending))
                    {
                        continue;
                    }

                    int startIndex = 0;
                    if (scopedToAddedDeregistrations)
                    {
                        if (baselineCounts.TryGetValue(handle, out int baselineCount))
                        {
                            startIndex = baselineCount;
                        }

                        if (startIndex >= pending.Count)
                        {
                            continue;
                        }
                    }

                    Exception exception = pending.InvokeFrom(startIndex);
                    if (exception != null)
                    {
                        firstException ??= exception;
                    }

                    if (pending.Count == 0)
                    {
                        _deregistrations.Remove(handle);
                    }
                }
            }
            finally
            {
                _handleQueue.Clear();
            }

            return firstException;
        }

        private void InvokeRegistrationQueueWithRollback()
        {
            Dictionary<MessageRegistrationHandle, int> rollbackBaseline =
                SnapshotDeregistrationCounts();
            try
            {
                InvokeActionQueue();
            }
            catch (Exception exception)
            {
                RollBackDeregistrationsAfterRegistrationFailure(rollbackBaseline);
                _enabled = _deregistrations.Count > 0;
                ExceptionDispatchInfo.Capture(exception).Throw();
                throw;
            }
        }

        private void RollBackDeregistrationsAfterRegistrationFailure(
            Dictionary<MessageRegistrationHandle, int> rollbackBaseline
        )
        {
            if (_deregistrations.Count == 0)
            {
                return;
            }

            Exception rollbackException = InvokeDeregistrationQueue(rollbackBaseline);
            if (rollbackException != null && MessagingDebug.enabled)
            {
                MessagingDebug.Log(
                    LogLevel.Error,
                    "Failed to roll back partial registration after token replay failure: {0}",
                    rollbackException
                );
            }
        }

        private void ClearDiagnosticState()
        {
            _metadata.Clear();
            // Clear through the backing fields so an inactive (never-materialized)
            // diagnostics collection is not allocated merely to be emptied.
            _callCountsBacking?.Clear();
            _emissionBufferBacking?.Clear();
        }

        private void PruneRegistrationStateToFailedDeregistrations()
        {
            for (int i = _registrationOrder.Count - 1; i >= 0; --i)
            {
                MessageRegistrationHandle handle = _registrationOrder[i];
                if (_deregistrations.ContainsKey(handle))
                {
                    continue;
                }

                RemoveRegistrationState(handle);
            }

            _emissionBufferBacking?.Clear();
            if (_registrations.Count == 0)
            {
                ClearDiagnosticState();
            }
        }

        private bool RemoveRegistrationState(MessageRegistrationHandle handle)
        {
            bool removedRegistration = _registrations.Remove(handle);
            _ = _registrationOrder.Remove(handle);
            _ = _metadata.Remove(handle);
            _callCountsBacking?.Remove(handle);
            if (removedRegistration && _registrations.Count == 0)
            {
                ClearDiagnosticState();
            }

            return removedRegistration;
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
            if (_deregistrations.TryGetValue(handle, out PendingDeregistration pending))
            {
                Exception deregistrationException = pending.InvokeFrom(0);
                if (pending.Count == 0)
                {
                    _deregistrations.Remove(handle);
                }

                if (deregistrationException != null)
                {
                    ExceptionDispatchInfo.Capture(deregistrationException).Throw();
                }
            }

            // Drop the matching staged registration and metadata so a later
            // Disable()/Enable() cycle does not silently re-register the
            // handler we were just asked to remove.
            RemoveRegistrationState(handle);
        }

        private sealed class PendingDeregistration
        {
            private readonly List<Action> _actions = new();

            internal int Count => _actions.Count;

            internal void Add(Action action)
            {
                _actions.Add(action);
            }

            internal Exception InvokeFrom(int startIndex)
            {
                if (startIndex < 0)
                {
                    startIndex = 0;
                }

                Exception firstException = null;
                for (int i = startIndex; i < _actions.Count; )
                {
                    try
                    {
                        _actions[i]?.Invoke();
                        _actions.RemoveAt(i);
                    }
                    catch (Exception exception)
                    {
                        firstException ??= exception;
                        ++i;
                    }
                }

                return firstException;
            }
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
        /// Removes all staged registrations and clears token-local diagnostic state.
        /// </summary>
        public void Dispose()
        {
            UnregisterAll();
        }
    }
}
