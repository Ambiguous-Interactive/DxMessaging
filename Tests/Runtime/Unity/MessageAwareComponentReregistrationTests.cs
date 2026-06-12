#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Unity
{
    using System.Collections;
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Tests.Runtime.Core;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using DxMessaging.Unity;
    using NUnit.Framework;
    using UnityEngine;
    using UnityEngine.TestTools;

    /// <summary>
    /// Covers <see cref="MessageAwareComponent.ReregisterOnEnableAfterRelease"/>: the opt-in
    /// virtual that lets a released listener re-create its token and replay
    /// <see cref="MessageAwareComponent.RegisterMessageHandlers"/> on the next enable.
    /// </summary>
    /// <remarks>
    /// Complements (does not duplicate):
    /// - <c>MessagingComponentLifecycleTests.DoubleReleaseReturnsFalseAndLeavesMessagingUsable</c>
    ///   pins that a released token stays dead and MANUAL re-creation works.
    /// - <c>MessageAwareComponentTogglingTests</c> pins plain enable/disable cycles without any
    ///   release in the mix.
    /// This fixture adds the release-then-enable matrix: default opt-out stays unregistered,
    /// opt-in resumes delivery, the replay happens exactly once per release, and releasing while
    /// disabled is recovered by the next enable.
    /// </remarks>
    public sealed class MessageAwareComponentReregistrationTests : MessagingTestBase
    {
        [UnityTest]
        public IEnumerator DefaultReleasedListenerStaysUnregisteredAcrossEnableCycles()
        {
            GameObject host = new(
                nameof(DefaultReleasedListenerStaysUnregisteredAcrossEnableCycles),
                typeof(MessagingComponent),
                typeof(ReregisteringMessageAwareComponent)
            );
            _spawned.Add(host);
            MessagingComponent messaging = host.GetComponent<MessagingComponent>();
            ReregisteringMessageAwareComponent listener =
                host.GetComponent<ReregisteringMessageAwareComponent>();
            listener.reregisterOnEnableAfterRelease = false;

            int count = 0;
            listener.untargetedHandler = () => ++count;

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted();
            Assert.AreEqual(1, count, "Positive control: listener should receive while active.");

            Assert.IsTrue(
                messaging.Release(listener),
                "Releasing the registered listener should succeed."
            );

            message.EmitUntargeted();
            Assert.AreEqual(1, count, "Released listener must stop receiving.");

            for (int cycle = 0; cycle < 2; ++cycle)
            {
                listener.enabled = false;
                listener.enabled = true;
                message.EmitUntargeted();
                Assert.AreEqual(
                    1,
                    count,
                    $"Cycle {cycle}: with the default opt-out, enable/disable cycles after a "
                        + "release must not resurrect registrations."
                );
            }

            Assert.AreEqual(
                1,
                listener.registerInvocationCount,
                "RegisterMessageHandlers must run only from Awake when the opt-in is off."
            );
            yield break;
        }

        [UnityTest]
        public IEnumerator OptInReregistersOnNextEnableAfterRelease()
        {
            GameObject host = new(
                nameof(OptInReregistersOnNextEnableAfterRelease),
                typeof(MessagingComponent),
                typeof(ReregisteringMessageAwareComponent)
            );
            _spawned.Add(host);
            MessagingComponent messaging = host.GetComponent<MessagingComponent>();
            ReregisteringMessageAwareComponent listener =
                host.GetComponent<ReregisteringMessageAwareComponent>();
            Assert.IsTrue(
                listener.reregisterOnEnableAfterRelease,
                "Precondition: the fixture component opts in by default."
            );

            int count = 0;
            listener.untargetedHandler = () => ++count;

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted();
            Assert.AreEqual(1, count, "Positive control: listener should receive while active.");

            // The watcher region starts AND ends with exactly one live registration: the
            // release -> re-register round-trip must net zero bus-side state. The warm emit
            // above also ensures the message type slot exists before the region opens.
            using (LeakWatcher watcher = LeakWatcher.Watch(label: "OptInReregister"))
            {
                MessageRegistrationToken originalToken = listener.Token;
                Assert.IsTrue(
                    messaging.Release(listener),
                    "Releasing the registered listener should succeed."
                );

                message.EmitUntargeted();
                Assert.AreEqual(
                    1,
                    count,
                    "Released listener must stop receiving until the next enable cycle."
                );

                listener.enabled = false;
                listener.enabled = true;

                message.EmitUntargeted();
                Assert.AreEqual(
                    2,
                    count,
                    "Opting in must re-register the released listener on the next enable."
                );
                Assert.AreNotSame(
                    originalToken,
                    listener.Token,
                    "Re-registration must mint a fresh token; the released token stays dead."
                );
                Assert.AreEqual(
                    2,
                    listener.registerInvocationCount,
                    "RegisterMessageHandlers must replay exactly once for the release."
                );

                listener.enabled = false;
                message.EmitUntargeted();
                Assert.AreEqual(
                    2,
                    count,
                    "The re-created token must still honor MessageRegistrationTiedToEnableStatus."
                );

                listener.enabled = true;
                message.EmitUntargeted();
                Assert.AreEqual(
                    3,
                    count,
                    "Re-enabling after re-registration must deliver exactly once per emit."
                );
                Assert.AreEqual(
                    2,
                    listener.registerInvocationCount,
                    "Plain enable cycles after the recovery replay must not re-register again."
                );
            }

            Assert.IsTrue(
                messaging.Release(listener),
                "Releasing the re-registered listener should succeed."
            );
            yield break;
        }

        [UnityTest]
        public IEnumerator OptInDoesNotReplayRegistrationsWithoutARelease()
        {
            GameObject host = new(
                nameof(OptInDoesNotReplayRegistrationsWithoutARelease),
                typeof(MessagingComponent),
                typeof(ReregisteringMessageAwareComponent)
            );
            _spawned.Add(host);
            ReregisteringMessageAwareComponent listener =
                host.GetComponent<ReregisteringMessageAwareComponent>();

            int count = 0;
            listener.untargetedHandler = () => ++count;

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted();
            Assert.AreEqual(1, count, "Positive control: listener should receive while active.");

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
                    $"Cycle {cycle}: emit after re-enable must deliver exactly once; a higher "
                        + "count means the opt-in replayed registrations without a release."
                );
            }

            Assert.AreEqual(
                1,
                listener.registerInvocationCount,
                "RegisterMessageHandlers must run only from Awake while the token is never released."
            );
            yield break;
        }

        [UnityTest]
        public IEnumerator OptInAdoptsManuallyRecreatedTokenWithoutReplaying()
        {
            GameObject host = new(
                nameof(OptInAdoptsManuallyRecreatedTokenWithoutReplaying),
                typeof(MessagingComponent),
                typeof(ReregisteringMessageAwareComponent)
            );
            _spawned.Add(host);
            MessagingComponent messaging = host.GetComponent<MessagingComponent>();
            ReregisteringMessageAwareComponent listener =
                host.GetComponent<ReregisteringMessageAwareComponent>();

            int componentCount = 0;
            listener.untargetedHandler = () => ++componentCount;

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted();
            Assert.AreEqual(
                1,
                componentCount,
                "Positive control: listener should receive while active."
            );

            Assert.IsTrue(
                messaging.Release(listener),
                "Releasing the registered listener should succeed."
            );

            // Manual recovery path: user code re-creates the token and stages its own
            // registration between the release and the next enable. The opt-in must
            // ADOPT this live token instead of keeping the disposed reference, and it
            // must NOT replay RegisterMessageHandlers onto it (the manual creator owns
            // staging).
            MessageRegistrationToken manualToken = messaging.Create(listener);
            int manualCount = 0;
            _ = manualToken.RegisterUntargeted<SimpleUntargetedMessage>(_ => ++manualCount);

            listener.enabled = false;
            listener.enabled = true;

            Assert.AreSame(
                manualToken,
                listener.Token,
                "Enable must adopt the manually re-created live token."
            );
            Assert.AreEqual(
                1,
                listener.registerInvocationCount,
                "Adopting a manual token must not replay RegisterMessageHandlers."
            );

            message.EmitUntargeted();
            Assert.AreEqual(
                1,
                manualCount,
                "The adopted token's manually staged registration must deliver."
            );
            Assert.AreEqual(
                1,
                componentCount,
                "The component handler was never staged on the manual token, so the "
                    + "component callback must not fire."
            );

            Assert.IsTrue(
                messaging.Release(listener),
                "Releasing the adopted listener should succeed."
            );
            yield break;
        }

        [UnityTest]
        public IEnumerator OptInWithRegistrationNotTiedToEnableStagesButDoesNotEnable()
        {
            GameObject host = new(
                nameof(OptInWithRegistrationNotTiedToEnableStagesButDoesNotEnable),
                typeof(MessagingComponent),
                typeof(ReregisteringMessageAwareComponent)
            );
            _spawned.Add(host);
            MessagingComponent messaging = host.GetComponent<MessagingComponent>();
            ReregisteringMessageAwareComponent listener =
                host.GetComponent<ReregisteringMessageAwareComponent>();
            listener.tieRegistrationToEnableStatus = false;

            int count = 0;
            listener.untargetedHandler = () => ++count;

            // With MessageRegistrationTiedToEnableStatus false, the token is never
            // auto-enabled; mirror the manual contract for the original token.
            listener.Token.Enable();

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted();
            Assert.AreEqual(1, count, "Positive control: manually enabled token delivers.");

            Assert.IsTrue(
                messaging.Release(listener),
                "Releasing the registered listener should succeed."
            );

            listener.enabled = false;
            listener.enabled = true;

            message.EmitUntargeted();
            Assert.AreEqual(
                1,
                count,
                "Recovery re-creates and stages the token, but with the lifecycle tie "
                    + "disabled nothing enables it automatically."
            );
            Assert.AreEqual(
                2,
                listener.registerInvocationCount,
                "The recovery replay must still have staged the registrations."
            );

            listener.Token.Enable();
            message.EmitUntargeted();
            Assert.AreEqual(
                2,
                count,
                "Manually enabling the recovered token must resume delivery."
            );

            Assert.IsTrue(
                messaging.Release(listener),
                "Releasing the recovered listener should succeed."
            );
            yield break;
        }

        [UnityTest]
        public IEnumerator OptInRecoversFromReleaseWhileDisabled()
        {
            GameObject host = new(
                nameof(OptInRecoversFromReleaseWhileDisabled),
                typeof(MessagingComponent),
                typeof(ReregisteringMessageAwareComponent)
            );
            _spawned.Add(host);
            MessagingComponent messaging = host.GetComponent<MessagingComponent>();
            ReregisteringMessageAwareComponent listener =
                host.GetComponent<ReregisteringMessageAwareComponent>();

            int count = 0;
            listener.untargetedHandler = () => ++count;

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted();
            Assert.AreEqual(1, count, "Positive control: listener should receive while active.");

            // Warm emit above creates the type slot before the region opens; the region then
            // starts and ends with one live registration so the recovery nets zero bus state.
            using (LeakWatcher watcher = LeakWatcher.Watch(label: "OptInReleaseWhileDisabled"))
            {
                listener.enabled = false;
                Assert.IsTrue(
                    messaging.Release(listener),
                    "Releasing a disabled listener should succeed."
                );

                message.EmitUntargeted();
                Assert.AreEqual(1, count, "Released and disabled listener must not receive.");

                listener.enabled = true;
                message.EmitUntargeted();
                Assert.AreEqual(
                    2,
                    count,
                    "Enabling after a release-while-disabled must re-register and resume delivery."
                );
            }

            Assert.IsTrue(
                messaging.Release(listener),
                "Releasing the recovered listener should succeed."
            );
            yield break;
        }
    }
}

#endif
