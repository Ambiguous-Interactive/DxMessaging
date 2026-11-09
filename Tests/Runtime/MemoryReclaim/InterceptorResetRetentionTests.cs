namespace DxMessaging.Tests.Runtime.MemoryReclaim
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Runtime.CompilerServices;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Core.Pooling;
    using DxMessaging.Tests;
    using DxMessaging.Tests.Runtime;
    using NUnit.Framework;
    using Bus = DxMessaging.Core.MessageBus.MessageBus;

    /// <summary>A stale interceptor handle must not retain unrelated targets after default-bus reset.</summary>
    /// <remarks>
    /// 2026-11-06: reset dropped the type map while stale raw handles still owned populated
    /// interceptor groups and flat arrays. Separate factory, observation, emission and cleanup
    /// frames distinguish that retained graph from temporary JIT roots. Custom-bus preservation
    /// and the retained handle's own target are deliberate positive controls.
    /// 2026-11-09: an ARM64 Mono full run retained both targets after cleanup. The cause
    /// remains unresolved. Failure-only ownership output follows the original weak-target
    /// observations without another collection or a change to their assertions.
    /// </remarks>
    [TestFixture]
    [Category("MemoryReclaim")]
#if !UNITY_2021_3_OR_NEWER
    [NonParallelizable]
#endif
    public sealed class InterceptorResetRetentionTests
    {
        private GlobalBusScope _global;
        private bool _debug;
        private Action<LogLevel, string> _log;
        private DiagnosticsTarget _diagnostics;
        private bool _stackTraces;
        private int _bufferSize;

        /// <summary>Captures and isolates globals before each retention case.</summary>
        [SetUp]
        public void CaptureStaticState()
        {
            _debug = MessagingDebug.enabled;
            _log = MessagingDebug.LogFunction;
            _diagnostics = IMessageBus.GlobalDiagnosticsTargets;
            _stackTraces = IMessageBus.GlobalDiagnosticsStackTraces;
            _bufferSize = IMessageBus.GlobalMessageBufferSize;
            _global = GlobalBusScope.CaptureAndReset();
            MessagingDebug.enabled = false;
        }

        /// <summary>Restores global configuration and releases the isolated bus scope.</summary>
        [TearDown]
        public void RestoreStaticState()
        {
            try
            {
                DxMessagingStaticState.Reset();
            }
            finally
            {
                MessagingDebug.enabled = _debug;
                MessagingDebug.LogFunction = _log;
                IMessageBus.GlobalDiagnosticsTargets = _diagnostics;
                IMessageBus.GlobalDiagnosticsStackTraces = _stackTraces;
                IMessageBus.GlobalMessageBufferSize = _bufferSize;
                _global.Dispose();
            }
        }

        /// <summary>Enumerates message, ownership, reset, and teardown combinations.</summary>
        /// <returns>Named retention cases with their original arguments.</returns>
        private static IEnumerable<TestCaseData> Cases()
        {
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            foreach (bool custom in new[] { false, true })
            foreach (bool hold in new[] { false, true })
            foreach (bool reset in new[] { false, true })
            foreach (bool warm in new[] { false, true })
            foreach (bool remove in new[] { false, true })
            foreach (bool trim in new[] { false, true })
            foreach (bool inside in new[] { false, true })
            foreach (bool diagnostics in new[] { false, true })
            {
                yield return new TestCaseData(
                    scenario,
                    custom,
                    hold,
                    reset,
                    warm,
                    remove,
                    trim,
                    inside,
                    diagnostics
                ).SetName(
                    $"InterceptorResetRetention_{scenario}_custom{custom}_hold{hold}_reset{reset}_warm{warm}_remove{remove}_trim{trim}_inside{inside}_diagnostics{diagnostics}"
                );
            }
        }

        /// <summary>Checks reset ownership, stale handles, callback counts, and final weak targets.</summary>
        /// <param name="scenario">Message route under test.</param>
        /// <param name="custom">Whether the case uses a custom global bus.</param>
        /// <param name="hold">Whether to retain the anchor handle before cleanup.</param>
        /// <param name="reset">Whether to reset the default bus during setup.</param>
        /// <param name="warm">Whether to emit once before changing registrations.</param>
        /// <param name="remove">Whether to remove the peer before observation.</param>
        /// <param name="trim">Whether to force-trim after setup.</param>
        /// <param name="inside">Whether to emit before observation and reset inside the callback when requested.</param>
        /// <param name="diagnostics">Whether to enable bus diagnostics.</param>
        [TestCaseSource(nameof(Cases))]
        public void DefaultResetReleasesPeersAndPreservesCustomBusOwnership(
            MessageScenario scenario,
            bool custom,
            bool hold,
            bool reset,
            bool warm,
            bool remove,
            bool trim,
            bool inside,
            bool diagnostics
        )
        {
            State state = new State(
                scenario,
                custom,
                hold,
                reset,
                warm,
                remove,
                trim,
                inside,
                diagnostics
            );
            using (LeakWatcher watcher = LeakWatcher.WatchWithSlots(state.Bus, label: state.Label))
            {
                try
                {
                    state.Populate();
                    state.Observe();
                    state.ExerciseFreshRegistration();
                }
                finally
                {
                    state.Cleanup();
                }
            }

            if (
                state.PeerAlive != (!remove && (custom || !reset))
                || state.AnchorAlive != (hold || custom || !reset)
                || state.FinalPeerAlive
                || state.FinalAnchorAlive
            )
            {
                TestContext.WriteLine(state.BuildReleasedOwnershipReport());
            }

            string report = state.Report;
            TestContext.WriteLine(report);
            AssertState(() =>
            {
                Assert.That(state.PeerAlive, Is.EqualTo(!remove && (custom || !reset)), report);
                Assert.That(state.AnchorAlive, Is.EqualTo(hold || custom || !reset), report);
                Assert.That(state.HandleValid, Is.EqualTo(hold), report);
                Assert.That(
                    state.BeforeAnchorCalls,
                    Is.EqualTo((warm ? 1 : 0) + (inside ? 1 : 0)),
                    report
                );
                Assert.That(
                    state.BeforePeerCalls,
                    Is.EqualTo((warm ? 1 : 0) + (inside && !reset && !remove ? 1 : 0)),
                    report
                );
                Assert.That(state.ResetCalls, Is.EqualTo(reset ? 1 : 0), report);
                int survivors = custom || !reset ? (remove ? 1 : 2) : 0;
                Assert.That(state.BeforeRegistered, Is.EqualTo(survivors), report);
                Assert.That(state.AfterStaleRegistered, Is.EqualTo(survivors + 1), report);
                Assert.That(state.FreshCalls, Is.EqualTo(1), report);
                Assert.That(
                    state.AfterFreshAnchorCalls,
                    Is.EqualTo(state.BeforeAnchorCalls + (custom || !reset ? 1 : 0)),
                    report
                );
                Assert.That(
                    state.AfterFreshPeerCalls,
                    Is.EqualTo(state.BeforePeerCalls + (!remove && (custom || !reset) ? 1 : 0)),
                    report
                );
                Assert.That(state.AfterFreshRemovalRegistered, Is.EqualTo(survivors), report);
                Assert.That(state.FinalPeerAlive, Is.False, report);
                Assert.That(state.FinalAnchorAlive, Is.False, report);
                Assert.That(state.FinalHandleValid, Is.False, report);
            });
        }

        /// <summary>Runs assertions with the framework's supported aggregation behavior.</summary>
        /// <param name="assertions">Assertions for the complete recorded case state.</param>
        private static void AssertState(Action assertions)
        {
#if UNITY_2021_3_OR_NEWER
            assertions();
#else
            Assert.Multiple(assertions.Invoke);
#endif
        }

        /// <summary>Owns case inputs, bus lifetime, and recorded receiver observations.</summary>
        private sealed class State
        {
            internal readonly Bus Bus;
            internal readonly string Label;
            private readonly MessageScenario _scenario;
            private readonly bool _custom,
                _hold,
                _reset,
                _warm,
                _remove,
                _trim,
                _inside;
            private readonly Counts _counts = new Counts();
            private MessageBusRegistration _retained;
            private WeakReference _peer,
                _anchor,
                _cache;
            internal bool PeerAlive,
                AnchorAlive,
                HandleValid,
                FinalPeerAlive,
                FinalAnchorAlive,
                FinalHandleValid;
            internal int BeforeAnchorCalls,
                BeforePeerCalls,
                ResetCalls,
                BeforeRegistered,
                AfterStaleRegistered,
                FreshCalls,
                AfterFreshAnchorCalls,
                AfterFreshPeerCalls,
                AfterFreshRemovalRegistered;
            private int _observedGroups,
                _observedFlat;

            /// <summary>Creates the selected bus and records one retention case's configuration.</summary>
            /// <param name="scenario">Message route under test.</param>
            /// <param name="custom">Whether the case uses a custom global bus.</param>
            /// <param name="hold">Whether to retain the anchor handle before cleanup.</param>
            /// <param name="reset">Whether to reset the default bus during setup.</param>
            /// <param name="warm">Whether to emit once before changing registrations.</param>
            /// <param name="remove">Whether to remove the peer before observation.</param>
            /// <param name="trim">Whether to force-trim after setup.</param>
            /// <param name="inside">Whether to emit before observation and reset inside the callback when requested.</param>
            /// <param name="diagnostics">Whether to enable bus diagnostics.</param>
            internal State(
                MessageScenario scenario,
                bool custom,
                bool hold,
                bool reset,
                bool warm,
                bool remove,
                bool trim,
                bool inside,
                bool diagnostics
            )
            {
                _scenario = scenario;
                _custom = custom;
                _hold = hold;
                _reset = reset;
                _warm = warm;
                _remove = remove;
                _trim = trim;
                _inside = inside;
                Bus = custom
                    ? Bus.CreateForInternalUse(
                        new ConstantClock(),
                        idleEvictionTicks: 30,
                        idleEvictionEnabled: false,
                        trimApiEnabled: true
                    )
                    : (Bus)MessageHandler.InitialGlobalMessageBus;
                Bus.DiagnosticsMode = diagnostics;
                if (custom)
                {
                    MessageHandler.SetGlobalMessageBus(Bus);
                }
                Label =
                    $"[{scenario}] custom={custom},hold={hold},reset={reset},warm={warm},remove={remove},trim={trim},inside={inside},diagnostics={diagnostics}";
            }

            /// <summary>Describes the original recorded weak-target and callback observations.</summary>
            internal string Report =>
                Label
                + $"; peer={PeerAlive},anchor={AnchorAlive},groups={_observedGroups},flat={_observedFlat},registered={BeforeRegistered},callsBefore=[{BeforeAnchorCalls},{BeforePeerCalls}],fresh={FreshCalls},callsAfter=[{AfterFreshAnchorCalls},{AfterFreshPeerCalls}],finalPeer={FinalPeerAlive},finalAnchor={FinalAnchorAlive}";

            /// <summary>Creates both receivers and applies the requested setup changes.</summary>
            [MethodImpl(MethodImplOptions.NoInlining)]
            internal void Populate()
            {
                Receiver anchor = new Receiver(_counts, 0);
                Receiver peer = new Receiver(_counts, 1);
                MessageBusRegistration anchorHandle = Register(anchor, 0);
                MessageBusRegistration peerHandle = Register(peer, 10);
                _anchor = new WeakReference(anchor);
                _peer = new WeakReference(peer);
                _cache = new WeakReference(anchorHandle.capturedPrimary);
                _retained = _hold ? anchorHandle : default;
                if (_warm)
                {
                    Emit();
                }
                if (_remove)
                {
                    Bus.Deregister<ProbeMessage>(in peerHandle);
                }
                if (_inside)
                {
                    anchor.ResetOnNextCall = _reset;
                    Emit();
                }
                else if (_reset)
                {
                    ++_counts.Resets;
                    ResetDefault();
                }
                if (_trim)
                {
                    _ = Bus.Trim(force: true);
                }
            }

            /// <summary>Collects and records weak targets, registrations, and callback counts before cleanup.</summary>
            [MethodImpl(MethodImplOptions.NoInlining)]
            internal void Observe()
            {
                Collect();
                PeerAlive = IsAlive(_peer);
                AnchorAlive = IsAlive(_anchor);
                HandleValid = _retained.IsValid;
                BeforeAnchorCalls = _counts.Anchor;
                BeforePeerCalls = _counts.Peer;
                ResetCalls = _counts.Resets;
                BeforeRegistered = Bus.RegisteredInterceptors;
                ObserveOwner();
            }

            /// <summary>Records the captured interceptor cache's group and flat-array sizes.</summary>
            [MethodImpl(MethodImplOptions.NoInlining)]
            private void ObserveOwner()
            {
                object cache = _cache.Target;
                if (cache == null)
                {
                    _observedGroups = -1;
                    _observedFlat = -1;
                    return;
                }
                _observedGroups = (
                    (IDictionary)MessageBusInspection.ReadField(cache, "handlers")
                ).Count;
                _observedFlat = ((Array)MessageBusInspection.ReadField(cache, "_flat")).Length;
            }

            /// <summary>Records stale removal and fresh dispatch against the surviving registrations.</summary>
            [MethodImpl(MethodImplOptions.NoInlining)]
            internal void ExerciseFreshRegistration()
            {
                Receiver fresh = new Receiver(_counts, 2);
                MessageBusRegistration registration = Register(fresh, 20);
                try
                {
                    if (_reset && _hold)
                    {
                        Bus.Deregister<ProbeMessage>(in _retained);
                    }
                    AfterStaleRegistered = Bus.RegisteredInterceptors;
                    Emit();
                    FreshCalls = _counts.Fresh;
                    AfterFreshAnchorCalls = _counts.Anchor;
                    AfterFreshPeerCalls = _counts.Peer;
                }
                finally
                {
                    Bus.Deregister<ProbeMessage>(in registration);
                }
                AfterFreshRemovalRegistered = Bus.RegisteredInterceptors;
            }

            /// <summary>Releases case ownership, collects, and records the final weak targets.</summary>
            [MethodImpl(MethodImplOptions.NoInlining)]
            internal void Cleanup()
            {
                Release();
                Collect();
                FinalPeerAlive = IsAlive(_peer);
                FinalAnchorAlive = IsAlive(_anchor);
                FinalHandleValid = _retained.IsValid;
                GC.KeepAlive(Bus);
            }

            /// <summary>Drops the retained handle, resets the case bus, and force-trims storage.</summary>
            [MethodImpl(MethodImplOptions.NoInlining)]
            private void Release()
            {
                _retained = default;
                if (_custom)
                {
                    Bus.ResetState();
                }
                else
                {
                    ResetDefault();
                }
                _ = Bus.Trim(force: true);
            }

            /// <summary>Registers an interceptor for the selected message route.</summary>
            /// <param name="receiver">Callback target registered on this case's bus.</param>
            /// <param name="priority">Interceptor execution priority.</param>
            /// <returns>Raw registration handle for the selected interceptor.</returns>
            private MessageBusRegistration Register(Receiver receiver, int priority)
            {
                return _scenario.Kind switch
                {
                    MessageKind.Untargeted => Bus.RegisterUntargetedInterceptor<ProbeMessage>(
                        receiver.Scalar,
                        priority
                    ),
                    MessageKind.Targeted => Bus.RegisterTargetedInterceptor<ProbeMessage>(
                        receiver.Context,
                        priority
                    ),
                    MessageKind.Broadcast => Bus.RegisterBroadcastInterceptor<ProbeMessage>(
                        receiver.Context,
                        priority
                    ),
                    _ => throw new ArgumentOutOfRangeException(nameof(_scenario)),
                };
            }

            /// <summary>Emits one probe message through the selected route.</summary>
            [MethodImpl(MethodImplOptions.NoInlining)]
            private void Emit()
            {
                ProbeMessage message = default;
                InstanceId context = new InstanceId(924);
                switch (_scenario.Kind)
                {
                    case MessageKind.Untargeted:
                        Bus.UntargetedBroadcast(ref message);
                        break;
                    case MessageKind.Targeted:
                        Bus.TargetedBroadcast(ref context, ref message);
                        break;
                    case MessageKind.Broadcast:
                        Bus.SourcedBroadcast(ref context, ref message);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(_scenario));
                }
            }

            /// <summary>Describes final owners after the original weak-target observations fail.</summary>
            /// <returns>Cold registration, dispatch and cache counts without another collection.</returns>
            [MethodImpl(MethodImplOptions.NoInlining)]
            internal string BuildReleasedOwnershipReport()
            {
                int registered = Bus.RegisteredInterceptors;
                int live = MessageBusInspection.CountLiveRegistrations(Bus);
                int unique = (
                    (IDictionary)
                        MessageBusInspection.ReadField(Bus, "_uniqueInterceptorsAndPriorities")
                ).Count;
                int depth = (int)MessageBusInspection.ReadField(Bus, "_dispatchDepth");
                object cache = _cache.Target;
                int groups =
                    cache == null
                        ? -1
                        : ((IDictionary)MessageBusInspection.ReadField(cache, "handlers")).Count;
                int flat =
                    cache == null
                        ? -1
                        : ((Array)MessageBusInspection.ReadField(cache, "_flat")).Length;
                return $"Final ownership {Label}; registered={registered},live={live},unique={unique},depth={depth},cacheAlive={cache != null},groups={groups},flat={flat}";
            }
        }

        /// <summary>Stores callback totals without retaining receiver instances.</summary>
        private sealed class Counts
        {
            internal int Anchor,
                Peer,
                Fresh,
                Resets;
        }

        /// <summary>Records callbacks and optionally resets the default bus during dispatch.</summary>
        private sealed class Receiver
        {
            private readonly Counts _counts;
            private readonly int _role;
            internal bool ResetOnNextCall;

            /// <summary>Creates a callback target with a fixed counter role.</summary>
            /// <param name="counts">Shared callback totals owned by the case.</param>
            /// <param name="role">Receiver role used to select the counter.</param>
            internal Receiver(Counts counts, int role)
            {
                _counts = counts;
                _role = role;
            }

            /// <summary>Records an untargeted interceptor callback.</summary>
            /// <param name="_">Unused probe message.</param>
            /// <returns>True after recording the callback.</returns>
            internal bool Scalar(ref ProbeMessage _) => Invoke();

            /// <summary>Records a targeted or broadcast interceptor callback.</summary>
            /// <param name="_">Unused route context.</param>
            /// <param name="__">Unused probe message.</param>
            /// <returns>True after recording the callback.</returns>
            internal bool Context(ref InstanceId _, ref ProbeMessage __) => Invoke();

            /// <summary>Updates the role counter and performs a requested callback reset.</summary>
            /// <returns>True to allow interceptor processing to continue.</returns>
            private bool Invoke()
            {
                switch (_role)
                {
                    case 0:
                        ++_counts.Anchor;
                        break;
                    case 1:
                        ++_counts.Peer;
                        break;
                    default:
                        ++_counts.Fresh;
                        break;
                }
                if (ResetOnNextCall)
                {
                    ResetOnNextCall = false;
                    ++_counts.Resets;
                    ResetDefault();
                }
                return true;
            }
        }

        /// <summary>Resets default-bus state and keeps test logging disabled.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ResetDefault()
        {
            DxMessagingStaticState.Reset();
            MessagingDebug.enabled = false;
        }

        /// <summary>Returns whether the supplied weak reference currently has a target.</summary>
        /// <param name="reference">Receiver reference observed by the case.</param>
        /// <returns>True when a non-null weak reference is alive.</returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool IsAlive(WeakReference reference) =>
            reference != null && reference.IsAlive;

        /// <summary>Runs the fixture's original full-collection and finalizer sequence.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Collect()
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        }

        /// <summary>Provides the fixed clock used by custom-bus controls.</summary>
        private sealed class ConstantClock : IDxMessagingClock
        {
            /// <summary>Gets the fixed tick value.</summary>
            public long NowTicks => 0;

            /// <summary>Gets the fixed clock value in seconds.</summary>
            public double NowSeconds => 0;
        }

        /// <summary>Implements all three message interfaces for the route checks.</summary>
        private readonly struct ProbeMessage
            : IUntargetedMessage,
                ITargetedMessage,
                IBroadcastMessage
        {
            /// <summary>Gets the probe message's runtime type.</summary>
            public Type MessageType => typeof(ProbeMessage);
        }
    }
}
