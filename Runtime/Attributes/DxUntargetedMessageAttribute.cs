namespace DxMessaging.Core.Attributes
{
    using System;

    /// <summary>
    /// Marks a type as an untargeted DxMessaging message.
    /// </summary>
    /// <remarks>
    /// Apply to a <c>class</c> or <c>struct</c> to indicate it represents a global notification that
    /// is not addressed to a specific target (see <c>DxMessaging.Core.Messages.IUntargetedMessage</c>).
    /// This attribute enables source generators and tooling to recognize message types without
    /// requiring the generic interface form.
    /// </remarks>
    /// <example>
    /// <code>
    /// [DxMessaging.Core.Attributes.DxUntargetedMessage]
    /// public readonly partial struct WorldRegenerated
    /// {
    ///     public readonly int seed;
    ///     public WorldRegenerated(int seed) { this.seed = seed; }
    /// }
    /// </code>
    /// </example>
    [AttributeUsage(
        AttributeTargets.Class | AttributeTargets.Struct,
        Inherited = false,
        AllowMultiple = false
    )]
    public sealed class DxUntargetedMessageAttribute : Attribute { }
}
