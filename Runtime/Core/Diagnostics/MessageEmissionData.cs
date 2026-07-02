namespace DxMessaging.Core.Diagnostics
{
    using System;
    using System.Linq;
#if UNITY_2021_3_OR_NEWER
    using UnityEngine;
#else
    using System.Diagnostics;
#endif

    /// <summary>
    /// Captures a snapshot of a message emission for diagnostics.
    /// </summary>
    /// <remarks>
    /// When diagnostics are enabled (see <see cref="MessageBus.IMessageBus.GlobalDiagnosticsTargets"/>),
    /// the bus and tokens record recent emissions in ring buffers along with a trimmed stack trace
    /// that excludes DxMessaging internals for easier debugging.
    ///
    /// The <see cref="context"/> contains the relevant <see cref="InstanceId"/> for targeted/broadcast messages
    /// (target or source respectively) and is null for untargeted messages. Runtime records emitted by a
    /// <see cref="MessageBus.MessageBus"/> also carry a <see cref="traceId"/> that token-local delivery records can
    /// use to join a bus emission to the registrations that observed it.
    /// </remarks>
    public readonly struct MessageEmissionData
    {
        private static readonly string[] NewlineSeparators = { "\r\n", "\n", "\r" };
        private static readonly string JoinSeparator = Environment.NewLine;

        /// <summary>Emitted message payload.</summary>
        public readonly IMessage message;

        /// <summary>Relevant context (target/source) for the emission; null for untargeted.</summary>
        public readonly InstanceId? context;

        /// <summary>Trimmed stack trace captured at the emission site.</summary>
        public readonly string stackTrace;

        /// <summary>
        /// Dispatch trace identifier shared by bus-side emission records and token-side delivery records.
        /// </summary>
        public readonly long traceId;

        /// <summary>
        /// Registration handle that observed this message; default for bus-side emission records.
        /// </summary>
        public readonly MessageRegistrationHandle registrationHandle;

        /// <summary>
        /// Creates a new diagnostic record for an emitted message.
        /// </summary>
        /// <param name="message">The message that was emitted.</param>
        /// <param name="context">Target or source depending on message category; null for untargeted.</param>
        public MessageEmissionData(IMessage message, InstanceId? context = null)
            : this(message, context, traceId: 0, registrationHandle: default) { }

        internal MessageEmissionData(IMessage message, long traceId)
            : this(message, context: null, traceId, registrationHandle: default) { }

        internal MessageEmissionData(IMessage message, InstanceId? context, long traceId)
            : this(message, context, traceId, registrationHandle: default) { }

        internal MessageEmissionData(
            IMessage message,
            InstanceId? context,
            long traceId,
            MessageRegistrationHandle registrationHandle
        )
        {
            this.message = message;
            this.context = context;
            this.traceId = traceId;
            this.registrationHandle = registrationHandle;
            stackTrace = GetAccurateStackTrace();
        }

        private static string GetAccurateStackTrace()
        {
            string fullStackTrace;
#if UNITY_2021_3_OR_NEWER
            fullStackTrace = StackTraceUtility.ExtractStackTrace();
#else
            fullStackTrace = new StackTrace(true).ToString();
#endif
            if (string.IsNullOrWhiteSpace(fullStackTrace))
            {
                return fullStackTrace;
            }

            string[] lines = fullStackTrace.Split(NewlineSeparators, StringSplitOptions.None);

            string[] trimmedLines = lines
                .Where(line => !string.IsNullOrWhiteSpace(line) && !IsInternalFrame(line))
                .ToArray();

            return trimmedLines.Length == 0
                ? string.Empty
                : string.Join(JoinSeparator, trimmedLines);
        }

        private static bool IsInternalFrame(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            if (!line.Contains("DxMessaging.", StringComparison.Ordinal))
            {
                return false;
            }

            return line.Contains("DxMessaging.Core.", StringComparison.Ordinal)
                || line.Contains("DxMessaging.Unity.", StringComparison.Ordinal)
                || line.Contains("DxMessaging.Editor.", StringComparison.Ordinal);
        }
    }
}
