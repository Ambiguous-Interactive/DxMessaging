#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Unity
{
    using System.Collections.Generic;
    using Core.MessageBus;
    using UnityEngine;

    /// <summary>
    /// Applies a shared message bus or provider to <see cref="MessagingComponent"/> instances in the hierarchy.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MessagingComponentInstaller : MonoBehaviour
    {
        [SerializeField]
        private bool applyOnAwake = true;

        [SerializeField]
        private bool includeInactive = true;

        [SerializeField]
        private MessageBusProviderHandle providerHandle;

        [SerializeReference]
        private IMessageBus explicitMessageBus;

        private readonly List<MessagingComponent> _messagingComponents = new();

        private void Awake()
        {
            if (applyOnAwake)
            {
                ApplyConfiguration();
            }
        }

        /// <summary>
        /// Applies the configured bus or provider to all <see cref="MessagingComponent"/> descendants.
        /// </summary>
        public void ApplyConfiguration()
        {
            GetComponentsInChildren<MessagingComponent>(includeInactive, _messagingComponents);

            if (_messagingComponents.Count == 0)
            {
                return;
            }

            if (providerHandle.TryGetProvider(out _))
            {
                foreach (MessagingComponent component in _messagingComponents)
                {
                    component.Configure(providerHandle, MessageBusRebindMode.RebindActive);
                }

                return;
            }

            if (explicitMessageBus != null)
            {
                foreach (MessagingComponent component in _messagingComponents)
                {
                    component.Configure(explicitMessageBus, MessageBusRebindMode.RebindActive);
                }

                return;
            }

            Debug.LogWarning(
                $"{nameof(MessagingComponentInstaller)} on {name} has no provider or explicit message bus configured.",
                this
            );
        }

        /// <summary>
        /// Creates a registration builder that mirrors the installer configuration.
        /// </summary>
        public IMessageRegistrationBuilder CreateRegistrationBuilder()
        {
            IMessageBusProvider provider = ResolveEffectiveProvider();
            if (provider != null)
            {
                return new MessageRegistrationBuilder(provider);
            }

            if (explicitMessageBus != null)
            {
                return new MessageRegistrationBuilder(
                    new FixedMessageBusProvider(explicitMessageBus)
                );
            }

            return new MessageRegistrationBuilder();
        }

        /// <summary>
        /// Assigns the provider handle that will be used for subsequent applications.
        /// </summary>
        public void SetProvider(MessageBusProviderHandle handle)
        {
            providerHandle = handle;
        }

        /// <summary>
        /// Assigns an explicit message bus that overrides the provider handle when present.
        /// </summary>
        public void SetExplicitMessageBus(IMessageBus messageBus)
        {
            explicitMessageBus = messageBus;
        }

        private IMessageBusProvider ResolveEffectiveProvider()
        {
            if (providerHandle.TryGetProvider(out IMessageBusProvider provider))
            {
                return provider;
            }

            return null;
        }
    }
}
#endif
