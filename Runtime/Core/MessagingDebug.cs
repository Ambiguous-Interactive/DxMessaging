namespace DxMessaging.Core
{
    using System;

    /// <summary>
    /// Severity of the log message.
    /// </summary>
    public enum LogLevel
    {
        /// <summary>
        /// Verbose diagnostic information useful while developing or debugging.
        /// </summary>
        Debug = 0,

        /// <summary>
        /// Informational messages that describe normal operation.
        /// </summary>
        Info = 1,

        /// <summary>
        /// Non-fatal issues that should be investigated.
        /// </summary>
        Warn = 2,

        /// <summary>
        /// Errors indicating messaging failed or data may be lost.
        /// </summary>
        Error = 3,
    }

    /// <summary>
    /// Debug/diagnostic logging helper for DxMessaging.
    /// </summary>
    /// <remarks>
    /// Set <see cref="enabled"/> to <c>true</c> and assign <see cref="LogFunction"/> to receive formatted logs
    /// from the messaging system (e.g., registration mismatches, over-deregistration, etc.). In Unity, you can set
    /// <c>MessagingDebug.LogFunction = (lvl, msg) =&gt; Debug.Log($"[{lvl}] {msg}");</c>
    /// </remarks>
    public static class MessagingDebug
    {
        /// <summary>Enables formatted logs and package call sites that explicitly check this flag.</summary>
        /// <remarks>The two-argument <see cref="Log(LogLevel, string)"/> overload does not read this flag.</remarks>
        public static bool enabled = false;

        /// <summary>
        /// Custom log function to use.
        /// </summary>
        /// <remarks>
        /// Use a two-argument callback, for example
        /// <c>(level, message) =&gt; UnityEngine.Debug.Log($"[{level}] {message}")</c>.
        /// A null receiver discards logs from either overload.
        /// </remarks>
        public static Action<LogLevel, string> LogFunction = null;

        /// <summary>
        /// Sends an unformatted message to the configured receiver, regardless of the enabled flag.
        /// </summary>
        /// <param name="logLevel">Severity of the message.</param>
        /// <param name="message">Message to log.</param>
        public static void Log(LogLevel logLevel, string message)
        {
            LogFunction?.Invoke(logLevel, message);
        }

        /// <summary>
        /// Sends a message to the configured receiver only when enabled.
        /// </summary>
        /// <remarks>
        /// A null receiver discards the message. Empty arguments send the text unchanged;
        /// otherwise <see cref="string.Format(string, object[])"/> formats it first.
        /// Formatting and receiver exceptions propagate to the caller.
        /// </remarks>
        /// <param name="logLevel">Severity of the message.</param>
        /// <param name="message">Format string.</param>
        /// <param name="args">Args to populate the format string with.</param>
        public static void Log(LogLevel logLevel, string message, params object[] args)
        {
            if (!enabled)
            {
                return;
            }

            Action<LogLevel, string> logFunction = LogFunction;
            /*
                We can potentially avoid an unnecessary string.Format call if the LogFunction is null,
                which is why the null check is outside the InternalLog function.
            */
            if (logFunction == null)
            {
                return;
            }

            if (args.Length <= 0)
            {
                logFunction.Invoke(logLevel, message);
            }
            else
            {
                logFunction.Invoke(logLevel, string.Format(message, args));
            }
        }
    }
}
