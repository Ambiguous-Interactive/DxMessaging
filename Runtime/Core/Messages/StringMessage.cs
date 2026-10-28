namespace DxMessaging.Core.Messages
{
    using System;

    /// <summary>
    /// Convenience targeted message carrying a simple string payload.
    /// </summary>
    /// <remarks>
    /// Often used in examples and tests. In production, prefer defining strongly-typed message structs
    /// with explicit fields for clarity and performance.
    /// </remarks>
    /// <example>
    /// <code>
    /// // Send a string message to a specific GameObject (Unity)
    /// var token = messagingComponent.Create(this);
    /// token.Enable();
    /// var msg = new DxMessaging.Core.Messages.StringMessage("Hello");
    /// msg.EmitGameObjectTargeted(gameObject); // implicit conversion to InstanceId
    /// </code>
    /// </example>
    public readonly struct StringMessage : ITargetedMessage<StringMessage>
    {
        /// <summary>Gets the type used to route this message through the bus.</summary>
        public Type MessageType => typeof(StringMessage);

        /// <summary>Text supplied as the message payload.</summary>
        public readonly string message;

        /// <summary>
        /// Creates a new message wrapping the provided string.
        /// </summary>
        /// <param name="message">The string payload.</param>
        public StringMessage(string message)
        {
            this.message = message;
        }
    }
}
