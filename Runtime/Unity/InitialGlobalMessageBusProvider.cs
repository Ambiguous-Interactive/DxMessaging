#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Unity
{
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using UnityEngine;

    /// <summary>
    /// Serialized provider that always returns the original global message bus created during static initialisation.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="CurrentGlobalMessageBusProvider"/>, this asset ignores runtime overrides of
    /// <see cref="MessageHandler.MessageBus"/>, making it ideal for debugging or diagnostics scenarios where you need a
    /// stable reference to the startup bus.
    /// </remarks>
    /// <example>
    /// <code>
    /// // Temporarily override the global bus while still keeping a reference to the startup instance.
    /// var initialProvider = Resources.Load<InitialGlobalMessageBusProvider>("InitialGlobalMessageBusProvider");
    /// IMessageBus startupBus = initialProvider.Resolve();
    /// using (MessageHandler.OverrideGlobalMessageBus(customBus))
    /// {
    ///     _ = startupBus; // scenario under test
    /// }
    /// // startupBus still references the original global bus
    /// </code>
    /// </summary>
    [CreateAssetMenu(
        fileName = "InitialGlobalMessageBusProvider",
        menuName = "Wallstop Studios/DxMessaging/Message Bus Providers/Initial Global Message Bus"
    )]
    public sealed class InitialGlobalMessageBusProvider : ScriptableMessageBusProvider
    {
        /// <summary>
        /// Resolves the message bus captured during static initialization before any runtime overrides occur.
        /// </summary>
        /// <returns>The initial global <see cref="IMessageBus"/> instance.</returns>
        public override IMessageBus Resolve()
        {
            return MessageHandler.InitialGlobalMessageBus;
        }
    }
}
#endif
