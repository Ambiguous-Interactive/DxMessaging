namespace DxMessaging.Core.MessageBus
{
    using System;

    /// <summary>
    /// Factory abstraction that creates <see cref="MessageRegistrationToken"/> lifetimes bound to a resolved message bus.
    /// </summary>
    public interface IMessageRegistrationBuilder
    {
        /// <summary>
        /// Creates a <see cref="MessageRegistrationLease"/> according to the supplied <paramref name="options"/>.
        /// </summary>
        /// <param name="options">Build configuration describing ownership, message bus preference, and lifecycle hooks.</param>
        /// <returns>Lease that exposes the constructed <see cref="MessageRegistrationToken"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
        /// <exception cref="MessageRegistrationBuildException">ActivateOnBuild activation failed and automatic cleanup remains retryable.</exception>
        MessageRegistrationLease Build(MessageRegistrationBuildOptions options);
    }
}
