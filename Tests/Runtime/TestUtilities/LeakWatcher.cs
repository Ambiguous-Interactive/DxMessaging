#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime
{
    using System;
    using System.Globalization;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using NUnit.Framework;

    /// <summary>
    /// Snapshots the public registration counters of an
    /// <see cref="IMessageBus"/> on construction and asserts on
    /// <see cref="Dispose"/> that the counters returned to their starting
    /// values. Intended to bracket a unit of work that should leave the bus
    /// without leaks (for example a register/dispatch/deregister cycle).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Counter source: this watcher reads every public registration counter on
    /// <see cref="IMessageBus"/>: <see cref="IMessageBus.RegisteredUntargeted"/>,
    /// <see cref="IMessageBus.RegisteredTargeted"/>,
    /// <see cref="IMessageBus.RegisteredBroadcast"/>,
    /// <see cref="IMessageBus.RegisteredInterceptors"/>,
    /// <see cref="IMessageBus.RegisteredPostProcessors"/>, and
    /// <see cref="IMessageBus.RegisteredGlobalAcceptAll"/>. Every counter is part of
    /// the public surface, so registration accounting does not inspect internal storage. If a
    /// future API adds a seventh registration kind, <see cref="Snapshot"/> and
    /// <see cref="LeakedRegistrations"/> need to be extended in lock-step.
    /// Slot occupancy counters (<see cref="IMessageBus.OccupiedTypeSlots"/> and
    /// <see cref="IMessageBus.OccupiedTargetSlots"/>) are also captured and
    /// reported. They are enforced only by <see cref="WatchWithSlots"/> so the
    /// default watcher remains suitable for registration-only leak checks that
    /// do not force a trim inside the watched region.
    /// Pass a handler to also enforce retained context-key and priority-cache
    /// counts for that handler on the watched bus. This optional diagnostic
    /// reads internal storage only at snapshot boundaries or on explicit queries.
    /// </para>
    /// <para>
    /// Allocation note: the <see cref="Snapshot"/> getter and the
    /// <see cref="LeakedRegistrations"/> getter both walk the per-message-type
    /// caches that back <see cref="IMessageBus.RegisteredInterceptors"/> and
    /// <see cref="IMessageBus.RegisteredPostProcessors"/>. Each call is O(types),
    /// so callers reading these properties in tight loops should snapshot at
    /// region boundaries instead of polling every frame.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// using (LeakWatcher.Watch())
    /// {
    ///     MessageRegistrationHandle handle = token.RegisterUntargeted&lt;Foo&gt;(_ =&gt; { });
    ///     Foo message = new();
    ///     message.EmitUntargeted();
    ///     token.RemoveRegistration(handle);
    /// }
    /// // Dispose asserts the bus drained.
    /// </code>
    /// </example>
    public sealed class LeakWatcher : IDisposable
    {
        private readonly IMessageBus _bus;
        private readonly bool _throwOnLeak;
        private readonly string _label;
        private readonly int _initialUntargeted;
        private readonly int _initialTargeted;
        private readonly int _initialBroadcast;
        private readonly int _initialInterceptors;
        private readonly int _initialPostProcessors;
        private readonly int _initialGlobalAcceptAll;
        private readonly int _initialTypeSlotCount;
        private readonly int _initialTargetSlotCount;
        private readonly bool _watchSlots;
        private readonly MessageHandler _handler;
        private readonly int _initialHandlerContexts;
        private readonly int _initialHandlerPriorityCaches;

        private bool _disposed;
        private int _finalUntargeted;
        private int _finalTargeted;
        private int _finalBroadcast;
        private int _finalInterceptors;
        private int _finalPostProcessors;
        private int _finalGlobalAcceptAll;
        private int _finalTypeSlotCount;
        private int _finalTargetSlotCount;
        private int _finalHandlerContexts;
        private int _finalHandlerPriorityCaches;

        /// <summary>
        /// Captures the initial registration counts on the supplied
        /// <see cref="IMessageBus"/>. Pass the global bus by passing
        /// <see langword="null"/> or omitting the argument.
        /// </summary>
        /// <param name="bus">Bus to watch. Defaults to the global bus.</param>
        /// <param name="throwOnLeak">
        /// When <see langword="true"/> (default), <see cref="Dispose"/>
        /// fails the current test via <see cref="Assert.Fail(string)"/>
        /// if the post-disposal counts differ from the pre-construction
        /// counts. When <see langword="false"/>, the leak is captured into
        /// <see cref="LeakedRegistrations"/> for inspection.
        /// </param>
        /// <param name="label">
        /// Optional label included in the failure message. Useful for
        /// distinguishing multiple watchers in a single test.
        /// </param>
        /// <param name="watchSlots">Whether disposal also enforces bus slot occupancy.</param>
        /// <param name="handler">Optional handler whose retained storage must return to baseline.</param>
        public LeakWatcher(
            IMessageBus bus = null,
            bool throwOnLeak = true,
            string label = null,
            bool watchSlots = false,
            MessageHandler handler = null
        )
        {
            _bus = bus ?? MessageHandler.MessageBus;
            if (_bus == null)
            {
                throw new InvalidOperationException(
                    "LeakWatcher requires a non-null bus. The global bus is null; "
                        + "ensure DxMessagingStaticState.Reset has run."
                );
            }

            _throwOnLeak = throwOnLeak;
            _label = label;
            _watchSlots = watchSlots;
            _handler = handler;
            _initialUntargeted = _bus.RegisteredUntargeted;
            _initialTargeted = _bus.RegisteredTargeted;
            _initialBroadcast = _bus.RegisteredBroadcast;
            _initialInterceptors = _bus.RegisteredInterceptors;
            _initialPostProcessors = _bus.RegisteredPostProcessors;
            _initialGlobalAcceptAll = _bus.RegisteredGlobalAcceptAll;
            _initialTypeSlotCount = _bus.OccupiedTypeSlots;
            _initialTargetSlotCount = _bus.OccupiedTargetSlots;
            if (_handler != null)
            {
                _handler.GetRetainedStorageCounts(
                    _bus,
                    out _initialHandlerContexts,
                    out _initialHandlerPriorityCaches
                );
            }
        }

        /// <summary>
        /// Convenience factory that builds a watcher with default options on
        /// the global bus. Use inside a <c>using</c> block to bracket the
        /// region under test.
        /// </summary>
        public static LeakWatcher Watch(string label = null)
        {
            return new LeakWatcher(bus: null, throwOnLeak: true, label: label);
        }

        /// <summary>
        /// Convenience factory that also asserts per-type and per-target slot
        /// occupancy returns to the starting counts. Use when the watched
        /// region performs an explicit trim or otherwise expects all empty
        /// memory-reclamation slots to be reclaimed before disposal.
        /// </summary>
        public static LeakWatcher WatchWithSlots(string label = null)
        {
            return new LeakWatcher(bus: null, throwOnLeak: true, label: label, watchSlots: true);
        }

        /// <summary>
        /// Convenience factory for a specific bus that also asserts
        /// per-type and per-target slot occupancy returns to the starting
        /// counts. An optional handler also checks retained context and priority caches.
        /// </summary>
        /// <param name="bus">Bus to watch. Null selects the global bus.</param>
        /// <param name="throwOnLeak">Whether disposal fails the test when counts drift.</param>
        /// <param name="label">Optional scope label for failure diagnostics.</param>
        /// <param name="handler">Optional handler whose storage is checked on this bus.</param>
        /// <returns>A watcher with bus slot checks and optional handler storage checks enabled.</returns>
        public static LeakWatcher WatchWithSlots(
            IMessageBus bus,
            bool throwOnLeak = true,
            string label = null,
            MessageHandler handler = null
        )
        {
            return new LeakWatcher(
                bus: bus,
                throwOnLeak: throwOnLeak,
                label: label,
                watchSlots: true,
                handler: handler
            );
        }

        /// <summary>
        /// The total registration count read from the live bus across all
        /// six counter kinds (handler counts plus interceptor, post-processor,
        /// and global-accept-all counts). Updates on every read so callers
        /// can compare against <see cref="InitialSnapshot"/>.
        /// </summary>
        /// <remarks>
        /// Each access walks the per-message-type caches behind
        /// <see cref="IMessageBus.RegisteredInterceptors"/> and
        /// <see cref="IMessageBus.RegisteredPostProcessors"/>, so the call is
        /// O(types). Avoid polling this property inside a tight loop.
        /// </remarks>
        public int Snapshot
        {
            get
            {
                return _bus.RegisteredUntargeted
                    + _bus.RegisteredTargeted
                    + _bus.RegisteredBroadcast
                    + _bus.RegisteredInterceptors
                    + _bus.RegisteredPostProcessors
                    + _bus.RegisteredGlobalAcceptAll;
            }
        }

        /// <summary>
        /// The total registration count captured at construction. Frozen for
        /// the lifetime of the watcher.
        /// </summary>
        public int InitialSnapshot =>
            _initialUntargeted
            + _initialTargeted
            + _initialBroadcast
            + _initialInterceptors
            + _initialPostProcessors
            + _initialGlobalAcceptAll;

        /// <summary>
        /// The live occupied-slot count across per-message-type and
        /// per-target/source slots. Updates on each read until disposal.
        /// </summary>
        public int SlotSnapshot =>
            (_disposed ? _finalTypeSlotCount : _bus.OccupiedTypeSlots)
            + (_disposed ? _finalTargetSlotCount : _bus.OccupiedTargetSlots);

        /// <summary>
        /// The occupied-slot count captured at construction.
        /// </summary>
        public int InitialSlotSnapshot => _initialTypeSlotCount + _initialTargetSlotCount;

        /// <summary>
        /// Number of additional registrations leaked relative to the initial
        /// snapshot. Negative values indicate a regression where the watched
        /// region removed registrations beyond what it owned. Reads the live
        /// bus on each access until <see cref="Dispose"/> is called, after
        /// which the disposal-time delta is returned.
        /// </summary>
        /// <remarks>
        /// Each pre-disposal access pays the same O(types) walk as
        /// <see cref="Snapshot"/>. Snapshot at region boundaries.
        /// </remarks>
        public int LeakedRegistrations
        {
            get
            {
                if (_disposed)
                {
                    return TotalDelta(
                        _finalUntargeted,
                        _finalTargeted,
                        _finalBroadcast,
                        _finalInterceptors,
                        _finalPostProcessors,
                        _finalGlobalAcceptAll
                    );
                }

                return TotalDelta(
                    _bus.RegisteredUntargeted,
                    _bus.RegisteredTargeted,
                    _bus.RegisteredBroadcast,
                    _bus.RegisteredInterceptors,
                    _bus.RegisteredPostProcessors,
                    _bus.RegisteredGlobalAcceptAll
                );
            }
        }

        /// <summary>
        /// Number of occupied per-message-type slots leaked relative to the
        /// initial snapshot. Negative values indicate the watched region
        /// reclaimed slots it did not create.
        /// </summary>
        public int LeakedTypeSlots =>
            (_disposed ? _finalTypeSlotCount : _bus.OccupiedTypeSlots) - _initialTypeSlotCount;

        /// <summary>
        /// Number of occupied per-target/source slots leaked relative to the
        /// initial snapshot. Negative values indicate the watched region
        /// reclaimed slots it did not create.
        /// </summary>
        public int LeakedTargetSlots =>
            (_disposed ? _finalTargetSlotCount : _bus.OccupiedTargetSlots)
            - _initialTargetSlotCount;

        /// <summary>
        /// Total occupied slot drift relative to the initial snapshot.
        /// </summary>
        public int LeakedSlots => LeakedTypeSlots + LeakedTargetSlots;

        /// <summary>
        /// Retained handler context-key drift, or zero when no handler was supplied.
        /// </summary>
        public int LeakedHandlerContexts
        {
            get
            {
                ReadHandlerStorageCounts(out int contexts, out _);
                return contexts - _initialHandlerContexts;
            }
        }

        /// <summary>
        /// Retained scalar and context-keyed priority-cache drift, or zero when no handler was supplied.
        /// </summary>
        public int LeakedHandlerPriorityCaches
        {
            get
            {
                ReadHandlerStorageCounts(out _, out int priorities);
                return priorities - _initialHandlerPriorityCaches;
            }
        }

        /// <summary>
        /// Returns a one-line per-counter description of the delta between the
        /// initial snapshot and the current (or final, post-disposal) bus
        /// counts. Intended for inclusion in NUnit assertion messages so a
        /// failure surfaces the exact registration kinds that drifted instead
        /// of just the aggregate delta.
        /// </summary>
        /// <remarks>
        /// Allocates one string per call. Pre-disposal calls walk the live bus
        /// counters (O(types)); post-disposal calls read the snapshot taken
        /// inside <see cref="Dispose"/>. Safe to call before or after disposal.
        /// </remarks>
        public string DescribeDelta()
        {
            int currentUntargeted = _disposed ? _finalUntargeted : _bus.RegisteredUntargeted;
            int currentTargeted = _disposed ? _finalTargeted : _bus.RegisteredTargeted;
            int currentBroadcast = _disposed ? _finalBroadcast : _bus.RegisteredBroadcast;
            int currentInterceptors = _disposed ? _finalInterceptors : _bus.RegisteredInterceptors;
            int currentPostProcessors = _disposed
                ? _finalPostProcessors
                : _bus.RegisteredPostProcessors;
            int currentGlobalAcceptAll = _disposed
                ? _finalGlobalAcceptAll
                : _bus.RegisteredGlobalAcceptAll;
            int currentTypeSlotCount = _disposed ? _finalTypeSlotCount : _bus.OccupiedTypeSlots;
            int currentTargetSlotCount = _disposed
                ? _finalTargetSlotCount
                : _bus.OccupiedTargetSlots;

            int delta = TotalDelta(
                currentUntargeted,
                currentTargeted,
                currentBroadcast,
                currentInterceptors,
                currentPostProcessors,
                currentGlobalAcceptAll
            );

            string scope = string.IsNullOrEmpty(_label) ? string.Empty : $" ({_label})";
            return string.Format(
                CultureInfo.InvariantCulture,
                "LeakWatcher{0}: delta={1} (Untargeted {2}->{3}, Targeted {4}->{5}, "
                    + "Broadcast {6}->{7}, Interceptors {8}->{9}, PostProcessors {10}->{11}, "
                    + "GlobalAcceptAll {12}->{13}, TypeSlots {14}->{15}, TargetSlots {16}->{17}).{18}",
                scope,
                delta,
                _initialUntargeted,
                currentUntargeted,
                _initialTargeted,
                currentTargeted,
                _initialBroadcast,
                currentBroadcast,
                _initialInterceptors,
                currentInterceptors,
                _initialPostProcessors,
                currentPostProcessors,
                _initialGlobalAcceptAll,
                currentGlobalAcceptAll,
                _initialTypeSlotCount,
                currentTypeSlotCount,
                _initialTargetSlotCount,
                currentTargetSlotCount,
                DescribeHandlerStorage()
            );
        }

        /// <summary>
        /// Compares the current bus counts to the initial snapshot. When
        /// <c>throwOnLeak</c> is true the diff is asserted via NUnit and the
        /// test fails on any mismatch. Idempotent.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            _finalUntargeted = _bus.RegisteredUntargeted;
            _finalTargeted = _bus.RegisteredTargeted;
            _finalBroadcast = _bus.RegisteredBroadcast;
            _finalInterceptors = _bus.RegisteredInterceptors;
            _finalPostProcessors = _bus.RegisteredPostProcessors;
            _finalGlobalAcceptAll = _bus.RegisteredGlobalAcceptAll;
            _finalTypeSlotCount = _bus.OccupiedTypeSlots;
            _finalTargetSlotCount = _bus.OccupiedTargetSlots;
            if (_handler != null)
            {
                _handler.GetRetainedStorageCounts(
                    _bus,
                    out _finalHandlerContexts,
                    out _finalHandlerPriorityCaches
                );
            }

            int delta = TotalDelta(
                _finalUntargeted,
                _finalTargeted,
                _finalBroadcast,
                _finalInterceptors,
                _finalPostProcessors,
                _finalGlobalAcceptAll
            );
            int typeSlotDelta = _finalTypeSlotCount - _initialTypeSlotCount;
            int targetSlotDelta = _finalTargetSlotCount - _initialTargetSlotCount;
            bool slotDeltaIsClean = !_watchSlots || (typeSlotDelta == 0 && targetSlotDelta == 0);
            bool handlerStorageIsClean =
                _finalHandlerContexts == _initialHandlerContexts
                && _finalHandlerPriorityCaches == _initialHandlerPriorityCaches;
            if (delta == 0 && slotDeltaIsClean && handlerStorageIsClean)
            {
                return;
            }

            if (!_throwOnLeak)
            {
                return;
            }

            Assert.Fail(BuildFailureMessage(delta, typeSlotDelta, targetSlotDelta));
        }

        private string BuildFailureMessage(int delta, int typeSlotDelta, int targetSlotDelta)
        {
            string scope = string.IsNullOrEmpty(_label) ? string.Empty : $" ({_label})";
            return string.Format(
                CultureInfo.InvariantCulture,
                "LeakWatcher{0}: watched counts changed during the region. "
                    + "Registration delta={1}; "
                    + "type slot delta={14}, target slot delta={15}. "
                    + "Untargeted {2}->{3}, Targeted {4}->{5}, Broadcast {6}->{7}, "
                    + "Interceptors {8}->{9}, PostProcessors {10}->{11}, GlobalAcceptAll {12}->{13}, "
                    + "TypeSlots {16}->{17}, TargetSlots {18}->{19}.{20}",
                scope,
                delta,
                _initialUntargeted,
                _finalUntargeted,
                _initialTargeted,
                _finalTargeted,
                _initialBroadcast,
                _finalBroadcast,
                _initialInterceptors,
                _finalInterceptors,
                _initialPostProcessors,
                _finalPostProcessors,
                _initialGlobalAcceptAll,
                _finalGlobalAcceptAll,
                typeSlotDelta,
                targetSlotDelta,
                _initialTypeSlotCount,
                _finalTypeSlotCount,
                _initialTargetSlotCount,
                _finalTargetSlotCount,
                DescribeHandlerStorage()
            );
        }

        private void ReadHandlerStorageCounts(out int contexts, out int priorities)
        {
            contexts = _finalHandlerContexts;
            priorities = _finalHandlerPriorityCaches;
            if (!_disposed && _handler != null)
            {
                _handler.GetRetainedStorageCounts(_bus, out contexts, out priorities);
            }
        }

        private string DescribeHandlerStorage()
        {
            if (_handler == null)
            {
                return string.Empty;
            }
            ReadHandlerStorageCounts(out int contexts, out int priorities);
            return string.Format(
                CultureInfo.InvariantCulture,
                " HandlerContexts {0}->{1}, HandlerPriorityCaches {2}->{3}.",
                _initialHandlerContexts,
                contexts,
                _initialHandlerPriorityCaches,
                priorities
            );
        }

        private int TotalDelta(
            int untargeted,
            int targeted,
            int broadcast,
            int interceptors,
            int postProcessors,
            int globalAcceptAll
        )
        {
            int sumNow =
                untargeted + targeted + broadcast + interceptors + postProcessors + globalAcceptAll;
            int sumThen =
                _initialUntargeted
                + _initialTargeted
                + _initialBroadcast
                + _initialInterceptors
                + _initialPostProcessors
                + _initialGlobalAcceptAll;
            return sumNow - sumThen;
        }
    }
}
#endif
