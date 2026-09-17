#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Benchmarks
{
    using System;
    using System.Diagnostics;
    using System.Globalization;
    using System.Threading;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;

    /// <summary>Physical one-percent burst-tail control for the #512 protocol.</summary>
    public sealed class OpenLoopTailControlTests
    {
        private const int BurstCount = 100;
        private const int MessagesPerBurst = 16;
        private const int StallBurst = BurstCount - 1;

        [Test]
        public void OneStalledFinalHandlerMovesStrictUpperP99()
        {
            long frequency = Stopwatch.Frequency;
            Assert.That(frequency, Is.GreaterThanOrEqualTo(1_000));
            using IDisposable registry = MessageBus.IsolateIdleSweepRegistryForBenchmark();
            MessageBus bus = new() { DiagnosticsMode = false };
            MessageHandler handler = new(new InstanceId(0x5120_0002), bus) { active = true };
            TailRun current = null;

            void OnMessage(in TailMessage message)
            {
                if (
                    current == null
                    || message.BurstIndex < 0
                    || message.BurstIndex >= BurstCount
                    || message.ItemIndex < 0
                    || message.ItemIndex >= MessagesPerBurst
                    || current.CallbackCount >= BurstCount * MessagesPerBurst
                )
                {
                    throw new InvalidOperationException("Tail callback ID or count is invalid.");
                }
                int position = current.CallbackCount++;
                current.ObservedBursts[position] = message.BurstIndex;
                current.ObservedItems[position] = message.ItemIndex;
                if (message.ItemIndex == MessagesPerBurst - 1)
                {
                    if (current.InjectStall && message.BurstIndex == StallBurst)
                    {
                        Thread.Sleep(250);
                        ++current.StallCount;
                    }
                    current.Completions[message.BurstIndex] = Stopwatch.GetTimestamp();
                }
            }

            using MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            token.DiagnosticMode = false;
            token.Enable();
            _ = token.RegisterUntargeted<TailMessage>(OnMessage);

            current = new TailRun(false, frequency, "editor-tail-warmup");
            for (int item = 0; item < MessagesPerBurst; ++item)
            {
                TailMessage warmup = new(0, item);
                bus.UntargetedBroadcast(ref warmup);
            }
            Assert.That(current.CallbackCount, Is.EqualTo(MessagesPerBurst));

            TailRun Capture(bool injectStall, string traceId)
            {
                TailRun run = new(injectStall, frequency, traceId);
                current = run;
                for (int burst = 0; burst < BurstCount; ++burst)
                {
                    while (Stopwatch.GetTimestamp() < run.Arrivals[burst])
                    {
                        Thread.SpinWait(32);
                    }
                    run.Starts[burst] = Stopwatch.GetTimestamp();
                    for (int item = 0; item < MessagesPerBurst; ++item)
                    {
                        TailMessage message = new(burst, item);
                        bus.UntargetedBroadcast(ref message);
                    }
                }
                Assert.That(run.CallbackCount, Is.EqualTo(BurstCount * MessagesPerBurst));
                for (int burst = 0; burst < BurstCount; ++burst)
                {
                    Assert.That(run.Starts[burst], Is.GreaterThanOrEqualTo(run.Arrivals[burst]));
                    Assert.That(run.Completions[burst], Is.GreaterThanOrEqualTo(run.Starts[burst]));
                    for (int item = 0; item < MessagesPerBurst; ++item)
                    {
                        int position = burst * MessagesPerBurst + item;
                        Assert.That(run.ObservedBursts[position], Is.EqualTo(burst));
                        Assert.That(run.ObservedItems[position], Is.EqualTo(item));
                    }
                }
                return run;
            }

            TailRun baseline = Capture(false, "editor-tail-baseline");
            TailRun stalled = Capture(true, "editor-tail-stalled");
            Assert.That(baseline.StallCount, Is.Zero);
            Assert.That(stalled.StallCount, Is.EqualTo(1));
            for (int burst = 0; burst < BurstCount; ++burst)
            {
                Assert.That(baseline.Completions[burst], Is.LessThan(baseline.HorizonEnd));
            }
            Assert.That(
                stalled.Completions[StallBurst],
                Is.GreaterThanOrEqualTo(stalled.HorizonEnd)
            );

            Report(baseline);
            Report(stalled);

            long baselineP99 = 0;
            long stalledP99 = 0;
            long baselineTotal = 0;
            long stalledTotal = 0;
            for (int burst = 0; burst < BurstCount; ++burst)
            {
                long baselineDelay = baseline.Completions[burst] - baseline.Arrivals[burst];
                long stalledDelay = stalled.Completions[burst] - stalled.Arrivals[burst];
                baselineP99 = Math.Max(baselineP99, baselineDelay);
                stalledP99 = Math.Max(stalledP99, stalledDelay);
                baselineTotal = checked(baselineTotal + baselineDelay);
                stalledTotal = checked(stalledTotal + stalledDelay);
            }
            long p99Shift = stalledP99 - baselineP99;
            long meanShiftNumerator = stalledTotal - baselineTotal;
            TestContext.Out.WriteLine(
                "DXM_OPEN_LOOP_TAIL_EFFECT_V1 p99ShiftTicks="
                    + p99Shift.ToString(CultureInfo.InvariantCulture)
                    + " meanShiftNumeratorTicks="
                    + meanShiftNumerator.ToString(CultureInfo.InvariantCulture)
                    + " meanShiftDenominator="
                    + BurstCount.ToString(CultureInfo.InvariantCulture)
            );
            Assert.That(p99Shift, Is.GreaterThan(frequency / 10));
            Assert.That(meanShiftNumerator, Is.GreaterThan(0));
            Assert.That(meanShiftNumerator, Is.LessThan(10 * p99Shift));
        }

        private static void Report(TailRun run)
        {
            string observations = OpenLoopBurstTraceTests.BuildObservations(
                BurstCount,
                run.Digest,
                run.Starts,
                run.Completions
            );
            TestContext.Out.WriteLine("DXM_OPEN_LOOP_SCHEDULE_V1 " + run.Schedule);
            TestContext.Out.WriteLine("DXM_OPEN_LOOP_OBSERVATIONS_V1 " + observations);
        }

        private sealed class TailRun
        {
            public TailRun(bool injectStall, long frequency, string traceId)
            {
                InjectStall = injectStall;
                Arrivals = new long[BurstCount];
                Starts = new long[BurstCount];
                Completions = new long[BurstCount];
                ObservedBursts = new int[BurstCount * MessagesPerBurst];
                ObservedItems = new int[BurstCount * MessagesPerBurst];
                long spacing = frequency / 1_000;
                long first = checked(Stopwatch.GetTimestamp() + frequency / 100);
                for (int burst = 0; burst < BurstCount; ++burst)
                {
                    Arrivals[burst] = checked(first + burst * spacing);
                }
                HorizonEnd = checked(first + BurstCount * spacing);
                Schedule = OpenLoopBurstTraceTests.BuildSchedule(
                    traceId,
                    frequency,
                    first,
                    HorizonEnd,
                    Arrivals
                );
                Digest = OpenLoopBurstTraceTests.Digest(Schedule);
            }

            public bool InjectStall { get; }

            public long[] Arrivals { get; }

            public long[] Starts { get; }

            public long[] Completions { get; }

            public int[] ObservedBursts { get; }

            public int[] ObservedItems { get; }

            public long HorizonEnd { get; }

            public string Schedule { get; }

            public string Digest { get; }

            public int CallbackCount { get; set; }

            public int StallCount { get; set; }
        }

        private readonly struct TailMessage : IUntargetedMessage
        {
            public TailMessage(int burstIndex, int itemIndex)
            {
                BurstIndex = burstIndex;
                ItemIndex = itemIndex;
            }

            public int BurstIndex { get; }

            public int ItemIndex { get; }
        }
    }
}
#endif
