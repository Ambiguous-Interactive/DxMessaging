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
    /// A bus rejection during an enabled token's Register call removes that call's staged entry.
    /// A failed Enable retains previously staged handles so their registrations can be retried.
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
    ///     private void OnInventoryChanged(in InventoryChanged msg) { /* update UI */ }
    ///     private void OnEquipItem(in EquipItem msg) { /* play animation */ }
    /// }
    /// </code>
    /// </example>
    public sealed class MessageRegistrationToken : IDisposable
    {
        /// <summary>
        /// Whether new registrations activate immediately.
        /// </summary>
        /// <remarks>
        /// Pending cleanup after a failure, or a registration accepted during teardown, can keep
        /// this true. Complete cleanup before treating the token as inactive.
        /// Rollback publishes this state before diagnostic formatting or log receivers run.
        /// </remarks>
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

        /*
            Maps each staged registration handle to the unified per-handle Registration
            object the public Register* methods built. Calling Registration.Register()
            (re)registers the handler on the bus and returns the matching
            HandlerDeregistration. The Registration object stores the staged kind,
            target/source, user handler and priority without a staging Func or closure.
            A separate immutable binding owns the augmented invokers and their resolved
            bus. Replays on the same bus reuse that binding; a different bus gets a new
            binding so frozen callbacks retain their own diagnostic origin. Binding
            creation is a registration cost, never a steady-state emission allocation.
            The registration also owns its common teardown state; an allocation-backed
            wrapper is created only for overlapping retry/recovery state.
            The central replay loop pairs
            the Registration with its handle and performs the AddDeregistration. The
            re-entrancy snapshot semantics are unchanged: the replay queue captures the
            Registration reference (exactly as it previously captured the staging Func),
            so an accepted registration removed during its own bus callback retains
            its teardown, while an entry removed before its replay turn stays inactive.
            Token-owned arena. A handle identifies a slot and the globally unique id that
            occupied it. Both are validated, so stale handles remain harmless after slot reuse.
            The doubly-linked live list preserves registration order while the singly-linked
            free list makes insertion and removal O(1).
        */
        private RegistrationSlot[] _slots = Array.Empty<RegistrationSlot>();
        private int _registrationHead = -1;
        private int _registrationTail = -1;
        private int _freeSlotHead = -1;
        private int _registrationCount;
        private int _deregistrationCount;

        /*
            Lifecycle snapshots are exceptional-path storage and materialize only when a replay,
            teardown, or retarget actually needs them.
        */
        private List<StagedRegistration> _registrationReplayQueue;
        private List<TeardownIdentity> _teardownQueue;
        private Dictionary<MessageRegistrationHandle, int> _rollbackCounts;
        private List<MessageRegistrationHandle> _activeRetargetHandles;
        private Dictionary<MessageRegistrationHandle, object> _orphanDeregistrations;

        internal RegistrationMetadataView _metadata => new RegistrationMetadataView(this);

        /*
            Diagnostics-only collections, allocated lazily on first use. A token whose
            owner never enables diagnostics (the default -- GlobalDiagnosticsTargets is
            Off -- and the common player case) never materializes them, saving the
            dictionary, the cyclic buffer, and the buffer's two backing lists per token.
            These are exposed as properties under their original field names so the
            inspector overlay and the diagnostics tests (which read token._callCounts /
            token._emissionBuffer) compile and behave unchanged; the getter caches into
            the backing field, so repeated reads return the same instance and an editor
            read simply materializes an empty collection. The only production writers are
            the dispatch-time AugmentedHandler bodies, all guarded by _diagnosticMode, so
            production-with-diagnostics-off never triggers the allocation. Teardown
            (ClearDiagnosticState / RemoveRegistrationState /
            PruneRegistrationStateToPendingDeregistrations) clears through the backing
            fields to avoid materializing a collection just to empty it.
        */
        private Dictionary<MessageRegistrationHandle, int> _callCountsBacking;
        private CyclicBuffer<MessageEmissionData> _emissionBufferBacking;

        internal Dictionary<MessageRegistrationHandle, int> _callCounts =>
            _callCountsBacking ??= new Dictionary<MessageRegistrationHandle, int>();

        internal CyclicBuffer<MessageEmissionData> _emissionBuffer =>
            _emissionBufferBacking ??= new CyclicBuffer<MessageEmissionData>(
                IMessageBus.GlobalMessageBufferSize
            );

        internal void RecordDiagnosticEmission(
            MessageRegistrationHandle handle,
            IMessageBus messageBus,
            IMessage message,
            InstanceId? context = null
        )
        {
            _callCounts[handle] = _callCounts.GetValueOrDefault(handle) + 1;
            long traceId =
                messageBus is DxMessaging.Core.MessageBus.MessageBus concreteBus
                && concreteBus.IsDispatching
                    ? concreteBus.EmissionId
                    : 0;
            _emissionBuffer.Add(new MessageEmissionData(message, context, traceId, handle));
        }

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
        /// <param name="targetedHandler">High-performance handler receiving <typeparamref name="T"/> by readonly reference.</param>
        /// <param name="priority">Execution order. Lower runs earlier; same priority uses registration order.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        /// <example>
        /// <code>
        /// _ = token.RegisterGameObjectTargeted&lt;ApplyDamage&gt;(gameObject, (in ApplyDamage m) =&gt; Apply(m));
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
        /// _ = token.RegisterComponentTargeted&lt;ApplyDamage&gt;(this, OnDamage);
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
        /// _ = token.RegisterGameObjectTargetedPostProcessor&lt;ApplyDamage&gt;(gameObject, (in ApplyDamage m) =&gt; metrics.RecordProcessedDamageRequest(m.amount));
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
        /// void OnSettingsChanged(in VideoSettingsChanged m) { /* refresh UI */ }
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
                )
            );
        }

        /// <summary>
        /// Stages a registration to accept untargeted messages of type <typeparamref name="T"/> (readonly by-reference fast path).
        /// </summary>
        /// <note>
        /// DOES NOT ACTUALLY REGISTER THE HANDLER IF NOT ENABLED. To register, a call to Enable() is needed.
        /// </note>
        /// <typeparam name="T">Type of message that the handler accepts.</typeparam>
        /// <param name="untargetedHandler">High-performance handler that receives <typeparamref name="T"/> by readonly reference.</param>
        /// <param name="priority">Execution order. Lower runs earlier; same priority uses registration order.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        /// <example>
        /// <code>
        /// _ = token.RegisterUntargeted&lt;WorldRegenerated&gt;((in WorldRegenerated m) =&gt; { /* ... */ });
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
        /// _ = token.RegisterBroadcast&lt;TookDamage&gt;(enemy, (in TookDamage m) =&gt; OnEnemyDamaged(m));
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
        /// _ = token.RegisterBroadcastWithoutSource&lt;TookDamage&gt;((DxMessaging.Core.InstanceId src, TookDamage m) =&gt; ShowDamageNumber(src, m));
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
        /// _ = token.RegisterBroadcastWithoutSourcePostProcessor&lt;TookDamage&gt;((DxMessaging.Core.InstanceId src, TookDamage m) =&gt; replay.RecordProcessedDamageMessage(src, m.amount));
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
        ///     (in DxMessaging.Core.Messages.IUntargetedMessage m) =&gt; UnityEngine.Debug.Log(m.MessageType),
        ///     (in DxMessaging.Core.InstanceId t, in DxMessaging.Core.Messages.ITargetedMessage m) =&gt; UnityEngine.Debug.Log($"{m.MessageType} to {t}"),
        ///     (in DxMessaging.Core.InstanceId s, in DxMessaging.Core.Messages.IBroadcastMessage m) =&gt; UnityEngine.Debug.Log($"{m.MessageType} from {s}")
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
                new UntargetedInterceptorRegistration<T>(this, interceptor, priority)
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
                new BroadcastInterceptorRegistration<T>(this, interceptor, priority)
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
                new TargetedInterceptorRegistration<T>(this, interceptor, priority)
            );
        }

        /// <summary>
        /// Handles the actual [de]registration wrapping and (potential) lazy execution.
        /// </summary>
        /// <param name="registration">The unified per-handle registration object the public Register* method built. Calling <see cref="Registration.Register"/> (re)registers the handler on the bus and returns the matching de-registration.</param>
        /// <returns>A handle that allows for registration and de-registration.</returns>
        private MessageRegistrationHandle InternalRegister(Registration registration)
        {
            /*
                Enabled registrations are caller-visible immediately. Validate before
                allocating a token slot so a rejected call cannot leave inaccessible
                metadata behind. Disabled tokens deliberately retain lazy validation:
                staged registrations are validated when Enable() replays them.
            */
            if (_enabled)
            {
                registration.Validate();
            }

            int slotIndex = AllocateSlot();
            MessageRegistrationHandle handle =
                MessageRegistrationHandle.CreateMessageRegistrationHandle(slotIndex);
            registration.Handle = handle;
            ref RegistrationSlot slot = ref _slots[slotIndex];
            slot.Id = handle.Id;
            slot.Registration = registration;
            slot.Previous = _registrationTail;
            slot.Next = -1;
            slot.NextFree = -1;
            if (0 <= _registrationTail)
            {
                _slots[_registrationTail].Next = slotIndex;
            }
            else
            {
                _registrationHead = slotIndex;
            }
            _registrationTail = slotIndex;
            ++_registrationCount;
            /*
                Generally, registrations should take place before all calls to enable.
                Just in case, though, register immediately if already enabled. We do not
                register at staging time when disabled (the owner might not be awake), so
                the Registration object is retained in _registrations to lazily
                (re)register on Enable().
            */
            if (_enabled)
            {
                MessageHandler.HandlerDeregistration actualDeregistration;
                try
                {
                    actualDeregistration = registration.RegisterValidated();
                }
                catch
                {
                    /*
                        No handle reached the caller. A reentrant bus may already have removed or
                        reused this slot, so retire only the failed registration's original identity.
                    */
                    RemoveRegistrationState(slotIndex, handle.Id);
                    throw;
                }
                AddDeregistration(slotIndex, handle.Id, actualDeregistration);
            }

            return handle;
        }

        /// <summary>
        /// Activates staged registrations in their original registration order.
        /// </summary>
        /// <remarks>
        /// Handles staged while disabled remain valid if replay fails. If rollback leaves
        /// <see cref="Enabled"/> true, finish cleanup with <see cref="Disable"/> before retrying
        /// <see cref="Enable"/>. If cleanup completes, retry Enable after the bus recovers.
        /// </remarks>
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

            if (0 < _registrationCount)
            {
                /*
                    Replay staged registrations in original registration order
                    (via _registrationOrder) rather than in
                    _registrations.Values enumeration order, which permutes
                    after Remove/Add churn. This preserves the documented
                    equal-priority "registration order" dispatch contract across
                    Disable()/Enable() cycles. Snapshot into _registrationReplayQueue first
                    so replay tolerates re-entrant registration mutation.
                */
                QueueRegistrationsInOrder();
                InvokeRegistrationQueueWithRollback(out _);
            }

            _enabled = true;
        }

        /// <summary>
        /// Runs active cleanup while retaining staged handlers for a later <see cref="Enable"/>.
        /// </summary>
        /// <remarks>
        /// Attempts the cleanup queued at the start, then rethrows the first failure. Failed
        /// cleanup keeps its original handle retryable. Registrations accepted during teardown
        /// retain cleanup for a later pass. <see cref="Enabled"/> stays true while cleanup remains.
        /// Retry after the bus recovers or the callback stops adding registrations.
        /// </remarks>
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
            if (!_enabled && _deregistrationCount == 0)
            {
                return;
            }

            Exception deregistrationException = InvokeDeregistrationQueue();
            _enabled = 0 < _deregistrationCount;
            if (deregistrationException != null)
            {
                ExceptionDispatchInfo.Capture(deregistrationException).Throw();
            }
        }

        /// <summary>
        /// Removes registrations and token-local diagnostic state while preserving any cleanup accepted during the call.
        /// </summary>
        /// <remarks>
        /// Failed cleanup keeps its original handle retryable and rethrows the first failure
        /// after other queued cleanup is attempted. Registrations accepted during teardown
        /// retain cleanup for a later pass. Repeat after the bus recovers or the callback stops
        /// adding registrations. <see cref="Enabled"/> stays true while cleanup remains.
        /// </remarks>
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
            if (deregistrationException == null && _deregistrationCount == 0)
            {
                _enabled = false;
                ClearRegistrationArena();
                ClearDiagnosticState();
                return;
            }

            _enabled = 0 < _deregistrationCount;
            PruneRegistrationStateToPendingDeregistrations();
            if (deregistrationException != null)
            {
                ExceptionDispatchInfo.Capture(deregistrationException).Throw();
            }
        }

        /// <summary>
        /// Retargets staged registrations to use a new message bus, re-registering active handlers if needed.
        /// </summary>
        /// <remarks>
        /// Failed active replay restores registrations on the previous bus. If diagnostic formatting
        /// or a log receiver successfully finishes pending rollback cleanup through <see cref="Disable"/>
        /// or <see cref="UnregisterAll"/>, recovery preserves that cleanup instead of replaying staging.
        /// </remarks>
        /// <param name="messageBus">Bus override to apply. Pass <c>null</c> to resume using the handler default.</param>
        /// <param name="rebindMode">Determines whether existing registrations should move to the supplied bus immediately.</param>
        public void RetargetMessageBus(IMessageBus messageBus, MessageBusRebindMode rebindMode)
        {
            MessageBusRebindMode effectiveMode =
                /*
                    Zero is the legacy Unknown value. Compare the default without naming the
                    obsolete compatibility member so strict consumers do not inherit CS0618.
                */
                rebindMode == default
                    ? MessageBusRebindMode.RebindActive
                    : rebindMode;

            bool sameBus = ReferenceEquals(_messageBus, messageBus);
            bool rebindActiveRegistrations =
                effectiveMode == MessageBusRebindMode.RebindActive
                && _enabled
                && 0 < _deregistrationCount;
            if (sameBus && !rebindActiveRegistrations)
            {
                return;
            }

            IMessageBus previousMessageBus = _messageBus;
            List<MessageRegistrationHandle> activeRetargetHandles = null;
            if (rebindActiveRegistrations)
            {
                activeRetargetHandles = _activeRetargetHandles ??=
                    new List<MessageRegistrationHandle>();
                activeRetargetHandles.Clear();
                QueueActiveHandles(activeRetargetHandles);
            }
            if (rebindActiveRegistrations)
            {
                Exception deregistrationException = InvokeDeregistrationQueue();
                if (deregistrationException != null)
                {
                    RestoreMissingRegistrationsAfterRetargetDeregistrationFailure(
                        previousMessageBus,
                        activeRetargetHandles
                    );
                    _enabled = 0 < _deregistrationCount;
                    ExceptionDispatchInfo.Capture(deregistrationException).Throw();
                }

                _enabled = false;
            }

            _messageBus = messageBus;

            if (rebindActiveRegistrations && 0 < _registrationCount)
            {
                /*
                    Mirror Enable(): rebind in original registration order so the
                    equal-priority dispatch order survives a bus retarget.
                */
                QueueRegistrationsInOrder();
                bool cleanupCompletedDuringLogging = false;
                try
                {
                    InvokeRegistrationQueueWithRollback(out cleanupCompletedDuringLogging);
                    _enabled = true;
                }
                catch (Exception exception)
                {
                    RestoreRegistrationsAfterRetargetFailure(
                        previousMessageBus,
                        cleanupCompletedDuringLogging
                    );
                    ExceptionDispatchInfo.Capture(exception).Throw();
                    throw;
                }
            }
        }

        private void AddDeregistration(
            int slotIndex,
            long id,
            MessageHandler.HandlerDeregistration deregistration
        )
        {
            if (!TryGetSlot(slotIndex, id, out RegistrationSlot slot))
            {
                /*
                    The staged registration was removed or its slot was reused while its replay
                    snapshot was executing. Preserve the returned teardown under the original
                    identity: consuming it here would lose a throwing teardown instead of keeping it
                    retryable, while attaching it to the slot would target the new occupant.
                */
                AddOrphanDeregistration(
                    MessageRegistrationHandle.FromIdentity(id, slotIndex),
                    deregistration
                );
                return;
            }

            object existing = slot.Deregistration;
            if (existing == null)
            {
                _slots[slotIndex].Deregistration = deregistration;
                ++_deregistrationCount;
                return;
            }

            _slots[slotIndex].Deregistration = MergeDeregistrations(existing, deregistration);
        }

        private void AddOrphanDeregistration(
            MessageRegistrationHandle handle,
            object value,
            bool prepend = false
        )
        {
            Dictionary<MessageRegistrationHandle, object> orphans = _orphanDeregistrations ??=
                new Dictionary<MessageRegistrationHandle, object>();
            if (!orphans.TryGetValue(handle, out object existing))
            {
                orphans.Add(handle, value);
                ++_deregistrationCount;
                return;
            }

            orphans[handle] = prepend
                ? MergeDeregistrations(value, existing)
                : MergeDeregistrations(existing, value);
        }

        private static PendingDeregistration MergeDeregistrations(object first, object second)
        {
            PendingDeregistration combined = first as PendingDeregistration;
            if (combined == null)
            {
                combined = new PendingDeregistration();
                combined.Add((MessageHandler.HandlerDeregistration)first);
            }

            if (second is PendingDeregistration pending)
            {
                combined.AddRange(pending);
            }
            else
            {
                combined.Add((MessageHandler.HandlerDeregistration)second);
            }
            return combined;
        }

        /// <summary>Temporarily transfers one cleanup value to its invoking owner.</summary>
        /// <remarks>2026-10-18: Nested cleanup must not invoke or consume an in-flight value twice.</remarks>
        private Exception InvokeOwnedDeregistration(
            MessageRegistrationHandle handle,
            object value,
            int startIndex,
            bool isOrphan
        )
        {
            if (isOrphan)
            {
                _orphanDeregistrations.Remove(handle);
            }
            else
            {
                _slots[handle.Slot].Deregistration = null;
            }
            --_deregistrationCount;

            Exception exception = InvokeDeregistration(value, startIndex, out bool shouldRemove);
            if (!shouldRemove)
            {
                /*
                    A callback may clear or reuse the original slot, or accept another cleanup
                    under the same handle. Keep the older unconsumed value first and preserve
                    both obligations; a removed identity becomes an orphan instead of attaching
                    its cleanup to a replacement slot.
                */
                if (TryGetSlot(handle.Slot, handle.Id, out RegistrationSlot slot))
                {
                    if (slot.Deregistration == null)
                    {
                        _slots[handle.Slot].Deregistration = value;
                        ++_deregistrationCount;
                    }
                    else
                    {
                        _slots[handle.Slot].Deregistration = MergeDeregistrations(
                            value,
                            slot.Deregistration
                        );
                    }
                }
                else
                {
                    AddOrphanDeregistration(handle, value, prepend: true);
                }
            }
            return exception;
        }

        // Logical de-registration count for a _deregistrations value (inline object == 1).
        private static int DeregistrationCount(object value) =>
            value is PendingDeregistration pending ? pending.Count : 1;

        /*
            Invokes the de-registration tail [startIndex..) for a _deregistrations value. For the
            inline object (logical Count 1, index 0): on success it is consumed (shouldRemove = true);
            on throw it is KEPT (retryable, shouldRemove = false); a rollback pass (startIndex &gt; 0)
            leaves the baseline entry untouched. For a holder it delegates to InvokeFrom, mutating the
            holder in place. Mirrors the prior PendingDeregistration-only semantics exactly.
        */
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

            if (0 < startIndex)
            {
                /*
                    Rollback baseline pass: the inline head (logical index 0) is below the requested
                    tail, so leave it untouched.
                */
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
            Dictionary<MessageRegistrationHandle, int> snapshot = _rollbackCounts ??=
                new Dictionary<MessageRegistrationHandle, int>();
            snapshot.Clear();
            for (
                int slotIndex = _registrationHead;
                0 <= slotIndex;
                slotIndex = _slots[slotIndex].Next
            )
            {
                RegistrationSlot slot = _slots[slotIndex];
                if (slot.Deregistration != null)
                {
                    snapshot[MessageRegistrationHandle.FromIdentity(slot.Id, slotIndex)] =
                        DeregistrationCount(slot.Deregistration);
                }
            }

            if (_orphanDeregistrations != null)
            {
                foreach (
                    KeyValuePair<MessageRegistrationHandle, object> entry in _orphanDeregistrations
                )
                {
                    snapshot[entry.Key] = DeregistrationCount(entry.Value);
                }
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
                InvokeRegistrationQueueWithRollback(out _);
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

        private void RestoreRegistrationsAfterRetargetFailure(
            IMessageBus previousMessageBus,
            bool cleanupCompletedDuringLogging
        )
        {
            _messageBus = previousMessageBus;
            if (cleanupCompletedDuringLogging || _registrationCount == 0)
            {
                _enabled = 0 < _deregistrationCount;
                return;
            }

            QueueRegistrationsWithoutRetryableDeregistrationsInOrder();
            try
            {
                InvokeRegistrationQueueWithRollback(out _);
                _enabled = true;
            }
            catch (Exception restoreException)
            {
                _enabled = 0 < _deregistrationCount;
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
            List<StagedRegistration> queue = GetRegistrationReplayQueue();
            queue.Clear();
            for (
                int slotIndex = _registrationHead;
                0 <= slotIndex;
                slotIndex = _slots[slotIndex].Next
            )
            {
                RegistrationSlot slot = _slots[slotIndex];
                queue.Add(new StagedRegistration(slotIndex, slot.Id, slot.Registration));
            }
        }

        private void QueueMissingRegistrationsInOrder(
            List<MessageRegistrationHandle> activeRetargetHandles
        )
        {
            List<StagedRegistration> queue = GetRegistrationReplayQueue();
            queue.Clear();
            for (
                int slotIndex = _registrationHead;
                0 <= slotIndex;
                slotIndex = _slots[slotIndex].Next
            )
            {
                RegistrationSlot slot = _slots[slotIndex];
                MessageRegistrationHandle handle = slot.Registration.Handle;
                if (!activeRetargetHandles.Contains(handle) || slot.Deregistration != null)
                {
                    continue;
                }

                queue.Add(new StagedRegistration(slotIndex, slot.Id, slot.Registration));
            }
        }

        private void QueueRegistrationsWithoutRetryableDeregistrationsInOrder()
        {
            List<StagedRegistration> queue = GetRegistrationReplayQueue();
            queue.Clear();
            for (
                int slotIndex = _registrationHead;
                0 <= slotIndex;
                slotIndex = _slots[slotIndex].Next
            )
            {
                RegistrationSlot slot = _slots[slotIndex];
                if (slot.Deregistration != null)
                {
                    continue;
                }

                queue.Add(new StagedRegistration(slotIndex, slot.Id, slot.Registration));
            }
        }

        /// <summary>Replays registrations still staged when their turn begins.</summary>
        /// <remarks>2026-10-17: Skip a later entry removed or replaced by an earlier registration callback.</remarks>
        private void InvokeRegistrationReplayQueue()
        {
            try
            {
                foreach (StagedRegistration staged in GetRegistrationReplayQueue())
                {
                    Registration registration = staged.Registration;
                    if (registration == null || !TryGetSlot(staged.Slot, staged.Id, out _))
                    {
                        continue;
                    }

                    MessageHandler.HandlerDeregistration actualDeregistration =
                        registration.Register();
                    AddDeregistration(staged.Slot, staged.Id, actualDeregistration);
                }
            }
            finally
            {
                _registrationReplayQueue?.Clear();
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
            public readonly int Slot;
            public readonly long Id;
            public readonly Registration Registration;

            public StagedRegistration(int slot, long id, Registration registration)
            {
                Slot = slot;
                Id = id;
                Registration = registration;
            }
        }

        private Exception InvokeDeregistrationQueue(
            Dictionary<MessageRegistrationHandle, int> baselineCounts = null
        )
        {
            if (_deregistrationCount == 0)
            {
                return null;
            }

            bool scopedToAddedDeregistrations = baselineCounts != null;
            List<TeardownIdentity> teardownQueue = _teardownQueue ??= new List<TeardownIdentity>();
            teardownQueue.Clear();
            for (
                int slotIndex = _registrationHead;
                0 <= slotIndex;
                slotIndex = _slots[slotIndex].Next
            )
            {
                RegistrationSlot slot = _slots[slotIndex];
                if (slot.Deregistration != null)
                {
                    teardownQueue.Add(new TeardownIdentity(slotIndex, slot.Id, isOrphan: false));
                }
            }

            bool hasOrphanSnapshot =
                _orphanDeregistrations != null && 0 < _orphanDeregistrations.Count;
            if (hasOrphanSnapshot)
            {
                foreach (MessageRegistrationHandle handle in _orphanDeregistrations.Keys)
                {
                    teardownQueue.Add(new TeardownIdentity(handle.Slot, handle.Id, isOrphan: true));
                }
            }

            /*
                Global handle ids are monotonic and therefore encode original registration order.
                Capture and sort BEFORE invoking anything: reentrant teardown additions are deferred
                to the next pass rather than joining this snapshot.
            */
            if (hasOrphanSnapshot)
            {
                teardownQueue.Sort(TeardownIdentityComparer.Instance);
            }
            /*
                The current invocation owns this snapshot until its finally block. Nested
                cleanup borrows another list, and an arena clear cannot invalidate this one.
            */
            _teardownQueue = null;
            Exception firstException = null;
            try
            {
                foreach (TeardownIdentity identity in teardownQueue)
                {
                    MessageRegistrationHandle handle = MessageRegistrationHandle.FromIdentity(
                        identity.Id,
                        identity.Slot
                    );
                    object value;
                    if (identity.IsOrphan)
                    {
                        if (
                            _orphanDeregistrations == null
                            || !_orphanDeregistrations.TryGetValue(handle, out value)
                        )
                        {
                            continue;
                        }
                    }
                    else
                    {
                        if (!TryGetSlot(identity.Slot, identity.Id, out RegistrationSlot slot))
                        {
                            continue;
                        }

                        value = slot.Deregistration;
                        if (value == null)
                        {
                            continue;
                        }
                    }

                    int startIndex = 0;
                    if (scopedToAddedDeregistrations)
                    {
                        if (baselineCounts.TryGetValue(handle, out int baselineCount))
                        {
                            startIndex = baselineCount;
                        }

                        if (DeregistrationCount(value) <= startIndex)
                        {
                            continue;
                        }
                    }

                    Exception exception = InvokeOwnedDeregistration(
                        handle,
                        value,
                        startIndex,
                        identity.IsOrphan
                    );
                    if (exception != null)
                    {
                        firstException ??= exception;
                    }
                }
            }
            finally
            {
                teardownQueue.Clear();
                _teardownQueue = teardownQueue;
            }

            return firstException;
        }

        private void InvokeRegistrationQueueWithRollback(out bool cleanupCompletedDuringLogging)
        {
            cleanupCompletedDuringLogging = false;
            Dictionary<MessageRegistrationHandle, int> rollbackBaseline =
                SnapshotDeregistrationCounts();
            try
            {
                InvokeRegistrationReplayQueue();
            }
            catch (Exception exception)
            {
                RollBackDeregistrationsAfterRegistrationFailure(
                    rollbackBaseline,
                    out cleanupCompletedDuringLogging
                );
                _enabled = 0 < _deregistrationCount;
                ExceptionDispatchInfo.Capture(exception).Throw();
                throw;
            }
        }

        private void RollBackDeregistrationsAfterRegistrationFailure(
            Dictionary<MessageRegistrationHandle, int> rollbackBaseline,
            out bool cleanupCompletedDuringLogging
        )
        {
            cleanupCompletedDuringLogging = false;
            if (_deregistrationCount == 0)
            {
                return;
            }

            Exception rollbackException = InvokeDeregistrationQueue(rollbackBaseline);
            /*
                Diagnostic formatting and receivers can throw or reenter this token.
                Publish pending cleanup before entering either user-code boundary.
            */
            _enabled = 0 < _deregistrationCount;
            bool pendingBeforeLogging = _enabled;
            try
            {
                if (rollbackException != null && MessagingDebug.enabled)
                {
                    MessagingDebug.Log(
                        LogLevel.Error,
                        "Failed to roll back partial registration after token replay failure: {0}",
                        rollbackException
                    );
                }
            }
            finally
            {
                /*
                    A logger can finish pending cleanup and then throw. Preserve that completed
                    cleanup in outer retarget recovery without suppressing the logger exception.
                    Natural rollback completion occurred before logging and still restores staging.
                */
                cleanupCompletedDuringLogging = pendingBeforeLogging && _deregistrationCount == 0;
            }
        }

        private void ClearDiagnosticState()
        {
            /*
                Clear through the backing fields so an inactive (never-materialized)
                diagnostics collection is not allocated merely to be emptied.
            */
            _callCountsBacking?.Clear();
            _emissionBufferBacking?.Clear();
        }

        private void PruneRegistrationStateToPendingDeregistrations()
        {
            int slotIndex = _registrationTail;
            while (0 <= slotIndex)
            {
                int previous = _slots[slotIndex].Previous;
                if (_slots[slotIndex].Deregistration == null)
                {
                    RemoveRegistrationState(slotIndex, _slots[slotIndex].Id);
                }

                slotIndex = previous;
            }

            _emissionBufferBacking?.Clear();
            if (_registrationCount == 0)
            {
                ClearDiagnosticState();
            }
        }

        private void RemoveRegistrationState(MessageRegistrationHandle handle)
        {
            _ = RemoveRegistrationState(handle.Slot, handle.Id);
        }

        private bool RemoveRegistrationState(int slotIndex, long id)
        {
            if (!TryGetSlot(slotIndex, id, out RegistrationSlot slot))
            {
                return false;
            }

            if (0 <= slot.Previous)
            {
                _slots[slot.Previous].Next = slot.Next;
            }
            else
            {
                _registrationHead = slot.Next;
            }

            if (0 <= slot.Next)
            {
                _slots[slot.Next].Previous = slot.Previous;
            }
            else
            {
                _registrationTail = slot.Previous;
            }

            if (slot.Deregistration != null)
            {
                /*
                    A teardown callback may accept another cleanup under this handle. Removing
                    staging must keep that accepted obligation reachable through orphan cleanup.
                */
                --_deregistrationCount;
                AddOrphanDeregistration(
                    MessageRegistrationHandle.FromIdentity(id, slotIndex),
                    slot.Deregistration
                );
            }

            MessageRegistrationHandle handle = MessageRegistrationHandle.FromIdentity(
                id,
                slotIndex
            );
            _callCountsBacking?.Remove(handle);
            _slots[slotIndex] = default;
            _slots[slotIndex].Previous = -1;
            _slots[slotIndex].Next = -1;
            _slots[slotIndex].NextFree = _freeSlotHead;
            _freeSlotHead = slotIndex;
            --_registrationCount;
            if (_registrationCount == 0)
            {
                ClearDiagnosticState();
            }

            return true;
        }

        /// <summary>
        /// Removes a single staged registration by handle.
        /// </summary>
        /// <param name="handle">Handle returned by a registration on this token.</param>
        /// <remarks>
        /// A cleanup failure is rethrown and retains the original handle for retry. Removing a
        /// handle with no staged registration or pending cleanup has no effect.
        /// </remarks>
        /// <example>
        /// <code>
        /// var h = token.RegisterUntargeted&lt;SceneLoaded&gt;(OnScene);
        /// token.RemoveRegistration(h); // de-register just this one
        /// </code>
        /// </example>
        public void RemoveRegistration(MessageRegistrationHandle handle)
        {
            if (!TryGetSlot(handle.Slot, handle.Id, out RegistrationSlot slot))
            {
                RemoveOrphanDeregistration(handle);
                return;
            }

            object value = slot.Deregistration;
            if (value != null)
            {
                Exception deregistrationException = InvokeOwnedDeregistration(
                    handle,
                    value,
                    0,
                    isOrphan: false
                );

                if (deregistrationException != null)
                {
                    _enabled = 0 < _deregistrationCount;
                    ExceptionDispatchInfo.Capture(deregistrationException).Throw();
                }
            }

            /*
                Drop the matching staged registration and metadata so a later
                Disable()/Enable() cycle does not silently re-register the
                handler we were just asked to remove.
            */
            RemoveRegistrationState(handle);
        }

        private void RemoveOrphanDeregistration(MessageRegistrationHandle handle)
        {
            if (
                _orphanDeregistrations == null
                || !_orphanDeregistrations.TryGetValue(handle, out object value)
            )
            {
                return;
            }

            Exception exception = InvokeOwnedDeregistration(handle, value, 0, isOrphan: true);

            if (exception != null)
            {
                _enabled = 0 < _deregistrationCount;
                ExceptionDispatchInfo.Capture(exception).Throw();
            }
        }

        private int AllocateSlot()
        {
            if (_freeSlotHead < 0)
            {
                int oldLength = _slots.Length;
                int newLength = oldLength == 0 ? 4 : oldLength << 1;
                Array.Resize(ref _slots, newLength);
                for (int i = newLength - 1; oldLength <= i; --i)
                {
                    _slots[i].Previous = -1;
                    _slots[i].Next = -1;
                    _slots[i].NextFree = _freeSlotHead;
                    _freeSlotHead = i;
                }
            }

            int slotIndex = _freeSlotHead;
            _freeSlotHead = _slots[slotIndex].NextFree;
            return slotIndex;
        }

        private bool TryGetSlot(int slotIndex, long id, out RegistrationSlot slot)
        {
            if ((uint)slotIndex < (uint)_slots.Length)
            {
                slot = _slots[slotIndex];
                if (slot.Registration != null && slot.Id == id)
                {
                    return true;
                }
            }

            slot = default;
            return false;
        }

        private void ClearRegistrationArena()
        {
            if (0 < _slots.Length)
            {
                Array.Clear(_slots, 0, _slots.Length);
                _freeSlotHead = -1;
                for (int i = _slots.Length - 1; 0 <= i; --i)
                {
                    _slots[i].Previous = -1;
                    _slots[i].Next = -1;
                    _slots[i].NextFree = _freeSlotHead;
                    _freeSlotHead = i;
                }
            }
            else
            {
                _freeSlotHead = -1;
            }

            _registrationHead = -1;
            _registrationTail = -1;
            _registrationCount = 0;
            _deregistrationCount = 0;
            /*
                Replay owns its snapshot until its finally block. Clearing that list here
                would invalidate an active enumerator when a bus callback clears the token.
            */
            _teardownQueue?.Clear();
            _rollbackCounts?.Clear();
            _activeRetargetHandles?.Clear();
            _orphanDeregistrations?.Clear();
        }

        private List<StagedRegistration> GetRegistrationReplayQueue()
        {
            return _registrationReplayQueue ??= new List<StagedRegistration>();
        }

        private void QueueActiveHandles(List<MessageRegistrationHandle> destination)
        {
            for (
                int slotIndex = _registrationHead;
                0 <= slotIndex;
                slotIndex = _slots[slotIndex].Next
            )
            {
                RegistrationSlot slot = _slots[slotIndex];
                if (slot.Deregistration != null)
                {
                    destination.Add(MessageRegistrationHandle.FromIdentity(slot.Id, slotIndex));
                }
            }

            if (_orphanDeregistrations != null)
            {
                destination.AddRange(_orphanDeregistrations.Keys);
            }
        }

        private struct RegistrationSlot
        {
            public Registration Registration;
            public object Deregistration;
            public long Id;
            public int Previous;
            public int Next;
            public int NextFree;
        }

        private readonly struct TeardownIdentity
        {
            public readonly int Slot;
            public readonly long Id;
            public readonly bool IsOrphan;

            public TeardownIdentity(int slot, long id, bool isOrphan)
            {
                Slot = slot;
                Id = id;
                IsOrphan = isOrphan;
            }
        }

        private sealed class TeardownIdentityComparer : IComparer<TeardownIdentity>
        {
            internal static readonly TeardownIdentityComparer Instance = new();

            public int Compare(TeardownIdentity left, TeardownIdentity right)
            {
                return left.Id.CompareTo(right.Id);
            }
        }

        internal readonly struct RegistrationMetadataView
            : IEnumerable<KeyValuePair<MessageRegistrationHandle, MessageRegistrationMetadata>>
        {
            private readonly MessageRegistrationToken _token;

            internal RegistrationMetadataView(MessageRegistrationToken token)
            {
                _token = token;
            }

            public int Count => _token._registrationCount;

            public bool ContainsKey(MessageRegistrationHandle handle)
            {
                return _token.TryGetSlot(handle.Slot, handle.Id, out _);
            }

            public Enumerator GetEnumerator()
            {
                return new Enumerator(_token);
            }

            IEnumerator<
                KeyValuePair<MessageRegistrationHandle, MessageRegistrationMetadata>
            > IEnumerable<
                KeyValuePair<MessageRegistrationHandle, MessageRegistrationMetadata>
            >.GetEnumerator()
            {
                return GetEnumerator();
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }

            internal struct Enumerator
                : IEnumerator<KeyValuePair<MessageRegistrationHandle, MessageRegistrationMetadata>>
            {
                private readonly MessageRegistrationToken _token;
                private int _next;
                private KeyValuePair<
                    MessageRegistrationHandle,
                    MessageRegistrationMetadata
                > _current;

                internal Enumerator(MessageRegistrationToken token)
                {
                    _token = token;
                    _next = token._registrationHead;
                    _current = default;
                }

                public KeyValuePair<
                    MessageRegistrationHandle,
                    MessageRegistrationMetadata
                > Current => _current;

                object System.Collections.IEnumerator.Current => _current;

                public bool MoveNext()
                {
                    if (_next < 0)
                    {
                        return false;
                    }

                    int slotIndex = _next;
                    RegistrationSlot slot = _token._slots[slotIndex];
                    _next = slot.Next;
                    _current = new KeyValuePair<
                        MessageRegistrationHandle,
                        MessageRegistrationMetadata
                    >(
                        MessageRegistrationHandle.FromIdentity(slot.Id, slotIndex),
                        slot.Registration.Metadata
                    );
                    return true;
                }

                public void Reset()
                {
                    _next = _token._registrationHead;
                    _current = default;
                }

                public void Dispose() { }
            }
        }

        /*
            Holds the live de-registration Actions for a single handle in the MULTI-de-registration
            case (2+). The common case is EXACTLY ONE de-registration per handle (the existing
            Registration object executes its embedded teardown state); that Registration is stored
            INLINE as the slot value, so the common path allocates no
            PendingDeregistration object at all (see AddDeregistration / InvokeDeregistration). This
            holder is allocated only when a rare second de-registration accumulates on the same
            handle -- a re-entrant retarget-recovery replay can stage one beyond the rollback
            baseline -- at which point the inline Registration is promoted into this holder's head
            and the second spills to a lazily-allocated overflow list. This stays a class (mutated in
            place through the _deregistrations dictionary), so reference semantics and every
            call site are unchanged; only the storage shape changed.

            Invariant: when Count > 0 the head lives in _hasHead/_head and is the LOGICAL
            FIRST entry; any further entries follow in _overflow in insertion order. Add
            appends to the logical tail; InvokeFrom invokes a contiguous logical tail
            [startIndex..Count), removing each success and KEEPING each failure (retryable),
            then promotes the first surviving overflow entry into the head slot if the head
            was consumed -- preserving the exact ordering, partial-failure, and
            rollback-baseline (startIndex) semantics the List<Action> form had.
        */
        private sealed class PendingDeregistration
        {
            private MessageHandler.HandlerDeregistration _head;
            private bool _hasHead;
            private List<MessageHandler.HandlerDeregistration> _overflow;

            internal int Count => (_hasHead ? 1 : 0) + (_overflow?.Count ?? 0);

            internal void Add(MessageHandler.HandlerDeregistration action)
            {
                /*
                    Fill the inline head ONLY when nothing is stored. The empty-overflow
                    clause matters during the transient window inside InvokeFrom where the
                    head has been consumed but overflow survivors remain (before they are
                    promoted): a re-entrant Add then appends to the logical TAIL (overflow),
                    exactly as the List form did, rather than jumping the new entry ahead of
                    the survivors into the head slot. The overflow loop re-reads its Count,
                    so such a tail-appended entry is still invoked in the same pass -- the
                    List form's behavior preserved. Appending during an invocation must
                    preserve the logical tail rather than replacing an already consumed head.
                */
                if (!_hasHead && (_overflow == null || _overflow.Count == 0))
                {
                    _head = action;
                    _hasHead = true;
                    return;
                }

                (_overflow ??= new List<MessageHandler.HandlerDeregistration>()).Add(action);
            }

            internal void AddRange(PendingDeregistration pending)
            {
                if (pending._hasHead)
                {
                    Add(pending._head);
                }
                if (pending._overflow != null)
                {
                    for (int index = 0; index < pending._overflow.Count; ++index)
                    {
                        Add(pending._overflow[index]);
                    }
                }
            }

            internal Exception InvokeFrom(int startIndex)
            {
                if (startIndex < 0)
                {
                    startIndex = 0;
                }

                Exception firstException = null;

                /*
                    Logical index 0 is the inline head; logical indices 1.. are _overflow.
                    Invoke the head only when it is within the requested tail (a rollback
                    pass with startIndex > 0 must leave the baseline head untouched).
                */
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
                        /*
                            Keep the failed head (retryable), exactly as the List form did
                            by advancing past a throwing entry instead of removing it.
                        */
                        firstException ??= exception;
                    }
                }

                if (_overflow is { Count: > 0 })
                {
                    /*
                        Overflow entry j has logical index 1 + j; invoke those at or past
                        startIndex. Remove successes, keep failures (retryable).
                    */
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

                /*
                    If the head was consumed but overflow survivors remain, promote the
                    first survivor into the head slot so the head is always the logical
                    first entry (keeping Count and future Adds consistent).
                */
                if (!_hasHead && _overflow is { Count: > 0 })
                {
                    _head = _overflow[0];
                    _hasHead = true;
                    _overflow.RemoveAt(0);
                }

                return firstException;
            }
        }

        /*
            Discriminates the per-handle Registration object's behaviour. Each value
            pins (a) which MessageHandler.Register* method the kind-switch in
            Registration.Register() calls and (b) which augmented-invoker body shape
            (user-delegate type + by-value vs by-ref call) the bound FastHandler/
            FastHandlerWithContext delegate runs. The values map 1:1 onto the former
            per-method staging lambdas; the *Action / *Fast suffix mirrors the two
            public overloads (Action<T>/Action<InstanceId,T> vs FastHandler<T>/
            FastHandlerWithContext<T>) that previously had distinct AugmentedHandler
            local functions.
        */
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

        /*
            The unified per-handle staging object holds the owning token, handle,
            user delegate, target/source, priority and kind without a staging Func.
            Its current immutable invocation binding owns the augmented delegates and
            resolved bus. Older frozen delegates keep their own binding through replay;
            MessageHandler still invokes a delegate without per-dispatch virtual calls.

            The base is non-generic so _registrations / the replay queue can hold every
            registration polymorphically. The constrained MessageHandler.Register*<T>
            calls each require T : ITargetedMessage / IUntargetedMessage /
            IBroadcastMessage, which a single Registration<T> (T : IMessage) cannot
            satisfy; the three concrete subclasses below carry the matching constraint
            and each run a kind-SWITCH over only the kinds in their family (per the
            user-accepted "unified object, accept the kind-switch" design -- NOT ~14
            subclasses). GlobalAcceptAllRegistration is non-generic (its sub-handlers
            are the fixed IMessage facades).
        */
        private abstract class Registration : MessageHandler.HandlerDeregistration
        {
            protected readonly MessageRegistrationToken Token;
            public MessageRegistrationHandle Handle;

            protected Registration(MessageRegistrationToken token)
            {
                Token = token;
            }

            /*
                Kind-switch: (re)register on the bus and return the matching
                HandlerDeregistration, exactly as the former staging lambda did. Reads
                the token's CURRENT _messageBus (so Enable()/RetargetMessageBus replay
                binds to the active bus, unchanged from when _messageBus was captured by
                the staging closure at call time -- the staging closure also read the
                field, not a snapshot).
            */
            public MessageHandler.HandlerDeregistration Register()
            {
                Validate();
                return RegisterValidated();
            }

            internal abstract MessageHandler.HandlerDeregistration RegisterValidated();

            /*
                Rejects invalid staged state without mutating the token or bus. Register()
                repeats this validation because disabled-token replay and retargeting do
                not pass through InternalRegister.
            */
            public abstract void Validate();

            public abstract MessageRegistrationMetadata Metadata { get; }

            protected static MessageRegistrationType GetRegistrationType(RegistrationKind kind)
            {
                switch (kind)
                {
                    case RegistrationKind.TargetedHandlerAction:
                    case RegistrationKind.TargetedHandlerFast:
                        return MessageRegistrationType.Targeted;
                    case RegistrationKind.TargetedPostProcessorAction:
                    case RegistrationKind.TargetedPostProcessorFast:
                        return MessageRegistrationType.TargetedPostProcessor;
                    case RegistrationKind.TargetedWithoutTargetingAction:
                    case RegistrationKind.TargetedWithoutTargetingFast:
                        return MessageRegistrationType.TargetedWithoutTargeting;
                    case RegistrationKind.TargetedWithoutTargetingPostProcessorAction:
                    case RegistrationKind.TargetedWithoutTargetingPostProcessorFast:
                        return MessageRegistrationType.TargetedWithoutTargetingPostProcessor;
                    case RegistrationKind.TargetedInterceptor:
                        return MessageRegistrationType.TargetedInterceptor;
                    case RegistrationKind.UntargetedHandlerAction:
                    case RegistrationKind.UntargetedHandlerFast:
                        return MessageRegistrationType.Untargeted;
                    case RegistrationKind.UntargetedPostProcessorFast:
                        return MessageRegistrationType.UntargetedPostProcessor;
                    case RegistrationKind.UntargetedInterceptor:
                        return MessageRegistrationType.UntargetedInterceptor;
                    case RegistrationKind.BroadcastHandlerAction:
                    case RegistrationKind.BroadcastHandlerFast:
                        return MessageRegistrationType.Broadcast;
                    case RegistrationKind.BroadcastPostProcessorAction:
                    case RegistrationKind.BroadcastPostProcessorFast:
                        return MessageRegistrationType.BroadcastPostProcessor;
                    case RegistrationKind.BroadcastWithoutSourceAction:
                    case RegistrationKind.BroadcastWithoutSourceFast:
                        return MessageRegistrationType.BroadcastWithoutSource;
                    case RegistrationKind.BroadcastWithoutSourcePostProcessorAction:
                    case RegistrationKind.BroadcastWithoutSourcePostProcessorFast:
                        return MessageRegistrationType.BroadcastWithoutSourcePostProcessor;
                    case RegistrationKind.BroadcastInterceptor:
                        return MessageRegistrationType.BroadcastInterceptor;
                    case RegistrationKind.GlobalAcceptAllAction:
                    case RegistrationKind.GlobalAcceptAllFast:
                        return MessageRegistrationType.GlobalAcceptAll;
                    default:
                        return MessageRegistrationType.None;
                }
            }

            protected IMessageBus ResolveRegistrationMessageBus()
            {
                return Token._messageBus;
            }

            protected static void ThrowIfNullHandler(object handler, string parameterName)
            {
                if (handler == null)
                {
                    throw new ArgumentNullException(parameterName);
                }
            }
        }

        /*
            A frozen dispatch delegate owns this binding, rather than the replayed staging
            object's current bus. Rebinding creates a new target; an older callback and
            its pending peers keep the old target until their snapshots are released.
            The token still decides whether to record after the user callback returns.
        */
        private abstract class InvocationBinding
        {
            protected readonly MessageRegistrationToken Token;
            private readonly MessageRegistrationHandle _handle;
            public readonly IMessageBus MessageBus;

            protected InvocationBinding(
                MessageRegistrationToken token,
                MessageRegistrationHandle handle,
                IMessageBus messageBus
            )
            {
                Token = token;
                _handle = handle;
                MessageBus = messageBus;
            }

            protected void RecordDiagnosticEmission(IMessage message, InstanceId? context = null)
            {
                Token.RecordDiagnosticEmission(_handle, MessageBus, message, context);
            }
        }

        private abstract class Registration<T> : Registration
            where T : IMessage
        {
            private MessageHandler.TypedHandler<T>.TypedHandlerDeregistrationState _typedDeregistration;
            private bool _hasInlineDeregistration;

            protected Registration(MessageRegistrationToken token)
                : base(token) { }

            protected MessageHandler.HandlerDeregistration StoreDeregistration(
                MessageHandler.TypedHandler<T>.TypedHandlerDeregistrationState deregistration
            )
            {
                if (_hasInlineDeregistration)
                {
                    return deregistration;
                }

                _typedDeregistration = deregistration;
                _hasInlineDeregistration = true;
                return this;
            }

            internal override void Deregister()
            {
                if (!_hasInlineDeregistration)
                {
                    return;
                }

                _typedDeregistration.Deregister();

                /*
                    Clear only after successful teardown. If a custom bus throws, this
                    registration remains the retryable inline action and any recovery
                    registration spills into an independent compatibility wrapper.
                */
                _hasInlineDeregistration = false;
                _typedDeregistration = default;
            }
        }

        private sealed class TargetedRegistration<T> : Registration<T>
            where T : ITargetedMessage
        {
            private readonly RegistrationKind _kind;
            private readonly InstanceId _context;
            private readonly object _userHandler;
            private readonly int _priority;

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

            public override MessageRegistrationMetadata Metadata =>
                new MessageRegistrationMetadata(
                    _kind == RegistrationKind.TargetedHandlerAction
                    || _kind == RegistrationKind.TargetedHandlerFast
                    || _kind == RegistrationKind.TargetedPostProcessorAction
                    || _kind == RegistrationKind.TargetedPostProcessorFast
                        ? _context
                        : (InstanceId?)null,
                    typeof(T),
                    GetRegistrationType(_kind),
                    _priority
                );

            internal override MessageHandler.HandlerDeregistration RegisterValidated()
            {
                MessageHandler messageHandler = Token._messageHandler;
                IMessageBus messageBus = ResolveRegistrationMessageBus();
                switch (_kind)
                {
                    case RegistrationKind.TargetedHandlerAction:
                        return StoreDeregistration(
                            messageHandler.RegisterTargetedMessageHandler(
                                _context,
                                (Action<T>)_userHandler,
                                GetBinding(messageBus).AugmentedScalarAction,
                                priority: _priority,
                                messageBus: messageBus
                            )
                        );
                    case RegistrationKind.TargetedHandlerFast:
                        return StoreDeregistration(
                            messageHandler.RegisterTargetedMessageHandler(
                                _context,
                                (MessageHandler.FastHandler<T>)_userHandler,
                                GetBinding(messageBus).AugmentedScalarFast,
                                priority: _priority,
                                messageBus: messageBus
                            )
                        );
                    case RegistrationKind.TargetedPostProcessorAction:
                        return StoreDeregistration(
                            messageHandler.RegisterTargetedPostProcessor(
                                _context,
                                (Action<T>)_userHandler,
                                GetBinding(messageBus).AugmentedScalarAction,
                                _priority,
                                messageBus
                            )
                        );
                    case RegistrationKind.TargetedPostProcessorFast:
                        return StoreDeregistration(
                            messageHandler.RegisterTargetedPostProcessor(
                                _context,
                                (MessageHandler.FastHandler<T>)_userHandler,
                                GetBinding(messageBus).AugmentedScalarFast,
                                _priority,
                                messageBus
                            )
                        );
                    case RegistrationKind.TargetedWithoutTargetingAction:
                        return StoreDeregistration(
                            messageHandler.RegisterTargetedWithoutTargeting(
                                (Action<InstanceId, T>)_userHandler,
                                GetBinding(messageBus).AugmentedContextAction,
                                priority: _priority,
                                messageBus: messageBus
                            )
                        );
                    case RegistrationKind.TargetedWithoutTargetingFast:
                        return StoreDeregistration(
                            messageHandler.RegisterTargetedWithoutTargeting(
                                (MessageHandler.FastHandlerWithContext<T>)_userHandler,
                                GetBinding(messageBus).AugmentedContextFast,
                                priority: _priority,
                                messageBus: messageBus
                            )
                        );
                    case RegistrationKind.TargetedWithoutTargetingPostProcessorAction:
                        return StoreDeregistration(
                            messageHandler.RegisterTargetedWithoutTargetingPostProcessor(
                                (Action<InstanceId, T>)_userHandler,
                                GetBinding(messageBus).AugmentedContextAction,
                                _priority,
                                messageBus
                            )
                        );
                    case RegistrationKind.TargetedWithoutTargetingPostProcessorFast:
                        return StoreDeregistration(
                            messageHandler.RegisterTargetedWithoutTargetingPostProcessor(
                                (MessageHandler.FastHandlerWithContext<T>)_userHandler,
                                GetBinding(messageBus).AugmentedContextFast,
                                _priority,
                                messageBus
                            )
                        );
                    default:
                        throw new InvalidOperationException(
                            $"Unexpected registration kind {_kind} for TargetedRegistration<{typeof(T)}>."
                        );
                }
            }

            public override void Validate()
            {
                string parameterName;
                switch (_kind)
                {
                    case RegistrationKind.TargetedHandlerAction:
                    case RegistrationKind.TargetedHandlerFast:
                        parameterName = "targetedHandler";
                        break;
                    case RegistrationKind.TargetedPostProcessorAction:
                    case RegistrationKind.TargetedPostProcessorFast:
                        parameterName = "targetedPostProcessor";
                        break;
                    case RegistrationKind.TargetedWithoutTargetingAction:
                    case RegistrationKind.TargetedWithoutTargetingFast:
                        parameterName = "messageHandler";
                        break;
                    default:
                        parameterName = "postProcessor";
                        break;
                }

                ThrowIfNullHandler(_userHandler, parameterName);
            }

            private Binding _binding;

            private Binding GetBinding(IMessageBus messageBus)
            {
                IMessageBus resolvedBus = messageBus ?? Token._messageHandler.DefaultMessageBus;
                if (_binding == null || !ReferenceEquals(_binding.MessageBus, resolvedBus))
                {
                    _binding = new Binding(this, resolvedBus);
                }
                return _binding;
            }

            private sealed class Binding : InvocationBinding
            {
                private readonly Action<T> _scalarAction;
                private readonly MessageHandler.FastHandler<T> _scalarFast;
                private readonly Action<InstanceId, T> _contextAction;
                private readonly MessageHandler.FastHandlerWithContext<T> _contextFast;
                private readonly InstanceId _context;

                public Binding(TargetedRegistration<T> registration, IMessageBus messageBus)
                    : base(registration.Token, registration.Handle, messageBus)
                {
                    _scalarAction = registration._userHandler as Action<T>;
                    _scalarFast = registration._userHandler as MessageHandler.FastHandler<T>;
                    _contextAction = registration._userHandler as Action<InstanceId, T>;
                    _contextFast =
                        registration._userHandler as MessageHandler.FastHandlerWithContext<T>;
                    _context = registration._context;
                }

                public void AugmentedScalarAction(in T message)
                {
                    _scalarAction(message);
                    if (Token._diagnosticMode)
                    {
                        RecordDiagnosticEmission(message, _context);
                    }
                }

                public void AugmentedScalarFast(in T message)
                {
                    _scalarFast(in message);
                    if (Token._diagnosticMode)
                    {
                        RecordDiagnosticEmission(message, _context);
                    }
                }

                /*
                    Context invokers (without-targeting handler / post-processor). Emission data
                    uses the dispatch-supplied target (the readonly parameter), not the stored _context
                    (default for these kinds), matching the former bodies exactly.
                */
                public void AugmentedContextAction(in InstanceId target, in T message)
                {
                    _contextAction(target, message);
                    if (Token._diagnosticMode)
                    {
                        RecordDiagnosticEmission(message, target);
                    }
                }

                public void AugmentedContextFast(in InstanceId target, in T message)
                {
                    _contextFast(in target, in message);
                    if (Token._diagnosticMode)
                    {
                        RecordDiagnosticEmission(message, target);
                    }
                }
            }
        }

        private sealed class UntargetedRegistration<T> : Registration<T>
            where T : IUntargetedMessage
        {
            private readonly RegistrationKind _kind;
            private readonly object _userHandler;
            private readonly int _priority;

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

            public override MessageRegistrationMetadata Metadata =>
                new MessageRegistrationMetadata(
                    null,
                    typeof(T),
                    GetRegistrationType(_kind),
                    _priority
                );

            internal override MessageHandler.HandlerDeregistration RegisterValidated()
            {
                MessageHandler messageHandler = Token._messageHandler;
                IMessageBus messageBus = ResolveRegistrationMessageBus();
                switch (_kind)
                {
                    case RegistrationKind.UntargetedHandlerAction:
                        return StoreDeregistration(
                            messageHandler.RegisterUntargetedMessageHandler(
                                (Action<T>)_userHandler,
                                GetBinding(messageBus).AugmentedScalarAction,
                                priority: _priority,
                                messageBus: messageBus
                            )
                        );
                    case RegistrationKind.UntargetedHandlerFast:
                        return StoreDeregistration(
                            messageHandler.RegisterUntargetedMessageHandler(
                                (MessageHandler.FastHandler<T>)_userHandler,
                                GetBinding(messageBus).AugmentedScalarFast,
                                priority: _priority,
                                messageBus: messageBus
                            )
                        );
                    case RegistrationKind.UntargetedPostProcessorFast:
                        return StoreDeregistration(
                            messageHandler.RegisterUntargetedPostProcessor(
                                (MessageHandler.FastHandler<T>)_userHandler,
                                GetBinding(messageBus).AugmentedScalarFast,
                                _priority,
                                messageBus
                            )
                        );
                    default:
                        throw new InvalidOperationException(
                            $"Unexpected registration kind {_kind} for UntargetedRegistration<{typeof(T)}>."
                        );
                }
            }

            public override void Validate()
            {
                string parameterName =
                    _kind == RegistrationKind.UntargetedPostProcessorFast
                        ? "untargetedPostProcessor"
                        : "untargetedHandler";
                ThrowIfNullHandler(_userHandler, parameterName);
            }

            private Binding _binding;

            private Binding GetBinding(IMessageBus messageBus)
            {
                IMessageBus resolvedBus = messageBus ?? Token._messageHandler.DefaultMessageBus;
                if (_binding == null || !ReferenceEquals(_binding.MessageBus, resolvedBus))
                {
                    _binding = new Binding(this, resolvedBus);
                }
                return _binding;
            }

            private sealed class Binding : InvocationBinding
            {
                private readonly Action<T> _scalarAction;
                private readonly MessageHandler.FastHandler<T> _scalarFast;

                public Binding(UntargetedRegistration<T> registration, IMessageBus messageBus)
                    : base(registration.Token, registration.Handle, messageBus)
                {
                    _scalarAction = registration._userHandler as Action<T>;
                    _scalarFast = registration._userHandler as MessageHandler.FastHandler<T>;
                }

                public void AugmentedScalarAction(in T message)
                {
                    _scalarAction(message);
                    if (Token._diagnosticMode)
                    {
                        RecordDiagnosticEmission(message);
                    }
                }

                public void AugmentedScalarFast(in T message)
                {
                    _scalarFast(in message);
                    if (Token._diagnosticMode)
                    {
                        RecordDiagnosticEmission(message);
                    }
                }
            }
        }

        private sealed class BroadcastRegistration<T> : Registration<T>
            where T : IBroadcastMessage
        {
            private readonly RegistrationKind _kind;
            private readonly InstanceId _context;
            private readonly object _userHandler;
            private readonly int _priority;

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

            public override MessageRegistrationMetadata Metadata =>
                new MessageRegistrationMetadata(
                    _kind == RegistrationKind.BroadcastHandlerAction
                    || _kind == RegistrationKind.BroadcastHandlerFast
                    || _kind == RegistrationKind.BroadcastPostProcessorAction
                    || _kind == RegistrationKind.BroadcastPostProcessorFast
                        ? _context
                        : (InstanceId?)null,
                    typeof(T),
                    GetRegistrationType(_kind),
                    _priority
                );

            internal override MessageHandler.HandlerDeregistration RegisterValidated()
            {
                MessageHandler messageHandler = Token._messageHandler;
                IMessageBus messageBus = ResolveRegistrationMessageBus();
                switch (_kind)
                {
                    case RegistrationKind.BroadcastHandlerAction:
                        return StoreDeregistration(
                            messageHandler.RegisterSourcedBroadcastMessageHandler(
                                _context,
                                (Action<T>)_userHandler,
                                GetBinding(messageBus).AugmentedScalarAction,
                                priority: _priority,
                                messageBus: messageBus
                            )
                        );
                    case RegistrationKind.BroadcastHandlerFast:
                        return StoreDeregistration(
                            messageHandler.RegisterSourcedBroadcastMessageHandler(
                                _context,
                                (MessageHandler.FastHandler<T>)_userHandler,
                                GetBinding(messageBus).AugmentedScalarFast,
                                priority: _priority,
                                messageBus: messageBus
                            )
                        );
                    case RegistrationKind.BroadcastPostProcessorAction:
                        return StoreDeregistration(
                            messageHandler.RegisterSourcedBroadcastPostProcessor(
                                _context,
                                (Action<T>)_userHandler,
                                GetBinding(messageBus).AugmentedScalarAction,
                                _priority,
                                messageBus
                            )
                        );
                    case RegistrationKind.BroadcastPostProcessorFast:
                        return StoreDeregistration(
                            messageHandler.RegisterSourcedBroadcastPostProcessor(
                                _context,
                                (MessageHandler.FastHandler<T>)_userHandler,
                                GetBinding(messageBus).AugmentedScalarFast,
                                _priority,
                                messageBus
                            )
                        );
                    case RegistrationKind.BroadcastWithoutSourceAction:
                        return StoreDeregistration(
                            messageHandler.RegisterSourcedBroadcastWithoutSource(
                                (Action<InstanceId, T>)_userHandler,
                                GetBinding(messageBus).AugmentedContextAction,
                                priority: _priority,
                                messageBus: messageBus
                            )
                        );
                    case RegistrationKind.BroadcastWithoutSourceFast:
                        return StoreDeregistration(
                            messageHandler.RegisterSourcedBroadcastWithoutSource(
                                (MessageHandler.FastHandlerWithContext<T>)_userHandler,
                                GetBinding(messageBus).AugmentedContextFast,
                                priority: _priority,
                                messageBus: messageBus
                            )
                        );
                    case RegistrationKind.BroadcastWithoutSourcePostProcessorAction:
                        return StoreDeregistration(
                            messageHandler.RegisterSourcedBroadcastWithoutSourcePostProcessor(
                                (Action<InstanceId, T>)_userHandler,
                                GetBinding(messageBus).AugmentedContextAction,
                                priority: _priority,
                                messageBus
                            )
                        );
                    case RegistrationKind.BroadcastWithoutSourcePostProcessorFast:
                        return StoreDeregistration(
                            messageHandler.RegisterSourcedBroadcastWithoutSourcePostProcessor(
                                (MessageHandler.FastHandlerWithContext<T>)_userHandler,
                                GetBinding(messageBus).AugmentedContextFast,
                                priority: _priority,
                                messageBus
                            )
                        );
                    default:
                        throw new InvalidOperationException(
                            $"Unexpected registration kind {_kind} for BroadcastRegistration<{typeof(T)}>."
                        );
                }
            }

            public override void Validate()
            {
                string parameterName;
                switch (_kind)
                {
                    case RegistrationKind.BroadcastPostProcessorAction:
                    case RegistrationKind.BroadcastPostProcessorFast:
                        parameterName = "broadcastPostProcessor";
                        break;
                    default:
                        parameterName = "broadcastHandler";
                        break;
                }

                ThrowIfNullHandler(_userHandler, parameterName);
            }

            private Binding _binding;

            private Binding GetBinding(IMessageBus messageBus)
            {
                IMessageBus resolvedBus = messageBus ?? Token._messageHandler.DefaultMessageBus;
                if (_binding == null || !ReferenceEquals(_binding.MessageBus, resolvedBus))
                {
                    _binding = new Binding(this, resolvedBus);
                }
                return _binding;
            }

            private sealed class Binding : InvocationBinding
            {
                private readonly Action<T> _scalarAction;
                private readonly MessageHandler.FastHandler<T> _scalarFast;
                private readonly Action<InstanceId, T> _contextAction;
                private readonly MessageHandler.FastHandlerWithContext<T> _contextFast;
                private readonly InstanceId _context;

                public Binding(BroadcastRegistration<T> registration, IMessageBus messageBus)
                    : base(registration.Token, registration.Handle, messageBus)
                {
                    _scalarAction = registration._userHandler as Action<T>;
                    _scalarFast = registration._userHandler as MessageHandler.FastHandler<T>;
                    _contextAction = registration._userHandler as Action<InstanceId, T>;
                    _contextFast =
                        registration._userHandler as MessageHandler.FastHandlerWithContext<T>;
                    _context = registration._context;
                }

                public void AugmentedScalarAction(in T message)
                {
                    _scalarAction(message);
                    if (Token._diagnosticMode)
                    {
                        RecordDiagnosticEmission(message, _context);
                    }
                }

                public void AugmentedScalarFast(in T message)
                {
                    _scalarFast(in message);
                    if (Token._diagnosticMode)
                    {
                        RecordDiagnosticEmission(message, _context);
                    }
                }

                /*
                    Broadcast context invokers for the without-source kinds. Emission data uses
                    the dispatch-supplied source (the readonly parameter), not the stored _context
                    (default for these kinds), matching the former bodies exactly.
                */
                public void AugmentedContextAction(in InstanceId source, in T message)
                {
                    _contextAction(source, message);
                    if (Token._diagnosticMode)
                    {
                        RecordDiagnosticEmission(message, source);
                    }
                }

                public void AugmentedContextFast(in InstanceId source, in T message)
                {
                    _contextFast(in source, in message);
                    if (Token._diagnosticMode)
                    {
                        RecordDiagnosticEmission(message, source);
                    }
                }
            }
        }

        private abstract class InterceptorRegistration<T> : Registration
            where T : IMessage
        {
            protected readonly int Priority;
            private MessageHandler.InterceptorDeregistrationState<T> _deregistration;
            private bool _hasInlineDeregistration;

            protected InterceptorRegistration(MessageRegistrationToken token, int priority)
                : base(token)
            {
                Priority = priority;
            }

            protected MessageHandler.HandlerDeregistration StoreDeregistration(
                MessageHandler.InterceptorDeregistrationState<T> deregistration
            )
            {
                if (_hasInlineDeregistration)
                {
                    return deregistration;
                }

                _deregistration = deregistration;
                _hasInlineDeregistration = true;
                return this;
            }

            internal override void Deregister()
            {
                if (!_hasInlineDeregistration)
                {
                    return;
                }

                _deregistration.Deregister();
                _hasInlineDeregistration = false;
            }
        }

        private sealed class UntargetedInterceptorRegistration<T> : InterceptorRegistration<T>
            where T : IUntargetedMessage
        {
            private readonly IMessageBus.UntargetedInterceptor<T> _interceptor;

            internal UntargetedInterceptorRegistration(
                MessageRegistrationToken token,
                IMessageBus.UntargetedInterceptor<T> interceptor,
                int priority
            )
                : base(token, priority)
            {
                _interceptor = interceptor;
            }

            public override MessageRegistrationMetadata Metadata =>
                new MessageRegistrationMetadata(
                    null,
                    typeof(T),
                    MessageRegistrationType.UntargetedInterceptor,
                    Priority
                );

            internal override MessageHandler.HandlerDeregistration RegisterValidated()
            {
                return StoreDeregistration(
                    Token._messageHandler.RegisterUntargetedInterceptor(
                        _interceptor,
                        Priority,
                        ResolveRegistrationMessageBus()
                    )
                );
            }

            public override void Validate()
            {
                ThrowIfNullHandler(_interceptor, "interceptor");
            }
        }

        private sealed class TargetedInterceptorRegistration<T> : InterceptorRegistration<T>
            where T : ITargetedMessage
        {
            private readonly IMessageBus.TargetedInterceptor<T> _interceptor;

            internal TargetedInterceptorRegistration(
                MessageRegistrationToken token,
                IMessageBus.TargetedInterceptor<T> interceptor,
                int priority
            )
                : base(token, priority)
            {
                _interceptor = interceptor;
            }

            public override MessageRegistrationMetadata Metadata =>
                new MessageRegistrationMetadata(
                    null,
                    typeof(T),
                    MessageRegistrationType.TargetedInterceptor,
                    Priority
                );

            internal override MessageHandler.HandlerDeregistration RegisterValidated()
            {
                return StoreDeregistration(
                    Token._messageHandler.RegisterTargetedInterceptor(
                        _interceptor,
                        Priority,
                        ResolveRegistrationMessageBus()
                    )
                );
            }

            public override void Validate()
            {
                ThrowIfNullHandler(_interceptor, "interceptor");
            }
        }

        private sealed class BroadcastInterceptorRegistration<T> : InterceptorRegistration<T>
            where T : IBroadcastMessage
        {
            private readonly IMessageBus.BroadcastInterceptor<T> _interceptor;

            internal BroadcastInterceptorRegistration(
                MessageRegistrationToken token,
                IMessageBus.BroadcastInterceptor<T> interceptor,
                int priority
            )
                : base(token, priority)
            {
                _interceptor = interceptor;
            }

            public override MessageRegistrationMetadata Metadata =>
                new MessageRegistrationMetadata(
                    null,
                    typeof(T),
                    MessageRegistrationType.BroadcastInterceptor,
                    Priority
                );

            internal override MessageHandler.HandlerDeregistration RegisterValidated()
            {
                return StoreDeregistration(
                    Token._messageHandler.RegisterBroadcastInterceptor(
                        _interceptor,
                        Priority,
                        ResolveRegistrationMessageBus()
                    )
                );
            }

            public override void Validate()
            {
                ThrowIfNullHandler(_interceptor, "interceptor");
            }
        }

        /*
            Global accept-all is non-generic: its three sub-handlers are the fixed
            IMessage facades. Stores the three user delegates (as object, since the two
            public overloads differ in delegate shape -- Action vs FastHandler/
            FastHandlerWithContext) and exposes six augmented sub-invokers (three per
            overload shape). The kind-switch picks the matching MessageHandler.
            RegisterGlobalAcceptAll overload and binds the three augmented invokers.
        */
        private sealed class GlobalAcceptAllRegistration : Registration
        {
            private readonly RegistrationKind _kind;

            /*
                Typed sub-handlers (one shape-trio is set per kind). The per-dispatch
                invokers call these directly -- no castclass on the hot global-dispatch
                path (the heaviest fan-out, so the castclass cost there was the worst).
            */
            private readonly Action<IUntargetedMessage> _untargetedAction;
            private readonly Action<InstanceId, ITargetedMessage> _targetedAction;
            private readonly Action<InstanceId, IBroadcastMessage> _broadcastAction;
            private readonly MessageHandler.FastHandler<IUntargetedMessage> _untargetedFast;
            private readonly MessageHandler.FastHandlerWithContext<ITargetedMessage> _targetedFast;
            private readonly MessageHandler.FastHandlerWithContext<IBroadcastMessage> _broadcastFast;
            private MessageHandler.GlobalAcceptAllDeregistrationState _deregistration;
            private bool _hasInlineDeregistration;

            public override MessageRegistrationMetadata Metadata =>
                new MessageRegistrationMetadata(
                    null,
                    typeof(IMessage),
                    MessageRegistrationType.GlobalAcceptAll,
                    0
                );

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

            internal override MessageHandler.HandlerDeregistration RegisterValidated()
            {
                MessageHandler messageHandler = Token._messageHandler;
                IMessageBus messageBus = ResolveRegistrationMessageBus();
                if (_kind == RegistrationKind.GlobalAcceptAllAction)
                {
                    return StoreDeregistration(
                        messageHandler.RegisterGlobalAcceptAll(
                            _untargetedAction,
                            GetBinding(messageBus).AugmentedUntargetedAction,
                            _targetedAction,
                            GetBinding(messageBus).AugmentedTargetedAction,
                            _broadcastAction,
                            GetBinding(messageBus).AugmentedBroadcastAction,
                            messageBus
                        )
                    );
                }

                return StoreDeregistration(
                    messageHandler.RegisterGlobalAcceptAll(
                        _untargetedFast,
                        GetBinding(messageBus).AugmentedUntargetedFast,
                        _targetedFast,
                        GetBinding(messageBus).AugmentedTargetedFast,
                        _broadcastFast,
                        GetBinding(messageBus).AugmentedBroadcastFast,
                        messageBus
                    )
                );
            }

            public override void Validate()
            {
                if (_kind == RegistrationKind.GlobalAcceptAllAction)
                {
                    ThrowIfNullHandler(_untargetedAction, "acceptAllUntargeted");
                    ThrowIfNullHandler(_targetedAction, "acceptAllTargeted");
                    ThrowIfNullHandler(_broadcastAction, "acceptAllBroadcast");
                }
                else
                {
                    ThrowIfNullHandler(_untargetedFast, "acceptAllUntargeted");
                    ThrowIfNullHandler(_targetedFast, "acceptAllTargeted");
                    ThrowIfNullHandler(_broadcastFast, "acceptAllBroadcast");
                }
            }

            internal override void Deregister()
            {
                if (!_hasInlineDeregistration)
                {
                    return;
                }

                _deregistration.Deregister();
                _hasInlineDeregistration = false;
            }

            private MessageHandler.HandlerDeregistration StoreDeregistration(
                MessageHandler.GlobalAcceptAllDeregistrationState deregistration
            )
            {
                if (_hasInlineDeregistration)
                {
                    return deregistration;
                }

                _deregistration = deregistration;
                _hasInlineDeregistration = true;
                return this;
            }

            private Binding _binding;

            private Binding GetBinding(IMessageBus messageBus)
            {
                IMessageBus resolvedBus = messageBus ?? Token._messageHandler.DefaultMessageBus;
                if (_binding == null || !ReferenceEquals(_binding.MessageBus, resolvedBus))
                {
                    _binding = new Binding(this, resolvedBus);
                }
                return _binding;
            }

            private sealed class Binding : InvocationBinding
            {
                private readonly Action<IUntargetedMessage> _untargetedAction;
                private readonly Action<InstanceId, ITargetedMessage> _targetedAction;
                private readonly Action<InstanceId, IBroadcastMessage> _broadcastAction;
                private readonly MessageHandler.FastHandler<IUntargetedMessage> _untargetedFast;
                private readonly MessageHandler.FastHandlerWithContext<ITargetedMessage> _targetedFast;
                private readonly MessageHandler.FastHandlerWithContext<IBroadcastMessage> _broadcastFast;

                public Binding(GlobalAcceptAllRegistration registration, IMessageBus messageBus)
                    : base(registration.Token, registration.Handle, messageBus)
                {
                    _untargetedAction = registration._untargetedAction;
                    _targetedAction = registration._targetedAction;
                    _broadcastAction = registration._broadcastAction;
                    _untargetedFast = registration._untargetedFast;
                    _targetedFast = registration._targetedFast;
                    _broadcastFast = registration._broadcastFast;
                }

                public void AugmentedUntargetedAction(IUntargetedMessage message)
                {
                    _untargetedAction(message);
                    if (Token._diagnosticMode)
                    {
                        RecordDiagnosticEmission(message);
                    }
                }

                public void AugmentedTargetedAction(InstanceId target, ITargetedMessage message)
                {
                    _targetedAction(target, message);
                    if (Token._diagnosticMode)
                    {
                        RecordDiagnosticEmission(message, target);
                    }
                }

                public void AugmentedBroadcastAction(InstanceId source, IBroadcastMessage message)
                {
                    _broadcastAction(source, message);
                    if (Token._diagnosticMode)
                    {
                        RecordDiagnosticEmission(message, source);
                    }
                }

                public void AugmentedUntargetedFast(in IUntargetedMessage message)
                {
                    _untargetedFast(in message);
                    if (Token._diagnosticMode)
                    {
                        RecordDiagnosticEmission(message);
                    }
                }

                public void AugmentedTargetedFast(in InstanceId target, in ITargetedMessage message)
                {
                    _targetedFast(in target, in message);
                    if (Token._diagnosticMode)
                    {
                        RecordDiagnosticEmission(message, target);
                    }
                }

                public void AugmentedBroadcastFast(
                    in InstanceId source,
                    in IBroadcastMessage message
                )
                {
                    _broadcastFast(in source, in message);
                    if (Token._diagnosticMode)
                    {
                        RecordDiagnosticEmission(message, source);
                    }
                }
            }
        }

        /// <summary>
        /// Wraps a registration handle in a disposable value type that removes it on dispose.
        /// </summary>
        /// <param name="handle">The registration handle to remove when disposed.</param>
        /// <returns>
        /// A concrete <see cref="RegistrationDisposable"/> value. Keep it as that concrete type to
        /// avoid boxing it as <see cref="IDisposable"/>.
        /// </returns>
        public RegistrationDisposable AsDisposable(MessageRegistrationHandle handle)
        {
            return new RegistrationDisposable(this, handle);
        }

        /// <summary>
        /// Removes one registration when disposed.
        /// </summary>
        /// <remarks>
        /// Copies are safe to dispose. The token's opaque handle identity makes every disposal
        /// after the first successful removal have no effect, including after the vacated slot is
        /// reused by a new registration. A failed removal remains retryable through any copy.
        /// <para><b>Fixed in v3.2.3.</b></para>
        /// </remarks>
        public readonly struct RegistrationDisposable : IDisposable
        {
            private readonly MessageRegistrationToken _token;
            private readonly MessageRegistrationHandle _handle;

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
            }

            /// <summary>
            /// Attempts to remove the wrapped registration. Only the first successful removal has
            /// an effect; a failed attempt remains retryable.
            /// </summary>
            public void Dispose()
            {
                /*
                    RemoveRegistration rejects a stale opaque handle even after its slot is reused.
                    Keeping the wrapper stateless makes every copy follow that authoritative check.
                */
                if (_token != null)
                {
                    _token.RemoveRegistration(_handle);
                }
            }
        }

        /// <summary>
        /// Creates a disabled registration token bound to the given handler.
        /// </summary>
        /// <param name="messageHandler">Message handler to register handlers to.</param>
        /// <param name="messageBus">
        /// Bus override. Pass <see langword="null"/> to use the handler's default bus when a registration activates.
        /// </param>
        /// <returns>A disabled token that stages handlers until <see cref="Enable"/> is called.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="messageHandler"/> is null.</exception>
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
        /// Removes registrations and clears diagnostics when cleanup completes.
        /// </summary>
        /// <remarks>
        /// Uses the same failure and reentrancy behavior as <see cref="UnregisterAll"/>. Pending
        /// cleanup remains owned by the token. Disposal does not prevent later registrations.
        /// </remarks>
        public void Dispose()
        {
            UnregisterAll();
        }
    }
}
