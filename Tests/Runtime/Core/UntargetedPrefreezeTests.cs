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
        /// The final post-processor follows the same frozen-snapshot rule as
        /// a removed processor that still has a peer: deregistration during
        /// the handler phase takes effect on the next emission.
        /// </summary>
        [Test]
        public void LastPostProcessorDeregisteredDuringHandlerStillFiresSameEmission()
        {
            MessageHandler handler = new(new InstanceId(127)) { active = true };
            MessageBus messageBus = new();
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, messageBus);

            int postProcessCount = 0;
            int firstEmissionCount;
            int secondEmissionCount;
            using (
                LeakWatcher watcher = new LeakWatcher(
                    messageBus,
                    label: nameof(SimpleUntargetedMessage)
                )
            )
            {
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
                firstEmissionCount = postProcessCount;
                messageBus.UntargetedBroadcast(ref message);
                secondEmissionCount = postProcessCount;
                token.Disable();
            }

            Assert.AreEqual(
                1,
                firstEmissionCount,
                "Deregistering the final untargeted post-processor during the handler "
                    + "phase must not change the frozen post-process snapshot. "
                    + "firstEmissionCount={0}, secondEmissionCount={1}.",
                firstEmissionCount,
                secondEmissionCount
            );
            Assert.AreEqual(
                1,
                secondEmissionCount,
                "The final post-processor removed during the previous emission must not run "
                    + "again. firstEmissionCount={0}, secondEmissionCount={1}.",
                firstEmissionCount,
                secondEmissionCount
            );
        }

        [Test]
        public void NestedPlanRefreshDoesNotReplaceOuterBorrowedPostRoute()
        {
            MessageHandler handler = new(new InstanceId(128)) { active = true };
            MessageBus messageBus = new();
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, messageBus);
            using LeakWatcher watcher = new(
                messageBus,
                label: nameof(NestedPlanRefreshDoesNotReplaceOuterBorrowedPostRoute)
            );
            int depth = 0;
            bool registeredLate = false;
            System.Collections.Generic.List<string> trace = new(8);

            _ = token.RegisterUntargeted(
                (ref SimpleUntargetedMessage message) =>
                {
                    trace.Add($"d{depth}:handle");
                    if (depth != 0 || registeredLate)
                    {
                        return;
                    }

                    registeredLate = true;
                    _ = token.RegisterUntargetedPostProcessor(
                        (ref SimpleUntargetedMessage _) => trace.Add($"d{depth}:late-post"),
                        priority: 1
                    );
                    ++depth;
                    try
                    {
                        messageBus.UntargetedBroadcast(ref message);
                    }
                    finally
                    {
                        --depth;
                    }
                }
            );
            _ = token.RegisterUntargetedPostProcessor(
                (ref SimpleUntargetedMessage _) => trace.Add($"d{depth}:existing-post"),
                priority: 0
            );
            token.Enable();

            SimpleUntargetedMessage message = new();
            Assert.DoesNotThrow(
                () => messageBus.UntargetedBroadcast(ref message),
                "A nested same-type plan refresh must not invalidate the outer emission's "
                    + "borrowed post route. trace=[{0}]",
                string.Join(",", trace)
            );

            CollectionAssert.AreEqual(
                new[]
                {
                    "d0:handle",
                    "d1:handle",
                    "d1:existing-post",
                    "d1:late-post",
                    "d0:existing-post",
                },
                trace,
                "The nested emission must use the refreshed post route while the outer "
                    + "emission resumes its original frozen route. trace=[{0}]",
                string.Join(",", trace)
            );

            token.Disable();
            handler.active = false;
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
