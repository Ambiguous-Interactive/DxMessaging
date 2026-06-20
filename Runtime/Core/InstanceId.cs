namespace DxMessaging.Core
{
    using System;
    using System.Runtime.CompilerServices;
    using System.Runtime.Serialization;

    /// <summary>
    /// Lightweight wrapper around Unity instance IDs to uniquely identify objects.
    /// </summary>
    /// <remarks>
    /// In Unity builds, this struct also carries a reference to the underlying <see cref="UnityEngine.Object"/>.
    /// It supports implicit conversions from <see cref="UnityEngine.GameObject"/> and <see cref="UnityEngine.Component"/>,
    /// and provides value semantics for hashing, equality, and ordering. Outside Unity, it stores only the integer ID.
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
        public static readonly InstanceId EmptyId = new(0);

        [DataMember(Name = "id")]
        public int Id => _id;

        private readonly int _id;

#if UNITY_2021_3_OR_NEWER
        // ReSharper disable once InconsistentNaming
        public readonly UnityEngine.Object Object;
#endif

        /// <summary>
        /// Creates an identifier that wraps the provided Unity instance ID.
        /// </summary>
        /// <param name="id">Unity instance ID to wrap.</param>
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        /// <summary>
        /// Converts a <see cref="UnityEngine.GameObject"/> reference into an <see cref="InstanceId"/>.
        /// </summary>
        /// <param name="gameObject">GameObject to wrap.</param>
        /// <returns>Instance identifier representing <paramref name="gameObject"/>.</returns>
        public static implicit operator InstanceId(UnityEngine.GameObject gameObject)
        {
            return new InstanceId(gameObject);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        /// <summary>
        /// Converts a <see cref="UnityEngine.Component"/> reference into an <see cref="InstanceId"/>.
        /// </summary>
        /// <param name="component">Component to wrap.</param>
        /// <returns>Instance identifier representing <paramref name="component"/>.</returns>
        public static implicit operator InstanceId(UnityEngine.Component component)
        {
            return new InstanceId(component);
        }
#endif

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        /// <summary>
        /// Checks for equality with another <see cref="InstanceId"/>.
        /// </summary>
        /// <param name="other">Other instance identifier.</param>
        /// <returns><c>true</c> when both identifiers refer to the same Unity instance.</returns>
        public bool Equals(InstanceId other)
        {
            return _id == other._id;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        /// <summary>
        /// Checks for equality with an arbitrary object.
        /// </summary>
        /// <param name="other">Object to compare.</param>
        /// <returns>
        /// <c>true</c> when <paramref name="other"/> is an <see cref="InstanceId"/> representing the same Unity instance.
        /// </returns>
        public override bool Equals(object other)
        {
            return other is InstanceId id && Equals(id);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        /// <summary>
        /// Gets a hash code suitable for dictionary or set lookups.
        /// </summary>
        /// <returns>Hash code derived from the underlying Unity instance ID.</returns>
        public override int GetHashCode()
        {
            return _id;
        }

        /// <summary>
        /// Returns a string representation that includes the Unity instance ID and, when available, the object name.
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        /// <summary>
        /// Determines whether two identifiers refer to the same Unity instance.
        /// </summary>
        /// <param name="lhs">Left-hand identifier.</param>
        /// <param name="rhs">Right-hand identifier.</param>
        /// <returns><c>true</c> when both identifiers represent the same value.</returns>
        public static bool operator ==(InstanceId lhs, InstanceId rhs)
        {
            return lhs._id == rhs._id;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        /// <summary>
        /// Determines whether two identifiers refer to different Unity instances.
        /// </summary>
        /// <param name="lhs">Left-hand identifier.</param>
        /// <param name="rhs">Right-hand identifier.</param>
        /// <returns><c>true</c> when the identifiers represent different values.</returns>
        public static bool operator !=(InstanceId lhs, InstanceId rhs)
        {
            return lhs._id != rhs._id;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        /// <summary>
        /// Compares two identifiers for ascending ordering.
        /// </summary>
        /// <param name="lhs">Left-hand identifier.</param>
        /// <param name="rhs">Right-hand identifier.</param>
        /// <returns><c>true</c> when <paramref name="lhs"/> precedes <paramref name="rhs"/>.</returns>
        public static bool operator <(InstanceId lhs, InstanceId rhs)
        {
            return lhs._id.CompareTo(rhs._id) < 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        /// <summary>
        /// Compares two identifiers for ascending ordering with equality.
        /// </summary>
        /// <param name="lhs">Left-hand identifier.</param>
        /// <param name="rhs">Right-hand identifier.</param>
        /// <returns>
        /// <c>true</c> when <paramref name="lhs"/> precedes <paramref name="rhs"/> or both identifiers are equal.
        /// </returns>
        public static bool operator <=(InstanceId lhs, InstanceId rhs)
        {
            return lhs._id.CompareTo(rhs._id) <= 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        /// <summary>
        /// Compares two identifiers for descending ordering.
        /// </summary>
        /// <param name="lhs">Left-hand identifier.</param>
        /// <param name="rhs">Right-hand identifier.</param>
        /// <returns><c>true</c> when <paramref name="lhs"/> follows <paramref name="rhs"/>.</returns>
        public static bool operator >(InstanceId lhs, InstanceId rhs)
        {
            return lhs._id.CompareTo(rhs._id) > 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        /// <summary>
        /// Compares two identifiers for descending ordering with equality.
        /// </summary>
        /// <param name="lhs">Left-hand identifier.</param>
        /// <param name="rhs">Right-hand identifier.</param>
        /// <returns>
        /// <c>true</c> when <paramref name="lhs"/> follows <paramref name="rhs"/> or both identifiers are equal.
        /// </returns>
        public static bool operator >=(InstanceId lhs, InstanceId rhs)
        {
            return lhs._id.CompareTo(rhs._id) >= 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        /// <summary>
        /// Compares this identifier with another identifier for ordering.
        /// </summary>
        /// <param name="other">Identifier to compare with.</param>
        /// <returns>Relative ordering as defined by <see cref="IComparable{T}.CompareTo(T)"/>.</returns>
        public int CompareTo(InstanceId other)
        {
            return _id.CompareTo(other._id);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        /// <summary>
        /// Compares this identifier with an arbitrary object for ordering.
        /// </summary>
        /// <param name="rhs">Object to compare with.</param>
        /// <returns>
        /// Relative ordering when <paramref name="rhs"/> is an <see cref="InstanceId"/>; otherwise <c>-1</c>.
        /// </returns>
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
