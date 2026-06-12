namespace DxMessaging.Core.DataStructure
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using Extensions;

    /// <summary>
    /// Fixed-capacity ring buffer used for lightweight diagnostics/history.
    /// </summary>
    /// <remarks>
    /// Maintains the most recent <see cref="Capacity"/> items, overwriting the oldest entries as new items are added.
    /// Provides efficient iteration in chronological order and supports simple remove/resize operations.
    /// </remarks>
    [Serializable]
    internal sealed class CyclicBuffer<T> : IReadOnlyList<T>
    {
        internal struct CyclicBufferEnumerator : IEnumerator<T>
        {
            private readonly CyclicBuffer<T> _buffer;

            private int _index;
            private T _current;

            internal CyclicBufferEnumerator(CyclicBuffer<T> buffer)
            {
                _buffer = buffer;
                _index = -1;
                _current = default;
            }

            /// <summary>
            /// Advances the enumerator to the next element in chronological order.
            /// </summary>
            /// <returns><c>true</c> when another element is available; otherwise <c>false</c>.</returns>
            public bool MoveNext()
            {
                if (++_index < _buffer.Count)
                {
                    _current = _buffer._buffer[_buffer.AdjustedIndexFor(_index)];
                    return true;
                }

                _current = default;
                return false;
            }

            /// <summary>
            /// Gets the element at the current enumerator position.
            /// </summary>
            public T Current => _current;

            object IEnumerator.Current => Current;

            /// <summary>
            /// Resets the enumerator to its initial position before the first element.
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

        /// <summary>Maximum number of elements retained in the buffer.</summary>
        public int Capacity { get; private set; }

        /// <summary>Current number of elements stored (<= <see cref="Capacity"/>).</summary>
        public int Count { get; private set; }

        private readonly List<T> _buffer;
        private readonly List<T> _cache;
        private int _position;

        internal int ScratchCacheCount => _cache.Count;

        /// <summary>
        /// Accesses the element at the specified chronological index (0 = oldest).
        /// </summary>
        public T this[int index]
        {
            get
            {
                BoundsCheck(index);
                return _buffer[AdjustedIndexFor(index)];
            }
            set
            {
                BoundsCheck(index);
                _buffer[AdjustedIndexFor(index)] = value;
            }
        }

        /// <summary>
        /// Creates a ring buffer with the given capacity and optional initial contents.
        /// </summary>
        /// <param name="capacity">Maximum number of elements to retain.</param>
        /// <param name="initialContents">Items to seed the buffer with (truncated to capacity).</param>
        public CyclicBuffer(int capacity, IEnumerable<T> initialContents = null)
        {
            if (capacity < 0)
            {
                throw new ArgumentException(nameof(capacity));
            }

            Capacity = capacity;
            _position = 0;
            Count = 0;
            _buffer = new List<T>();
            _cache = new List<T>();
            if (initialContents != null)
            {
                foreach (T item in initialContents)
                {
                    Add(item);
                }
            }
        }

        /// <summary>
        /// Creates an enumerator that iterates from the oldest element to the most recently added.
        /// </summary>
        public CyclicBufferEnumerator GetEnumerator()
        {
            return new CyclicBufferEnumerator(this);
        }

        IEnumerator<T> IEnumerable<T>.GetEnumerator()
        {
            return GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        /// <summary>
        /// Adds an item, overwriting the oldest element when the buffer is full.
        /// </summary>
        public void Add(T item)
        {
            if (Capacity == 0)
            {
                return;
            }

            if (_position < _buffer.Count)
            {
                _buffer[_position] = item;
            }
            else
            {
                _buffer.Add(item);
            }

            _position = (_position + 1) % Capacity;
            if (Count < Capacity)
            {
                ++Count;
            }
        }

        /// <summary>
        /// Removes the first occurrence of the specified element.
        /// </summary>
        /// <param name="element">Element to remove.</param>
        /// <param name="comparer">Optional equality comparer; defaults to <see cref="EqualityComparer{T}.Default"/>.</param>
        /// <returns>True if an element was removed.</returns>
        public bool Remove(T element, IEqualityComparer<T> comparer = null)
        {
            if (Count == 0)
            {
                return false;
            }

            comparer ??= EqualityComparer<T>.Default;

            _cache.Clear();
            try
            {
                for (int i = 0; i < Count; ++i)
                {
                    _cache.Add(_buffer[AdjustedIndexFor(i)]);
                }

                // Find and remove the element
                bool removed = false;
                for (int i = 0; i < _cache.Count; ++i)
                {
                    if (comparer.Equals(_cache[i], element))
                    {
                        _cache.RemoveAt(i);
                        removed = true;
                        break;
                    }
                }

                if (!removed)
                {
                    return false;
                }

                RebuildFromCache();
                return true;
            }
            finally
            {
                _cache.Clear();
            }
        }

        /// <summary>
        /// Removes all elements that match the given predicate.
        /// </summary>
        /// <param name="predicate">Function returning true for items to remove.</param>
        /// <returns>Number of elements removed.</returns>
        public int RemoveAll(Predicate<T> predicate)
        {
            if (Count == 0)
            {
                return 0;
            }

            _cache.Clear();
            try
            {
                for (int i = 0; i < Count; ++i)
                {
                    _cache.Add(_buffer[AdjustedIndexFor(i)]);
                }

                int removedCount = _cache.RemoveAll(predicate);
                if (removedCount == 0)
                {
                    return 0;
                }

                RebuildFromCache();
                return removedCount;
            }
            finally
            {
                _cache.Clear();
            }
        }

        private void RebuildFromCache()
        {
            _buffer.Clear();
            _buffer.AddRange(_cache);
            Count = _cache.Count;
            _position = Count < Capacity ? Count : 0;
            _cache.Clear();
        }

        /// <summary>
        /// Clears the buffer (Count becomes 0).
        /// </summary>
        public void Clear()
        {
            Count = 0;
            _position = 0;
            _buffer.Clear();
            _cache.Clear();
        }

        /// <summary>
        /// Changes the capacity, truncating or expanding storage accordingly.
        /// </summary>
        /// <param name="newCapacity">New maximum number of elements.</param>
        public void Resize(int newCapacity)
        {
            if (newCapacity == Capacity)
            {
                return;
            }

            if (newCapacity < 0)
            {
                throw new ArgumentException(nameof(newCapacity));
            }

            Capacity = newCapacity;

            // Normalize underlying storage so the oldest element is at index 0.
            _buffer.Shift(-_position);

            if (newCapacity < _buffer.Count)
            {
                // When shrinking, drop the oldest elements to retain the most recent window.
                int removeCount = _buffer.Count - newCapacity;
                _buffer.RemoveRange(0, removeCount);
            }

            // Update next-write position: if full, wrap to 0 to overwrite oldest; otherwise append at end.
            if (Capacity <= 0)
            {
                _position = 0;
                Count = 0;
                _buffer.Clear();
                return;
            }

            // Count cannot exceed new capacity
            Count = Math.Min(newCapacity, Count);
            _position = _buffer.Count >= Capacity ? 0 : _buffer.Count;
        }

        /// <summary>
        /// Returns true if the buffer currently contains the specified item.
        /// </summary>
        public bool Contains(T item)
        {
            return _buffer.Contains(item);
        }

        private int AdjustedIndexFor(int index)
        {
            long longCapacity = Capacity;
            if (longCapacity == 0L)
            {
                return 0;
            }
            unchecked
            {
                int adjustedIndex = (int)(
                    (_position - 1L + longCapacity - (_buffer.Count - 1 - index)) % longCapacity
                );
                return adjustedIndex;
            }
        }

        private void BoundsCheck(int index)
        {
            if (!InBounds(index))
            {
                throw new IndexOutOfRangeException($"{index} is outside of bounds [0, {Count})");
            }
        }

        private bool InBounds(int index)
        {
            return 0 <= index && index < Count;
        }
    }
}
