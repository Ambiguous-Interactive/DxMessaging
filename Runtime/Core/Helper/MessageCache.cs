namespace DxMessaging.Core.Helper
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Runtime.CompilerServices;

    /// <summary>
    /// Sparse, type-indexed cache keyed by message type for fast lookups without dictionaries.
    /// </summary>
    /// <remarks>
    /// Internally maintains a sparse array indexed by a compact, per-message-type integer (assigned via
    /// <see cref="MessageHelperIndexer{TMessage}"/>). Used heavily by the bus to store handlers and interceptors
    /// with minimal overhead.
    /// </remarks>
    public sealed class MessageCache<TValue> : IEnumerable<TValue>
        where TValue : class, new()
    {
        /// <summary>
        /// Enumerator over non-null values in the cache.
        /// </summary>
        public struct MessageCacheEnumerator : IEnumerator<TValue>
        {
            private readonly MessageCache<TValue> _cache;

            private int _index;
            private TValue _current;

            internal MessageCacheEnumerator(MessageCache<TValue> cache)
            {
                _cache = cache;
                _index = -1;
                _current = default;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            /// <summary>
            /// Advances the enumerator to the next cached value.
            /// </summary>
            /// <returns><c>true</c> if another non-null value exists; otherwise <c>false</c>.</returns>
            public bool MoveNext()
            {
                TValue[] values = _cache._values;
                int count = _cache._count;
                while (++_index < count)
                {
                    _current = values[_index];
                    if (_current != null)
                    {
                        return true;
                    }
                }

                _current = default;
                return false;
            }

            public TValue Current => _current;

            object IEnumerator.Current => Current;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            /// <summary>
            /// Resets the enumerator to the position before the first element.
            /// </summary>
            public void Reset()
            {
                _index = -1;
                _current = default;
            }

            /// <summary>
            /// Releases resources held by the enumerator.
            /// </summary>
            public void Dispose() { }
        }

        private TValue[] _values = Array.Empty<TValue>();
        private int _count;

        /// <summary>
        /// Retrieves the value associated with <typeparamref name="TMessage"/>, creating one if needed.
        /// </summary>
        /// <typeparam name="TMessage">Message type key.</typeparam>
        /// <returns>Existing or newly created value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TValue GetOrAdd<TMessage>()
            where TMessage : IMessage
        {
            TValue value;
            int index = MessageHelperIndexer<TMessage>.SequentialId;
            if (0 <= index)
            {
                EnsureIndex(index);
                value = _values[index];
                if (value != null)
                {
                    return value;
                }

                value = new TValue();
                _values[index] = value;
            }
            else
            {
                index = AssignIndex<TMessage>();
                value = new TValue();
                _values[index] = value;
            }

            return value;
        }

        /// <summary>
        /// Sets the value for the given <typeparamref name="TMessage"/> key.
        /// </summary>
        /// <typeparam name="TMessage">Message type key.</typeparam>
        /// <param name="value">Value to store.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set<TMessage>(TValue value)
            where TMessage : IMessage
        {
            int index = MessageHelperIndexer<TMessage>.SequentialId;
            if (0 <= index)
            {
                EnsureIndex(index);
                _values[index] = value;
                return;
            }

            index = AssignIndex<TMessage>();
            _values[index] = value;
        }

        /// <summary>
        /// Attempts to get the value for the given <typeparamref name="TMessage"/> key.
        /// </summary>
        /// <typeparam name="TMessage">Message type key.</typeparam>
        /// <param name="value">Out parameter receiving the value if present.</param>
        /// <returns>True if a non-null value was present.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetValue<TMessage>(out TValue value)
            where TMessage : IMessage
        {
            int index = MessageHelperIndexer<TMessage>.SequentialId;
            if (0 <= index && index < _values.Length)
            {
                value = _values[index];
                return value != null;
            }

            value = default;
            return false;
        }

        /// <summary>
        /// Attempts to get the value at an already-resolved message type index.
        /// </summary>
        /// <param name="index">Index previously assigned by <see cref="MessageHelperIndexer{TMessage}"/>.</param>
        /// <param name="value">Out parameter receiving the value if present.</param>
        /// <returns>True if a non-null value was present.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryGetValueAtIndex(int index, out TValue value)
        {
            if (0 <= index && index < _values.Length)
            {
                value = _values[index];
                return value != null;
            }

            value = default;
            return false;
        }

        /// <summary>
        /// Removes the value for the given <typeparamref name="TMessage"/> key.
        /// </summary>
        /// <typeparam name="TMessage">Message type key.</typeparam>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Remove<TMessage>()
            where TMessage : IMessage
        {
            int index = MessageHelperIndexer<TMessage>.SequentialId;
            if (0 <= index && index < _values.Length)
            {
                _values[index] = null;
            }
        }

        /// <summary>
        /// Removes the value at an already-resolved message type index.
        /// </summary>
        /// <param name="index">Index previously assigned by <see cref="MessageHelperIndexer{TMessage}"/>.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void RemoveAtIndex(int index)
        {
            if (0 <= index && index < _values.Length)
            {
                _values[index] = null;
            }
        }

        /// <summary>
        /// Returns an enumerator iterating over non-null entries in insertion order.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public MessageCacheEnumerator GetEnumerator()
        {
            return new MessageCacheEnumerator(this);
        }

        IEnumerator<TValue> IEnumerable<TValue>.GetEnumerator()
        {
            return GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Clear()
        {
            _values = Array.Empty<TValue>();
            _count = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int AssignIndex<TMessage>()
            where TMessage : IMessage
        {
            int index = MessageHelperIndexer.TotalMessages;
            EnsureIndex(index);
            MessageHelperIndexer<TMessage>.SequentialId = index;
            MessageHelperIndexer.TotalMessages = index + 1;
            return index;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureIndex(int index)
        {
            if (index < 0)
            {
                throw new InvalidOperationException("Message type indices cannot be negative.");
            }

            if (index < _values.Length)
            {
                if (_count <= index)
                {
                    _count = index + 1;
                }
                return;
            }

            int capacity = _values.Length == 0 ? 4 : _values.Length;
            while (capacity <= index)
            {
                if (capacity > int.MaxValue >> 1)
                {
                    throw new InvalidOperationException(
                        "The message type index exceeds the cache's supported capacity."
                    );
                }
                capacity <<= 1;
            }

            Array.Resize(ref _values, capacity);
            _count = index + 1;
        }
    }
}
