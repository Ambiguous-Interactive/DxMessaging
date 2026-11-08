namespace DxMessaging.Core.MessageBus.Internal
{
    /// <summary>
    /// First axis of the dispatch slot key. Identifies the structural shape of a
    /// registration: untargeted scalar, targeted (per-recipient), broadcast
    /// (per-source), or global (catch-all that subscribes to every emit of a
    /// registered base shape).
    /// </summary>
    /// <remarks>
    /// Encoded into <see cref="SlotKey.Packed"/> in the high nibble (bits 4-7).
    /// The declared values are 0..3. The four-bit field can encode values 0..15.
    /// </remarks>
    internal enum DispatchKind : byte
    {
        /// <summary>
        /// Untargeted dispatch. No <see cref="InstanceId"/> recipient or source
        /// carried; the message itself is the entire payload.
        /// </summary>
        Untargeted = 0,

        /// <summary>Targeted dispatch. Message carries an <see cref="InstanceId"/> recipient.</summary>
        Targeted = 1,

        /// <summary>Broadcast dispatch. Message carries an <see cref="InstanceId"/> source.</summary>
        Broadcast = 2,

        /// <summary>
        /// Reserved for the global-dispatch axis. Currently unreferenced -- the
        /// global dispatch path uses <see cref="BusGlobalSlot"/> directly with the
        /// per-message-shape <see cref="DispatchKind"/> (<see cref="Untargeted"/>,
        /// <see cref="Targeted"/>, or <see cref="Broadcast"/>) rather than this
        /// value. Kept to lock the bit-packing contract documented on
        /// <see cref="SlotKey"/>.
        /// </summary>
        Global = 3,
    }
}
