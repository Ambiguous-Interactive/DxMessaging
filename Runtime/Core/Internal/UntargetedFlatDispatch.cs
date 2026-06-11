namespace DxMessaging.Core.Internal
{
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using DxMessaging.Core;

    /// <summary>
    /// One fully-resolved untargeted dispatch entry: the owning
    /// <see cref="MessageHandler"/> (for the single live <c>active</c> check)
    /// plus the final invocable delegate, resolved at snapshot-build time.
    /// Steady-state dispatch over an array of these is a single field read,
    /// one branch, and a direct delegate invocation per entry - no dispatch
    /// link, no generation guard, no per-priority dictionary lookup, and no
    /// type test.
    /// </summary>
    /// <typeparam name="TMessage">Concrete message type of the dispatch slot.</typeparam>
    internal readonly struct UntargetedFlatEntry<TMessage>
        where TMessage : IMessage
    {
        public UntargetedFlatEntry(
            MessageHandler handler,
            MessageHandler.FastHandler<TMessage> invoker
        )
        {
            this.handler = handler;
            this.invoker = invoker;
        }

        public readonly MessageHandler handler;
        public readonly MessageHandler.FastHandler<TMessage> invoker;
    }

    /// <summary>
    /// Non-generic erasure base so the non-generic
    /// <c>MessageBus.DispatchSnapshot</c> can carry and release a typed flat
    /// entry array without knowing the closed message type. The snapshot's
    /// pooled-release path calls <see cref="Release"/> exactly once per
    /// snapshot teardown; the typed override returns its array to the
    /// per-closed-generic pool.
    /// </summary>
    internal abstract class FlatDispatchArray
    {
        internal abstract void Release();
    }

    /// <summary>
    /// Pooled flat array of resolved untargeted entries for one
    /// (bus, message-type, phase) dispatch snapshot. Built at snapshot-build
    /// time by walking the bus priority buckets ascending, then each bucket's
    /// MessageHandlers in bus insertion order, then each handler's fast
    /// entries followed by its default entries (both in first-registration
    /// order), so a plain forward iteration reproduces the documented
    /// dispatch order exactly.
    /// </summary>
    /// <remarks>
    /// Lifecycle mirrors the snapshot that owns it: the array is frozen for
    /// the duration of any emission that acquired it (mutations mark the
    /// owning DispatchState dirty and are observed by the NEXT emission's
    /// rebuild), and it is released back to the pool only through
    /// <c>DispatchSnapshot.Release()</c>. Holder instances are recycled
    /// through a small per-closed-generic stack so registration churn
    /// rebuilds allocate nothing in steady state.
    /// </remarks>
    /// <typeparam name="TMessage">Concrete message type of the dispatch slot.</typeparam>
    internal sealed class UntargetedFlatDispatch<TMessage> : FlatDispatchArray
        where TMessage : IMessage
    {
        private static readonly ArrayPool<UntargetedFlatEntry<TMessage>> EntryPool = ArrayPool<
            UntargetedFlatEntry<TMessage>
        >.Shared;

        // Cold-path pool (rebuild/teardown only); the lock is uncontended in
        // practice but keeps the holder pool safe if multiple buses are ever
        // driven from different threads.
        private static readonly Stack<UntargetedFlatDispatch<TMessage>> HolderPool = new();
        private static readonly object HolderPoolLock = new();
        private const int MaxRetainedHolders = 64;

        internal UntargetedFlatEntry<TMessage>[] entries = Array.Empty<
            UntargetedFlatEntry<TMessage>
        >();
        internal int count;

        private UntargetedFlatDispatch() { }

        internal static UntargetedFlatDispatch<TMessage> Rent(int capacity)
        {
            UntargetedFlatDispatch<TMessage> holder = null;
            lock (HolderPoolLock)
            {
                if (0 < HolderPool.Count)
                {
                    holder = HolderPool.Pop();
                }
            }

            holder ??= new UntargetedFlatDispatch<TMessage>();
            holder.entries =
                0 < capacity
                    ? EntryPool.Rent(capacity)
                    : Array.Empty<UntargetedFlatEntry<TMessage>>();
            holder.count = 0;
            return holder;
        }

        internal override void Release()
        {
            UntargetedFlatEntry<TMessage>[] localEntries = entries;
            int localCount = count;
            entries = Array.Empty<UntargetedFlatEntry<TMessage>>();
            count = 0;
            if (0 < localEntries.Length)
            {
                Array.Clear(localEntries, 0, localCount);
                EntryPool.Return(localEntries);
            }

            lock (HolderPoolLock)
            {
                if (HolderPool.Count < MaxRetainedHolders)
                {
                    HolderPool.Push(this);
                }
            }
        }
    }
}
