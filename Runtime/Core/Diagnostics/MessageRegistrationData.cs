namespace DxMessaging.Core.Diagnostics
{
    using System;

    /// <summary>
    /// Describes a staged registration captured for diagnostics.
    /// </summary>
    /// <remarks>
    /// Used by <see cref="Core.MessageRegistrationToken"/> when <c>DiagnosticMode</c> is enabled to correlate
    /// call counts and message emissions for a given registration.
    /// </remarks>
    public readonly struct MessageRegistrationMetadata
    {
        /// <summary>Target/source for targeted/broadcast registrations; null for untargeted.</summary>
        public readonly InstanceId? context;

        /// <summary>Kind of registration (e.g., targeted, post-processor, interceptor).</summary>
        public readonly MessageRegistrationType registrationType;

        /// <summary>Message type registered for.</summary>
        public readonly Type type;

        /// <summary>Priority used for registration; lower runs earlier.</summary>
        public readonly int priority;

        /// <summary>
        /// Creates a new diagnostic registration descriptor.
        /// </summary>
        /// <param name="context">Registration context (target/source) or null.</param>
        /// <param name="type">Message type registered for.</param>
        /// <param name="registrationType">Kind of registration.</param>
        /// <param name="priority">Registration priority; lower runs earlier.</param>
        public MessageRegistrationMetadata(InstanceId? context, Type type, MessageRegistrationType registrationType, int priority)
        {
            this.context = context;
            this.type = type;
            this.registrationType = registrationType;
            this.priority = priority;
        }
    }
}
