namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Runtime;
    using NUnit.Framework;

    /// <summary>Checks historical post-route ownership while nested callbacks reset or mutate routes.</summary>
    /// <remarks>
    /// 2026-10-20: Parent compaction may retire an old-generation snapshot while its
    /// callback still holds the entry array. These cases pin the generation check,
    /// same-generation parent retention, empty post-reset captures and cleanup.
    /// </remarks>
    [TestFixture]
    public sealed class HistoricalPostRouteOwnershipTests
    {
        private static IEnumerable<TestCaseData> Cases()
        {
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            {
                if (scenario.Kind != MessageKind.Targeted && scenario.Kind != MessageKind.Broadcast)
                {
                    continue;
                }
                foreach (int reset in new[] { 0, 1, 2 })
                {
                    foreach (bool fast in new[] { false, true })
                    {
                        foreach (bool throwing in new[] { false, true })
                        {
                            foreach (int count in new[] { 2, 8 })
                            {
                                yield return new TestCaseData(
                                    scenario,
                                    reset,
                                    fast,
                                    throwing,
                                    count
                                ).SetName(
                                    $"HistoricalPostRouteOwnership_{scenario.Kind}_Reset{reset}_Fast{fast}_Throw{throwing}_Count{count}"
                                );
                            }
                        }
                    }
                }
            }
        }

        [TestCaseSource(nameof(Cases))]
        public void HistoricalPostCallbacksRetainOnlyTheirValidSnapshotOwners(
            MessageScenario scenario,
            int reset,
            bool fast,
            bool throwing,
            int count
        )
        {
            string label =
                scenario
                + ",reset="
                + reset
                + ",fast="
                + fast
                + ",throwing="
                + throwing
                + ",count="
                + count;
            using CaseState state = new CaseState(label);
            MessageBus bus = state.Bus;
            MessageKind kind = scenario.Kind;
            InstanceId origin = new InstanceId(2001);
            InstanceId destination = new InstanceId(2002);
            MessageHandler handler = state.Handler;
            MessageRegistrationToken token = state.Token;
            MessageRegistrationToken fresh = null;
            List<string> calls = new List<string>();
            List<int> childHistoryCounts = new List<int>();
            string phase = "outer";
            bool armed = true;
            bool childAdded = false;
            MessageRegistrationHandle last = default;
            ProbeFailure sentinel = new ProbeFailure();
            Exception actualError = null;
            object oldSnapshot = null;
            object oldFlat = null;
            Array oldEntries = null;
            int oldEntryCount = -1;
            int beforeChildHistory = -1;
            int afterChildHistory = -1;
            bool activeTrimBeforePreserved = false;
            bool activeTrimAfterPreserved = false;
            bool parentOwnershipCorrect = false;
            bool parentArrayStateCorrect = false;
            bool retainedSnapshotStateCorrect = false;
            bool retainedIsEmpty = false;
            int retainedEntryCount = -1;
            bool retainedHasRegistrations = false;

            void Record(string value)
            {
                calls.Add(phase + ":" + value);
                if (phase == "child")
                {
                    childHistoryCounts.Add(History(bus)?.Count ?? 0);
                }
            }

            void RegisterChildInterceptor(MessageRegistrationToken owner)
            {
                RegisterInterceptor(
                    owner,
                    kind,
                    (ref InstanceId route, ref ProbeMessage message) =>
                    {
                        route = destination;
                        if (phase == "child" && !childAdded)
                        {
                            childAdded = true;
                            _ = RegisterPost(owner, kind, fast, destination, () => Record("D"));
                        }
                        return true;
                    }
                );
            }

            _ = RegisterPost(
                token,
                kind,
                fast,
                destination,
                () =>
                {
                    Record("A");
                    if (!armed || phase != "outer")
                    {
                        return;
                    }

                    armed = false;
                    IList history = History(bus);
                    beforeChildHistory = history?.Count ?? 0;
                    oldSnapshot = Field(history[0], "snapshot");
                    oldFlat = Field(oldSnapshot, "flat");
                    oldEntries = (Array)Field(oldFlat, "entries");
                    oldEntryCount = (int)Field(oldFlat, "count");
                    _ = bus.Trim(force: true);
                    activeTrimBeforePreserved =
                        History(bus).Count == beforeChildHistory
                        && ReferenceEquals(oldFlat, Field(oldSnapshot, "flat"))
                        && ReferencesPresent(oldEntries, oldEntryCount);

                    if (reset == 1)
                    {
                        bus.BumpResetGeneration();
                    }
                    else if (reset == 2)
                    {
                        bus.ResetState();
                        fresh = MessageRegistrationToken.Create(handler, bus);
                        state.Fresh = fresh;
                        fresh.Enable();
                        for (int index = 0; index < count; ++index)
                        {
                            int capturedIndex = index;
                            _ = RegisterPost(
                                fresh,
                                kind,
                                fast,
                                destination,
                                () => Record("X" + capturedIndex)
                            );
                        }
                        RegisterWithout(fresh, kind, fast, () => Record("fresh-without"));
                        RegisterChildInterceptor(fresh);
                    }

                    phase = "child";
                    try
                    {
                        Emit(bus, kind, origin);
                    }
                    finally
                    {
                        phase = "outer";
                    }

                    afterChildHistory = History(bus)?.Count ?? 0;
                    parentOwnershipCorrect =
                        reset == 0
                            ? ReferenceEquals(oldFlat, Field(oldSnapshot, "flat"))
                            : Field(oldSnapshot, "flat") == null;
                    parentArrayStateCorrect =
                        reset == 0
                            ? ReferencesPresent(oldEntries, oldEntryCount)
                            : ReferencesCleared(oldEntries, oldEntryCount);
                    object retainedSnapshot =
                        afterChildHistory == 1 ? Field(History(bus)[0], "snapshot") : null;
                    object retainedFlat =
                        retainedSnapshot == null ? null : Field(retainedSnapshot, "flat");
                    if (retainedSnapshot != null)
                    {
                        object empty = typeof(MessageBus)
                            .GetNestedType("DispatchSnapshot", BindingFlags.NonPublic)
                            .GetField("Empty", BindingFlags.Static | BindingFlags.Public)
                            .GetValue(null);
                        retainedIsEmpty = ReferenceEquals(empty, retainedSnapshot);
                        retainedEntryCount = (int)Field(retainedSnapshot, "entryCount");
                        retainedHasRegistrations = (bool)Field(
                            retainedSnapshot,
                            "hasRegistrations"
                        );
                        retainedSnapshotStateCorrect =
                            reset == 2
                                ? retainedIsEmpty
                                    && retainedFlat == null
                                    && retainedEntryCount == 0
                                    && !retainedHasRegistrations
                                : !retainedIsEmpty
                                    && retainedFlat != null
                                    && retainedEntryCount == count
                                    && retainedHasRegistrations;
                    }
                    _ = bus.Trim(force: true);
                    activeTrimAfterPreserved =
                        afterChildHistory == 1
                        && retainedSnapshot != null
                        && History(bus).Count == 1
                        && ReferenceEquals(retainedSnapshot, Field(History(bus)[0], "snapshot"))
                        && ReferenceEquals(retainedFlat, Field(retainedSnapshot, "flat"));
                    if (throwing)
                    {
                        throw sentinel;
                    }
                }
            );
            for (int index = 1; index < count; ++index)
            {
                int capturedIndex = index;
                last = RegisterPost(
                    token,
                    kind,
                    fast,
                    destination,
                    () => Record("tail" + capturedIndex)
                );
            }
            RegisterWithout(token, kind, fast, () => Record("W"));
            RegisterInterceptor(
                token,
                kind,
                (ref InstanceId route, ref ProbeMessage message) =>
                {
                    route = destination;
                    if (phase == "outer" && armed)
                    {
                        token.RemoveRegistration(last);
                        _ = RegisterPost(token, kind, fast, destination, () => Record("C"));
                    }
                    else if (phase == "child" && !childAdded)
                    {
                        childAdded = true;
                        _ = RegisterPost(token, kind, fast, destination, () => Record("D"));
                    }
                    return true;
                }
            );

            List<string> expectedFirst = new List<string> { "outer:A" };
            if (reset == 2)
            {
                for (int index = 0; index < count; ++index)
                {
                    expectedFirst.Add("child:X" + index);
                }
                expectedFirst.Add("child:fresh-without");
            }
            else
            {
                expectedFirst.Add("child:A");
                for (int index = 1; index < count - 1; ++index)
                {
                    expectedFirst.Add("child:tail" + index);
                }
                expectedFirst.Add("child:C");
                expectedFirst.Add("child:W");
            }
            if (reset == 0 && !throwing)
            {
                for (int index = 1; index < count; ++index)
                {
                    expectedFirst.Add("outer:tail" + index);
                }
                expectedFirst.Add("outer:W");
            }

            try
            {
                Emit(bus, kind, origin);
            }
            catch (Exception exception)
            {
                actualError = exception;
            }
            string[] first = calls.ToArray();
            int afterDrainHistory = History(bus)?.Count ?? 0;
            int afterDrainDepth = (int)Field(bus, "_dispatchDepth");
            bool afterDrainDeferred = (bool)Field(bus, "_hasDeferredResetTeardown");
            long afterDrainLease = (long)Field(bus, "_activeDispatchLeaseId");
            long afterDrainScopedEmission = (long)Field(bus, "_scopedEmissionId");
            bool afterDrainOldOwnerReleased =
                oldSnapshot != null && Field(oldSnapshot, "flat") == null;
            bool afterDrainOldEntriesCleared =
                oldEntries != null && ReferencesCleared(oldEntries, oldEntryCount);
            calls.Clear();
            phase = "next";
            Emit(bus, kind, origin);
            string[] second = calls.ToArray();
            List<string> expectedSecond = new List<string>();
            if (reset == 2)
            {
                for (int index = 0; index < count; ++index)
                {
                    expectedSecond.Add("next:X" + index);
                }
                expectedSecond.Add("next:D");
                expectedSecond.Add("next:fresh-without");
            }
            else
            {
                expectedSecond.Add("next:A");
                for (int index = 1; index < count - 1; ++index)
                {
                    expectedSecond.Add("next:tail" + index);
                }
                expectedSecond.Add("next:C");
                expectedSecond.Add("next:D");
                expectedSecond.Add("next:W");
            }
            int beforeCleanupLive = MessageBusInspection.CountLiveRegistrations(bus);
            token.Dispose();
            fresh?.Dispose();
            int afterTokenCleanupLive = MessageBusInspection.CountLiveRegistrations(bus);
            calls.Clear();
            phase = "after-cleanup";
            Emit(bus, kind, origin);
            string[] afterCleanupCalls = calls.ToArray();
            List<string> expectedAfterCleanupCalls = new List<string>();
            if (reset == 1)
            {
                expectedAfterCleanupCalls.Add("after-cleanup:A");
                for (int index = 1; index < count - 1; ++index)
                {
                    expectedAfterCleanupCalls.Add("after-cleanup:tail" + index);
                }
                expectedAfterCleanupCalls.Add("after-cleanup:C");
                expectedAfterCleanupCalls.Add("after-cleanup:W");
            }
            int afterCleanupEmitLive = MessageBusInspection.CountLiveRegistrations(bus);
            bus.ResetState();
            state.CheckLeaksBeforeMaintenance();
            _ = bus.Trim(force: true);
            int afterCleanup =
                bus.RegisteredUntargeted
                + bus.RegisteredTargeted
                + bus.RegisteredBroadcast
                + bus.RegisteredInterceptors
                + bus.RegisteredPostProcessors
                + bus.RegisteredGlobalAcceptAll;
            Assert.That(first, Is.EqualTo(expectedFirst), label + ": first callback vector");
            Assert.That(second, Is.EqualTo(expectedSecond), label + ": next callback vector");
            if (throwing)
            {
                Assert.That(actualError, Is.SameAs(sentinel), label + ": exact sentinel exception");
            }
            else
            {
                Assert.That(actualError, Is.Null, label + ": normal exit");
            }
            Assert.That(beforeChildHistory, Is.EqualTo(1), label + ": parent history before child");
            Assert.That(afterChildHistory, Is.EqualTo(1), label + ": compacted parent history");
            Assert.That(
                oldEntryCount,
                Is.EqualTo(count),
                label + ": original populated entry count"
            );
            Assert.That(childHistoryCounts, Is.Not.Empty, label + ": child callback observations");
            Assert.That(
                childHistoryCounts,
                Is.All.EqualTo(reset == 2 ? 3 : 2),
                label + ": exact child capture count"
            );
            Assert.That(
                activeTrimBeforePreserved,
                Is.True,
                label + ": active trim preserves original parent"
            );
            Assert.That(
                activeTrimAfterPreserved,
                Is.True,
                label + ": active trim preserves retained parent"
            );
            Assert.That(
                retainedSnapshotStateCorrect,
                Is.True,
                label + ": exact retained empty/populated snapshot state"
            );
            Assert.That(
                parentOwnershipCorrect,
                Is.True,
                label + ": parent lifetime at child return"
            );
            Assert.That(
                parentArrayStateCorrect,
                Is.True,
                label + ": parent array lifetime at child return"
            );
            Assert.That(
                afterDrainOldEntriesCleared,
                Is.True,
                label + ": old populated array scrubbed at final exit"
            );
            Assert.That(afterDrainHistory, Is.Zero, label + ": no history after final exit");
            Assert.That(afterDrainDepth, Is.Zero, label + ": dispatch depth after final exit");
            Assert.That(afterDrainDeferred, Is.False, label + ": deferred flag after final exit");
            Assert.That(
                afterDrainLease,
                Is.Zero,
                label + ": no active lease identity after final exit"
            );
            Assert.That(
                afterDrainScopedEmission,
                Is.Zero,
                label + ": idle scoped emission identity after final exit"
            );
            Assert.That(
                afterDrainOldOwnerReleased,
                Is.True,
                label + ": old snapshot owner released at final exit"
            );
            Assert.That(
                beforeCleanupLive,
                Is.EqualTo(count + 3),
                label + ": live registrations before cleanup"
            );
            Assert.That(
                afterTokenCleanupLive,
                Is.EqualTo(reset == 1 ? count + 2 : 0),
                label + ": valid or generation-invalidated cleanup"
            );
            Assert.That(
                afterCleanupCalls,
                Is.EqualTo(expectedAfterCleanupCalls),
                label + ": mixed-generation callback identity after cleanup"
            );
            Assert.That(
                afterCleanupEmitLive,
                Is.EqualTo(afterTokenCleanupLive),
                label + ": cleanup emission preserves registration count"
            );
            Assert.That(afterCleanup, Is.Zero, label + ": counters after explicit owned cleanup");
        }

        private static object Field(object value, string name) =>
            value
                .GetType()
                .GetField(
                    name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                )
                .GetValue(value);

        private static IList History(MessageBus bus) =>
            (IList)Field(bus, "_postRouteSnapshotHistory");

        private static bool ReferencesPresent(Array values, int count) =>
            0 < count
            && Enumerable
                .Range(0, count)
                .All(index =>
                    Field(values.GetValue(index), "handler") != null
                    && Field(values.GetValue(index), "invoker") != null
                );

        private static bool ReferencesCleared(Array values, int count) =>
            Enumerable
                .Range(0, count)
                .All(index =>
                    Field(values.GetValue(index), "handler") == null
                    && Field(values.GetValue(index), "invoker") == null
                );

        private static MessageRegistrationHandle RegisterPost(
            MessageRegistrationToken owner,
            MessageKind kind,
            bool fast,
            InstanceId context,
            Action callback
        ) =>
            kind == MessageKind.Targeted
                ? fast
                    ? owner.RegisterTargetedPostProcessor<ProbeMessage>(
                        context,
                        (in ProbeMessage _) => callback()
                    )
                    : owner.RegisterTargetedPostProcessor<ProbeMessage>(context, _ => callback())
                : fast
                    ? owner.RegisterBroadcastPostProcessor<ProbeMessage>(
                        context,
                        (in ProbeMessage _) => callback()
                    )
                    : owner.RegisterBroadcastPostProcessor<ProbeMessage>(context, _ => callback());

        private static void RegisterWithout(
            MessageRegistrationToken owner,
            MessageKind kind,
            bool fast,
            Action callback
        )
        {
            if (kind == MessageKind.Targeted)
            {
                _ = fast
                    ? owner.RegisterTargetedWithoutTargetingPostProcessor<ProbeMessage>(
                        (in InstanceId _, in ProbeMessage __) => callback()
                    )
                    : owner.RegisterTargetedWithoutTargetingPostProcessor<ProbeMessage>(
                        (_, __) => callback()
                    );
            }
            else
            {
                _ = fast
                    ? owner.RegisterBroadcastWithoutSourcePostProcessor<ProbeMessage>(
                        (in InstanceId _, in ProbeMessage __) => callback()
                    )
                    : owner.RegisterBroadcastWithoutSourcePostProcessor<ProbeMessage>(
                        (_, __) => callback()
                    );
            }
        }

        private static void RegisterInterceptor(
            MessageRegistrationToken owner,
            MessageKind kind,
            Interceptor callback
        )
        {
            if (kind == MessageKind.Targeted)
            {
                _ = owner.RegisterTargetedInterceptor<ProbeMessage>(
                    (ref InstanceId route, ref ProbeMessage message) =>
                        callback(ref route, ref message)
                );
            }
            else
            {
                _ = owner.RegisterBroadcastInterceptor<ProbeMessage>(
                    (ref InstanceId route, ref ProbeMessage message) =>
                        callback(ref route, ref message)
                );
            }
        }

        private static void Emit(MessageBus bus, MessageKind kind, InstanceId route)
        {
            ProbeMessage message = new ProbeMessage();
            if (kind == MessageKind.Targeted)
            {
                bus.TargetedBroadcast(ref route, ref message);
            }
            else
            {
                bus.SourcedBroadcast(ref route, ref message);
            }
        }

        private delegate bool Interceptor(ref InstanceId route, ref ProbeMessage message);

        private sealed class ProbeFailure : Exception { }

        private readonly struct ProbeMessage : ITargetedMessage, IBroadcastMessage
        {
            public Type MessageType => typeof(ProbeMessage);
        }

        private sealed class CaseState : IDisposable
        {
            private readonly bool _previousDebug;
            private readonly LeakWatcher _watcher;

            internal CaseState(string label)
            {
                _previousDebug = MessagingDebug.enabled;
                MessagingDebug.enabled = false;
                Bus = new MessageBus { DiagnosticsMode = false };
                _watcher = new LeakWatcher(Bus, label: label);
                Handler = new MessageHandler(new InstanceId(8001), Bus) { active = true };
                Token = MessageRegistrationToken.Create(Handler, Bus);
                Token.Enable();
            }

            internal MessageBus Bus { get; }
            internal MessageHandler Handler { get; }
            internal MessageRegistrationToken Token { get; }
            internal MessageRegistrationToken Fresh { get; set; }

            internal void CheckLeaksBeforeMaintenance() => _watcher.Dispose();

            public void Dispose()
            {
                try
                {
                    Token.Dispose();
                    Fresh?.Dispose();
                    Bus.ResetState();
                    _watcher.Dispose();
                    _ = Bus.Trim(force: true);
                }
                finally
                {
                    MessagingDebug.enabled = _previousDebug;
                }
            }
        }
    }
}
