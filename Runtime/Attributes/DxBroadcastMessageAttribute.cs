namespace DxMessaging.Core.Attributes
{
    using System;

    /// <summary>
    /// Marks a type as a broadcast DxMessaging message.
    /// </summary>
    /// <remarks>
    /// Apply to a <c>class</c> or <c>struct</c> to indicate it represents an event emitted by a
    /// specific source but consumable by any listener (see <c>DxMessaging.Core.Messages.IBroadcastMessage</c>).
    /// This attribute helps source generators/tooling pick up message types via attributes.
    /// </remarks>
    /// <example>
    /// <code>
    /// [DxMessaging.Core.Attributes.DxBroadcastMessage]
    /// public readonly partial struct TookDamage
    /// {
    ///     public readonly int amount;
    ///     public TookDamage(int amount) { this.amount = amount; }
    /// }
    /// </code>
    /// </example>
    [AttributeUsage(
        AttributeTargets.Class | AttributeTargets.Struct,
        Inherited = false,
        AllowMultiple = false
    )]
    public sealed class DxBroadcastMessageAttribute : Attribute { }
}
