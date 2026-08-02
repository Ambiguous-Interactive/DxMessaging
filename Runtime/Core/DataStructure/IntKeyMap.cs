namespace DxMessaging.Core.DataStructure
{
    using System;
    using System.Runtime.CompilerServices;

    /// <summary>
    /// Compact open-addressed map specialized for integer keys and reference values.
    /// </summary>
    /// <remarks>
    /// Uses power-of-two storage, linear probing, and backward-shift deletion. The
    /// specialized shape avoids storing the managed reference carried by a Unity
    /// <see cref="InstanceId"/> in every routing entry. It also avoids tombstones,
    /// so repeated registration churn cannot lengthen unsuccessful probe chains.
    /// </remarks>
    internal class IntKeyMap<TValue>
        where TValue : class
    {
        public struct ValueEnumerator
        {
            private readonly IntKeyMap<TValue> _map;
            private int _index;

            internal ValueEnumerator(IntKeyMap<TValue> map)
            {
                _map = map;
                _index = -1;
                Current = null;
            }

            public TValue Current { get; private set; }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool MoveNext()
            {
                TValue[] values = _map._values;
                while (++_index < values.Length)
                {
                    TValue value = values[_index];
                    if (value != null)
                    {
                        Current = value;
                        return true;
                    }
                }

                Current = null;
                return false;
            }
        }

        private const int InitialCapacity = 4;
        private const int MaximumCapacity = 1 << 30;
        private const uint HashMultiplierOne = 0x7FEB352Du;
        private const uint HashMultiplierTwo = 0x846CA68Bu;

        private int[] _keys = Array.Empty<int>();
        private TValue[] _values = Array.Empty<TValue>();

        internal int Count { get; private set; }

        internal int Capacity => _values.Length;

        internal TValue this[int key]
        {
            set => Set(key, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryGetValue(int key, out TValue value)
        {
            TValue[] values = _values;
            if (values.Length != 0)
            {
                int mask = values.Length - 1;
                int index = Bucket(key, mask);
                while (values[index] != null)
                {
                    if (_keys[index] == key)
                    {
                        value = values[index];
                        return true;
                    }

                    index = (index + 1) & mask;
                }
            }

            value = null;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Set(int key, TValue value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            if (_values.Length == 0)
            {
                Resize(InitialCapacity);
            }

            int mask = _values.Length - 1;
            int index = Bucket(key, mask);
            while (_values[index] != null)
            {
                if (_keys[index] == key)
                {
                    _values[index] = value;
                    return;
                }

                index = (index + 1) & mask;
            }

            if (Count + 1 > LoadLimit(_values.Length))
            {
                Resize(NextCapacity(_values.Length));
                mask = _values.Length - 1;
                index = Bucket(key, mask);
                while (_values[index] != null)
                {
                    index = (index + 1) & mask;
                }
            }

            _keys[index] = key;
            _values[index] = value;
            Count++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool Remove(int key)
        {
            TValue[] values = _values;
            if (values.Length == 0)
            {
                return false;
            }

            int mask = values.Length - 1;
            int gap = Bucket(key, mask);
            while (values[gap] != null)
            {
                if (_keys[gap] == key)
                {
                    DeleteAt(gap, mask);
                    Count--;
                    return true;
                }

                gap = (gap + 1) & mask;
            }

            return false;
        }

        internal void Clear()
        {
            if (Count == 0)
            {
                return;
            }

            Array.Clear(_values, 0, _values.Length);
            Count = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ValueEnumerator GetEnumerator()
        {
            return new ValueEnumerator(this);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DeleteAt(int gap, int mask)
        {
            int scan = (gap + 1) & mask;
            while (_values[scan] != null)
            {
                int home = Bucket(_keys[scan], mask);
                if (((gap - home) & mask) < ((scan - home) & mask))
                {
                    _keys[gap] = _keys[scan];
                    _values[gap] = _values[scan];
                    gap = scan;
                }

                scan = (scan + 1) & mask;
            }

            _keys[gap] = 0;
            _values[gap] = null;
        }

        private void Resize(int capacity)
        {
            int[] keys = new int[capacity];
            TValue[] values = new TValue[capacity];
            int mask = capacity - 1;

            for (int source = 0; source < _values.Length; ++source)
            {
                TValue value = _values[source];
                if (value == null)
                {
                    continue;
                }

                int key = _keys[source];
                int target = Bucket(key, mask);
                while (values[target] != null)
                {
                    target = (target + 1) & mask;
                }

                keys[target] = key;
                values[target] = value;
            }

            _keys = keys;
            _values = values;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int Bucket(int key, int mask)
        {
            unchecked
            {
                uint hash = (uint)key;
                hash ^= hash >> 16;
                hash *= HashMultiplierOne;
                hash ^= hash >> 15;
                hash *= HashMultiplierTwo;
                hash ^= hash >> 16;
                return (int)(hash & (uint)mask);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int LoadLimit(int capacity)
        {
            return capacity - (capacity >> 2);
        }

        private static int NextCapacity(int capacity)
        {
            if (capacity >= MaximumCapacity)
            {
                throw new InvalidOperationException(
                    "The integer map reached its maximum capacity."
                );
            }

            return capacity << 1;
        }
    }
}
