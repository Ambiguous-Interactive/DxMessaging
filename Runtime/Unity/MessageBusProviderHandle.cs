#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Unity
{
    using System;
    using DxMessaging.Core.MessageBus;
    using UnityEngine;
    using UnityEngine.Serialization;

    /// <summary>
    /// Serializable handle that references a <see cref="ScriptableMessageBusProvider"/> or runtime provider.
    /// </summary>
    [Serializable]
    public struct MessageBusProviderHandle
    {
        [FormerlySerializedAs("providerAsset")]
        [SerializeField]
        private ScriptableMessageBusProvider _provider;

        [NonSerialized]
        private IMessageBusProvider _runtimeProvider;

        internal ScriptableMessageBusProvider SerializedProvider => _provider;

        /// <summary>
        /// Initializes a new instance referencing the supplied provider asset.
        /// </summary>
        /// <param name="provider">Serialized provider asset.</param>
        public MessageBusProviderHandle(ScriptableMessageBusProvider provider)
        {
            _provider = provider;
            _runtimeProvider = provider;
        }

        /// <summary>
        /// Gets an empty handle.
        /// </summary>
        public static MessageBusProviderHandle Empty => default;

        /// <summary>
        /// Creates a handle that wraps a runtime-only provider instance.
        /// </summary>
        /// <param name="provider">Runtime provider.</param>
        /// <returns>Handle referencing the provider.</returns>
        public static MessageBusProviderHandle FromProvider(IMessageBusProvider provider)
        {
            MessageBusProviderHandle handle = default;
            handle._runtimeProvider = provider;
            return handle;
        }

        /// <summary>
        /// Associates the handle with a runtime provider, returning a new handle.
        /// </summary>
        /// <param name="provider">Provider to associate.</param>
        /// <returns>New handle referencing the provider.</returns>
        public MessageBusProviderHandle WithRuntimeProvider(IMessageBusProvider provider)
        {
            MessageBusProviderHandle handle = this;
            handle._runtimeProvider = provider;
            return handle;
        }

        /// <summary>
        /// Attempts to resolve the provider referenced by this handle.
        /// </summary>
        /// <param name="provider">Resolved provider.</param>
        /// <returns><c>true</c> when a provider exists; otherwise <c>false</c>.</returns>
        public bool TryGetProvider(out IMessageBusProvider provider)
        {
            if (IsAvailable(_runtimeProvider))
            {
                provider = _runtimeProvider;
                return true;
            }

            if (_provider != null)
            {
                provider = _provider;
                return true;
            }

            provider = null;
            return false;
        }

        internal static bool IsAvailable(IMessageBusProvider provider) =>
            provider != null
            && (!(provider is UnityEngine.Object unityProvider) || unityProvider != null);

        /// <summary>
        /// Resolves the effective message bus for this handle.
        /// </summary>
        /// <returns>The resolved bus, or <see langword="null"/> if none available.</returns>
        public IMessageBus ResolveBus()
        {
            if (!TryGetProvider(out IMessageBusProvider provider))
            {
                return null;
            }

            return provider.Resolve();
        }
    }
}
#endif
