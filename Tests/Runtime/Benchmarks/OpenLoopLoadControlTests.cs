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

    /// <summary>Fixed offered-load controls for the #512 open-loop protocol.</summary>
    public sealed class OpenLoopLoadControlTests
    {
        private const int OfferCount = 100;

        [TestCase("idle", 8)]
        [TestCase("moderate", 4)]
        [TestCase("nominal-saturation", 2)]
        [TestCase("overload", 1)]
        public void FixedOfferedLoadKeepsLateCompletions(string control, int spacingMs)
        {
            long frequency = Stopwatch.Frequency;
            Assert.That(frequency, Is.GreaterThanOrEqualTo(1_000));
            long serviceTicks = frequency / 500;
            long spacingTicks = checked(frequency * spacingMs / 1_000);
            Assert.That(serviceTicks, Is.GreaterThan(0));
            Assert.That(spacingTicks, Is.GreaterThan(0));

            using IDisposable registry = MessageBus.IsolateIdleSweepRegistryForBenchmark();
            MessageBus bus = new() { DiagnosticsMode = false };
            MessageHandler handler = new(new InstanceId(0x5120_0003), bus) { active = true };
            long[] arrivals = new long[OfferCount];
            long[] starts = new long[OfferCount];
            long[] completions = new long[OfferCount];
            int[] callbackOrder = new int[OfferCount];
            int callbackCount = 0;
            bool serviceEnabled = false;

            void OnMessage(in LoadMessage message)
            {
                if (message.Id < 0 || OfferCount <= message.Id || OfferCount <= callbackCount)
                {
                    throw new InvalidOperationException("Load callback ID or count is invalid.");
                }
                callbackOrder[callbackCount++] = message.Id;
                if (serviceEnabled)
                {
                    long serviceEnd = checked(Stopwatch.GetTimestamp() + serviceTicks);
                    while (Stopwatch.GetTimestamp() < serviceEnd)
                    {
                        Thread.SpinWait(32);
                    }
                }
                completions[message.Id] = Stopwatch.GetTimestamp();
            }

            using MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            token.DiagnosticMode = false;
            token.Enable();
            _ = token.RegisterUntargeted<LoadMessage>(OnMessage);
            LoadMessage warmup = new(0);
            bus.UntargetedBroadcast(ref warmup);
            Assert.That(callbackCount, Is.EqualTo(1));
            callbackCount = 0;
            Array.Clear(completions, 0, OfferCount);

            long firstOffer = checked(Stopwatch.GetTimestamp() + frequency / 10);
            long horizonEnd = checked(firstOffer + OfferCount * spacingTicks);
            for (int id = 0; id < OfferCount; ++id)
            {
                arrivals[id] = checked(firstOffer + id * spacingTicks);
            }
            string schedule = OpenLoopBurstTraceTests.BuildSchedule(
                "editor-load-" + control,
                frequency,
                firstOffer,
                horizonEnd,
                arrivals
            );
            string digest = OpenLoopBurstTraceTests.Digest(schedule);
            TestContext.Out.WriteLine("DXM_OPEN_LOOP_SCHEDULE_V1 " + schedule);
            Assert.That(Stopwatch.GetTimestamp(), Is.LessThan(firstOffer), "schedule missed offer");
            serviceEnabled = true;
            for (int id = 0; id < OfferCount; ++id)
            {
                while (Stopwatch.GetTimestamp() < arrivals[id])
                {
                    Thread.SpinWait(32);
                }
                LoadMessage message = new(id);
                starts[id] = Stopwatch.GetTimestamp();
                bus.UntargetedBroadcast(ref message);
            }

            Assert.That(callbackCount, Is.EqualTo(OfferCount));
            int completedWithin = 0;
            for (int id = 0; id < OfferCount; ++id)
            {
                Assert.That(callbackOrder[id], Is.EqualTo(id), $"callback order {id}");
                Assert.That(starts[id], Is.GreaterThanOrEqualTo(arrivals[id]), $"start {id}");
                Assert.That(
                    completions[id],
                    Is.GreaterThanOrEqualTo(starts[id]),
                    $"completion {id}"
                );
                if (completions[id] < horizonEnd)
                {
                    ++completedWithin;
                }
            }
            string observations = OpenLoopBurstTraceTests.BuildObservations(
                OfferCount,
                digest,
                starts,
                completions
            );
            TestContext.Out.WriteLine("DXM_OPEN_LOOP_OBSERVATIONS_V1 " + observations);
            TestContext.Out.WriteLine(
                "DXM_OPEN_LOOP_LOAD_V1 control="
                    + control
                    + " serviceTicks="
                    + serviceTicks.ToString(CultureInfo.InvariantCulture)
                    + " completedWithin="
                    + completedWithin.ToString(CultureInfo.InvariantCulture)
            );
            if (control == "idle" || control == "moderate")
            {
                Assert.That(completedWithin, Is.EqualTo(OfferCount));
            }
            else if (control == "overload")
            {
                Assert.That(completedWithin, Is.LessThan(OfferCount));
            }
        }

        private readonly struct LoadMessage : IUntargetedMessage
        {
            public LoadMessage(int id) => Id = id;

            public int Id { get; }
        }
    }
}
#endif
