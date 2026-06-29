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

        // Maps each staged registration handle to the unified per-handle Registration
        // object the public Register* methods built. Calling Registration.Register()
        // (re)registers the handler on the bus and returns the matching
        // HandlerDeregistration. The Registration object IS the collapsed staging
        // state: it replaces (a) the per-registration staging Func display class, (b)
        // the staging Func delegate itself, and (c) the nested AugmentedHandler
        // local-function delegate -- the captured target/source, user handler,
        // priority, and kind are now plain fields, and the diagnostics-augmented
        // invoker is an instance method bound to the object (the FastHandler<T> handed
        // to MessageHandler is (FastHandler<T>)registration.AugmentedHandlerScalar).
        // Net ~ -2 managed allocations per registration. The central replay loop pairs
        // the Registration with its handle and performs the AddDeregistration. The
        // re-entrancy snapshot semantics are unchanged: the replay queue captures the
        // Registration reference (exactly as it previously captured the staging Func),
        // so a registration removed mid-replay is still replayed if it was already
        // snapshotted.
        private readonly Dictionary<MessageRegistrationHandle, Registration> _registrations = new();

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

        // Maps a handle to its live de-registration(s). The common case is EXACTLY ONE
        // de-registration per handle, so the single Action is stored INLINE as the dictionary
        // value (presence == pending, removal == done, keep-on-throw == retryable) -- no
        // per-registration PendingDeregistration object. A PendingDeregistration is allocated and
        // substituted ONLY when a second de-registration accumulates on the same handle (the rare
        // retarget-recovery replay; see AddDeregistration). The value is therefore either an
        // Action (1 de-registration) or a PendingDeregistration (2+).
        private readonly Dictionary<MessageRegistrationHandle, object> _deregistrations = new();

        // Snapshot of (handle, Registration object) pairs to replay on Enable() /
        // RetargetMessageBus. Snapshotting before invoking lets replay tolerate
        // re-entrant registration mutation (a handler that registers or removes
        // handlers while it runs). The pair carries the handle so the central replay
        // loop can call Registration.Register() and AddDeregistration without a
        // per-registration wrapper closure.
        private readonly List<StagedRegistration> _registrationReplayQueue = new();
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
                new TargetedRegistration<T>(
                    this,
                    RegistrationKind.TargetedHandlerAction,
                    target,
                    targetedHandler,
                    priority
                ),
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
                new TargetedRegistration<T>(
                    this,
                    RegistrationKind.TargetedHandlerFast,
                    target,
                    targetedHandler,
                    priority
                ),
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
                new TargetedRegistration<T>(
                    this,
                    RegistrationKind.TargetedPostProcessorFast,
                    target,
                    targetedPostProcessor,
                    priority
                ),
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
                new TargetedRegistration<T>(
                    this,
                    RegistrationKind.TargetedPostProcessorFast,
                    target,
                    targetedPostProcessor,
                    priority
                ),
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
                new TargetedRegistration<T>(
                    this,
                    RegistrationKind.TargetedPostProcessorFast,
                    target,
                    targetedPostProcessor,
                    priority
                ),
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
                new TargetedRegistration<T>(
                    this,
                    RegistrationKind.TargetedPostProcessorAction,
                    target,
                    targetedPostProcessor,
                    priority
                ),
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
                new TargetedRegistration<T>(
                    this,
                    RegistrationKind.TargetedWithoutTargetingAction,
                    default,
                    messageHandler,
                    priority
                ),
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
                new TargetedRegistration<T>(
                    this,
                    RegistrationKind.TargetedWithoutTargetingFast,
                    default,
                    messageHandler,
                    priority
                ),
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
                new TargetedRegistration<T>(
                    this,
                    RegistrationKind.TargetedWithoutTargetingPostProcessorAction,
                    default,
                    postProcessor,
                    priority
                ),
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
                new TargetedRegistration<T>(
                    this,
                    RegistrationKind.TargetedWithoutTargetingPostProcessorFast,
                    default,
                    postProcessor,
                    priority
                ),
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
                new UntargetedRegistration<T>(
                    this,
                    RegistrationKind.UntargetedHandlerAction,
                    untargetedHandler,
                    priority
                ),
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
                new UntargetedRegistration<T>(
                    this,
                    RegistrationKind.UntargetedHandlerFast,
                    untargetedHandler,
                    priority
                ),
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
                new UntargetedRegistration<T>(
                    this,
                    RegistrationKind.UntargetedPostProcessorFast,
                    untargetedPostProcessor,
                    priority
                ),
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
                new BroadcastRegistration<T>(
                    this,
                    RegistrationKind.BroadcastHandlerAction,
                    source,
                    broadcastHandler,
                    priority
                ),
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
                new BroadcastRegistration<T>(
                    this,
                    RegistrationKind.BroadcastHandlerFast,
                    source,
                    broadcastHandler,
                    priority
                ),
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
                new BroadcastRegistration<T>(
                    this,
                    RegistrationKind.BroadcastPostProcessorAction,
                    source,
                    broadcastPostProcessor,
                    priority
                ),
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
                new BroadcastRegistration<T>(
                    this,
                    RegistrationKind.BroadcastPostProcessorFast,
                    source,
                    broadcastPostProcessor,
                    priority
                ),
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
                new BroadcastRegistration<T>(
                    this,
                    RegistrationKind.BroadcastPostProcessorAction,
                    source,
                    broadcastPostProcessor,
                    priority
                ),
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
                new BroadcastRegistration<T>(
                    this,
                    RegistrationKind.BroadcastPostProcessorFast,
                    source,
                    broadcastPostProcessor,
                    priority
                ),
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
                new BroadcastRegistration<T>(
                    this,
                    RegistrationKind.BroadcastWithoutSourceAction,
                    default,
                    broadcastHandler,
                    priority
                ),
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
                new BroadcastRegistration<T>(
                    this,
                    RegistrationKind.BroadcastWithoutSourceFast,
                    default,
                    broadcastHandler,
                    priority
                ),
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
                new BroadcastRegistration<T>(
                    this,
                    RegistrationKind.BroadcastWithoutSourcePostProcessorAction,
                    default,
                    broadcastHandler,
                    priority
                ),
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
                new BroadcastRegistration<T>(
                    this,
                    RegistrationKind.BroadcastWithoutSourcePostProcessorFast,
                    default,
                    broadcastHandler,
                    priority
                ),
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
                new GlobalAcceptAllRegistration(
                    this,
                    acceptAllUntargeted,
                    acceptAllTargeted,
                    acceptAllBroadcast
                ),
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
                new GlobalAcceptAllRegistration(
                    this,
                    acceptAllUntargeted,
                    acceptAllTargeted,
                    acceptAllBroadcast
                ),
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
                new UntargetedRegistration<T>(
                    this,
                    RegistrationKind.UntargetedInterceptor,
                    interceptor,
                    priority
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
                new BroadcastRegistration<T>(
                    this,
                    RegistrationKind.BroadcastInterceptor,
                    default,
                    interceptor,
                    priority
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
                new TargetedRegistration<T>(
                    this,
                    RegistrationKind.TargetedInterceptor,
                    default,
                    interceptor,
                    priority
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
        /// <param name="registration">The unified per-handle registration object the public Register* method built. Calling <see cref="Registration.Register"/> (re)registers the handler on the bus and returns the matching de-registration.</param>
        /// <param name="metadata">Registration metadata recorded for the diagnostics inspector overlay and the registration-count warning.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        private MessageRegistrationHandle InternalRegister(
            Registration registration,
            MessageRegistrationMetadata metadata
        )
        {
            MessageRegistrationHandle handle =
                MessageRegistrationHandle.CreateMessageRegistrationHandle();

            registration.Handle = handle;
            _registrations[handle] = registration;
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

            // Generally, registrations should take place before all calls to enable.
            // Just in case, though, register immediately if already enabled. We do not
            // register at staging time when disabled (the owner might not be awake), so
            // the Registration object is retained in _registrations to lazily
            // (re)register on Enable().
            if (_enabled)
            {
                MessageHandler.HandlerDeregistration actualDeregistration = registration.Register();
                AddDeregistration(handle, actualDeregistration);
            }

            return handle;
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
                // Disable()/Enable() cycles. Snapshot into _registrationReplayQueue first
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

        private void AddDeregistration(
            MessageRegistrationHandle handle,
            MessageHandler.HandlerDeregistration deregistration
        )
        {
            if (!_deregistrations.TryGetValue(handle, out object existing))
            {
                // First (and usually only) de-registration: store the object inline.
                _deregistrations[handle] = deregistration;
                return;
            }

            if (existing is PendingDeregistration pending)
            {
                pending.Add(deregistration);
                return;
            }

            // A second de-registration accumulated on this handle: promote the inline object to a
            // PendingDeregistration holder that preserves the ordering / partial-failure / rollback
            // semantics for the multi-de-registration (retarget-recovery replay) case.
            PendingDeregistration promoted = new();
            promoted.Add((MessageHandler.HandlerDeregistration)existing);
            promoted.Add(deregistration);
            _deregistrations[handle] = promoted;
        }

        // Logical de-registration count for a _deregistrations value (inline object == 1).
        private static int DeregistrationCount(object value) =>
            value is PendingDeregistration pending ? pending.Count : 1;

        // Invokes the de-registration tail [startIndex..) for a _deregistrations value. For the
        // inline object (logical Count 1, index 0): on success it is consumed (shouldRemove = true);
        // on throw it is KEPT (retryable, shouldRemove = false); a rollback pass (startIndex &gt; 0)
        // leaves the baseline entry untouched. For a holder it delegates to InvokeFrom, mutating the
        // holder in place. Mirrors the prior PendingDeregistration-only semantics exactly.
        private static Exception InvokeDeregistration(
            object value,
            int startIndex,
            out bool shouldRemove
        )
        {
            if (value is PendingDeregistration pending)
            {
                Exception holderException = pending.InvokeFrom(startIndex);
                shouldRemove = pending.Count == 0;
                return holderException;
            }

            if (startIndex > 0)
            {
                // Rollback baseline pass: the inline head (logical index 0) is below the requested
                // tail, so leave it untouched.
                shouldRemove = false;
                return null;
            }

            try
            {
                ((MessageHandler.HandlerDeregistration)value)?.Deregister();
                shouldRemove = true;
                return null;
            }
            catch (Exception exception)
            {
                // Keep the failed de-registration (retryable), exactly as the holder form does.
                shouldRemove = false;
                return exception;
            }
        }

        private Dictionary<MessageRegistrationHandle, int> SnapshotDeregistrationCounts()
        {
            Dictionary<MessageRegistrationHandle, int> snapshot = new(_deregistrations.Count);
            foreach (KeyValuePair<MessageRegistrationHandle, object> entry in _deregistrations)
            {
                snapshot[entry.Key] = DeregistrationCount(entry.Value);
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
            _registrationReplayQueue.Clear();
            int registrationCount = _registrationOrder.Count;
            for (int i = 0; i < registrationCount; ++i)
            {
                MessageRegistrationHandle handle = _registrationOrder[i];
                if (_registrations.TryGetValue(handle, out Registration registration))
                {
                    _registrationReplayQueue.Add(new StagedRegistration(handle, registration));
                }
            }
        }

        private void QueueMissingRegistrationsInOrder(
            List<MessageRegistrationHandle> activeRetargetHandles
        )
        {
            _registrationReplayQueue.Clear();
            int registrationCount = _registrationOrder.Count;
            for (int i = 0; i < registrationCount; ++i)
            {
                MessageRegistrationHandle handle = _registrationOrder[i];
                if (
                    !activeRetargetHandles.Contains(handle)
                    || _deregistrations.ContainsKey(handle)
                    || !_registrations.TryGetValue(handle, out Registration registration)
                )
                {
                    continue;
                }

                _registrationReplayQueue.Add(new StagedRegistration(handle, registration));
            }
        }

        private void QueueRegistrationsWithoutRetryableDeregistrationsInOrder()
        {
            _registrationReplayQueue.Clear();
            int registrationCount = _registrationOrder.Count;
            for (int i = 0; i < registrationCount; ++i)
            {
                MessageRegistrationHandle handle = _registrationOrder[i];
                if (
                    _deregistrations.ContainsKey(handle)
                    || !_registrations.TryGetValue(handle, out Registration registration)
                )
                {
                    continue;
                }

                _registrationReplayQueue.Add(new StagedRegistration(handle, registration));
            }
        }

        private void InvokeRegistrationReplayQueue()
        {
            try
            {
                foreach (StagedRegistration staged in _registrationReplayQueue)
                {
                    Registration registration = staged.Registration;
                    if (registration == null)
                    {
                        continue;
                    }

                    MessageHandler.HandlerDeregistration actualDeregistration =
                        registration.Register();
                    AddDeregistration(staged.Handle, actualDeregistration);
                }
            }
            finally
            {
                _registrationReplayQueue.Clear();
            }
        }

        /// <summary>
        /// A staged registration captured for replay: the handle plus the unified
        /// per-handle <see cref="Registration"/> object that (re)registers the handler
        /// and returns its <see cref="MessageHandler.HandlerDeregistration"/>.
        /// Snapshotting the object reference (rather than a per-registration wrapper
        /// closure) preserves the original re-entrancy semantics while keeping the
        /// staging state in a single object per registration.
        /// </summary>
        private readonly struct StagedRegistration
        {
            public readonly MessageRegistrationHandle Handle;
            public readonly Registration Registration;

            public StagedRegistration(MessageRegistrationHandle handle, Registration registration)
            {
                Handle = handle;
                Registration = registration;
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
                    if (!_deregistrations.TryGetValue(handle, out object value))
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

                        if (startIndex >= DeregistrationCount(value))
                        {
                            continue;
                        }
                    }

                    Exception exception = InvokeDeregistration(
                        value,
                        startIndex,
                        out bool shouldRemove
                    );
                    if (exception != null)
                    {
                        firstException ??= exception;
                    }

                    if (shouldRemove)
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
                InvokeRegistrationReplayQueue();
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
            if (_deregistrations.TryGetValue(handle, out object value))
            {
                Exception deregistrationException = InvokeDeregistration(
                    value,
                    0,
                    out bool shouldRemove
                );
                if (shouldRemove)
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

        // Holds the live de-registration Actions for a single handle in the MULTI-de-registration
        // case (2+). The common case is EXACTLY ONE de-registration per handle (a staging function
        // registers once and returns one de-registration Action); that single Action is stored
        // INLINE as the _deregistrations dictionary value, so the common path allocates no
        // PendingDeregistration object at all (see AddDeregistration / InvokeDeregistration). This
        // holder is allocated only when a rare second de-registration accumulates on the same
        // handle -- a re-entrant retarget-recovery replay can stage one beyond the rollback
        // baseline -- at which point the inline Action is promoted into this holder's inline head
        // and the second spills to a lazily-allocated overflow list. This stays a class (mutated in
        // place through the _deregistrations dictionary), so reference semantics and every
        // call site are unchanged; only the storage shape changed.
        //
        // Invariant: when Count > 0 the head lives in _hasHead/_head and is the LOGICAL
        // FIRST entry; any further entries follow in _overflow in insertion order. Add
        // appends to the logical tail; InvokeFrom invokes a contiguous logical tail
        // [startIndex..Count), removing each success and KEEPING each failure (retryable),
        // then promotes the first surviving overflow entry into the head slot if the head
        // was consumed -- preserving the exact ordering, partial-failure, and
        // rollback-baseline (startIndex) semantics the List<Action> form had.
        private sealed class PendingDeregistration
        {
            private MessageHandler.HandlerDeregistration _head;
            private bool _hasHead;
            private List<MessageHandler.HandlerDeregistration> _overflow;

            internal int Count => (_hasHead ? 1 : 0) + (_overflow?.Count ?? 0);

            internal void Add(MessageHandler.HandlerDeregistration action)
            {
                // Fill the inline head ONLY when nothing is stored. The empty-overflow
                // clause matters during the transient window inside InvokeFrom where the
                // head has been consumed but overflow survivors remain (before they are
                // promoted): a re-entrant Add then appends to the logical TAIL (overflow),
                // exactly as the List form did, rather than jumping the new entry ahead of
                // the survivors into the head slot. The overflow loop re-reads its Count,
                // so such a tail-appended entry is still invoked in the same pass -- the
                // List form's behavior preserved. (The stored Actions are pure bus
                // de-registration callbacks, so this re-entrancy is not reachable through
                // the public API today; the guard keeps the invariant honest regardless.)
                if (!_hasHead && (_overflow == null || _overflow.Count == 0))
                {
                    _head = action;
                    _hasHead = true;
                    return;
                }

                (_overflow ??= new List<MessageHandler.HandlerDeregistration>()).Add(action);
            }

            internal Exception InvokeFrom(int startIndex)
            {
                if (startIndex < 0)
                {
                    startIndex = 0;
                }

                Exception firstException = null;

                // Logical index 0 is the inline head; logical indices 1.. are _overflow.
                // Invoke the head only when it is within the requested tail (a rollback
                // pass with startIndex > 0 must leave the baseline head untouched).
                if (_hasHead && startIndex <= 0)
                {
                    try
                    {
                        _head?.Deregister();
                        _head = null;
                        _hasHead = false;
                    }
                    catch (Exception exception)
                    {
                        // Keep the failed head (retryable), exactly as the List form did
                        // by advancing past a throwing entry instead of removing it.
                        firstException ??= exception;
                    }
                }

                if (_overflow is { Count: > 0 })
                {
                    // Overflow entry j has logical index 1 + j; invoke those at or past
                    // startIndex. Remove successes, keep failures (retryable).
                    int overflowStart = startIndex <= 1 ? 0 : startIndex - 1;
                    for (int j = overflowStart; j < _overflow.Count; )
                    {
                        try
                        {
                            _overflow[j]?.Deregister();
                            _overflow.RemoveAt(j);
                        }
                        catch (Exception exception)
                        {
                            firstException ??= exception;
                            ++j;
                        }
                    }
                }

                // If the head was consumed but overflow survivors remain, promote the
                // first survivor into the head slot so the head is always the logical
                // first entry (keeping Count and future Adds consistent).
                if (!_hasHead && _overflow is { Count: > 0 })
                {
                    _head = _overflow[0];
                    _hasHead = true;
                    _overflow.RemoveAt(0);
                }

                return firstException;
            }
        }

        // Discriminates the per-handle Registration object's behaviour. Each value
        // pins (a) which MessageHandler.Register* method the kind-switch in
        // Registration.Register() calls and (b) which augmented-invoker body shape
        // (user-delegate type + by-value vs by-ref call) the bound FastHandler/
        // FastHandlerWithContext delegate runs. The values map 1:1 onto the former
        // per-method staging lambdas; the *Action / *Fast suffix mirrors the two
        // public overloads (Action<T>/Action<InstanceId,T> vs FastHandler<T>/
        // FastHandlerWithContext<T>) that previously had distinct AugmentedHandler
        // local functions.
        private enum RegistrationKind
        {
            TargetedHandlerAction,
            TargetedHandlerFast,
            TargetedPostProcessorAction,
            TargetedPostProcessorFast,
            TargetedWithoutTargetingAction,
            TargetedWithoutTargetingFast,
            TargetedWithoutTargetingPostProcessorAction,
            TargetedWithoutTargetingPostProcessorFast,
            TargetedInterceptor,

            UntargetedHandlerAction,
            UntargetedHandlerFast,
            UntargetedPostProcessorFast,
            UntargetedInterceptor,

            BroadcastHandlerAction,
            BroadcastHandlerFast,
            BroadcastPostProcessorAction,
            BroadcastPostProcessorFast,
            BroadcastWithoutSourceAction,
            BroadcastWithoutSourceFast,
            BroadcastWithoutSourcePostProcessorAction,
            BroadcastWithoutSourcePostProcessorFast,
            BroadcastInterceptor,

            GlobalAcceptAllAction,
            GlobalAcceptAllFast,
        }

        // The unified per-handle staging object. Replaces, per registration, the old
        // staging Func<handle, HandlerDeregistration> display class + delegate AND the
        // nested AugmentedHandler local-function delegate. The captured staging state
        // (owning token, handle, user handler delegate, target/source InstanceId,
        // priority, kind) lives in plain fields; the diagnostics-augmented invoker is
        // an instance method bound to this object (so MessageHandler still receives a
        // delegate on the hot path -- no virtual/interface call per dispatch).
        //
        // The base is non-generic so _registrations / the replay queue can hold every
        // registration polymorphically. The constrained MessageHandler.Register*<T>
        // calls each require T : ITargetedMessage / IUntargetedMessage /
        // IBroadcastMessage, which a single Registration<T> (T : IMessage) cannot
        // satisfy; the three concrete subclasses below carry the matching constraint
        // and each run a kind-SWITCH over only the kinds in their family (per the
        // user-accepted "unified object, accept the kind-switch" design -- NOT ~14
        // subclasses). GlobalAcceptAllRegistration is non-generic (its sub-handlers
        // are the fixed IMessage facades).
        private abstract class Registration
        {
            protected readonly MessageRegistrationToken Token;
            public MessageRegistrationHandle Handle;

            protected Registration(MessageRegistrationToken token)
            {
                Token = token;
            }

            // Kind-switch: (re)register on the bus and return the matching
            // HandlerDeregistration, exactly as the former staging lambda did. Reads
            // the token's CURRENT _messageBus (so Enable()/RetargetMessageBus replay
            // binds to the active bus, unchanged from when _messageBus was captured by
            // the staging closure at call time -- the staging closure also read the
            // field, not a snapshot).
            public abstract MessageHandler.HandlerDeregistration Register();
        }

        private sealed class TargetedRegistration<T> : Registration
            where T : ITargetedMessage
        {
            private readonly RegistrationKind _kind;
            private readonly InstanceId _context;
            private readonly object _userHandler;
            private readonly int _priority;

            // Strongly-typed views of _userHandler, resolved ONCE at Register() time
            // (cold) so the per-dispatch augmented invoker calls a typed field directly
            // -- no per-dispatch castclass or kind-switch (which would add an O(handlers)
            // cost on the hot dispatch path). Exactly one is set per registration kind.
            private Action<T> _scalarAction;
            private MessageHandler.FastHandler<T> _scalarFast;
            private Action<InstanceId, T> _contextAction;
            private MessageHandler.FastHandlerWithContext<T> _contextFast;

            internal TargetedRegistration(
                MessageRegistrationToken token,
                RegistrationKind kind,
                InstanceId context,
                object userHandler,
                int priority
            )
                : base(token)
            {
                _kind = kind;
                _context = context;
                _userHandler = userHandler;
                _priority = priority;
            }

            public override MessageHandler.HandlerDeregistration Register()
            {
                MessageHandler messageHandler = Token._messageHandler;
                IMessageBus messageBus = Token._messageBus;
                switch (_kind)
                {
                    case RegistrationKind.TargetedHandlerAction:
                        _scalarAction = (Action<T>)_userHandler;
                        return messageHandler.RegisterTargetedMessageHandler(
                            _context,
                            _scalarAction,
                            AugmentedScalarAction,
                            priority: _priority,
                            messageBus: messageBus
                        );
                    case RegistrationKind.TargetedHandlerFast:
                        _scalarFast = (MessageHandler.FastHandler<T>)_userHandler;
                        return messageHandler.RegisterTargetedMessageHandler(
                            _context,
                            _scalarFast,
                            AugmentedScalarFast,
                            priority: _priority,
                            messageBus: messageBus
                        );
                    case RegistrationKind.TargetedPostProcessorAction:
                        _scalarAction = (Action<T>)_userHandler;
                        return messageHandler.RegisterTargetedPostProcessor(
                            _context,
                            _scalarAction,
                            AugmentedScalarAction,
                            _priority,
                            messageBus
                        );
                    case RegistrationKind.TargetedPostProcessorFast:
                        _scalarFast = (MessageHandler.FastHandler<T>)_userHandler;
                        return messageHandler.RegisterTargetedPostProcessor(
                            _context,
                            _scalarFast,
                            AugmentedScalarFast,
                            _priority,
                            messageBus
                        );
                    case RegistrationKind.TargetedWithoutTargetingAction:
                        _contextAction = (Action<InstanceId, T>)_userHandler;
                        return messageHandler.RegisterTargetedWithoutTargeting(
                            _contextAction,
                            AugmentedContextAction,
                            priority: _priority,
                            messageBus: messageBus
                        );
                    case RegistrationKind.TargetedWithoutTargetingFast:
                        _contextFast = (MessageHandler.FastHandlerWithContext<T>)_userHandler;
                        return messageHandler.RegisterTargetedWithoutTargeting(
                            _contextFast,
                            AugmentedContextFast,
                            priority: _priority,
                            messageBus: messageBus
                        );
                    case RegistrationKind.TargetedWithoutTargetingPostProcessorAction:
                        _contextAction = (Action<InstanceId, T>)_userHandler;
                        return messageHandler.RegisterTargetedWithoutTargetingPostProcessor(
                            _contextAction,
                            AugmentedContextAction,
                            _priority,
                            messageBus
                        );
                    case RegistrationKind.TargetedWithoutTargetingPostProcessorFast:
                        _contextFast = (MessageHandler.FastHandlerWithContext<T>)_userHandler;
                        return messageHandler.RegisterTargetedWithoutTargetingPostProcessor(
                            _contextFast,
                            AugmentedContextFast,
                            _priority,
                            messageBus
                        );
                    case RegistrationKind.TargetedInterceptor:
                        return messageHandler.RegisterTargetedInterceptor(
                            (IMessageBus.TargetedInterceptor<T>)_userHandler,
                            priority: _priority,
                            messageBus: messageBus
                        );
                    default:
                        throw new InvalidOperationException(
                            $"Unexpected registration kind {_kind} for TargetedRegistration<{typeof(T)}>."
                        );
                }
            }

            // Scalar invokers (targeted handler / post-processor). The user's handler is
            // the identity/dedup key; this flat invoker runs for the default slot. Calls
            // the typed field directly (no castclass) then records the (message, _context)
            // emission, matching the former AugmentedHandler bodies exactly.
            private void AugmentedScalarAction(ref T message)
            {
                _scalarAction(message);
                if (Token._diagnosticMode)
                {
                    Token._callCounts[Handle] = Token._callCounts.GetValueOrDefault(Handle) + 1;
                    Token._emissionBuffer.Add(new MessageEmissionData(message, _context));
                }
            }

            private void AugmentedScalarFast(ref T message)
            {
                _scalarFast(ref message);
                if (Token._diagnosticMode)
                {
                    Token._callCounts[Handle] = Token._callCounts.GetValueOrDefault(Handle) + 1;
                    Token._emissionBuffer.Add(new MessageEmissionData(message, _context));
                }
            }

            // Context invokers (without-targeting handler / post-processor). Emission data
            // uses the dispatch-supplied target (the ref param), not the stored _context
            // (default for these kinds), matching the former bodies exactly.
            private void AugmentedContextAction(ref InstanceId target, ref T message)
            {
                _contextAction(target, message);
                if (Token._diagnosticMode)
                {
                    Token._callCounts[Handle] = Token._callCounts.GetValueOrDefault(Handle) + 1;
                    Token._emissionBuffer.Add(new MessageEmissionData(message, target));
                }
            }

            private void AugmentedContextFast(ref InstanceId target, ref T message)
            {
                _contextFast(ref target, ref message);
                if (Token._diagnosticMode)
                {
                    Token._callCounts[Handle] = Token._callCounts.GetValueOrDefault(Handle) + 1;
                    Token._emissionBuffer.Add(new MessageEmissionData(message, target));
                }
            }
        }

        private sealed class UntargetedRegistration<T> : Registration
            where T : IUntargetedMessage
        {
            private readonly RegistrationKind _kind;
            private readonly object _userHandler;
            private readonly int _priority;

            // Typed views of _userHandler, resolved once at Register() time (cold) so the
            // per-dispatch invoker calls a typed field directly -- no castclass/switch on
            // the hot path. Exactly one is set per registration kind.
            private Action<T> _scalarAction;
            private MessageHandler.FastHandler<T> _scalarFast;

            internal UntargetedRegistration(
                MessageRegistrationToken token,
                RegistrationKind kind,
                object userHandler,
                int priority
            )
                : base(token)
            {
                _kind = kind;
                _userHandler = userHandler;
                _priority = priority;
            }

            public override MessageHandler.HandlerDeregistration Register()
            {
                MessageHandler messageHandler = Token._messageHandler;
                IMessageBus messageBus = Token._messageBus;
                switch (_kind)
                {
                    case RegistrationKind.UntargetedHandlerAction:
                        _scalarAction = (Action<T>)_userHandler;
                        return messageHandler.RegisterUntargetedMessageHandler(
                            _scalarAction,
                            AugmentedScalarAction,
                            priority: _priority,
                            messageBus: messageBus
                        );
                    case RegistrationKind.UntargetedHandlerFast:
                        _scalarFast = (MessageHandler.FastHandler<T>)_userHandler;
                        return messageHandler.RegisterUntargetedMessageHandler(
                            _scalarFast,
                            AugmentedScalarFast,
                            priority: _priority,
                            messageBus: messageBus
                        );
                    case RegistrationKind.UntargetedPostProcessorFast:
                        _scalarFast = (MessageHandler.FastHandler<T>)_userHandler;
                        return messageHandler.RegisterUntargetedPostProcessor(
                            _scalarFast,
                            AugmentedScalarFast,
                            _priority,
                            messageBus
                        );
                    case RegistrationKind.UntargetedInterceptor:
                        return messageHandler.RegisterUntargetedInterceptor(
                            (IMessageBus.UntargetedInterceptor<T>)_userHandler,
                            priority: _priority,
                            messageBus: messageBus
                        );
                    default:
                        throw new InvalidOperationException(
                            $"Unexpected registration kind {_kind} for UntargetedRegistration<{typeof(T)}>."
                        );
                }
            }

            // Untargeted scalar invokers. No context: emission data carries the message
            // only, matching the former AugmentedHandler bodies exactly.
            private void AugmentedScalarAction(ref T message)
            {
                _scalarAction(message);
                if (Token._diagnosticMode)
                {
                    Token._callCounts[Handle] = Token._callCounts.GetValueOrDefault(Handle) + 1;
                    Token._emissionBuffer.Add(new MessageEmissionData(message));
                }
            }

            private void AugmentedScalarFast(ref T message)
            {
                _scalarFast(ref message);
                if (Token._diagnosticMode)
                {
                    Token._callCounts[Handle] = Token._callCounts.GetValueOrDefault(Handle) + 1;
                    Token._emissionBuffer.Add(new MessageEmissionData(message));
                }
            }
        }

        private sealed class BroadcastRegistration<T> : Registration
            where T : IBroadcastMessage
        {
            private readonly RegistrationKind _kind;
            private readonly InstanceId _context;
            private readonly object _userHandler;
            private readonly int _priority;

            // Typed views of _userHandler, resolved once at Register() time (cold) so the
            // per-dispatch invoker calls a typed field directly -- no castclass/switch on
            // the hot path. Exactly one is set per registration kind.
            private Action<T> _scalarAction;
            private MessageHandler.FastHandler<T> _scalarFast;
            private Action<InstanceId, T> _contextAction;
            private MessageHandler.FastHandlerWithContext<T> _contextFast;

            internal BroadcastRegistration(
                MessageRegistrationToken token,
                RegistrationKind kind,
                InstanceId context,
                object userHandler,
                int priority
            )
                : base(token)
            {
                _kind = kind;
                _context = context;
                _userHandler = userHandler;
                _priority = priority;
            }

            public override MessageHandler.HandlerDeregistration Register()
            {
                MessageHandler messageHandler = Token._messageHandler;
                IMessageBus messageBus = Token._messageBus;
                switch (_kind)
                {
                    case RegistrationKind.BroadcastHandlerAction:
                        _scalarAction = (Action<T>)_userHandler;
                        return messageHandler.RegisterSourcedBroadcastMessageHandler(
                            _context,
                            _scalarAction,
                            AugmentedScalarAction,
                            priority: _priority,
                            messageBus: messageBus
                        );
                    case RegistrationKind.BroadcastHandlerFast:
                        _scalarFast = (MessageHandler.FastHandler<T>)_userHandler;
                        return messageHandler.RegisterSourcedBroadcastMessageHandler(
                            _context,
                            _scalarFast,
                            AugmentedScalarFast,
                            priority: _priority,
                            messageBus: messageBus
                        );
                    case RegistrationKind.BroadcastPostProcessorAction:
                        _scalarAction = (Action<T>)_userHandler;
                        return messageHandler.RegisterSourcedBroadcastPostProcessor(
                            _context,
                            _scalarAction,
                            AugmentedScalarAction,
                            _priority,
                            messageBus
                        );
                    case RegistrationKind.BroadcastPostProcessorFast:
                        _scalarFast = (MessageHandler.FastHandler<T>)_userHandler;
                        return messageHandler.RegisterSourcedBroadcastPostProcessor(
                            _context,
                            _scalarFast,
                            AugmentedScalarFast,
                            _priority,
                            messageBus
                        );
                    case RegistrationKind.BroadcastWithoutSourceAction:
                        _contextAction = (Action<InstanceId, T>)_userHandler;
                        return messageHandler.RegisterSourcedBroadcastWithoutSource(
                            _contextAction,
                            AugmentedContextAction,
                            priority: _priority,
                            messageBus: messageBus
                        );
                    case RegistrationKind.BroadcastWithoutSourceFast:
                        _contextFast = (MessageHandler.FastHandlerWithContext<T>)_userHandler;
                        return messageHandler.RegisterSourcedBroadcastWithoutSource(
                            _contextFast,
                            AugmentedContextFast,
                            priority: _priority,
                            messageBus: messageBus
                        );
                    case RegistrationKind.BroadcastWithoutSourcePostProcessorAction:
                        _contextAction = (Action<InstanceId, T>)_userHandler;
                        return messageHandler.RegisterSourcedBroadcastWithoutSourcePostProcessor(
                            _contextAction,
                            AugmentedContextAction,
                            priority: _priority,
                            messageBus
                        );
                    case RegistrationKind.BroadcastWithoutSourcePostProcessorFast:
                        _contextFast = (MessageHandler.FastHandlerWithContext<T>)_userHandler;
                        return messageHandler.RegisterSourcedBroadcastWithoutSourcePostProcessor(
                            _contextFast,
                            AugmentedContextFast,
                            priority: _priority,
                            messageBus
                        );
                    case RegistrationKind.BroadcastInterceptor:
                        return messageHandler.RegisterBroadcastInterceptor(
                            (IMessageBus.BroadcastInterceptor<T>)_userHandler,
                            priority: _priority,
                            messageBus: messageBus
                        );
                    default:
                        throw new InvalidOperationException(
                            $"Unexpected registration kind {_kind} for BroadcastRegistration<{typeof(T)}>."
                        );
                }
            }

            // Broadcast scalar invokers. Emission data carries the stored source
            // (_context), matching the former AugmentedHandler bodies exactly.
            private void AugmentedScalarAction(ref T message)
            {
                _scalarAction(message);
                if (Token._diagnosticMode)
                {
                    Token._callCounts[Handle] = Token._callCounts.GetValueOrDefault(Handle) + 1;
                    Token._emissionBuffer.Add(new MessageEmissionData(message, _context));
                }
            }

            private void AugmentedScalarFast(ref T message)
            {
                _scalarFast(ref message);
                if (Token._diagnosticMode)
                {
                    Token._callCounts[Handle] = Token._callCounts.GetValueOrDefault(Handle) + 1;
                    Token._emissionBuffer.Add(new MessageEmissionData(message, _context));
                }
            }

            // Broadcast context invokers for the without-source kinds. Emission data uses
            // the dispatch-supplied source (the ref param), not the stored _context
            // (default for these kinds), matching the former bodies exactly.
            private void AugmentedContextAction(ref InstanceId source, ref T message)
            {
                _contextAction(source, message);
                if (Token._diagnosticMode)
                {
                    Token._callCounts[Handle] = Token._callCounts.GetValueOrDefault(Handle) + 1;
                    Token._emissionBuffer.Add(new MessageEmissionData(message, source));
                }
            }

            private void AugmentedContextFast(ref InstanceId source, ref T message)
            {
                _contextFast(ref source, ref message);
                if (Token._diagnosticMode)
                {
                    Token._callCounts[Handle] = Token._callCounts.GetValueOrDefault(Handle) + 1;
                    Token._emissionBuffer.Add(new MessageEmissionData(message, source));
                }
            }
        }

        // Global accept-all is non-generic: its three sub-handlers are the fixed
        // IMessage facades. Stores the three user delegates (as object, since the two
        // public overloads differ in delegate shape -- Action vs FastHandler/
        // FastHandlerWithContext) and exposes six augmented sub-invokers (three per
        // overload shape). The kind-switch picks the matching MessageHandler.
        // RegisterGlobalAcceptAll overload and binds the three augmented invokers.
        private sealed class GlobalAcceptAllRegistration : Registration
        {
            private readonly RegistrationKind _kind;

            // Typed sub-handlers (one shape-trio is set per kind). The per-dispatch
            // invokers call these directly -- no castclass on the hot global-dispatch
            // path (the heaviest fan-out, so the castclass cost there was the worst).
            private readonly Action<IUntargetedMessage> _untargetedAction;
            private readonly Action<InstanceId, ITargetedMessage> _targetedAction;
            private readonly Action<InstanceId, IBroadcastMessage> _broadcastAction;
            private readonly MessageHandler.FastHandler<IUntargetedMessage> _untargetedFast;
            private readonly MessageHandler.FastHandlerWithContext<ITargetedMessage> _targetedFast;
            private readonly MessageHandler.FastHandlerWithContext<IBroadcastMessage> _broadcastFast;

            internal GlobalAcceptAllRegistration(
                MessageRegistrationToken token,
                Action<IUntargetedMessage> untargeted,
                Action<InstanceId, ITargetedMessage> targeted,
                Action<InstanceId, IBroadcastMessage> broadcast
            )
                : base(token)
            {
                _kind = RegistrationKind.GlobalAcceptAllAction;
                _untargetedAction = untargeted;
                _targetedAction = targeted;
                _broadcastAction = broadcast;
            }

            internal GlobalAcceptAllRegistration(
                MessageRegistrationToken token,
                MessageHandler.FastHandler<IUntargetedMessage> untargeted,
                MessageHandler.FastHandlerWithContext<ITargetedMessage> targeted,
                MessageHandler.FastHandlerWithContext<IBroadcastMessage> broadcast
            )
                : base(token)
            {
                _kind = RegistrationKind.GlobalAcceptAllFast;
                _untargetedFast = untargeted;
                _targetedFast = targeted;
                _broadcastFast = broadcast;
            }

            public override MessageHandler.HandlerDeregistration Register()
            {
                MessageHandler messageHandler = Token._messageHandler;
                IMessageBus messageBus = Token._messageBus;
                if (_kind == RegistrationKind.GlobalAcceptAllAction)
                {
                    return messageHandler.RegisterGlobalAcceptAll(
                        _untargetedAction,
                        AugmentedUntargetedAction,
                        _targetedAction,
                        AugmentedTargetedAction,
                        _broadcastAction,
                        AugmentedBroadcastAction,
                        messageBus
                    );
                }

                return messageHandler.RegisterGlobalAcceptAll(
                    _untargetedFast,
                    AugmentedUntargetedFast,
                    _targetedFast,
                    AugmentedTargetedFast,
                    _broadcastFast,
                    AugmentedBroadcastFast,
                    messageBus
                );
            }

            private void AugmentedUntargetedAction(IUntargetedMessage message)
            {
                _untargetedAction(message);
                if (Token._diagnosticMode)
                {
                    Token._callCounts[Handle] = Token._callCounts.GetValueOrDefault(Handle) + 1;
                    Token._emissionBuffer.Add(new MessageEmissionData(message));
                }
            }

            private void AugmentedTargetedAction(InstanceId target, ITargetedMessage message)
            {
                _targetedAction(target, message);
                if (Token._diagnosticMode)
                {
                    Token._callCounts[Handle] = Token._callCounts.GetValueOrDefault(Handle) + 1;
                    Token._emissionBuffer.Add(new MessageEmissionData(message, target));
                }
            }

            private void AugmentedBroadcastAction(InstanceId source, IBroadcastMessage message)
            {
                _broadcastAction(source, message);
                if (Token._diagnosticMode)
                {
                    Token._callCounts[Handle] = Token._callCounts.GetValueOrDefault(Handle) + 1;
                    Token._emissionBuffer.Add(new MessageEmissionData(message, source));
                }
            }

            private void AugmentedUntargetedFast(ref IUntargetedMessage message)
            {
                _untargetedFast(ref message);
                if (Token._diagnosticMode)
                {
                    Token._callCounts[Handle] = Token._callCounts.GetValueOrDefault(Handle) + 1;
                    Token._emissionBuffer.Add(new MessageEmissionData(message));
                }
            }

            private void AugmentedTargetedFast(ref InstanceId target, ref ITargetedMessage message)
            {
                _targetedFast(ref target, ref message);
                if (Token._diagnosticMode)
                {
                    Token._callCounts[Handle] = Token._callCounts.GetValueOrDefault(Handle) + 1;
                    Token._emissionBuffer.Add(new MessageEmissionData(message, target));
                }
            }

            private void AugmentedBroadcastFast(
                ref InstanceId source,
                ref IBroadcastMessage message
            )
            {
                _broadcastFast(ref source, ref message);
                if (Token._diagnosticMode)
                {
                    Token._callCounts[Handle] = Token._callCounts.GetValueOrDefault(Handle) + 1;
                    Token._emissionBuffer.Add(new MessageEmissionData(message, source));
                }
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
