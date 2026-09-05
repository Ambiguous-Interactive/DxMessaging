#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using DxMessaging.Core;
    using DxMessaging.Core.Configuration;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using UnityEngine;

    /// <summary>
    /// Self-tests for <see cref="LeakWatcher"/>. Confirms the watcher detects a
    /// known leak (a registration that escapes its <c>using</c> region) and
    /// does not flag clean code (a registration removed before
    /// <see cref="LeakWatcher.Dispose"/>).
    /// </summary>
    public sealed class LeakWatcherSelfTests : MessagingTestBase
    {
        [Test]
        public void WatcherPassesWhenAllHandlesAreRemoved(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(WatcherPassesWhenAllHandlesAreRemoved) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            using (LeakWatcher watcher = LeakWatcher.Watch(label: scenario.DisplayName))
            {
                int initial = watcher.InitialSnapshot;
                MessageRegistrationHandle handle = RegisterCountingHandler(scenario, token, hostId);
                Assert.GreaterOrEqual(
                    watcher.Snapshot,
                    initial + 1,
                    "[{0}] Watcher.Snapshot must reflect the new registration in real time.",
                    scenario.Kind
                );
                token.RemoveRegistration(handle);
                Assert.AreEqual(
                    initial,
                    watcher.Snapshot,
                    "[{0}] Watcher.Snapshot must return to the initial value after removal.",
                    scenario.Kind
                );
            }
        }

        [Test]
        public void WatcherDetectsLeakedRegistrationWhenNotThrowing(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(WatcherDetectsLeakedRegistrationWhenNotThrowing) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            MessageRegistrationHandle leaked = default;
            bool leakedRegistered = false;
            int observedLeak = 0;
            try
            {
                using (
                    LeakWatcher watcher = new LeakWatcher(
                        bus: MessageHandler.MessageBus,
                        throwOnLeak: false,
                        label: scenario.DisplayName
                    )
                )
                {
                    leaked = RegisterCountingHandler(scenario, token, hostId);
                    leakedRegistered = true;
                    // Intentionally NOT removing the registration before Dispose so
                    // the watcher records the leak.
                    Assert.GreaterOrEqual(
                        watcher.LeakedRegistrations,
                        1,
                        "[{0}] LeakedRegistrations must report >=1 while a leaked handle is still live.",
                        scenario.Kind
                    );
                    observedLeak = watcher.LeakedRegistrations;
                }

                Assert.GreaterOrEqual(
                    observedLeak,
                    1,
                    "[{0}] Watcher must observe at least one leaked registration before disposal.",
                    scenario.Kind
                );
            }
            finally
            {
                // Clean up the leaked handle outside the using block, in a
                // finally that runs even if any of the assertions above
                // throw (so the next test does not inherit the leaked
                // registration). The cleanup is best-effort: a registration
                // wiped by a Reset triggered earlier is a no-op here.
                if (leakedRegistered)
                {
                    token.RemoveRegistration(leaked);
                }
            }
        }

        [Test]
        public void WatcherThrowsOnLeakWhenConfiguredTo(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(WatcherThrowsOnLeakWhenConfiguredTo) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            LeakWatcher watcher = LeakWatcher.Watch(label: scenario.DisplayName);
            MessageRegistrationHandle leaked = RegisterCountingHandler(scenario, token, hostId);

            try
            {
                Assert.Throws<AssertionException>(
                    watcher.Dispose,
                    "[{0}] LeakWatcher.Dispose with throwOnLeak=true must surface a failed assertion when registrations leak.",
                    scenario.Kind
                );
            }
            finally
            {
                token.RemoveRegistration(leaked);
            }
        }

        [Test]
        public void WatcherWithSlotsPassesAfterExplicitTrim(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(WatcherWithSlotsPassesAfterExplicitTrim) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;
            using IDisposable settingsOverride = ForceTrimEnabledSettings();
            IMessageBus bus = MessageHandler.MessageBus;

            using (LeakWatcher watcher = LeakWatcher.WatchWithSlots(label: scenario.DisplayName))
            {
                int initialSlots = watcher.InitialSlotSnapshot;
                MessageRegistrationHandle handle = RegisterCountingHandler(scenario, token, hostId);
                Assert.GreaterOrEqual(
                    watcher.SlotSnapshot,
                    initialSlots + 1,
                    "[{0}] Watcher.SlotSnapshot must reflect occupied slots while registered.",
                    scenario.Kind
                );

                token.RemoveRegistration(handle);
                _ = bus.Trim(force: true);

                Assert.AreEqual(
                    initialSlots,
                    watcher.SlotSnapshot,
                    "[{0}] Watcher.SlotSnapshot must return to the initial value after trim.",
                    scenario.Kind
                );
            }
        }

        [Test]
        public void WatcherWithSlotsDetectsUnreclaimedSlotWhenNotThrowing(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(WatcherWithSlotsDetectsUnreclaimedSlotWhenNotThrowing) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;
            using IDisposable settingsOverride = ForceTrimEnabledSettings();
            IMessageBus bus = MessageHandler.MessageBus;

            int leakedTypeSlots;
            int leakedTargetSlots;
            int leakedSlots;
            int leakedRegistrations;
            string deltaDescription;
            try
            {
                using (
                    LeakWatcher watcher = LeakWatcher.WatchWithSlots(
                        bus,
                        throwOnLeak: false,
                        label: scenario.DisplayName
                    )
                )
                {
                    MessageRegistrationHandle handle = RegisterCountingHandler(
                        scenario,
                        token,
                        hostId
                    );
                    token.RemoveRegistration(handle);

                    leakedRegistrations = watcher.LeakedRegistrations;
                    leakedTypeSlots = watcher.LeakedTypeSlots;
                    leakedTargetSlots = watcher.LeakedTargetSlots;
                    leakedSlots = watcher.LeakedSlots;
                    deltaDescription = watcher.DescribeDelta();
                }

                Assert.AreEqual(
                    0,
                    leakedRegistrations,
                    "[{0}] WatchWithSlots must keep registration and slot leak accounting separate.",
                    scenario.Kind
                );
                Assert.GreaterOrEqual(
                    leakedSlots,
                    1,
                    "[{0}] WatchWithSlots must detect occupied slots left behind after deregistration without trim.",
                    scenario.Kind
                );
                Assert.AreEqual(leakedSlots, leakedTypeSlots + leakedTargetSlots);
                StringAssert.Contains("TypeSlots", deltaDescription);
                StringAssert.Contains("TargetSlots", deltaDescription);
            }
            finally
            {
                _ = bus.Trim(force: true);
            }
        }

        [Test]
        public void WatcherWithSlotsThrowsOnSlotOnlyLeak(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(WatcherWithSlotsThrowsOnSlotOnlyLeak) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;
            using IDisposable settingsOverride = ForceTrimEnabledSettings();
            IMessageBus bus = MessageHandler.MessageBus;
            LeakWatcher watcher = LeakWatcher.WatchWithSlots(label: scenario.DisplayName);

            try
            {
                MessageRegistrationHandle handle = RegisterCountingHandler(scenario, token, hostId);
                token.RemoveRegistration(handle);

                AssertionException exception = Assert.Throws<AssertionException>(
                    watcher.Dispose,
                    "[{0}] WatchWithSlots must fail when registrations drain but occupied slots remain.",
                    scenario.Kind
                );
                StringAssert.Contains("type slot delta", exception.Message);
            }
            finally
            {
                _ = bus.Trim(force: true);
            }
        }

        [Test]
        public void DefaultWatcherIgnoresSlotOnlyFootprint(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(DefaultWatcherIgnoresSlotOnlyFootprint) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;
            using IDisposable settingsOverride = ForceTrimEnabledSettings();
            IMessageBus bus = MessageHandler.MessageBus;

            try
            {
                using (LeakWatcher watcher = LeakWatcher.Watch(label: scenario.DisplayName))
                {
                    MessageRegistrationHandle handle = RegisterCountingHandler(
                        scenario,
                        token,
                        hostId
                    );
                    token.RemoveRegistration(handle);

                    Assert.GreaterOrEqual(
                        watcher.LeakedSlots,
                        1,
                        "[{0}] The default watcher should still report slot drift.",
                        scenario.Kind
                    );
                    Assert.AreEqual(
                        0,
                        watcher.LeakedRegistrations,
                        "[{0}] Registration counters must be clean before default watcher disposal.",
                        scenario.Kind
                    );
                }
            }
            finally
            {
                _ = bus.Trim(force: true);
            }
        }

        [Test]
        public void HandlerStorageWatcherDetectsDeferredContextAfterRegistrationsDrain(
            [Values(false, true)] bool throwOnLeak,
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.KindsWithComponentTarget)
            )]
                MessageScenario scenario
        )
        {
            MessageBus bus = MessageBus.CreateForInternalUse(new FakeClock(), idleEvictionTicks: 0);
            MessageHandler handler = new MessageHandler(new InstanceId(701), bus) { active = true };
            using MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            token.Enable();
            InstanceId anchorContext = new InstanceId(702);
            InstanceId transientContext = new InstanceId(703);
            MessageRegistrationHandle anchor = RegisterCountingHandler(
                scenario,
                token,
                anchorContext
            );
            LeakWatcher watcher = new LeakWatcher(
                bus,
                throwOnLeak,
                scenario.DisplayName,
                handler: handler
            );
            LeakWatcher defaultWatcher = new LeakWatcher(bus);
            try
            {
                MessageRegistrationHandle transient = default;
                transient = RegisterCountingHandler(
                    scenario,
                    token,
                    transientContext,
                    () => token.RemoveRegistration(transient)
                );
                EmitCountingMessage(scenario, bus, transientContext);
                Assert.AreEqual(
                    0,
                    watcher.LeakedRegistrations,
                    "[{0}, throw={1}] the transient registration must be removed before checking storage.",
                    scenario.Kind,
                    throwOnLeak
                );
                Assert.AreEqual(
                    1,
                    watcher.LeakedHandlerContexts,
                    "[{0}, throw={1}] the watcher must detect the retained empty context above its live baseline.",
                    scenario.Kind,
                    throwOnLeak
                );
                Assert.AreEqual(
                    1,
                    watcher.LeakedHandlerPriorityCaches,
                    "[{0}, throw={1}] the watcher must detect the retained empty priority cache.",
                    scenario.Kind,
                    throwOnLeak
                );
                Assert.DoesNotThrow(
                    defaultWatcher.Dispose,
                    "[{0}, throw={1}] default registration-only watching must ignore retained handler storage.",
                    scenario.Kind,
                    throwOnLeak
                );
                if (throwOnLeak)
                {
                    AssertionException exception = Assert.Throws<AssertionException>(
                        watcher.Dispose
                    );
                    StringAssert.Contains("HandlerContexts 1->2", exception.Message);
                    StringAssert.Contains("HandlerPriorityCaches 1->2", exception.Message);
                }
                else
                {
                    Assert.DoesNotThrow(
                        watcher.Dispose,
                        "[{0}] non-throwing mode must record handler storage without failing.",
                        scenario.Kind
                    );
                }

                _ = bus.Trim(force: true);
                Assert.AreEqual(
                    1,
                    watcher.LeakedHandlerContexts,
                    "[{0}] disposal must freeze context drift before a later trim.",
                    scenario.Kind
                );
                Assert.AreEqual(
                    1,
                    watcher.LeakedHandlerPriorityCaches,
                    "[{0}] disposal must freeze priority-cache drift before a later trim.",
                    scenario.Kind
                );
                using LeakWatcher trimmedWatcher = LeakWatcher.WatchWithSlots(
                    bus,
                    handler: handler
                );
                MessageRegistrationHandle replacement = RegisterCountingHandler(
                    scenario,
                    token,
                    transientContext
                );
                token.RemoveRegistration(replacement);
                _ = bus.Trim(force: true);
                Assert.AreEqual(
                    0,
                    trimmedWatcher.LeakedHandlerContexts,
                    "[{0}] successful cleanup must restore the live context baseline.",
                    scenario.Kind
                );
                Assert.AreEqual(
                    0,
                    trimmedWatcher.LeakedHandlerPriorityCaches,
                    "[{0}] successful cleanup must restore the live priority-cache baseline.",
                    scenario.Kind
                );
            }
            finally
            {
                token.RemoveRegistration(anchor);
                token.UnregisterAll();
                _ = bus.Trim(force: true);
            }
        }

        [Test]
        public void HandlerStorageCountsRetainedPrioritiesAndOnlyTheRequestedBus(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            MessageBus bus = MessageBus.CreateForInternalUse(new FakeClock(), idleEvictionTicks: 0);
            MessageBus otherBus = MessageBus.CreateForInternalUse(
                new FakeClock(),
                idleEvictionTicks: 0
            );
            MessageHandler handler = new MessageHandler(new InstanceId(710), bus) { active = true };
            using MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            using MessageRegistrationToken otherToken = MessageRegistrationToken.Create(
                handler,
                otherBus
            );
            token.Enable();
            otherToken.Enable();
            using LeakWatcher watcher = LeakWatcher.WatchWithSlots(bus, handler: handler);
            using LeakWatcher otherWatcher = LeakWatcher.WatchWithSlots(otherBus, handler: handler);
            try
            {
                handler.GetRetainedStorageCounts(bus, out int contexts, out int priorities);
                Assert.AreEqual(
                    0,
                    contexts + priorities,
                    "[{0}] querying an unused bus must report no retained storage.",
                    scenario.Kind
                );
                Assert.AreEqual(
                    0,
                    handler._handlersByTypeByMessageBus.Count,
                    "[{0}] the query must not create bus storage.",
                    scenario.Kind
                );
                _ = RegisterCountingHandler(scenario, token, new InstanceId(711));
                _ = RegisterCountingHandler(scenario, token, new InstanceId(711), priority: 1);
                _ = RegisterCountingHandler(scenario, token, new InstanceId(712));
                _ = RegisterCountingHandler(scenario, otherToken, new InstanceId(713));
                handler.GetRetainedStorageCounts(bus, out contexts, out priorities);
                Assert.AreEqual(
                    scenario.Kind == MessageKind.Untargeted ? 0 : 2,
                    contexts,
                    "[{0}] the query must count distinct context keys only on the requested bus.",
                    scenario.Kind
                );
                Assert.AreEqual(
                    scenario.Kind == MessageKind.Untargeted ? 2 : 3,
                    priorities,
                    "[{0}] the query must count priority caches, not delegates sharing one priority.",
                    scenario.Kind
                );
                InstanceId sharedContext = new InstanceId(711);
                switch (scenario.Kind)
                {
                    case MessageKind.Untargeted:
                    {
                        _ = token.RegisterUntargeted<ComplexUntargetedMessage>(
                            (in ComplexUntargetedMessage _) => { }
                        );
                        _ = token.RegisterUntargetedPostProcessor<SimpleUntargetedMessage>(
                            (in SimpleUntargetedMessage _) => { }
                        );
                        break;
                    }
                    case MessageKind.Targeted:
                    {
                        _ = token.RegisterTargeted<ComplexTargetedMessage>(
                            sharedContext,
                            (in ComplexTargetedMessage _) => { }
                        );
                        _ = token.RegisterTargetedPostProcessor<SimpleTargetedMessage>(
                            sharedContext,
                            (in SimpleTargetedMessage _) => { }
                        );
                        break;
                    }
                    case MessageKind.Broadcast:
                    {
                        _ = token.RegisterBroadcast<ComplexBroadcastMessage>(
                            sharedContext,
                            (in ComplexBroadcastMessage _) => { }
                        );
                        _ = token.RegisterBroadcastPostProcessor<SimpleBroadcastMessage>(
                            sharedContext,
                            (in SimpleBroadcastMessage _) => { }
                        );
                        break;
                    }
                }
                handler.GetRetainedStorageCounts(bus, out contexts, out priorities);
                Assert.AreEqual(
                    scenario.Kind == MessageKind.Untargeted ? 0 : 4,
                    contexts,
                    "[{0}] context keys must sum across message types and simultaneous handle/postprocessor slots.",
                    scenario.Kind
                );
                Assert.AreEqual(
                    scenario.Kind == MessageKind.Untargeted ? 4 : 5,
                    priorities,
                    "[{0}] priority caches must sum across message types and simultaneous handle/postprocessor slots.",
                    scenario.Kind
                );
                handler.GetRetainedStorageCounts(otherBus, out contexts, out priorities);
                Assert.AreEqual(
                    scenario.Kind == MessageKind.Untargeted ? 0 : 1,
                    contexts,
                    "[{0}] the other bus must retain only its own context.",
                    scenario.Kind
                );
                Assert.AreEqual(
                    1,
                    priorities,
                    "[{0}] the other bus must retain only its own priority cache.",
                    scenario.Kind
                );
            }
            finally
            {
                token.UnregisterAll();
                otherToken.UnregisterAll();
                _ = bus.Trim(force: true);
                _ = otherBus.Trim(force: true);
            }
        }

        private static void EmitCountingMessage(
            MessageScenario scenario,
            MessageBus bus,
            InstanceId context
        )
        {
            if (scenario.Kind == MessageKind.Targeted)
            {
                SimpleTargetedMessage message = new SimpleTargetedMessage();
                bus.TargetedBroadcast(ref context, ref message);
            }
            else
            {
                SimpleBroadcastMessage message = new SimpleBroadcastMessage();
                bus.SourcedBroadcast(ref context, ref message);
            }
        }

        private static MessageRegistrationHandle RegisterCountingHandler(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId target,
            Action onMessage = null,
            int priority = 0
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    return ScenarioHarness.RegisterUntargeted<SimpleUntargetedMessage>(
                        scenario,
                        token,
                        (in SimpleUntargetedMessage _) => onMessage?.Invoke(),
                        priority
                    );
                }
                case MessageKind.Targeted:
                {
                    return ScenarioHarness.RegisterTargeted<SimpleTargetedMessage>(
                        scenario,
                        token,
                        target,
                        (in SimpleTargetedMessage _) => onMessage?.Invoke(),
                        priority
                    );
                }
                case MessageKind.Broadcast:
                {
                    return ScenarioHarness.RegisterBroadcast<SimpleBroadcastMessage>(
                        scenario,
                        token,
                        target,
                        (in SimpleBroadcastMessage _) => onMessage?.Invoke(),
                        priority
                    );
                }
                default:
                {
                    throw new System.ArgumentOutOfRangeException(
                        nameof(scenario),
                        scenario.Kind,
                        "Unsupported message kind."
                    );
                }
            }
        }

        private static IDisposable ForceTrimEnabledSettings()
        {
            DxMessagingRuntimeSettings settings =
                ScriptableObject.CreateInstance<DxMessagingRuntimeSettings>();
            settings._enableTrimApi = true;
            settings._evictionEnabled = true;
            settings._idleEvictionSeconds = 0f;
            settings._evictionTickIntervalSeconds = 0f;
            return new RuntimeSettingsScope(
                DxMessagingRuntimeSettingsProvider.Override(settings),
                settings
            );
        }

        private sealed class RuntimeSettingsScope : IDisposable
        {
            private readonly IDisposable _overrideToken;
            private readonly DxMessagingRuntimeSettings _settings;
            private bool _disposed;

            public RuntimeSettingsScope(
                IDisposable overrideToken,
                DxMessagingRuntimeSettings settings
            )
            {
                _overrideToken = overrideToken;
                _settings = settings;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _overrideToken.Dispose();
                if (_settings != null)
                {
                    UnityEngine.Object.DestroyImmediate(_settings);
                }
            }
        }
    }
}
#endif
