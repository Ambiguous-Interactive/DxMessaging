namespace DxMessaging.Core.MessageBus
{
    /// <summary>
    /// Provides an <see cref="IMessageBus"/> instance for DI-friendly scenarios.
    /// </summary>
    public interface IMessageBusProvider
    {
        /// <summary>
        /// Resolves the <see cref="IMessageBus"/> that should be used for the current context.
        /// </summary>
        /// <returns>The resolved message bus, or <see langword="null"/> to defer to fallbacks.</returns>
        IMessageBus Resolve();
    }

    internal static class MessageBusProviderUtility
    {
        internal static bool IsAvailable(IMessageBusProvider provider)
        {
            if (provider == null)
            {
                return false;
            }

#if UNITY_2021_3_OR_NEWER
            if (provider is UnityEngine.Object unityProvider)
            {
                return unityProvider != null;
            }
#endif

            return true;
        }
    }
}
