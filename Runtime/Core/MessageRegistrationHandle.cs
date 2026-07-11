namespace DxMessaging.Core
{
    using System;
    using System.Threading;

    /// <summary>
    /// Opaque identifier for a staged registration within a <see cref="MessageRegistrationToken"/>.
    /// </summary>
    /// <remarks>
    /// Use with <see cref="MessageRegistrationToken.RemoveRegistration"/> to selectively cancel a registration.
    /// </remarks>
    public readonly struct MessageRegistrationHandle
        : IEquatable<MessageRegistrationHandle>,
            IComparable<MessageRegistrationHandle>,
            IComparable
    {
        private static long StaticIdCount;

        private readonly long _id;
        private readonly int _slot;

        internal long Id => _id;

        internal int Slot => _slot;

        internal static long GetCurrentIdSeed()
        {
            return Interlocked.Read(ref StaticIdCount);
        }

        internal static void SetIdSeed(long value)
        {
            _ = Interlocked.Exchange(ref StaticIdCount, value);
        }

        /// <summary>
        /// Creates a new unique handle.
        /// </summary>
        public static MessageRegistrationHandle CreateMessageRegistrationHandle()
        {
            return new MessageRegistrationHandle(Interlocked.Increment(ref StaticIdCount), -1);
        }

        internal static MessageRegistrationHandle CreateMessageRegistrationHandle(int slot)
        {
            return new MessageRegistrationHandle(Interlocked.Increment(ref StaticIdCount), slot);
        }

        internal static MessageRegistrationHandle FromIdentity(long id, int slot)
        {
            return new MessageRegistrationHandle(id, slot);
        }

        private MessageRegistrationHandle(long id, int slot)
        {
            _id = id;
            _slot = slot;
        }

        public static bool operator ==(
            MessageRegistrationHandle left,
            MessageRegistrationHandle right
        )
        {
            return left.Equals(right);
        }

        public static bool operator !=(
            MessageRegistrationHandle left,
            MessageRegistrationHandle right
        )
        {
            return !left.Equals(right);
        }

        /// <summary>
        /// Determines whether the left handle sorts after the right handle.
        /// </summary>
        /// <param name="left">Left-hand handle.</param>
        /// <param name="right">Right-hand handle.</param>
        /// <returns><c>true</c> when <paramref name="left"/> sorts after <paramref name="right"/>.</returns>
        public static bool operator >(
            MessageRegistrationHandle left,
            MessageRegistrationHandle right
        )
        {
            return left.CompareTo(right) > 0;
        }

        /// <summary>
        /// Determines whether the left handle sorts before the right handle.
        /// </summary>
        /// <param name="left">Left-hand handle.</param>
        /// <param name="right">Right-hand handle.</param>
        /// <returns><c>true</c> when <paramref name="left"/> sorts before <paramref name="right"/>.</returns>
        public static bool operator <(
            MessageRegistrationHandle left,
            MessageRegistrationHandle right
        )
        {
            return left.CompareTo(right) < 0;
        }

        public static bool operator <=(
            MessageRegistrationHandle left,
            MessageRegistrationHandle right
        )
        {
            return left.CompareTo(right) <= 0;
        }

        public static bool operator >=(
            MessageRegistrationHandle left,
            MessageRegistrationHandle right
        )
        {
            return left.CompareTo(right) >= 0;
        }

        /// <summary>
        /// Compares this handle with another handle for ordering.
        /// </summary>
        /// <param name="other">Other handle to compare with.</param>
        /// <returns>Relative ordering as defined by <see cref="IComparable{T}.CompareTo(T)"/>.</returns>
        public int CompareTo(MessageRegistrationHandle other)
        {
            return _id.CompareTo(other._id);
        }

        /// <summary>
        /// Compares this handle with an arbitrary object.
        /// </summary>
        /// <param name="obj">Object to compare with.</param>
        /// <returns>
        /// Relative ordering when <paramref name="obj"/> is a <see cref="MessageRegistrationHandle"/>; otherwise <c>-1</c>.
        /// </returns>
        public int CompareTo(object obj)
        {
            if (obj is MessageRegistrationHandle handle)
            {
                return CompareTo(handle);
            }

            return -1;
        }

        /// <summary>
        /// Checks equality against another object.
        /// </summary>
        /// <param name="other">Object to compare.</param>
        /// <returns>
        /// <c>true</c> when <paramref name="other"/> is a <see cref="MessageRegistrationHandle"/> representing the same registration.
        /// </returns>
        public override bool Equals(object other)
        {
            return other is MessageRegistrationHandle handle && Equals(handle);
        }

        /// <summary>
        /// Checks equality against another handle.
        /// </summary>
        /// <param name="other">Handle to compare.</param>
        /// <returns><c>true</c> when both handles represent the same registration.</returns>
        public bool Equals(MessageRegistrationHandle other)
        {
            return _id == other._id;
        }

        /// <summary>
        /// Produces a hash code suitable for dictionary or set lookups.
        /// </summary>
        /// <returns>Hash code derived from the internal identifier.</returns>
        public override int GetHashCode()
        {
            return _id.GetHashCode();
        }

        /// <summary>
        /// Returns a string representation of the handle, including the underlying identifier.
        /// </summary>
        /// <returns>Human-readable representation of the handle.</returns>
        public override string ToString()
        {
            return new { Id = _id }.ToString();
        }
    }
}
