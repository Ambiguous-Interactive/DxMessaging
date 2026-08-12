namespace DxMessaging.Core
{
    using System;
    using Configuration;
    using MessageBus;

    /// <summary>
    /// Centralised utility for resetting DxMessaging static state when Domain Reload is disabled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This class is designed for Unity's Enter Play Mode Settings with Domain Reload disabled.
    /// When Domain Reload is disabled, static fields persist between play mode sessions, which
    /// can cause issues if stale state is not cleared.
    /// </para>
    /// <para>
    /// <strong>Important:</strong> Message type sequential IDs (managed by MessageHelperIndexer)
    /// and opaque registration handle IDs are intentionally NOT reset. Once an ID is assigned, it
    /// remains consumed for the lifetime of the application domain. This prevents stale handles or
    /// new message types from colliding with identities issued before a reset.
    /// </para>
    /// </remarks>
    public static class DxMessagingStaticState
    {
        private static readonly object ResetLock = new object();
        private static readonly BaselineState Baseline;

        static DxMessagingStaticState()
        {
            Baseline = CaptureBaseline();
        }

        /// <summary>
        /// Resets all static variables in DxMessaging to their default values.
        /// </summary>
        /// <remarks>
        /// Message type and registration handle IDs are NOT reset by this method. See the class
        /// remarks for details.
        /// </remarks>
        public static void Reset()
        {
            lock (ResetLock)
            {
                MessagingDebug.enabled = Baseline.MessagingDebugEnabled;
                MessagingDebug.LogFunction = Baseline.MessagingDebugLogFunction;

                IMessageBus.GlobalDiagnosticsTargets = Baseline.GlobalDiagnosticsTargets;
                IMessageBus.GlobalMessageBufferSize = Baseline.GlobalMessageBufferSize;
                IMessageBus.GlobalSequentialIndex = Baseline.GlobalSequentialIndex;
                DxMessagingRuntimeSettingsProvider.ResetForTests();

                MessageRegistrationBuilder.SetSyntheticOwnerCounter(Baseline.SyntheticOwnerCounter);

                // Capture the active global bus before ResetStatics swaps it back to the default
                // instance. If a user installed a custom global bus via SetGlobalMessageBus, we
                // also bump that bus's reset generation so deregister closures captured against
                // it (e.g. a deferred Object.Destroy that lands after Reset) silently no-op
                // instead of logging spurious over-deregistration errors. We deliberately do NOT
                // call ResetState() on the custom bus -- that would clear its sinks, which the
                // user may have intentionally preserved.
                IMessageBus activeBus = MessageHandler.MessageBus;
                IMessageBus defaultBus = MessageHandler.InitialGlobalMessageBus;

                MessageHandler.ResetStatics();
                MessageBus.MessageBus.ResetStaticPools();

                if (
                    !ReferenceEquals(activeBus, defaultBus)
                    && activeBus is MessageBus.MessageBus customConcrete
                )
                {
                    customConcrete.BumpResetGeneration();
                }
            }
        }

        private static BaselineState CaptureBaseline()
        {
            bool messagingDebugEnabled = MessagingDebug.enabled;
            Action<LogLevel, string> messagingDebugLogFunction = MessagingDebug.LogFunction;
            DiagnosticsTarget globalDiagnosticsTargets = IMessageBus.GlobalDiagnosticsTargets;
            int globalMessageBufferSize = IMessageBus.GlobalMessageBufferSize;
            int globalSequentialIndex = IMessageBus.GlobalSequentialIndex;
            int syntheticOwnerCounter = MessageRegistrationBuilder.GetSyntheticOwnerCounter();

            return new BaselineState(
                messagingDebugEnabled,
                messagingDebugLogFunction,
                globalDiagnosticsTargets,
                globalMessageBufferSize,
                globalSequentialIndex,
                syntheticOwnerCounter
            );
        }

        private sealed class BaselineState
        {
            internal BaselineState(
                bool messagingDebugEnabled,
                Action<LogLevel, string> messagingDebugLogFunction,
                DiagnosticsTarget globalDiagnosticsTargets,
                int globalMessageBufferSize,
                int globalSequentialIndex,
                int syntheticOwnerCounter
            )
            {
                MessagingDebugEnabled = messagingDebugEnabled;
                MessagingDebugLogFunction = messagingDebugLogFunction;
                GlobalDiagnosticsTargets = globalDiagnosticsTargets;
                GlobalMessageBufferSize = globalMessageBufferSize;
                GlobalSequentialIndex = globalSequentialIndex;
                SyntheticOwnerCounter = syntheticOwnerCounter;
            }

            internal bool MessagingDebugEnabled { get; }

            internal Action<LogLevel, string> MessagingDebugLogFunction { get; }

            internal DiagnosticsTarget GlobalDiagnosticsTargets { get; }

            internal int GlobalMessageBufferSize { get; }

            internal int GlobalSequentialIndex { get; }

            internal int SyntheticOwnerCounter { get; }
        }
    }
}
