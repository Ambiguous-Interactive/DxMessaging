#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime
{
    using System;
    using System.Threading;

    /// <summary>Test-only bounded queue for exactly one producer and one consumer.</summary>
    internal sealed class SpscFixedRingControl<T> : IDisposable
    {
        private readonly T[] _items;
        private readonly int _mask;
        private readonly object _maintenance = new();
        private int _head;
        private int _tail;
        private int _activeOperations;
        private int _state; // 0: live, 1: reset, 2: disposed.

        internal SpscFixedRingControl(int capacity)
        {
            if (capacity <= 0 || capacity > 1 << 30 || (capacity & (capacity - 1)) != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }
            _items = new T[capacity];
            _mask = capacity - 1;
        }

        internal int Capacity => _items.Length;

        internal bool TryEnqueue(T item)
        {
            EnterOperation();
            try
            {
                int tail = _tail; // Written only by the producer outside maintenance.
                uint used = unchecked((uint)(tail - Volatile.Read(ref _head)));
                if (used >= (uint)_items.Length)
                {
                    return false;
                }
                _items[tail & _mask] = item;
                Volatile.Write(ref _tail, unchecked(tail + 1));
                return true;
            }
            finally
            {
                ExitOperation();
            }
        }

        internal bool TryDequeue(out T item)
        {
            EnterOperation();
            try
            {
                int head = _head; // Written only by the consumer outside maintenance.
                if (head == Volatile.Read(ref _tail))
                {
                    item = default;
                    return false;
                }
                int slot = head & _mask;
                item = _items[slot];
                _items[slot] = default;
                Volatile.Write(ref _head, unchecked(head + 1));
                return true;
            }
            finally
            {
                ExitOperation();
            }
        }

        internal int Reset()
        {
            lock (_maintenance)
            {
                ThrowIfDisposed();
                Volatile.Write(ref _state, 1);
                WaitForOperations();
                int dropped = unchecked((int)(uint)(_tail - _head));
                Array.Clear(_items, 0, _items.Length);
                _head = 0;
                _tail = 0;
                Volatile.Write(ref _state, 0);
                return dropped;
            }
        }

        public void Dispose()
        {
            lock (_maintenance)
            {
                if (Volatile.Read(ref _state) == 2)
                {
                    return;
                }
                Volatile.Write(ref _state, 2);
                WaitForOperations();
                Array.Clear(_items, 0, _items.Length);
                _head = 0;
                _tail = 0;
            }
        }

        private void EnterOperation()
        {
            RequireLive();
            Interlocked.Increment(ref _activeOperations);
            try
            {
                RequireLive();
            }
            catch
            {
                ExitOperation();
                throw;
            }
        }

        private void ExitOperation() => Interlocked.Decrement(ref _activeOperations);

        private void RequireLive()
        {
            int state = Volatile.Read(ref _state);
            if (state == 2)
            {
                throw new ObjectDisposedException(nameof(SpscFixedRingControl<T>));
            }
            if (state == 1)
            {
                throw new InvalidOperationException("The ring is resetting.");
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _state) == 2)
            {
                throw new ObjectDisposedException(nameof(SpscFixedRingControl<T>));
            }
        }

        private void WaitForOperations()
        {
            SpinWait spin = new();
            while (Volatile.Read(ref _activeOperations) != 0)
            {
                spin.SpinOnce();
            }
        }
    }
}
#endif
