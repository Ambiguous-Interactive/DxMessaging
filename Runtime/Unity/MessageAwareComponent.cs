#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Unity
{
    using System;
    using Core;
    using Core.MessageBus;
    using Core.Messages;
    using UnityEngine;

    /// <summary>
    /// Base MonoBehaviour that wires up a <see cref="MessagingComponent"/> and a registration token.
    /// </summary>
    /// <remarks>
    /// Derive from this to quickly make components that participate in DxMessaging without boilerplate.
    /// Override <see cref="RegisterMessageHandlers"/> to stage your registrations. By default this class
    /// subscribes to <see cref="Core.Messages.StringMessage"/> in a few common forms to demonstrate usage.
    ///
    /// Lifecycle integration:
    /// - <see cref="Awake"/> creates the token and calls <see cref="RegisterMessageHandlers"/>.
    /// - <see cref="OnEnable"/>/<see cref="OnDisable"/> enable/disable the token when
    ///   <see cref="MessageRegistrationTiedToEnableStatus"/> is true.
    /// - <see cref="OnDestroy"/> disables the token and releases references.
    /// </remarks>
    /// <example>
    /// <code>
    /// public sealed class HealthComponent : DxMessaging.Unity.MessageAwareComponent
    /// {
    ///     protected override void RegisterMessageHandlers()
    ///     {
    ///         base.RegisterMessageHandlers();
    ///         // Listen for targeted damage commands to this component
    ///         _ = Token.RegisterComponentTargeted&lt;TookDamage&gt;(this, HandleDamage);
    ///         // Listen for global difficulty changes
    ///         _ = Token.RegisterUntargeted&lt;DifficultyChanged&gt;(HandleDifficulty);
    ///     }
    ///
    ///     private void HandleDamage(ref TookDamage msg)
    ///     {
    ///         // apply damage
    ///     }
    ///
    ///     private void HandleDifficulty(ref DifficultyChanged msg)
    ///     {
    ///         // adjust health scaling
    ///     }
    /// }
    /// </code>
    /// </example>
    [RequireComponent(typeof(MessagingComponent))]
    public abstract class MessageAwareComponent : MonoBehaviour
    {
        /// <summary>
        /// Accessor for the token that manages registrations for this component.
        /// </summary>
        public virtual MessageRegistrationToken Token => _messageRegistrationToken;

        protected MessageRegistrationToken _messageRegistrationToken;

        /// <summary>
        /// If true, will register/unregister handles when the component is enabled or disabled.
        /// </summary>
        protected virtual bool MessageRegistrationTiedToEnableStatus => true;

        /// <summary>
        /// If true, registers demo handlers for <see cref="Core.Messages.StringMessage"/> and
        /// <see cref="Core.Messages.GlobalStringMessage"/>. Override and return <c>false</c> to disable.
        /// </summary>
        protected virtual bool RegisterForStringMessages => true;

        protected MessagingComponent _messagingComponent;
        protected IMessageBus _configuredMessageBus;
        protected IMessageBusProvider _configuredMessageBusProvider;
        protected MessageBusProviderHandle _configuredMessageBusProviderHandle;

#if UNITY_EDITOR || DEBUG
        // G6: latch the OnEnable self-check log to fire at most once per component instance.
        // [NonSerialized] keeps the latch from being saved into the scene; every fresh
        // instance starts with the latch clear and gets at most one breadcrumb if it really is
        // missing the base.Awake() call.
        [NonSerialized]
        private bool _selfCheckLogged;
#endif

        /// <summary>
        /// Creates the <see cref="MessagingComponent"/>, token, and calls <see cref="RegisterMessageHandlers"/>.
        /// </summary>
        protected virtual void Awake()
        {
            _messagingComponent = GetComponent<MessagingComponent>();
            if (
                _configuredMessageBusProvider == null
                && _configuredMessageBusProviderHandle.TryGetProvider(
                    out IMessageBusProvider handleProvider
                )
            )
            {
                _configuredMessageBusProvider = handleProvider;
            }

            if (_configuredMessageBusProvider != null)
            {
                _messagingComponent.Configure(
                    _configuredMessageBusProvider,
                    MessageBusRebindMode.PreserveRegistrations
                );
            }
            else if (_configuredMessageBus != null)
            {
                _messagingComponent.Configure(
                    _configuredMessageBus,
                    MessageBusRebindMode.PreserveRegistrations
                );
            }
            _messageRegistrationToken = _messagingComponent.Create(this);
            RegisterMessageHandlers();
        }

        /// <summary>
        /// Stage message registrations for this component. Called from <see cref="Awake"/>.
        /// </summary>
        protected virtual void RegisterMessageHandlers()
        {
            if (RegisterForStringMessages)
            {
                _ = _messageRegistrationToken.RegisterGameObjectTargeted<StringMessage>(
                    gameObject,
                    HandleStringGameObjectMessage
                );
                _ = _messageRegistrationToken.RegisterComponentTargeted<StringMessage>(
                    this,
                    HandleStringComponentMessage
                );
                _ = _messageRegistrationToken.RegisterUntargeted<GlobalStringMessage>(
                    HandleGlobalStringMessage
                );
            }
        }

        /// <summary>
        /// Enables the token if <see cref="MessageRegistrationTiedToEnableStatus"/> is true.
        /// </summary>
        protected virtual void OnEnable()
        {
            if (MessageRegistrationTiedToEnableStatus)
            {
                _messageRegistrationToken?.Enable();
            }
#if UNITY_EDITOR || DEBUG
            // G6: belt-and-braces self-check. If we got here without a registration token, the
            // most common cause is a subclass that overrode Awake without calling base.Awake();
            // the analyzer DXMSG006 catches this at compile time but only if the project loads
            // the analyzer DLL (e.g. user has disabled analyzers, opened the project on a Unity
            // version that does not load the DLL, or the analyzer suppressed via attribute /
            // ignore list). We surface a one-time-per-instance LogError so the failure mode is
            // not silent. Gated on UNITY_EDITOR || DEBUG so release builds pay zero cost.
            if (_messageRegistrationToken == null && !_selfCheckLogged)
            {
                _selfCheckLogged = true;
                Debug.LogError(
                    $"[DxMessaging] {GetType().Name} appears to be missing a base.Awake() call; "
                        + "no registration token exists, so this component will not receive messages. "
                        + "See https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/docs/reference/analyzers.md#dxmsg006-missing-base-call",
                    this
                );
            }
#endif
        }

        /// <summary>
        /// Disables the token if <see cref="MessageRegistrationTiedToEnableStatus"/> is true.
        /// </summary>
        protected virtual void OnDisable()
        {
            if (MessageRegistrationTiedToEnableStatus)
            {
                _messageRegistrationToken?.Disable();
            }
        }

        /// <summary>
        /// Ensures deregistration and clears the token on destroy.
        /// </summary>
        protected virtual void OnDestroy()
        {
            if (_messagingComponent != null)
            {
                _messagingComponent.Release(this);
            }

            _messageRegistrationToken?.Disable();
            _messageRegistrationToken = null;
            _messagingComponent = null;
        }

        /// <summary>
        /// Receives the application quit message to avoid spurious Unity SendMessage warnings in tests.
        /// No default behavior.
        /// </summary>
        protected virtual void OnApplicationQuit()
        {
            // Intentionally left blank
        }

        /// <summary>
        /// Supplies a custom <see cref="IMessageBus"/> for this component's underlying <see cref="MessageHandler"/>.
        /// </summary>
        /// <param name="messageBus">
        /// Container-managed bus to use. Pass <see langword="null"/> to revert to the global bus
        /// returned by <see cref="MessageHandler.MessageBus"/>.
        /// </param>
        /// <param name="rebindMode">Controls whether existing handlers should migrate to the provided bus immediately.</param>
        /// <remarks>
        /// Call this during dependency injection (before <see cref="Awake"/>) to ensure the token is created against
        /// the provided bus, or invoke it later to retarget existing registrations.
        /// </remarks>
        public virtual void ConfigureMessageBus(
            IMessageBus messageBus,
            MessageBusRebindMode rebindMode
        )
        {
            _configuredMessageBus = messageBus;
            _configuredMessageBusProvider = null;
            _configuredMessageBusProviderHandle = MessageBusProviderHandle.Empty;
            _messagingComponent?.Configure(_configuredMessageBus, rebindMode);
        }

        /// <summary>
        /// Supplies an <see cref="IMessageBusProvider"/> for this component's underlying <see cref="MessageHandler"/>.
        /// </summary>
        /// <param name="messageBusProvider">Provider used to resolve buses for this component.</param>
        /// <param name="rebindMode">Controls whether existing handlers should migrate to the provided bus immediately.</param>
        public virtual void ConfigureMessageBus(
            IMessageBusProvider messageBusProvider,
            MessageBusRebindMode rebindMode
        )
        {
            _configuredMessageBusProvider = messageBusProvider;
            if (messageBusProvider != null)
            {
                _configuredMessageBus = null;
                _configuredMessageBusProviderHandle = MessageBusProviderHandle.FromProvider(
                    messageBusProvider
                );
            }
            else
            {
                _configuredMessageBusProviderHandle = MessageBusProviderHandle.Empty;
            }

            _messagingComponent?.Configure(_configuredMessageBusProvider, rebindMode);
        }

        /// <summary>
        /// Supplies a serialized handle that resolves an <see cref="IMessageBusProvider"/>.
        /// </summary>
        /// <param name="providerHandle">Handle referencing the provider.</param>
        /// <param name="rebindMode">Controls whether existing handlers should migrate to the provided bus immediately.</param>
        public virtual void ConfigureMessageBus(
            MessageBusProviderHandle providerHandle,
            MessageBusRebindMode rebindMode
        )
        {
            _configuredMessageBusProviderHandle = providerHandle;
            if (providerHandle.TryGetProvider(out IMessageBusProvider provider))
            {
                _configuredMessageBusProvider = provider;
                _configuredMessageBus = null;
            }
            else
            {
                _configuredMessageBusProvider = null;
                _configuredMessageBusProviderHandle = MessageBusProviderHandle.Empty;
            }

            _messagingComponent?.Configure(providerHandle, rebindMode);
        }

        /// <summary>
        /// Demo handler: targeted string message to this GameObject.
        /// Override to implement behavior.
        /// </summary>
        protected virtual void HandleStringGameObjectMessage(ref StringMessage message)
        {
            // No-op by default
        }

        /// <summary>
        /// Demo handler: targeted string message to this Component.
        /// Override to implement behavior.
        /// </summary>
        protected virtual void HandleStringComponentMessage(ref StringMessage message)
        {
            // No-op by default
        }

        /// <summary>
        /// Demo handler: global string message.
        /// Override to implement behavior.
        /// </summary>
        protected virtual void HandleGlobalStringMessage(ref GlobalStringMessage message)
        {
            // No-op by default
        }
    }
}
#endif
