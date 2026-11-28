namespace DxMessaging.Core.MessageBus
{
    using System;
    using Helper;
    using Messages;

    public sealed partial class MessageBus
    {
        private StickyStore _stickyValues;

        /// <inheritdoc />
        public void EmitSticky<TMessage>(ref TMessage message)
            where TMessage : IUntargetedMessage
        {
            StickyStore values = _stickyValues ??= new StickyStore();
            values.GetOrAdd<TMessage>().Value = message;
            UntargetedBroadcast(ref message);
        }

        /// <inheritdoc />
        public bool TryGetLatest<TMessage>(out TMessage message)
            where TMessage : IUntargetedMessage
        {
            if (_stickyValues != null && _stickyValues.TryGet<TMessage>(out message))
            {
                return true;
            }
            message = default;
            return false;
        }

        /// <inheritdoc />
        public bool ClearLatest<TMessage>()
            where TMessage : IUntargetedMessage
        {
            if (_stickyValues == null || !_stickyValues.TryGet<TMessage>(out TMessage _))
            {
                return false;
            }
            if (_stickyValues.Remove<TMessage>())
            {
                _stickyValues = null;
            }
            return true;
        }

        /// <summary>
        /// Sparse per-message-type storage for the latest sticky value.
        /// </summary>
        /// <remarks>
        /// Uses the same sequential type index as <see cref="MessageCache{TValue}"/>, so the
        /// warmed publication path is an array read instead of a dictionary lookup. Unlike the
        /// bus's idle handler caches, published sticky values are user state and must survive
        /// <see cref="MessageBus.Trim"/>; the store releases its array as soon as the last value
        /// is cleared, and <see cref="ResetState"/> drops it whole.
        /// </remarks>
        private sealed class StickyStore
        {
            private static readonly object[] Empty = Array.Empty<object>();

            private object[] _values = Empty;
            private int _count;

            internal StickyValue<TMessage> GetOrAdd<TMessage>()
                where TMessage : IUntargetedMessage
            {
                int index = MessageHelperIndexer<TMessage>.SequentialId;
                object[] values = _values;
                if (index < 0)
                {
                    index = MessageHelperIndexer.TotalMessages;
                    if (int.MaxValue <= index)
                    {
                        throw new InvalidOperationException("The message type index exceeds the store's supported capacity.");
                    }
                    // Publish the shared index only after growth succeeds, so a rejected
                    // capacity cannot consume a type ID.
                    EnsureIndex(index);
                    values = _values;
                    MessageHelperIndexer<TMessage>.SequentialId = index;
                    MessageHelperIndexer.TotalMessages = index + 1;
                }
                else if (index >= values.Length)
                {
                    EnsureIndex(index);
                    values = _values;
                }
                if (values[index] is StickyValue<TMessage> existing)
                {
                    return existing;
                }
                StickyValue<TMessage> created = new StickyValue<TMessage>();
                values[index] = created;
                ++_count;
                return created;
            }

            internal bool TryGet<TMessage>(out TMessage message)
                where TMessage : IUntargetedMessage
            {
                int index = MessageHelperIndexer<TMessage>.SequentialId;
                if (0 <= index && index < _values.Length && _values[index] is StickyValue<TMessage> stored)
                {
                    message = stored.Value;
                    return true;
                }
                message = default;
                return false;
            }

            /// <returns><see langword="true"/> when the last value was removed.</returns>
            internal bool Remove<TMessage>()
                where TMessage : IUntargetedMessage
            {
                int index = MessageHelperIndexer<TMessage>.SequentialId;
                if (0 <= index && index < _values.Length && _values[index] != null)
                {
                    _values[index] = null;
                    if (--_count <= 0)
                    {
                        _values = Empty;
                        _count = 0;
                        return true;
                    }
                }
                return false;
            }

            private void EnsureIndex(int index)
            {
                int capacity = _values.Length == 0 ? 4 : _values.Length;
                while (capacity <= index)
                {
                    if (int.MaxValue >> 1 < capacity)
                    {
                        throw new InvalidOperationException("The message type index exceeds the store's supported capacity.");
                    }
                    capacity <<= 1;
                }
                Array.Resize(ref _values, capacity);
            }

            internal sealed class StickyValue<TMessage>
                where TMessage : IUntargetedMessage
            {
                internal TMessage Value;
            }
        }
    }
}
