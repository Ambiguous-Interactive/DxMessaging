namespace DxMessaging.Core.Diagnostics
{
    using System;
    using System.Text;
    using DxMessaging.Core.MessageBus;
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
    /// the bus and tokens record recent emissions in ring buffers. Each record also carries a trimmed
    /// stack trace that excludes DxMessaging internals, but ONLY when
    /// <see cref="MessageBus.IMessageBus.GlobalDiagnosticsStackTraces"/> is enabled; capturing a trace
    /// costs hundreds of microseconds and tens of allocations per record, so <see cref="stackTrace"/> is
    /// <see cref="string.Empty"/> by default.
    ///
    /// The <see cref="context"/> contains the relevant <see cref="InstanceId"/> for targeted/broadcast messages
    /// (target or source respectively) and is null for untargeted messages. Runtime records emitted by a
    /// <see cref="MessageBus.MessageBus"/> also carry a <see cref="traceId"/> that token-local delivery records can
    /// use to join a bus emission to the registrations that observed it.
    /// </remarks>
    public readonly struct MessageEmissionData
    {
        private static readonly string JoinSeparator = Environment.NewLine;

        /// <summary>Emitted message payload.</summary>
        public readonly IMessage message;

        /// <summary>Relevant context (target/source) for the emission; null for untargeted.</summary>
        public readonly InstanceId? context;

        /// <summary>
        /// Trimmed stack trace captured at the emission site, or <see cref="string.Empty"/> when
        /// <see cref="MessageBus.IMessageBus.GlobalDiagnosticsStackTraces"/> is disabled (the default).
        /// </summary>
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
            stackTrace = IMessageBus.GlobalDiagnosticsStackTraces
                ? GetAccurateStackTrace()
                : string.Empty;
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

            return TrimInternalFrames(fullStackTrace);
        }

        /// <summary>
        /// Drops blank lines and DxMessaging-internal frames in a single pass over
        /// <paramref name="fullStackTrace"/>. The previous split + LINQ filter + join allocated a
        /// line array, an iterator, a filtered array, and a join buffer on top of the trace itself;
        /// this keeps the identical output with one builder.
        /// </summary>
        private static string TrimInternalFrames(string fullStackTrace)
        {
            StringBuilder builder = null;
            int length = fullStackTrace.Length;
            int lineStart = 0;
            while (lineStart <= length)
            {
                int lineEnd = lineStart;
                while (
                    lineEnd < length
                    && fullStackTrace[lineEnd] != '\n'
                    && fullStackTrace[lineEnd] != '\r'
                )
                {
                    lineEnd++;
                }

                int lineLength = lineEnd - lineStart;
                if (
                    0 < lineLength
                    && !IsBlank(fullStackTrace, lineStart, lineLength)
                    && !IsInternalFrame(fullStackTrace, lineStart, lineLength)
                )
                {
                    builder ??= new StringBuilder(length);
                    if (0 < builder.Length)
                    {
                        builder.Append(JoinSeparator);
                    }

                    builder.Append(fullStackTrace, lineStart, lineLength);
                }

                if (lineEnd >= length)
                {
                    break;
                }

                // A CRLF pair terminates one line, matching the old separator list.
                if (
                    fullStackTrace[lineEnd] == '\r'
                    && lineEnd + 1 < length
                    && fullStackTrace[lineEnd + 1] == '\n'
                )
                {
                    lineEnd++;
                }

                lineStart = lineEnd + 1;
            }

            return builder == null ? string.Empty : builder.ToString();
        }

        private static bool IsBlank(string text, int start, int length)
        {
            for (int index = start; index < start + length; index++)
            {
                if (!char.IsWhiteSpace(text[index]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsInternalFrame(string text, int start, int length)
        {
            if (0 > text.IndexOf("DxMessaging.", start, length, StringComparison.Ordinal))
            {
                return false;
            }

            return 0 <= text.IndexOf("DxMessaging.Core.", start, length, StringComparison.Ordinal)
                || 0 <= text.IndexOf("DxMessaging.Unity.", start, length, StringComparison.Ordinal)
                || 0
                    <= text.IndexOf("DxMessaging.Editor.", start, length, StringComparison.Ordinal);
        }
    }
}
