namespace DxMessaging.Tests.Runtime
{
    using DxMessaging.Core.Pooling;

    /// <summary>
    /// Manually advanced <see cref="IDxMessagingClock"/> for deterministic
    /// eviction tests. Tests construct a FakeClock, inject it into the system
    /// under test, and call <see cref="Advance"/> to push the apparent time
    /// forward without sleeping.
    /// </summary>
    public sealed class FakeClock : IDxMessagingClock
    {
        private double _now;

        public FakeClock(double initialSeconds = 0d)
        {
            _now = initialSeconds;
        }

        /// <summary>
        /// Number of times <see cref="NowSeconds"/> has been read.
        /// </summary>
        public int ReadCount { get; private set; }

        /// <inheritdoc />
        public double NowSeconds
        {
            get
            {
                ++ReadCount;
                return _now;
            }
        }

        /// <summary>Advance the clock by the given number of seconds.</summary>
        public void Advance(double seconds)
        {
            if (seconds < 0d)
            {
                throw new System.ArgumentOutOfRangeException(
                    nameof(seconds),
                    "FakeClock.Advance does not accept negative deltas."
                );
            }
            _now += seconds;
        }

        /// <summary>Set the clock to an absolute value. Must be non-decreasing.</summary>
        public void SetTo(double seconds)
        {
            if (seconds < _now)
            {
                throw new System.ArgumentOutOfRangeException(
                    nameof(seconds),
                    "FakeClock is monotonic; cannot rewind."
                );
            }
            _now = seconds;
        }
    }
}
