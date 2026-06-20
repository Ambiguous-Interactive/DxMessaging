#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Unity
{
    using System;
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Core;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using DxMessaging.Unity;
    using NUnit.Framework;
    using UnityEngine;

    /// <summary>
    /// Covers <see cref="MessagingComponent"/> lifecycle surface not exercised elsewhere:
    /// direct <see cref="MessagingComponent.ToggleMessageHandler"/> calls, same-host listener
    /// multiplexing (two <see cref="MessageAwareComponent"/> listeners sharing one
    /// <see cref="MessagingComponent"/>), and <see cref="MessagingComponent.Release"/> edge cases.
    /// </summary>
    /// <remarks>
    /// Complements (does not duplicate):
    /// - <c>EdgeCaseTests.MessagingComponentStopsEmittingWhenDisabled</c> /
    ///   <c>MessagingComponentContinuesEmittingWhenConfigured</c> (single-cycle
    ///   <c>MessagingComponent.enabled</c> toggles via the Unity lifecycle).
    /// - <c>BaseCallContractTests.MultipleSubclassesDoNotCrossContaminate</c> (separate-host
    ///   listeners); the same-host variants live here.
    /// - <c>Core.MessagingComponentLifecycleTests</c> (destroy-driven release bookkeeping).
    /// </remarks>
    public sealed class MessagingComponentLifecycleTests : MessagingTestBase
    {
        [Test]
        public void ToggleMessageHandlerFalseSuspendsDeliveryUntilToggledTrue()
        {
            GameObject host = new(
                nameof(ToggleMessageHandlerFalseSuspendsDeliveryUntilToggledTrue),
                typeof(MessagingComponent),
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(host);
            MessagingComponent messaging = host.GetComponent<MessagingComponent>();
            SimpleMessageAwareComponent listener = host.GetComponent<SimpleMessageAwareComponent>();

            int count = 0;
            listener.untargetedHandler = () => ++count;

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted();
            Assert.AreEqual(1, count, "Positive control: listener should receive while active.");

            // Direct public API call; the component and the listener stay enabled throughout,
            // proving the toggle gates delivery independently of Unity's enabled state.
            messaging.ToggleMessageHandler(false);
            Assert.IsTrue(messaging.enabled, "Toggling the handler must not touch enabled state.");

            message.EmitUntargeted();
            Assert.AreEqual(
                1,
                count,
                "ToggleMessageHandler(false) should suspend delivery for the shared handler."
            );

            messaging.ToggleMessageHandler(true);
            message.EmitUntargeted();
            Assert.AreEqual(2, count, "ToggleMessageHandler(true) should resume delivery.");
        }

        [Test]
        public void ToggleMessageHandlerFalseWinsOverEmitMessagesWhenDisabled()
        {
            GameObject host = new(
                nameof(ToggleMessageHandlerFalseWinsOverEmitMessagesWhenDisabled),
                typeof(MessagingComponent),
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(host);
            MessagingComponent messaging = host.GetComponent<MessagingComponent>();
            messaging.emitMessagesWhenDisabled = true;
            SimpleMessageAwareComponent listener = host.GetComponent<SimpleMessageAwareComponent>();

            int count = 0;
            listener.untargetedHandler = () => ++count;

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted();
            Assert.AreEqual(1, count, "Positive control: listener should receive while active.");

            // emitMessagesWhenDisabled only opts the Unity enable/disable lifecycle out of
            // touching the handler. An EXPLICIT ToggleMessageHandler(false) call is a direct
            // user decision and must always win, flag or no flag.
            messaging.ToggleMessageHandler(false);
            message.EmitUntargeted();
            Assert.AreEqual(
                1,
                count,
                "An explicit ToggleMessageHandler(false) must suspend delivery even while "
                    + "emitMessagesWhenDisabled is true."
            );

            messaging.ToggleMessageHandler(true);
            message.EmitUntargeted();
            Assert.AreEqual(2, count, "ToggleMessageHandler(true) should resume delivery.");
        }

        [Test]
        public void EnableCycleDoesNotOverrideExplicitToggleWhileEmitMessagesWhenDisabledIsTrue()
        {
            GameObject host = new(
                nameof(EnableCycleDoesNotOverrideExplicitToggleWhileEmitMessagesWhenDisabledIsTrue),
                typeof(MessagingComponent),
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(host);
            MessagingComponent messaging = host.GetComponent<MessagingComponent>();
            messaging.emitMessagesWhenDisabled = true;
            SimpleMessageAwareComponent listener = host.GetComponent<SimpleMessageAwareComponent>();

            int count = 0;
            listener.untargetedHandler = () => ++count;

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted();
            Assert.AreEqual(1, count, "Positive control: listener should receive while active.");

            messaging.ToggleMessageHandler(false);
            message.EmitUntargeted();
            Assert.AreEqual(1, count, "Explicit deactivation must suspend delivery.");

            // While emitMessagesWhenDisabled is true the Unity lifecycle must leave the handler
            // alone in BOTH directions: OnDisable must not deactivate it, and OnEnable must not
            // reactivate it behind the user's back. The explicit choice above survives a full
            // enabled=false/true cycle.
            messaging.enabled = false;
            message.EmitUntargeted();
            Assert.AreEqual(
                1,
                count,
                "Disabling the MessagingComponent must not disturb the explicitly suspended handler."
            );

            messaging.enabled = true;
            message.EmitUntargeted();
            Assert.AreEqual(
                1,
                count,
                "Re-enabling the MessagingComponent must not silently reactivate a handler the "
                    + "user explicitly toggled off while emitMessagesWhenDisabled is true."
            );

            messaging.ToggleMessageHandler(true);
            message.EmitUntargeted();
            Assert.AreEqual(
                2,
                count,
                "An explicit ToggleMessageHandler(true) remains the way to resume delivery."
            );
        }

        [Test]
        public void ToggleMessageHandlerTrueReactivatesEvenWhenEmitMessagesWhenDisabledIsTrue()
        {
            GameObject host = new(
                nameof(ToggleMessageHandlerTrueReactivatesEvenWhenEmitMessagesWhenDisabledIsTrue),
                typeof(MessagingComponent),
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(host);
            MessagingComponent messaging = host.GetComponent<MessagingComponent>();
            SimpleMessageAwareComponent listener = host.GetComponent<SimpleMessageAwareComponent>();

            int count = 0;
            listener.untargetedHandler = () => ++count;

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted();
            Assert.AreEqual(1, count, "Positive control: listener should receive while active.");

            // Suspend with the flag clear, then set the flag while suspended. Explicit
            // toggle calls are never gated by emitMessagesWhenDisabled in either
            // direction, so reactivation works with the flag set.
            messaging.ToggleMessageHandler(false);
            message.EmitUntargeted();
            Assert.AreEqual(1, count, "Handler should be suspended while the flag is false.");

            messaging.emitMessagesWhenDisabled = true;
            messaging.ToggleMessageHandler(true);
            message.EmitUntargeted();
            Assert.AreEqual(
                2,
                count,
                "ToggleMessageHandler(true) must reactivate regardless of emitMessagesWhenDisabled."
            );
        }

        [Test]
        public void FlagEnabledWhileLifecycleSuspendedRequiresExplicitReactivation()
        {
            GameObject host = new(
                nameof(FlagEnabledWhileLifecycleSuspendedRequiresExplicitReactivation),
                typeof(MessagingComponent),
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(host);
            MessagingComponent messaging = host.GetComponent<MessagingComponent>();
            SimpleMessageAwareComponent listener = host.GetComponent<SimpleMessageAwareComponent>();
            listener.enabled = false; // isolate the handler gate from the listener token

            int count = 0;
            listener.untargetedHandler = () => ++count;

            // Pins the documented edge of the lifecycle-skip model: with the flag
            // clear, disabling deactivates the handler via the lifecycle. Setting
            // emitMessagesWhenDisabled WHILE suspended then re-enabling does NOT
            // reactivate (the lifecycle no longer touches the handler once the flag
            // is set); an explicit ToggleMessageHandler(true) is the way to resume.
            messaging.enabled = false;
            messaging.emitMessagesWhenDisabled = true;
            messaging.enabled = true;

            listener.enabled = true;
            SimpleUntargetedMessage message = new();
            message.EmitUntargeted();
            Assert.AreEqual(
                0,
                count,
                "Enabling with the flag newly set must not silently reactivate a handler "
                    + "the lifecycle previously deactivated."
            );

            messaging.ToggleMessageHandler(true);
            message.EmitUntargeted();
            Assert.AreEqual(1, count, "An explicit ToggleMessageHandler(true) resumes delivery.");
        }

        [Test]
        public void ToggleMessageHandlerBeforeAwakeIsSafeNoOp()
        {
            GameObject host = new(nameof(ToggleMessageHandlerBeforeAwakeIsSafeNoOp));
            _spawned.Add(host);
            host.SetActive(false);
            MessagingComponent messaging = host.AddComponent<MessagingComponent>();
            ManualListenerComponent listener = host.AddComponent<ManualListenerComponent>();

            // Awake has not run on the inactive host, so no MessageHandler exists yet. Both
            // toggle directions must be safe no-ops on the public API.
            Assert.DoesNotThrow(() => messaging.ToggleMessageHandler(false));
            Assert.DoesNotThrow(() => messaging.ToggleMessageHandler(true));

            host.SetActive(true);

            using (LeakWatcher watcher = LeakWatcher.Watch(label: "PreAwakeToggle"))
            {
                MessageRegistrationToken token = listener.RequestToken(messaging);
                int count = 0;
                _ = token.RegisterUntargeted<SimpleUntargetedMessage>(_ => ++count);
                token.Enable();

                SimpleUntargetedMessage message = new();
                message.EmitUntargeted();
                Assert.AreEqual(
                    1,
                    count,
                    "Handler created on activation should deliver normally after pre-Awake toggles."
                );

                Assert.IsTrue(
                    messaging.Release(listener),
                    "Releasing the registered listener should succeed."
                );
            }
        }

        [Test]
        public void DisablingOneListenerOnSharedHostLeavesSiblingActive()
        {
            GameObject host = new(
                nameof(DisablingOneListenerOnSharedHostLeavesSiblingActive),
                typeof(MessagingComponent),
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(host);
            SimpleMessageAwareComponent first = host.GetComponent<SimpleMessageAwareComponent>();
            SimpleMessageAwareComponent second = host.AddComponent<SimpleMessageAwareComponent>();

            int firstCount = 0;
            int secondCount = 0;
            first.untargetedHandler = () => ++firstCount;
            second.untargetedHandler = () => ++secondCount;

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted();
            Assert.AreEqual(
                1,
                firstCount,
                "Positive control: first listener multiplexed over the shared handler should receive."
            );
            Assert.AreEqual(
                1,
                secondCount,
                "Positive control: second listener multiplexed over the shared handler should receive."
            );

            first.enabled = false;
            message.EmitUntargeted();
            Assert.AreEqual(1, firstCount, "Disabled listener must stop receiving.");
            Assert.AreEqual(
                2,
                secondCount,
                "Sibling on the same host must keep receiving while the other listener is disabled."
            );

            first.enabled = true;
            message.EmitUntargeted();
            Assert.AreEqual(
                2,
                firstCount,
                "Re-enabled listener must resume receiving exactly once per emit."
            );
            Assert.AreEqual(3, secondCount, "Sibling must be unaffected by the re-enable.");
        }

        [Test]
        public void ReleasingOneListenerOnSharedHostLeavesSiblingActive()
        {
            GameObject host = new(
                nameof(ReleasingOneListenerOnSharedHostLeavesSiblingActive),
                typeof(MessagingComponent),
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(host);
            MessagingComponent messaging = host.GetComponent<MessagingComponent>();
            SimpleMessageAwareComponent first = host.GetComponent<SimpleMessageAwareComponent>();
            SimpleMessageAwareComponent second = host.AddComponent<SimpleMessageAwareComponent>();

            int firstCount = 0;
            int secondCount = 0;
            first.untargetedHandler = () => ++firstCount;
            second.untargetedHandler = () => ++secondCount;

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted();
            Assert.AreEqual(1, firstCount, "Positive control: first listener should receive.");
            Assert.AreEqual(1, secondCount, "Positive control: second listener should receive.");

            Assert.IsTrue(
                messaging.Release(first),
                "Releasing a registered listener should report success."
            );
            Assert.IsFalse(first.Token.Enabled, "Released listener's token must be disabled.");

            message.EmitUntargeted();
            Assert.AreEqual(1, firstCount, "Released listener must stop receiving.");
            Assert.AreEqual(
                2,
                secondCount,
                "Sibling on the same host must keep receiving after the other listener is released."
            );

            Assert.IsFalse(
                messaging.Release(first),
                "Releasing the same listener twice should report failure on the second call."
            );
            message.EmitUntargeted();
            Assert.AreEqual(1, firstCount, "Double release must not resurrect the listener.");
            Assert.AreEqual(3, secondCount, "Sibling must be unaffected by the double release.");
        }

        [Test]
        public void ReleaseReturnsFalseForNeverRegisteredListener()
        {
            GameObject host = new(
                nameof(ReleaseReturnsFalseForNeverRegisteredListener),
                typeof(MessagingComponent),
                typeof(SimpleMessageAwareComponent),
                typeof(ManualListenerComponent)
            );
            _spawned.Add(host);
            MessagingComponent messaging = host.GetComponent<MessagingComponent>();
            SimpleMessageAwareComponent registered =
                host.GetComponent<SimpleMessageAwareComponent>();
            ManualListenerComponent neverRegistered = host.GetComponent<ManualListenerComponent>();

            int count = 0;
            registered.untargetedHandler = () => ++count;

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted();
            Assert.AreEqual(1, count, "Positive control: registered listener should receive.");

            Assert.IsFalse(
                messaging.Release(neverRegistered),
                "Releasing a listener that never requested a token should report failure."
            );
            Assert.IsFalse(
                messaging.Release(null),
                "Releasing a null listener should report failure."
            );

            message.EmitUntargeted();
            Assert.AreEqual(
                2,
                count,
                "Failed release calls must have no side effects on registered listeners."
            );
        }

        [Test]
        public void DoubleReleaseReturnsFalseAndLeavesMessagingUsable()
        {
            GameObject host = new(
                nameof(DoubleReleaseReturnsFalseAndLeavesMessagingUsable),
                typeof(MessagingComponent),
                typeof(ManualListenerComponent)
            );
            _spawned.Add(host);
            MessagingComponent messaging = host.GetComponent<MessagingComponent>();
            ManualListenerComponent listener = host.GetComponent<ManualListenerComponent>();

            int originalCount = 0;
            SimpleUntargetedMessage message = new();

            using (LeakWatcher watcher = LeakWatcher.Watch(label: "DoubleRelease"))
            {
                MessageRegistrationToken token = listener.RequestToken(messaging);
                token.DiagnosticMode = true;
                _ = token.RegisterUntargeted<SimpleUntargetedMessage>(_ => ++originalCount);
                token.Enable();

                message.EmitUntargeted();
                Assert.AreEqual(1, originalCount, "Positive control: listener should receive.");
                Assert.AreEqual(1, token._metadata.Count, "Control failed: metadata must exist.");
                Assert.AreEqual(
                    1,
                    token._callCounts.Count,
                    "Control failed: call count must exist."
                );
                Assert.AreEqual(
                    1,
                    token._emissionBuffer.Count,
                    "Control failed: emission history must exist."
                );

                Assert.IsTrue(messaging.Release(listener), "First release should succeed.");
                Assert.IsFalse(token.Enabled, "First release must disable the token.");
                Assert.AreEqual(0, token._metadata.Count, "Release must clear token metadata.");
                Assert.AreEqual(
                    0,
                    token._callCounts.Count,
                    "Release must clear token call counts."
                );
                Assert.AreEqual(
                    0,
                    token._emissionBuffer.Count,
                    "Release must clear token emission history."
                );

                message.EmitUntargeted();
                Assert.AreEqual(1, originalCount, "Released listener must stop receiving.");

                token.Enable();
                message.EmitUntargeted();
                Assert.AreEqual(
                    1,
                    originalCount,
                    "A released token reference must not resurrect old registrations."
                );

                Assert.IsFalse(
                    messaging.Release(listener),
                    "Second release of the same listener should report failure."
                );
                message.EmitUntargeted();
                Assert.AreEqual(
                    1,
                    originalCount,
                    "Second release must not corrupt or resurrect the registration."
                );
            }

            // No corruption: the same listener can request a fresh, fully functional token
            // after the double release, and the old token stays dead.
            MessageRegistrationToken recreated = messaging.Create(listener);
            Assert.IsNotNull(recreated, "Create after release should produce a token.");

            int recreatedCount = 0;
            _ = recreated.RegisterUntargeted<SimpleUntargetedMessage>(_ => ++recreatedCount);
            recreated.Enable();

            message.EmitUntargeted();
            Assert.AreEqual(1, recreatedCount, "Recreated token must deliver messages.");
            Assert.AreEqual(1, originalCount, "Old released token must remain dead.");

            Assert.IsTrue(
                messaging.Release(listener),
                "Release should succeed again after re-creating the token."
            );
        }

        [Test]
        public void ReleaseFailureKeepsListenerRegisteredForRetry()
        {
            GameObject host = new(
                nameof(ReleaseFailureKeepsListenerRegisteredForRetry),
                typeof(MessagingComponent),
                typeof(ManualListenerComponent)
            );
            _spawned.Add(host);
            MessagingComponent messaging = host.GetComponent<MessagingComponent>();
            ManualListenerComponent listener = host.GetComponent<ManualListenerComponent>();
            MessageBus innerBus = new();
            FailingDeregistrationBus failingBus = new(innerBus);
            messaging.Configure(failingBus, MessageBusRebindMode.RebindActive);

            int count = 0;
            SimpleUntargetedMessage message = new();

            using (
                LeakWatcher watcher = new(
                    bus: failingBus,
                    throwOnLeak: true,
                    label: nameof(ReleaseFailureKeepsListenerRegisteredForRetry)
                )
            )
            {
                MessageRegistrationToken token = listener.RequestToken(messaging);
                _ = token.RegisterUntargeted<SimpleUntargetedMessage>(_ => ++count);
                token.Enable();

                message.EmitUntargeted(failingBus);
                Assert.AreEqual(1, count, "Control failed: listener should receive.");

                Assert.Throws<InvalidOperationException>(
                    () => messaging.Release(listener),
                    "The failing bus must surface the token disposal failure."
                );
                Assert.IsTrue(
                    token.Enabled,
                    "Failed release must leave the token active for cleanup retry."
                );
                Assert.AreEqual(
                    1,
                    failingBus.RegisteredUntargeted,
                    "Failed release must not forget the live registration."
                );

                failingBus.AllowDeregistrations();
                Assert.IsTrue(messaging.Release(listener), "Release retry must succeed.");
                Assert.IsFalse(token.Enabled, "Release retry must disable the token.");
                Assert.AreEqual(0, failingBus.RegisteredUntargeted, "Retry must deregister.");
                Assert.IsFalse(
                    messaging.Release(listener),
                    "A second release after successful retry must report failure."
                );
            }
        }

        private sealed class FailingDeregistrationBus : DelegatingMessageBus
        {
            private bool _throwOnDeregistration = true;

            internal FailingDeregistrationBus(IMessageBus inner)
                : base(inner) { }

            internal void AllowDeregistrations()
            {
                _throwOnDeregistration = false;
            }

            public override Action RegisterUntargeted<T>(
                MessageHandler messageHandler,
                int priority = 0
            )
            {
                Action innerDeregister = base.RegisterUntargeted<T>(messageHandler, priority);
                return () =>
                {
                    if (_throwOnDeregistration && typeof(T) == typeof(SimpleUntargetedMessage))
                    {
                        throw new InvalidOperationException("Deregistration failure.");
                    }

                    innerDeregister();
                };
            }
        }
    }
}

#endif
