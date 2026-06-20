#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System.Collections.Generic;
    using System.Linq;
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using UnityEngine;

    /// <summary>
    /// Stress exercises for mid-emission listener growth. Each scenario creates a large number of initial listeners
    /// that register multiple additional listeners during the first emission. The new listeners must not observe the
    /// emission they were created in, but must participate in subsequent emissions. These tests cover normal handlers,
    /// interceptors, global accept-all registrations, and post-processors.
    /// </summary>
    [Category("Stress")]
    public sealed class MutationHighVolumeStressTests : MessagingTestBase
    {
        private const int InitialListenerCount = 32;
        private const int NewListenersPerHandler = 3;

        [Test]
        public void UntargetedHandlersRegisterMultipleListeners()
        {
            List<(MessageRegistrationToken token, MessageRegistrationHandle handle)> registrations =
                new();
            int[] baseCounts = new int[InitialListenerCount];
            List<int> dynamicCounts = new();

            for (int i = 0; i < InitialListenerCount; ++i)
            {
                EmptyMessageAwareComponent component = CreateComponent("UntargetedBase", i);
                MessageRegistrationToken token = GetToken(component);
                bool expanded = false;
                int capturedIndex = i;

                MessageRegistrationHandle baseHandle =
                    token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) =>
                        {
                            baseCounts[capturedIndex]++;
                            if (expanded)
                            {
                                return;
                            }

                            expanded = true;
                            for (int n = 0; n < NewListenersPerHandler; ++n)
                            {
                                int dynamicIndex = dynamicCounts.Count;
                                dynamicCounts.Add(0);
                                EmptyMessageAwareComponent extraComponent = CreateComponent(
                                    $"UntargetedExtra_{capturedIndex}_{n}",
                                    dynamicIndex
                                );
                                MessageRegistrationToken extraToken = GetToken(extraComponent);
                                MessageRegistrationHandle extraHandle =
                                    extraToken.RegisterUntargeted<SimpleUntargetedMessage>(
                                        (ref SimpleUntargetedMessage _) =>
                                        {
                                            dynamicCounts[dynamicIndex]++;
                                        }
                                    );
                                registrations.Add((extraToken, extraHandle));
                            }
                        }
                    );

                registrations.Add((token, baseHandle));
            }

            SimpleUntargetedMessage message = new();

            message.EmitUntargeted();
            Assert.That(
                dynamicCounts.Count,
                Is.EqualTo(InitialListenerCount * NewListenersPerHandler)
            );
            CollectionAssert.AreEqual(
                Enumerable.Repeat(1, InitialListenerCount),
                baseCounts,
                "Baseline handlers should run exactly once on the first emission."
            );
            CollectionAssert.AreEqual(
                Enumerable.Repeat(0, dynamicCounts.Count),
                dynamicCounts,
                "Newly registered handlers must not observe the emission that registered them."
            );

            message.EmitUntargeted();
            CollectionAssert.AreEqual(
                Enumerable.Repeat(2, InitialListenerCount),
                baseCounts,
                "Baseline handlers should continue observing each emission."
            );
            CollectionAssert.AreEqual(
                Enumerable.Repeat(1, dynamicCounts.Count),
                dynamicCounts,
                "Newly registered handlers must activate starting with the next emission."
            );

            RemoveRegistrations(registrations);
        }

        [Test]
        public void UntargetedInterceptorsRegisterMultipleInterceptors()
        {
            List<(MessageRegistrationToken token, MessageRegistrationHandle handle)> registrations =
                new();
            int[] baseCounts = new int[InitialListenerCount];
            List<int> dynamicCounts = new();

            for (int i = 0; i < InitialListenerCount; ++i)
            {
                EmptyMessageAwareComponent component = CreateComponent("InterceptorBase", i);
                MessageRegistrationToken token = GetToken(component);
                bool expanded = false;
                int capturedIndex = i;

                MessageRegistrationHandle baseHandle =
                    token.RegisterUntargetedInterceptor<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) =>
                        {
                            baseCounts[capturedIndex]++;
                            if (!expanded)
                            {
                                expanded = true;
                                for (int n = 0; n < NewListenersPerHandler; ++n)
                                {
                                    int dynamicIndex = dynamicCounts.Count;
                                    dynamicCounts.Add(0);
                                    EmptyMessageAwareComponent extraComponent = CreateComponent(
                                        $"InterceptorExtra_{capturedIndex}_{n}",
                                        dynamicIndex
                                    );
                                    MessageRegistrationToken extraToken = GetToken(extraComponent);
                                    MessageRegistrationHandle extraHandle =
                                        extraToken.RegisterUntargetedInterceptor<SimpleUntargetedMessage>(
                                            (ref SimpleUntargetedMessage _) =>
                                            {
                                                dynamicCounts[dynamicIndex]++;
                                                return true;
                                            }
                                        );
                                    registrations.Add((extraToken, extraHandle));
                                }
                            }

                            return true;
                        }
                    );
                registrations.Add((token, baseHandle));
            }

            // Ensure at least one regular handler executes so the emission is fully processed.
            EmptyMessageAwareComponent sinkComponent = CreateComponent("InterceptorSink", -1);
            MessageRegistrationToken sinkToken = GetToken(sinkComponent);
            MessageRegistrationHandle sinkHandle =
                sinkToken.RegisterUntargeted<SimpleUntargetedMessage>(
                    (ref SimpleUntargetedMessage _) => { }
                );
            registrations.Add((sinkToken, sinkHandle));

            SimpleUntargetedMessage message = new();

            message.EmitUntargeted();
            Assert.That(
                dynamicCounts.Count,
                Is.EqualTo(InitialListenerCount * NewListenersPerHandler)
            );
            CollectionAssert.AreEqual(
                Enumerable.Repeat(1, InitialListenerCount),
                baseCounts,
                "Baseline interceptors should run once on the first emission."
            );
            CollectionAssert.AreEqual(
                Enumerable.Repeat(0, dynamicCounts.Count),
                dynamicCounts,
                "New interceptors must not run until the next emission."
            );

            message.EmitUntargeted();
            CollectionAssert.AreEqual(
                Enumerable.Repeat(2, InitialListenerCount),
                baseCounts,
                "Baseline interceptors should continue observing each emission."
            );
            CollectionAssert.AreEqual(
                Enumerable.Repeat(1, dynamicCounts.Count),
                dynamicCounts,
                "Newly registered interceptors must activate starting with the next emission."
            );

            RemoveRegistrations(registrations);
        }

        [Test]
        public void GlobalHandlersRegisterMultipleListeners()
        {
            List<(MessageRegistrationToken token, MessageRegistrationHandle handle)> registrations =
                new();
            int[] baseUntargetedCounts = new int[InitialListenerCount];
            int[] baseTargetedCounts = new int[InitialListenerCount];
            int[] baseBroadcastCounts = new int[InitialListenerCount];
            List<int> dynamicUntargetedCounts = new();
            List<int> dynamicTargetedCounts = new();
            List<int> dynamicBroadcastCounts = new();

            GameObject emissionTarget = new(
                "GlobalStressTarget",
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(emissionTarget);

            for (int i = 0; i < InitialListenerCount; ++i)
            {
                EmptyMessageAwareComponent component = CreateComponent("GlobalBase", i);
                MessageRegistrationToken token = GetToken(component);
                bool expanded = false;
                int capturedIndex = i;

                MessageRegistrationHandle baseHandle = token.RegisterGlobalAcceptAll(
                    (ref IUntargetedMessage _) =>
                    {
                        baseUntargetedCounts[capturedIndex]++;
                        ExpandIfNeeded();
                    },
                    (ref InstanceId _, ref ITargetedMessage _) =>
                    {
                        baseTargetedCounts[capturedIndex]++;
                    },
                    (ref InstanceId _, ref IBroadcastMessage _) =>
                    {
                        baseBroadcastCounts[capturedIndex]++;
                    }
                );
                registrations.Add((token, baseHandle));

                void ExpandIfNeeded()
                {
                    if (expanded)
                    {
                        return;
                    }

                    expanded = true;
                    for (int n = 0; n < NewListenersPerHandler; ++n)
                    {
                        int dynamicIndex = dynamicUntargetedCounts.Count;
                        dynamicUntargetedCounts.Add(0);
                        dynamicTargetedCounts.Add(0);
                        dynamicBroadcastCounts.Add(0);
                        EmptyMessageAwareComponent extraComponent = CreateComponent(
                            $"GlobalExtra_{capturedIndex}_{n}",
                            dynamicIndex
                        );
                        MessageRegistrationToken extraToken = GetToken(extraComponent);
                        MessageRegistrationHandle extraHandle = extraToken.RegisterGlobalAcceptAll(
                            (ref IUntargetedMessage _) =>
                            {
                                dynamicUntargetedCounts[dynamicIndex]++;
                            },
                            (ref InstanceId _, ref ITargetedMessage _) =>
                            {
                                dynamicTargetedCounts[dynamicIndex]++;
                            },
                            (ref InstanceId _, ref IBroadcastMessage _) =>
                            {
                                dynamicBroadcastCounts[dynamicIndex]++;
                            }
                        );
                        registrations.Add((extraToken, extraHandle));
                    }
                }
            }

            SimpleUntargetedMessage untargetedMessage = new();
            SimpleTargetedMessage targetedMessage = new();
            SimpleBroadcastMessage broadcastMessage = new();

            EmitGlobalMessages(
                emissionTarget,
                untargetedMessage,
                targetedMessage,
                broadcastMessage
            );

            Assert.That(
                dynamicUntargetedCounts.Count,
                Is.EqualTo(InitialListenerCount * NewListenersPerHandler)
            );
            CollectionAssert.AreEqual(
                Enumerable.Repeat(1, InitialListenerCount),
                baseUntargetedCounts,
                "Baseline global untargeted listeners should run once on the first emission."
            );
            CollectionAssert.AreEqual(
                Enumerable.Repeat(1, InitialListenerCount),
                baseTargetedCounts,
                "Baseline global targeted listeners should run once on the first emission."
            );
            CollectionAssert.AreEqual(
                Enumerable.Repeat(1, InitialListenerCount),
                baseBroadcastCounts,
                "Baseline global broadcast listeners should run once on the first emission."
            );
            CollectionAssert.AreEqual(
                Enumerable.Repeat(0, dynamicUntargetedCounts.Count),
                dynamicUntargetedCounts,
                "New global listeners must not run during the emission that created them (untargeted)."
            );
            CollectionAssert.AreEqual(
                Enumerable.Repeat(1, dynamicTargetedCounts.Count),
                dynamicTargetedCounts,
                "New global listeners should activate on the first targeted emission after registration."
            );
            CollectionAssert.AreEqual(
                Enumerable.Repeat(1, dynamicBroadcastCounts.Count),
                dynamicBroadcastCounts,
                "New global listeners should activate on the first broadcast emission after registration."
            );

            EmitGlobalMessages(
                emissionTarget,
                untargetedMessage,
                targetedMessage,
                broadcastMessage
            );

            CollectionAssert.AreEqual(
                Enumerable.Repeat(2, InitialListenerCount),
                baseUntargetedCounts,
                "Baseline global untargeted listeners should run again on subsequent emissions."
            );
            CollectionAssert.AreEqual(
                Enumerable.Repeat(2, InitialListenerCount),
                baseTargetedCounts,
                "Baseline global targeted listeners should run again on subsequent emissions."
            );
            CollectionAssert.AreEqual(
                Enumerable.Repeat(2, InitialListenerCount),
                baseBroadcastCounts,
                "Baseline global broadcast listeners should run again on subsequent emissions."
            );
            CollectionAssert.AreEqual(
                Enumerable.Repeat(1, dynamicUntargetedCounts.Count),
                dynamicUntargetedCounts,
                "New global listeners must activate on the next emission (untargeted)."
            );
            CollectionAssert.AreEqual(
                Enumerable.Repeat(2, dynamicTargetedCounts.Count),
                dynamicTargetedCounts,
                "New global listeners should continue observing subsequent targeted emissions."
            );
            CollectionAssert.AreEqual(
                Enumerable.Repeat(2, dynamicBroadcastCounts.Count),
                dynamicBroadcastCounts,
                "New global listeners should continue observing subsequent broadcast emissions."
            );

            RemoveRegistrations(registrations);
        }

        [Test]
        public void UntargetedPostProcessorsRegisterMultiplePostProcessors()
        {
            List<(MessageRegistrationToken token, MessageRegistrationHandle handle)> registrations =
                new();
            int[] baseHandlerCounts = new int[InitialListenerCount];
            int[] basePostCounts = new int[InitialListenerCount];
            List<int> dynamicPostCounts = new();

            for (int i = 0; i < InitialListenerCount; ++i)
            {
                EmptyMessageAwareComponent component = CreateComponent("PostBase", i);
                MessageRegistrationToken token = GetToken(component);
                bool expanded = false;
                int capturedIndex = i;

                MessageRegistrationHandle handlerHandle =
                    token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) =>
                        {
                            baseHandlerCounts[capturedIndex]++;
                        }
                    );
                registrations.Add((token, handlerHandle));

                MessageRegistrationHandle postHandle =
                    token.RegisterUntargetedPostProcessor<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) =>
                        {
                            basePostCounts[capturedIndex]++;
                            if (expanded)
                            {
                                return;
                            }

                            expanded = true;
                            for (int n = 0; n < NewListenersPerHandler; ++n)
                            {
                                int dynamicIndex = dynamicPostCounts.Count;
                                dynamicPostCounts.Add(0);
                                EmptyMessageAwareComponent extraComponent = CreateComponent(
                                    $"PostExtra_{capturedIndex}_{n}",
                                    dynamicIndex
                                );
                                MessageRegistrationToken extraToken = GetToken(extraComponent);
                                MessageRegistrationHandle extraHandle =
                                    extraToken.RegisterUntargetedPostProcessor<SimpleUntargetedMessage>(
                                        (ref SimpleUntargetedMessage _) =>
                                        {
                                            dynamicPostCounts[dynamicIndex]++;
                                        }
                                    );
                                registrations.Add((extraToken, extraHandle));
                            }
                        }
                    );
                registrations.Add((token, postHandle));
            }

            SimpleUntargetedMessage message = new();

            message.EmitUntargeted();
            Assert.That(
                dynamicPostCounts.Count,
                Is.EqualTo(InitialListenerCount * NewListenersPerHandler)
            );
            CollectionAssert.AreEqual(
                Enumerable.Repeat(1, InitialListenerCount),
                baseHandlerCounts,
                "Baseline handlers should run once per emission."
            );
            CollectionAssert.AreEqual(
                Enumerable.Repeat(1, InitialListenerCount),
                basePostCounts,
                "Baseline post-processors should run once on the first emission."
            );
            CollectionAssert.AreEqual(
                Enumerable.Repeat(0, dynamicPostCounts.Count),
                dynamicPostCounts,
                "Newly registered post-processors must not observe the emission that created them."
            );

            message.EmitUntargeted();
            CollectionAssert.AreEqual(
                Enumerable.Repeat(2, InitialListenerCount),
                baseHandlerCounts,
                "Baseline handlers should run again on the second emission."
            );
            CollectionAssert.AreEqual(
                Enumerable.Repeat(2, InitialListenerCount),
                basePostCounts,
                "Baseline post-processors should continue running on each emission."
            );
            CollectionAssert.AreEqual(
                Enumerable.Repeat(1, dynamicPostCounts.Count),
                dynamicPostCounts,
                "Newly registered post-processors must activate starting with the next emission."
            );

            RemoveRegistrations(registrations);
        }

        private EmptyMessageAwareComponent CreateComponent(string prefix, int index)
        {
            GameObject go = new($"{prefix}_{index}", typeof(EmptyMessageAwareComponent));
            _spawned.Add(go);
            return go.GetComponent<EmptyMessageAwareComponent>();
        }

        private static void RemoveRegistrations(
            IReadOnlyList<(
                MessageRegistrationToken token,
                MessageRegistrationHandle handle
            )> registrations
        )
        {
            for (int i = registrations.Count - 1; i >= 0; --i)
            {
                (MessageRegistrationToken token, MessageRegistrationHandle handle) = registrations[
                    i
                ];
                token.RemoveRegistration(handle);
            }
        }

        private static void EmitGlobalMessages(
            GameObject targetObject,
            SimpleUntargetedMessage untargeted,
            SimpleTargetedMessage targeted,
            SimpleBroadcastMessage broadcast
        )
        {
            untargeted.EmitUntargeted();
            targeted.EmitGameObjectTargeted(targetObject);
            broadcast.EmitGameObjectBroadcast(targetObject);
        }
    }
}

#endif
