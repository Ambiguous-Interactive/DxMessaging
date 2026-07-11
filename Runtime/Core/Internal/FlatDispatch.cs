namespace DxMessaging.Core.Internal
{
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using DxMessaging.Core;

    /// <summary>
    /// One fully-resolved dispatch entry for slots whose delegates do NOT
    /// receive the routing context (untargeted handle/post, and the
    /// context-keyed Default-variant targeted/broadcast slots, where the
    /// target/source is the routing key rather than a delegate parameter):
    /// the owning <see cref="MessageHandler"/> (for the single live
    /// <c>active</c> check) plus the final invocable delegate, resolved at
    /// snapshot-build time. Steady-state dispatch over an array of these is
    /// a single field read, one branch, and a direct delegate invocation per
    /// entry - no dispatch link, no generation guard, no per-priority
    /// dictionary lookup, and no type test.
    /// </summary>
    /// <typeparam name="TMessage">Concrete message type of the dispatch slot.</typeparam>
    internal readonly struct FlatDispatchEntry<TMessage>
        where TMessage : IMessage
    {
        public FlatDispatchEntry(
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
    /// One fully-resolved dispatch entry for the WithoutContext
    /// targeted/broadcast slots, whose delegates DO receive the routing
    /// context (target/source <see cref="InstanceId"/>) as a parameter.
    /// Mirrors <see cref="FlatDispatchEntry{TMessage}"/> with the
    /// context-carrying delegate shape.
    /// </summary>
    /// <typeparam name="TMessage">Concrete message type of the dispatch slot.</typeparam>
    internal readonly struct ContextFlatDispatchEntry<TMessage>
        where TMessage : IMessage
    {
        public ContextFlatDispatchEntry(
            MessageHandler handler,
            MessageHandler.FastHandlerWithContext<TMessage> invoker
        )
        {
            this.handler = handler;
            this.invoker = invoker;
        }

        public readonly MessageHandler handler;
        public readonly MessageHandler.FastHandlerWithContext<TMessage> invoker;
    }

    /// <summary>
    /// Non-generic erasure base so the non-generic
    /// <c>MessageBus.DispatchSnapshot</c> can carry and release a typed flat
    /// entry array without knowing the closed message type (or the entry
    /// shape). The snapshot's pooled-release path calls <see cref="Release"/>
    /// exactly once per snapshot teardown; the typed holder returns its array
    /// to the per-closed-generic pool.
    /// </summary>
    internal abstract class FlatDispatchArray
    {
        internal abstract int Count { get; }

        /// <summary>
        /// Physical length of the currently rented entry array. Exposed only for internal
        /// storage benchmarks; dispatch continues to use <see cref="Count"/> as its bound.
        /// </summary>
        internal abstract int Capacity { get; }

        /// <summary>
        /// Number of released holders currently retained by this holder shape's pool. Released
        /// holders have already returned their rented arrays and hold <c>Array.Empty</c>,
        /// so this is empty-holder pool topology, not retained array-memory evidence. Array-pool
        /// retained bytes require external memory measurement. This benchmark telemetry does not
        /// participate in rent/release decisions.
        /// </summary>
        internal abstract int EmptyHolderPoolCount { get; }

        internal abstract void Release();
    }

    /// <summary>
    /// Shared pooled-holder implementation for flat dispatch arrays: a pooled
    /// entry array (rented from <see cref="ArrayPool{T}"/>) plus a small
    /// per-closed-generic holder stack so registration-churn rebuilds
    /// allocate nothing in steady state. Concrete holders
    /// (<see cref="FlatDispatch{TMessage}"/>,
    /// <see cref="ContextFlatDispatch{TMessage}"/>) only pick the entry
    /// shape; all lifecycle logic lives here so the pool/lock/cap/released
    /// pattern exists exactly once.
    /// </summary>
    /// <remarks>
    /// Lifecycle mirrors the snapshot that owns the holder: the array is
    /// frozen for the duration of any emission that acquired it (mutations
    /// mark the owning DispatchState dirty and are observed by the NEXT
    /// emission's rebuild), and it is released back to the pool only through
    /// <c>DispatchSnapshot.Release()</c>. The <c>_released</c> flag guards
    /// the holder against double-release (which would seat the same holder
    /// in the pool twice and corrupt later rents): DEBUG builds assert,
    /// release builds no-op the second call.
    /// </remarks>
    /// <typeparam name="TEntry">Resolved entry struct stored in the array.</typeparam>
    /// <typeparam name="THolder">Concrete holder type (CRTP, for typed pooling).</typeparam>
    internal abstract class PooledFlatDispatch<TEntry, THolder> : FlatDispatchArray
        where THolder : PooledFlatDispatch<TEntry, THolder>, new()
    {
        private static readonly ArrayPool<TEntry> EntryPool = ArrayPool<TEntry>.Shared;

        // Cold-path pool (rebuild/teardown only); the lock is uncontended in
        // practice but keeps the holder pool safe if multiple buses are ever
        // driven from different threads.
        private static readonly Stack<THolder> HolderPool = new();
        private static readonly object HolderPoolLock = new();
        private const int MaxRetainedHolders = 64;

        internal TEntry[] entries = Array.Empty<TEntry>();
        internal int count;

        internal override int Count => count;

        internal override int Capacity => entries.Length;

        internal override int EmptyHolderPoolCount
        {
            get
            {
                lock (HolderPoolLock)
                {
                    return HolderPool.Count;
                }
            }
        }

        // True while the holder is parked in (or eligible for) the pool;
        // false while it is owned by a live DispatchSnapshot. Guards the
        // rent/release lifecycle against double-release and rent-of-live.
        private bool _released = true;

        internal static THolder Rent(int capacity)
        {
            THolder holder = null;
            lock (HolderPoolLock)
            {
                if (0 < HolderPool.Count)
                {
                    holder = HolderPool.Pop();
                }
            }

            holder ??= new THolder();
            System.Diagnostics.Debug.Assert(
                holder._released,
                "PooledFlatDispatch.Rent returned a holder that is still owned by a live "
                    + "snapshot; a Release() was skipped or the pool was corrupted."
            );
            holder._released = false;
            holder.entries = 0 < capacity ? EntryPool.Rent(capacity) : Array.Empty<TEntry>();
            holder.count = 0;
            return holder;
        }

        internal sealed override void Release()
        {
            if (_released)
            {
                System.Diagnostics.Debug.Assert(
                    false,
                    "PooledFlatDispatch.Release called twice on the same holder; the owning "
                        + "DispatchSnapshot must release its flat array exactly once."
                );
                return;
            }

            _released = true;
            TEntry[] localEntries = entries;
            int localCount = count;
            entries = Array.Empty<TEntry>();
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
                    HolderPool.Push((THolder)this);
                }
            }
        }
    }

    /// <summary>
    /// Pooled flat array of resolved entries for one
    /// (bus, message-type[, context], phase) dispatch snapshot whose
    /// delegates take only the message: untargeted handle/post and the
    /// context-keyed (Default variant) targeted/broadcast handle/post slots.
    /// Built at snapshot-build time by walking the bus priority buckets
    /// ascending, then each bucket's MessageHandlers in bus insertion order,
    /// then each handler's fast entries followed by its default entries
    /// (both in first-registration order), so a plain forward iteration
    /// reproduces the documented dispatch order exactly.
    /// </summary>
    /// <typeparam name="TMessage">Concrete message type of the dispatch slot.</typeparam>
    internal sealed class FlatDispatch<TMessage>
        : PooledFlatDispatch<FlatDispatchEntry<TMessage>, FlatDispatch<TMessage>>
        where TMessage : IMessage { }

    /// <summary>
    /// Pooled flat array of resolved entries for one (bus, message-type,
    /// phase) dispatch snapshot of a WithoutContext targeted/broadcast slot,
    /// whose delegates receive the routing <see cref="InstanceId"/> alongside
    /// the message. Build order matches <see cref="FlatDispatch{TMessage}"/>.
    /// </summary>
    /// <typeparam name="TMessage">Concrete message type of the dispatch slot.</typeparam>
    internal sealed class ContextFlatDispatch<TMessage>
        : PooledFlatDispatch<ContextFlatDispatchEntry<TMessage>, ContextFlatDispatch<TMessage>>
        where TMessage : IMessage { }
}
