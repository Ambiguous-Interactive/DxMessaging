namespace DxMessaging.Tests.Runtime.MemoryReclaim
{
    using System;
    using System.Runtime.CompilerServices;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Core.Pooling;
    using NUnit.Framework;

    /// <summary>Checks what the synchronous collection window observes for minimal control graphs.</summary>
    /// <remarks>
    /// 2026-10-26: native collection failures include isolated graphs without storage reuse. Compare minimal
    /// managed and production bus graphs with an independent negative and retained positive control.
    /// Factory order is an explicit input; passing controls do not explain the original failures.
    /// </remarks>
    [TestFixture]
    [Category("MemoryReclaim")]
#if !UNITY_2021_3_OR_NEWER
    [NonParallelizable]
#endif
    public sealed class NativeCollectionWindowControlTests
    {
        [Test]
        public void SynchronousWindowObservesMinimalGraphLifetime(
            [Values(0, 1, 2, 3)] int shape,
            [Values(false, true)] bool negativeFactoryLast
        )
        {
            ControlNode positive = new();
            WeakReference positiveReference = new(positive);
            Probe probe;
            WeakReference negative;
            if (negativeFactoryLast)
            {
                probe = CreateProbe(shape);
                negative = CreateNegativeControl();
            }
            else
            {
                negative = CreateNegativeControl();
                probe = CreateProbe(shape);
            }
            try
            {
                for (int index = 0; index < 3; ++index)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
                bool negativeAlive = negative.IsAlive;
                bool positiveAlive = positiveReference.IsAlive;
                bool[] alive = new bool[probe.Graph.Length];
                for (int index = 0; index < alive.Length; ++index)
                {
                    alive[index] = probe.Graph[index].IsAlive;
                }
                string context =
                    $"shape={shape}, negativeFactoryLast={negativeFactoryLast}, "
                    + $"negativeAlive={negativeAlive}, positiveAlive={positiveAlive}, graph=[{string.Join(", ", alive)}]";
                TestContext.Out.WriteLine("COLLECTION_WINDOW " + context);
                Assert.That(negativeAlive, Is.False, context + ": independent negative control");
                Assert.That(positiveAlive, Is.True, context + ": retained positive control");
                Assert.That(
                    alive,
                    Is.EqualTo(new bool[alive.Length]),
                    context + ": unreachable graph"
                );
            }
            finally
            {
                try
                {
                    Cleanup(probe);
                }
                finally
                {
                    GC.KeepAlive(positive);
                }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference CreateNegativeControl() => new(new ControlNode());

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Probe CreateProbe(int shape)
        {
            if (shape < 2)
            {
                ControlNode node = new();
                node.Next = shape == 1 ? node : null;
                return new Probe(new[] { new WeakReference(node) });
            }
            MessageBus bus = MessageBus.CreateForInternalUse(
                new ConstantClock(),
                idleEvictionTicks: 30,
                idleEvictionEnabled: false,
                trimApiEnabled: true
            );
            if (shape == 2)
            {
                return new Probe(new[] { new WeakReference(bus) });
            }
            MessageHandler handler = new(new InstanceId(9371), bus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            Receiver receiver = new() { Token = token };
            _ = token.RegisterUntargeted<ProbeMessage>(receiver.Receive);
            token.Enable();
            ProbeMessage message = default;
            bus.UntargetedBroadcast(ref message);
            Assert.That(
                receiver.Calls,
                Is.EqualTo(1),
                "The active graph must deliver a real callback."
            );
            return new Probe(
                new[]
                {
                    new WeakReference(bus),
                    new WeakReference(handler),
                    new WeakReference(token),
                    new WeakReference(receiver),
                }
            );
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Cleanup(Probe probe)
        {
            if (probe.Graph.Length == 4)
            {
                (probe.Graph[2].Target as MessageRegistrationToken)?.Dispose();
            }
            if (probe.Graph[0].Target is MessageBus bus)
            {
                Assert.That(
                    MessageBusInspection.CountLiveRegistrations(bus),
                    Is.Zero,
                    "Owned cleanup removes every surviving minimal-graph registration."
                );
                _ = bus.Trim(force: true);
            }
        }

        private sealed class Probe
        {
            internal readonly WeakReference[] Graph;

            internal Probe(WeakReference[] graph) => Graph = graph;
        }

        private sealed class ControlNode
        {
            internal ControlNode Next;
        }

        private sealed class Receiver
        {
            internal MessageRegistrationToken Token;
            internal int Calls;

            internal void Receive(ProbeMessage message)
            {
                ++Calls;
                GC.KeepAlive(Token);
            }
        }

        private sealed class ConstantClock : IDxMessagingClock
        {
            public double NowSeconds => 0d;
        }

        private readonly struct ProbeMessage : IUntargetedMessage
        {
            public Type MessageType => typeof(ProbeMessage);
        }
    }
}
