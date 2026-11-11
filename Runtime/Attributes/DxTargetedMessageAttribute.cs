namespace DxMessaging.Core.Attributes
{
    using System;

    /// <summary>
    /// Marks a type as a targeted DxMessaging message.
    /// </summary>
    /// <remarks>
    /// Apply to a <c>class</c> or <c>struct</c> to indicate it represents a message addressed to a
    /// specific <c>DxMessaging.Core.InstanceId</c> (see <c>DxMessaging.Core.Messages.ITargetedMessage</c>).
    /// This attribute can be used in place of the generic interface variant when you want
    /// source generators/tooling to recognize the message type by attribute.
    /// </remarks>
    /// <example>
    /// <code>
    /// [DxMessaging.Core.Attributes.DxTargetedMessage]
    /// public readonly partial struct HealRequest
    /// {
    ///     public readonly int amount;
    ///     public HealRequest(int amount) { this.amount = amount; }
    /// }
    /// </code>
    /// </example>
    [AttributeUsage(
        AttributeTargets.Class | AttributeTargets.Struct,
        Inherited = false,
        AllowMultiple = false
    )]
    public sealed class DxTargetedMessageAttribute : Attribute { }
}
