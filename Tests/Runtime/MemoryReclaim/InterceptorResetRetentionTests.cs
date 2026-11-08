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

        private static void AssertState(Action assertions)
        {
#if UNITY_2021_3_OR_NEWER
            assertions();
#else
            Assert.Multiple(assertions.Invoke);
#endif
        }

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

            internal string Report =>
                Label
                + $"; peer={PeerAlive},anchor={AnchorAlive},groups={_observedGroups},flat={_observedFlat},registered={BeforeRegistered},callsBefore=[{BeforeAnchorCalls},{BeforePeerCalls}],fresh={FreshCalls},callsAfter=[{AfterFreshAnchorCalls},{AfterFreshPeerCalls}],finalPeer={FinalPeerAlive},finalAnchor={FinalAnchorAlive}";

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
        }

        private sealed class Counts
        {
            internal int Anchor,
                Peer,
                Fresh,
                Resets;
        }

        private sealed class Receiver
        {
            private readonly Counts _counts;
            private readonly int _role;
            internal bool ResetOnNextCall;

            internal Receiver(Counts counts, int role)
            {
                _counts = counts;
                _role = role;
            }

            internal bool Scalar(ref ProbeMessage _) => Invoke();

            internal bool Context(ref InstanceId _, ref ProbeMessage __) => Invoke();

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

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ResetDefault()
        {
            DxMessagingStaticState.Reset();
            MessagingDebug.enabled = false;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool IsAlive(WeakReference reference) =>
            reference != null && reference.IsAlive;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Collect()
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        }

        private sealed class ConstantClock : IDxMessagingClock
        {
            public long NowTicks => 0;
            public double NowSeconds => 0;
        }

        private readonly struct ProbeMessage
            : IUntargetedMessage,
                ITargetedMessage,
                IBroadcastMessage
        {
            public Type MessageType => typeof(ProbeMessage);
        }
    }
}
