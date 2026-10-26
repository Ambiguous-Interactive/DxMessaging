namespace DxMessaging.Tests.Runtime.MemoryReclaim
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using DxMessaging.Core;
    using DxMessaging.Core.Helper;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Core.Pooling;
    using DxMessaging.Tests.Runtime;
    using NUnit.Framework;

    /// <summary>Checks that completed global snapshots release captures while their handler stays alive.</summary>
    /// <remarks>
    /// 2026-10-26: Unity bus construction reapplies runtime pool settings. Set the explicit test cap
    /// afterward so each capacity assertion exercises its declared cap, and restore it during cleanup.
    /// </remarks>
    [TestFixture]
    [Category("MemoryReclaim")]
    public sealed class GlobalNestedSnapshotReclamationTests
    {
        private static IEnumerable<TestCaseData> Cases()
        {
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            {
                foreach (int shape in new[] { 0, 1, 2 })
                {
                    foreach (int count in new[] { 3, 8 })
                    {
                        foreach (bool throwing in new[] { false, true })
                        {
                            foreach (int cap in new[] { 0, 1, 4 })
                            {
                                yield return new TestCaseData(
                                    scenario,
                                    shape,
                                    count,
                                    throwing,
                                    cap
                                ).SetName(
                                    "GlobalSnapshotReclaim_"
                                        + scenario
                                        + "_shape"
                                        + shape
                                        + "_count"
                                        + count
                                        + "_throw"
                                        + throwing
                                        + "_cap"
                                        + cap
                                );
                            }
                        }
                    }
                }
            }
        }

        [TestCaseSource(nameof(Cases))]
        public void CompletedSnapshotsReleaseRemovedCapturesBeforeHandlerCleanup(
            MessageScenario scenario,
            int shape,
            int count,
            bool throwing,
            int cap
        )
        {
            string label =
                scenario
                + ",shape="
                + shape
                + ",count="
                + count
                + ",throw="
                + throwing
                + ",cap="
                + cap;
            int previousCap = DxPools.IntSets.MaxRetained;
            bool previousDebug = MessagingDebug.enabled;
            MessageBus bus = null;
            MessageRegistrationToken token = null;
            LeakWatcher watcher = null;
            try
            {
                MessagingDebug.enabled = false;
                bus = new MessageBus { DiagnosticsMode = false };
                MessageHandler handler = new MessageHandler(new InstanceId(1652), bus)
                {
                    active = true,
                };
                DxPools.IntSets.MaxRetained = cap;
                Assert.That(
                    DxPools.IntSets.MaxRetained,
                    Is.EqualTo(cap),
                    label + ": explicit pool cap after bus initialization"
                );
                watcher = LeakWatcher.WatchWithSlots(bus, label: label, handler: handler);
                token = MessageRegistrationToken.Create(handler, bus);
                token.Enable();
                List<string> calls = new List<string>();
                ReceiverProbe receivers = CreateDisplacedSnapshot(
                    scenario,
                    shape,
                    count,
                    throwing,
                    bus,
                    token,
                    calls,
                    label
                );
                Collect();
                Assert.That(
                    receivers.Removed.IsAlive,
                    Is.False,
                    label + ": removed receiver before trim"
                );
                Assert.That(
                    receivers.Live.IsAlive,
                    Is.True,
                    label + ": registered positive control"
                );
                Assert.That(
                    handler.HasTypedHandlersForBus(bus),
                    Is.True,
                    label + ": strong live handler"
                );
                CheckTrackingStorage(handler, bus, cap, label);
                token.RemoveRegistration(receivers.RemovedHandle);
                calls.Clear();
                Emit(scenario.Kind, bus);
                Assert.That(calls, Does.Contain("live"), label + ": live peer after stale removal");
                Assert.That(
                    calls,
                    Does.Contain("replacement"),
                    label + ": stale removal preserves its replacement"
                );
                Assert.That(
                    calls,
                    Does.Not.Contain("removed"),
                    label + ": removed peer stays absent"
                );
                token.Dispose();
                int emptySlots = handler.CountEmptyTypedSlotsForSweep(bus);
                Assert.That(
                    0 < emptySlots,
                    Is.True,
                    label + ": empty typed slots before trim=" + emptySlots
                );
                bus.Trim(force: true);
                watcher.Dispose();
                Assert.That(
                    handler.CountEmptyTypedSlotsForSweep(bus),
                    Is.EqualTo(0),
                    label + ": reclaimed slots"
                );
                Assert.That(
                    handler.HasTypedHandlersForBus(bus),
                    Is.False,
                    label + ": outer typed cache removed"
                );
                IMessageBus.TrimResult again = bus.Trim(force: true);
                Assert.That(again.TypeSlotsEvicted, Is.EqualTo(0), label + ": repeated type trim");
                Assert.That(
                    again.TargetSlotsEvicted,
                    Is.EqualTo(0),
                    label + ": repeated target trim"
                );
                Collect();
                Assert.That(
                    receivers.Removed.IsAlive,
                    Is.False,
                    label + ": removed receiver after trim"
                );
                Assert.That(receivers.Live.IsAlive, Is.False, label + ": cleaned positive control");
                GC.KeepAlive(handler);
                GC.KeepAlive(bus);
            }
            finally
            {
                try
                {
                    token?.Dispose();
                }
                finally
                {
                    try
                    {
                        bus?.Trim(force: true);
                    }
                    finally
                    {
                        try
                        {
                            watcher?.Dispose();
                        }
                        finally
                        {
                            DxPools.IntSets.MaxRetained = previousCap;
                            MessagingDebug.enabled = previousDebug;
                        }
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ReceiverProbe CreateDisplacedSnapshot(
            MessageScenario scenario,
            int shape,
            int count,
            bool throwing,
            MessageBus bus,
            MessageRegistrationToken token,
            List<string> calls,
            string label
        )
        {
            bool started = false;
            MessageRegistrationHandle removed = default;
            InvalidOperationException failure = new InvalidOperationException(label);
            Register(
                token,
                shape != 0,
                () =>
                {
                    calls.Add("A");
                    if (started)
                    {
                        return;
                    }
                    started = true;
                    token.RemoveRegistration(removed);
                    Register(token, shape == 1, () => calls.Add("replacement"));
                    Emit(scenario.Kind, bus);
                    if (throwing)
                    {
                        throw failure;
                    }
                }
            );
            CapturedReceiver live = new CapturedReceiver(calls, "live");
            Register(token, shape == 1, live.Invoke);
            for (int index = 0; index < count - 3; ++index)
            {
                int captured = index;
                Register(token, shape == 1, () => calls.Add("filler" + captured));
            }
            CapturedReceiver receiver = new CapturedReceiver(calls, "removed");
            removed = Register(token, shape == 1, receiver.Invoke);
            Exception observed = null;
            try
            {
                Emit(scenario.Kind, bus);
            }
            catch (Exception exception)
            {
                observed = exception;
            }
            Assert.That(
                observed,
                Is.SameAs(throwing ? failure : null),
                label + ": primary exception"
            );
            return new ReceiverProbe(new WeakReference(receiver), new WeakReference(live), removed);
        }

        private static void CheckTrackingStorage(
            MessageHandler handler,
            MessageBus bus,
            int cap,
            string label
        )
        {
            Assert.That(
                handler._handlersByTypeByMessageBus.TryGetValue(
                    bus.RegisteredGlobalSequentialIndex,
                    out MessageCache<object> byType
                ),
                Is.True,
                label + ": actual handler cache exists"
            );
            int observedCaches = 0;
            foreach (object typed in byType)
            {
                foreach (
                    object slot in (Array)MessageBusInspection.ReadField(typed, "_globalSlots")
                )
                {
                    if (slot == null)
                    {
                        continue;
                    }
                    object cache = MessageBusInspection.ReadField(slot, "cache");
                    if (cache == null)
                    {
                        continue;
                    }
                    ++observedCaches;
                    foreach (
                        FieldInfo field in cache
                            .GetType()
                            .GetFields(
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                            )
                    )
                    {
                        Type type = field.FieldType;
                        if (
                            !type.IsGenericType
                            || type.GetGenericTypeDefinition() != typeof(List<>)
                        )
                        {
                            continue;
                        }
                        Type element = type.GetGenericArguments()[0];
                        if (
                            !element.IsGenericType
                            || element.GetGenericTypeDefinition() != typeof(List<>)
                        )
                        {
                            continue;
                        }
                        IList tracking = (IList)field.GetValue(cache);
                        if (tracking == null)
                        {
                            continue;
                        }
                        Assert.That(
                            tracking.Count,
                            Is.EqualTo(0),
                            label + ": retired references cleared"
                        );
                        int capacity = (int)MessageBusInspection.ReadProperty(tracking, "Capacity");
                        Assert.That(
                            capacity <= cap,
                            Is.True,
                            label + ": tracking capacity=" + capacity
                        );
                    }
                }
            }
            Assert.That(
                3 <= observedCaches,
                Is.True,
                label + ": actual global caches=" + observedCaches
            );
        }

        private static void Collect()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        private static MessageRegistrationHandle Register(
            MessageRegistrationToken token,
            bool fast,
            Action callback
        )
        {
            if (fast)
            {
                return token.RegisterGlobalAcceptAll(
                    (in IUntargetedMessage _) => callback(),
                    (in InstanceId _, in ITargetedMessage __) => callback(),
                    (in InstanceId _, in IBroadcastMessage __) => callback()
                );
            }
            return token.RegisterGlobalAcceptAll(
                _ => callback(),
                (_, _) => callback(),
                (_, _) => callback()
            );
        }

        private static void Emit(MessageKind kind, MessageBus bus)
        {
            ProbeMessage message = new ProbeMessage();
            InstanceId context = new InstanceId(2652);
            switch (kind)
            {
                case MessageKind.Untargeted:
                    bus.UntargetedBroadcast(ref message);
                    break;
                case MessageKind.Targeted:
                    bus.TargetedBroadcast(ref context, ref message);
                    break;
                case MessageKind.Broadcast:
                    bus.SourcedBroadcast(ref context, ref message);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        private readonly struct ReceiverProbe
        {
            internal ReceiverProbe(
                WeakReference removed,
                WeakReference live,
                MessageRegistrationHandle removedHandle
            )
            {
                Removed = removed;
                Live = live;
                RemovedHandle = removedHandle;
            }

            internal WeakReference Removed { get; }
            internal WeakReference Live { get; }
            internal MessageRegistrationHandle RemovedHandle { get; }
        }

        private sealed class CapturedReceiver
        {
            private readonly List<string> _calls;
            private readonly string _name;

            internal CapturedReceiver(List<string> calls, string name)
            {
                _calls = calls;
                _name = name;
            }

            internal void Invoke() => _calls.Add(_name);
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
