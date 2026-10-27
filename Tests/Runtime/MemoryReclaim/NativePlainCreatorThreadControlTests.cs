namespace DxMessaging.Tests.Runtime.MemoryReclaim
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using NUnit.Framework;

    /// <summary>Compares plain callback graphs created on the observer and an exited worker.</summary>
    /// <remarks>
    /// 2026-10-27: earlier plain graphs survive some synchronous Mono observations without
    /// messaging objects. Compare creator threads with identical graph factories and collection
    /// rounds. A difference establishes an association, not a production root or collector cause.
    /// </remarks>
    [TestFixture]
    [Category("MemoryReclaim")]
#if !UNITY_2021_3_OR_NEWER
    [NonParallelizable]
#endif
    public sealed class NativePlainCreatorThreadControlTests
    {
        [Test]
        public void SynchronousWindowObservesPlainCreatorThread(
            [Values(false, true)] bool dictionary,
            [Values(false, true)] bool invoke,
            [Values(false, true)] bool negativeFactoryLast,
            [Values(false, true)] bool worker
        )
        {
            object positive = new();
            WeakReference positiveReference = new(positive);
            Construction state = new(dictionary, invoke, negativeFactoryLast);
            int observerThread = Thread.CurrentThread.ManagedThreadId;
            Thread owned = null;
            bool started = false;
            try
            {
                if (worker)
                {
                    owned = new Thread(state.Create) { IsBackground = true };
                    owned.Start();
                    started = true;
                    owned.Join();
                    Assert.That(owned.IsAlive, Is.False, "Owned creator exits before collection");
                    Assert.That(
                        state.CreatorThread,
                        Is.Not.EqualTo(observerThread),
                        "Owned creator uses a distinct thread"
                    );
                }
                else
                {
                    state.Create();
                    Assert.That(
                        state.CreatorThread,
                        Is.EqualTo(observerThread),
                        "Caller construction uses the observer thread"
                    );
                }
                Assert.That(state.Error, Is.Null, "Actual creator exception invalidates this cell");
                Assert.That(
                    0 < state.CreatorThread,
                    Is.True,
                    "Creator records a positive actual thread identifier"
                );
                Assert.That(state.Probe, Is.Not.Null, "Creator transfers the weak-reference probe");
                Assert.That(state.Negative, Is.Not.Null, "Creator transfers the negative control");
                for (int index = 0; index < 3; ++index)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
                bool negativeAlive = state.Negative.IsAlive;
                bool positiveAlive = positiveReference.IsAlive;
                bool[] alive = new bool[state.Probe.Graph.Length];
                for (int index = 0; index < alive.Length; ++index)
                {
                    alive[index] = state.Probe.Graph[index].IsAlive;
                }
                string context =
                    $"dictionary={dictionary}, invoke={invoke}, negativeFactoryLast={negativeFactoryLast}, "
                    + $"worker={worker}, observerThread={observerThread}, creatorThread={state.CreatorThread}, "
                    + $"creatorExited={(!worker || !owned.IsAlive)}, "
                    + $"negativeAlive={negativeAlive}, positiveAlive={positiveAlive}, "
                    + $"storedCallbacks={state.Probe.StoredCallbacks}, calls={state.Probe.Calls}, "
                    + $"graph=[{string.Join(", ", alive)}]";
                TestContext.Out.WriteLine("PLAIN_CREATOR_THREAD " + context);
                Assert.That(negativeAlive, Is.False, context + ": independent negative control");
                Assert.That(positiveAlive, Is.True, context + ": retained positive control");
                Assert.That(
                    state.Probe.StoredCallbacks,
                    Is.EqualTo(1),
                    context + ": real callback storage"
                );
                Assert.That(
                    state.Probe.Calls,
                    Is.EqualTo(invoke ? 1 : 0),
                    context + ": actual invocation"
                );
                Assert.That(alive, Is.EqualTo(new bool[4]), context + ": unreachable plain cycle");
            }
            finally
            {
                if (started && owned.IsAlive)
                {
                    owned.Join();
                }
                GC.KeepAlive(positive);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference CreateNegativeControl() => new(new object());

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Probe CreateProbe(bool dictionary, bool invoke)
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

        private sealed class Construction
        {
            private readonly bool dictionary;
            private readonly bool invoke;
            private readonly bool negativeFactoryLast;
            internal Probe Probe;
            internal WeakReference Negative;
            internal Exception Error;
            internal int CreatorThread;

            internal Construction(bool dictionary, bool invoke, bool negativeFactoryLast)
            {
                this.dictionary = dictionary;
                this.invoke = invoke;
                this.negativeFactoryLast = negativeFactoryLast;
            }

            internal void Create()
            {
                try
                {
                    CreatorThread = Thread.CurrentThread.ManagedThreadId;
                    if (negativeFactoryLast)
                    {
                        Probe = CreateProbe(dictionary, invoke);
                        Negative = CreateNegativeControl();
                    }
                    else
                    {
                        Negative = CreateNegativeControl();
                        Probe = CreateProbe(dictionary, invoke);
                    }
                }
                catch (Exception error)
                {
                    Error = error;
                }
            }
        }

        private sealed class Probe
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
