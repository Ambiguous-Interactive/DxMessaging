namespace DxMessaging.Core
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.ExceptionServices;
    using MessageBus;

    public sealed partial class MessageRegistrationToken
    {
        private List<MessageRegistrationHandle> _sourceMoves;
        private long _sourceMoveCancellationVersion;

        /// <summary>Moves one source-bound broadcast handler or post-processor while keeping its handle.</summary>
        /// <param name="handle">Handle returned by a source-bound registration on this token.</param>
        /// <param name="newSource">Source to accept on subsequent emissions and lifecycle replays.</param>
        /// <returns>True for a retained source-bound registration; false for an unsupported, stale, foreign, or removed handle.</returns>
        /// <exception cref="InvalidOperationException">The same registration is moving, being accepted or cleaned up, or retains overlapping cleanup.</exception>
        /// <remarks>
        /// Added in 4.0.0. A disabled registration changes staging only. An active registration moves
        /// on its current bus. Priority, state, handle and diagnostic counts survive; a move joins the
        /// end of the new source's equal-priority registration order. In-flight callbacks retain their
        /// original source. A same-source move has no effect.
        /// Failed old-source cleanup leaves the source unchanged and retryable. Failed activation
        /// restores old staging and attempts old-source recovery; two failures produce an
        /// AggregateException. Reentrant disable/removal takes precedence over activation. Cleanup
        /// accepted after a reentrant lifecycle cancellation remains under this handle; finish it
        /// through Disable, RemoveRegistration or an owning group before enabling again. A move
        /// during this handle's cleanup or with overlapping retry cleanup is rejected before staging changes.
        /// </remarks>
        public bool RetargetSource(MessageRegistrationHandle handle, InstanceId newSource)
        {
            if (
                !TryGetSlot(handle.Slot, handle.Id, out RegistrationSlot slot)
                || !(slot.Registration is ISourceRegistration source)
                || !source.SupportsSourceRetargeting
            )
            {
                return false;
            }
            InstanceId previousSource = source.Source;
            if (previousSource == newSource)
            {
                return true;
            }
            Registration registration = slot.Registration;
            if (
                registration.IsRegistering
                || IsCleanupInFlight(handle)
                || slot.Deregistration is PendingDeregistration
                || (_sourceMoves != null && _sourceMoves.Contains(handle))
            )
            {
                throw new InvalidOperationException(
                    "Finish the current registration, source move or overlapping cleanup before moving the same handle again."
                );
            }
            List<MessageRegistrationHandle> moves = _sourceMoves ??=
                new List<MessageRegistrationHandle>();
            moves.Add(handle);
            long cancellationVersion = _sourceMoveCancellationVersion;
            bool active = slot.Deregistration != null;
            IMessageBus activeMessageBus = active ? source.ActiveMessageBus : null;
            try
            {
                if (active)
                {
                    if (activeMessageBus == null)
                    {
                        throw new InvalidOperationException(
                            "The active source registration has no accepted bus binding."
                        );
                    }
                    Exception cleanupFailure = InvokeOwnedDeregistration(
                        handle,
                        slot.Deregistration,
                        0,
                        isOrphan: false
                    );
                    if (cleanupFailure != null)
                    {
                        _enabled = 0 < _deregistrationCount;
                        ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
                    }
                    if (
                        !TryGetSlot(handle.Slot, handle.Id, out slot)
                        || !ReferenceEquals(slot.Registration, registration)
                    )
                    {
                        return false;
                    }
                    if (slot.Deregistration != null)
                    {
                        _enabled = true;
                        throw new InvalidOperationException(
                            "Cleanup accepted another registration under this handle. Finish token or handle cleanup before retrying the source move."
                        );
                    }
                }
                source.Source = newSource;
                if (!active || cancellationVersion != _sourceMoveCancellationVersion)
                {
                    return true;
                }
                try
                {
                    MessageHandler.HandlerDeregistration cleanup = registration.Register(
                        activeMessageBus
                    );
                    AddDeregistration(handle.Slot, handle.Id, cleanup);
                }
                catch (Exception activationFailure)
                {
                    source.Source = previousSource;
                    Exception recoveryFailure = null;
                    if (
                        cancellationVersion == _sourceMoveCancellationVersion
                        && TryGetSlot(handle.Slot, handle.Id, out slot)
                        && ReferenceEquals(slot.Registration, registration)
                        && slot.Deregistration == null
                    )
                    {
                        try
                        {
                            AddDeregistration(
                                handle.Slot,
                                handle.Id,
                                registration.Register(activeMessageBus)
                            );
                        }
                        catch (Exception exception)
                        {
                            recoveryFailure = exception;
                        }
                    }
                    _enabled = 0 < _deregistrationCount;
                    if (recoveryFailure != null)
                    {
                        throw new AggregateException(
                            "Source activation and old-source recovery both failed. Finish token cleanup and retry Enable after the bus recovers.",
                            activationFailure,
                            recoveryFailure
                        );
                    }
                    ExceptionDispatchInfo.Capture(activationFailure).Throw();
                }
                if (cancellationVersion != _sourceMoveCancellationVersion)
                {
                    _enabled = 0 < _deregistrationCount;
                }
                return TryGetSlot(handle.Slot, handle.Id, out slot)
                    && ReferenceEquals(slot.Registration, registration);
            }
            finally
            {
                moves.RemoveAt(moves.Count - 1);
            }
        }

#if UNITY_2021_3_OR_NEWER
        /// <summary>Moves a source-bound broadcast registration to a GameObject's identity.</summary>
        /// <param name="handle">Handle returned by a source-bound registration on this token.</param>
        /// <param name="newSource">New source, converted through InstanceId. Null selects the empty identity.</param>
        /// <returns>True when source-bound staging remains; false for an unsupported, stale, foreign, or removed handle.</returns>
        /// <remarks>Added in 4.0.0. Uses the same cleanup, reentrancy and recovery rules as the InstanceId overload.</remarks>
        public bool RetargetSource(
            MessageRegistrationHandle handle,
            UnityEngine.GameObject newSource
        )
        {
            return RetargetSource(
                handle,
                newSource == null ? InstanceId.EmptyId : (InstanceId)newSource
            );
        }
#endif

        private void CancelSourceMoves()
        {
            if (_sourceMoves != null && 0 < _sourceMoves.Count)
            {
                _sourceMoveCancellationVersion = unchecked(_sourceMoveCancellationVersion + 1);
            }
        }

        private interface ISourceRegistration
        {
            bool SupportsSourceRetargeting { get; }
            IMessageBus ActiveMessageBus { get; }
            InstanceId Source { get; set; }
        }
    }
}
