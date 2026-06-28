namespace DxMessaging.Core.MessageBus
{
    using System;

    /// <summary>
    /// Opaque subscription handle returned by the <see cref="IMessageBus"/> registration
    /// methods (replacing the deregistration <see cref="Action"/> returned prior to v4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hand the handle back to <see cref="IMessageBus.Deregister{T}"/> with the SAME
    /// <c>T</c> you registered with to undo the registration. It is an opaque value token:
    /// copy it freely, but do NOT inspect or persist its internals. Handles are
    /// process-local, single-thread, and NOT serializable; a handle is meaningless after a
    /// domain reload (its captured generation no longer matches, so deregistration becomes a
    /// silent no-op) and is not portable across <see cref="IMessageBus"/> implementations.
    /// </para>
    /// <para>
    /// The <c>default</c> value is the <see cref="None"/> sentinel; deregistering it is a
    /// no-op. Custom <see cref="IMessageBus"/> implementers that do not wrap
    /// <see cref="MessageBus"/> mint their own handles via the
    /// <see cref="MessageBusRegistration(long, object)"/> constructor and read them back in
    /// their own <see cref="IMessageBus.Deregister{T}"/> override (see <see cref="ExternalId"/>
    /// / <see cref="ExternalState"/>).
    /// </para>
    /// </remarks>
    public readonly struct MessageBusRegistration : IEquatable<MessageBusRegistration>
    {
        /// <summary>
        /// Discriminates which bus store a handle deregisters from. The interceptor stores are
        /// split because all three interceptor registrars log a single
        /// <see cref="RegistrationMethod.Interceptor"/>, so <see cref="method"/> cannot pick
        /// among them; the registering site (which knows the category) stamps the kind.
        /// </summary>
        internal enum Kind : byte
        {
            None = 0,
            Handler,
            UntargetedInterceptor,
            TargetedInterceptor,
            BroadcastInterceptor,
            GlobalAcceptAll,
            External,
        }

        /// <summary><see cref="Kind.None"/> =&gt; an invalid/empty handle; deregistration no-ops.</summary>
        internal readonly Kind kind;

        /// <summary>
        /// Sink/scalar-vs-keyed discriminator AND the <see cref="RegistrationLog"/> row key for
        /// the <see cref="Kind.Handler"/> shape. Unused (default) for the other kinds.
        /// </summary>
        internal readonly RegistrationMethod method;

        /// <summary>The priority the handler/interceptor was registered at.</summary>
        internal readonly int priority;

        /// <summary>The <c>_resetGeneration</c> snapshot (reset/domain-reload guard, all kinds).</summary>
        internal readonly long generation;

        /// <summary>The <c>_globalSlotSweepGeneration</c> snapshot (<see cref="Kind.GlobalAcceptAll"/> only).</summary>
        internal readonly long sweepGeneration;

        /// <summary>
        /// The captured identity anchor for the over-deregistration / stale-after-sweep checks:
        /// the per-type <c>HandlerCache</c> (<see cref="Kind.Handler"/>) or the
        /// <c>InterceptorCache</c> (interceptor kinds). Unused for
        /// <see cref="Kind.GlobalAcceptAll"/> (reads the live global slot) and
        /// <see cref="Kind.External"/>.
        /// </summary>
        internal readonly object capturedPrimary;

        /// <summary>
        /// The keyed (<see cref="Kind.Handler"/> targeted/broadcast) per-type context map captured
        /// by identity; null otherwise.
        /// </summary>
        internal readonly object capturedSecondary;

        /// <summary>
        /// The deregistration payload: the <see cref="MessageHandler"/>
        /// (<see cref="Kind.Handler"/> / <see cref="Kind.GlobalAcceptAll"/>), the interceptor
        /// delegate (interceptor kinds), or the caller's external state
        /// (<see cref="Kind.External"/>).
        /// </summary>
        internal readonly object payload;

        /// <summary>
        /// The keyed (<see cref="Kind.Handler"/> targeted/broadcast) target/source; <c>default</c>
        /// otherwise (so no <c>UnityEngine.Object</c> is pinned for non-keyed kinds).
        /// </summary>
        internal readonly InstanceId context;

        internal MessageBusRegistration(
            Kind kind,
            RegistrationMethod method,
            int priority,
            long generation,
            long sweepGeneration,
            object capturedPrimary,
            object capturedSecondary,
            object payload,
            InstanceId context
        )
        {
            this.kind = kind;
            this.method = method;
            this.priority = priority;
            this.generation = generation;
            this.sweepGeneration = sweepGeneration;
            this.capturedPrimary = capturedPrimary;
            this.capturedSecondary = capturedSecondary;
            this.payload = payload;
            this.context = context;
        }

        /// <summary>
        /// Mints an opaque handle for a custom (non-wrapping) <see cref="IMessageBus"/>
        /// implementer. Pack any state required to deregister into
        /// <paramref name="externalState"/> and an id into <paramref name="externalId"/>; read
        /// them back via <see cref="ExternalId"/> / <see cref="ExternalState"/> in your own
        /// <see cref="IMessageBus.Deregister{T}"/>. The built-in <see cref="MessageBus"/> treats
        /// such a handle as a no-op (it owns no store for it).
        /// </summary>
        public MessageBusRegistration(long externalId, object externalState)
            : this(Kind.External, default, 0, externalId, 0L, null, null, externalState, default)
        { }

        /// <summary>The invalid/empty handle. Deregistering it is a silent no-op.</summary>
        public static readonly MessageBusRegistration None = default;

        /// <summary>True when this handle represents a live registration (not <see cref="None"/>).</summary>
        public bool IsValid => kind != Kind.None;

        /// <summary>The external id supplied to <see cref="MessageBusRegistration(long, object)"/>; 0 for non-external handles.</summary>
        public long ExternalId => kind == Kind.External ? generation : 0L;

        /// <summary>The external state supplied to <see cref="MessageBusRegistration(long, object)"/>; null for non-external handles.</summary>
        public object ExternalState => kind == Kind.External ? payload : null;

        /// <inheritdoc />
        public bool Equals(MessageBusRegistration other) =>
            kind == other.kind
            && method == other.method
            && priority == other.priority
            && generation == other.generation
            && sweepGeneration == other.sweepGeneration
            && ReferenceEquals(capturedPrimary, other.capturedPrimary)
            && ReferenceEquals(capturedSecondary, other.capturedSecondary)
            && ReferenceEquals(payload, other.payload)
            && context.Equals(other.context);

        /// <inheritdoc />
        public override bool Equals(object obj) =>
            obj is MessageBusRegistration other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            // Reference-identity hashes for the captured refs keep parity with the
            // ReferenceEquals comparisons above (ref-equal objects hash identically).
            int hash = (int)kind;
            hash = (hash * 397) ^ (int)method;
            hash = (hash * 397) ^ priority;
            hash = (hash * 397) ^ generation.GetHashCode();
            hash = (hash * 397) ^ sweepGeneration.GetHashCode();
            hash = (hash * 397) ^ (capturedPrimary?.GetHashCode() ?? 0);
            hash = (hash * 397) ^ (capturedSecondary?.GetHashCode() ?? 0);
            hash = (hash * 397) ^ (payload?.GetHashCode() ?? 0);
            hash = (hash * 397) ^ context.GetHashCode();
            return hash;
        }

        public static bool operator ==(MessageBusRegistration left, MessageBusRegistration right) =>
            left.Equals(right);

        public static bool operator !=(MessageBusRegistration left, MessageBusRegistration right) =>
            !left.Equals(right);
    }
}
