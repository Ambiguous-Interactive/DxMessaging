namespace DxMessaging.Core.MessageBus
{
    using System;
    using System.Runtime.Serialization;

    /// <summary>
    /// Indicates whether a registration was added or removed.
    /// </summary>
    public enum RegistrationType
    {
        /// <summary>
        /// The registration was added to the bus.
        /// </summary>
        Register,

        /// <summary>
        /// The registration was removed from the bus.
        /// </summary>
        Deregister,
    }

    /// <summary>
    /// Exact registration category used when the handler was wired up.
    /// </summary>
    public enum RegistrationMethod
    {
        /// <summary>
        /// Registered as a targeted handler bound to a specific recipient.
        /// </summary>
        Targeted,

        /// <summary>
        /// Registered as a global untargeted handler.
        /// </summary>
        Untargeted,

        /// <summary>
        /// Registered as a broadcast handler bound to a specific source.
        /// </summary>
        Broadcast,

        /// <summary>
        /// Registered as a broadcast handler without an explicit source.
        /// </summary>
        BroadcastWithoutSource,

        /// <summary>
        /// Registered as a targeted handler that ignores the runtime target.
        /// </summary>
        TargetedWithoutTargeting,

        /// <summary>
        /// Registered as a global catch-all handler.
        /// </summary>
        GlobalAcceptAll,

        /// <summary>
        /// Registered as an interceptor (exact type recorded separately).
        /// </summary>
        Interceptor,

        /// <summary>
        /// Registered as a post-processor for untargeted messages.
        /// </summary>
        UntargetedPostProcessor,

        /// <summary>
        /// Registered as a post-processor for targeted messages.
        /// </summary>
        TargetedPostProcessor,

        /// <summary>
        /// Registered as a post-processor for broadcast messages.
        /// </summary>
        BroadcastPostProcessor,

        /// <summary>
        /// Registered as a post-processor for targeted messages that ignore the runtime target.
        /// </summary>
        TargetedWithoutTargetingPostProcessor,

        /// <summary>
        /// Registered as a post-processor for broadcasts without explicit source information.
        /// </summary>
        BroadcastWithoutSourcePostProcessor,
    }

    /// <summary>
    /// Holds relevant information about a MessagingRegistration event
    /// </summary>
    [Serializable]
    [DataContract]
    public readonly struct MessagingRegistration
    {
        /// <summary>
        /// Numeric identity of the MessageHandler that was registered, without retaining its Unity object.
        /// </summary>
        public readonly InstanceId id;

        /// <summary>
        /// Name of the type of Message that was registered for.
        /// </summary>
        public readonly Type type;

        /// <summary>
        /// New registration? De-registration?
        /// </summary>
        public readonly RegistrationType registrationType;

        /// <summary>
        /// Exact method of the registration.
        /// </summary>
        public readonly RegistrationMethod registrationMethod;

#if UNITY_2021_3_OR_NEWER
        /// <summary>
        /// Unity time of the registration
        /// </summary>
        public readonly float time;
#endif

        /// <summary>
        /// Creates a MessagingRegistration with all of the relevant tidbits filled out.
        /// </summary>
        /// <param name="id">Id of the MessageHandler.</param>
        /// <param name="type">Type of Message.</param>
        /// <param name="registrationType">Register? Deregister?</param>
        /// <param name="registrationMethod">How the Message was chosen to be listened for.</param>
        public MessagingRegistration(
            InstanceId id,
            Type type,
            RegistrationType registrationType,
            RegistrationMethod registrationMethod
        )
        {
            this.id = new InstanceId(id.Id);
            this.type = type;
            this.registrationType = registrationType;
            this.registrationMethod = registrationMethod;
#if UNITY_2021_3_OR_NEWER
            time = UnityEngine.Time.time;
#endif
        }

        /// <summary>
        /// Returns a descriptive string that includes key registration metadata for diagnostics.
        /// </summary>
        /// <returns>Human-readable summary of this registration entry.</returns>
        public override string ToString()
        {
            return new
            {
#if UNITY_2021_3_OR_NEWER
                time,
#endif
                id,
                type,
                registrationType,
                registrationMethod,
            }.ToString();
        }
    }
}
