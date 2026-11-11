namespace DxMessaging.Core
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.ExceptionServices;

    /// <summary>Owns a selected set of registrations from one token.</summary>
    /// <remarks>
    /// Added in 4.0.0. Add returned handles with <see cref="Add"/>. Disposing the group removes those registrations
    /// without disabling the token or removing its other registrations. The group follows the token's
    /// single-threaded contract. Group creation and enrollment allocate; dispatch is unchanged.
    /// </remarks>
    public sealed class MessageRegistrationGroup : IDisposable
    {
        private MessageRegistrationToken _token;
        private readonly List<MessageRegistrationHandle> _handles =
            new List<MessageRegistrationHandle>();
        private bool _closed;
        private bool _disposing;

        internal MessageRegistrationGroup(MessageRegistrationToken token)
        {
            _token = token;
        }

        /// <summary>Gets the number of enrolled handles still awaiting successful group cleanup.</summary>
        /// <remarks>Removal through the token is reflected when the group next disposes.</remarks>
        public int Count => _handles.Count;

        /// <summary>Enrolls an existing registration belonging to the group's token.</summary>
        /// <param name="handle">Handle returned by a registration on the group's token.</param>
        /// <returns>The same handle, for assignment at the enrollment call site.</returns>
        /// <exception cref="ArgumentException">The handle has no staged registration or pending cleanup on this token.</exception>
        /// <exception cref="ObjectDisposedException">Disposal has started, including a disposal that still needs cleanup retries.</exception>
        /// <remarks>
        /// Duplicate enrollment has no effect. A handle can belong to more than one group; removing
        /// it from either group cancels the registration. Other groups then hold a harmless stale handle.
        /// </remarks>
        public MessageRegistrationHandle Add(MessageRegistrationHandle handle)
        {
            if (_closed)
            {
                throw new ObjectDisposedException(nameof(MessageRegistrationGroup));
            }
            if (!_token.OwnsRegistration(handle))
            {
                throw new ArgumentException(
                    "Register the handle on this group's token before adding it.",
                    nameof(handle)
                );
            }
            _token.TrackGroupRegistration(handle);
            if (!_handles.Contains(handle))
            {
                _handles.Add(handle);
            }
            return handle;
        }

        /// <summary>Removes every enrolled registration, preserving failed removals for a later retry.</summary>
        /// <remarks>
        /// Cleanup visits every member even if another member fails. A single failure is rethrown;
        /// multiple failures are reported in an <see cref="AggregateException"/>. Call Dispose again
        /// after the cause is resolved. Successful members are never retried. Nested disposal defers
        /// to the current call. Cleanup accepted by a callback under an enrolled handle remains for
        /// the next pass, as it does with token cleanup. Check <see cref="Count"/> for remaining
        /// obligations. After successful cleanup, repeated disposal has no effect.
        /// </remarks>
        public void Dispose()
        {
            if (_disposing || _token == null)
            {
                return;
            }
            _closed = true;
            _disposing = true;
            Exception firstFailure = null;
            List<Exception> failures = null;
            int remaining = 0;
            try
            {
                for (int i = 0; i < _handles.Count; ++i)
                {
                    MessageRegistrationHandle handle = _handles[i];
                    try
                    {
                        _token.RemoveRegistration(handle);
                        if (_token.OwnsRegistration(handle))
                        {
                            _handles[remaining++] = handle;
                        }
                    }
                    catch (Exception exception)
                    {
                        _handles[remaining++] = handle;
                        if (firstFailure == null)
                        {
                            firstFailure = exception;
                        }
                        else
                        {
                            if (failures == null)
                            {
                                failures = new List<Exception> { firstFailure };
                            }
                            failures.Add(exception);
                        }
                    }
                }
                _handles.RemoveRange(remaining, _handles.Count - remaining);
                if (remaining == 0)
                {
                    _token = null;
                }
            }
            finally
            {
                _disposing = false;
            }
            if (failures != null)
            {
                throw new AggregateException(
                    "Some group registrations could not be removed. Retry Dispose after resolving the cleanup failures.",
                    failures
                );
            }
            if (firstFailure != null)
            {
                ExceptionDispatchInfo.Capture(firstFailure).Throw();
            }
        }
    }
}
