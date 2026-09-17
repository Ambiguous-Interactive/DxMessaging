#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Benchmarks
{
    using System;
    using System.Diagnostics;
    using System.Globalization;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;

    /// <summary>Raw immediate-dispatch burst traces for the #512 open-loop protocol screen.</summary>
    public sealed class OpenLoopBurstTraceTests
    {
        [TestCase(1)]
        [TestCase(4)]
        [TestCase(16)]
        [TestCase(64)]
        [TestCase(128)]
        [TestCase(256)]
        public void FixedArrivalBurstRetainsEveryFinalHandlerCompletion(int burstSize)
        {
            Assert.That(Stopwatch.Frequency, Is.GreaterThanOrEqualTo(60));
            using IDisposable registry = MessageBus.IsolateIdleSweepRegistryForBenchmark();
            MessageBus bus = new() { DiagnosticsMode = false };
            MessageHandler handler = new(new InstanceId(0x5120_0001), bus) { active = true };
            long[] arrivals = new long[burstSize];
            long[] starts = new long[burstSize];
            long[] completions = new long[burstSize];
            int[] callbackOrder = new int[burstSize];
            int callbackCount = 0;

            void OnMessage(in BurstMessage message)
            {
                if (message.Id < 0 || message.Id >= burstSize || callbackCount >= burstSize)
                {
                    throw new InvalidOperationException("Burst callback ID or count is invalid.");
                }
                callbackOrder[callbackCount++] = message.Id;
                completions[message.Id] = Stopwatch.GetTimestamp();
            }

            using MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            token.DiagnosticMode = false;
            token.Enable();
            _ = token.RegisterUntargeted<BurstMessage>(OnMessage);

            // The typed route is warmed before the declared burst and the count is reset.
            BurstMessage warmup = new(0);
            bus.UntargetedBroadcast(ref warmup);
            Assert.That(callbackCount, Is.EqualTo(1));
            callbackCount = 0;
            Array.Clear(completions, 0, burstSize);

            long frequency = Stopwatch.Frequency;
            long offeredTick = checked(Stopwatch.GetTimestamp() + frequency / 10);
            long horizonEndTick = checked(offeredTick + frequency / 60);
            for (int id = 0; id < burstSize; ++id)
            {
                arrivals[id] = offeredTick;
            }
            string schedule = BuildSchedule(
                "editor-immediate-burst-" + burstSize.ToString(CultureInfo.InvariantCulture),
                frequency,
                offeredTick,
                horizonEndTick,
                arrivals
            );
            string digest = Digest(schedule);
            TestContext.Out.WriteLine("DXM_OPEN_LOOP_SCHEDULE_V1 " + schedule);
            Assert.That(
                Stopwatch.GetTimestamp(),
                Is.LessThan(offeredTick),
                "schedule missed offer"
            );
            while (Stopwatch.GetTimestamp() < offeredTick)
            {
                Thread.SpinWait(32);
            }
            for (int id = 0; id < burstSize; ++id)
            {
                BurstMessage message = new(id);
                starts[id] = Stopwatch.GetTimestamp();
                bus.UntargetedBroadcast(ref message);
            }

            Assert.That(callbackCount, Is.EqualTo(burstSize));
            for (int id = 0; id < burstSize; ++id)
            {
                Assert.That(callbackOrder[id], Is.EqualTo(id), $"callback order {id}");
                Assert.That(starts[id], Is.GreaterThanOrEqualTo(offeredTick), $"start {id}");
                Assert.That(
                    completions[id],
                    Is.GreaterThanOrEqualTo(starts[id]),
                    $"completion {id}"
                );
            }

            string observations = BuildObservations(burstSize, digest, starts, completions);
            TestContext.Out.WriteLine("DXM_OPEN_LOOP_OBSERVATIONS_V1 " + observations);
        }

        internal static string BuildSchedule(
            string traceId,
            long frequency,
            long offered,
            long end,
            long[] arrivals
        )
        {
            StringBuilder output = new();
            output
                .Append("{\"schemaVersion\":1,\"traceId\":\"")
                .Append(traceId)
                .Append("\",\"frequencyHz\":\"")
                .Append(Decimal(frequency))
                .Append("\",\"horizonStartTick\":\"")
                .Append(Decimal(offered))
                .Append("\",\"horizonEndTick\":\"")
                .Append(Decimal(end))
                .Append("\",\"arrivals\":[");
            for (int id = 0; id < arrivals.Length; ++id)
            {
                if (id != 0)
                {
                    output.Append(',');
                }
                output
                    .Append("{\"id\":\"")
                    .Append(id.ToString(CultureInfo.InvariantCulture))
                    .Append("\",\"arrivalTick\":\"")
                    .Append(Decimal(arrivals[id]))
                    .Append("\"}");
            }
            return output.Append("]}").ToString();
        }

        internal static string BuildObservations(
            int count,
            string digest,
            long[] starts,
            long[] ends
        )
        {
            StringBuilder output = new();
            output
                .Append("{\"schemaVersion\":1,\"scheduleSha256\":\"")
                .Append(digest)
                .Append("\",\"events\":[");
            for (int id = 0; id < count; ++id)
            {
                if (id != 0)
                {
                    output.Append(',');
                }
                output
                    .Append("{\"id\":\"")
                    .Append(id.ToString(CultureInfo.InvariantCulture))
                    .Append("\",\"startTick\":\"")
                    .Append(Decimal(starts[id]))
                    .Append("\",\"completionTick\":\"")
                    .Append(Decimal(ends[id]))
                    .Append("\"}");
            }
            return output.Append("]}").ToString();
        }

        private static string Decimal(long value) => value.ToString(CultureInfo.InvariantCulture);

        internal static string Digest(string schedule)
        {
            using SHA256 sha = SHA256.Create();
            return BitConverter
                .ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(schedule)))
                .Replace("-", string.Empty)
                .ToLowerInvariant();
        }

        private readonly struct BurstMessage : IUntargetedMessage
        {
            public BurstMessage(int id) => Id = id;

            public int Id { get; }
        }
    }
}
#endif
