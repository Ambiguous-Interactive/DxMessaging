namespace DxMessaging.Core.MessageBus
{
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq.Expressions;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using DataStructure;
    using Diagnostics;
    using DxMessaging.Core;
    using DxMessaging.Core.Internal;
    using Extensions;
    using Helper;
    using Internal;
    using Messages;
    using Pooling;
    using static IMessageBus;
#if UNITY_2021_3_OR_NEWER
    using Configuration;
    using UnityEngine;
#endif

    /// <summary>
    /// Instanced MessageBus for use cases where you want distinct islands of MessageBuses.
    /// </summary>
    public sealed class MessageBus : IMessageBus
    {
        private long _emissionId;

        // The emission id of the dispatch pass currently executing on this
        // bus. Assigned alongside each _emissionId increment and
        // saved/restored by DispatchLease, so when a nested (reentrant)
        // emission completes, the OUTER emission resumes reading ITS OWN id
        // rather than the bumped live counter. Every per-emission freeze key
        // (handler-side GetOrAddNewHandlerStack via dispatch links / the
        // EmissionId property, global prefreeze stamps, snapshot
        // acquisition) compares against
        // this scoped value; using the live counter instead would make the
        // outer emission's post-nested cache reads look like a NEW emission
        // and rebuild (dropping mid-emission-deregistered handlers /
        // surfacing mid-emission registrations), violating the documented
        // frozen-snapshot contract. Outside any dispatch the two values are
        // always equal.
        private long _scopedEmissionId;

        /// <summary>
        /// The id of the emission currently being dispatched when read from
        /// inside a handler/interceptor/post-processor (nested emissions get
        /// their own id and the outer id is restored when they complete);
        /// outside of dispatch, the id of the most recent emission.
        /// </summary>
        public long EmissionId => 0 < _dispatchDepth ? _scopedEmissionId : _emissionId;
        internal long TickCounter => _tickCounter;

        internal readonly struct PrefreezeDescriptor
        {
            public PrefreezeDescriptor(byte kind, int priority)
            {
                this.kind = kind;
                this.priority = priority;
            }

            public static readonly PrefreezeDescriptor Empty = new PrefreezeDescriptor(0, 0);
            public readonly byte kind;
            public readonly int priority;
        }

        private const byte PrefreezeKindNone = 0;
        private const byte PrefreezeKindTargetedWithoutTargetingHandlers = 1;
        private const byte PrefreezeKindBroadcastWithoutSourceHandlers = 2;
        private const byte PrefreezeKindGlobalUntargetedHandlers = 3;
        private const byte PrefreezeKindGlobalTargetedHandlers = 4;
        private const byte PrefreezeKindGlobalBroadcastHandlers = 5;
        private const long DefaultIdleEvictionTicks = 30;
        private const double DefaultEvictionTickIntervalSeconds = 5d;
        internal const int SweepGateSampleSize = 16;
        private const long SweepGateMask = SweepGateSampleSize - 1;

        private static readonly SlotKey UntargetedHandleSlot = new SlotKey(
            DispatchKind.Untargeted,
            DispatchPhase.Handle,
            DispatchVariant.Default
        );
        private static readonly SlotKey UntargetedPostSlot = new SlotKey(
            DispatchKind.Untargeted,
            DispatchPhase.PostProcess,
            DispatchVariant.Default
        );
        private static readonly SlotKey TargetedHandleSlot = new SlotKey(
            DispatchKind.Targeted,
            DispatchPhase.Handle,
            DispatchVariant.Default
        );
        private static readonly SlotKey TargetedWithoutContextHandleSlot = new SlotKey(
            DispatchKind.Targeted,
            DispatchPhase.Handle,
            DispatchVariant.WithoutContext
        );
        private static readonly SlotKey TargetedPostSlot = new SlotKey(
            DispatchKind.Targeted,
            DispatchPhase.PostProcess,
            DispatchVariant.Default
        );
        private static readonly SlotKey TargetedWithoutContextPostSlot = new SlotKey(
            DispatchKind.Targeted,
            DispatchPhase.PostProcess,
            DispatchVariant.WithoutContext
        );
        private static readonly SlotKey BroadcastHandleSlot = new SlotKey(
            DispatchKind.Broadcast,
            DispatchPhase.Handle,
            DispatchVariant.Default
        );
        private static readonly SlotKey BroadcastWithoutContextHandleSlot = new SlotKey(
            DispatchKind.Broadcast,
            DispatchPhase.Handle,
            DispatchVariant.WithoutContext
        );
        private static readonly SlotKey BroadcastPostSlot = new SlotKey(
            DispatchKind.Broadcast,
            DispatchPhase.PostProcess,
            DispatchVariant.Default
        );
        private static readonly SlotKey BroadcastWithoutContextPostSlot = new SlotKey(
            DispatchKind.Broadcast,
            DispatchPhase.PostProcess,
            DispatchVariant.WithoutContext
        );
        internal const int ExpectedMessageCacheFieldCount = 5;

        private static readonly ISweepable[] SweepableTypeCacheRegistry =
        {
            new SweepableTypeCache(
                nameof(_scalarSinks),
                typeof(MessageCache<HandlerCache<int, HandlerCache>>[]),
                static (bus, force) => bus.SweepDirtyScalarTypeSlots(force)
            ),
            new SweepableTypeCache(
                nameof(_contextSinks),
                typeof(MessageCache<Dictionary<InstanceId, HandlerCache<int, HandlerCache>>>[]),
                static (bus, force) => bus.SweepDirtyTargetSlots(force)
            ),
            new SweepableTypeCache(
                nameof(_untargetedInterceptsByType),
                typeof(MessageCache<InterceptorCache<object>>),
                static (bus, force) =>
                    bus.SweepDirtyInterceptorTypeSlots(bus._untargetedInterceptsByType, force)
            ),
            new SweepableTypeCache(
                nameof(_targetedInterceptsByType),
                typeof(MessageCache<InterceptorCache<object>>),
                static (bus, force) =>
                    bus.SweepDirtyInterceptorTypeSlots(bus._targetedInterceptsByType, force)
            ),
            new SweepableTypeCache(
                nameof(_broadcastInterceptsByType),
                typeof(MessageCache<InterceptorCache<object>>),
                static (bus, force) =>
                    bus.SweepDirtyInterceptorTypeSlots(bus._broadcastInterceptsByType, force)
            ),
        };

        internal static IReadOnlyList<ISweepable> SweepableTypeCaches => SweepableTypeCacheRegistry;

        private static readonly ArrayPool<DispatchBucket> DispatchBucketPool =
            ArrayPool<DispatchBucket>.Shared;
        private static readonly ArrayPool<DispatchEntry> DispatchEntryPool =
            ArrayPool<DispatchEntry>.Shared;

        private static CollectionPool<
            Dictionary<InstanceId, HandlerCache<int, HandlerCache>>
        > ContextHandlerByTargetDicts => ContextHandlerByTargetDictPoolHolder.Instance;

        private static class ContextHandlerByTargetDictPoolHolder
        {
            public static readonly CollectionPool<
                Dictionary<InstanceId, HandlerCache<int, HandlerCache>>
            > Instance = new(
                maxRetained: 512,
                useLru: true,
                factory: static () => new Dictionary<InstanceId, HandlerCache<int, HandlerCache>>(),
                onRecycled: static dict => dict.Clear()
            );
        }

        internal static int ResetStaticPools()
        {
            return ContextHandlerByTargetDicts.Trim(0);
        }

        internal readonly struct DispatchEntry
        {
            public DispatchEntry(
                MessageHandler handler,
                object dispatch,
                PrefreezeDescriptor prefreeze
            )
            {
                this.handler = handler;
                this.dispatch = dispatch;
                this.prefreeze = prefreeze;
            }

            public readonly MessageHandler handler;
            public readonly object dispatch;
            public readonly PrefreezeDescriptor prefreeze;
        }

        internal struct DispatchBucket
        {
            public DispatchBucket(
                int priority,
                DispatchEntry[] entries,
                int entryCount,
                bool pooledEntries
            )
            {
                this.priority = priority;
                this.entries = entries;
                this.entryCount = entryCount;
                this.pooledEntries = pooledEntries;
            }

            public readonly int priority;
            public DispatchEntry[] entries;
            public int entryCount;
            public bool pooledEntries;

            public static DispatchBucket CreateEmpty(int priority)
            {
                return new DispatchBucket(priority, Array.Empty<DispatchEntry>(), 0, false);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void ReleaseEntries()
            {
                if (!pooledEntries || entries == null)
                {
                    return;
                }

                Array.Clear(entries, 0, entryCount);
                DispatchEntryPool.Return(entries);
                entries = Array.Empty<DispatchEntry>();
                entryCount = 0;
                pooledEntries = false;
            }
        }

        internal sealed class DispatchSnapshot
        {
            public static readonly DispatchSnapshot Empty = new DispatchSnapshot(
                Array.Empty<DispatchBucket>(),
                0,
                false
            );

            public DispatchSnapshot(DispatchBucket[] buckets, int count, bool pooled)
            {
                this.buckets = buckets;
                bucketCount = count;
                _pooled = pooled;
            }

            public DispatchBucket[] buckets;
            public int bucketCount;

            // Resolved flat dispatch entries for every flattened slot kind:
            // untargeted handle/post, targeted/broadcast Default handle/post
            // (context-keyed; one snapshot per context), and the
            // targeted/broadcast WithoutContext handle/post slots. Holds a
            // closed FlatDispatch<TMessage> (message-only delegate shape) or
            // ContextFlatDispatch<TMessage> (WithoutContext shape, delegates
            // receive the routing InstanceId); the dispatch site reinterprets
            // it via DxUnsafe.As using the emission's TMessage and the slot's
            // known shape. Owned by this snapshot: released exactly once in
            // Release().
            public FlatDispatchArray flat;
            private bool _pooled;

            public bool IsEmpty => bucketCount == 0;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Release()
            {
                if (!_pooled || buckets == null)
                {
                    return;
                }

                flat?.Release();
                flat = null;

                for (int i = 0; i < bucketCount; ++i)
                {
                    buckets[i].ReleaseEntries();
                }

                Array.Clear(buckets, 0, bucketCount);
                DispatchBucketPool.Return(buckets);
                buckets = Array.Empty<DispatchBucket>();
                bucketCount = 0;
                _pooled = false;
            }
        }

        internal sealed class DispatchState
        {
            public DispatchSnapshot active = DispatchSnapshot.Empty;
            public DispatchSnapshot pending = DispatchSnapshot.Empty;
            public bool hasPending;
            public bool pendingDirty;
            public long snapshotEmissionId = -1;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Reset()
            {
                ReleaseSnapshot(ref active);
                ReleaseSnapshot(ref pending);
                hasPending = false;
                pendingDirty = false;
                snapshotEmissionId = -1;
            }
        }

        private sealed class HandlerCache<TKey, TValue>
        {
            public readonly Dictionary<TKey, TValue> handlers = new();
            public readonly List<TKey> order = new();
            public readonly List<KeyValuePair<TKey, TValue>> cache = new();
            public long version;
            public long lastSeenVersion = -1;
            public long lastSeenEmissionId = -1;
            public long lastTouchTicks;
            public DispatchState dispatchState;

            /// <summary>
            /// Clears all cached handler references and resets the version tracking metadata.
            /// </summary>
            public void Clear()
            {
                // LEGACY: version reset semantics. Bus-side deregistration closures use
                // captured cache identity and reset generations, so monotonic versioning
                // is handled by sweep-driven slot reset paths.
                handlers.Clear();
                order.Clear();
                cache.Clear();
                version = 0;
                lastSeenVersion = -1;
                lastSeenEmissionId = -1;
                dispatchState?.Reset();
                dispatchState = null;
            }
        }

        private sealed class InterceptorCache<TValue>
        {
            public readonly SortedList<int, List<TValue>> handlers = new();
            public long lastSeenEmissionId = -1;
            public long lastTouchTicks;

            public void Clear()
            {
                handlers.Clear();
                lastSeenEmissionId = -1;
                lastTouchTicks = 0;
            }
        }

        private sealed class SweepableTypeCache : ISweepable
        {
            private readonly Func<MessageBus, bool, int> _sweep;

            public SweepableTypeCache(
                string storageFieldName,
                Type storageFieldType,
                Func<MessageBus, bool, int> sweep
            )
            {
                StorageFieldName = storageFieldName;
                StorageFieldType = storageFieldType;
                _sweep = sweep;
            }

            public string StorageFieldName { get; }
            public Type StorageFieldType { get; }

            public int Sweep(MessageBus bus, bool force)
            {
                if (bus == null)
                {
                    throw new ArgumentNullException(nameof(bus));
                }

                return _sweep(bus, force);
            }
        }

        private readonly struct DispatchLease : IDisposable
        {
            private readonly MessageBus _bus;

            // The scoped emission id of the emission this lease is nested
            // inside (or the idle value at the outermost lease). Restored on
            // Dispose so that when a nested (reentrant) emission completes,
            // the outer emission's remaining dispatch reads its OWN emission
            // id again - the per-emission freeze keys (GetOrAdd*HandlerStack,
            // prefreeze stamps) rely on this to keep the outer emission's
            // frozen caches frozen across nested emissions.
            private readonly long _previousScopedEmissionId;

            public DispatchLease(MessageBus bus)
            {
                _bus = bus;
                _previousScopedEmissionId = bus._scopedEmissionId;
                bus._dispatchDepth++;
            }

            public void Dispose()
            {
                MessageBus bus = _bus;
                bus._scopedEmissionId = _previousScopedEmissionId;
                bus._dispatchDepth--;
                if (bus._dispatchDepth == 0 && bus._hasDeferredResetTeardown)
                {
                    bus.FlushDeferredResetTeardown();
                }
            }
        }

        private sealed class HandlerCache
        {
            public readonly Dictionary<MessageHandler, int> handlers = new();

            // MessageHandler keys in first-registration order. Dictionary
            // enumeration order is NOT stable across Remove/Add churn (.NET
            // reuses freed slots LIFO), so dispatch snapshots are built from
            // this list instead of from <see cref="handlers"/> to honor the
            // documented "same priority uses registration order" contract
            // across components. Invariants: contains exactly the keys of
            // <see cref="handlers"/>; a key is appended on its FIRST
            // registration only (refcount increments do not move it) and
            // removed when its refcount drops to zero. Mirrors the
            // MessageHandler-side HandlerActionCache.insertionOrder design.
            public readonly List<MessageHandler> insertionOrder = new();
            public readonly List<MessageHandler> cache = new();
            public long version;
            public long lastSeenVersion = -1;
            public long lastSeenEmissionId = -1;

            /// <summary>
            /// Clears all cached handler references and resets the version tracking metadata.
            /// </summary>
            public void Clear()
            {
                // LEGACY: version reset semantics. Bus-side deregistration closures use
                // captured cache identity and reset generations, so monotonic versioning
                // is handled by sweep-driven slot reset paths.
                handlers.Clear();
                insertionOrder.Clear();
                cache.Clear();
                version = 0;
                lastSeenVersion = -1;
                lastSeenEmissionId = -1;
            }
        }

        public int RegisteredTargeted
        {
            get
            {
                int count = 0;
                count += SumTargetedSinks(_contextSinks[BusContextIndex.TargetedHandleDefault]);
                foreach (
                    HandlerCache<int, HandlerCache> entry in _scalarSinks[
                        BusSinkIndex.TargetedHandleWithoutContext
                    ]
                )
                {
                    count += entry?.handlers?.Count ?? 0;
                }

                return count;
            }
        }

        public int RegisteredGlobalSequentialIndex { get; } = GenerateNewGlobalSequentialIndex();

        public int OccupiedTypeSlots
        {
            get
            {
                int count = 0;
                for (int i = 0; i < _scalarSinks.Length; ++i)
                {
                    MessageCache<HandlerCache<int, HandlerCache>> sink = _scalarSinks[i];
                    if (sink == null)
                    {
                        continue;
                    }

                    foreach (HandlerCache<int, HandlerCache> _ in sink)
                    {
                        count++;
                    }
                }

                for (int i = 0; i < _contextSinks.Length; ++i)
                {
                    foreach (
                        Dictionary<InstanceId, HandlerCache<int, HandlerCache>> _ in _contextSinks[
                            i
                        ]
                    )
                    {
                        count++;
                    }
                }

                return count + OccupiedInterceptorTypeSlots + CountDirtyEmptyTypedHandlerSlots();
            }
        }

        private int OccupiedInterceptorTypeSlots
        {
            get
            {
                return CountOccupiedInterceptorTypeSlots(_untargetedInterceptsByType)
                    + CountOccupiedInterceptorTypeSlots(_targetedInterceptsByType)
                    + CountOccupiedInterceptorTypeSlots(_broadcastInterceptsByType);
            }
        }

        public int OccupiedTargetSlots
        {
            get
            {
                int count = 0;
                for (int i = 0; i < _contextSinks.Length; ++i)
                {
                    foreach (
                        Dictionary<
                            InstanceId,
                            HandlerCache<int, HandlerCache>
                        > byTarget in _contextSinks[i]
                    )
                    {
                        count += byTarget?.Count ?? 0;
                    }
                }

                return count;
            }
        }

        public int RegisteredBroadcast
        {
            get
            {
                int count = 0;
                count += SumTargetedSinks(_contextSinks[BusContextIndex.BroadcastHandleDefault]);
                foreach (
                    HandlerCache<int, HandlerCache> entry in _scalarSinks[
                        BusSinkIndex.BroadcastHandleWithoutContext
                    ]
                )
                {
                    count += entry?.handlers?.Count ?? 0;
                }

                return count;
            }
        }

        public int RegisteredUntargeted
        {
            get
            {
                int count = 0;
                foreach (
                    HandlerCache<int, HandlerCache> entry in _scalarSinks[
                        BusSinkIndex.UntargetedHandleDefault
                    ]
                )
                {
                    count += entry?.handlers?.Count ?? 0;
                }

                return count;
            }
        }

        public int RegisteredInterceptors
        {
            get
            {
                int count = 0;
                count += SumInterceptorCache(_untargetedInterceptsByType);
                count += SumInterceptorCache(_targetedInterceptsByType);
                count += SumInterceptorCache(_broadcastInterceptsByType);
                return count;
            }
        }

        public int RegisteredPostProcessors
        {
            get
            {
                int count = 0;
                foreach (
                    HandlerCache<int, HandlerCache> entry in _scalarSinks[
                        BusSinkIndex.UntargetedPostProcessDefault
                    ]
                )
                {
                    count += entry?.handlers?.Count ?? 0;
                }
                count += SumTargetedSinks(
                    _contextSinks[BusContextIndex.TargetedPostProcessDefault]
                );
                count += SumTargetedSinks(
                    _contextSinks[BusContextIndex.BroadcastPostProcessDefault]
                );
                foreach (
                    HandlerCache<int, HandlerCache> entry in _scalarSinks[
                        BusSinkIndex.TargetedPostProcessWithoutContext
                    ]
                )
                {
                    count += entry?.handlers?.Count ?? 0;
                }
                foreach (
                    HandlerCache<int, HandlerCache> entry in _scalarSinks[
                        BusSinkIndex.BroadcastPostProcessWithoutContext
                    ]
                )
                {
                    count += entry?.handlers?.Count ?? 0;
                }
                return count;
            }
        }

        public int RegisteredGlobalAcceptAll => _globalSlots.sharedHandlers.Count;

        private static int SumInterceptorCache(MessageCache<InterceptorCache<object>> cache)
        {
            int count = 0;
            foreach (InterceptorCache<object> entry in cache)
            {
                if (entry == null)
                {
                    continue;
                }
                foreach (KeyValuePair<int, List<object>> bucket in entry.handlers)
                {
                    count += bucket.Value?.Count ?? 0;
                }
            }
            return count;
        }

        private static int SumTargetedSinks(
            MessageCache<Dictionary<InstanceId, HandlerCache<int, HandlerCache>>> cache
        )
        {
            int count = 0;
            foreach (Dictionary<InstanceId, HandlerCache<int, HandlerCache>> entry in cache)
            {
                if (entry == null)
                {
                    continue;
                }
                foreach (KeyValuePair<InstanceId, HandlerCache<int, HandlerCache>> kvp in entry)
                {
                    count += kvp.Value?.handlers?.Count ?? 0;
                }
            }
            return count;
        }

        public bool DiagnosticsMode
        {
            get => _diagnosticsMode;
            set => _diagnosticsMode = value;
        }

        private static readonly Type MessageBusType = typeof(MessageBus);

        private static readonly List<Expression> ArgumentExpressionsCache = new();

        private const BindingFlags ReflectionHelperBindingFlags =
            BindingFlags.Static | BindingFlags.NonPublic;
        private const BindingFlags ReflexiveMethodBindingFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private delegate void FastUntargetedBroadcast<T>(ref T message)
            where T : IUntargetedMessage;
        private delegate void FastTargetedBroadcast<T>(ref InstanceId target, ref T message)
            where T : ITargetedMessage;
        private delegate void FastSourcedBroadcast<T>(ref InstanceId target, ref T message)
            where T : IBroadcastMessage;

        private static bool RequiresAotUntypedDispatch
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
#if ENABLE_IL2CPP
                return true;
#else
                return false;
#endif
            }
        }

        private static readonly object AotBridgeRegistryLock = new();
        private static Dictionary<
            Type,
            Action<IMessageBus, IUntargetedMessage>
        > _aotUntargetedBroadcastsByType = new();
        private static Dictionary<
            Type,
            Action<IMessageBus, InstanceId, ITargetedMessage>
        > _aotTargetedBroadcastsByType = new();
        private static Dictionary<
            Type,
            Action<IMessageBus, InstanceId, IBroadcastMessage>
        > _aotSourcedBroadcastsByType = new();

        private static class AotBridgeState<T>
        {
            public static bool UntargetedRegistered;
            public static bool TargetedRegistered;
            public static bool SourcedRegistered;
        }

        public RegistrationLog Log => _log;

        // Storage trio for typed and global dispatch. _scalarSinks and
        // _contextSinks are SlotKey-indexed arrays of MessageCache (call sites
        // index by BusSinkIndex / BusContextIndex constants; reserved-null
        // entries are documented in BusSinkIndex.cs). _globalSlots is a single
        // BusGlobalSlot -- the global accept-all slot is single-cardinality, so
        // there is no array to index, but it is grouped here because it shares
        // the lifecycle of the typed sinks (cleared together in ResetState,
        // touched together by the eviction layer).
        private readonly MessageCache<HandlerCache<int, HandlerCache>>[] _scalarSinks =
            new MessageCache<HandlerCache<int, HandlerCache>>[BusSinkIndex.Length]
            {
                /* [0] UntargetedHandleDefault            */new(),
                /* [1] BroadcastHandleWithoutContext      */new(),
                /* [2] TargetedHandleWithoutContext       */new(),
                /* [3] UntargetedPostProcessDefault       */new(),
                /* [4] TargetedPostProcessWithoutContext  */new(),
                /* [5] BroadcastPostProcessWithoutContext */new(),
                /* [6] Reserved6                          */null,
                /* [7] Reserved7                          */null,
            };

        private readonly MessageCache<
            Dictionary<InstanceId, HandlerCache<int, HandlerCache>>
        >[] _contextSinks = new MessageCache<
            Dictionary<InstanceId, HandlerCache<int, HandlerCache>>
        >[BusContextIndex.Length]
        {
            /* [0] TargetedHandleDefault         */new(),
            /* [1] BroadcastHandleDefault        */new(),
            /* [2] TargetedPostProcessDefault    */new(),
            /* [3] BroadcastPostProcessDefault   */new(),
        };

        private readonly BusGlobalSlot _globalSlots = new();

        /// <summary>
        /// Constructs a <see cref="MessageBus"/> using the default <see cref="StopwatchClock"/>
        /// and runtime-settings provided eviction cadence. This is the only public constructor; DI
        /// containers that scan constructors reflectively (for example VContainer, which inspects
        /// both public and private constructors) must be configured with an explicit factory --
        /// see the integration helpers under <c>Runtime/Unity/Integrations</c>.
        /// </summary>
        public MessageBus()
            : this(StopwatchClock.Instance, DefaultIdleEvictionTicks, applyRuntimeSettings: true)
        { }

        /// <summary>
        /// Internal factory used by tests and integration assemblies to construct a
        /// <see cref="MessageBus"/> with an injected <see cref="IDxMessagingClock"/> and optional
        /// eviction overrides. Lives behind an <c>internal static</c> entry point so the public
        /// surface exposes only the parameterless constructor; this keeps reflection-based DI
        /// containers from latching onto a clock-taking overload they cannot satisfy.
        /// </summary>
        /// <param name="clock">Clock implementation. Must not be null.</param>
        /// <param name="idleEvictionTicks">Optional idle-eviction tick budget; falls back to <see cref="DefaultIdleEvictionTicks"/> when null.</param>
        /// <param name="evictionTickIntervalSeconds">Optional sweep cadence in seconds.</param>
        /// <param name="idleEvictionEnabled">Optional opt-out for idle eviction.</param>
        /// <param name="trimApiEnabled">Optional opt-out for the trim API.</param>
        /// <returns>Configured <see cref="MessageBus"/> instance.</returns>
        internal static MessageBus CreateForInternalUse(
            IDxMessagingClock clock,
            long? idleEvictionTicks = null,
            double? evictionTickIntervalSeconds = null,
            bool? idleEvictionEnabled = null,
            bool? trimApiEnabled = null
        )
        {
            if (clock == null)
            {
                throw new ArgumentNullException(nameof(clock));
            }

            long resolvedIdleEvictionTicks = idleEvictionTicks ?? DefaultIdleEvictionTicks;
            bool applyRuntimeSettings =
                idleEvictionTicks == null
                && evictionTickIntervalSeconds == null
                && idleEvictionEnabled == null
                && trimApiEnabled == null;

            MessageBus bus = new MessageBus(
                clock,
                resolvedIdleEvictionTicks,
                applyRuntimeSettings: applyRuntimeSettings
            );

            if (evictionTickIntervalSeconds.HasValue)
            {
                bus._evictionTickIntervalSeconds = Math.Max(0d, evictionTickIntervalSeconds.Value);
            }
            if (idleEvictionEnabled.HasValue)
            {
                bus._idleEvictionEnabled = idleEvictionEnabled.Value;
            }
            if (trimApiEnabled.HasValue)
            {
                bus._trimApiEnabled = trimApiEnabled.Value;
            }

            return bus;
        }

        private MessageBus(
            IDxMessagingClock clock,
            long idleEvictionTicks,
            bool applyRuntimeSettings
        )
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _idleEvictionTicks = Math.Max(0, idleEvictionTicks);
            _evictionTickIntervalSeconds = DefaultEvictionTickIntervalSeconds;
            _lastSweepSeconds = _clock.NowSeconds;
#if UNITY_2021_3_OR_NEWER
            RegisterForIdleSweeps(this);
            EnsureRuntimeSettingsSubscription();
            if (applyRuntimeSettings)
            {
                ApplyRuntimeSettings(DxMessagingRuntimeSettingsProvider.Current);
            }
#endif
            ValidateSinkArrays();
        }

#if UNITY_2021_3_OR_NEWER
        private static readonly List<WeakReference<MessageBus>> IdleSweepBuses = new();
        private static bool RuntimeSettingsSubscribed;

        private static void RegisterForIdleSweeps(MessageBus bus)
        {
            for (int i = IdleSweepBuses.Count - 1; i >= 0; --i)
            {
                if (!IdleSweepBuses[i].TryGetTarget(out MessageBus existing))
                {
                    IdleSweepBuses.RemoveAt(i);
                    continue;
                }
                if (ReferenceEquals(existing, bus))
                {
                    return;
                }
            }

            IdleSweepBuses.Add(new WeakReference<MessageBus>(bus));
        }

        private static void EnsureRuntimeSettingsSubscription()
        {
            if (RuntimeSettingsSubscribed)
            {
                return;
            }

            DxMessagingRuntimeSettings.SettingsChanged += HandleRuntimeSettingsChanged;
            RuntimeSettingsSubscribed = true;
        }

        private static void HandleRuntimeSettingsChanged(DxMessagingRuntimeSettings settings)
        {
            if (settings == null)
            {
                settings = DxMessagingRuntimeSettingsProvider.Current;
            }

            for (int i = IdleSweepBuses.Count - 1; i >= 0; --i)
            {
                if (IdleSweepBuses[i].TryGetTarget(out MessageBus bus))
                {
                    bus.ApplyRuntimeSettings(settings);
                    continue;
                }

                IdleSweepBuses.RemoveAt(i);
            }
        }

        internal static void SweepIdleBusesFromPlayerLoop()
        {
            for (int i = IdleSweepBuses.Count - 1; i >= 0; --i)
            {
                if (IdleSweepBuses[i].TryGetTarget(out MessageBus bus))
                {
                    bus.TrySweepIdle(advanceTickForIdleAging: true);
                    continue;
                }

                IdleSweepBuses.RemoveAt(i);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetIdleSweepRegistry()
        {
            DxMessagingRuntimeSettings.SettingsChanged -= HandleRuntimeSettingsChanged;
            IdleSweepBuses.Clear();
            RuntimeSettingsSubscribed = false;
            ResetStaticPools();
        }

        private void ApplyRuntimeSettings(DxMessagingRuntimeSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            DxPools.Configure(settings);
            ContextHandlerByTargetDicts.UseLru = settings.BufferUseLruEviction;
            ContextHandlerByTargetDicts.MaxRetained = settings.BufferMaxDistinctEntries;
            if (!settings.IsFallbackInstance)
            {
                IMessageBus.GlobalMessageBufferSize = Math.Max(0, settings.MessageBufferSize);
            }
            _emissionBuffer.Resize(Math.Max(0, IMessageBus.GlobalMessageBufferSize));
            _idleEvictionTicks = ComputeIdleEvictionTicks(settings.IdleEvictionSeconds);
            _evictionTickIntervalSeconds = Math.Max(0d, settings.EvictionTickIntervalSeconds);
            _idleEvictionEnabled = settings.EvictionEnabled;
            _trimApiEnabled = settings.EnableTrimApi;
        }
#endif

        private static long ComputeIdleEvictionTicks(float idleEvictionSeconds)
        {
            if (idleEvictionSeconds <= 0f)
            {
                return 0;
            }

            return (long)Math.Ceiling(idleEvictionSeconds);
        }

        [Conditional("DEBUG")]
        private void ValidateSinkArrays()
        {
            if (_scalarSinks.Length != BusSinkIndex.Length)
            {
                throw new InvalidOperationException(
                    $"_scalarSinks length is {_scalarSinks.Length} but BusSinkIndex.Length is {BusSinkIndex.Length}."
                );
            }
            if (_contextSinks.Length != BusContextIndex.Length)
            {
                throw new InvalidOperationException(
                    $"_contextSinks length is {_contextSinks.Length} but BusContextIndex.Length is {BusContextIndex.Length}."
                );
            }
            if (_scalarSinks[BusSinkIndex.Reserved6] != null)
            {
                throw new InvalidOperationException(
                    "_scalarSinks[Reserved6] is a permanent future-expansion stub and must be null."
                );
            }
            if (_scalarSinks[BusSinkIndex.Reserved7] != null)
            {
                throw new InvalidOperationException(
                    "_scalarSinks[Reserved7] is a permanent future-expansion stub and must be null."
                );
            }
            if (_scalarSinks[BusSinkIndex.UntargetedHandleDefault] == null)
            {
                throw new InvalidOperationException(
                    "_scalarSinks[UntargetedHandleDefault] must be non-null."
                );
            }
            if (_scalarSinks[BusSinkIndex.BroadcastHandleWithoutContext] == null)
            {
                throw new InvalidOperationException(
                    "_scalarSinks[BroadcastHandleWithoutContext] must be non-null."
                );
            }
            if (_scalarSinks[BusSinkIndex.TargetedHandleWithoutContext] == null)
            {
                throw new InvalidOperationException(
                    "_scalarSinks[TargetedHandleWithoutContext] must be non-null."
                );
            }
            if (_scalarSinks[BusSinkIndex.UntargetedPostProcessDefault] == null)
            {
                throw new InvalidOperationException(
                    "_scalarSinks[UntargetedPostProcessDefault] must be non-null."
                );
            }
            if (_scalarSinks[BusSinkIndex.TargetedPostProcessWithoutContext] == null)
            {
                throw new InvalidOperationException(
                    "_scalarSinks[TargetedPostProcessWithoutContext] must be non-null."
                );
            }
            if (_scalarSinks[BusSinkIndex.BroadcastPostProcessWithoutContext] == null)
            {
                throw new InvalidOperationException(
                    "_scalarSinks[BroadcastPostProcessWithoutContext] must be non-null."
                );
            }
            if (_contextSinks[BusContextIndex.TargetedHandleDefault] == null)
            {
                throw new InvalidOperationException(
                    "_contextSinks[TargetedHandleDefault] must be non-null."
                );
            }
            if (_contextSinks[BusContextIndex.BroadcastHandleDefault] == null)
            {
                throw new InvalidOperationException(
                    "_contextSinks[BroadcastHandleDefault] must be non-null."
                );
            }
            if (_contextSinks[BusContextIndex.TargetedPostProcessDefault] == null)
            {
                throw new InvalidOperationException(
                    "_contextSinks[TargetedPostProcessDefault] must be non-null."
                );
            }
            if (_contextSinks[BusContextIndex.BroadcastPostProcessDefault] == null)
            {
                throw new InvalidOperationException(
                    "_contextSinks[BroadcastPostProcessDefault] must be non-null."
                );
            }
        }

        // Asserts BusGlobalSlot.liveCount remains in lockstep with
        // _globalSlots.sharedHandlers.Count after every register / deregister.
        // Stripped in Release builds via [Conditional("DEBUG")] -- zero
        // hot-path cost. Kept separate from ValidateSinkArrays (which runs
        // once at construction) because this invariant must hold across
        // mutations, not only at startup.
        [Conditional("DEBUG")]
        private void DebugAssertGlobalLiveCount()
        {
            System.Diagnostics.Debug.Assert(
                _globalSlots.liveCount == _globalSlots.sharedHandlers.Count,
                "BusGlobalSlot.liveCount must mirror sharedHandlers.Count at every "
                    + "stable observation point. Drift indicates a missed register / "
                    + "deregister wiring point or an unexpected mutation path."
            );
        }

        // Interceptors split by category to avoid mixing types
        private readonly MessageCache<InterceptorCache<object>> _untargetedInterceptsByType = new();
        private readonly MessageCache<InterceptorCache<object>> _targetedInterceptsByType = new();
        private readonly MessageCache<InterceptorCache<object>> _broadcastInterceptsByType = new();
        private readonly Dictionary<object, Dictionary<int, int>> _uniqueInterceptorsAndPriorities =
            new();

        private readonly Dictionary<
            Type,
            Action<IUntargetedMessage>
        > _untargetedBroadcastMethodsByType = new();
        private readonly Dictionary<
            Type,
            Action<InstanceId, ITargetedMessage>
        > _targetedBroadcastMethodsByType = new();
        private readonly Dictionary<
            Type,
            Action<InstanceId, IBroadcastMessage>
        > _sourcedBroadcastMethodsByType = new();
        private readonly Stack<List<object>> _innerInterceptorsStack = new();

        private readonly Dictionary<
            Type,
            Dictionary<MethodSignatureKey, Action<MonoBehaviour, object[]>>
        > _methodCache = new();

#if UNITY_2021_3_OR_NEWER
        private readonly HashSet<MonoBehaviour> _recipientCache = new();
        private readonly List<MonoBehaviour> _componentCache = new();
#endif

        private readonly RegistrationLog _log = new();
        internal readonly CyclicBuffer<MessageEmissionData> _emissionBuffer = new(
            GlobalMessageBufferSize
        );

        private bool _diagnosticsMode = ShouldEnableDiagnostics();
        private bool _loggedReflexiveWarning;
        private long _tickCounter;
        private readonly IDxMessagingClock _clock;
        private long _idleEvictionTicks = DefaultIdleEvictionTicks;
        private double _evictionTickIntervalSeconds = DefaultEvictionTickIntervalSeconds;
        private bool _idleEvictionEnabled = true;
        private bool _trimApiEnabled = true;
        private double _lastSweepSeconds;
        private readonly List<int> _dirtyTypes = new();
        private readonly Dictionary<int, List<InstanceId>> _dirtyTargets = new();
        private readonly Dictionary<int, int> _dirtyTargetHighWaterCounts = new();
        private readonly HashSet<int> _dirtyTypeSet = new();
        private readonly Dictionary<int, HashSet<InstanceId>> _dirtyTargetSets = new();
        private readonly Dictionary<
            Dictionary<InstanceId, HandlerCache<int, HandlerCache>>,
            int
        > _contextMapHighWaterCounts = new();
        private readonly List<MessageHandler> _dirtyHandlers = new();
        private readonly HashSet<MessageHandler> _dirtyHandlerSet = new();
        private readonly Dictionary<MessageHandler, long> _dirtyHandlerTicks = new();
        private bool _globalSlotSweepCandidate;
        private long _globalSlotSweepGeneration;
        private int _lastContextTypeSlotsEvicted;
        private int _dispatchDepth;

        // Deferred teardown for ResetState() invoked from inside a handler
        // while an emission is in flight. Clearing a context HandlerCache (or
        // resetting a global DispatchState) inline would release the in-flight
        // emission's frozen DispatchSnapshot bucket/entry arrays back to their
        // ArrayPools (and clear the frozen priority list) while the dispatch
        // loop is still iterating them. Instead, the caches/states are queued
        // here and torn down when the outermost dispatch lease exits --
        // mirroring how Trim/sweep defer eviction via HasActiveDispatchSnapshot
        // and the _dispatchDepth gate in SweepDirtyTypedHandlerSlots. These
        // lists allocate only on the rare reset-during-dispatch path; the
        // steady-state dispatch path pays a single flag check on lease exit.
        //
        // _deferredDisplacedSnapshots extends the same machinery to dispatch
        // snapshots DISPLACED out of DispatchState.active by a nested
        // emission's snapshot promotion (see ReleaseDisplacedSnapshot): a
        // handler that mutates the same-type registration set and then
        // reentrantly emits the same message type promotes the staged pending
        // snapshot under a new emission id, displacing the snapshot the OUTER
        // dispatch loop is still iterating. Releasing it inline would clear
        // and pool the frozen arrays mid-iteration (NRE / silent handler
        // drops / cross-dispatch pool aliasing at deeper nesting). Displaced
        // snapshots are queued here instead and released when the outermost
        // dispatch lease exits. A snapshot can be queued at most once: it is
        // removed from its DispatchState field at the moment it is queued,
        // snapshot instances are never shared between state fields, and
        // Release() is additionally idempotent via its _pooled guard.
        private bool _hasDeferredResetTeardown;
        private List<HandlerCache<int, HandlerCache>> _deferredResetHandlerCaches;
        private List<DispatchState> _deferredResetDispatchStates;
        private List<DispatchSnapshot> _deferredDisplacedSnapshots;

        // Bumped by ResetState. Deregister closures captured before the bump
        // compare their captured generation to this field and silently skip
        // when they no longer match, so a deferred Object.Destroy that lands
        // after a Reset cannot log spurious over-deregistration errors.
        private long _resetGeneration;

        /// <summary>
        /// Bumps the internal reset generation counter without clearing any registrations or sinks.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Deregister closures returned by the registration entry points capture the value of the
        /// reset generation at registration time and silently no-op when the captured value differs
        /// from the bus's current value. Calling this method invalidates every previously-issued
        /// deregister closure for this bus, which is the desired behaviour after a logical "wipe"
        /// performed by external state-management code (for example, a custom domain-reload-disabled
        /// reset utility) that does not wish to clear registrations via <see cref="ResetState"/>.
        /// </para>
        /// <para>
        /// <see cref="DxMessagingStaticState.Reset"/> uses this method to extend the destroy-then-Reset
        /// race-safety guarantee to user-installed custom global buses without clobbering their state.
        /// </para>
        /// </remarks>
        public void BumpResetGeneration()
        {
            unchecked
            {
                _resetGeneration++;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static long GetCurrentTouchTick(IMessageBus messageBus)
        {
            return messageBus is MessageBus bus ? bus._tickCounter : messageBus?.EmissionId ?? 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static long GetResetGeneration(IMessageBus messageBus)
        {
            return messageBus is MessageBus bus ? bus._resetGeneration : 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsResetGenerationCurrent(IMessageBus messageBus, long generation)
        {
            return messageBus is not MessageBus bus || bus._resetGeneration == generation;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private long AdvanceTick()
        {
            unchecked
            {
                _tickCounter++;
            }

            return _tickCounter;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Touch(HandlerCache<int, HandlerCache> handlers, long tick)
        {
            if (handlers != null)
            {
                handlers.lastTouchTicks = tick;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void MarkDirtyType<TMessage>()
            where TMessage : IMessage
        {
            int typeIndex = MessageHelperIndexer<TMessage>.SequentialId;
            if (0 <= typeIndex && _dirtyTypeSet.Add(typeIndex))
            {
                _dirtyTypes.Add(typeIndex);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void MarkDirtyTarget<TMessage>(InstanceId target)
            where TMessage : IMessage
        {
            int typeIndex = MessageHelperIndexer<TMessage>.SequentialId;
            if (typeIndex < 0)
            {
                return;
            }

            if (!_dirtyTargets.TryGetValue(typeIndex, out List<InstanceId> targets))
            {
                targets = DxPools.InstanceIdLists.Rent();
                _dirtyTargets[typeIndex] = targets;
            }

            if (!_dirtyTargetSets.TryGetValue(typeIndex, out HashSet<InstanceId> targetSet))
            {
                targetSet = DxPools.InstanceIdSets.Rent();
                _dirtyTargetSets[typeIndex] = targetSet;
            }

            if (targetSet.Add(target))
            {
                targets.Add(target);
                _dirtyTargetHighWaterCounts[typeIndex] = Math.Max(
                    GetDirtyTargetHighWaterCount(typeIndex),
                    targets.Count
                );
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void MarkDirtyHandler(MessageHandler handler)
        {
            if (handler == null)
            {
                return;
            }

            _dirtyHandlerTicks[handler] = _tickCounter;
            if (_dirtyHandlerSet.Add(handler))
            {
                _dirtyHandlers.Add(handler);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private DispatchLease EnterDispatch()
        {
            return new DispatchLease(this);
        }

        public TrimResult Trim(bool force = false)
        {
            if (!_trimApiEnabled)
            {
                return default;
            }

            return Sweep(force);
        }

        internal TrimResult Sweep(bool force)
        {
            int typeSlotsEvicted = SweepableTypeCacheRegistry[0].Sweep(this, force);
            _lastContextTypeSlotsEvicted = 0;
            int targetSlotsEvicted = SweepableTypeCacheRegistry[1].Sweep(this, force);
            typeSlotsEvicted += _lastContextTypeSlotsEvicted;
            typeSlotsEvicted += SweepableTypeCacheRegistry[2].Sweep(this, force);
            typeSlotsEvicted += SweepableTypeCacheRegistry[3].Sweep(this, force);
            typeSlotsEvicted += SweepableTypeCacheRegistry[4].Sweep(this, force);
            typeSlotsEvicted += SweepGlobalSlot(force);
            typeSlotsEvicted += SweepDirtyTypedHandlerSlots(force);
            if (force)
            {
                ClearDirtySweepCandidates();
            }
            else
            {
                PruneDirtySweepCandidates();
            }
            int pooledCollectionsEvicted = DxPools.TrimAll(force);
            pooledCollectionsEvicted += ContextHandlerByTargetDicts.Trim(
                force ? 0 : ContextHandlerByTargetDicts.MaxRetained
            );
            _lastSweepSeconds = _clock.NowSeconds;

            return new TrimResult(
                typeSlotsEvicted,
                targetSlotsEvicted,
                pooledCollectionsEvicted,
                OccupiedTypeSlots
            );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void TrySweepIdle(bool advanceTickForIdleAging = false)
        {
            if (!_idleEvictionEnabled)
            {
                return;
            }

            if (!advanceTickForIdleAging && ((unchecked(_emissionId + 1)) & SweepGateMask) != 0)
            {
                return;
            }

            double nowSeconds = _clock.NowSeconds;
            if (nowSeconds - _lastSweepSeconds < _evictionTickIntervalSeconds)
            {
                return;
            }

            if (advanceTickForIdleAging)
            {
                _ = AdvanceTick();
            }

            _ = Sweep(force: false);
        }

        private int SweepDirtyScalarTypeSlots(bool force)
        {
            int evicted = 0;
            for (int i = 0; i < _dirtyTypes.Count; ++i)
            {
                int typeIndex = _dirtyTypes[i];
                for (int sinkIndex = 0; sinkIndex < _scalarSinks.Length; ++sinkIndex)
                {
                    MessageCache<HandlerCache<int, HandlerCache>> sink = _scalarSinks[sinkIndex];
                    if (
                        sink == null
                        || !sink.TryGetValueAtIndex(
                            typeIndex,
                            out HandlerCache<int, HandlerCache> handlers
                        )
                        || handlers.handlers.Count != 0
                        || HasActiveDispatchSnapshot(handlers.dispatchState)
                        || !IsIdleForSweep(handlers.lastTouchTicks, force)
                    )
                    {
                        continue;
                    }

                    handlers.Clear();
                    sink.RemoveAtIndex(typeIndex);
                    evicted++;
                }
            }

            return evicted;
        }

        private int SweepDirtyInterceptorTypeSlots(
            MessageCache<InterceptorCache<object>> interceptorsByType,
            bool force
        )
        {
            int evicted = 0;
            for (int i = 0; i < _dirtyTypes.Count; ++i)
            {
                int typeIndex = _dirtyTypes[i];
                if (
                    !interceptorsByType.TryGetValueAtIndex(
                        typeIndex,
                        out InterceptorCache<object> interceptors
                    )
                    || interceptors.handlers.Count != 0
                    || !IsIdleForSweep(interceptors.lastTouchTicks, force)
                )
                {
                    continue;
                }

                interceptors.Clear();
                interceptorsByType.RemoveAtIndex(typeIndex);
                evicted++;
            }

            return evicted;
        }

        private int SweepDirtyTargetSlots(bool force)
        {
            int evicted = 0;
            foreach (KeyValuePair<int, List<InstanceId>> dirtyTargetEntry in _dirtyTargets)
            {
                int typeIndex = dirtyTargetEntry.Key;
                List<InstanceId> targets = dirtyTargetEntry.Value;
                for (int sinkIndex = 0; sinkIndex < _contextSinks.Length; ++sinkIndex)
                {
                    MessageCache<Dictionary<InstanceId, HandlerCache<int, HandlerCache>>> sink =
                        _contextSinks[sinkIndex];
                    if (
                        sink == null
                        || !sink.TryGetValueAtIndex(
                            typeIndex,
                            out Dictionary<
                                InstanceId,
                                HandlerCache<int, HandlerCache>
                            > handlersByTarget
                        )
                    )
                    {
                        continue;
                    }

                    for (int targetIndex = 0; targetIndex < targets.Count; ++targetIndex)
                    {
                        InstanceId target = targets[targetIndex];
                        if (
                            !handlersByTarget.TryGetValue(
                                target,
                                out HandlerCache<int, HandlerCache> handlers
                            )
                            || handlers.handlers.Count != 0
                            || HasActiveDispatchSnapshot(handlers.dispatchState)
                            || !IsIdleForSweep(handlers.lastTouchTicks, force)
                        )
                        {
                            continue;
                        }

                        handlers.Clear();
                        _ = handlersByTarget.Remove(target);
                        evicted++;
                    }

                    if (handlersByTarget.Count == 0)
                    {
                        RemoveAndReturnContextMap(sink, typeIndex, handlersByTarget);
                        _lastContextTypeSlotsEvicted++;
                    }
                }
            }

            return evicted;
        }

        private int SweepGlobalSlot(bool force)
        {
            if (
                !_globalSlotSweepCandidate
                || !_globalSlots.IsEmpty
                || HasActiveGlobalDispatchSnapshot()
                || !IsIdleForSweep(_globalSlots.lastTouchTicks, force)
            )
            {
                return 0;
            }

            // LEGACY: global slot reset keeps the sweep-generation guard for stale
            // deregistration closures.
            _globalSlots.Reset();
            unchecked
            {
                _globalSlotSweepGeneration++;
            }
            _globalSlotSweepCandidate = false;
            return 1;
        }

        private int SweepDirtyTypedHandlerSlots(bool force)
        {
            int evicted = 0;
            if (_dispatchDepth > 0)
            {
                return evicted;
            }

            int write = 0;
            int count = _dirtyHandlers.Count;
            for (int i = 0; i < count; ++i)
            {
                MessageHandler handler = _dirtyHandlers[i];
                if (
                    !force
                    && (
                        !_dirtyHandlerTicks.TryGetValue(handler, out long lastTouchTicks)
                        || !IsIdleForSweep(lastTouchTicks, force: false)
                    )
                )
                {
                    _dirtyHandlers[write++] = handler;
                    continue;
                }

                evicted += handler.ResetEmptyTypedSlotsForSweep(this);
                if (handler.HasTypedHandlersForBus(this))
                {
                    _dirtyHandlers[write++] = handler;
                    continue;
                }

                _dirtyHandlerSet.Remove(handler);
                _dirtyHandlerTicks.Remove(handler);
            }

            if (write < count)
            {
                _dirtyHandlers.RemoveRange(write, count - write);
            }

            return evicted;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsIdleForSweep(long lastTouchTicks, bool force)
        {
            return force || unchecked(_tickCounter - lastTouchTicks) > _idleEvictionTicks;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool HasActiveDispatchSnapshot(DispatchState state)
        {
            return _dispatchDepth > 0 && state != null && !state.active.IsEmpty;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool HasActiveGlobalDispatchSnapshot()
        {
            return HasActiveDispatchSnapshot(_globalSlots.untargetedDispatchState)
                || HasActiveDispatchSnapshot(_globalSlots.targetedDispatchState)
                || HasActiveDispatchSnapshot(_globalSlots.broadcastDispatchState);
        }

        private void PruneDirtySweepCandidates()
        {
            PruneDirtyScalarTypeCandidates();
            PruneDirtyTargetCandidates();
            PruneDirtyHandlerCandidates();
        }

        private void PruneDirtyScalarTypeCandidates()
        {
            int write = 0;
            for (int i = 0; i < _dirtyTypes.Count; ++i)
            {
                int typeIndex = _dirtyTypes[i];
                if (
                    HasFreshEmptyScalarTypeCandidate(typeIndex)
                    || HasFreshEmptyInterceptorTypeCandidate(typeIndex)
                )
                {
                    _dirtyTypes[write++] = typeIndex;
                    continue;
                }

                _dirtyTypeSet.Remove(typeIndex);
            }

            if (write < _dirtyTypes.Count)
            {
                _dirtyTypes.RemoveRange(write, _dirtyTypes.Count - write);
            }
        }

        private bool HasFreshEmptyScalarTypeCandidate(int typeIndex)
        {
            for (int sinkIndex = 0; sinkIndex < _scalarSinks.Length; ++sinkIndex)
            {
                MessageCache<HandlerCache<int, HandlerCache>> sink = _scalarSinks[sinkIndex];
                if (
                    sink != null
                    && sink.TryGetValueAtIndex(
                        typeIndex,
                        out HandlerCache<int, HandlerCache> handlers
                    )
                    && handlers.handlers.Count == 0
                    && !IsIdleForSweep(handlers.lastTouchTicks, force: false)
                )
                {
                    return true;
                }
            }

            return false;
        }

        private bool HasFreshEmptyInterceptorTypeCandidate(int typeIndex)
        {
            return HasFreshEmptyInterceptorTypeCandidate(_untargetedInterceptsByType, typeIndex)
                || HasFreshEmptyInterceptorTypeCandidate(_targetedInterceptsByType, typeIndex)
                || HasFreshEmptyInterceptorTypeCandidate(_broadcastInterceptsByType, typeIndex);
        }

        private bool HasFreshEmptyInterceptorTypeCandidate(
            MessageCache<InterceptorCache<object>> interceptorsByType,
            int typeIndex
        )
        {
            return interceptorsByType.TryGetValueAtIndex(
                    typeIndex,
                    out InterceptorCache<object> interceptors
                )
                && interceptors.handlers.Count == 0
                && !IsIdleForSweep(interceptors.lastTouchTicks, force: false);
        }

        private void PruneDirtyTargetCandidates()
        {
            List<int> emptyTypeKeys = null;
            foreach (KeyValuePair<int, List<InstanceId>> entry in _dirtyTargets)
            {
                int typeIndex = entry.Key;
                List<InstanceId> targets = entry.Value;
                _dirtyTargetSets.TryGetValue(typeIndex, out HashSet<InstanceId> targetSet);
                int write = 0;
                for (int i = 0; i < targets.Count; ++i)
                {
                    InstanceId target = targets[i];
                    if (HasFreshEmptyTargetCandidate(typeIndex, target))
                    {
                        targets[write++] = target;
                        continue;
                    }

                    targetSet?.Remove(target);
                }

                if (write < targets.Count)
                {
                    targets.RemoveRange(write, targets.Count - write);
                }

                if (targets.Count == 0)
                {
                    (emptyTypeKeys ??= new List<int>()).Add(typeIndex);
                }
            }

            if (emptyTypeKeys == null)
            {
                return;
            }

            for (int i = 0; i < emptyTypeKeys.Count; ++i)
            {
                int typeIndex = emptyTypeKeys[i];
                ReturnDirtyTargetCollections(typeIndex);
                _dirtyTargets.Remove(typeIndex);
                _dirtyTargetSets.Remove(typeIndex);
            }
        }

        private bool HasFreshEmptyTargetCandidate(int typeIndex, InstanceId target)
        {
            for (int sinkIndex = 0; sinkIndex < _contextSinks.Length; ++sinkIndex)
            {
                MessageCache<Dictionary<InstanceId, HandlerCache<int, HandlerCache>>> sink =
                    _contextSinks[sinkIndex];
                if (
                    sink == null
                    || !sink.TryGetValueAtIndex(
                        typeIndex,
                        out Dictionary<InstanceId, HandlerCache<int, HandlerCache>> handlersByTarget
                    )
                    || !handlersByTarget.TryGetValue(
                        target,
                        out HandlerCache<int, HandlerCache> handlers
                    )
                )
                {
                    continue;
                }

                if (
                    handlers.handlers.Count == 0
                    && (
                        HasActiveDispatchSnapshot(handlers.dispatchState)
                        || !IsIdleForSweep(handlers.lastTouchTicks, force: false)
                    )
                )
                {
                    return true;
                }
            }

            return false;
        }

        private void PruneDirtyHandlerCandidates()
        {
            int write = 0;
            for (int i = 0; i < _dirtyHandlers.Count; ++i)
            {
                MessageHandler handler = _dirtyHandlers[i];
                if (
                    handler != null
                    && _dirtyHandlerSet.Contains(handler)
                    && _dirtyHandlerTicks.TryGetValue(handler, out long lastTouchTicks)
                    && handler.CountEmptyTypedSlotsForSweep(this) > 0
                    && !IsIdleForSweep(lastTouchTicks, force: false)
                )
                {
                    _dirtyHandlers[write++] = handler;
                    continue;
                }

                _dirtyHandlerSet.Remove(handler);
                _dirtyHandlerTicks.Remove(handler);
            }

            if (write < _dirtyHandlers.Count)
            {
                _dirtyHandlers.RemoveRange(write, _dirtyHandlers.Count - write);
            }
        }

        private void ClearDirtySweepCandidates()
        {
            ClearDirtyTypeCandidatesWithoutEmptySlots();
            ClearDirtyTargetCandidatesWithoutEmptySlots();
            ClearDirtyHandlerCandidatesWithoutEmptySlots();
        }

        private void ReturnDirtyTargetCollections(int typeIndex)
        {
            _dirtyTargets.TryGetValue(typeIndex, out List<InstanceId> targets);
            _dirtyTargetSets.TryGetValue(typeIndex, out HashSet<InstanceId> targetSet);
            int highWaterCount = GetDirtyTargetHighWaterCount(typeIndex);
            ReturnDirtyTargetList(targets, highWaterCount);
            ReturnDirtyTargetSet(targetSet, highWaterCount);
            _dirtyTargetHighWaterCounts.Remove(typeIndex);
        }

        private void ReturnAllDirtyTargetCollections()
        {
            foreach (KeyValuePair<int, List<InstanceId>> entry in _dirtyTargets)
            {
                int highWaterCount = GetDirtyTargetHighWaterCount(entry.Key);
                ReturnDirtyTargetList(entry.Value, highWaterCount);
            }

            foreach (KeyValuePair<int, HashSet<InstanceId>> entry in _dirtyTargetSets)
            {
                int highWaterCount = GetDirtyTargetHighWaterCount(entry.Key);
                ReturnDirtyTargetSet(entry.Value, highWaterCount);
            }

            _dirtyTargetHighWaterCounts.Clear();
        }

        private int GetDirtyTargetHighWaterCount(int typeIndex)
        {
            return _dirtyTargetHighWaterCounts.TryGetValue(typeIndex, out int count) ? count : 0;
        }

        private static void ReturnDirtyTargetList(List<InstanceId> targets, int highWaterCount)
        {
            if (targets == null)
            {
                return;
            }

            if (ShouldDropOversizedPoolEntry(highWaterCount, DxPools.InstanceIdLists.MaxRetained))
            {
                targets.Clear();
                return;
            }

            DxPools.InstanceIdLists.Return(targets);
        }

        private static void ReturnDirtyTargetSet(HashSet<InstanceId> targets, int highWaterCount)
        {
            if (targets == null)
            {
                return;
            }

            if (ShouldDropOversizedPoolEntry(highWaterCount, DxPools.InstanceIdSets.MaxRetained))
            {
                targets.Clear();
                return;
            }

            DxPools.InstanceIdSets.Return(targets);
        }

        internal CollectionPoolDiagnostics GetContextDictPoolDiagnosticsForTesting()
        {
            return ContextHandlerByTargetDicts.Snapshot();
        }

        private Dictionary<InstanceId, HandlerCache<int, HandlerCache>> GetOrRentContextMap<T>(
            MessageCache<Dictionary<InstanceId, HandlerCache<int, HandlerCache>>> sinks
        )
            where T : IMessage
        {
            if (
                sinks.TryGetValue<T>(
                    out Dictionary<InstanceId, HandlerCache<int, HandlerCache>> handlersByTarget
                )
            )
            {
                return handlersByTarget;
            }

            handlersByTarget = ContextHandlerByTargetDicts.Rent();
            _contextMapHighWaterCounts[handlersByTarget] = handlersByTarget.Count;
            sinks.Set<T>(handlersByTarget);
            return handlersByTarget;
        }

        private void RemoveAndReturnContextMap(
            MessageCache<Dictionary<InstanceId, HandlerCache<int, HandlerCache>>> sink,
            int typeIndex,
            Dictionary<InstanceId, HandlerCache<int, HandlerCache>> handlersByTarget
        )
        {
            sink.RemoveAtIndex(typeIndex);
            ReturnContextMap(handlersByTarget);
        }

        private void ReturnContextMap(
            Dictionary<InstanceId, HandlerCache<int, HandlerCache>> handlersByTarget
        )
        {
            if (handlersByTarget == null)
            {
                return;
            }

            int highWaterCount = GetContextMapHighWaterCount(handlersByTarget);
            _contextMapHighWaterCounts.Remove(handlersByTarget);

            foreach (HandlerCache<int, HandlerCache> handlers in handlersByTarget.Values)
            {
                if (handlers == null)
                {
                    continue;
                }

                if (_dispatchDepth > 0)
                {
                    // An emission is in flight (ResetState invoked from inside
                    // a handler): Clear() would release the active dispatch
                    // snapshot's pooled arrays and the frozen priority list
                    // while the dispatch loop is still iterating them. Defer
                    // the teardown until the outermost dispatch lease exits.
                    DeferHandlerCacheClear(handlers);
                    continue;
                }

                handlers.Clear();
            }

            handlersByTarget.Clear();
            if (
                ShouldDropOversizedPoolEntry(
                    highWaterCount,
                    ContextHandlerByTargetDicts.MaxRetained
                )
            )
            {
                return;
            }

            ContextHandlerByTargetDicts.Return(handlersByTarget);
        }

        private void ClearAndReturnContextSink(
            MessageCache<Dictionary<InstanceId, HandlerCache<int, HandlerCache>>> sink
        )
        {
            foreach (
                Dictionary<InstanceId, HandlerCache<int, HandlerCache>> handlersByTarget in sink
            )
            {
                ReturnContextMap(handlersByTarget);
            }

            sink.Clear();
        }

        /// <summary>
        /// Clears a scalar (per-type, priority-keyed) sink during
        /// <see cref="ResetState"/>, releasing each per-type cache's dispatch
        /// snapshots back to their pools instead of dropping them to the GC
        /// (the legacy MessageCache.Clear()-only path leaked the pooled
        /// bucket/entry arrays - and, post-flattening, the pooled flat
        /// untargeted arrays - on every reset). Mirrors
        /// <see cref="ReturnContextMap"/>: when a reset fires from inside a
        /// handler (dispatch lease active), the per-type Clear is deferred
        /// until the outermost lease exits so the in-flight emission keeps
        /// iterating its frozen snapshot arrays safely.
        /// </summary>
        private void ClearScalarSink(MessageCache<HandlerCache<int, HandlerCache>> sink)
        {
            foreach (HandlerCache<int, HandlerCache> handlers in sink)
            {
                if (_dispatchDepth > 0)
                {
                    DeferHandlerCacheClear(handlers);
                    continue;
                }

                handlers.Clear();
            }

            sink.Clear();
        }

        private void DeferHandlerCacheClear(HandlerCache<int, HandlerCache> handlers)
        {
            _deferredResetHandlerCaches ??= new List<HandlerCache<int, HandlerCache>>();
            _deferredResetHandlerCaches.Add(handlers);
            _hasDeferredResetTeardown = true;
        }

        private void DeferDispatchStateReset(ref DispatchState state)
        {
            if (state == null)
            {
                return;
            }

            _deferredResetDispatchStates ??= new List<DispatchState>();
            _deferredResetDispatchStates.Add(state);
            state = null;
            _hasDeferredResetTeardown = true;
        }

        /// <summary>
        /// Releases a snapshot displaced out of <see cref="DispatchState.active"/>
        /// (or discarded while shrinking to empty). When any dispatch lease is
        /// active beyond the one owned by the emission performing the
        /// displacement (<c>_dispatchDepth &gt; 1</c>), an OUTER emission may
        /// still be iterating the displaced snapshot's pooled arrays, so the
        /// release is deferred to the outermost lease exit via
        /// <see cref="FlushDeferredResetTeardown"/>. At depth &lt;= 1 every
        /// prior emission has fully completed (leases strictly nest), so the
        /// displaced snapshot is provably unreferenced and is released inline,
        /// preserving the legacy pooling behavior for non-reentrant churn.
        /// Note this deferral is only needed for <c>active</c>:
        /// <see cref="DispatchState.pending"/> is never returned to a dispatch
        /// loop (the acquire paths only ever return <c>active</c>, and
        /// promotion transfers ownership pending -&gt; active while clearing
        /// pending), so inline pending releases in
        /// <see cref="StageDispatchSnapshot{TMessage}"/> /
        /// <see cref="AcquireDispatchSnapshot{TMessage}"/> can never free
        /// arrays an in-flight emission is iterating.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ReleaseDisplacedSnapshot(ref DispatchSnapshot snapshot)
        {
            DispatchSnapshot displaced = snapshot;
            snapshot = DispatchSnapshot.Empty;
            if (displaced == null || displaced.IsEmpty)
            {
                return;
            }

            if (1 < _dispatchDepth)
            {
                _deferredDisplacedSnapshots ??= new List<DispatchSnapshot>();
                _deferredDisplacedSnapshots.Add(displaced);
                _hasDeferredResetTeardown = true;
                return;
            }

            displaced.Release();
        }

        private void FlushDeferredResetTeardown()
        {
            _hasDeferredResetTeardown = false;
            // Displaced snapshots are released FIRST: they are standalone
            // (queued only after being unlinked from their DispatchState
            // field, and snapshot instances are never shared between state
            // fields), so releasing them cannot interact with the cache
            // clears / state resets below, which release *different* snapshot
            // objects still referenced by their states. Releasing them ahead
            // of the clears keeps the pools warm for any rebuilds those
            // clears trigger later, and Release() stays idempotent via
            // _pooled should both paths ever observe the same instance.
            List<DispatchSnapshot> displacedSnapshots = _deferredDisplacedSnapshots;
            if (displacedSnapshots != null)
            {
                for (int i = 0; i < displacedSnapshots.Count; ++i)
                {
                    displacedSnapshots[i].Release();
                }

                displacedSnapshots.Clear();
            }

            List<HandlerCache<int, HandlerCache>> deferredCaches = _deferredResetHandlerCaches;
            if (deferredCaches != null)
            {
                for (int i = 0; i < deferredCaches.Count; ++i)
                {
                    deferredCaches[i].Clear();
                }

                deferredCaches.Clear();
            }

            List<DispatchState> deferredStates = _deferredResetDispatchStates;
            if (deferredStates != null)
            {
                for (int i = 0; i < deferredStates.Count; ++i)
                {
                    deferredStates[i].Reset();
                }

                deferredStates.Clear();
            }
        }

        private void TrackContextMapHighWater(
            Dictionary<InstanceId, HandlerCache<int, HandlerCache>> handlersByTarget
        )
        {
            if (handlersByTarget == null)
            {
                return;
            }

            _contextMapHighWaterCounts[handlersByTarget] = Math.Max(
                GetContextMapHighWaterCount(handlersByTarget),
                handlersByTarget.Count
            );
        }

        private int GetContextMapHighWaterCount(
            Dictionary<InstanceId, HandlerCache<int, HandlerCache>> handlersByTarget
        )
        {
            return _contextMapHighWaterCounts.TryGetValue(handlersByTarget, out int count)
                ? count
                : handlersByTarget?.Count ?? 0;
        }

        private static bool ShouldDropOversizedPoolEntry(int retainedEntryCount, int maxRetained)
        {
            return maxRetained > 0 && retainedEntryCount > maxRetained;
        }

        private void ClearDirtyTypeCandidatesWithoutEmptySlots()
        {
            int write = 0;
            for (int i = 0; i < _dirtyTypes.Count; ++i)
            {
                int typeIndex = _dirtyTypes[i];
                if (HasEmptyScalarTypeCandidate(typeIndex))
                {
                    _dirtyTypes[write++] = typeIndex;
                    continue;
                }

                _dirtyTypeSet.Remove(typeIndex);
            }

            if (write < _dirtyTypes.Count)
            {
                _dirtyTypes.RemoveRange(write, _dirtyTypes.Count - write);
            }
        }

        private bool HasEmptyScalarTypeCandidate(int typeIndex)
        {
            for (int sinkIndex = 0; sinkIndex < _scalarSinks.Length; ++sinkIndex)
            {
                MessageCache<HandlerCache<int, HandlerCache>> sink = _scalarSinks[sinkIndex];
                if (
                    sink != null
                    && sink.TryGetValueAtIndex(
                        typeIndex,
                        out HandlerCache<int, HandlerCache> handlers
                    )
                    && handlers.handlers.Count == 0
                )
                {
                    return true;
                }
            }

            return HasEmptyInterceptorTypeCandidate(_untargetedInterceptsByType, typeIndex)
                || HasEmptyInterceptorTypeCandidate(_targetedInterceptsByType, typeIndex)
                || HasEmptyInterceptorTypeCandidate(_broadcastInterceptsByType, typeIndex);
        }

        private static bool HasEmptyInterceptorTypeCandidate(
            MessageCache<InterceptorCache<object>> interceptorsByType,
            int typeIndex
        )
        {
            return interceptorsByType.TryGetValueAtIndex(
                    typeIndex,
                    out InterceptorCache<object> interceptors
                )
                && interceptors.handlers.Count == 0;
        }

        private void ClearDirtyTargetCandidatesWithoutEmptySlots()
        {
            List<int> emptyTypeKeys = null;
            foreach (KeyValuePair<int, List<InstanceId>> entry in _dirtyTargets)
            {
                int typeIndex = entry.Key;
                List<InstanceId> targets = entry.Value;
                _dirtyTargetSets.TryGetValue(typeIndex, out HashSet<InstanceId> targetSet);
                int write = 0;
                for (int i = 0; i < targets.Count; ++i)
                {
                    InstanceId target = targets[i];
                    if (HasEmptyTargetCandidate(typeIndex, target))
                    {
                        targets[write++] = target;
                        continue;
                    }

                    targetSet?.Remove(target);
                }

                if (write < targets.Count)
                {
                    targets.RemoveRange(write, targets.Count - write);
                }

                if (targets.Count == 0)
                {
                    (emptyTypeKeys ??= new List<int>()).Add(typeIndex);
                }
            }

            if (emptyTypeKeys == null)
            {
                return;
            }

            for (int i = 0; i < emptyTypeKeys.Count; ++i)
            {
                int typeIndex = emptyTypeKeys[i];
                ReturnDirtyTargetCollections(typeIndex);
                _dirtyTargets.Remove(typeIndex);
                _dirtyTargetSets.Remove(typeIndex);
            }
        }

        private bool HasEmptyTargetCandidate(int typeIndex, InstanceId target)
        {
            for (int sinkIndex = 0; sinkIndex < _contextSinks.Length; ++sinkIndex)
            {
                MessageCache<Dictionary<InstanceId, HandlerCache<int, HandlerCache>>> sink =
                    _contextSinks[sinkIndex];
                if (
                    sink != null
                    && sink.TryGetValueAtIndex(
                        typeIndex,
                        out Dictionary<InstanceId, HandlerCache<int, HandlerCache>> handlersByTarget
                    )
                    && handlersByTarget.TryGetValue(
                        target,
                        out HandlerCache<int, HandlerCache> handlers
                    )
                    && handlers.handlers.Count == 0
                )
                {
                    return true;
                }
            }

            return false;
        }

        private void ClearDirtyHandlerCandidatesWithoutEmptySlots()
        {
            int write = 0;
            for (int i = 0; i < _dirtyHandlers.Count; ++i)
            {
                MessageHandler handler = _dirtyHandlers[i];
                if (
                    handler != null
                    && _dirtyHandlerSet.Contains(handler)
                    && handler.CountEmptyTypedSlotsForSweep(this) > 0
                )
                {
                    _dirtyHandlers[write++] = handler;
                    continue;
                }

                _dirtyHandlerSet.Remove(handler);
                _dirtyHandlerTicks.Remove(handler);
            }

            if (write < _dirtyHandlers.Count)
            {
                _dirtyHandlers.RemoveRange(write, _dirtyHandlers.Count - write);
            }
        }

        private int CountDirtyEmptyTypedHandlerSlots()
        {
            int count = 0;
            for (int i = 0; i < _dirtyHandlers.Count; ++i)
            {
                MessageHandler handler = _dirtyHandlers[i];
                if (handler != null && _dirtyHandlerSet.Contains(handler))
                {
                    count += handler.CountEmptyTypedSlotsForSweep(this);
                }
            }

            return count;
        }

        private static int CountOccupiedInterceptorTypeSlots(
            MessageCache<InterceptorCache<object>> cache
        )
        {
            int count = 0;
            foreach (InterceptorCache<object> entry in cache)
            {
                if (entry != null)
                {
                    count++;
                }
            }

            return count;
        }

        internal void ResetState()
        {
            ResetTypedSlotsForReferencedHandlers();
            _emissionId = 0;
            _scopedEmissionId = 0;
            _tickCounter = 0;
            _diagnosticsMode = ShouldEnableDiagnostics();
            _loggedReflexiveWarning = false;
            BumpResetGeneration();

            ClearScalarSink(_scalarSinks[BusSinkIndex.UntargetedHandleDefault]);
            ClearScalarSink(_scalarSinks[BusSinkIndex.BroadcastHandleWithoutContext]);
            ClearScalarSink(_scalarSinks[BusSinkIndex.TargetedHandleWithoutContext]);
            ClearAndReturnContextSink(_contextSinks[BusContextIndex.TargetedHandleDefault]);
            ClearAndReturnContextSink(_contextSinks[BusContextIndex.BroadcastHandleDefault]);
            ClearScalarSink(_scalarSinks[BusSinkIndex.UntargetedPostProcessDefault]);
            ClearAndReturnContextSink(_contextSinks[BusContextIndex.TargetedPostProcessDefault]);
            ClearAndReturnContextSink(_contextSinks[BusContextIndex.BroadcastPostProcessDefault]);
            ClearScalarSink(_scalarSinks[BusSinkIndex.TargetedPostProcessWithoutContext]);
            ClearScalarSink(_scalarSinks[BusSinkIndex.BroadcastPostProcessWithoutContext]);
            if (_dispatchDepth > 0)
            {
                // BusGlobalSlot.Clear() resets its dispatch states inline,
                // which would release an in-flight global accept-all
                // snapshot's pooled arrays mid-iteration. Detach the states
                // first so the release runs after the outermost dispatch
                // lease exits; see FlushDeferredResetTeardown.
                DeferDispatchStateReset(ref _globalSlots.untargetedDispatchState);
                DeferDispatchStateReset(ref _globalSlots.targetedDispatchState);
                DeferDispatchStateReset(ref _globalSlots.broadcastDispatchState);
            }

            _globalSlots.Clear();

            _untargetedInterceptsByType.Clear();
            _targetedInterceptsByType.Clear();
            _broadcastInterceptsByType.Clear();
            _uniqueInterceptorsAndPriorities.Clear();
            _untargetedBroadcastMethodsByType.Clear();
            _targetedBroadcastMethodsByType.Clear();
            _sourcedBroadcastMethodsByType.Clear();
            _innerInterceptorsStack.Clear();
            _methodCache.Clear();
            _dirtyTypes.Clear();
            ReturnAllDirtyTargetCollections();
            _dirtyTargets.Clear();
            _dirtyTypeSet.Clear();
            _dirtyTargetSets.Clear();
            _dirtyTargetHighWaterCounts.Clear();
            _contextMapHighWaterCounts.Clear();
            _dirtyHandlers.Clear();
            _dirtyHandlerSet.Clear();
            _dirtyHandlerTicks.Clear();
            _globalSlotSweepCandidate = false;
            _lastSweepSeconds = _clock.NowSeconds;

#if UNITY_2021_3_OR_NEWER
            _recipientCache.Clear();
            _componentCache.Clear();
#endif

            bool enabled = _log.Enabled;
            _log.Clear();
            _log.Enabled = enabled;
            _emissionBuffer.Resize(GlobalMessageBufferSize);
            _emissionBuffer.Clear();
        }

        private void ResetTypedSlotsForReferencedHandlers()
        {
            HashSet<MessageHandler> handlers = new HashSet<MessageHandler>();
            AddHandlersFromScalarSinks(handlers);
            AddHandlersFromContextSinks(handlers);

            foreach (MessageHandler handler in _globalSlots.sharedHandlers.Keys)
            {
                handlers.Add(handler);
            }

            foreach (MessageHandler handler in handlers)
            {
                handler.ResetAllTypedSlotsForBusReset(this);
            }
        }

        private void AddHandlersFromScalarSinks(HashSet<MessageHandler> handlers)
        {
            foreach (MessageCache<HandlerCache<int, HandlerCache>> sink in _scalarSinks)
            {
                if (sink == null)
                {
                    continue;
                }

                foreach (HandlerCache<int, HandlerCache> handlersByPriority in sink)
                {
                    AddHandlersFromPriorityCache(handlersByPriority, handlers);
                }
            }
        }

        private void AddHandlersFromContextSinks(HashSet<MessageHandler> handlers)
        {
            foreach (
                MessageCache<
                    Dictionary<InstanceId, HandlerCache<int, HandlerCache>>
                > sink in _contextSinks
            )
            {
                foreach (
                    Dictionary<
                        InstanceId,
                        HandlerCache<int, HandlerCache>
                    > handlersByContext in sink
                )
                {
                    foreach (
                        HandlerCache<
                            int,
                            HandlerCache
                        > handlersByPriority in handlersByContext.Values
                    )
                    {
                        AddHandlersFromPriorityCache(handlersByPriority, handlers);
                    }
                }
            }
        }

        private static void AddHandlersFromPriorityCache(
            HandlerCache<int, HandlerCache> handlersByPriority,
            HashSet<MessageHandler> handlers
        )
        {
            if (handlersByPriority == null)
            {
                return;
            }

            foreach (HandlerCache cache in handlersByPriority.handlers.Values)
            {
                foreach (MessageHandler handler in cache.handlers.Keys)
                {
                    handlers.Add(handler);
                }
            }
        }

        /// <inheritdoc />
        public Action RegisterUntargeted<T>(MessageHandler messageHandler, int priority = 0)
            where T : IUntargetedMessage
        {
            EnsureAotUntargetedBridge<T>();
            return InternalRegisterUntargeted<T>(
                messageHandler,
                _scalarSinks[BusSinkIndex.UntargetedHandleDefault],
                RegistrationMethod.Untargeted,
                priority
            );
        }

        /// <inheritdoc />
        public Action RegisterTargeted<T>(
            InstanceId target,
            MessageHandler messageHandler,
            int priority = 0
        )
            where T : ITargetedMessage
        {
            EnsureAotTargetedBridge<T>();
            return InternalRegisterWithContext<T>(
                target,
                messageHandler,
                _contextSinks[BusContextIndex.TargetedHandleDefault],
                RegistrationMethod.Targeted,
                priority
            );
        }

        /// <inheritdoc />
        public Action RegisterSourcedBroadcast<T>(
            InstanceId source,
            MessageHandler messageHandler,
            int priority = 0
        )
            where T : IBroadcastMessage
        {
            EnsureAotSourcedBridge<T>();
            return InternalRegisterWithContext<T>(
                source,
                messageHandler,
                _contextSinks[BusContextIndex.BroadcastHandleDefault],
                RegistrationMethod.Broadcast,
                priority
            );
        }

        /// <inheritdoc />
        public Action RegisterSourcedBroadcastWithoutSource<T>(
            MessageHandler messageHandler,
            int priority = 0
        )
            where T : IBroadcastMessage
        {
            EnsureAotSourcedBridge<T>();
            return InternalRegisterUntargeted<T>(
                messageHandler,
                _scalarSinks[BusSinkIndex.BroadcastHandleWithoutContext],
                RegistrationMethod.BroadcastWithoutSource,
                priority
            );
        }

        /// <inheritdoc />
        public Action RegisterTargetedWithoutTargeting<T>(
            MessageHandler messageHandler,
            int priority = 0
        )
            where T : ITargetedMessage
        {
            EnsureAotTargetedBridge<T>();
            return InternalRegisterUntargeted<T>(
                messageHandler,
                _scalarSinks[BusSinkIndex.TargetedHandleWithoutContext],
                RegistrationMethod.TargetedWithoutTargeting,
                priority
            );
        }

        /// <inheritdoc />
        public Action RegisterGlobalAcceptAll(MessageHandler messageHandler)
        {
            long touchTick = AdvanceTick();
            _globalSlots.lastTouchTicks = touchTick;
            _globalSlots.version++;
            int count = _globalSlots.sharedHandlers.GetValueOrDefault(messageHandler, 0);

            Type type = typeof(IMessage);
            _globalSlots.sharedHandlers[messageHandler] = count + 1;
            // liveCount mirrors sharedHandlers.Count at every stable
            // observation point; only newly-inserted handlers (the 0 -> 1
            // transition in the per-handler refcount) advance it. See
            // BusGlobalSlot.liveCount xmldoc for the full invariant.
            if (count == 0)
            {
                _globalSlots.liveCount++;
            }
            _log.Log(
                new MessagingRegistration(
                    messageHandler.owner,
                    type,
                    RegistrationType.Register,
                    RegistrationMethod.GlobalAcceptAll
                )
            );

            StageGlobalDispatchSnapshot<IUntargetedMessage>(
                this,
                _globalSlots,
                DispatchKind.Untargeted
            );
            StageGlobalDispatchSnapshot<ITargetedMessage>(
                this,
                _globalSlots,
                DispatchKind.Targeted
            );
            StageGlobalDispatchSnapshot<IBroadcastMessage>(
                this,
                _globalSlots,
                DispatchKind.Broadcast
            );
            DebugAssertGlobalLiveCount();

            long capturedGeneration = _resetGeneration;
            long capturedSweepGeneration = _globalSlotSweepGeneration;
            return () =>
            {
                // Generation guard: see InternalRegisterUntargeted for the
                // rationale. Skip silently when the closure outlived a Reset.
                if (
                    capturedGeneration != _resetGeneration
                    || capturedSweepGeneration != _globalSlotSweepGeneration
                )
                {
                    return;
                }

                long deregisterTouchTick = AdvanceTick();
                _globalSlots.version++;
                _log.Log(
                    new MessagingRegistration(
                        messageHandler.owner,
                        type,
                        RegistrationType.Deregister,
                        RegistrationMethod.GlobalAcceptAll
                    )
                );
                if (!_globalSlots.sharedHandlers.TryGetValue(messageHandler, out count))
                {
                    if (MessagingDebug.enabled)
                    {
                        MessagingDebug.Log(
                            LogLevel.Error,
                            "Received over-deregistration of GlobalAcceptAll for MessageHandler {0}. Check to make sure you're not calling (de)registration multiple times.",
                            messageHandler
                        );
                    }

                    return;
                }

                _globalSlots.lastTouchTicks = deregisterTouchTick;
                if (count <= 1)
                {
                    _ = _globalSlots.sharedHandlers.Remove(messageHandler);
                    MarkDirtyHandler(messageHandler);
                    _globalSlotSweepCandidate = true;
                    // Final-removal of this handler from sharedHandlers is the
                    // 1 -> 0 transition that mirrors back into liveCount.
                    // Partial deregistration (count > 1) leaves liveCount
                    // alone -- the dictionary entry is still present.
                    _globalSlots.liveCount--;
                }
                else
                {
                    _globalSlots.sharedHandlers[messageHandler] = count - 1;
                }

                StageGlobalDispatchSnapshot<IUntargetedMessage>(
                    this,
                    _globalSlots,
                    DispatchKind.Untargeted
                );
                StageGlobalDispatchSnapshot<ITargetedMessage>(
                    this,
                    _globalSlots,
                    DispatchKind.Targeted
                );
                StageGlobalDispatchSnapshot<IBroadcastMessage>(
                    this,
                    _globalSlots,
                    DispatchKind.Broadcast
                );
                DebugAssertGlobalLiveCount();
            };
        }

        /// <inheritdoc />
        public Action RegisterUntargetedInterceptor<T>(
            UntargetedInterceptor<T> interceptor,
            int priority = 0
        )
            where T : IUntargetedMessage
        {
            EnsureAotUntargetedBridge<T>();
            _ = AdvanceTick();
            InterceptorCache<object> prioritizedInterceptors =
                _untargetedInterceptsByType.GetOrAdd<T>();
            InterceptorCache<object> capturedInterceptors = prioritizedInterceptors;
            prioritizedInterceptors.lastTouchTicks = _tickCounter;
            MarkDirtyType<T>();

            if (
                !_uniqueInterceptorsAndPriorities.TryGetValue(
                    interceptor,
                    out Dictionary<int, int> priorityCount
                )
            )
            {
                priorityCount = new Dictionary<int, int>();
                _uniqueInterceptorsAndPriorities[interceptor] = priorityCount;
            }

            if (
                !prioritizedInterceptors.handlers.TryGetValue(
                    priority,
                    out List<object> interceptors
                )
            )
            {
                interceptors = new List<object>();
                prioritizedInterceptors.handlers.Add(priority, interceptors);
            }

            if (!priorityCount.TryGetValue(priority, out int count))
            {
                count = 0;
                interceptors.Add(interceptor);
            }

            priorityCount[priority] = count + 1;

            Type type = typeof(T);
            _log.Log(
                new MessagingRegistration(
                    InstanceId.EmptyId,
                    type,
                    RegistrationType.Register,
                    RegistrationMethod.Interceptor
                )
            );

            long capturedGeneration = _resetGeneration;
            return () =>
            {
                // Generation guard: see InternalRegisterUntargeted for the
                // rationale. Skip silently when the closure outlived a Reset.
                if (capturedGeneration != _resetGeneration)
                {
                    return;
                }
                if (
                    IsStaleInterceptorDeregisterAfterSweep<T>(
                        _untargetedInterceptsByType,
                        capturedInterceptors
                    )
                )
                {
                    return;
                }

                _ = AdvanceTick();
                prioritizedInterceptors.lastTouchTicks = _tickCounter;
                MarkDirtyType<T>();
                _log.Log(
                    new MessagingRegistration(
                        InstanceId.EmptyId,
                        type,
                        RegistrationType.Deregister,
                        RegistrationMethod.Interceptor
                    )
                );
                bool removed = false;
                if (_uniqueInterceptorsAndPriorities.TryGetValue(interceptor, out priorityCount))
                {
                    if (priorityCount.TryGetValue(priority, out count))
                    {
                        if (1 < count)
                        {
                            priorityCount[priority] = count - 1;
                        }
                        else
                        {
                            removed = true;
                            _ = priorityCount.Remove(priority);
                        }
                    }

                    if (priorityCount.Count == 0)
                    {
                        _uniqueInterceptorsAndPriorities.Remove(interceptor);
                    }
                }
                else if (MessagingDebug.enabled)
                {
                    MessagingDebug.Log(
                        LogLevel.Error,
                        "Received over-deregistration of Interceptor {0}. Check to make sure you're not calling (de)registration multiple times.",
                        interceptor
                    );
                }

                bool complete = false;
                if (removed)
                {
                    if (_untargetedInterceptsByType.TryGetValue<T>(out prioritizedInterceptors))
                    {
                        if (
                            prioritizedInterceptors.handlers.TryGetValue(
                                priority,
                                out List<object> interceptors
                            )
                        )
                        {
                            complete = interceptors.Remove(interceptor);
                            if (interceptors.Count == 0)
                            {
                                _ = prioritizedInterceptors.handlers.Remove(priority);
                            }
                        }
                    }

                    if (!complete && MessagingDebug.enabled)
                    {
                        MessagingDebug.Log(
                            LogLevel.Error,
                            "Received over-deregistration of Interceptor {0}. Check to make sure you're not calling (de)registration multiple times.",
                            interceptor
                        );
                    }
                }
            };
        }

        /// <inheritdoc />
        public Action RegisterTargetedInterceptor<T>(
            TargetedInterceptor<T> interceptor,
            int priority = 0
        )
            where T : ITargetedMessage
        {
            EnsureAotTargetedBridge<T>();
            _ = AdvanceTick();
            InterceptorCache<object> prioritizedInterceptors =
                _targetedInterceptsByType.GetOrAdd<T>();
            InterceptorCache<object> capturedInterceptors = prioritizedInterceptors;
            prioritizedInterceptors.lastTouchTicks = _tickCounter;
            MarkDirtyType<T>();

            if (
                !_uniqueInterceptorsAndPriorities.TryGetValue(
                    interceptor,
                    out Dictionary<int, int> priorityCount
                )
            )
            {
                priorityCount = new Dictionary<int, int>();
                _uniqueInterceptorsAndPriorities[interceptor] = priorityCount;
            }

            if (
                !prioritizedInterceptors.handlers.TryGetValue(
                    priority,
                    out List<object> interceptors
                )
            )
            {
                interceptors = new List<object>();
                prioritizedInterceptors.handlers.Add(priority, interceptors);
            }

            if (!priorityCount.TryGetValue(priority, out int count))
            {
                count = 0;
                interceptors.Add(interceptor);
            }

            priorityCount[priority] = count + 1;

            Type type = typeof(T);
            _log.Log(
                new MessagingRegistration(
                    InstanceId.EmptyId,
                    type,
                    RegistrationType.Register,
                    RegistrationMethod.Interceptor
                )
            );

            long capturedGeneration = _resetGeneration;
            return () =>
            {
                // Generation guard: see InternalRegisterUntargeted for the
                // rationale. Skip silently when the closure outlived a Reset.
                if (capturedGeneration != _resetGeneration)
                {
                    return;
                }
                if (
                    IsStaleInterceptorDeregisterAfterSweep<T>(
                        _targetedInterceptsByType,
                        capturedInterceptors
                    )
                )
                {
                    return;
                }

                _ = AdvanceTick();
                prioritizedInterceptors.lastTouchTicks = _tickCounter;
                MarkDirtyType<T>();
                _log.Log(
                    new MessagingRegistration(
                        InstanceId.EmptyId,
                        type,
                        RegistrationType.Deregister,
                        RegistrationMethod.Interceptor
                    )
                );
                bool removed = false;
                if (_uniqueInterceptorsAndPriorities.TryGetValue(interceptor, out priorityCount))
                {
                    if (priorityCount.TryGetValue(priority, out count))
                    {
                        if (1 < count)
                        {
                            priorityCount[priority] = count - 1;
                        }
                        else
                        {
                            removed = true;
                            _ = priorityCount.Remove(priority);
                        }
                    }

                    if (priorityCount.Count == 0)
                    {
                        _uniqueInterceptorsAndPriorities.Remove(interceptor);
                    }
                }
                else if (MessagingDebug.enabled)
                {
                    MessagingDebug.Log(
                        LogLevel.Error,
                        "Received over-deregistration of Interceptor {0}. Check to make sure you're not calling (de)registration multiple times.",
                        interceptor
                    );
                }

                bool complete = false;
                if (removed)
                {
                    if (_targetedInterceptsByType.TryGetValue<T>(out prioritizedInterceptors))
                    {
                        if (
                            prioritizedInterceptors.handlers.TryGetValue(
                                priority,
                                out List<object> interceptors
                            )
                        )
                        {
                            complete = interceptors.Remove(interceptor);
                            if (interceptors.Count == 0)
                            {
                                _ = prioritizedInterceptors.handlers.Remove(priority);
                            }
                        }
                    }

                    if (!complete && MessagingDebug.enabled)
                    {
                        MessagingDebug.Log(
                            LogLevel.Error,
                            "Received over-deregistration of Interceptor {0}. Check to make sure you're not calling (de)registration multiple times.",
                            interceptor
                        );
                    }
                }
            };
        }

        /// <inheritdoc />
        public Action RegisterBroadcastInterceptor<T>(
            BroadcastInterceptor<T> interceptor,
            int priority = 0
        )
            where T : IBroadcastMessage
        {
            EnsureAotSourcedBridge<T>();
            _ = AdvanceTick();
            InterceptorCache<object> prioritizedInterceptors =
                _broadcastInterceptsByType.GetOrAdd<T>();
            InterceptorCache<object> capturedInterceptors = prioritizedInterceptors;
            prioritizedInterceptors.lastTouchTicks = _tickCounter;
            MarkDirtyType<T>();

            if (
                !_uniqueInterceptorsAndPriorities.TryGetValue(
                    interceptor,
                    out Dictionary<int, int> priorityCount
                )
            )
            {
                priorityCount = new Dictionary<int, int>();
                _uniqueInterceptorsAndPriorities[interceptor] = priorityCount;
            }

            if (
                !prioritizedInterceptors.handlers.TryGetValue(
                    priority,
                    out List<object> interceptors
                )
            )
            {
                interceptors = new List<object>();
                prioritizedInterceptors.handlers.Add(priority, interceptors);
            }

            if (!priorityCount.TryGetValue(priority, out int count))
            {
                count = 0;
                interceptors.Add(interceptor);
            }

            priorityCount[priority] = count + 1;

            Type type = typeof(T);
            _log.Log(
                new MessagingRegistration(
                    InstanceId.EmptyId,
                    type,
                    RegistrationType.Register,
                    RegistrationMethod.Interceptor
                )
            );

            long capturedGeneration = _resetGeneration;
            return () =>
            {
                // Generation guard: see InternalRegisterUntargeted for the
                // rationale. Skip silently when the closure outlived a Reset.
                if (capturedGeneration != _resetGeneration)
                {
                    return;
                }
                if (
                    IsStaleInterceptorDeregisterAfterSweep<T>(
                        _broadcastInterceptsByType,
                        capturedInterceptors
                    )
                )
                {
                    return;
                }

                _ = AdvanceTick();
                prioritizedInterceptors.lastTouchTicks = _tickCounter;
                MarkDirtyType<T>();
                _log.Log(
                    new MessagingRegistration(
                        InstanceId.EmptyId,
                        type,
                        RegistrationType.Deregister,
                        RegistrationMethod.Interceptor
                    )
                );
                bool removed = false;
                if (_uniqueInterceptorsAndPriorities.TryGetValue(interceptor, out priorityCount))
                {
                    if (priorityCount.TryGetValue(priority, out count))
                    {
                        if (1 < count)
                        {
                            priorityCount[priority] = count - 1;
                        }
                        else
                        {
                            removed = true;
                            _ = priorityCount.Remove(priority);
                        }
                    }

                    if (priorityCount.Count == 0)
                    {
                        _uniqueInterceptorsAndPriorities.Remove(interceptor);
                    }
                }
                else if (MessagingDebug.enabled)
                {
                    MessagingDebug.Log(
                        LogLevel.Error,
                        "Received over-deregistration of Interceptor {0}. Check to make sure you're not calling (de)registration multiple times.",
                        interceptor
                    );
                }

                bool complete = false;
                if (removed)
                {
                    if (_broadcastInterceptsByType.TryGetValue<T>(out prioritizedInterceptors))
                    {
                        if (
                            prioritizedInterceptors.handlers.TryGetValue(
                                priority,
                                out List<object> interceptors
                            )
                        )
                        {
                            complete = interceptors.Remove(interceptor);
                            if (interceptors.Count == 0)
                            {
                                _ = prioritizedInterceptors.handlers.Remove(priority);
                            }
                        }
                    }

                    if (!complete && MessagingDebug.enabled)
                    {
                        MessagingDebug.Log(
                            LogLevel.Error,
                            "Received over-deregistration of Interceptor {0}. Check to make sure you're not calling (de)registration multiple times.",
                            interceptor
                        );
                    }
                }
            };
        }

        private bool IsStaleInterceptorDeregisterAfterSweep<T>(
            MessageCache<InterceptorCache<object>> interceptorsByType,
            InterceptorCache<object> capturedInterceptors
        )
            where T : IMessage
        {
            return !interceptorsByType.TryGetValue<T>(
                    out InterceptorCache<object> currentInterceptors
                ) || !ReferenceEquals(currentInterceptors, capturedInterceptors);
        }

        /// <inheritdoc />
        public Action RegisterUntargetedPostProcessor<T>(
            MessageHandler messageHandler,
            int priority = 0
        )
            where T : IUntargetedMessage
        {
            EnsureAotUntargetedBridge<T>();
            return InternalRegisterUntargeted<T>(
                messageHandler,
                _scalarSinks[BusSinkIndex.UntargetedPostProcessDefault],
                RegistrationMethod.UntargetedPostProcessor,
                priority
            );
        }

        /// <inheritdoc />
        public Action RegisterTargetedPostProcessor<T>(
            InstanceId target,
            MessageHandler messageHandler,
            int priority = 0
        )
            where T : ITargetedMessage
        {
            EnsureAotTargetedBridge<T>();
            return InternalRegisterWithContext<T>(
                target,
                messageHandler,
                _contextSinks[BusContextIndex.TargetedPostProcessDefault],
                RegistrationMethod.TargetedPostProcessor,
                priority
            );
        }

        /// <inheritdoc />
        public Action RegisterTargetedWithoutTargetingPostProcessor<T>(
            MessageHandler messageHandler,
            int priority = 0
        )
            where T : ITargetedMessage
        {
            EnsureAotTargetedBridge<T>();
            return InternalRegisterUntargeted<T>(
                messageHandler,
                _scalarSinks[BusSinkIndex.TargetedPostProcessWithoutContext],
                RegistrationMethod.TargetedWithoutTargetingPostProcessor,
                priority
            );
        }

        /// <inheritdoc />
        public Action RegisterBroadcastPostProcessor<T>(
            InstanceId source,
            MessageHandler messageHandler,
            int priority = 0
        )
            where T : IBroadcastMessage
        {
            EnsureAotSourcedBridge<T>();
            return InternalRegisterWithContext<T>(
                source,
                messageHandler,
                _contextSinks[BusContextIndex.BroadcastPostProcessDefault],
                RegistrationMethod.BroadcastPostProcessor,
                priority
            );
        }

        /// <inheritdoc />
        public Action RegisterBroadcastWithoutSourcePostProcessor<T>(
            MessageHandler messageHandler,
            int priority = 0
        )
            where T : IBroadcastMessage
        {
            EnsureAotSourcedBridge<T>();
            return InternalRegisterUntargeted<T>(
                messageHandler,
                _scalarSinks[BusSinkIndex.BroadcastPostProcessWithoutContext],
                RegistrationMethod.BroadcastWithoutSourcePostProcessor,
                priority
            );
        }

        // Legacy RegisterInterceptor removed in favor of split implementations above

        /// <inheritdoc />
        public void UntypedUntargetedBroadcast(IUntargetedMessage typedMessage)
        {
            Type messageType = typedMessage.MessageType;
            if (RequiresAotUntypedDispatch)
            {
                if (
                    Volatile
                        .Read(ref _aotUntargetedBroadcastsByType)
                        .TryGetValue(messageType, out var bridge)
                )
                {
                    bridge(this, typedMessage);
                    return;
                }

                ThrowMissingAotBridge(messageType, "untargeted");
                return;
            }

            if (
                !_untargetedBroadcastMethodsByType.TryGetValue(
                    messageType,
                    out Action<IUntargetedMessage> broadcast
                )
            )
            {
                broadcast = CreateUntargetedBroadcastDelegate(messageType);
                _untargetedBroadcastMethodsByType[messageType] = broadcast;
            }
            broadcast.Invoke(typedMessage);
        }

        /// <inheritdoc />
        public void UntargetedBroadcast<TMessage>(ref TMessage typedMessage)
            where TMessage : IUntargetedMessage
        {
            EnsureAotUntargetedBridge<TMessage>();
            TrySweepIdle();
            using DispatchLease dispatchLease = EnterDispatch();
            unchecked
            {
                _emissionId++;
                _scopedEmissionId = _emissionId;
            }
            long touchTick = AdvanceTick();
            if (_diagnosticsMode)
            {
                _emissionBuffer.Add(new MessageEmissionData(typedMessage));
            }

            // Pre-freeze the post-processing snapshot for this emission so
            // mutations during handlers/post-processors are not observed
            // until the next emission. Acquiring the snapshot here (before
            // interceptors and handlers run) is sufficient: the snapshot's
            // flat entry array was fully resolved at build time, so no lazy
            // per-handler cache read remains to observe a mid-emission
            // registration.
            DispatchSnapshot untargetedPostSnapshot = DispatchSnapshot.Empty;
            if (
                _scalarSinks[BusSinkIndex.UntargetedPostProcessDefault]
                    .TryGetValue<TMessage>(
                        out HandlerCache<int, HandlerCache> untargetedPostHandlers
                    )
                && untargetedPostHandlers.handlers.Count > 0
            )
            {
                Touch(untargetedPostHandlers, touchTick);
                untargetedPostSnapshot = AcquireDispatchSnapshot<TMessage>(
                    this,
                    untargetedPostHandlers,
                    UntargetedPostSlot,
                    _scopedEmissionId,
                    default
                );
            }

            if (!RunUntargetedInterceptors(ref typedMessage))
            {
                return;
            }

            if (0 < _globalSlots.sharedHandlers.Count)
            {
                IUntargetedMessage untargetedMessage = typedMessage;
                BroadcastGlobalUntargeted(ref untargetedMessage);
            }

            bool foundAnyHandlers = InternalUntargetedBroadcast(ref typedMessage);

            if (
                _scalarSinks[BusSinkIndex.UntargetedPostProcessDefault]
                    .TryGetValue<TMessage>(out HandlerCache<int, HandlerCache> sortedHandlers)
                && 0 < sortedHandlers.handlers.Count
            )
            {
                Touch(sortedHandlers, touchTick);
                DispatchSnapshot snapshot = untargetedPostSnapshot.IsEmpty
                    ? AcquireDispatchSnapshot<TMessage>(
                        this,
                        sortedHandlers,
                        UntargetedPostSlot,
                        _scopedEmissionId,
                        default
                    )
                    : untargetedPostSnapshot;
                // Flat dispatch; see DispatchFlatSnapshot for the
                // frozen-array semantics that replace prefreeze stamping and
                // for the reset-generation guard rationale.
                if (DispatchFlatSnapshot(snapshot, ref typedMessage))
                {
                    foundAnyHandlers = true;
                }
            }

            if (!foundAnyHandlers && MessagingDebug.enabled)
            {
                MessagingDebug.Log(
                    LogLevel.Info,
                    "Could not find a matching untargeted broadcast handler for Message: {0}.",
                    typedMessage
                );
            }
        }

        /// <inheritdoc />
        public void UntypedTargetedBroadcast(InstanceId target, ITargetedMessage typedMessage)
        {
            Type messageType = typedMessage.MessageType;
            if (RequiresAotUntypedDispatch)
            {
                if (
                    Volatile
                        .Read(ref _aotTargetedBroadcastsByType)
                        .TryGetValue(messageType, out var bridge)
                )
                {
                    bridge(this, target, typedMessage);
                    return;
                }

                ThrowMissingAotBridge(messageType, "targeted");
                return;
            }

            if (
                !_targetedBroadcastMethodsByType.TryGetValue(
                    messageType,
                    out Action<InstanceId, ITargetedMessage> broadcast
                )
            )
            {
                broadcast = CreateTargetedBroadcastDelegate(messageType);
                _targetedBroadcastMethodsByType[messageType] = broadcast;
            }
            broadcast.Invoke(target, typedMessage);
        }

        /// <inheritdoc />
        public void TargetedBroadcast<TMessage>(ref InstanceId target, ref TMessage typedMessage)
            where TMessage : ITargetedMessage
        {
            EnsureAotTargetedBridge<TMessage>();
            TrySweepIdle();
            using DispatchLease dispatchLease = EnterDispatch();
            unchecked
            {
                _emissionId++;
                _scopedEmissionId = _emissionId;
            }
            long touchTick = AdvanceTick();
            if (_diagnosticsMode)
            {
                _emissionBuffer.Add(new MessageEmissionData(typedMessage, target));
            }

            // Pre-freeze targeted post-processing for this emission
            // (target-specific and without targeting). Acquiring the snapshot
            // here (before interceptors and handlers run) is sufficient: the
            // snapshot's flat entry array was fully resolved at build time,
            // so no lazy per-handler cache read remains to observe a
            // mid-emission registration - no prefreeze stamping needed.
            DispatchSnapshot targetedPostSnapshot = DispatchSnapshot.Empty;
            DispatchSnapshot targetedWithoutTargetingPostSnapshot = DispatchSnapshot.Empty;
            if (
                _contextSinks[BusContextIndex.TargetedPostProcessDefault]
                    .TryGetValue<TMessage>(
                        out Dictionary<
                            InstanceId,
                            HandlerCache<int, HandlerCache>
                        > targetedPostHandlers
                    )
                && targetedPostHandlers.TryGetValue(
                    target,
                    out HandlerCache<int, HandlerCache> targetedPostByPriority
                )
                && targetedPostByPriority.handlers.Count > 0
            )
            {
                Touch(targetedPostByPriority, touchTick);
                targetedPostSnapshot = AcquireDispatchSnapshot<TMessage>(
                    this,
                    targetedPostByPriority,
                    TargetedPostSlot,
                    _scopedEmissionId,
                    target
                );
            }
            if (
                _scalarSinks[BusSinkIndex.TargetedPostProcessWithoutContext]
                    .TryGetValue<TMessage>(
                        out HandlerCache<int, HandlerCache> targetedWithoutTargetingHandlers
                    )
                && targetedWithoutTargetingHandlers.handlers.Count > 0
            )
            {
                Touch(targetedWithoutTargetingHandlers, touchTick);
                targetedWithoutTargetingPostSnapshot = AcquireDispatchSnapshot<TMessage>(
                    this,
                    targetedWithoutTargetingHandlers,
                    TargetedWithoutContextPostSlot,
                    _scopedEmissionId,
                    default
                );
            }

            // Capture the pre-interceptor target so post-processing can detect a
            // rewritten id and re-resolve its snapshot against the final target.
            InstanceId preInterceptorTarget = target;
            if (!RunTargetedInterceptors(ref typedMessage, ref target))
            {
                return;
            }

            if (0 < _globalSlots.sharedHandlers.Count)
            {
                ITargetedMessage targetedMessage = typedMessage;
                BroadcastGlobalTargeted(ref target, ref targetedMessage);
            }

            bool foundAnyHandlers = false;

            if (typeof(TMessage) == typeof(ReflexiveMessage))
            {
                if (!_loggedReflexiveWarning)
                {
                    _loggedReflexiveWarning = true;
                    if (MessagingDebug.enabled)
                    {
                        MessagingDebug.Log(
                            LogLevel.Warn,
                            "ReflexiveMessage dispatch traverses the Unity hierarchy and is significantly slower than typed messages. Prefer targeted or broadcast messages where possible."
                        );
                    }
                }
#if UNITY_2021_3_OR_NEWER
                ref ReflexiveMessage reflexiveMessage = ref DxUnsafe.As<TMessage, ReflexiveMessage>(
                    ref typedMessage
                );

                GameObject go;
                bool found;
                UnityEngine.Object targetObject = target.Object;
                switch (targetObject)
                {
                    case GameObject gameObject:
                    {
                        found = true;
                        go = gameObject;
                        break;
                    }
                    case Component component:
                    {
                        found = true;
                        go = component.gameObject;
                        break;
                    }
                    default:
                    {
                        go = null;
                        found = false;
                        break;
                    }
                }

                if (found)
                {
                    _recipientCache.Clear();
                    bool sentInADirection = false;
                    ReflexiveSendMode sendMode = reflexiveMessage.sendMode;
                    if (sendMode.HasFlagNoAlloc(ReflexiveSendMode.Upwards))
                    {
                        sentInADirection = true;
                        if (
                            !sendMode.HasFlagNoAlloc(ReflexiveSendMode.Downwards)
                            && !sendMode.HasFlagNoAlloc(ReflexiveSendMode.Flat)
                            && !sendMode.HasFlagNoAlloc(ReflexiveSendMode.OnlyIncludeActive)
                        )
                        {
                            switch (reflexiveMessage.parameters.Length)
                            {
                                case 0:
                                {
                                    go.SendMessageUpwards(reflexiveMessage.method);
                                    break;
                                }
                                case 1:
                                {
                                    go.SendMessageUpwards(
                                        reflexiveMessage.method,
                                        reflexiveMessage.parameters[0]
                                    );
                                    break;
                                }
                                default:
                                {
                                    Transform current = go.transform;
                                    do
                                    {
                                        _componentCache.Clear();
                                        current.GetComponents(_componentCache);
                                        for (int i = 0; i < _componentCache.Count; ++i)
                                        {
                                            MonoBehaviour script = _componentCache[i];
                                            SendMessage(script, ref reflexiveMessage, false);
                                        }
                                        current = current.parent;
                                    } while (current != null);

                                    break;
                                }
                            }
                        }
                        else
                        {
                            Transform current = go.transform;
                            do
                            {
                                _componentCache.Clear();
                                current.GetComponents(_componentCache);
                                for (int i = 0; i < _componentCache.Count; ++i)
                                {
                                    MonoBehaviour script = _componentCache[i];
                                    SendMessage(script, ref reflexiveMessage, true);
                                }
                                current = current.parent;
                            } while (current != null);
                        }
                    }
                    if (sendMode.HasFlagNoAlloc(ReflexiveSendMode.Downwards))
                    {
                        if (
                            !sendMode.HasFlagNoAlloc(ReflexiveSendMode.Upwards)
                            && !sendMode.HasFlagNoAlloc(ReflexiveSendMode.Flat)
                            && !sendMode.HasFlagNoAlloc(ReflexiveSendMode.OnlyIncludeActive)
                        )
                        {
                            switch (reflexiveMessage.parameters.Length)
                            {
                                case 0:
                                {
                                    go.BroadcastMessage(reflexiveMessage.method);
                                    break;
                                }
                                case 1:
                                {
                                    go.BroadcastMessage(
                                        reflexiveMessage.method,
                                        reflexiveMessage.parameters[0]
                                    );
                                    break;
                                }
                                default:
                                {
                                    _componentCache.Clear();
                                    go.GetComponentsInChildren(true, _componentCache);
                                    for (int i = 0; i < _componentCache.Count; ++i)
                                    {
                                        MonoBehaviour parentComponent = _componentCache[i];
                                        SendMessage(parentComponent, ref reflexiveMessage, false);
                                    }

                                    break;
                                }
                            }
                        }
                        else
                        {
                            _componentCache.Clear();
                            go.GetComponentsInChildren(_componentCache);
                            for (int i = 0; i < _componentCache.Count; ++i)
                            {
                                MonoBehaviour parentComponent = _componentCache[i];
                                SendMessage(parentComponent, ref reflexiveMessage, true);
                            }
                        }
                    }
                    else if (!sentInADirection && sendMode.HasFlagNoAlloc(ReflexiveSendMode.Flat))
                    {
                        if (!sendMode.HasFlagNoAlloc(ReflexiveSendMode.OnlyIncludeActive))
                        {
                            switch (reflexiveMessage.parameters.Length)
                            {
                                case 0:
                                {
                                    go.SendMessage(reflexiveMessage.method);
                                    break;
                                }
                                case 1:
                                {
                                    go.SendMessage(
                                        reflexiveMessage.method,
                                        reflexiveMessage.parameters[0]
                                    );
                                    break;
                                }
                                default:
                                {
                                    _componentCache.Clear();
                                    go.GetComponents(_componentCache);
                                    for (int i = 0; i < _componentCache.Count; ++i)
                                    {
                                        MonoBehaviour component = _componentCache[i];
                                        SendMessage(component, ref reflexiveMessage, false);
                                    }

                                    break;
                                }
                            }
                        }
                        else
                        {
                            _componentCache.Clear();
                            go.GetComponents(_componentCache);
                            for (int i = 0; i < _componentCache.Count; ++i)
                            {
                                MonoBehaviour component = _componentCache[i];
                                SendMessage(component, ref reflexiveMessage, true);
                            }
                        }
                    }
                }
#else
                MessagingDebug.Log(
                    LogLevel.Error,
                    "Reflexive messages are not supported in this build."
                );
#endif
            }

            if (
                _contextSinks[BusContextIndex.TargetedHandleDefault]
                    .TryGetValue<TMessage>(
                        out Dictionary<InstanceId, HandlerCache<int, HandlerCache>> targetedHandlers
                    )
                && targetedHandlers.TryGetValue(
                    target,
                    out HandlerCache<int, HandlerCache> sortedHandlers
                )
                && sortedHandlers.handlers.Count > 0
            )
            {
                Touch(sortedHandlers, touchTick);
                DispatchSnapshot snapshot = AcquireDispatchSnapshot<TMessage>(
                    this,
                    sortedHandlers,
                    TargetedHandleSlot,
                    _scopedEmissionId,
                    target
                );
                // Flat dispatch; see DispatchFlatSnapshot for the
                // frozen-array semantics that replace the legacy
                // cross-priority prefreeze pass.
                if (DispatchFlatSnapshot(snapshot, ref typedMessage))
                {
                    foundAnyHandlers = true;
                }
            }

            if (InternalTargetedWithoutTargetingBroadcast(ref target, ref typedMessage))
            {
                foundAnyHandlers = true;
            }

            if (
                _contextSinks[BusContextIndex.TargetedPostProcessDefault]
                    .TryGetValue<TMessage>(out targetedHandlers)
                && targetedHandlers.TryGetValue(target, out sortedHandlers)
                && sortedHandlers.handlers.Count > 0
            )
            {
                // Post-processors follow the FINAL (post-interceptor) target. When an
                // interceptor rewrote the id, the pre-frozen snapshot is keyed by the
                // ORIGINAL target and must not be dispatched against the new one;
                // re-resolve for the rewritten target instead, mirroring the
                // handle-phase re-resolution above. Like that path, this exposes
                // registrations made mid-emission for the REWRITTEN id (its cache
                // had no snapshot pinned at emission start); the pre-frozen
                // snapshot - and its mid-emission registration gating - is
                // preferred only when the target is unchanged.
                DispatchSnapshot snapshot;
                if (target != preInterceptorTarget)
                {
                    snapshot = AcquireDispatchSnapshot<TMessage>(
                        this,
                        sortedHandlers,
                        TargetedPostSlot,
                        _scopedEmissionId,
                        target
                    );
                }
                else if (targetedPostSnapshot.IsEmpty)
                {
                    snapshot = AcquireDispatchSnapshot<TMessage>(
                        this,
                        sortedHandlers,
                        TargetedPostSlot,
                        _scopedEmissionId,
                        target
                    );
                }
                else
                {
                    snapshot = targetedPostSnapshot;
                }
                if (DispatchFlatSnapshot(snapshot, ref typedMessage))
                {
                    foundAnyHandlers = true;
                }
            }

            if (
                _scalarSinks[BusSinkIndex.TargetedPostProcessWithoutContext]
                    .TryGetValue<TMessage>(out HandlerCache<int, HandlerCache> postTwt)
                && postTwt.handlers.Count > 0
            )
            {
                DispatchSnapshot snapshot = targetedWithoutTargetingPostSnapshot.IsEmpty
                    ? AcquireDispatchSnapshot<TMessage>(
                        this,
                        postTwt,
                        TargetedWithoutContextPostSlot,
                        _scopedEmissionId,
                        default
                    )
                    : targetedWithoutTargetingPostSnapshot;
                if (DispatchContextFlatSnapshot(snapshot, ref target, ref typedMessage))
                {
                    foundAnyHandlers = true;
                }
            }

            if (!foundAnyHandlers && MessagingDebug.enabled)
            {
                MessagingDebug.Log(
                    LogLevel.Info,
                    "Could not find a matching targeted broadcast handler for Id: {0}, Message: {1}.",
                    target,
                    typedMessage
                );
            }
        }

        /// <inheritdoc />
        public void UntypedSourcedBroadcast(InstanceId source, IBroadcastMessage typedMessage)
        {
            Type messageType = typedMessage.MessageType;
            if (RequiresAotUntypedDispatch)
            {
                if (
                    Volatile
                        .Read(ref _aotSourcedBroadcastsByType)
                        .TryGetValue(messageType, out var bridge)
                )
                {
                    bridge(this, source, typedMessage);
                    return;
                }

                ThrowMissingAotBridge(messageType, "sourced broadcast");
                return;
            }

            if (
                !_sourcedBroadcastMethodsByType.TryGetValue(
                    messageType,
                    out Action<InstanceId, IBroadcastMessage> broadcast
                )
            )
            {
                broadcast = CreateSourcedBroadcastDelegate(messageType);
                _sourcedBroadcastMethodsByType[messageType] = broadcast;
            }
            broadcast.Invoke(source, typedMessage);
        }

        /// <inheritdoc />
        public void SourcedBroadcast<TMessage>(ref InstanceId source, ref TMessage typedMessage)
            where TMessage : IBroadcastMessage
        {
            EnsureAotSourcedBridge<TMessage>();
            TrySweepIdle();
            using DispatchLease dispatchLease = EnterDispatch();
            unchecked
            {
                _emissionId++;
                _scopedEmissionId = _emissionId;
            }
            long touchTick = AdvanceTick();
            if (_diagnosticsMode)
            {
                _emissionBuffer.Add(new MessageEmissionData(typedMessage, source));
            }

            // Pre-freeze broadcast post-processing for this emission
            // (source-specific and without source). Acquiring the snapshot
            // here (before interceptors and handlers run) is sufficient: the
            // snapshot's flat entry array was fully resolved at build time,
            // so no lazy per-handler cache read remains to observe a
            // mid-emission registration - no prefreeze stamping needed.
            DispatchSnapshot broadcastPostSnapshot = DispatchSnapshot.Empty;
            DispatchSnapshot broadcastWithoutSourcePostSnapshot = DispatchSnapshot.Empty;
            if (
                _contextSinks[BusContextIndex.BroadcastPostProcessDefault]
                    .TryGetValue<TMessage>(
                        out Dictionary<
                            InstanceId,
                            HandlerCache<int, HandlerCache>
                        > broadcastPostHandlers
                    )
                && broadcastPostHandlers.TryGetValue(
                    source,
                    out HandlerCache<int, HandlerCache> broadcastPostByPriority
                )
                && broadcastPostByPriority.handlers.Count > 0
            )
            {
                Touch(broadcastPostByPriority, touchTick);
                broadcastPostSnapshot = AcquireDispatchSnapshot<TMessage>(
                    this,
                    broadcastPostByPriority,
                    BroadcastPostSlot,
                    _scopedEmissionId,
                    source
                );
            }
            if (
                _scalarSinks[BusSinkIndex.BroadcastPostProcessWithoutContext]
                    .TryGetValue<TMessage>(
                        out HandlerCache<int, HandlerCache> broadcastWithoutSourceHandlers
                    )
                && broadcastWithoutSourceHandlers.handlers.Count > 0
            )
            {
                Touch(broadcastWithoutSourceHandlers, touchTick);
                broadcastWithoutSourcePostSnapshot = AcquireDispatchSnapshot<TMessage>(
                    this,
                    broadcastWithoutSourceHandlers,
                    BroadcastWithoutContextPostSlot,
                    _scopedEmissionId,
                    default
                );
            }

            // Capture the pre-interceptor source so post-processing can detect a
            // rewritten id and re-resolve its snapshot against the final source.
            InstanceId preInterceptorSource = source;
            if (!RunBroadcastInterceptors(ref typedMessage, ref source))
            {
                return;
            }

            if (0 < _globalSlots.sharedHandlers.Count)
            {
                IBroadcastMessage broadcastMessage = typedMessage;
                BroadcastGlobalSourcedBroadcast(ref source, ref broadcastMessage);
            }

            // Pre-freeze the broadcast-without-source HANDLE snapshot at the
            // point the legacy per-handler prefreeze pass ran (after
            // interceptors and the global walk, before the source-keyed
            // handle phase), so registrations made by a source-keyed handler
            // this emission are not observed - acquisition alone freezes the
            // fully-resolved flat array.
            DispatchSnapshot broadcastWithoutSourceHandleSnapshot = DispatchSnapshot.Empty;
            if (
                _scalarSinks[BusSinkIndex.BroadcastHandleWithoutContext]
                    .TryGetValue<TMessage>(out HandlerCache<int, HandlerCache> bwsHandlers)
                && bwsHandlers.handlers.Count > 0
            )
            {
                Touch(bwsHandlers, touchTick);
                broadcastWithoutSourceHandleSnapshot = AcquireDispatchSnapshot<TMessage>(
                    this,
                    bwsHandlers,
                    BroadcastWithoutContextHandleSlot,
                    _scopedEmissionId,
                    default
                );
            }

            bool foundAnyHandlers = false;
            _ = _contextSinks[BusContextIndex.BroadcastHandleDefault]
                .TryGetValue<TMessage>(
                    out Dictionary<InstanceId, HandlerCache<int, HandlerCache>> broadcastHandlers
                );
            if (
                broadcastHandlers != null
                && broadcastHandlers.TryGetValue(
                    source,
                    out HandlerCache<int, HandlerCache> sortedHandlers
                )
                && 0 < sortedHandlers.handlers.Count
            )
            {
                Touch(sortedHandlers, touchTick);
                // Legacy reporting: the live per-source gate above passing
                // counts as "found", regardless of how many frozen delegates
                // fire below.
                foundAnyHandlers = true;
                DispatchSnapshot snapshot = AcquireDispatchSnapshot<TMessage>(
                    this,
                    sortedHandlers,
                    BroadcastHandleSlot,
                    _scopedEmissionId,
                    source
                );
                // Flat dispatch; see DispatchFlatSnapshot for the
                // frozen-array semantics that replace the legacy
                // cross-priority prefreeze pass and the reentrant-rebuild
                // copy/live-count guards.
                _ = DispatchFlatSnapshot(snapshot, ref typedMessage);
            }

            bool bwsFound = InternalBroadcastWithoutSource(
                broadcastWithoutSourceHandleSnapshot,
                ref source,
                ref typedMessage
            );

            // Post-processors follow the FINAL (post-interceptor) source. The
            // pre-frozen snapshot above is keyed by the ORIGINAL source; when an
            // interceptor rewrote it, re-resolve the snapshot for the new source,
            // mirroring the targeted post-phase re-resolution. Like that path,
            // this exposes registrations made mid-emission for the REWRITTEN id
            // (its cache had no snapshot pinned at emission start); the
            // pre-frozen snapshot - and its mid-emission registration gating -
            // applies only when the source is unchanged.
            if (source != preInterceptorSource)
            {
                broadcastPostSnapshot = DispatchSnapshot.Empty;
                if (
                    _contextSinks[BusContextIndex.BroadcastPostProcessDefault]
                        .TryGetValue<TMessage>(out broadcastPostHandlers)
                    && broadcastPostHandlers.TryGetValue(source, out broadcastPostByPriority)
                    && broadcastPostByPriority.handlers.Count > 0
                )
                {
                    broadcastPostSnapshot = AcquireDispatchSnapshot<TMessage>(
                        this,
                        broadcastPostByPriority,
                        BroadcastPostSlot,
                        _scopedEmissionId,
                        source
                    );
                }
            }

            if (!broadcastPostSnapshot.IsEmpty)
            {
                // Legacy reporting: a non-empty pre-frozen snapshot counts as
                // "found" even if every frozen delegate is skipped below.
                foundAnyHandlers = true;
                _ = DispatchFlatSnapshot(broadcastPostSnapshot, ref typedMessage);
            }

            if (!broadcastWithoutSourcePostSnapshot.IsEmpty)
            {
                if (
                    DispatchContextFlatSnapshot(
                        broadcastWithoutSourcePostSnapshot,
                        ref source,
                        ref typedMessage
                    )
                )
                {
                    bwsFound = true;
                }
            }

            if (!(foundAnyHandlers || bwsFound) && MessagingDebug.enabled)
            {
                MessagingDebug.Log(
                    LogLevel.Info,
                    "Could not find a matching sourced broadcast handler for Id: {0}, Message: {1}.",
                    source,
                    typedMessage
                );
            }
        }

        private void BroadcastGlobalUntargeted(ref IUntargetedMessage message)
        {
            DispatchSnapshot snapshot = AcquireGlobalDispatchSnapshot<IUntargetedMessage>(
                this,
                _globalSlots,
                DispatchKind.Untargeted,
                _scopedEmissionId
            );
            DispatchBucket[] buckets = snapshot.buckets;
            int bucketCount = snapshot.bucketCount;
            if (bucketCount == 0)
            {
                return;
            }

            for (int bucketIndex = 0; bucketIndex < bucketCount; ++bucketIndex)
            {
                DispatchBucket bucket = buckets[bucketIndex];
                DispatchEntry[] entries = bucket.entries;
                int entryCount = bucket.entryCount;
                if (entryCount == 0)
                {
                    continue;
                }

                switch (entryCount)
                {
                    case 1:
                    {
                        InvokeGlobalUntargetedEntry(ref message, entries[0]);
                        continue;
                    }
                    case 2:
                    {
                        InvokeGlobalUntargetedEntry(ref message, entries[0]);
                        InvokeGlobalUntargetedEntry(ref message, entries[1]);
                        continue;
                    }
                    case 3:
                    {
                        InvokeGlobalUntargetedEntry(ref message, entries[0]);
                        InvokeGlobalUntargetedEntry(ref message, entries[1]);
                        InvokeGlobalUntargetedEntry(ref message, entries[2]);
                        continue;
                    }
                    case 4:
                    {
                        InvokeGlobalUntargetedEntry(ref message, entries[0]);
                        InvokeGlobalUntargetedEntry(ref message, entries[1]);
                        InvokeGlobalUntargetedEntry(ref message, entries[2]);
                        InvokeGlobalUntargetedEntry(ref message, entries[3]);
                        continue;
                    }
                    case 5:
                    {
                        InvokeGlobalUntargetedEntry(ref message, entries[0]);
                        InvokeGlobalUntargetedEntry(ref message, entries[1]);
                        InvokeGlobalUntargetedEntry(ref message, entries[2]);
                        InvokeGlobalUntargetedEntry(ref message, entries[3]);
                        InvokeGlobalUntargetedEntry(ref message, entries[4]);
                        continue;
                    }
                }

                for (int entryIndex = 0; entryIndex < entryCount; ++entryIndex)
                {
                    InvokeGlobalUntargetedEntry(ref message, entries[entryIndex]);
                }
            }
        }

        private void BroadcastGlobalTargeted(ref InstanceId target, ref ITargetedMessage message)
        {
            DispatchSnapshot snapshot = AcquireGlobalDispatchSnapshot<ITargetedMessage>(
                this,
                _globalSlots,
                DispatchKind.Targeted,
                _scopedEmissionId
            );
            DispatchBucket[] buckets = snapshot.buckets;
            int bucketCount = snapshot.bucketCount;
            if (bucketCount == 0)
            {
                return;
            }

            for (int bucketIndex = 0; bucketIndex < bucketCount; ++bucketIndex)
            {
                DispatchBucket bucket = buckets[bucketIndex];
                DispatchEntry[] entries = bucket.entries;
                int entryCount = bucket.entryCount;
                if (entryCount == 0)
                {
                    continue;
                }

                switch (entryCount)
                {
                    case 1:
                    {
                        InvokeGlobalTargetedEntry(ref target, ref message, entries[0]);
                        continue;
                    }
                    case 2:
                    {
                        InvokeGlobalTargetedEntry(ref target, ref message, entries[0]);
                        InvokeGlobalTargetedEntry(ref target, ref message, entries[1]);
                        continue;
                    }
                    case 3:
                    {
                        InvokeGlobalTargetedEntry(ref target, ref message, entries[0]);
                        InvokeGlobalTargetedEntry(ref target, ref message, entries[1]);
                        InvokeGlobalTargetedEntry(ref target, ref message, entries[2]);
                        continue;
                    }
                    case 4:
                    {
                        InvokeGlobalTargetedEntry(ref target, ref message, entries[0]);
                        InvokeGlobalTargetedEntry(ref target, ref message, entries[1]);
                        InvokeGlobalTargetedEntry(ref target, ref message, entries[2]);
                        InvokeGlobalTargetedEntry(ref target, ref message, entries[3]);
                        continue;
                    }
                    case 5:
                    {
                        InvokeGlobalTargetedEntry(ref target, ref message, entries[0]);
                        InvokeGlobalTargetedEntry(ref target, ref message, entries[1]);
                        InvokeGlobalTargetedEntry(ref target, ref message, entries[2]);
                        InvokeGlobalTargetedEntry(ref target, ref message, entries[3]);
                        InvokeGlobalTargetedEntry(ref target, ref message, entries[4]);
                        continue;
                    }
                }

                for (int entryIndex = 0; entryIndex < entryCount; ++entryIndex)
                {
                    InvokeGlobalTargetedEntry(ref target, ref message, entries[entryIndex]);
                }
            }
        }

        private void BroadcastGlobalSourcedBroadcast(
            ref InstanceId source,
            ref IBroadcastMessage message
        )
        {
            DispatchSnapshot snapshot = AcquireGlobalDispatchSnapshot<IBroadcastMessage>(
                this,
                _globalSlots,
                DispatchKind.Broadcast,
                _scopedEmissionId
            );
            DispatchBucket[] buckets = snapshot.buckets;
            int bucketCount = snapshot.bucketCount;
            if (bucketCount == 0)
            {
                return;
            }

            for (int bucketIndex = 0; bucketIndex < bucketCount; ++bucketIndex)
            {
                DispatchBucket bucket = buckets[bucketIndex];
                DispatchEntry[] entries = bucket.entries;
                int entryCount = bucket.entryCount;
                if (entryCount == 0)
                {
                    continue;
                }

                switch (entryCount)
                {
                    case 1:
                    {
                        InvokeGlobalBroadcastEntry(ref source, ref message, entries[0]);
                        continue;
                    }
                    case 2:
                    {
                        InvokeGlobalBroadcastEntry(ref source, ref message, entries[0]);
                        InvokeGlobalBroadcastEntry(ref source, ref message, entries[1]);
                        continue;
                    }
                    case 3:
                    {
                        InvokeGlobalBroadcastEntry(ref source, ref message, entries[0]);
                        InvokeGlobalBroadcastEntry(ref source, ref message, entries[1]);
                        InvokeGlobalBroadcastEntry(ref source, ref message, entries[2]);
                        continue;
                    }
                    case 4:
                    {
                        InvokeGlobalBroadcastEntry(ref source, ref message, entries[0]);
                        InvokeGlobalBroadcastEntry(ref source, ref message, entries[1]);
                        InvokeGlobalBroadcastEntry(ref source, ref message, entries[2]);
                        InvokeGlobalBroadcastEntry(ref source, ref message, entries[3]);
                        continue;
                    }
                    case 5:
                    {
                        InvokeGlobalBroadcastEntry(ref source, ref message, entries[0]);
                        InvokeGlobalBroadcastEntry(ref source, ref message, entries[1]);
                        InvokeGlobalBroadcastEntry(ref source, ref message, entries[2]);
                        InvokeGlobalBroadcastEntry(ref source, ref message, entries[3]);
                        InvokeGlobalBroadcastEntry(ref source, ref message, entries[4]);
                        continue;
                    }
                }

                for (int entryIndex = 0; entryIndex < entryCount; ++entryIndex)
                {
                    InvokeGlobalBroadcastEntry(ref source, ref message, entries[entryIndex]);
                }
            }
        }

        private bool TryGetUntargetedInterceptorCaches<TMessage>(
            out SortedList<int, List<object>> interceptorHandlers,
            out List<object> interceptorObjects
        )
            where TMessage : IUntargetedMessage
        {
            if (
                !_untargetedInterceptsByType.TryGetValue<TMessage>(
                    out InterceptorCache<object> interceptors
                )
                || interceptors.handlers.Count == 0
            )
            {
                interceptorHandlers = default;
                interceptorObjects = default;
                return false;
            }

            interceptorHandlers = interceptors.handlers;

            if (!_innerInterceptorsStack.TryPop(out interceptorObjects))
            {
                interceptorObjects = new List<object>();
            }

            return true;
        }

        private bool TryGetTargetedInterceptorCaches<TMessage>(
            out SortedList<int, List<object>> interceptorHandlers,
            out List<object> interceptorObjects
        )
            where TMessage : ITargetedMessage
        {
            if (
                !_targetedInterceptsByType.TryGetValue<TMessage>(
                    out InterceptorCache<object> interceptors
                )
                || interceptors.handlers.Count == 0
            )
            {
                interceptorHandlers = default;
                interceptorObjects = default;
                return false;
            }

            interceptorHandlers = interceptors.handlers;

            if (!_innerInterceptorsStack.TryPop(out interceptorObjects))
            {
                interceptorObjects = new List<object>();
            }

            return true;
        }

        private bool TryGetBroadcastInterceptorCaches<TMessage>(
            out SortedList<int, List<object>> interceptorHandlers,
            out List<object> interceptorObjects
        )
            where TMessage : IBroadcastMessage
        {
            if (
                !_broadcastInterceptsByType.TryGetValue<TMessage>(
                    out InterceptorCache<object> interceptors
                )
                || interceptors.handlers.Count == 0
            )
            {
                interceptorHandlers = default;
                interceptorObjects = default;
                return false;
            }

            interceptorHandlers = interceptors.handlers;

            if (!_innerInterceptorsStack.TryPop(out interceptorObjects))
            {
                interceptorObjects = new List<object>();
            }

            return true;
        }

        private bool RunUntargetedInterceptors<T>(ref T message)
            where T : IUntargetedMessage
        {
            if (
                !TryGetUntargetedInterceptorCaches<T>(
                    out SortedList<int, List<object>> interceptorHandlers,
                    out List<object> interceptorObjects
                )
            )
            {
                return true;
            }

            try
            {
                IList<List<object>> prioritizedInterceptors = interceptorHandlers.Values;
                for (int s = 0; s < prioritizedInterceptors.Count; ++s)
                {
                    interceptorObjects.Clear();
                    List<object> interceptors = prioritizedInterceptors[s];
                    interceptorObjects.AddRange(interceptors);

                    for (int i = 0; i < interceptorObjects.Count; ++i)
                    {
                        UntargetedInterceptor<T> typedTransformer = DxUnsafe.As<
                            UntargetedInterceptor<T>
                        >(interceptorObjects[i]);
                        if (!typedTransformer(ref message))
                        {
                            return false;
                        }
                    }
                }
            }
            finally
            {
                _innerInterceptorsStack.Push(interceptorObjects);
            }

            return true;
        }

        private bool RunTargetedInterceptors<T>(ref T message, ref InstanceId target)
            where T : ITargetedMessage
        {
            if (
                !TryGetTargetedInterceptorCaches<T>(
                    out SortedList<int, List<object>> interceptorHandlers,
                    out List<object> interceptorObjects
                )
            )
            {
                return true;
            }

            try
            {
                IList<List<object>> prioritizedInterceptors = interceptorHandlers.Values;
                for (int s = 0; s < prioritizedInterceptors.Count; ++s)
                {
                    interceptorObjects.Clear();
                    List<object> interceptors = prioritizedInterceptors[s];
                    interceptorObjects.AddRange(interceptors);

                    for (int i = 0; i < interceptorObjects.Count; ++i)
                    {
                        TargetedInterceptor<T> typedTransformer = DxUnsafe.As<
                            TargetedInterceptor<T>
                        >(interceptorObjects[i]);
                        if (!typedTransformer(ref target, ref message))
                        {
                            return false;
                        }
                    }
                }
            }
            finally
            {
                _innerInterceptorsStack.Push(interceptorObjects);
            }

            return true;
        }

        private bool RunBroadcastInterceptors<T>(ref T message, ref InstanceId source)
            where T : IBroadcastMessage
        {
            if (
                !TryGetBroadcastInterceptorCaches<T>(
                    out SortedList<int, List<object>> interceptorHandlers,
                    out List<object> interceptorObjects
                )
            )
            {
                return true;
            }

            try
            {
                IList<List<object>> prioritizedInterceptors = interceptorHandlers.Values;
                for (int s = 0; s < prioritizedInterceptors.Count; ++s)
                {
                    interceptorObjects.Clear();
                    List<object> interceptors = prioritizedInterceptors[s];
                    interceptorObjects.AddRange(interceptors);

                    for (int i = 0; i < interceptorObjects.Count; ++i)
                    {
                        BroadcastInterceptor<T> typedTransformer = DxUnsafe.As<
                            BroadcastInterceptor<T>
                        >(interceptorObjects[i]);
                        if (!typedTransformer(ref source, ref message))
                        {
                            return false;
                        }
                    }
                }
            }
            finally
            {
                _innerInterceptorsStack.Push(interceptorObjects);
            }

            return true;
        }

        private bool InternalUntargetedBroadcast<TMessage>(ref TMessage message)
            where TMessage : IUntargetedMessage
        {
            if (
                !_scalarSinks[BusSinkIndex.UntargetedHandleDefault]
                    .TryGetValue<TMessage>(out HandlerCache<int, HandlerCache> sortedHandlers)
                || sortedHandlers.handlers.Count == 0
            )
            {
                return false;
            }

            DispatchSnapshot snapshot = AcquireDispatchSnapshot<TMessage>(
                this,
                sortedHandlers,
                UntargetedHandleSlot,
                _scopedEmissionId,
                default
            );

            // Flat dispatch; see DispatchFlatSnapshot for the frozen-array
            // semantics, the reset-generation guard, and the
            // "found any handlers" reporting (delegates fired OR bus-level
            // bucket entries exist, matching the legacy link path's
            // bucket-count behavior).
            return DispatchFlatSnapshot(snapshot, ref message);
        }

        private bool InternalTargetedWithoutTargetingBroadcast<TMessage>(
            ref InstanceId target,
            ref TMessage message
        )
            where TMessage : ITargetedMessage
        {
            if (
                !_scalarSinks[BusSinkIndex.TargetedHandleWithoutContext]
                    .TryGetValue<TMessage>(out HandlerCache<int, HandlerCache> sortedHandlers)
                || sortedHandlers.handlers.Count == 0
            )
            {
                return false;
            }

            DispatchSnapshot snapshot = AcquireDispatchSnapshot<TMessage>(
                this,
                sortedHandlers,
                TargetedWithoutContextHandleSlot,
                _scopedEmissionId,
                default
            );

            // Flat dispatch; see DispatchContextFlatSnapshot for the
            // frozen-array semantics that replace the legacy prefreeze hoist
            // (the resolved array cannot observe a mid-emission
            // deregistration, so no per-bucket prefreeze pass is needed).
            return DispatchContextFlatSnapshot(snapshot, ref target, ref message);
        }

        private bool InternalBroadcastWithoutSource<TMessage>(
            DispatchSnapshot prefrozenSnapshot,
            ref InstanceId source,
            ref TMessage message
        )
            where TMessage : IBroadcastMessage
        {
            if (
                !_scalarSinks[BusSinkIndex.BroadcastHandleWithoutContext]
                    .TryGetValue<TMessage>(out HandlerCache<int, HandlerCache> sortedHandlers)
                || sortedHandlers.handlers.Count == 0
            )
            {
                return false;
            }

            // The handle-phase snapshot is acquired at emission start (where
            // the legacy per-handler prefreeze pass ran, after interceptors
            // and the global walk but before the source-keyed handle phase),
            // so registrations made by a source-keyed handler this emission
            // are not observed - matching the legacy emission-start freeze.
            // When the sink was EMPTY at emission start (prefrozenSnapshot is
            // Empty), acquire lazily here: the legacy path took its first
            // freeze at this point in that case, so a registration made
            // earlier in this emission into a previously-empty sink fires.
            DispatchSnapshot snapshot = prefrozenSnapshot.IsEmpty
                ? AcquireDispatchSnapshot<TMessage>(
                    this,
                    sortedHandlers,
                    BroadcastWithoutContextHandleSlot,
                    _scopedEmissionId,
                    default
                )
                : prefrozenSnapshot;
            _ = DispatchContextFlatSnapshot(snapshot, ref source, ref message);
            // Legacy reporting: the live-sink gate above passing counts as
            // "found", regardless of how many frozen delegates fired.
            return true;
        }

        private Action InternalRegisterUntargeted<T>(
            MessageHandler messageHandler,
            MessageCache<HandlerCache<int, HandlerCache>> sinks,
            RegistrationMethod registrationMethod,
            int priority
        )
            where T : IMessage
        {
            if (messageHandler == null)
            {
                throw new ArgumentNullException(nameof(messageHandler));
            }

            long touchTick = AdvanceTick();
            InstanceId handlerOwnerId = messageHandler.owner;
            HandlerCache<int, HandlerCache> handlers = sinks.GetOrAdd<T>();
            Touch(handlers, touchTick);
            HandlerCache<int, HandlerCache> capturedHandlers = handlers;
            SlotKey slotKey = RegistrationMethodAxes.GetSlotKey(registrationMethod);

            if (!handlers.handlers.TryGetValue(priority, out HandlerCache cache))
            {
                handlers.version++;
                cache = new HandlerCache();
                handlers.handlers[priority] = cache;
                // insert priority in sorted order
                List<int> order = handlers.order;
                int idx = 0;
                while (idx < order.Count && order[idx] < priority)
                {
                    idx++;
                }
                order.Insert(idx, priority);
            }

            Dictionary<MessageHandler, int> handler = cache.handlers;
            cache.version++;
            int count = handler.GetValueOrDefault(messageHandler, 0);

            handler[messageHandler] = count + 1;
            if (count == 0)
            {
                // First registration of this MessageHandler in the bucket:
                // record its position. Refcount increments (count > 0) must
                // NOT move it.
                cache.insertionOrder.Add(messageHandler);
            }
            StageDispatchSnapshot<T>(this, capturedHandlers, slotKey);
            Type type = typeof(T);
            _log.Log(
                new MessagingRegistration(
                    handlerOwnerId,
                    type,
                    RegistrationType.Register,
                    registrationMethod
                )
            );

            long capturedGeneration = _resetGeneration;
            return () =>
            {
                // Generation guard: if ResetState() ran after this closure was
                // captured (e.g. a deferred Object.Destroy fires after a
                // domain-reload-style reset), silently no-op rather than
                // logging a misleading over-deregistration error.
                if (capturedGeneration != _resetGeneration)
                {
                    return;
                }

                long deregisterTouchTick = AdvanceTick();
                cache.version++;
                if (
                    !sinks.TryGetValue<T>(out handlers)
                    || !ReferenceEquals(handlers, capturedHandlers)
                    || !handlers.handlers.TryGetValue(priority, out cache)
                    || !cache.handlers.TryGetValue(messageHandler, out count)
                )
                {
                    if (
                        capturedHandlers.handlers.Count == 0
                        && !ReferenceEquals(handlers, capturedHandlers)
                    )
                    {
                        return;
                    }

                    if (MessagingDebug.enabled)
                    {
                        MessagingDebug.Log(
                            LogLevel.Error,
                            "Received over-deregistration of {0} for {1}. Check to make sure you're not calling (de)registration multiple times.",
                            type,
                            messageHandler
                        );
                    }

                    return;
                }

                _log.Log(
                    new MessagingRegistration(
                        handlerOwnerId,
                        type,
                        RegistrationType.Deregister,
                        registrationMethod
                    )
                );
                Touch(handlers, deregisterTouchTick);
                handlers.version++;
                handler = cache.handlers;
                if (count <= 1)
                {
                    bool complete = handler.Remove(messageHandler);
                    // List.Remove is O(n) over the same-priority bucket.
                    // Accepted tradeoff (here and at the context-path sibling
                    // site): buckets are small in practice, removal is a cold
                    // churn path, and the list keeps dispatch-order rebuilds
                    // allocation-free while preserving first-registration
                    // order, unlike Dictionary enumeration whose freed slots
                    // are reused LIFO. Mirrors the MessageHandler-side
                    // insertionOrder tradeoff.
                    _ = cache.insertionOrder.Remove(messageHandler);
                    MarkDirtyHandler(messageHandler);
                    cache.version++;
                    // do not mutate cache.cache here; let next read rebuild from handlers

                    if (handler.Count == 0)
                    {
                        _ = handlers.handlers.Remove(priority);
                        // remove priority from order
                        List<int> order = handlers.order;
                        int removeIdx = order.IndexOf(priority);
                        if (removeIdx >= 0)
                        {
                            order.RemoveAt(removeIdx);
                        }
                    }

                    if (handlers.handlers.Count == 0)
                    {
                        MarkDirtyType<T>();
                    }

                    if (!complete && MessagingDebug.enabled)
                    {
                        MessagingDebug.Log(
                            LogLevel.Error,
                            "Received over-deregistration of {0} for {1}. Check to make sure you're not calling (de)registration multiple times.",
                            type,
                            messageHandler
                        );
                    }
                }
                else
                {
                    handler[messageHandler] = count - 1;
                }
                StageDispatchSnapshot<T>(this, handlers, slotKey);
            };
        }

        private Action InternalRegisterWithContext<T>(
            InstanceId context,
            MessageHandler messageHandler,
            MessageCache<Dictionary<InstanceId, HandlerCache<int, HandlerCache>>> sinks,
            RegistrationMethod registrationMethod,
            int priority
        )
            where T : IMessage
        {
            if (messageHandler == null)
            {
                throw new ArgumentNullException(nameof(messageHandler));
            }

            long touchTick = AdvanceTick();
            Dictionary<InstanceId, HandlerCache<int, HandlerCache>> broadcastHandlers =
                GetOrRentContextMap<T>(sinks);
            Dictionary<InstanceId, HandlerCache<int, HandlerCache>> capturedBroadcastHandlers =
                broadcastHandlers;
            SlotKey slotKey = RegistrationMethodAxes.GetSlotKey(registrationMethod);

            if (
                !broadcastHandlers.TryGetValue(
                    context,
                    out HandlerCache<int, HandlerCache> handlers
                )
            )
            {
                handlers = new HandlerCache<int, HandlerCache>();
                broadcastHandlers[context] = handlers;
                TrackContextMapHighWater(broadcastHandlers);
            }
            Touch(handlers, touchTick);
            HandlerCache<int, HandlerCache> capturedHandlers = handlers;

            if (!handlers.handlers.TryGetValue(priority, out HandlerCache cache))
            {
                handlers.version++;
                cache = new HandlerCache();
                handlers.handlers[priority] = cache;
                // insert priority in sorted order
                List<int> order = handlers.order;
                int idx = 0;
                while (idx < order.Count && order[idx] < priority)
                {
                    idx++;
                }
                order.Insert(idx, priority);
            }

            cache.version++;
            Dictionary<MessageHandler, int> handler = cache.handlers;
            int count = handler.GetValueOrDefault(messageHandler, 0);

            handler[messageHandler] = count + 1;
            if (count == 0)
            {
                // First registration of this MessageHandler in the bucket:
                // record its position. Refcount increments (count > 0) must
                // NOT move it.
                cache.insertionOrder.Add(messageHandler);
            }

            Type type = typeof(T);
            _log.Log(
                new MessagingRegistration(
                    context,
                    type,
                    RegistrationType.Register,
                    registrationMethod
                )
            );
            StageDispatchSnapshot<T>(this, handlers, slotKey);

            long capturedGeneration = _resetGeneration;
            return () =>
            {
                // Generation guard: see InternalRegisterUntargeted for the
                // rationale. Skip silently when the closure outlived a Reset.
                if (capturedGeneration != _resetGeneration)
                {
                    return;
                }

                long deregisterTouchTick = AdvanceTick();
                cache.version++;
                if (
                    !sinks.TryGetValue<T>(out broadcastHandlers)
                    || !ReferenceEquals(broadcastHandlers, capturedBroadcastHandlers)
                    || !broadcastHandlers.TryGetValue(context, out handlers)
                    || !ReferenceEquals(handlers, capturedHandlers)
                    || !handlers.handlers.TryGetValue(priority, out cache)
                    || !cache.handlers.TryGetValue(messageHandler, out count)
                )
                {
                    if (IsStaleContextDeregisterAfterSweep<T>(sinks, context, capturedHandlers))
                    {
                        return;
                    }

                    if (MessagingDebug.enabled)
                    {
                        MessagingDebug.Log(
                            LogLevel.Error,
                            "Received over-deregistration of {0} for {1}. Check to make sure you're not calling (de)registration multiple times.",
                            type,
                            messageHandler
                        );
                    }

                    return;
                }

                _log.Log(
                    new MessagingRegistration(
                        context,
                        type,
                        RegistrationType.Deregister,
                        registrationMethod
                    )
                );
                Touch(handlers, deregisterTouchTick);
                handler = cache.handlers;
                if (count <= 1)
                {
                    bool complete = handler.Remove(messageHandler);
                    // O(n) List.Remove: see the tradeoff comment at the
                    // scalar-path sibling site in InternalRegisterUntargeted.
                    _ = cache.insertionOrder.Remove(messageHandler);
                    MarkDirtyHandler(messageHandler);
                    cache.version++;
                    // do not mutate cache.cache here; let next read rebuild from handlers
                    if (handler.Count == 0)
                    {
                        handlers.version++;
                        _ = handlers.handlers.Remove(priority);
                        // remove priority from order
                        List<int> order = handlers.order;
                        int removeIdx = order.IndexOf(priority);
                        if (removeIdx >= 0)
                        {
                            order.RemoveAt(removeIdx);
                        }
                    }

                    if (handlers.handlers.Count == 0)
                    {
                        MarkDirtyTarget<T>(context);
                    }

                    if (!complete && MessagingDebug.enabled)
                    {
                        MessagingDebug.Log(
                            LogLevel.Error,
                            "Received over-deregistration of {0} for {1}. Check to make sure you're not calling (de)registration multiple times.",
                            type,
                            messageHandler
                        );
                    }
                }
                else
                {
                    handler[messageHandler] = count - 1;
                }
                StageDispatchSnapshot<T>(this, handlers, slotKey);
            };
        }

        private static bool IsStaleContextDeregisterAfterSweep<T>(
            MessageCache<Dictionary<InstanceId, HandlerCache<int, HandlerCache>>> sinks,
            InstanceId context,
            HandlerCache<int, HandlerCache> capturedHandlers
        )
            where T : IMessage
        {
            return capturedHandlers.handlers.Count == 0
                && (
                    !sinks.TryGetValue<T>(
                        out Dictionary<InstanceId, HandlerCache<int, HandlerCache>> currentByContext
                    )
                    || !currentByContext.TryGetValue(
                        context,
                        out HandlerCache<int, HandlerCache> currentHandlers
                    )
                    || !ReferenceEquals(currentHandlers, capturedHandlers)
                );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void StageDispatchSnapshot<TMessage>(
            MessageBus messageBus,
            HandlerCache<int, HandlerCache> handlers,
            SlotKey slotKey
        )
            where TMessage : IMessage
        {
            if (handlers == null || slotKey == SlotKey.None)
            {
                return;
            }

            DispatchState state = handlers.dispatchState ??= new DispatchState();
            if (state.hasPending)
            {
                ReleaseSnapshot(ref state.pending);
            }
            state.hasPending = true;
            state.pendingDirty = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void StageGlobalDispatchSnapshot<TMessage>(
            MessageBus messageBus,
            BusGlobalSlot handlers,
            DispatchKind kind
        )
            where TMessage : IMessage
        {
            // DispatchKind has no None sentinel; the bus only reaches this path
            // through register sites that pass a valid kind, so the legacy
            // category-None short-circuit is no longer needed -- the
            // `handlers == null` guard alone suffices.
            if (handlers == null)
            {
                return;
            }

            ref DispatchState slotState = ref SelectGlobalDispatchState(handlers, kind);
            slotState ??= new DispatchState();
            DispatchState state = slotState;
            if (state.hasPending)
            {
                ReleaseSnapshot(ref state.pending);
            }

            state.hasPending = true;
            state.pendingDirty = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ref DispatchState SelectGlobalDispatchState(
            BusGlobalSlot slot,
            DispatchKind kind
        )
        {
            switch (kind)
            {
                case DispatchKind.Untargeted:
                    return ref slot.untargetedDispatchState;
                case DispatchKind.Targeted:
                    return ref slot.targetedDispatchState;
                case DispatchKind.Broadcast:
                    return ref slot.broadcastDispatchState;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(kind),
                        kind,
                        "SelectGlobalDispatchState only supports Untargeted, Targeted, Broadcast."
                    );
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReleaseSnapshot(ref DispatchSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            snapshot.Release();
            snapshot = DispatchSnapshot.Empty;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static DispatchSnapshot AcquireDispatchSnapshot<TMessage>(
            MessageBus messageBus,
            HandlerCache<int, HandlerCache> handlers,
            SlotKey slotKey,
            long emissionId,
            InstanceId context
        )
            where TMessage : IMessage
        {
            if (handlers == null)
            {
                return DispatchSnapshot.Empty;
            }

            if (slotKey == SlotKey.None)
            {
                return DispatchSnapshot.Empty;
            }

            Touch(handlers, messageBus._tickCounter);
            DispatchState state = handlers.dispatchState ??= new DispatchState();

            bool hasHandlers = handlers.handlers.Count > 0;

            if (state.hasPending)
            {
                if (state.pendingDirty || (hasHandlers && state.pending.IsEmpty))
                {
                    ReleaseSnapshot(ref state.pending);
                    state.pending = hasHandlers
                        ? BuildDispatchSnapshot<TMessage>(messageBus, handlers, slotKey, context)
                        : DispatchSnapshot.Empty;

                    state.pendingDirty = false;
                }
            }
            else if (state.active.IsEmpty && hasHandlers)
            {
                ReleaseSnapshot(ref state.pending);
                state.pending = BuildDispatchSnapshot<TMessage>(
                    messageBus,
                    handlers,
                    slotKey,
                    context
                );
                state.hasPending = true;
                state.pendingDirty = false;
            }

            if (state.snapshotEmissionId != emissionId)
            {
                if (state.hasPending)
                {
                    // Displacement, not plain release: an OUTER emission may
                    // still be iterating state.active (handler mutated the
                    // registration set, then reentrantly emitted this type).
                    // ReleaseDisplacedSnapshot defers the release to the
                    // outermost dispatch-lease exit when one is in flight.
                    messageBus.ReleaseDisplacedSnapshot(ref state.active);
                    if (state.pendingDirty || (hasHandlers && state.pending.IsEmpty))
                    {
                        ReleaseSnapshot(ref state.pending);
                        state.pending = hasHandlers
                            ? BuildDispatchSnapshot<TMessage>(
                                messageBus,
                                handlers,
                                slotKey,
                                context
                            )
                            : DispatchSnapshot.Empty;

                        state.pendingDirty = false;
                    }

                    state.active = state.pending ?? DispatchSnapshot.Empty;
                    state.pending = DispatchSnapshot.Empty;
                    state.hasPending = false;
                    state.pendingDirty = false;
                }
                else if (!hasHandlers && !state.active.IsEmpty)
                {
                    messageBus.ReleaseDisplacedSnapshot(ref state.active);
                }

                state.snapshotEmissionId = emissionId;
            }

            return state.active;
        }

        private static DispatchSnapshot BuildDispatchSnapshot<TMessage>(
            MessageBus messageBus,
            HandlerCache<int, HandlerCache> handlers,
            SlotKey slotKey,
            InstanceId context
        )
            where TMessage : IMessage
        {
            if (handlers == null || handlers.order.Count == 0)
            {
                return DispatchSnapshot.Empty;
            }

            List<int> orderedPriorities = handlers.order;
            int priorityCount = orderedPriorities.Count;
            DispatchBucket[] buckets = DispatchBucketPool.Rent(priorityCount);

            for (int i = 0; i < priorityCount; ++i)
            {
                int priority = orderedPriorities[i];
                if (
                    !handlers.handlers.TryGetValue(priority, out HandlerCache cache)
                    || cache == null
                )
                {
                    buckets[i] = DispatchBucket.CreateEmpty(priority);
                    continue;
                }

                Dictionary<MessageHandler, int> handlerLookup = cache.handlers;
                if (handlerLookup == null || handlerLookup.Count == 0)
                {
                    buckets[i] = DispatchBucket.CreateEmpty(priority);
                    continue;
                }

                int entryCount = handlerLookup.Count;
                DispatchEntry[] entries = DispatchEntryPool.Rent(entryCount);
                FillDispatchEntries<TMessage>(messageBus, cache, slotKey, priority, entries);
                buckets[i] = new DispatchBucket(priority, entries, entryCount, pooledEntries: true);
            }

            DispatchSnapshot snapshot = new DispatchSnapshot(buckets, priorityCount, pooled: true);
            snapshot.flat = BuildFlatDispatch<TMessage>(messageBus, handlers, slotKey, context);
            return snapshot;
        }

        /// <summary>
        /// Selects the typed-handler slot pair for the snapshot's
        /// (kind, phase, variant) coordinate and builds the resolved flat
        /// dispatch array. Every bus-side slot that reaches
        /// <see cref="BuildDispatchSnapshot{TMessage}"/> is flattened:
        /// untargeted handle/post, targeted/broadcast Default handle/post
        /// (context-keyed; <paramref name="context"/> selects the per-target
        /// or per-source typed caches), and targeted/broadcast WithoutContext
        /// handle/post (whose delegates receive the routing InstanceId, so
        /// they resolve into <see cref="ContextFlatDispatch{TMessage}"/>
        /// entries instead).
        /// </summary>
        private static FlatDispatchArray BuildFlatDispatch<TMessage>(
            MessageBus messageBus,
            HandlerCache<int, HandlerCache> handlers,
            SlotKey slotKey,
            InstanceId context
        )
            where TMessage : IMessage
        {
            bool postProcessing = slotKey.Phase == DispatchPhase.PostProcess;
            DispatchKind kind = slotKey.Kind;
            int fastIndex;
            int defaultIndex;
            if (slotKey.Variant == DispatchVariant.WithoutContext)
            {
                if (kind == DispatchKind.Targeted)
                {
                    fastIndex = postProcessing
                        ? TypedSlotIndex.TargetedPostProcessWithoutContextFast
                        : TypedSlotIndex.TargetedHandleWithoutContextFast;
                    defaultIndex = postProcessing
                        ? TypedSlotIndex.TargetedPostProcessWithoutContext
                        : TypedSlotIndex.TargetedHandleWithoutContext;
                }
                else
                {
                    fastIndex = postProcessing
                        ? TypedSlotIndex.BroadcastPostProcessWithoutContextFast
                        : TypedSlotIndex.BroadcastHandleWithoutContextFast;
                    defaultIndex = postProcessing
                        ? TypedSlotIndex.BroadcastPostProcessWithoutContext
                        : TypedSlotIndex.BroadcastHandleWithoutContext;
                }

                return BuildWithContextFlatDispatch<TMessage>(
                    messageBus,
                    handlers,
                    fastIndex,
                    defaultIndex
                );
            }

            if (kind == DispatchKind.Untargeted)
            {
                fastIndex = postProcessing
                    ? TypedSlotIndex.UntargetedPostProcessFast
                    : TypedSlotIndex.UntargetedHandleFast;
                defaultIndex = postProcessing
                    ? TypedSlotIndex.UntargetedPostProcessDefault
                    : TypedSlotIndex.UntargetedHandleDefault;
                return BuildMessageFlatDispatch<TMessage>(
                    messageBus,
                    handlers,
                    fastIndex,
                    defaultIndex
                );
            }

            if (kind == DispatchKind.Targeted)
            {
                fastIndex = postProcessing
                    ? TypedSlotIndex.TargetedPostProcessFast
                    : TypedSlotIndex.TargetedHandleFast;
                defaultIndex = postProcessing
                    ? TypedSlotIndex.TargetedPostProcessDefault
                    : TypedSlotIndex.TargetedHandleDefault;
            }
            else
            {
                fastIndex = postProcessing
                    ? TypedSlotIndex.BroadcastPostProcessFast
                    : TypedSlotIndex.BroadcastHandleFast;
                defaultIndex = postProcessing
                    ? TypedSlotIndex.BroadcastPostProcessDefault
                    : TypedSlotIndex.BroadcastHandleDefault;
            }

            return BuildContextFlatDispatch<TMessage>(
                messageBus,
                handlers,
                context,
                fastIndex,
                defaultIndex
            );
        }

        /// <summary>
        /// Resolves every registration reachable from the bus-side priority
        /// buckets of a non-context-keyed FastHandler-shaped slot into a
        /// single pooled flat array of {MessageHandler, FastHandler} pairs,
        /// in exact dispatch order: priority ascending, then bus-bucket
        /// insertion order per priority, then per MessageHandler all fast
        /// entries (registration order) followed by all default entries
        /// (registration order). Runs only at snapshot-build time
        /// (registration churn); steady-state emission walks the result with
        /// one live <c>active</c> check per entry and a direct delegate
        /// invocation - no dispatch links, generation guards, per-priority
        /// dictionary lookups, or delegate-shape type tests. Creates zero
        /// closures: default Action handlers were adapted to FastHandler form
        /// once at registration time (HandlerActionCache.Entry.flatInvoker).
        /// </summary>
        private static FlatDispatchArray BuildMessageFlatDispatch<TMessage>(
            MessageBus messageBus,
            HandlerCache<int, HandlerCache> handlers,
            int fastIndex,
            int defaultIndex
        )
            where TMessage : IMessage
        {
            List<int> orderedPriorities = handlers.order;
            int priorityCount = orderedPriorities.Count;
            int total = 0;
            for (int i = 0; i < priorityCount; ++i)
            {
                int priority = orderedPriorities[i];
                if (
                    !handlers.handlers.TryGetValue(priority, out HandlerCache cache)
                    || cache == null
                )
                {
                    continue;
                }

                DebugAssertBusInsertionOrderInSync(cache);
                List<MessageHandler> ordered = cache.insertionOrder;
                int orderedCount = ordered.Count;
                for (int j = 0; j < orderedCount; ++j)
                {
                    total += ordered[j]
                        .CountFlatHandlers<TMessage>(messageBus, priority, fastIndex, defaultIndex);
                }
            }

            FlatDispatch<TMessage> flat = FlatDispatch<TMessage>.Rent(total);
            if (total == 0)
            {
                return flat;
            }

            FlatDispatchEntry<TMessage>[] flatEntries = flat.entries;
            int write = 0;
            for (int i = 0; i < priorityCount; ++i)
            {
                int priority = orderedPriorities[i];
                if (
                    !handlers.handlers.TryGetValue(priority, out HandlerCache cache)
                    || cache == null
                )
                {
                    continue;
                }

                List<MessageHandler> ordered = cache.insertionOrder;
                int orderedCount = ordered.Count;
                for (int j = 0; j < orderedCount; ++j)
                {
                    write = ordered[j]
                        .FillFlatHandlers<TMessage>(
                            messageBus,
                            priority,
                            fastIndex,
                            defaultIndex,
                            flatEntries,
                            write
                        );
                }
            }

            flat.count = write;
            return flat;
        }

        /// <summary>
        /// Context-keyed sibling of
        /// <see cref="BuildMessageFlatDispatch{TMessage}"/> for the
        /// Default-variant targeted/broadcast slots: the bus-side priority
        /// buckets are already per-context (the HandlerCache is keyed by
        /// target/source), and each MessageHandler's typed caches are
        /// resolved through its per-context map for
        /// <paramref name="context"/>. Same ordering and zero-closure
        /// guarantees.
        /// </summary>
        private static FlatDispatchArray BuildContextFlatDispatch<TMessage>(
            MessageBus messageBus,
            HandlerCache<int, HandlerCache> handlers,
            InstanceId context,
            int fastIndex,
            int defaultIndex
        )
            where TMessage : IMessage
        {
            List<int> orderedPriorities = handlers.order;
            int priorityCount = orderedPriorities.Count;
            int total = 0;
            for (int i = 0; i < priorityCount; ++i)
            {
                int priority = orderedPriorities[i];
                if (
                    !handlers.handlers.TryGetValue(priority, out HandlerCache cache)
                    || cache == null
                )
                {
                    continue;
                }

                DebugAssertBusInsertionOrderInSync(cache);
                List<MessageHandler> ordered = cache.insertionOrder;
                int orderedCount = ordered.Count;
                for (int j = 0; j < orderedCount; ++j)
                {
                    total += ordered[j]
                        .CountContextFlatHandlers<TMessage>(
                            messageBus,
                            context,
                            priority,
                            fastIndex,
                            defaultIndex
                        );
                }
            }

            FlatDispatch<TMessage> flat = FlatDispatch<TMessage>.Rent(total);
            if (total == 0)
            {
                return flat;
            }

            FlatDispatchEntry<TMessage>[] flatEntries = flat.entries;
            int write = 0;
            for (int i = 0; i < priorityCount; ++i)
            {
                int priority = orderedPriorities[i];
                if (
                    !handlers.handlers.TryGetValue(priority, out HandlerCache cache)
                    || cache == null
                )
                {
                    continue;
                }

                List<MessageHandler> ordered = cache.insertionOrder;
                int orderedCount = ordered.Count;
                for (int j = 0; j < orderedCount; ++j)
                {
                    write = ordered[j]
                        .FillContextFlatHandlers<TMessage>(
                            messageBus,
                            context,
                            priority,
                            fastIndex,
                            defaultIndex,
                            flatEntries,
                            write
                        );
                }
            }

            flat.count = write;
            return flat;
        }

        /// <summary>
        /// WithoutContext sibling of
        /// <see cref="BuildMessageFlatDispatch{TMessage}"/> for the
        /// targeted/broadcast slots whose delegates receive the routing
        /// InstanceId: entries resolve to FastHandlerWithContext delegates
        /// (fast registrations directly; default Action&lt;InstanceId, T&gt;
        /// registrations via the registration-time adapter). Same ordering
        /// and zero-closure guarantees.
        /// </summary>
        private static FlatDispatchArray BuildWithContextFlatDispatch<TMessage>(
            MessageBus messageBus,
            HandlerCache<int, HandlerCache> handlers,
            int fastIndex,
            int defaultIndex
        )
            where TMessage : IMessage
        {
            List<int> orderedPriorities = handlers.order;
            int priorityCount = orderedPriorities.Count;
            int total = 0;
            for (int i = 0; i < priorityCount; ++i)
            {
                int priority = orderedPriorities[i];
                if (
                    !handlers.handlers.TryGetValue(priority, out HandlerCache cache)
                    || cache == null
                )
                {
                    continue;
                }

                DebugAssertBusInsertionOrderInSync(cache);
                List<MessageHandler> ordered = cache.insertionOrder;
                int orderedCount = ordered.Count;
                for (int j = 0; j < orderedCount; ++j)
                {
                    total += ordered[j]
                        .CountWithContextFlatHandlers<TMessage>(
                            messageBus,
                            priority,
                            fastIndex,
                            defaultIndex
                        );
                }
            }

            ContextFlatDispatch<TMessage> flat = ContextFlatDispatch<TMessage>.Rent(total);
            if (total == 0)
            {
                return flat;
            }

            ContextFlatDispatchEntry<TMessage>[] flatEntries = flat.entries;
            int write = 0;
            for (int i = 0; i < priorityCount; ++i)
            {
                int priority = orderedPriorities[i];
                if (
                    !handlers.handlers.TryGetValue(priority, out HandlerCache cache)
                    || cache == null
                )
                {
                    continue;
                }

                List<MessageHandler> ordered = cache.insertionOrder;
                int orderedCount = ordered.Count;
                for (int j = 0; j < orderedCount; ++j)
                {
                    write = ordered[j]
                        .FillWithContextFlatHandlers<TMessage>(
                            messageBus,
                            priority,
                            fastIndex,
                            defaultIndex,
                            flatEntries,
                            write
                        );
                }
            }

            flat.count = write;
            return flat;
        }

        /// <summary>
        /// Walks a flat snapshot whose entries carry message-only
        /// (FastHandler) delegates: one live <c>active</c> check per entry,
        /// then a direct delegate invocation. The array is frozen for this
        /// emission (mutations mark the DispatchState dirty and surface on
        /// the NEXT emission's rebuild), which preserves the mid-emission
        /// contracts the legacy link path enforced via prefreeze stamping:
        /// a handler deregistered mid-emission still fires (its entry is
        /// already in the frozen array); a handler registered mid-emission
        /// does not fire (it is not in the frozen array); a
        /// destroyed/disabled component never fires (live per-entry
        /// <c>active</c> check, at per-delegate granularity). The
        /// reset-generation re-read after each invocation halts in-flight
        /// dispatch when a handler calls ResetState() (the documented
        /// mid-dispatch reset contract).
        /// Returns the legacy "found any handlers" semantics: delegates were
        /// resolved, OR bus-level bucket entries exist for the slot.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool DispatchFlatSnapshot<TMessage>(DispatchSnapshot snapshot, ref TMessage message)
            where TMessage : IMessage
        {
            FlatDispatchArray flatBase = snapshot.flat;
            if (flatBase == null)
            {
                return false;
            }

            DebugAssertFlatShape<FlatDispatch<TMessage>>(flatBase);
            FlatDispatch<TMessage> flat = DxUnsafe.As<FlatDispatch<TMessage>>(flatBase);
            FlatDispatchEntry<TMessage>[] entries = flat.entries;
            int count = flat.count;
            long resetGeneration = _resetGeneration;
            for (int i = 0; i < count; ++i)
            {
                ref FlatDispatchEntry<TMessage> entry = ref entries[i];
                if (entry.handler.active)
                {
                    entry.invoker(ref message);
                    if (_resetGeneration != resetGeneration)
                    {
                        break;
                    }
                }
            }

            if (0 < count)
            {
                return true;
            }

            return HasAnyDispatchEntries(snapshot);
        }

        /// <summary>
        /// Sibling of <see cref="DispatchFlatSnapshot{TMessage}"/> for the
        /// WithoutContext targeted/broadcast slots: entries carry
        /// FastHandlerWithContext delegates that receive the routing
        /// InstanceId alongside the message. Same frozen-array and
        /// reset-generation semantics.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool DispatchContextFlatSnapshot<TMessage>(
            DispatchSnapshot snapshot,
            ref InstanceId context,
            ref TMessage message
        )
            where TMessage : IMessage
        {
            FlatDispatchArray flatBase = snapshot.flat;
            if (flatBase == null)
            {
                return false;
            }

            DebugAssertFlatShape<ContextFlatDispatch<TMessage>>(flatBase);
            ContextFlatDispatch<TMessage> flat = DxUnsafe.As<ContextFlatDispatch<TMessage>>(
                flatBase
            );
            ContextFlatDispatchEntry<TMessage>[] entries = flat.entries;
            int count = flat.count;
            long resetGeneration = _resetGeneration;
            for (int i = 0; i < count; ++i)
            {
                ref ContextFlatDispatchEntry<TMessage> entry = ref entries[i];
                if (entry.handler.active)
                {
                    entry.invoker(ref context, ref message);
                    if (_resetGeneration != resetGeneration)
                    {
                        break;
                    }
                }
            }

            if (0 < count)
            {
                return true;
            }

            return HasAnyDispatchEntries(snapshot);
        }

        // Asserts the snapshot's flat array is the concrete closed-generic
        // holder the dispatch site is about to DxUnsafe.As-cast it to. A
        // mismatch means a slot-key/build-shape wiring bug (e.g. a
        // WithoutContext slot built a message-shape array); the unchecked
        // cast would otherwise corrupt the dispatch. Stripped in Release.
        [Conditional("DEBUG")]
        private static void DebugAssertFlatShape<TExpected>(FlatDispatchArray flat)
            where TExpected : FlatDispatchArray
        {
            // Early return on the expected (always-taken) path so the failure
            // message is only materialized on an actual mismatch; building it
            // eagerly would allocate strings on every dispatch in DEBUG
            // (editor) runs and trip the zero-alloc steady-state gates.
            if (flat is TExpected)
            {
                return;
            }

            System.Diagnostics.Debug.Assert(
                false,
                "Flat dispatch shape mismatch: snapshot.flat is "
                    + flat.GetType().Name
                    + " but the dispatch site expects "
                    + typeof(TExpected).Name
                    + ". Check BuildFlatDispatch's slot-key routing."
            );
        }

        // Asserts the bus-side per-priority insertionOrder list stays in
        // lockstep with the refcount dictionary at every snapshot build.
        // Drift indicates a mutation site of HandlerCache.handlers that
        // forgot to mirror the change into insertionOrder (register /
        // deregistration closures), which would corrupt the documented
        // same-priority registration order. Mirrors the MessageHandler-side
        // DebugAssertInsertionOrderInSync; stripped in Release builds.
        [Conditional("DEBUG")]
        private static void DebugAssertBusInsertionOrderInSync(HandlerCache cache)
        {
            System.Diagnostics.Debug.Assert(
                cache.insertionOrder.Count == cache.handlers.Count,
                "Bus-side HandlerCache.insertionOrder must mirror handlers: every first "
                    + "registration appends and every final deregistration removes. A count "
                    + "mismatch means a mutation site skipped the insertionOrder update and "
                    + "same-priority dispatch order is no longer trustworthy."
            );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool HasAnyDispatchEntries(DispatchSnapshot snapshot)
        {
            DispatchBucket[] buckets = snapshot.buckets;
            int bucketCount = snapshot.bucketCount;
            for (int i = 0; i < bucketCount; ++i)
            {
                if (0 < buckets[i].entryCount)
                {
                    return true;
                }
            }

            return false;
        }

        private static void FillDispatchEntries<TMessage>(
            MessageBus messageBus,
            HandlerCache cache,
            SlotKey slotKey,
            int priority,
            DispatchEntry[] entries
        )
            where TMessage : IMessage
        {
            if (cache == null)
            {
                return;
            }

            DebugAssertBusInsertionOrderInSync(cache);
            PrefreezeDescriptor prefreeze = CreatePrefreezeDescriptor(slotKey, priority);
            int index = 0;
            // Iterate insertionOrder, NOT the handlers dictionary: Dictionary
            // enumeration order permutes after Remove/Add churn (freed slots
            // are reused LIFO), which would seat a newly-registered
            // MessageHandler in a destroyed one's position and break the
            // documented same-priority cross-component registration order.
            List<MessageHandler> ordered = cache.insertionOrder;
            int orderedCount = ordered.Count;
            for (int i = 0; i < orderedCount && index < entries.Length; ++i)
            {
                MessageHandler messageHandler = ordered[i];
                object dispatch = GetDispatchLink<TMessage>(messageBus, messageHandler, slotKey);
                entries[index++] = new DispatchEntry(messageHandler, dispatch, prefreeze);
            }
            if (index < entries.Length)
            {
                Array.Clear(entries, index, entries.Length - index);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static DispatchSnapshot AcquireGlobalDispatchSnapshot<TMessage>(
            MessageBus messageBus,
            BusGlobalSlot handlers,
            DispatchKind kind,
            long emissionId
        )
            where TMessage : IMessage
        {
            if (handlers == null)
            {
                return DispatchSnapshot.Empty;
            }

            handlers.lastTouchTicks = messageBus._tickCounter;
            ref DispatchState slotState = ref SelectGlobalDispatchState(handlers, kind);
            slotState ??= new DispatchState();
            DispatchState state = slotState;
            bool hasHandlers = handlers.sharedHandlers.Count > 0;

            if (state.hasPending)
            {
                if (state.pendingDirty || (hasHandlers && state.pending.IsEmpty))
                {
                    ReleaseSnapshot(ref state.pending);
                    if (hasHandlers)
                    {
                        state.pending = BuildGlobalDispatchSnapshot<TMessage>(
                            messageBus,
                            handlers,
                            kind
                        );
                    }
                    else
                    {
                        state.pending = DispatchSnapshot.Empty;
                    }

                    state.pendingDirty = false;
                }
            }
            else if (state.active.IsEmpty && hasHandlers)
            {
                ReleaseSnapshot(ref state.pending);
                state.pending = BuildGlobalDispatchSnapshot<TMessage>(messageBus, handlers, kind);
                state.hasPending = true;
                state.pendingDirty = false;
            }

            if (state.snapshotEmissionId != emissionId)
            {
                if (state.hasPending)
                {
                    // See AcquireDispatchSnapshot: the displaced active
                    // snapshot may still be iterated by an outer emission, so
                    // its release is deferred while a dispatch lease is live.
                    messageBus.ReleaseDisplacedSnapshot(ref state.active);
                    if (state.pendingDirty || (hasHandlers && state.pending.IsEmpty))
                    {
                        ReleaseSnapshot(ref state.pending);
                        state.pending = hasHandlers
                            ? BuildGlobalDispatchSnapshot<TMessage>(messageBus, handlers, kind)
                            : DispatchSnapshot.Empty;

                        state.pendingDirty = false;
                    }

                    state.active = state.pending ?? DispatchSnapshot.Empty;
                    state.pending = DispatchSnapshot.Empty;
                    state.hasPending = false;
                    state.pendingDirty = false;
                }
                else if (!hasHandlers && !state.active.IsEmpty)
                {
                    messageBus.ReleaseDisplacedSnapshot(ref state.active);
                }

                state.snapshotEmissionId = emissionId;
            }

            return state.active;
        }

        private static DispatchSnapshot BuildGlobalDispatchSnapshot<TMessage>(
            MessageBus messageBus,
            BusGlobalSlot handlers,
            DispatchKind kind
        )
            where TMessage : IMessage
        {
            if (handlers == null || handlers.sharedHandlers.Count == 0)
            {
                return DispatchSnapshot.Empty;
            }

            DispatchBucket[] buckets = DispatchBucketPool.Rent(1);
            Dictionary<MessageHandler, int> handlerLookup = handlers.sharedHandlers;
            int entryCount = handlerLookup.Count;
            DispatchEntry[] entries = DispatchEntryPool.Rent(entryCount);
            PrefreezeDescriptor prefreeze = CreateGlobalPrefreezeDescriptor(kind, 0);
            int index = 0;
            foreach (KeyValuePair<MessageHandler, int> kvp in handlerLookup)
            {
                MessageHandler messageHandler = kvp.Key;
                // Global dispatch paths intentionally pass null for the
                // dispatch-link argument. GetDispatchLink is no longer reached
                // from this code path; inlining null here matches what the
                // legacy switch returned for all three Global cases and avoids
                // a per-entry call.
                object dispatch = null;
                entries[index++] = new DispatchEntry(messageHandler, dispatch, prefreeze);
            }

            buckets[0] = new DispatchBucket(0, entries, entryCount, pooledEntries: true);
            return new DispatchSnapshot(buckets, 1, pooled: true);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static PrefreezeDescriptor CreatePrefreezeDescriptor(SlotKey slotKey, int priority)
        {
            if (
                slotKey.Phase != DispatchPhase.Handle
                || slotKey.Variant != DispatchVariant.WithoutContext
            )
            {
                return PrefreezeDescriptor.Empty;
            }
            switch (slotKey.Kind)
            {
                case DispatchKind.Targeted:
                    return new PrefreezeDescriptor(
                        PrefreezeKindTargetedWithoutTargetingHandlers,
                        priority
                    );
                case DispatchKind.Broadcast:
                    return new PrefreezeDescriptor(
                        PrefreezeKindBroadcastWithoutSourceHandlers,
                        priority
                    );
                default:
                    return PrefreezeDescriptor.Empty;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static PrefreezeDescriptor CreateGlobalPrefreezeDescriptor(
            DispatchKind kind,
            int priority
        )
        {
            switch (kind)
            {
                case DispatchKind.Untargeted:
                    return new PrefreezeDescriptor(PrefreezeKindGlobalUntargetedHandlers, priority);
                case DispatchKind.Targeted:
                    return new PrefreezeDescriptor(PrefreezeKindGlobalTargetedHandlers, priority);
                case DispatchKind.Broadcast:
                    return new PrefreezeDescriptor(PrefreezeKindGlobalBroadcastHandlers, priority);
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(kind),
                        kind,
                        "CreateGlobalPrefreezeDescriptor only supports Untargeted, Targeted, Broadcast."
                    );
            }
        }

        private static object GetDispatchLink<TMessage>(
            MessageBus messageBus,
            MessageHandler handler,
            SlotKey slotKey
        )
            where TMessage : IMessage
        {
            DispatchKind kind = slotKey.Kind;
            DispatchPhase phase = slotKey.Phase;
            DispatchVariant variant = slotKey.Variant;
            if (kind == DispatchKind.Untargeted)
            {
                return phase == DispatchPhase.PostProcess
                    ? handler.GetOrCreateUntargetedPostDispatchLink<TMessage>(messageBus)
                    : handler.GetOrCreateUntargetedDispatchLink<TMessage>(messageBus);
            }
            if (kind == DispatchKind.Targeted)
            {
                if (phase == DispatchPhase.PostProcess)
                {
                    return variant == DispatchVariant.WithoutContext
                        ? handler.GetOrCreateTargetedWithoutTargetingPostDispatchLink<TMessage>(
                            messageBus
                        )
                        : handler.GetOrCreateTargetedPostDispatchLink<TMessage>(messageBus);
                }
                return variant == DispatchVariant.WithoutContext
                    ? handler.GetOrCreateTargetedWithoutTargetingDispatchLink<TMessage>(messageBus)
                    : handler.GetOrCreateTargetedDispatchLink<TMessage>(messageBus);
            }
            if (kind == DispatchKind.Broadcast)
            {
                if (phase == DispatchPhase.PostProcess)
                {
                    return variant == DispatchVariant.WithoutContext
                        ? handler.GetOrCreateBroadcastWithoutSourcePostDispatchLink<TMessage>(
                            messageBus
                        )
                        : handler.GetOrCreateBroadcastPostDispatchLink<TMessage>(messageBus);
                }
                return variant == DispatchVariant.WithoutContext
                    ? handler.GetOrCreateBroadcastWithoutSourceDispatchLink<TMessage>(messageBus)
                    : handler.GetOrCreateBroadcastDispatchLink<TMessage>(messageBus);
            }
            return handler.GetOrCreateUntargetedDispatchLink<TMessage>(messageBus);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void InvokeGlobalUntargetedEntry<TMessage>(
            ref TMessage message,
            DispatchEntry entry
        )
            where TMessage : IUntargetedMessage
        {
            MessageHandler handler = entry.handler;
            if (entry.prefreeze.kind == PrefreezeKindGlobalUntargetedHandlers)
            {
                handler.PrefreezeGlobalUntargetedForEmission(_scopedEmissionId, this);
            }

            if (!handler.active)
            {
                return;
            }

            ref IUntargetedMessage interfaceMessage = ref DxUnsafe.As<TMessage, IUntargetedMessage>(
                ref message
            );
            handler.HandleGlobalUntargetedMessage(ref interfaceMessage, this);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void InvokeGlobalTargetedEntry<TMessage>(
            ref InstanceId target,
            ref TMessage message,
            DispatchEntry entry
        )
            where TMessage : ITargetedMessage
        {
            MessageHandler handler = entry.handler;
            if (entry.prefreeze.kind == PrefreezeKindGlobalTargetedHandlers)
            {
                handler.PrefreezeGlobalTargetedForEmission(_scopedEmissionId, this);
            }

            if (!handler.active)
            {
                return;
            }

            ref ITargetedMessage interfaceMessage = ref DxUnsafe.As<TMessage, ITargetedMessage>(
                ref message
            );
            handler.HandleGlobalTargetedMessage(ref target, ref interfaceMessage, this);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void InvokeGlobalBroadcastEntry<TMessage>(
            ref InstanceId source,
            ref TMessage message,
            DispatchEntry entry
        )
            where TMessage : IBroadcastMessage
        {
            MessageHandler handler = entry.handler;
            if (entry.prefreeze.kind == PrefreezeKindGlobalBroadcastHandlers)
            {
                handler.PrefreezeGlobalBroadcastForEmission(_scopedEmissionId, this);
            }

            if (!handler.active)
            {
                return;
            }

            ref IBroadcastMessage interfaceMessage = ref DxUnsafe.As<TMessage, IBroadcastMessage>(
                ref message
            );
            handler.HandleGlobalSourcedBroadcastMessage(ref source, ref interfaceMessage, this);
        }

        [Conditional("ENABLE_IL2CPP")]
        private static void EnsureAotUntargetedBridge<T>()
            where T : IUntargetedMessage
        {
            if (AotBridgeState<T>.UntargetedRegistered)
            {
                return;
            }

            RegisterAotUntargetedBridge(
                typeof(T),
                (Action<IMessageBus, IUntargetedMessage>)AotUntargetedBroadcast<T>
            );
            AotBridgeState<T>.UntargetedRegistered = true;
        }

        [Conditional("ENABLE_IL2CPP")]
        private static void EnsureAotTargetedBridge<T>()
            where T : ITargetedMessage
        {
            if (AotBridgeState<T>.TargetedRegistered)
            {
                return;
            }

            RegisterAotTargetedBridge(
                typeof(T),
                (Action<IMessageBus, InstanceId, ITargetedMessage>)AotTargetedBroadcast<T>
            );
            AotBridgeState<T>.TargetedRegistered = true;
        }

        [Conditional("ENABLE_IL2CPP")]
        private static void EnsureAotSourcedBridge<T>()
            where T : IBroadcastMessage
        {
            if (AotBridgeState<T>.SourcedRegistered)
            {
                return;
            }

            RegisterAotSourcedBridge(
                typeof(T),
                (Action<IMessageBus, InstanceId, IBroadcastMessage>)AotSourcedBroadcast<T>
            );
            AotBridgeState<T>.SourcedRegistered = true;
        }

#if UNITY_2021_3_OR_NEWER
        [UnityEngine.Scripting.Preserve]
#endif
        private static void RegisterAotUntargetedBridge(Type messageType, Delegate bridge)
        {
            if (messageType == null)
            {
                throw new ArgumentNullException(nameof(messageType));
            }

            if (bridge is not Action<IMessageBus, IUntargetedMessage> typedBridge)
            {
                throw new ArgumentException(
                    "AOT untargeted bridge must be Action<IMessageBus, IUntargetedMessage>.",
                    nameof(bridge)
                );
            }

            lock (AotBridgeRegistryLock)
            {
                if (
                    _aotUntargetedBroadcastsByType.TryGetValue(
                        messageType,
                        out Action<IMessageBus, IUntargetedMessage> existing
                    )
                    && existing == typedBridge
                )
                {
                    return;
                }

                var updated = new Dictionary<Type, Action<IMessageBus, IUntargetedMessage>>(
                    _aotUntargetedBroadcastsByType
                )
                {
                    [messageType] = typedBridge,
                };
                Volatile.Write(ref _aotUntargetedBroadcastsByType, updated);
            }
        }

#if UNITY_2021_3_OR_NEWER
        [UnityEngine.Scripting.Preserve]
#endif
        private static void RegisterAotTargetedBridge(Type messageType, Delegate bridge)
        {
            if (messageType == null)
            {
                throw new ArgumentNullException(nameof(messageType));
            }

            if (bridge is not Action<IMessageBus, InstanceId, ITargetedMessage> typedBridge)
            {
                throw new ArgumentException(
                    "AOT targeted bridge must be Action<IMessageBus, InstanceId, ITargetedMessage>.",
                    nameof(bridge)
                );
            }

            lock (AotBridgeRegistryLock)
            {
                if (
                    _aotTargetedBroadcastsByType.TryGetValue(
                        messageType,
                        out Action<IMessageBus, InstanceId, ITargetedMessage> existing
                    )
                    && existing == typedBridge
                )
                {
                    return;
                }

                var updated = new Dictionary<
                    Type,
                    Action<IMessageBus, InstanceId, ITargetedMessage>
                >(_aotTargetedBroadcastsByType)
                {
                    [messageType] = typedBridge,
                };
                Volatile.Write(ref _aotTargetedBroadcastsByType, updated);
            }
        }

#if UNITY_2021_3_OR_NEWER
        [UnityEngine.Scripting.Preserve]
#endif
        private static void RegisterAotSourcedBridge(Type messageType, Delegate bridge)
        {
            if (messageType == null)
            {
                throw new ArgumentNullException(nameof(messageType));
            }

            if (bridge is not Action<IMessageBus, InstanceId, IBroadcastMessage> typedBridge)
            {
                throw new ArgumentException(
                    "AOT sourced bridge must be Action<IMessageBus, InstanceId, IBroadcastMessage>.",
                    nameof(bridge)
                );
            }

            lock (AotBridgeRegistryLock)
            {
                if (
                    _aotSourcedBroadcastsByType.TryGetValue(
                        messageType,
                        out Action<IMessageBus, InstanceId, IBroadcastMessage> existing
                    )
                    && existing == typedBridge
                )
                {
                    return;
                }

                var updated = new Dictionary<
                    Type,
                    Action<IMessageBus, InstanceId, IBroadcastMessage>
                >(_aotSourcedBroadcastsByType)
                {
                    [messageType] = typedBridge,
                };
                Volatile.Write(ref _aotSourcedBroadcastsByType, updated);
            }
        }

        private static void AotUntargetedBroadcast<T>(
            IMessageBus messageBus,
            IUntargetedMessage message
        )
            where T : IUntargetedMessage
        {
            if (typeof(T).IsValueType)
            {
                object box = message;
                ref T typedRef = ref DxUnsafe.As<object, T>(ref box);
                messageBus.UntargetedBroadcast(ref typedRef);
                return;
            }

            T typedMessage = (T)message;
            messageBus.UntargetedBroadcast(ref typedMessage);
        }

        private static void AotTargetedBroadcast<T>(
            IMessageBus messageBus,
            InstanceId target,
            ITargetedMessage message
        )
            where T : ITargetedMessage
        {
            if (typeof(T).IsValueType)
            {
                object box = message;
                ref T typedRef = ref DxUnsafe.As<object, T>(ref box);
                messageBus.TargetedBroadcast(ref target, ref typedRef);
                return;
            }

            T typedMessage = (T)message;
            messageBus.TargetedBroadcast(ref target, ref typedMessage);
        }

        private static void AotSourcedBroadcast<T>(
            IMessageBus messageBus,
            InstanceId source,
            IBroadcastMessage message
        )
            where T : IBroadcastMessage
        {
            if (typeof(T).IsValueType)
            {
                object box = message;
                ref T typedRef = ref DxUnsafe.As<object, T>(ref box);
                messageBus.SourcedBroadcast(ref source, ref typedRef);
                return;
            }

            T typedMessage = (T)message;
            messageBus.SourcedBroadcast(ref source, ref typedMessage);
        }

        private static void ThrowMissingAotBridge(Type messageType, string dispatchKind)
        {
            throw new InvalidOperationException(
                "DxMessaging cannot perform untyped "
                    + dispatchKind
                    + " dispatch for message type '"
                    + messageType.FullName
                    + "' under an AOT scripting backend because no rooted dispatch bridge was registered. "
                    + "Use a source-visible concrete message type, annotate it with the DxMessaging message attribute, "
                    + "or touch the generic registration or typed dispatch path for that concrete type before untyped dispatch."
            );
        }

        private Action<IUntargetedMessage> CreateUntargetedBroadcastDelegate(Type messageType)
        {
            // ReSharper disable once PossibleNullReferenceException
            MethodInfo broadcastMethod = MessageBusType
                .GetMethod(nameof(UntargetedBroadcast))
                .MakeGenericMethod(messageType);
            // ReSharper disable once PossibleNullReferenceException
            MethodInfo helperMethod = MessageBusType
                .GetMethod(
                    nameof(UntargetedBroadcastReflectionHelper),
                    ReflectionHelperBindingFlags
                )
                .MakeGenericMethod(messageType);

            return (Action<IUntargetedMessage>)
                helperMethod.Invoke(null, new object[] { this, broadcastMethod });
        }

        private Action<InstanceId, ITargetedMessage> CreateTargetedBroadcastDelegate(
            Type messageType
        )
        {
            // ReSharper disable once PossibleNullReferenceException
            MethodInfo broadcastMethod = MessageBusType
                .GetMethod(nameof(TargetedBroadcast))
                .MakeGenericMethod(messageType);
            // ReSharper disable once PossibleNullReferenceException
            MethodInfo helperMethod = MessageBusType
                .GetMethod(nameof(TargetedBroadcastReflectionHelper), ReflectionHelperBindingFlags)
                .MakeGenericMethod(messageType);

            return (Action<InstanceId, ITargetedMessage>)
                helperMethod.Invoke(null, new object[] { this, broadcastMethod });
        }

        private Action<InstanceId, IBroadcastMessage> CreateSourcedBroadcastDelegate(
            Type messageType
        )
        {
            // ReSharper disable once PossibleNullReferenceException
            MethodInfo broadcastMethod = MessageBusType
                .GetMethod(nameof(SourcedBroadcast))
                .MakeGenericMethod(messageType);
            // ReSharper disable once PossibleNullReferenceException
            MethodInfo helperMethod = MessageBusType
                .GetMethod(nameof(SourcedBroadcastReflectionHelper), ReflectionHelperBindingFlags)
                .MakeGenericMethod(messageType);

            return (Action<InstanceId, IBroadcastMessage>)
                helperMethod.Invoke(null, new object[] { this, broadcastMethod });
        }

        // https://blogs.msmvps.com/jonskeet/2008/08/09/making-reflection-fly-and-exploring-delegates/
        private static Action<IUntargetedMessage> UntargetedBroadcastReflectionHelper<T>(
            IMessageBus messageBus,
            MethodInfo methodInfo
        )
            where T : IUntargetedMessage
        {
            FastUntargetedBroadcast<T> untargetedBroadcast =
                (FastUntargetedBroadcast<T>)
                    Delegate.CreateDelegate(
                        typeof(FastUntargetedBroadcast<T>),
                        messageBus,
                        methodInfo
                    );

            return UntypedBroadcast;

            void UntypedBroadcast(IUntargetedMessage message)
            {
                if (typeof(T).IsValueType)
                {
                    object box = message;
                    ref T typedRef = ref DxUnsafe.As<object, T>(ref box);
                    untargetedBroadcast(ref typedRef);
                    return;
                }

                T typedMessage = (T)message;
                untargetedBroadcast(ref typedMessage);
            }
        }

        private static Action<InstanceId, ITargetedMessage> TargetedBroadcastReflectionHelper<T>(
            IMessageBus messageBus,
            MethodInfo methodInfo
        )
            where T : ITargetedMessage
        {
            FastTargetedBroadcast<T> targetedBroadcast =
                (FastTargetedBroadcast<T>)
                    Delegate.CreateDelegate(
                        typeof(FastTargetedBroadcast<T>),
                        messageBus,
                        methodInfo
                    );

            return UntypedBroadcast;

            void UntypedBroadcast(InstanceId target, ITargetedMessage message)
            {
                if (typeof(T).IsValueType)
                {
                    object box = message;
                    ref T typedRef = ref DxUnsafe.As<object, T>(ref box);
                    targetedBroadcast(ref target, ref typedRef);
                    return;
                }

                T typedMessage = (T)message;
                targetedBroadcast(ref target, ref typedMessage);
            }
        }

        private static Action<InstanceId, IBroadcastMessage> SourcedBroadcastReflectionHelper<T>(
            IMessageBus messageBus,
            MethodInfo methodInfo
        )
            where T : IBroadcastMessage
        {
            FastSourcedBroadcast<T> sourcedBroadcast =
                (FastSourcedBroadcast<T>)
                    Delegate.CreateDelegate(
                        typeof(FastSourcedBroadcast<T>),
                        messageBus,
                        methodInfo
                    );

            return UntypedBroadcast;

            void UntypedBroadcast(InstanceId target, IBroadcastMessage message)
            {
                if (typeof(T).IsValueType)
                {
                    object box = message;
                    ref T typedRef = ref DxUnsafe.As<object, T>(ref box);
                    sourcedBroadcast(ref target, ref typedRef);
                    return;
                }

                T typedMessage = (T)message;
                sourcedBroadcast(ref target, ref typedMessage);
            }
        }

#if UNITY_2021_3_OR_NEWER
        private static Action<MonoBehaviour, object[]> CompileMethodAction(MethodInfo methodInfo)
        {
            ParameterExpression componentParameter = Expression.Parameter(
                typeof(MonoBehaviour),
                "targetComponent"
            );
            ParameterExpression argsParameter = Expression.Parameter(typeof(object[]), "args");
            ParameterInfo[] methodParams = methodInfo.GetParameters();

            ArgumentExpressionsCache.Clear();
            for (int i = 0; i < methodParams.Length; ++i)
            {
                Expression indexAccess = Expression.ArrayIndex(
                    argsParameter,
                    Expression.Constant(i)
                );
                Expression convertedArg = Expression.Convert(
                    indexAccess,
                    methodParams[i].ParameterType
                );
                ArgumentExpressionsCache.Add(convertedArg);
            }

            // ReSharper disable once AssignNullToNotNullAttribute
            Expression instanceExpression = methodInfo.IsStatic
                ? null
                : Expression.Convert(componentParameter, methodInfo.DeclaringType);
            MethodCallExpression callExpression = Expression.Call(
                instanceExpression,
                methodInfo,
                ArgumentExpressionsCache
            );
            Expression<Action<MonoBehaviour, object[]>> lambda = Expression.Lambda<
                Action<MonoBehaviour, object[]>
            >(callExpression, componentParameter, argsParameter);

            return lambda.Compile();
        }
#endif

        private void SendMessage(
            MonoBehaviour recipient,
            ref ReflexiveMessage message,
            bool onlyActive
        )
        {
            if (onlyActive && !recipient.enabled)
            {
                return;
            }

            if (!_recipientCache.Add(recipient))
            {
                return;
            }

            Type componentType = recipient.GetType();
            if (
                !_methodCache.TryGetValue(
                    componentType,
                    out Dictionary<MethodSignatureKey, Action<MonoBehaviour, object[]>> methodCache
                )
            )
            {
                _methodCache[componentType] = methodCache =
                    new Dictionary<MethodSignatureKey, Action<MonoBehaviour, object[]>>();
            }

            MethodSignatureKey lookupKey = message.signatureKey;
            if (!methodCache.TryGetValue(lookupKey, out Action<MonoBehaviour, object[]> method))
            {
                MethodInfo methodInfo = null;
                try
                {
                    methodInfo = componentType.GetMethod(
                        message.method,
                        ReflexiveMethodBindingFlags,
                        null,
                        message.parameterTypes,
                        null
                    );
                }
                catch (AmbiguousMatchException)
                {
                    MethodInfo[] matchingMethods = componentType.GetMethods(
                        ReflexiveMethodBindingFlags
                    );
                    Span<MethodInfo> span = matchingMethods.AsSpan();
                    for (int i = 0; i < span.Length; ++i)
                    {
                        MethodInfo matchingMethod = span[i];
                        if (
                            !string.Equals(
                                matchingMethod.Name,
                                message.method,
                                StringComparison.Ordinal
                            )
                            || !ParameterTypesMatch(
                                matchingMethod.GetParameters(),
                                message.parameterTypes
                            )
                        )
                        {
                            continue;
                        }

                        methodInfo = matchingMethod;
                        break;
                    }
                }
                catch
                {
                    methodInfo = null;
                }

                if (methodInfo != null)
                {
                    method = CompileMethodAction(methodInfo);
                }
                methodCache[lookupKey] = method;
            }

            method?.Invoke(recipient, message.parameters);
        }

        private static bool ParameterTypesMatch(ParameterInfo[] methodParams, Type[] expectedTypes)
        {
            if (methodParams.Length != expectedTypes.Length)
            {
                return false;
            }

            for (int i = 0; i < methodParams.Length; ++i)
            {
                if (methodParams[i].ParameterType != expectedTypes[i])
                {
                    return false;
                }
            }
            return true;
        }
    }
}
