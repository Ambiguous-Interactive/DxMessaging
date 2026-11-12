namespace DxMessaging.Core
{
    using System;
    using Extensions;
    using MessageBus;
    using Messages;

    public sealed partial class MessageRegistrationToken
    {
        // SYNC: .docs-tests/DocsSnippetCompiler.cs mirrors the public replay overload signatures.
        private StickyReplayContext _stickyReplay;

        /// <summary>Stages a callback with optional current-state replay after activation.</summary>
        /// <typeparam name="TMessage">Untargeted state message type.</typeparam>
        /// <param name="untargetedHandler">Callback receiving a copied message.</param>
        /// <param name="replayLatest">Replay the accepted bus's retained value once per activation.</param>
        /// <param name="priority">Normal delivery order; lower values run first.</param>
        /// <returns>A token-owned handle for removal or group enrollment.</returns>
        /// <remarks>
        /// Added in 4.0.0. Replay follows subscription acceptance and cleanup ownership. It invokes
        /// only this callback, bypassing the delivery pipeline while respecting MessageHandler.active.
        /// Disabled staging waits for Enable;
        /// Disable/Enable replays again. A throwing immediate replay removes its new registration;
        /// activation failure rolls back new cleanup. Failed cleanup remains token-owned and
        /// retryable through Disable or Dispose. Replay requires IStickyMessageBus at activation.
        /// Active bus rebinding from inside replay is rejected before mutation; defer it until the
        /// callback returns, or select PreserveRegistrations for future registrations.
        /// </remarks>
        public MessageRegistrationHandle RegisterUntargeted<TMessage>(
            Action<TMessage> untargetedHandler,
            bool replayLatest,
            int priority = 0
        )
            where TMessage : IUntargetedMessage
        {
            return replayLatest
                ? InternalRegister(
                    new StickyUntargetedRegistration<TMessage>(
                        this,
                        RegistrationKind.UntargetedHandlerAction,
                        untargetedHandler,
                        priority
                    )
                )
                : RegisterUntargeted(untargetedHandler, priority);
        }

        /// <summary>Stages a readonly-reference callback with optional current-state replay.</summary>
        /// <typeparam name="TMessage">Untargeted state message type.</typeparam>
        /// <param name="untargetedHandler">Callback receiving the message by readonly reference.</param>
        /// <param name="replayLatest">Replay retained state once per activation.</param>
        /// <param name="priority">Normal delivery order; lower values run first.</param>
        /// <returns>A token-owned handle for removal or group enrollment.</returns>
        /// <remarks>Uses the same replay, ownership and exception contract as the Action overload.</remarks>
        public MessageRegistrationHandle RegisterUntargeted<TMessage>(
            MessageHandler.FastHandler<TMessage> untargetedHandler,
            bool replayLatest,
            int priority = 0
        )
            where TMessage : IUntargetedMessage
        {
            return replayLatest
                ? InternalRegister(
                    new StickyUntargetedRegistration<TMessage>(
                        this,
                        RegistrationKind.UntargetedHandlerFast,
                        untargetedHandler,
                        priority
                    )
                )
                : RegisterUntargeted(untargetedHandler, priority);
        }

        /// <summary>Stages an explicit-state callback with optional current-state replay.</summary>
        /// <typeparam name="TMessage">Untargeted state message type.</typeparam>
        /// <typeparam name="TState">Caller state, copied for structs and shared for references.</typeparam>
        /// <param name="state">State supplied to each callback, including replay.</param>
        /// <param name="handler">Callback. Prefer a static callback to avoid captures.</param>
        /// <param name="replayLatest">Replay retained state once per activation.</param>
        /// <param name="priority">Normal delivery order; lower values run first.</param>
        /// <returns>A token-owned handle for removal or group enrollment.</returns>
        /// <remarks>Uses the same replay, ownership and exception contract as the Action overload.</remarks>
        public MessageRegistrationHandle RegisterUntargeted<TMessage, TState>(
            TState state,
            StateHandler<TMessage, TState> handler,
            bool replayLatest,
            int priority = 0
        )
            where TMessage : IUntargetedMessage
        {
            StateBinding<TMessage, TState> binding = new StateBinding<TMessage, TState>(
                state,
                handler
            );
            return RegisterUntargeted<TMessage>(binding.Invoke, replayLatest, priority);
        }

        private void ReplayLatestIfOwned(
            Registration registration,
            MessageRegistrationHandle handle,
            MessageHandler.HandlerDeregistration cleanup
        )
        {
            if (!(registration is IStickyReplay replay) || !CanReplayLatest(registration, handle))
            {
                return;
            }
            StickyReplayContext context = _stickyReplay ??= new StickyReplayContext();
            ++context.Depth;
            registration.BeginOwnedReplay();
            try
            {
                replay.ReplayLatest(cleanup.CapturedMessageBus);
            }
            finally
            {
                registration.EndOwnedReplay();
                --context.Depth;
            }
        }

        private bool CanReplayLatest(Registration registration, MessageRegistrationHandle handle)
        {
            return _messageHandler.active
                && TryGetSlot(handle.Slot, handle.Id, out RegistrationSlot slot)
                && ReferenceEquals(slot.Registration, registration)
                && slot.Deregistration != null;
        }

        private void CancelStickyReplay()
        {
            if (_stickyReplay != null && 0 < _stickyReplay.Depth)
            {
                _stickyReplay.CancellationVersion = unchecked(
                    _stickyReplay.CancellationVersion + 1
                );
            }
        }

        private interface IStickyReplay
        {
            void ReplayLatest(IMessageBus acceptedBus);
        }

        private sealed class StickyReplayContext
        {
            internal int Depth;
            internal long CancellationVersion;
        }

        private sealed class StickyUntargetedRegistration<TMessage>
            : UntargetedRegistration<TMessage>,
                IStickyReplay
            where TMessage : IUntargetedMessage
        {
            internal StickyUntargetedRegistration(
                MessageRegistrationToken token,
                RegistrationKind kind,
                object userHandler,
                int priority
            )
                : base(token, kind, userHandler, priority) { }

            protected override MessageHandler.HandlerDeregistration RegisterCore(
                IMessageBus messageBusOverride
            )
            {
                StickyMessageExtensions.RequireStickyBus(
                    ResolveRegistrationMessageBus(messageBusOverride)
                        ?? Token._messageHandler.DefaultMessageBus
                );
                return base.RegisterCore(messageBusOverride);
            }

            public void ReplayLatest(IMessageBus acceptedBus)
            {
                IStickyMessageBus bus = StickyMessageExtensions.RequireStickyBus(acceptedBus);
                long cancellationVersion = Token._stickyReplay.CancellationVersion;
                if (
                    bus.TryGetLatest(out TMessage message)
                    && cancellationVersion == Token._stickyReplay.CancellationVersion
                    && Token.CanReplayLatest(this, Handle)
                )
                {
                    InvokeRetained(in message, acceptedBus);
                }
            }
        }
    }
}
