namespace DxMessaging.Tests.Runtime.MemoryReclaim
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.CompilerServices;
    using NUnit.Framework;

    /// <summary>Checks synchronous collection of a plain four-object callback ownership cycle.</summary>
    /// <remarks>
    /// 2026-10-27: plain single-node weak-registry controls collect while the production active
    /// graph survives. Compare callback storage and invocation without any messaging objects.
    /// Factor association does not identify a native root or justify a production change.
    /// </remarks>
    [TestFixture]
    [Category("MemoryReclaim")]
#if !UNITY_2021_3_OR_NEWER
    [NonParallelizable]
#endif
    public sealed class NativePlainCallbackCollectionControlTests
    {
        [Test]
        public void SynchronousWindowObservesPlainCallbackGraph(
            [Values(false, true)] bool dictionary,
            [Values(false, true)] bool invoke,
            [Values(false, true)] bool negativeFactoryLast
        )
        {
            object positive = new();
            WeakReference positiveReference = new(positive);
            Probe probe;
            WeakReference negative;
            if (negativeFactoryLast)
            {
                probe = CreateProbe(dictionary, invoke);
                negative = CreateNegativeControl();
            }
            else
            {
                negative = CreateNegativeControl();
                probe = CreateProbe(dictionary, invoke);
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
                    $"dictionary={dictionary}, invoke={invoke}, negativeFactoryLast={negativeFactoryLast}, "
                    + $"negativeAlive={negativeAlive}, positiveAlive={positiveAlive}, "
                    + $"storedCallbacks={probe.StoredCallbacks}, calls={probe.Calls}, "
                    + $"graph=[{string.Join(", ", alive)}]";
                TestContext.Out.WriteLine("PLAIN_CALLBACK_WINDOW " + context);
                Assert.That(negativeAlive, Is.False, context + ": independent negative control");
                Assert.That(positiveAlive, Is.True, context + ": retained positive control");
                Assert.That(
                    probe.StoredCallbacks,
                    Is.EqualTo(1),
                    context + ": real callback storage"
                );
                Assert.That(
                    probe.Calls,
                    Is.EqualTo(invoke ? 1 : 0),
                    context + ": actual invocation"
                );
                Assert.That(alive, Is.EqualTo(new bool[4]), context + ": unreachable plain cycle");
            }
            finally
            {
                GC.KeepAlive(positive);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference CreateNegativeControl() => new(new object());

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Probe CreateProbe(bool dictionary, bool invoke)
        {
            PlainRoute route = new();
            PlainHandler handler = new(route);
            PlainToken token = new(route, handler);
            PlainReceiver receiver = new(token);
            if (dictionary)
            {
                route.Keyed.Add(0, receiver.Receive);
                if (invoke)
                {
                    route.Keyed[0]();
                }
            }
            else
            {
                route.Direct = receiver.Receive;
                if (invoke)
                {
                    route.Direct();
                }
            }
            return new Probe(
                new[]
                {
                    new WeakReference(route),
                    new WeakReference(handler),
                    new WeakReference(token),
                    new WeakReference(receiver),
                },
                route.Keyed.Count + (route.Direct == null ? 0 : 1),
                receiver.Calls
            );
        }

        internal sealed class Probe
        {
            internal readonly WeakReference[] Graph;
            internal readonly int StoredCallbacks;
            internal readonly int Calls;

            internal Probe(WeakReference[] graph, int storedCallbacks, int calls)
            {
                Graph = graph;
                StoredCallbacks = storedCallbacks;
                Calls = calls;
            }
        }

        private sealed class PlainRoute
        {
            internal readonly Dictionary<int, Action> Keyed = new();
            internal Action Direct;
        }

        private sealed class PlainHandler
        {
            internal readonly PlainRoute Route;

            internal PlainHandler(PlainRoute route) => Route = route;
        }

        private sealed class PlainToken
        {
            internal readonly PlainRoute Route;
            internal readonly PlainHandler Handler;

            internal PlainToken(PlainRoute route, PlainHandler handler)
            {
                Route = route;
                Handler = handler;
            }
        }

        private sealed class PlainReceiver
        {
            internal readonly PlainToken Token;
            internal int Calls;

            internal PlainReceiver(PlainToken token) => Token = token;

            internal void Receive()
            {
                ++Calls;
                GC.KeepAlive(Token.Route);
                GC.KeepAlive(Token.Handler.Route);
            }
        }
    }
}
