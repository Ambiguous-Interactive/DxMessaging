#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime
{
    using System;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;

    /// <summary>
    /// Save-restore guard for the global message bus
    /// (<see cref="MessageHandler.MessageBus"/>). Captures the current bus on
    /// construction (optionally resetting to the stock configuration) and restores the
    /// captured bus on <see cref="Dispose"/>. Replaces the hand-rolled SetUp/TearDown
    /// capture-restore pairs that were duplicated across global-bus fixtures.
    /// </summary>
    public sealed class GlobalBusScope : IDisposable
    {
        private readonly IMessageBus _capturedBus;
        private bool _disposed;

        private GlobalBusScope(bool reset)
        {
            _capturedBus = MessageHandler.MessageBus;
            if (reset)
            {
                MessageHandler.ResetGlobalMessageBus();
            }
        }

        /// <summary>
        /// Captures the current global bus without modifying it.
        /// </summary>
        public static GlobalBusScope Capture()
        {
            return new GlobalBusScope(reset: false);
        }

        /// <summary>
        /// Captures the current global bus, then resets the global bus to the stock
        /// configuration via <see cref="MessageHandler.ResetGlobalMessageBus"/>.
        /// </summary>
        public static GlobalBusScope CaptureAndReset()
        {
            return new GlobalBusScope(reset: true);
        }

        /// <summary>
        /// Restores the bus captured at construction. Idempotent.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            MessageHandler.SetGlobalMessageBus(_capturedBus);
        }
    }
}
#endif
