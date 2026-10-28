namespace DxMessaging.Core.Messages
{
    using System;

    /// <summary>
    /// Convenience broadcast message carrying a simple string payload.
    /// </summary>
    /// <remarks>
    /// Useful for quick diagnostics and examples when a source identity matters. In production,
    /// prefer strongly typed messages over strings for clarity and safety.
    /// </remarks>
    /// <example>
    /// <code>
    /// // Broadcast a string from a specific GameObject (Unity)
    /// var msg = new DxMessaging.Core.Messages.SourcedStringMessage("Hit");
    /// msg.EmitGameObjectBroadcast(gameObject);
    /// </code>
    /// </example>
    public readonly struct SourcedStringMessage : IBroadcastMessage<SourcedStringMessage>
    {
        /// <summary>Gets the type used to route this message through the bus.</summary>
        public Type MessageType => typeof(SourcedStringMessage);

        /// <summary>Text supplied as the message payload.</summary>
        public readonly string message;

        /// <summary>
        /// Creates a new message wrapping the provided string.
        /// </summary>
        /// <param name="message">The string payload.</param>
        public SourcedStringMessage(string message)
        {
            this.message = message;
        }
    }
}
