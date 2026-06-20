#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Unity
{
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Tests.Runtime.Core;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using DxMessaging.Unity;
    using NUnit.Framework;
    using UnityEngine;

    /// <summary>
    /// Covers the interplay between <see cref="MessagingComponent.emitMessagesWhenDisabled"/> and
    /// the <see cref="MessageAwareComponent"/> enable/disable lifecycle.
    /// </summary>
    /// <remarks>
    /// Complements (does not duplicate):
    /// - <c>EdgeCaseTests.MessagingComponentStopsEmittingWhenDisabled</c> and
    ///   <c>EdgeCaseTests.MessagingComponentContinuesEmittingWhenConfigured</c> already pin the
    ///   SINGLE-cycle <c>MessagingComponent.enabled</c> toggle for flag=false / flag=true.
    /// - <c>EnablementTests.StartsEnabled</c> / <c>StartsDisabled</c> already pin the
    ///   SINGLE-cycle listener-level toggle.
    /// This fixture adds the uncovered variants: flag=true with a disabled LISTENER (the flag
    /// does not keep a disabled listener subscribed), repeated toggle cycles with exact-delivery
    /// assertions (no double-registration accumulation), and whole-GameObject deactivation.
    /// </remarks>
    public sealed class MessageAwareComponentTogglingTests : MessagingTestBase
    {
        [Test]
        public void EmitMessagesWhenDisabledTrueDoesNotKeepDisabledListenerReceiving()
        {
            GameObject host = new(
                nameof(EmitMessagesWhenDisabledTrueDoesNotKeepDisabledListenerReceiving),
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
            Assert.AreEqual(1, count, "Positive control: listener should receive while enabled.");

            // emitMessagesWhenDisabled only keeps the SHARED MessageHandler active when the
            // MessagingComponent itself is disabled. A disabled MessageAwareComponent still
            // disables its own registration token, so it stops receiving despite the flag.
            listener.enabled = false;
            message.EmitUntargeted();
            Assert.AreEqual(
                1,
                count,
                "Disabling the listener must stop delivery even when emitMessagesWhenDisabled is true."
            );

            listener.enabled = true;
            message.EmitUntargeted();
            Assert.AreEqual(
                2,
                count,
                "Re-enabling the listener must resume delivery exactly once."
            );
        }

        [Test]
        public void DisabledListenerResumesExactlyOnceAcrossRepeatedToggleCycles()
        {
            GameObject host = new(
                nameof(DisabledListenerResumesExactlyOnceAcrossRepeatedToggleCycles),
                typeof(MessagingComponent),
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(host);
            SimpleMessageAwareComponent listener = host.GetComponent<SimpleMessageAwareComponent>();

            int count = 0;
            listener.untargetedHandler = () => ++count;

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted();
            Assert.AreEqual(1, count, "Positive control: listener should receive while enabled.");

            int expected = 1;
            for (int cycle = 0; cycle < 3; ++cycle)
            {
                listener.enabled = false;
                message.EmitUntargeted();
                Assert.AreEqual(
                    expected,
                    count,
                    $"Cycle {cycle}: emit while disabled must not deliver."
                );

                listener.enabled = true;
                message.EmitUntargeted();
                ++expected;
                Assert.AreEqual(
                    expected,
                    count,
                    $"Cycle {cycle}: emit after re-enable must deliver exactly once. A higher "
                        + "count means a toggle cycle double-registered the handlers."
                );
            }
        }

        [Test]
        public void MessagingComponentToggleCyclesWithEmitFlagFalseResumeExactlyOnce()
        {
            GameObject host = new(
                nameof(MessagingComponentToggleCyclesWithEmitFlagFalseResumeExactlyOnce),
                typeof(MessagingComponent),
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(host);
            MessagingComponent messaging = host.GetComponent<MessagingComponent>();
            Assert.IsFalse(
                messaging.emitMessagesWhenDisabled,
                "Precondition: emitMessagesWhenDisabled defaults to false."
            );
            SimpleMessageAwareComponent listener = host.GetComponent<SimpleMessageAwareComponent>();

            int count = 0;
            listener.untargetedHandler = () => ++count;

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted();
            Assert.AreEqual(1, count, "Positive control: listener should receive while enabled.");

            int expected = 1;
            for (int cycle = 0; cycle < 3; ++cycle)
            {
                messaging.enabled = false;
                message.EmitUntargeted();
                Assert.AreEqual(
                    expected,
                    count,
                    $"Cycle {cycle}: emit while the MessagingComponent is disabled must not deliver."
                );

                messaging.enabled = true;
                message.EmitUntargeted();
                ++expected;
                Assert.AreEqual(
                    expected,
                    count,
                    $"Cycle {cycle}: emit after re-enabling the MessagingComponent must deliver "
                        + "exactly once."
                );
            }
        }

        [Test]
        public void MessagingComponentDisabledWithEmitFlagTrueKeepsReceivingAcrossToggleCycles()
        {
            GameObject host = new(
                nameof(MessagingComponentDisabledWithEmitFlagTrueKeepsReceivingAcrossToggleCycles),
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
            Assert.AreEqual(1, count, "Positive control: listener should receive while enabled.");

            int expected = 1;
            for (int cycle = 0; cycle < 3; ++cycle)
            {
                messaging.enabled = false;
                message.EmitUntargeted();
                ++expected;
                Assert.AreEqual(
                    expected,
                    count,
                    $"Cycle {cycle}: emitMessagesWhenDisabled must keep delivery alive while the "
                        + "MessagingComponent is disabled, exactly once per emit."
                );

                messaging.enabled = true;
                message.EmitUntargeted();
                ++expected;
                Assert.AreEqual(
                    expected,
                    count,
                    $"Cycle {cycle}: re-enabling must deliver exactly once per emit; a higher "
                        + "count means repeated OnEnable cycles accumulated registrations."
                );
            }
        }

        [Test]
        public void InactiveGameObjectKeepsReceivingWhenEmitMessagesWhenDisabledIsTrue()
        {
            GameObject host = new(
                nameof(InactiveGameObjectKeepsReceivingWhenEmitMessagesWhenDisabledIsTrue),
                typeof(MessagingComponent),
                typeof(ManualListenerComponent)
            );
            _spawned.Add(host);
            MessagingComponent messaging = host.GetComponent<MessagingComponent>();
            messaging.emitMessagesWhenDisabled = true;
            ManualListenerComponent listener = host.GetComponent<ManualListenerComponent>();

            using (LeakWatcher watcher = LeakWatcher.Watch(label: "InactiveGameObjectFlagTrue"))
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
                    "Positive control: listener should receive while active."
                );

                // The manual listener does not tie its token to the Unity lifecycle, so the only
                // gate that whole-GameObject deactivation flips is the shared handler's active
                // flag - which emitMessagesWhenDisabled keeps alive. This pins the documented
                // purpose of the flag: keep emitting while the GameObject is disabled.
                host.SetActive(false);
                message.EmitUntargeted();
                Assert.AreEqual(
                    2,
                    count,
                    "emitMessagesWhenDisabled must keep delivery alive while the GameObject is inactive."
                );

                host.SetActive(true);
                message.EmitUntargeted();
                Assert.AreEqual(3, count, "Reactivation must continue delivering exactly once.");

                Assert.IsTrue(
                    messaging.Release(listener),
                    "Releasing the registered listener should succeed."
                );
            }
        }

        [Test]
        public void InactiveGameObjectStopsReceivingWhenEmitMessagesWhenDisabledIsFalse()
        {
            GameObject host = new(
                nameof(InactiveGameObjectStopsReceivingWhenEmitMessagesWhenDisabledIsFalse),
                typeof(MessagingComponent),
                typeof(ManualListenerComponent)
            );
            _spawned.Add(host);
            MessagingComponent messaging = host.GetComponent<MessagingComponent>();
            Assert.IsFalse(
                messaging.emitMessagesWhenDisabled,
                "Precondition: emitMessagesWhenDisabled defaults to false."
            );
            ManualListenerComponent listener = host.GetComponent<ManualListenerComponent>();

            using (LeakWatcher watcher = LeakWatcher.Watch(label: "InactiveGameObjectFlagFalse"))
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
                    "Positive control: listener should receive while active."
                );

                host.SetActive(false);
                message.EmitUntargeted();
                Assert.AreEqual(
                    1,
                    count,
                    "Deactivating the GameObject must suspend delivery when the flag is false, "
                        + "even though the manual token itself stays enabled."
                );

                host.SetActive(true);
                message.EmitUntargeted();
                Assert.AreEqual(2, count, "Reactivating the GameObject must resume delivery.");

                Assert.IsTrue(
                    messaging.Release(listener),
                    "Releasing the registered listener should succeed."
                );
            }
        }
    }
}

#endif
