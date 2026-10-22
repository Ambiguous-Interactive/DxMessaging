namespace DxMessaging.Core
{
    using System;

    /// <summary>
    /// Base interface for all DxMessaging messages.
    /// </summary>
    /// <remarks>
    /// DxMessaging models three primary categories of messages:
    /// <list type="bullet">
    /// <item><description><see cref="Messages.IUntargetedMessage"/> -- global notifications (for example: settings changed).</description></item>
    /// <item><description><see cref="Messages.ITargetedMessage"/> -- commands or events directed at a specific target.</description></item>
    /// <item><description><see cref="Messages.IBroadcastMessage"/> -- events emitted by a specific source and consumable by any listener.</description></item>
    /// </list>
    /// <para>
    /// Implementors typically use the generic variants (for example, <c>IUntargetedMessage&lt;T&gt;</c>) on <c>struct</c> messages
    /// to avoid boxing and to expose a stable <see cref="MessageType"/> at compile time.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Untargeted (global) message as a struct without boxing
    /// public readonly struct VideoSettingsChanged : DxMessaging.Core.Messages.IUntargetedMessage&lt;VideoSettingsChanged&gt;
    /// {
    ///     public readonly int width;
    ///     public readonly int height;
    ///     public VideoSettingsChanged(int width, int height)
    ///     {
    ///         this.width = width;
    ///         this.height = height;
    ///     }
    /// }
    /// </code>
    /// </example>
    public interface IMessage
    {
        /// <summary>Gets the type used to identify this message to the bus.</summary>
        Type MessageType => GetType();
    }
}
