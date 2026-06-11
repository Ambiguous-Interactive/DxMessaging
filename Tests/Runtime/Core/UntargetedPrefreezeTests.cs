namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;

    /// <summary>
    /// Public-behavior pins for the untargeted emission-freeze contract.
    /// Historically this file pinned the internal prefreeze invocation
    /// counter (+1 per emission). The P0 dispatch-flattening redesign
    /// resolved every untargeted registration at snapshot-build time, so the
    /// per-handler prefreeze stamping no longer exists; these tests pin the
    /// PUBLIC semantics the stamping used to guarantee instead:
    /// mutations performed during an emission are not observed until the
    /// next emission, and the post-process snapshot is captured before
    /// interceptors run.
    /// </summary>
    public sealed class UntargetedPrefreezeTests
    {
        [Test]
        public void PostProcessorRegisteredDuringHandlerDoesNotFireSameEmission()
        {
            MessageHandler handler = new(new InstanceId(123)) { active = true };
            MessageBus messageBus = new();
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, messageBus);

            int existingPostProcessCount = 0;
            int latePostProcessCount = 0;
            bool registeredLate = false;

            _ = token.RegisterUntargeted(
                (ref SimpleUntargetedMessage message) =>
                {
                    if (registeredLate)
                    {
                        return;
                    }

                    registeredLate = true;
                    _ = token.RegisterUntargetedPostProcessor(
                        (ref SimpleUntargetedMessage _) => latePostProcessCount++,
                        priority: 0
                    );
                }
            );
            _ = token.RegisterUntargetedPostProcessor(
                (ref SimpleUntargetedMessage _) => existingPostProcessCount++,
                priority: 0
            );

            token.Enable();

            SimpleUntargetedMessage message = new();
            messageBus.UntargetedBroadcast(ref message);
            Assert.AreEqual(1, existingPostProcessCount);
            Assert.AreEqual(
                0,
                latePostProcessCount,
                "A post-processor registered during handler execution must not fire "
                    + "within the emission that registered it; the post-process snapshot "
                    + "is frozen at emission start."
            );

            messageBus.UntargetedBroadcast(ref message);
            Assert.AreEqual(2, existingPostProcessCount);
            Assert.AreEqual(
                1,
                latePostProcessCount,
                "A post-processor registered during a previous emission must fire on "
                    + "the next emission."
            );

            token.Disable();
        }

        [Test]
        public void PostProcessorRegisteredByInterceptorDoesNotFireSameEmission()
        {
            MessageHandler handler = new(new InstanceId(124)) { active = true };
            MessageBus messageBus = new();
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, messageBus);

            int existingPostProcessCount = 0;
            int latePostProcessCount = 0;
            bool registeredLate = false;

            // Register the interceptor directly through the MessageHandler so
            // it lands on THIS bus:
            // MessageRegistrationToken.RegisterUntargetedInterceptor does not
            // forward the token's bus and always registers on the global bus
            // (pre-existing token behavior, flagged for API review).
            Action interceptorDeregistration =
                handler.RegisterUntargetedInterceptor<SimpleUntargetedMessage>(
                    (ref SimpleUntargetedMessage message) =>
                    {
                        if (!registeredLate)
                        {
                            registeredLate = true;
                            _ = token.RegisterUntargetedPostProcessor(
                                (ref SimpleUntargetedMessage _) => latePostProcessCount++,
                                priority: 0
                            );
                        }

                        return true;
                    },
                    priority: 0,
                    messageBus: messageBus
                );
            _ = token.RegisterUntargeted((ref SimpleUntargetedMessage _) => { });
            _ = token.RegisterUntargetedPostProcessor(
                (ref SimpleUntargetedMessage _) => existingPostProcessCount++,
                priority: 0
            );

            token.Enable();

            SimpleUntargetedMessage message = new();
            messageBus.UntargetedBroadcast(ref message);
            Assert.AreEqual(1, existingPostProcessCount);
            Assert.AreEqual(
                0,
                latePostProcessCount,
                "The post-process snapshot is captured BEFORE interceptors run; a "
                    + "post-processor registered from an interceptor must not fire within "
                    + "the same emission."
            );

            messageBus.UntargetedBroadcast(ref message);
            Assert.AreEqual(2, existingPostProcessCount);
            Assert.AreEqual(
                1,
                latePostProcessCount,
                "A post-processor registered from an interceptor during a previous "
                    + "emission must fire on the next emission."
            );

            interceptorDeregistration();
            token.Disable();
        }

        [Test]
        public void PostProcessorDeregisteredDuringHandlerStillFiresSameEmission()
        {
            MessageHandler handler = new(new InstanceId(125)) { active = true };
            MessageBus messageBus = new();
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, messageBus);

            int removedPostProcessCount = 0;
            int peerPostProcessCount = 0;
            MessageRegistrationHandle postHandle = token.RegisterUntargetedPostProcessor(
                (ref SimpleUntargetedMessage _) => removedPostProcessCount++,
                priority: 0
            );
            _ = token.RegisterUntargetedPostProcessor(
                (ref SimpleUntargetedMessage _) => peerPostProcessCount++,
                priority: 0
            );

            bool removed = false;
            _ = token.RegisterUntargeted(
                (ref SimpleUntargetedMessage _) =>
                {
                    if (!removed)
                    {
                        removed = true;
                        token.RemoveRegistration(postHandle);
                    }
                }
            );

            token.Enable();

            SimpleUntargetedMessage message = new();
            messageBus.UntargetedBroadcast(ref message);
            Assert.AreEqual(
                1,
                removedPostProcessCount,
                "A post-processor deregistered during handler execution still fires "
                    + "within that emission; the frozen snapshot is immutable mid-emission."
            );
            Assert.AreEqual(1, peerPostProcessCount);

            messageBus.UntargetedBroadcast(ref message);
            Assert.AreEqual(
                1,
                removedPostProcessCount,
                "A post-processor deregistered during a previous emission must not "
                    + "fire on subsequent emissions."
            );
            Assert.AreEqual(2, peerPostProcessCount);

            token.Disable();
        }

        /// <summary>
        /// Edge pin (pre-existing public behavior, preserved by the
        /// flattening redesign): when the ONLY remaining untargeted
        /// post-processor for a message type is deregistered during the
        /// handler phase, the post-process phase is skipped for that
        /// emission entirely. The bus re-reads the live post-process sink
        /// count after the handler phase, and the sink is empty by then, so
        /// the frozen snapshot is never consulted. This differs from the
        /// peer-remains case above, where the frozen snapshot still fires
        /// the deregistered entry.
        /// </summary>
        [Test]
        public void LastPostProcessorDeregisteredDuringHandlerSkipsPostPhaseThatEmission()
        {
            MessageHandler handler = new(new InstanceId(127)) { active = true };
            MessageBus messageBus = new();
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, messageBus);

            int postProcessCount = 0;
            MessageRegistrationHandle postHandle = token.RegisterUntargetedPostProcessor(
                (ref SimpleUntargetedMessage _) => postProcessCount++,
                priority: 0
            );

            bool removed = false;
            _ = token.RegisterUntargeted(
                (ref SimpleUntargetedMessage _) =>
                {
                    if (!removed)
                    {
                        removed = true;
                        token.RemoveRegistration(postHandle);
                    }
                }
            );

            token.Enable();

            SimpleUntargetedMessage message = new();
            messageBus.UntargetedBroadcast(ref message);
            Assert.AreEqual(
                0,
                postProcessCount,
                "Deregistering the LAST untargeted post-processor during the handler "
                    + "phase empties the live post-process sink, so the post phase is "
                    + "skipped for that emission (live count gate runs before the frozen "
                    + "snapshot is consulted)."
            );

            messageBus.UntargetedBroadcast(ref message);
            Assert.AreEqual(0, postProcessCount);

            token.Disable();
        }

        [Test]
        public void HandlerRegisteredDuringHandlerDoesNotFireSameEmissionButFiresNext()
        {
            MessageHandler handler = new(new InstanceId(126)) { active = true };
            MessageBus messageBus = new();
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, messageBus);

            int lateHandlerCount = 0;
            bool registeredLate = false;

            _ = token.RegisterUntargeted(
                (ref SimpleUntargetedMessage message) =>
                {
                    if (registeredLate)
                    {
                        return;
                    }

                    registeredLate = true;
                    _ = token.RegisterUntargeted(
                        (ref SimpleUntargetedMessage _) => lateHandlerCount++,
                        priority: 100
                    );
                }
            );

            token.Enable();

            SimpleUntargetedMessage message = new();
            messageBus.UntargetedBroadcast(ref message);
            Assert.AreEqual(
                0,
                lateHandlerCount,
                "A handler registered during an emission (even at a not-yet-dispatched "
                    + "higher priority) must not fire within that emission."
            );

            messageBus.UntargetedBroadcast(ref message);
            Assert.AreEqual(1, lateHandlerCount);

            token.Disable();
        }
    }
}
