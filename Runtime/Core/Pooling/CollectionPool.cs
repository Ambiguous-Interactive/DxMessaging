namespace DxMessaging.Core.Pooling
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Single-threaded object pool with bounded capacity and either LRU or LIFO
    /// recycle order. Used by <see cref="DxPools"/> to recycle the dictionaries,
    /// lists, sets, and stacks that DxMessaging slots rent and return on slot
    /// reset. Not thread-safe by design -- the entire dispatch path is single-
    /// threaded.
    /// </summary>
    /// <remarks>
    /// Adapted from https://github.com/wallstop/unity-helpers/blob/de22dcd22fd98d4fe8c7aa8e70814496698681f7/Runtime/Utils/Buffers.cs
    /// (single-threaded portion only -- the unity-helpers version supports
    /// thread-static caches we do not need here).
    /// </remarks>
    internal sealed class CollectionPool<T>
        where T : class, new()
    {
        private readonly Func<T> _factory;
        private readonly Action<T> _onRecycled; // called once when a returned entry is accepted into the pool
        private readonly Action<T> _onEvicted; // called when an entry is dropped (cap overflow or trim)
        private bool _useLru;
        private int _maxRetained;

        // LRU state -- Queue<T> is FIFO with O(1) Enqueue/Dequeue (amortized)
        // and avoids the per-node allocation that LinkedList incurs.
        private readonly Queue<T> _lruQueue;
        private readonly HashSet<T> _lruMembership;

        // LIFO state
        private readonly Stack<T> _stack;
        private readonly HashSet<T> _stackMembership;

        private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;

        private long _hits;
        private long _misses;
        private long _evictions;

        public CollectionPool(
            int maxRetained,
            bool useLru,
            Func<T> factory,
            Action<T> onRecycled = null,
            Action<T> onEvicted = null
        )
        {
            if (maxRetained < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxRetained));
            }
            _maxRetained = maxRetained;
            _useLru = useLru;
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _onRecycled = onRecycled;
            _onEvicted = onEvicted;
            int initialCapacity = Math.Min(maxRetained, 32);
            _lruQueue = new Queue<T>(initialCapacity);
            _lruMembership = new HashSet<T>();
            _stack = new Stack<T>(initialCapacity);
            _stackMembership = new HashSet<T>();
        }

        /// <summary>Current cached count.</summary>
        public int Count => _useLru ? _lruQueue.Count : _stack.Count;

        /// <summary>Whether this pool evicts oldest returned entries first.</summary>
        public bool UseLru
        {
            get => _useLru;
            set
            {
                AssertOwnerThread();
                if (_useLru == value)
                {
                    return;
                }
                if (value)
                {
                    ConvertStackToLru();
                }
                else
                {
                    ConvertLruToStack();
                }
                _useLru = value;
            }
        }

        /// <summary>Soft cap on retained entries. Mutating this may evict.</summary>
        public int MaxRetained
        {
            get => _maxRetained;
            set
            {
                AssertOwnerThread();
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }
                _maxRetained = value;
                EvictDownTo(value);
            }
        }

        public T Rent()
        {
            AssertOwnerThread();
            if (_useLru)
            {
                if (_lruQueue.Count > 0)
                {
                    T pooled = _lruQueue.Dequeue();
                    _lruMembership.Remove(pooled);
                    _hits++;
                    return pooled;
                }
            }
            else if (_stack.Count > 0)
            {
                T pooled = _stack.Pop();
                _stackMembership.Remove(pooled);
                _hits++;
                return pooled;
            }
            _misses++;
            T fresh = _factory();
            if (fresh == null)
            {
                throw new InvalidOperationException("CollectionPool factory returned null.");
            }
            return fresh;
        }

        /// <summary>
        /// Rents a value and transfers its ownership to <paramref name="owner"/>.
        /// If the dictionary insertion fails, returns the value to this pool
        /// before propagating the exception.
        /// </summary>
        public T RentAndAdd<TKey>(Dictionary<TKey, T> owner, TKey key)
        {
            if (owner == null)
            {
                throw new ArgumentNullException(nameof(owner));
            }

            T value = Rent();
            try
            {
                owner.Add(key, value);
                return value;
            }
            catch
            {
                Return(value);
                throw;
            }
        }

        public void Return(T value)
        {
            AssertOwnerThread();
            if (value == null)
            {
                return;
            }
            if (_maxRetained == 0)
            {
                _evictions++;
                _onEvicted?.Invoke(value);
                return;
            }
            if (_useLru)
            {
                if (_lruMembership.Contains(value))
                {
                    return; // already pooled; ignore double-return
                }
                if (_lruQueue.Count >= _maxRetained)
                {
                    T head = _lruQueue.Dequeue();
                    _lruMembership.Remove(head);
                    _evictions++;
                    _onEvicted?.Invoke(head);
                }
                _onRecycled?.Invoke(value);
                _lruQueue.Enqueue(value);
                _lruMembership.Add(value);
            }
            else
            {
                if (_stackMembership.Contains(value))
                {
                    return; // already pooled; ignore double-return
                }
                if (_stack.Count >= _maxRetained)
                {
                    _evictions++;
                    _onEvicted?.Invoke(value);
                    return;
                }
                _onRecycled?.Invoke(value);
                _stack.Push(value);
                _stackMembership.Add(value);
            }
        }

        /// <summary>Trim the pool to <paramref name="targetSize"/>. Returns count evicted.</summary>
        public int Trim(int targetSize)
        {
            AssertOwnerThread();
            if (targetSize < 0)
            {
                targetSize = 0;
            }
            return EvictDownTo(targetSize);
        }

        public CollectionPoolDiagnostics Snapshot()
        {
            return new CollectionPoolDiagnostics(Count, _hits, _misses, _evictions);
        }

        private int EvictDownTo(int targetSize)
        {
            int evicted = 0;
            if (_useLru)
            {
                while (_lruQueue.Count > targetSize)
                {
                    T head = _lruQueue.Dequeue();
                    _lruMembership.Remove(head);
                    _evictions++;
                    evicted++;
                    _onEvicted?.Invoke(head);
                }
            }
            else
            {
                while (_stack.Count > targetSize)
                {
                    T item = _stack.Pop();
                    _stackMembership.Remove(item);
                    _evictions++;
                    evicted++;
                    _onEvicted?.Invoke(item);
                }
            }
            return evicted;
        }

        private void ConvertLruToStack()
        {
            while (_lruQueue.Count > 0)
            {
                T item = _lruQueue.Dequeue();
                _lruMembership.Remove(item);
                _stack.Push(item);
                _stackMembership.Add(item);
            }
        }

        private void ConvertStackToLru()
        {
            T[] items = _stack.ToArray();
            _stack.Clear();
            _stackMembership.Clear();
            for (int index = items.Length - 1; index >= 0; index--)
            {
                T item = items[index];
                _lruQueue.Enqueue(item);
                _lruMembership.Add(item);
            }
        }

        [System.Diagnostics.Conditional("DEBUG")]
        private void AssertOwnerThread()
        {
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                throw new InvalidOperationException(
                    "CollectionPool<"
                        + typeof(T).Name
                        + "> is single-threaded; accessed from foreign thread."
                );
            }
        }
    }
}
