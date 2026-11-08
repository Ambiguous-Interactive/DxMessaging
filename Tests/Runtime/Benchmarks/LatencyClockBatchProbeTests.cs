#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Benchmarks
{
    using System;
    using System.Diagnostics;
    using System.Text;
    using System.Threading;
    using NUnit.Framework;

    /// <summary>Test-only raw windows for the #512 batched timestamp-cost screen.</summary>
    public sealed class LatencyClockBatchProbeTests
    {
        private const int BatchCount = 128;
        private const int CallsPerBatch = 4_096;
        private const int WarmupCalls = 1_000;
        private static long s_checksum;

        [Test]
        public void TimestampCallBatchesRetainRawWindowPairs()
        {
            Assert.That(Stopwatch.Frequency, Is.GreaterThan(0));
            long[] starts = new long[BatchCount];
            long[] ends = new long[BatchCount];
            long warmup = 0;
            for (int i = 0; i < WarmupCalls; ++i)
            {
                warmup ^= Stopwatch.GetTimestamp();
            }
            Volatile.Write(ref s_checksum, warmup);

            for (int batch = 0; batch < BatchCount; ++batch)
            {
                long checksum = 0;
                starts[batch] = Stopwatch.GetTimestamp();
                for (int call = 0; call < CallsPerBatch; ++call)
                {
                    checksum ^= Stopwatch.GetTimestamp();
                }
                ends[batch] = Stopwatch.GetTimestamp();
                Volatile.Write(ref s_checksum, checksum);
            }

            StringBuilder output = new();
            output.Append("DXM_LATENCY_CLOCK_BATCH_V1 frequencyHz=").Append(Stopwatch.Frequency);
            output.Append(" callsPerBatch=").Append(CallsPerBatch);
            output.Append(" batchCount=").Append(BatchCount);
            output.Append(" pairs=");
            for (int batch = 0; batch < BatchCount; ++batch)
            {
                Assert.That(ends[batch], Is.GreaterThanOrEqualTo(starts[batch]), $"batch {batch}");
                if (batch != 0)
                {
                    output.Append(';');
                }
                output.Append(starts[batch]).Append(':').Append(ends[batch]);
            }
            TestContext.Out.WriteLine(output.ToString());
        }
    }
}
#endif
