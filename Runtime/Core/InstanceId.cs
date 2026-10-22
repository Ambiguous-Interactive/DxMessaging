namespace DxMessaging.Core
{
    using System;
    using System.Runtime.CompilerServices;
    using System.Runtime.Serialization;

    /// <summary>
    /// Numeric identity for objects and message routing.
    /// </summary>
    /// <remarks>
    /// In Unity builds, this struct also carries a reference to the underlying <c>UnityEngine.Object</c>.
    /// It supports implicit conversions from <c>UnityEngine.GameObject</c> and <c>UnityEngine.Component</c>,
    /// and provides value semantics for hashing, equality, and ordering based only on the integer ID.
    /// Constructing from an integer does not find or attach a Unity object. Caller-defined IDs share the same
    /// numeric identity space as Unity IDs. Outside Unity, this struct stores only the integer ID.
    /// </remarks>
    /// <example>
    /// <code>
    /// // Unity: send a targeted message to a GameObject
    /// var msg = new DxMessaging.Core.Messages.StringMessage("Hello");
    /// msg.EmitGameObjectTargeted(gameObject); // implicit conversion from GameObject handled by extension overload
    /// </code>
    /// </example>
    [Serializable]
    [DataContract]
    public readonly struct InstanceId : IComparable, IComparable<InstanceId>, IEquatable<InstanceId>
    {
        /// <summary>Identifier with numeric value zero and no associated Unity object.</summary>
        public static readonly InstanceId EmptyId = new(0);

        /// <summary>Gets the numeric identifier used for equality, hashing and routing.</summary>
        [DataMember(Name = "id")]
        public int Id => _id;

        private readonly int _id;

#if UNITY_2021_3_OR_NEWER
        // ReSharper disable once InconsistentNaming
        /// <summary>Stored Unity object reference, when this identifier was created from one.</summary>
        /// <remarks>This reference does not participate in equality or hashing.</remarks>
        public readonly UnityEngine.Object Object;
#endif

        /// <summary>
        /// Creates an identifier from a Unity instance ID or a caller-defined numeric identity.
        /// </summary>
        /// <param name="id">Numeric identity to wrap.</param>
        public InstanceId(int id)
        {
            _id = id;
#if UNITY_2021_3_OR_NEWER
            Object = null;
#endif
        }

#if UNITY_2021_3_OR_NEWER
        private InstanceId(UnityEngine.Object unityObject)
        {
            _id = StableId(unityObject);
            Object = unityObject;
        }

        /// <summary>
        /// Returns the stable per-object integer identity used as the dispatch key.
        /// Unity 6 is migrating object identity to the 64-bit <c>EntityId</c>:
        /// <c>GetInstanceID()</c> is deprecated and becomes a compile error in Unity 6.5,
        /// and successive raw accessors (the <c>EntityId</c>-to-<c>int</c> cast, then
        /// <c>GetRawData()</c>) have each been deprecated in turn. On Unity 6.4+ this reads
        /// the non-deprecated <c>EntityId.ToULong(...)</c> and keeps its low 32 bits --
        /// exactly the integer the legacy <c>GetInstanceID()</c> returned (verified on 6.4
        /// across GameObject/Component/ScriptableObject). Older Unity keeps
        /// <c>GetInstanceID()</c> (valid, warning-only there, never the 6.5 error). The
        /// 32-bit dispatch key is identical either way; only the (non-deprecated) source
        /// differs. The gate is the host-verified 6.4 rather than 6.2 because the exact
        /// accessor that is non-deprecated has shifted across Unity 6 minors.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int StableId(UnityEngine.Object unityObject)
        {
#if UNITY_6000_4_OR_NEWER
            return unchecked((int)UnityEngine.EntityId.ToULong(unityObject.GetEntityId()));
#else
            return unityObject.GetInstanceID();
#endif
        }

        /// <summary>
        /// Converts a <c>UnityEngine.GameObject</c> reference into an <see cref="InstanceId"/>.
        /// </summary>
        /// <param name="gameObject">GameObject to wrap.</param>
        /// <returns>Instance identifier representing <paramref name="gameObject"/>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator InstanceId(UnityEngine.GameObject gameObject)
        {
            return new InstanceId(gameObject);
        }

        /// <summary>
        /// Converts a <c>UnityEngine.Component</c> reference into an <see cref="InstanceId"/>.
        /// </summary>
        /// <param name="component">Component to wrap.</param>
        /// <returns>Instance identifier representing <paramref name="component"/>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator InstanceId(UnityEngine.Component component)
        {
            return new InstanceId(component);
        }
#endif

        /// <summary>
        /// Checks for equality with another <see cref="InstanceId"/>.
        /// </summary>
        /// <param name="other">Other instance identifier.</param>
        /// <returns><c>true</c> when both identifiers have the same numeric identity.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(InstanceId other)
        {
            return _id == other._id;
        }

        /// <summary>
        /// Checks for equality with an arbitrary object.
        /// </summary>
        /// <param name="other">Object to compare.</param>
        /// <returns>
        /// <c>true</c> when <paramref name="other"/> is an <see cref="InstanceId"/> with the same numeric identity.
        /// </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override bool Equals(object other)
        {
            return other is InstanceId id && Equals(id);
        }

        /// <summary>
        /// Gets a hash code suitable for dictionary or set lookups.
        /// </summary>
        /// <returns>Hash code derived from the wrapped numeric identifier.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override int GetHashCode()
        {
            return _id;
        }

        /// <summary>
        /// Returns a string representation that includes the numeric identifier and, when available, the Unity object name.
        /// </summary>
        /// <returns>Human-readable description of the identifier.</returns>
        public override string ToString()
        {
#if UNITY_2021_3_OR_NEWER
            UnityEngine.Object instance = Object;
            string objectName = instance == null ? string.Empty : instance.name;
            return new { Id = _id, Name = objectName }.ToString();
#else
            return new { Id = _id }.ToString();
#endif
        }

        /// <summary>
        /// Determines whether two identifiers have the same numeric identity.
        /// </summary>
        /// <param name="lhs">Left-hand identifier.</param>
        /// <param name="rhs">Right-hand identifier.</param>
        /// <returns><c>true</c> when both identifiers represent the same value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(InstanceId lhs, InstanceId rhs)
        {
            return lhs._id == rhs._id;
        }

        /// <summary>
        /// Determines whether two identifiers have different numeric identities.
        /// </summary>
        /// <param name="lhs">Left-hand identifier.</param>
        /// <param name="rhs">Right-hand identifier.</param>
        /// <returns><c>true</c> when the identifiers represent different values.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(InstanceId lhs, InstanceId rhs)
        {
            return lhs._id != rhs._id;
        }

        /// <summary>
        /// Compares two identifiers for ascending ordering.
        /// </summary>
        /// <param name="lhs">Left-hand identifier.</param>
        /// <param name="rhs">Right-hand identifier.</param>
        /// <returns><c>true</c> when <paramref name="lhs"/> precedes <paramref name="rhs"/>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator <(InstanceId lhs, InstanceId rhs)
        {
            return lhs._id.CompareTo(rhs._id) < 0;
        }

        /// <summary>
        /// Compares two identifiers for ascending ordering with equality.
        /// </summary>
        /// <param name="lhs">Left-hand identifier.</param>
        /// <param name="rhs">Right-hand identifier.</param>
        /// <returns>
        /// <c>true</c> when <paramref name="lhs"/> precedes <paramref name="rhs"/> or both identifiers are equal.
        /// </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator <=(InstanceId lhs, InstanceId rhs)
        {
            return lhs._id.CompareTo(rhs._id) <= 0;
        }

        /// <summary>
        /// Compares two identifiers for descending ordering.
        /// </summary>
        /// <param name="lhs">Left-hand identifier.</param>
        /// <param name="rhs">Right-hand identifier.</param>
        /// <returns><c>true</c> when <paramref name="lhs"/> follows <paramref name="rhs"/>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator >(InstanceId lhs, InstanceId rhs)
        {
            return 0 < lhs._id.CompareTo(rhs._id);
        }

        /// <summary>
        /// Compares two identifiers for descending ordering with equality.
        /// </summary>
        /// <param name="lhs">Left-hand identifier.</param>
        /// <param name="rhs">Right-hand identifier.</param>
        /// <returns>
        /// <c>true</c> when <paramref name="lhs"/> follows <paramref name="rhs"/> or both identifiers are equal.
        /// </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator >=(InstanceId lhs, InstanceId rhs)
        {
            return 0 <= lhs._id.CompareTo(rhs._id);
        }

        /// <summary>
        /// Compares this identifier with another identifier for ordering.
        /// </summary>
        /// <param name="other">Identifier to compare with.</param>
        /// <returns>Relative ordering as defined by <see cref="IComparable{T}.CompareTo(T)"/>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int CompareTo(InstanceId other)
        {
            return _id.CompareTo(other._id);
        }

        /// <summary>
        /// Compares this identifier with an arbitrary object for ordering.
        /// </summary>
        /// <param name="rhs">Object to compare with.</param>
        /// <returns>
        /// Relative ordering when <paramref name="rhs"/> is an <see cref="InstanceId"/>; otherwise <c>-1</c>.
        /// </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int CompareTo(object rhs)
        {
            if (rhs is InstanceId other)
            {
                return CompareTo(other);
            }
            return -1;
        }
    }
}
