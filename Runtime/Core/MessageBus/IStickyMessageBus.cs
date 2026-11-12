namespace DxMessaging.Core.MessageBus
{
    using Messages;

    /// <summary>Optional bus capability for explicitly retained untargeted state.</summary>
    /// <remarks>
    /// Added in 4.0.0. Implementations distinguish absence from a published default or null.
    /// Ordinary emission does not update retained state. This capability follows the bus's
    /// single-threaded ownership contract.
    /// </remarks>
    public interface IStickyMessageBus : IMessageBus
    {
        /// <summary>Retains the offered value, then runs normal untargeted delivery.</summary>
        /// <typeparam name="TMessage">Untargeted state message type.</typeparam>
        /// <param name="message">Offered state, then the mutable normal-pipeline message.</param>
        /// <remarks>
        /// The retained snapshot precedes interceptors. Cancellation, edits and callback exceptions
        /// do not undo it. A nested sticky publication wins. Structs copy; referenced objects remain
        /// shared. First use allocates typed storage; retained payloads live until replacement,
        /// explicit clear or bus reset. Trim preserves current state.
        /// </remarks>
        void EmitSticky<TMessage>(ref TMessage message)
            where TMessage : IUntargetedMessage;

        /// <summary>Reads retained state, returning false and default when nothing is retained.</summary>
        /// <typeparam name="TMessage">Untargeted state message type.</typeparam>
        /// <param name="message">The retained snapshot, or default when absent.</param>
        /// <returns>True even when the published value is default or null.</returns>
        bool TryGetLatest<TMessage>(out TMessage message)
            where TMessage : IUntargetedMessage;

        /// <summary>Immediately releases one type's retained state without changing subscriptions.</summary>
        /// <typeparam name="TMessage">Untargeted state message type.</typeparam>
        /// <returns>True when a retained value was removed; false when already absent.</returns>
        bool ClearLatest<TMessage>()
            where TMessage : IUntargetedMessage;
    }
}
