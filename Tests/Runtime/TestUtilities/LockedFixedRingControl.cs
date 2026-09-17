#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime
{
    using System;

    /// <summary>Test-only bounded queue control with one monitor per operation.</summary>
    internal sealed class LockedFixedRingControl<T> : IDisposable
    {
        private readonly object _gate = new();
        private readonly T[] _items;
        private int _head;
        private int _tail;
        private int _count;
        private bool _disposed;

        internal LockedFixedRingControl(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }
            _items = new T[capacity];
        }

        internal int Capacity => _items.Length;

        internal int Count
        {
            get
            {
                lock (_gate)
                {
                    ThrowIfDisposed();
                    return _count;
                }
            }
        }

        internal bool TryEnqueue(T item)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_count == _items.Length)
                {
                    return false;
                }
                _items[_tail] = item;
                _tail = Next(_tail);
                ++_count;
                return true;
            }
        }

        internal bool TryDequeue(out T item)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_count == 0)
                {
                    item = default;
                    return false;
                }
                item = _items[_head];
                _items[_head] = default;
                _head = Next(_head);
                --_count;
                return true;
            }
        }

        internal int Reset()
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                int dropped = _count;
                Array.Clear(_items, 0, _items.Length);
                _head = 0;
                _tail = 0;
                _count = 0;
                return dropped;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
                Array.Clear(_items, 0, _items.Length);
                _head = 0;
                _tail = 0;
                _count = 0;
                _disposed = true;
            }
        }

        private int Next(int index) => index + 1 == _items.Length ? 0 : index + 1;

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(LockedFixedRingControl<T>));
            }
        }
    }
}
#endif
