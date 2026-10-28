namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;
    using BusType = DxMessaging.Core.MessageBus.MessageBus;

    /// <summary>Keeps diagnostic counts scoped to staging while retaining completed deliveries.</summary>
    /// <remarks>2026-10-25: Post-callback recording could reinsert a removed handle indefinitely.</remarks>
    [TestFixture]
#if !UNITY_2021_3_OR_NEWER
    [NonParallelizable]
#endif
    public sealed class TokenDiagnosticStagedCountsTests
    {
        private static readonly InstanceId Context = new InstanceId(988);

        private static IEnumerable<TestCaseData> ChurnCases()
        {
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            foreach (int carrier in new[] { 0, 1, 2, 3, 4 })
            foreach (bool diagnostics in new[] { false, true })
            foreach (bool churn in new[] { false, true })
            foreach (int emissions in new[] { 1, 2, 8, 64 })
            foreach (int capacity in new[] { 0, 4 })
            foreach (bool peer in new[] { false, true })
            {
                yield return new TestCaseData(
                    scenario,
                    carrier,
                    diagnostics,
                    churn,
                    emissions,
                    capacity,
                    peer
                ).SetName(
                    $"StagedDiagnosticCounts_{scenario}_carrier{carrier}_diagnostics{diagnostics}_churn{churn}_emissions{emissions}_capacity{capacity}_peer{peer}"
                );
            }
        }

        /// <summary>Retains only staged counter identities across repeated callback replacement.</summary>
        [TestCaseSource(nameof(ChurnCases))]
        public void RestagedCallbacksDoNotRetainRetiredCounterKeys(
            MessageScenario scenario,
            int carrier,
            bool diagnostics,
            bool churn,
            int emissions,
            int capacity,
            bool peer
        )
        {
            string label =
                $"{scenario},carrier={carrier},diagnostics={diagnostics},churn={churn},emissions={emissions},capacity={capacity},peer={peer}";
            using Scope scope = new Scope(capacity, diagnostics, label);
            int calls = 0;
            int peerCalls = 0;
            MessageRegistrationHandle current = default;
            MessageRegistrationHandle peerHandle = default;
            List<MessageRegistrationHandle> retired = new List<MessageRegistrationHandle>();
            List<long> traces = new List<long>();
            if (peer)
            {
                peerHandle = scope.Token.RegisterUntargeted<PeerMessage>(
                    (in PeerMessage _) => ++peerCalls
                );
            }
            Action callback = null;
            callback = () =>
            {
                ++calls;
                traces.Add(scope.Bus.EmissionId);
                if (churn)
                {
                    MessageRegistrationHandle previous = current;
                    scope.Token.RemoveRegistration(previous);
                    retired.Add(previous);
                    current = Register(scope.Token, scenario, carrier, callback);
                    Assert.That(
                        current.Slot,
                        Is.EqualTo(previous.Slot),
                        label + ": actual slot reuse"
                    );
                    Assert.That(
                        current,
                        Is.Not.EqualTo(previous),
                        label + ": replacement has a new identity"
                    );
                    scope.Token.RemoveRegistration(previous);
                    Assert.That(
                        scope.Token._metadata.ContainsKey(current),
                        Is.True,
                        label + ": stale removal preserves replacement"
                    );
                }
            };
            current = Register(scope.Token, scenario, carrier, callback);
            Assert.That(
                Counters(scope.Bus),
                Is.All.EqualTo(0),
                label + ": staging is initially inactive"
            );
            scope.Token.Enable();
            for (int i = 0; i < emissions; ++i)
            {
                Emit(scope.Bus, scenario);
            }
            Assert.That(
                calls,
                Is.EqualTo(emissions),
                label + ": each emission invokes exactly once"
            );
            Assert.That(peerCalls, Is.Zero, label + ": unused peer never runs");
            Assert.That(scope.Token.Enabled, Is.True, label + ": replacement stays active");
            Assert.That(
                scope.Token._metadata.Count,
                Is.EqualTo(peer ? 2 : 1),
                label + ": exact staged count"
            );
            Assert.That(
                scope.Token._metadata.ContainsKey(current),
                Is.True,
                label + ": current identity remains staged"
            );
            Assert.That(
                Sum(Counters(scope.Bus)),
                Is.EqualTo(peer ? 2 : 1),
                label + ": exact live registrations"
            );

            bool recording = diagnostics && carrier != 2;
            Assert.That(
                scope.Token._callCounts.Count,
                Is.EqualTo(recording && !churn ? 1 : 0),
                label + ": only current staging can have a count"
            );
            if (recording && !churn)
            {
                Assert.That(
                    scope.Token._callCounts[current],
                    Is.EqualTo(emissions),
                    label + ": stable handle counts every completed callback"
                );
            }
            foreach (MessageRegistrationHandle handle in retired)
            {
                Assert.That(
                    scope.Token._callCounts.ContainsKey(handle),
                    Is.False,
                    label + ": retired identity has no counter"
                );
            }
            if (peer)
            {
                Assert.That(
                    scope.Token._callCounts.ContainsKey(peerHandle),
                    Is.False,
                    label + ": unused peer has no count"
                );
            }

            int history = recording ? Math.Min(capacity, churn && !peer ? 1 : emissions) : 0;
            Assert.That(
                scope.Token._emissionBuffer.Count,
                Is.EqualTo(history),
                label + ": bounded successful-delivery history"
            );
            for (int i = 0; i < history; ++i)
            {
                int index = emissions - history + i;
                Assert.That(
                    scope.Token._emissionBuffer[i].registrationHandle,
                    Is.EqualTo(churn ? retired[index] : current),
                    label + ": history preserves delivered identity"
                );
                Assert.That(
                    scope.Token._emissionBuffer[i].traceId,
                    Is.EqualTo(traces[index]),
                    label + ": history preserves originating dispatch"
                );
            }
            AssertClean(scope, label);
        }

        private static IEnumerable<TestCaseData> FrozenCases()
        {
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            foreach (int carrier in new[] { 0, 1, 3, 4 })
            foreach (bool remove in new[] { false, true })
            foreach (bool nested in new[] { false, true })
            foreach (bool throws in new[] { false, true })
            foreach (bool diagnostics in new[] { false, true })
            foreach (int toggle in new[] { -1, 0, 1 })
            foreach (int capacity in new[] { 0, 4 })
            {
                yield return new TestCaseData(
                    scenario,
                    carrier,
                    remove,
                    nested,
                    throws,
                    diagnostics,
                    toggle,
                    capacity
                ).SetName(
                    $"FrozenStagedDiagnosticCounts_{scenario}_carrier{carrier}_remove{remove}_nested{nested}_throws{throws}_diagnostics{diagnostics}_toggle{toggle}_capacity{capacity}"
                );
            }
        }

        /// <summary>Preserves completed frozen history without recreating retired counters.</summary>
        [TestCaseSource(nameof(FrozenCases))]
        public void FrozenCallbacksKeepHistoryWithoutRevivingRemovedCounters(
            MessageScenario scenario,
            int carrier,
            bool remove,
            bool nested,
            bool throws,
            bool diagnostics,
            int toggle,
            int capacity
        )
        {
            string label =
                $"{scenario},carrier={carrier},remove={remove},nested={nested},throws={throws},diagnostics={diagnostics},toggle={toggle},capacity={capacity}";
            using Scope scope = new Scope(capacity, diagnostics, label);
            MessageRegistrationHandle first = default;
            MessageRegistrationHandle peer = default;
            MessageRegistrationHandle replacement = default;
            bool outer = true;
            bool inNested = false;
            long outerTrace = 0;
            long nestedTrace = 0;
            List<string> delivery = new List<string>();
            InvalidOperationException sentinel = new InvalidOperationException(
                "Expected outer callback failure."
            );
            first = Register(
                scope.Token,
                scenario,
                carrier,
                () =>
                {
                    delivery.Add(inNested ? "nested:A" : "outer:A");
                    if (!outer)
                    {
                        nestedTrace = scope.Bus.EmissionId;
                        return;
                    }
                    outer = false;
                    outerTrace = scope.Bus.EmissionId;
                    if (remove)
                    {
                        scope.Token.RemoveRegistration(peer);
                        replacement = Register(
                            scope.Token,
                            scenario,
                            carrier,
                            () => delivery.Add(inNested ? "nested:C" : "outer:C")
                        );
                        Assert.That(
                            replacement.Slot,
                            Is.EqualTo(peer.Slot),
                            label + ": peer slot is reused"
                        );
                        Assert.That(
                            replacement,
                            Is.Not.EqualTo(peer),
                            label + ": replacement identity is distinct"
                        );
                    }
                    if (0 <= toggle)
                    {
                        scope.Token.DiagnosticMode = toggle == 1;
                    }
                    if (nested)
                    {
                        inNested = true;
                        try
                        {
                            Emit(scope.Bus, scenario);
                        }
                        finally
                        {
                            inNested = false;
                        }
                    }
                    if (throws)
                    {
                        throw sentinel;
                    }
                }
            );
            peer = Register(
                scope.Token,
                scenario,
                carrier,
                () => delivery.Add(inNested ? "nested:B" : "outer:B")
            );
            scope.Token.Enable();
            Exception caught = null;
            try
            {
                Emit(scope.Bus, scenario);
            }
            catch (Exception exception)
            {
                caught = exception;
            }
            Assert.That(
                caught,
                Is.SameAs(throws ? sentinel : null),
                label + ": original user exception propagates"
            );
            List<string> expectedDelivery = new List<string> { "outer:A" };
            if (nested)
            {
                expectedDelivery.Add("nested:A");
                expectedDelivery.Add(remove ? "nested:C" : "nested:B");
                Assert.That(
                    nestedTrace,
                    Is.Not.EqualTo(outerTrace),
                    label + ": nested dispatch has its own trace"
                );
            }
            if (!throws)
            {
                expectedDelivery.Add("outer:B");
            }
            Assert.That(
                delivery,
                Is.EqualTo(expectedDelivery),
                label + ": outer snapshot and nested visibility"
            );
            Assert.That(outerTrace, Is.GreaterThan(0L), label + ": actual outer dispatch");
            Assert.That(
                scope.Token._metadata.Count,
                Is.EqualTo(2),
                label + ": exact staging survives callback mutation"
            );
            Assert.That(
                scope.Token._metadata.ContainsKey(first),
                Is.True,
                label + ": first remains staged"
            );
            Assert.That(
                scope.Token._metadata.ContainsKey(peer),
                Is.EqualTo(!remove),
                label + ": exact peer staging"
            );
            Assert.That(
                Sum(Counters(scope.Bus)),
                Is.EqualTo(1),
                label + ": one shared public route container remains"
            );
            Assert.That(
                MessageBusInspection.CountLiveRegistrations(scope.Bus),
                Is.EqualTo(2),
                label + ": two live callback registrations remain"
            );
            bool effectiveDiagnostics = toggle < 0 ? diagnostics : toggle == 1;
            int firstCount = effectiveDiagnostics ? (nested ? 1 : 0) + (throws ? 0 : 1) : 0;
            int secondCount = effectiveDiagnostics
                ? (nested ? 1 : 0) + (!remove && !throws ? 1 : 0)
                : 0;
            int expectedKeys = (0 < firstCount ? 1 : 0) + (0 < secondCount ? 1 : 0);
            Assert.That(
                scope.Token._callCounts.Count,
                Is.EqualTo(expectedKeys),
                label + ": exact staged diagnostic key set"
            );
            AssertCount(scope.Token, first, firstCount, label);
            AssertCount(scope.Token, remove ? replacement : peer, secondCount, label);
            if (remove)
            {
                Assert.That(
                    scope.Token._callCounts.ContainsKey(peer),
                    Is.False,
                    label + ": frozen removed callback has no counter"
                );
            }
            List<MessageRegistrationHandle> expectedHandles = new List<MessageRegistrationHandle>();
            List<long> expectedTraces = new List<long>();
            if (effectiveDiagnostics && 0 < capacity)
            {
                if (nested)
                {
                    expectedHandles.Add(first);
                    expectedTraces.Add(nestedTrace);
                    expectedHandles.Add(remove ? replacement : peer);
                    expectedTraces.Add(nestedTrace);
                }
                if (!throws)
                {
                    expectedHandles.Add(first);
                    expectedTraces.Add(outerTrace);
                    expectedHandles.Add(peer);
                    expectedTraces.Add(outerTrace);
                }
            }
            Assert.That(
                scope.Token._emissionBuffer.Count,
                Is.EqualTo(expectedHandles.Count),
                label + ": exact completed callback history"
            );
            for (int i = 0; i < expectedHandles.Count; ++i)
            {
                Assert.That(
                    scope.Token._emissionBuffer[i].registrationHandle,
                    Is.EqualTo(expectedHandles[i]),
                    label + ": completed identity order"
                );
                Assert.That(
                    scope.Token._emissionBuffer[i].traceId,
                    Is.EqualTo(expectedTraces[i]),
                    label + ": scoped origin order"
                );
            }
            AssertClean(scope, label);
        }

        private static void AssertCount(
            MessageRegistrationToken token,
            MessageRegistrationHandle handle,
            int expected,
            string label
        )
        {
            Assert.That(
                token._callCounts.TryGetValue(handle, out int actual),
                Is.EqualTo(0 < expected),
                label + ": count exists only after successful staged delivery"
            );
            Assert.That(
                actual,
                Is.EqualTo(expected),
                label + ": exact successful staged callback count"
            );
        }

        private static void AssertClean(Scope scope, string label)
        {
            scope.Token.UnregisterAll();
            Assert.That(
                Counters(scope.Bus),
                Is.All.EqualTo(0),
                label + ": token-only cleanup clears live registrations"
            );
            Assert.That(scope.Token.Enabled, Is.False, label + ": cleanup disables token");
            Assert.That(scope.Token._metadata.Count, Is.Zero, label + ": cleanup clears staging");
            Assert.That(scope.Token._callCounts, Is.Empty, label + ": cleanup clears counts");
            Assert.That(
                scope.Token._emissionBuffer.Count,
                Is.Zero,
                label + ": cleanup clears history"
            );
        }

        private static MessageRegistrationHandle Register(
            MessageRegistrationToken token,
            MessageScenario scenario,
            int carrier,
            Action callback
        )
        {
            Action<ProbeMessage> action = _ => callback();
            MessageHandler.FastHandler<ProbeMessage> fast = (in ProbeMessage _) => callback();
            if (carrier < 2)
            {
                return scenario.Kind switch
                {
                    MessageKind.Untargeted => carrier == 0
                        ? token.RegisterUntargeted(action)
                        : ScenarioHarness.RegisterUntargeted(scenario, token, fast),
                    MessageKind.Targeted => carrier == 0
                        ? token.RegisterTargeted(Context, action)
                        : ScenarioHarness.RegisterTargeted(scenario, token, Context, fast),
                    MessageKind.Broadcast => carrier == 0
                        ? token.RegisterBroadcast(Context, action)
                        : ScenarioHarness.RegisterBroadcast(scenario, token, Context, fast),
                    _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
                };
            }
            if (carrier == 2)
            {
                bool Scalar(ref ProbeMessage _)
                {
                    callback();
                    return true;
                }
                bool Contextual(ref InstanceId _, ref ProbeMessage __)
                {
                    callback();
                    return true;
                }
                return scenario.Kind switch
                {
                    MessageKind.Untargeted => token.RegisterUntargetedInterceptor<ProbeMessage>(
                        Scalar
                    ),
                    MessageKind.Targeted => token.RegisterTargetedInterceptor<ProbeMessage>(
                        Contextual
                    ),
                    MessageKind.Broadcast => token.RegisterBroadcastInterceptor<ProbeMessage>(
                        Contextual
                    ),
                    _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
                };
            }
            if (carrier == 3)
            {
                return token.RegisterGlobalAcceptAll(
                    (IUntargetedMessage _) => callback(),
                    (InstanceId _, ITargetedMessage __) => callback(),
                    (InstanceId _, IBroadcastMessage __) => callback()
                );
            }
            void Untargeted(in IUntargetedMessage _)
            {
                callback();
            }
            void Targeted(in InstanceId _, in ITargetedMessage __)
            {
                callback();
            }
            void Broadcast(in InstanceId _, in IBroadcastMessage __)
            {
                callback();
            }
            return token.RegisterGlobalAcceptAll(Untargeted, Targeted, Broadcast);
        }

        private static void Emit(BusType bus, MessageScenario scenario)
        {
            ProbeMessage message = default;
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                    ScenarioHarness.EmitUntargeted(scenario, ref message, bus);
                    break;
                case MessageKind.Targeted:
                    ScenarioHarness.EmitTargeted(scenario, ref message, Context, bus);
                    break;
                case MessageKind.Broadcast:
                    ScenarioHarness.EmitBroadcast(scenario, ref message, Context, bus);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario));
            }
        }

        private static int[] Counters(BusType bus) =>
            new[]
            {
                bus.RegisteredUntargeted,
                bus.RegisteredTargeted,
                bus.RegisteredBroadcast,
                bus.RegisteredInterceptors,
                bus.RegisteredPostProcessors,
                bus.RegisteredGlobalAcceptAll,
            };

        private static int Sum(int[] values)
        {
            int result = 0;
            foreach (int value in values)
            {
                result += value;
            }
            return result;
        }

        private sealed class Scope : IDisposable
        {
            private readonly bool _debug = MessagingDebug.enabled;
            private readonly Action<LogLevel, string> _log = MessagingDebug.LogFunction;
            private readonly int _capacity = IMessageBus.GlobalMessageBufferSize;
            private readonly bool _stacks = IMessageBus.GlobalDiagnosticsStackTraces;
            private readonly DiagnosticsTarget _targets = IMessageBus.GlobalDiagnosticsTargets;
            private readonly MessageHandler _handler;
            private readonly LeakWatcher _watcher;
            internal readonly BusType Bus = new BusType { DiagnosticsMode = false };
            internal readonly MessageRegistrationToken Token;

            internal Scope(int capacity, bool diagnostics, string label)
            {
                MessagingDebug.enabled = false;
                MessagingDebug.LogFunction = null;
                IMessageBus.GlobalMessageBufferSize = capacity;
                IMessageBus.GlobalDiagnosticsStackTraces = false;
                IMessageBus.GlobalDiagnosticsTargets = DiagnosticsTarget.Off;
                _handler = new MessageHandler(new InstanceId(989), Bus) { active = true };
                Token = MessageRegistrationToken.Create(_handler, Bus);
                Token.DiagnosticMode = diagnostics;
                _watcher = new LeakWatcher(Bus, label: label);
            }

            public void Dispose()
            {
                try
                {
                    Token.Dispose();
                    _handler.active = false;
                    Bus.Trim(force: true);
                    _watcher.Dispose();
                }
                finally
                {
                    MessagingDebug.enabled = _debug;
                    MessagingDebug.LogFunction = _log;
                    IMessageBus.GlobalMessageBufferSize = _capacity;
                    IMessageBus.GlobalDiagnosticsStackTraces = _stacks;
                    IMessageBus.GlobalDiagnosticsTargets = _targets;
                }
            }
        }

        private readonly struct ProbeMessage
            : IUntargetedMessage,
                ITargetedMessage,
                IBroadcastMessage
        {
            public Type MessageType => typeof(ProbeMessage);
        }

        private readonly struct PeerMessage : IUntargetedMessage
        {
            public Type MessageType => typeof(PeerMessage);
        }
    }
}
