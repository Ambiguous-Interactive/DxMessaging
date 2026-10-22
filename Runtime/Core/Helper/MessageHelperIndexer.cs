namespace DxMessaging.Core.Helper
{
    /// <summary>
    /// Provides sequential ID assignment for message types.
    /// </summary>
    /// <remarks>
    /// Message type IDs are intentionally NOT reset during static state reset operations.
    /// Once a message type is assigned an ID, it retains that ID for the lifetime of the
    /// application domain. This design prevents ID collisions and ensures message routing
    /// stability across reset cycles (e.g., when using Enter Play Mode Settings with
    /// Domain Reload disabled in Unity).
    /// </remarks>
    public static class MessageHelperIndexer
    {
        /// <summary>
        /// The total number of message types that have been assigned sequential IDs.
        /// This counter only increases and is never reset.
        /// </summary>
        internal static int TotalMessages = 0;
    }

    /// <summary>Per-message-type storage for the lazily assigned cache index.</summary>
    /// <typeparam name="TMessage">Message type associated with the cached index.</typeparam>
    public static class MessageHelperIndexer<TMessage>
        where TMessage : IMessage
    {
        // ReSharper disable once StaticMemberInGenericType
        internal static int SequentialId = -1;
    }
}
