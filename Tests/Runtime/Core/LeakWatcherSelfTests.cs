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

        private static MessageRegistrationHandle RegisterCountingHandler(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId target
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    return ScenarioHarness.RegisterUntargeted<SimpleUntargetedMessage>(
                        scenario,
                        token,
                        (ref SimpleUntargetedMessage _) => { }
                    );
                }
                case MessageKind.Targeted:
                {
                    return ScenarioHarness.RegisterTargeted<SimpleTargetedMessage>(
                        scenario,
                        token,
                        target,
                        (ref SimpleTargetedMessage _) => { }
                    );
                }
                case MessageKind.Broadcast:
                {
                    return ScenarioHarness.RegisterBroadcast<SimpleBroadcastMessage>(
                        scenario,
                        token,
                        target,
                        (ref SimpleBroadcastMessage _) => { }
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
