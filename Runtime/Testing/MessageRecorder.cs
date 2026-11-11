namespace DxMessaging.Testing
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;

    /// <summary>Creates scoped message recorders for tests using an explicit bus.</summary>
    /// <remarks>
    /// Reference the WallstopStudios.DxMessaging.Testing test support assembly.
    /// Each recorder owns one ordinary subscription and follows its route and interceptor rules.
    /// Setup and collection growth allocate; this helper is intended for assertions, not timing.
    /// </remarks>
    public static class MessageRecorder
    {
        /// <summary>Records untargeted messages delivered by the selected bus.</summary>
        /// <typeparam name="TMessage">The untargeted message type to capture.</typeparam>
        /// <param name="bus">The explicit test bus.</param>
        /// <returns>An active recorder owned by the caller.</returns>
        /// <exception cref="ArgumentNullException">The bus is null.</exception>
        /// <exception cref="MessageRegistrationBuildException">Setup failed and the exception's lease owns retryable cleanup.</exception>
        public static MessageRecorder<TMessage> Untargeted<TMessage>(IMessageBus bus)
            where TMessage : IUntargetedMessage
        {
            return new MessageRecorder<TMessage>(
                bus,
                static (token, handler) => token.RegisterUntargeted(handler)
            );
        }

        /// <summary>Records messages delivered to one target on the selected bus.</summary>
        /// <typeparam name="TMessage">The targeted message type to capture.</typeparam>
        /// <param name="target">The target to observe.</param>
        /// <param name="bus">The explicit test bus.</param>
        /// <returns>An active recorder owned by the caller.</returns>
        /// <exception cref="ArgumentNullException">The bus is null.</exception>
        /// <exception cref="MessageRegistrationBuildException">Setup failed and the exception's lease owns retryable cleanup.</exception>
        public static MessageRecorder<TMessage> Targeted<TMessage>(
            InstanceId target,
            IMessageBus bus
        )
            where TMessage : ITargetedMessage
        {
            return new MessageRecorder<TMessage>(
                bus,
                (token, handler) => token.RegisterTargeted(target, handler)
            );
        }

        /// <summary>Records messages broadcast from one source on the selected bus.</summary>
        /// <typeparam name="TMessage">The broadcast message type to capture.</typeparam>
        /// <param name="source">The source to observe.</param>
        /// <param name="bus">The explicit test bus.</param>
        /// <returns>An active recorder owned by the caller.</returns>
        /// <exception cref="ArgumentNullException">The bus is null.</exception>
        /// <exception cref="MessageRegistrationBuildException">Setup failed and the exception's lease owns retryable cleanup.</exception>
        public static MessageRecorder<TMessage> Broadcast<TMessage>(
            InstanceId source,
            IMessageBus bus
        )
            where TMessage : IBroadcastMessage
        {
            return new MessageRecorder<TMessage>(
                bus,
                (token, handler) => token.RegisterBroadcast(source, handler)
            );
        }
    }

    /// <summary>Owns a test subscription and its captured messages until disposal.</summary>
    /// <typeparam name="TMessage">The message type to capture.</typeparam>
    /// <remarks>
    /// Struct messages are copied; class messages retain their original references.
    /// The recorder observes ordinary handler delivery, without bypassing interceptors or earlier exceptions.
    /// Clear or dispose the recorder to release captures. Like the bus, use it on one thread.
    /// </remarks>
    public sealed class MessageRecorder<TMessage> : IDisposable
        where TMessage : IMessage
    {
        private readonly List<TMessage> _messages = new();
        private readonly ReadOnlyCollection<TMessage> _view;
        private MessageRegistrationLease _lease;
        private bool _disposing;

        /// <summary>Creates the private subscription lease after validating the explicit bus.</summary>
        internal MessageRecorder(
            IMessageBus bus,
            Action<MessageRegistrationToken, MessageHandler.FastHandler<TMessage>> register
        )
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }
            _view = _messages.AsReadOnly();
            MessageRegistrationBuilder builder = new();
            _lease = builder.Build(
                new MessageRegistrationBuildOptions
                {
                    PreferredMessageBus = bus,
                    ActivateOnBuild = true,
                    Configure = token => register(token, Record),
                }
            );
        }

        /// <summary>Gets the live read-only capture view in delivery order.</summary>
        public IReadOnlyList<TMessage> Received => _view;

        /// <summary>Releases captures while preserving the active subscription.</summary>
        public void Clear()
        {
            _messages.Clear();
        }

        /// <summary>Removes the subscription and releases captures after successful cleanup.</summary>
        /// <remarks>
        /// Repeated successful disposal is harmless. If bus cleanup throws, resolve the cause
        /// and call Dispose again on this recorder; it retains its cleanup owner and captures.
        /// </remarks>
        public void Dispose()
        {
            if (_lease == null || _disposing)
            {
                return;
            }
            _disposing = true;
            try
            {
                _lease.Dispose();
                _lease = null;
                _messages.Clear();
            }
            finally
            {
                _disposing = false;
            }
        }

        private void Record(in TMessage message)
        {
            _messages.Add(message);
        }
    }
}
