#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Benchmarks
{
    using System;
    using System.Diagnostics;
    using System.Text;
    using NUnit.Framework;

    /// <summary>Test-only raw stopwatch pairs for the #512 observer-cost screen.</summary>
    public sealed class LatencyClockProbeTests
    {
        private const int SampleCount = 256;
        private const int WarmupCount = 1_000;

        [Test]
        public void TimerOnlyAndEmptyCallbackRetainMonotonicRawPairs()
        {
            Assert.That(Stopwatch.Frequency, Is.GreaterThan(0));
            CaptureAndReport("timer-only", null);
            CaptureAndReport("empty-callback", EmptyCallback);
        }

        private static void EmptyCallback() { }

        private static void CaptureAndReport(string control, Action callback)
        {
            long[] starts = new long[SampleCount];
            long[] ends = new long[SampleCount];
            int callbackCalls = 0;
            for (int i = 0; i < WarmupCount; ++i)
            {
                callback?.Invoke();
                Stopwatch.GetTimestamp();
            }
            for (int i = 0; i < SampleCount; ++i)
            {
                starts[i] = Stopwatch.GetTimestamp();
                callback?.Invoke();
                ends[i] = Stopwatch.GetTimestamp();
                if (callback != null)
                {
                    ++callbackCalls;
                }
            }
            Assert.That(callbackCalls, Is.EqualTo(callback == null ? 0 : SampleCount));
            StringBuilder output = new();
            output.Append("DXM_LATENCY_CLOCK_V1 control=").Append(control);
            output.Append(" frequencyHz=").Append(Stopwatch.Frequency);
            output.Append(" callbackCalls=").Append(callbackCalls);
            output.Append(" pairs=");
            for (int i = 0; i < SampleCount; ++i)
            {
                Assert.That(ends[i], Is.GreaterThanOrEqualTo(starts[i]), $"pair {i}");
                if (i != 0)
                {
                    output.Append(';');
                }
                output.Append(starts[i]).Append(':').Append(ends[i]);
            }
            TestContext.Out.WriteLine(output.ToString());
        }
    }
}
#endif
