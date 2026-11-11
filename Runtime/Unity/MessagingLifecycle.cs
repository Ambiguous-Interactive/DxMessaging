#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Unity
{
    using System;
    using DxMessaging.Core;
    using UnityEngine;

    /// <summary>Owns a listener token across explicit Unity lifecycle forwarding.</summary>
    /// <remarks>
    /// Create with MessagingComponent.CreateLifecycleBound in Awake. Stage handlers on Token,
    /// forward OnEnable to Enable and OnDisable to Disable, and dispose from OnDestroy.
    /// This class does not receive Unity callbacks automatically. Dispose can throw when a bus
    /// rejects cleanup. Retry Dispose while IsDisposed is false; accepted reentrant work can also
    /// leave cleanup pending without an exception. Methods use the bus's single-thread contract.
    /// </remarks>
    public sealed class MessagingLifecycle : IDisposable
    {
        private MessagingComponent _component;
        private MonoBehaviour _listener;
        private MessageRegistrationToken _token;
        private bool _disposing;

        /// <summary>Captures the exact token validated and retained by the component factory.</summary>
        internal MessagingLifecycle(
            MessagingComponent component,
            MonoBehaviour listener,
            MessageRegistrationToken token
        )
        {
            _component = component;
            _listener = listener;
            _token = token;
        }

        /// <summary>Gets the captured token for handler configuration until final cleanup succeeds.</summary>
        /// <exception cref="ObjectDisposedException">Final disposal has completed.</exception>
        public MessageRegistrationToken Token =>
            _token ?? throw new ObjectDisposedException(nameof(MessagingLifecycle));

        /// <summary>Gets whether final cleanup completed and released the retained owner references.</summary>
        public bool IsDisposed => _token == null;

        /// <summary>Activates the captured token while its original listener and cache binding remain live.</summary>
        /// <exception cref="ObjectDisposedException">The lifecycle or its Unity owner has been destroyed.</exception>
        /// <exception cref="InvalidOperationException">The component released or replaced the captured token.</exception>
        public void Enable()
        {
            MessageRegistrationToken token = Token;
            if (_component == null || _listener == null)
            {
                throw new ObjectDisposedException(nameof(MessagingLifecycle));
            }
            if (
                !_component.TryGetToken(_listener, out MessageRegistrationToken current)
                || !ReferenceEquals(current, token)
            )
            {
                throw new InvalidOperationException(
                    "The lifecycle token was released or replaced. Create a new lifecycle for this listener."
                );
            }
            token.Enable();
        }

        /// <summary>Deactivates the captured token while preserving its staged registrations.</summary>
        /// <remarks>Safe after successful disposal. Failed bus cleanup remains retryable through this owner.</remarks>
        public void Disable()
        {
            _token?.Disable();
        }

        /// <summary>Releases only the captured token and retains this owner while cleanup remains pending.</summary>
        /// <remarks>
        /// A stale lifecycle never releases a replacement token. Nested disposal returns to the
        /// outer cleanup call. Retry after a bus exception or when IsDisposed remains false.
        /// Successful disposal releases token, listener, and component references.
        /// </remarks>
        public void Dispose()
        {
            MessageRegistrationToken token = _token;
            if (token == null || _disposing)
            {
                return;
            }
            _disposing = true;
            try
            {
                bool ownsCachedToken =
                    _component._registeredListeners.TryGetValue(
                        _listener,
                        out MessageRegistrationToken current
                    ) && ReferenceEquals(current, token);
                if (ownsCachedToken)
                {
                    _component.Release(_listener);
                }
                else
                {
                    token.Dispose();
                }
                if (
                    !token.Enabled
                    && token._metadata.Count == 0
                    && (
                        !_component._registeredListeners.TryGetValue(_listener, out current)
                        || !ReferenceEquals(current, token)
                    )
                )
                {
                    _token = null;
                    _listener = null;
                    _component = null;
                }
            }
            finally
            {
                _disposing = false;
            }
        }
    }
}
#endif
