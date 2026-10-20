namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Runtime;
    using NUnit.Framework;

    /// <summary>Stops reset emissions before entering a later without-context handle phase.</summary>
    /// <remarks>
    /// 2026-10-20: A keyed handler's reset stopped its own peer loop but could
    /// resume without-context handling on fast and featured dispatch lanes.
    /// No-reset controls retain targeted lazy and broadcast prefrozen semantics.
    /// </remarks>
    [TestFixture]
    public sealed class TypedWithoutContextResetBoundaryTests
    {
        private static IEnumerable<TestCaseData> Cases()
        {
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            {
                if (scenario.Kind != MessageKind.Targeted && scenario.Kind != MessageKind.Broadcast)
                {
                    continue;
                }
                foreach (bool featured in new[] { false, true })
                {
                    foreach (int reset in new[] { 0, 1, 2 })
                    {
                        foreach (bool initial in new[] { false, true })
                        {
                            foreach (bool replacement in new[] { false, true })
                            {
                                foreach (bool fast in new[] { false, true })
                                {
                                    foreach (bool throwing in new[] { false, true })
                                    {
                                        foreach (bool warm in new[] { false, true })
                                        {
                                            yield return Case(
                                                scenario,
                                                featured,
                                                reset,
                                                initial,
                                                replacement,
                                                fast,
                                                throwing,
                                                warm,
                                                false
                                            );
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                foreach (bool featured in new[] { false, true })
                {
                    foreach (int reset in new[] { 1, 2 })
                    {
                        foreach (bool initial in new[] { false, true })
                        {
                            foreach (bool fast in new[] { false, true })
                            {
                                foreach (bool throwing in new[] { false, true })
                                {
                                    yield return Case(
                                        scenario,
                                        featured,
                                        reset,
                                        initial,
                                        true,
                                        fast,
                                        throwing,
                                        true,
                                        true
                                    );
                                }
                            }
                        }
                    }
                }
            }
        }

        private static TestCaseData Case(
            MessageScenario scenario,
            bool featured,
            int reset,
            bool initial,
            bool replacement,
            bool fast,
            bool throwing,
            bool warm,
            bool nested
        ) =>
            new TestCaseData(
                scenario,
                featured,
                reset,
                initial,
                replacement,
                fast,
                throwing,
                warm,
                nested
            ).SetName(
                $"TypedWithoutContextReset_{scenario.Kind}_Featured{featured}_Reset{reset}_Initial{initial}_Replace{replacement}_Fast{fast}_Throw{throwing}_Warm{warm}_Nested{nested}"
            );

        [TestCaseSource(nameof(Cases))]
        public void ResetStopsLaterHandlePhasesWithoutChangingValidMutationDelivery(
            MessageScenario scenario,
            bool featured,
            int reset,
            bool initial,
            bool replacement,
            bool fast,
            bool throwing,
            bool warm,
            bool nested
        )
        {
            string label =
                scenario
                + ",featured="
                + featured
                + ",reset="
                + reset
                + ",initial="
                + initial
                + ",replacement="
                + replacement
                + ",fast="
                + fast
                + ",throwing="
                + throwing
                + ",warm="
                + warm
                + ",nested="
                + nested;
            using CaseState state = new CaseState(label);
            MessageBus bus = state.Bus;
            MessageKind kind = scenario.Kind;
            InstanceId context = new InstanceId(8202);
            MessageRegistrationToken original = state.Token;
            MessageRegistrationToken fresh = null;
            List<string> calls = new List<string>();
            string phase = "warmup";
            bool armed = false;
            ProbeFailure sentinel = new ProbeFailure();
            Exception actualError = null;

            RegisterPrimary(
                original,
                kind,
                fast,
                context,
                () =>
                {
                    calls.Add(phase + ":A");
                    if (!armed)
                    {
                        return;
                    }
                    armed = false;
                    if (reset == 1)
                    {
                        bus.BumpResetGeneration();
                    }
                    else if (reset == 2)
                    {
                        bus.ResetState();
                    }
                    if (replacement)
                    {
                        fresh = MessageRegistrationToken.Create(state.Handler, bus);
                        state.Fresh = fresh;
                        fresh.Enable();
                        if (reset == 2)
                        {
                            RegisterPrimary(
                                fresh,
                                kind,
                                fast,
                                context,
                                () => calls.Add(phase + ":fresh-primary")
                            );
                        }
                        RegisterWithout(
                            fresh,
                            kind,
                            fast,
                            () => calls.Add(phase + ":replacement-variant")
                        );
                    }
                    if (nested)
                    {
                        phase = "nested";
                        try
                        {
                            Emit(bus, kind, context);
                        }
                        finally
                        {
                            phase = "first";
                        }
                    }
                    if (throwing)
                    {
                        throw sentinel;
                    }
                }
            );
            RegisterPrimary(original, kind, fast, context, () => calls.Add(phase + ":B"));
            if (initial)
            {
                RegisterWithout(original, kind, fast, () => calls.Add(phase + ":old-variant"));
            }
            if (featured)
            {
                RegisterInterceptor(original, kind);
            }

            List<string> expectedWarm = new List<string>();
            if (warm)
            {
                expectedWarm.Add("warmup:A");
                expectedWarm.Add("warmup:B");
                if (initial)
                {
                    expectedWarm.Add("warmup:old-variant");
                }
                Emit(bus, kind, context);
            }
            string[] actualWarm = calls.ToArray();
            calls.Clear();
            armed = true;
            phase = "first";
            List<string> expectedFirst = new List<string> { "first:A" };
            if (nested)
            {
                AppendCurrent(expectedFirst, "nested", reset, initial, replacement);
            }
            if (reset == 0 && !throwing)
            {
                expectedFirst.Add("first:B");
                if (initial)
                {
                    expectedFirst.Add("first:old-variant");
                }
                if (replacement && (kind == MessageKind.Targeted || !initial))
                {
                    expectedFirst.Add("first:replacement-variant");
                }
            }
            try
            {
                Emit(bus, kind, context);
            }
            catch (Exception exception)
            {
                actualError = exception;
            }
            string[] first = calls.ToArray();
            int afterFirstDepth = (int)Field(bus, "_dispatchDepth");
            long afterFirstLease = (long)Field(bus, "_activeDispatchLeaseId");
            long afterFirstScoped = (long)Field(bus, "_scopedEmissionId");
            bool afterFirstDeferred = (bool)Field(bus, "_hasDeferredResetTeardown");
            calls.Clear();
            phase = "next";
            Emit(bus, kind, context);
            string[] second = calls.ToArray();
            List<string> expectedSecond = new List<string>();
            AppendCurrent(expectedSecond, "next", reset, initial, replacement);
            int beforeCleanup = MessageBusInspection.CountLiveRegistrations(bus);
            original.Dispose();
            fresh?.Dispose();
            int afterTokenCleanup = MessageBusInspection.CountLiveRegistrations(bus);
            calls.Clear();
            phase = "cleanup";
            Emit(bus, kind, context);
            string[] cleanup = calls.ToArray();
            List<string> expectedCleanup = new List<string>();
            if (reset == 1)
            {
                AppendCurrent(expectedCleanup, "cleanup", 0, initial, false);
            }
            int afterCleanupEmit = MessageBusInspection.CountLiveRegistrations(bus);
            bus.ResetState();
            state.CheckLeaksBeforeMaintenance();
            _ = bus.Trim(force: true);
            int expectedLive =
                reset == 2
                    ? replacement
                        ? 2
                        : 0
                    : 2 + (initial ? 1 : 0) + (replacement ? 1 : 0) + (featured ? 1 : 0);
            int expectedAfterCleanup = reset == 1 ? 2 + (initial ? 1 : 0) + (featured ? 1 : 0) : 0;

            Assert.That(actualWarm, Is.EqualTo(expectedWarm), label + ": warmup control");
            Assert.That(first, Is.EqualTo(expectedFirst), label + ": original and nested delivery");
            Assert.That(second, Is.EqualTo(expectedSecond), label + ": next valid emission");
            if (throwing)
            {
                Assert.That(actualError, Is.SameAs(sentinel), label + ": exact sentinel");
            }
            else
            {
                Assert.That(actualError, Is.Null, label + ": normal exit");
            }
            Assert.That(afterFirstDepth, Is.Zero, label + ": idle depth");
            Assert.That(afterFirstLease, Is.Zero, label + ": idle lease identity");
            Assert.That(afterFirstScoped, Is.Zero, label + ": idle scoped emission");
            Assert.That(afterFirstDeferred, Is.False, label + ": deferred teardown drained");
            Assert.That(beforeCleanup, Is.EqualTo(expectedLive), label + ": live registrations");
            Assert.That(
                afterTokenCleanup,
                Is.EqualTo(expectedAfterCleanup),
                label + ": valid replacement and stale original cleanup"
            );
            Assert.That(
                cleanup,
                Is.EqualTo(expectedCleanup),
                label + ": cleanup callback identities"
            );
            Assert.That(
                afterCleanupEmit,
                Is.EqualTo(expectedAfterCleanup),
                label + ": cleanup emission preserves live registrations"
            );
            Assert.That(
                MessageBusInspection.CountLiveRegistrations(bus),
                Is.Zero,
                label + ": explicit owned reset cleanup"
            );
        }

        private static void AppendCurrent(
            List<string> expected,
            string phase,
            int reset,
            bool initial,
            bool replacement
        )
        {
            if (reset == 2)
            {
                if (replacement)
                {
                    expected.Add(phase + ":fresh-primary");
                    expected.Add(phase + ":replacement-variant");
                }
                return;
            }
            expected.Add(phase + ":A");
            expected.Add(phase + ":B");
            if (initial)
            {
                expected.Add(phase + ":old-variant");
            }
            if (replacement)
            {
                expected.Add(phase + ":replacement-variant");
            }
        }

        private static object Field(MessageBus bus, string name) =>
            typeof(MessageBus)
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(bus);

        private static void RegisterPrimary(
            MessageRegistrationToken token,
            MessageKind kind,
            bool fast,
            InstanceId context,
            Action callback
        )
        {
            if (kind == MessageKind.Targeted)
            {
                _ = fast
                    ? token.RegisterTargeted<ProbeMessage>(
                        context,
                        (in ProbeMessage _) => callback()
                    )
                    : token.RegisterTargeted<ProbeMessage>(context, _ => callback());
            }
            else
            {
                _ = fast
                    ? token.RegisterBroadcast<ProbeMessage>(
                        context,
                        (in ProbeMessage _) => callback()
                    )
                    : token.RegisterBroadcast<ProbeMessage>(context, _ => callback());
            }
        }

        private static void RegisterWithout(
            MessageRegistrationToken token,
            MessageKind kind,
            bool fast,
            Action callback
        )
        {
            if (kind == MessageKind.Targeted)
            {
                _ = fast
                    ? token.RegisterTargetedWithoutTargeting<ProbeMessage>(
                        (in InstanceId _, in ProbeMessage __) => callback()
                    )
                    : token.RegisterTargetedWithoutTargeting<ProbeMessage>((_, __) => callback());
            }
            else
            {
                _ = fast
                    ? token.RegisterBroadcastWithoutSource<ProbeMessage>(
                        (in InstanceId _, in ProbeMessage __) => callback()
                    )
                    : token.RegisterBroadcastWithoutSource<ProbeMessage>((_, __) => callback());
            }
        }

        private static void RegisterInterceptor(MessageRegistrationToken token, MessageKind kind)
        {
            if (kind == MessageKind.Targeted)
            {
                _ = token.RegisterTargetedInterceptor<ProbeMessage>(
                    static (ref InstanceId _, ref ProbeMessage __) => true
                );
            }
            else
            {
                _ = token.RegisterBroadcastInterceptor<ProbeMessage>(
                    static (ref InstanceId _, ref ProbeMessage __) => true
                );
            }
        }

        private static void Emit(MessageBus bus, MessageKind kind, InstanceId context)
        {
            ProbeMessage message = new ProbeMessage();
            if (kind == MessageKind.Targeted)
            {
                bus.TargetedBroadcast(ref context, ref message);
            }
            else
            {
                bus.SourcedBroadcast(ref context, ref message);
            }
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
                Handler = new MessageHandler(new InstanceId(8201), Bus) { active = true };
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

        private sealed class ProbeFailure : Exception { }

        private readonly struct ProbeMessage : ITargetedMessage, IBroadcastMessage
        {
            public Type MessageType => typeof(ProbeMessage);
        }
    }
}
