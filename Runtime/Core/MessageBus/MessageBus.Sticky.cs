namespace DxMessaging.Core.MessageBus
{
    using System;
    using System.Collections.Generic;
    using Messages;

    public sealed partial class MessageBus
    {
        private Dictionary<Type, object> _stickyValues;

        /// <inheritdoc />
        public void EmitSticky<TMessage>(ref TMessage message)
            where TMessage : IUntargetedMessage
        {
            Dictionary<Type, object> values = _stickyValues ??= new Dictionary<Type, object>();
            if (!values.TryGetValue(typeof(TMessage), out object stored))
            {
                stored = new StickyValue<TMessage>();
                values.Add(typeof(TMessage), stored);
            }
            ((StickyValue<TMessage>)stored).Value = message;
            UntargetedBroadcast(ref message);
        }

        /// <inheritdoc />
        public bool TryGetLatest<TMessage>(out TMessage message)
            where TMessage : IUntargetedMessage
        {
            if (
                _stickyValues != null
                && _stickyValues.TryGetValue(typeof(TMessage), out object stored)
            )
            {
                message = ((StickyValue<TMessage>)stored).Value;
                return true;
            }
            message = default;
            return false;
        }

        /// <inheritdoc />
        public bool ClearLatest<TMessage>()
            where TMessage : IUntargetedMessage
        {
            if (_stickyValues == null || !_stickyValues.Remove(typeof(TMessage)))
            {
                return false;
            }
            if (_stickyValues.Count == 0)
            {
                _stickyValues = null;
            }
            return true;
        }

        private sealed class StickyValue<TMessage>
            where TMessage : IUntargetedMessage
        {
            internal TMessage Value;
        }
    }
}
