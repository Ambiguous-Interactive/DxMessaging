#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime
{
    using System;
    using DxMessaging.Core.MessageBus;

    /// <summary>
    /// Save-restore guard for the global diagnostics statics
    /// (<see cref="IMessageBus.GlobalDiagnosticsTargets"/> and
    /// <see cref="IMessageBus.GlobalMessageBufferSize"/>). Captures both values on
    /// construction, optionally applies overrides, and restores the captured values on
    /// <see cref="Dispose"/>. Replaces the hand-rolled try/finally and SetUp/TearDown
    /// save-restore blocks that were duplicated across diagnostics-sensitive fixtures.
    /// </summary>
    public sealed class DiagnosticsScope : IDisposable
    {
        private readonly DiagnosticsTarget _savedDiagnosticsTargets;
        private readonly int _savedMessageBufferSize;
        private bool _disposed;

        /// <summary>
        /// Captures the current global diagnostics state and applies any non-null
        /// overrides. Values left null are captured but not modified.
        /// </summary>
        public DiagnosticsScope(
            DiagnosticsTarget? diagnosticsTargets = null,
            int? messageBufferSize = null
        )
        {
            _savedDiagnosticsTargets = IMessageBus.GlobalDiagnosticsTargets;
            _savedMessageBufferSize = IMessageBus.GlobalMessageBufferSize;
            if (diagnosticsTargets.HasValue)
            {
                IMessageBus.GlobalDiagnosticsTargets = diagnosticsTargets.Value;
            }

            if (messageBufferSize.HasValue)
            {
                IMessageBus.GlobalMessageBufferSize = messageBufferSize.Value;
            }
        }

        /// <summary>
        /// Restores the diagnostics state captured at construction. Idempotent.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            IMessageBus.GlobalDiagnosticsTargets = _savedDiagnosticsTargets;
            IMessageBus.GlobalMessageBufferSize = _savedMessageBufferSize;
        }
    }
}
#endif
