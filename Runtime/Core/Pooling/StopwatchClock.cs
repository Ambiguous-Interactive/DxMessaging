namespace DxMessaging.Core.Pooling
{
    using System.Diagnostics;
    using System.Runtime.CompilerServices;

    /// <summary>
    /// Default <see cref="IDxMessagingClock"/> backed by a process-lifetime
    /// <see cref="Stopwatch"/>. Monotonic and Unity-agnostic so it works in
    /// Unity players, editors, and non-Unity hosts.
    /// </summary>
    public sealed class StopwatchClock : IDxMessagingClock
    {
        /// <summary>Shared instance. Stopwatch is lock-free and safe to share.</summary>
        public static readonly StopwatchClock Instance = new();

        private static readonly double TicksToSeconds = 1.0 / Stopwatch.Frequency;

        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        /// <inheritdoc />
        public double NowSeconds
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _stopwatch.ElapsedTicks * TicksToSeconds;
        }
    }
}
