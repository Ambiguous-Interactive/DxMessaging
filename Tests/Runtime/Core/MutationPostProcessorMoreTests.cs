#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class MutationPostProcessorMoreTests : MessagingTestBase
    {
        public enum PostProcessorVariant
        {
            Untargeted,
            Targeted,
            Broadcast,
            TargetedWithoutTargeting,
            BroadcastWithoutSource,
        }

        public static IEnumerable<PostProcessorVariant> AllVariants
        {
            get
            {
                yield return PostProcessorVariant.Untargeted;
                yield return PostProcessorVariant.Targeted;
                yield return PostProcessorVariant.Broadcast;
                yield return PostProcessorVariant.TargetedWithoutTargeting;
                yield return PostProcessorVariant.BroadcastWithoutSource;
            }
        }

        public static IEnumerable<PostProcessorVariant> SourceFilteredVariants
        {
            get
            {
                yield return PostProcessorVariant.Targeted;
                yield return PostProcessorVariant.Broadcast;
            }
        }

        [Test]
        public void BaselineFirstEmissionFiresHandlerAndPostProcessor(
            [ValueSource(nameof(AllVariants))] PostProcessorVariant variant
        )
        {
            GameObject host = new(
                nameof(BaselineFirstEmissionFiresHandlerAndPostProcessor) + "_" + variant,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int handlerCount = 0;
            int ppCount = 0;
            _ = RegisterHandler(variant, token, hostId, () => ++handlerCount);
            _ = RegisterPostProcessor(variant, token, hostId, () => ++ppCount);

            Emit(variant, host);

            Assert.AreEqual(
                1,
                handlerCount,
                "[{0}] Baseline handler must fire exactly once on first emission. sourceId={1}, handlerCount={2}.",
                variant,
                hostId.Id,
                handlerCount
            );
            Assert.AreEqual(
                1,
                ppCount,
                "[{0}] Baseline post-processor must fire exactly once on first emission. sourceId={1}, ppCount={2}.",
                variant,
                hostId.Id,
                ppCount
            );
        }

        [Test]
        public void AddPostProcessorDuringHandlerDoesNotRunInSameEmission(
            [ValueSource(nameof(AllVariants))] PostProcessorVariant variant
        )
        {
            GameObject host = new(
                nameof(AddPostProcessorDuringHandlerDoesNotRunInSameEmission) + "_" + variant,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int originalPpCount = 0;
            int newPpCount = 0;
            bool added = false;

            _ = RegisterHandler(
                variant,
                token,
                hostId,
                () =>
                {
                    if (added)
                    {
                        return;
                    }

                    added = true;
                    _ = RegisterPostProcessor(variant, token, hostId, () => ++newPpCount);
                }
            );
            _ = RegisterPostProcessor(variant, token, hostId, () => ++originalPpCount);

            Emit(variant, host);

            Assert.AreEqual(
                1,
                originalPpCount,
                "[{0}] Original post-processor must run exactly once on first emission. sourceId={1}, originalPpCount={2}, newPpCount={3}.",
                variant,
                hostId.Id,
                originalPpCount,
                newPpCount
            );
            Assert.AreEqual(
                0,
                newPpCount,
                "[{0}] Post-processor registered during handler dispatch must not fire on the in-flight emission. sourceId={1}, originalPpCount={2}, newPpCount={3}.",
                variant,
                hostId.Id,
                originalPpCount,
                newPpCount
            );

            Emit(variant, host);

            Assert.AreEqual(
                2,
                originalPpCount,
                "[{0}] Original post-processor must run again on second emission. sourceId={1}, originalPpCount={2}, newPpCount={3}.",
                variant,
                hostId.Id,
                originalPpCount,
                newPpCount
            );
            Assert.AreEqual(
                1,
                newPpCount,
                "[{0}] Newly added post-processor must run starting on the second emission. sourceId={1}, originalPpCount={2}, newPpCount={3}.",
                variant,
                hostId.Id,
                originalPpCount,
                newPpCount
            );
        }

        [Test]
        public void AddPostProcessorAtDifferentPriorityDuringHandlerDoesNotRunInSameEmission(
            [ValueSource(nameof(AllVariants))] PostProcessorVariant variant
        )
        {
            GameObject host = new(
                nameof(AddPostProcessorAtDifferentPriorityDuringHandlerDoesNotRunInSameEmission)
                    + "_"
                    + variant,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int originalPpCount = 0;
            int newPpCount = 0;
            bool added = false;

            _ = RegisterHandler(
                variant,
                token,
                hostId,
                () =>
                {
                    if (added)
                    {
                        return;
                    }

                    added = true;
                    _ = RegisterPostProcessor(
                        variant,
                        token,
                        hostId,
                        () => ++newPpCount,
                        priority: 7
                    );
                }
            );
            _ = RegisterPostProcessor(variant, token, hostId, () => ++originalPpCount, priority: 0);

            Emit(variant, host);

            Assert.AreEqual(
                1,
                originalPpCount,
                "[{0}] Original post-processor must run exactly once on first emission. sourceId={1}, originalPpCount={2}, newPpCount={3}.",
                variant,
                hostId.Id,
                originalPpCount,
                newPpCount
            );
            Assert.AreEqual(
                0,
                newPpCount,
                "[{0}] Post-processor registered at a new priority during dispatch must not fire on the in-flight emission. sourceId={1}, originalPpCount={2}, newPpCount={3}.",
                variant,
                hostId.Id,
                originalPpCount,
                newPpCount
            );

            Emit(variant, host);

            Assert.AreEqual(
                2,
                originalPpCount,
                "[{0}] Original post-processor must run again on second emission. sourceId={1}, originalPpCount={2}, newPpCount={3}.",
                variant,
                hostId.Id,
                originalPpCount,
                newPpCount
            );
            Assert.AreEqual(
                1,
                newPpCount,
                "[{0}] Newly added post-processor at a new priority must run starting on the second emission. sourceId={1}, originalPpCount={2}, newPpCount={3}.",
                variant,
                hostId.Id,
                originalPpCount,
                newPpCount
            );
        }

        [Test]
        public void AddManyPostProcessorsDuringHandlerNoneRunInSameEmission(
            [ValueSource(nameof(AllVariants))] PostProcessorVariant variant
        )
        {
            const int NewCount = 4;

            GameObject host = new(
                nameof(AddManyPostProcessorsDuringHandlerNoneRunInSameEmission) + "_" + variant,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int originalPpCount = 0;
            int[] newCounts = new int[NewCount];
            bool added = false;

            _ = RegisterHandler(
                variant,
                token,
                hostId,
                () =>
                {
                    if (added)
                    {
                        return;
                    }

                    added = true;
                    for (int i = 0; i < NewCount; i++)
                    {
                        int idx = i;
                        _ = RegisterPostProcessor(variant, token, hostId, () => ++newCounts[idx]);
                    }
                }
            );
            _ = RegisterPostProcessor(variant, token, hostId, () => ++originalPpCount);

            Emit(variant, host);

            Assert.AreEqual(
                1,
                originalPpCount,
                "[{0}] Original post-processor must run exactly once on first emission. sourceId={1}, originalPpCount={2}.",
                variant,
                hostId.Id,
                originalPpCount
            );
            for (int i = 0; i < NewCount; i++)
            {
                Assert.AreEqual(
                    0,
                    newCounts[i],
                    "[{0}] Post-processor #{1} registered during handler dispatch must not fire on the in-flight emission. sourceId={2}, count={3}.",
                    variant,
                    i,
                    hostId.Id,
                    newCounts[i]
                );
            }

            Emit(variant, host);

            Assert.AreEqual(
                2,
                originalPpCount,
                "[{0}] Original post-processor must run again on second emission. sourceId={1}, originalPpCount={2}.",
                variant,
                hostId.Id,
                originalPpCount
            );
            for (int i = 0; i < NewCount; i++)
            {
                Assert.AreEqual(
                    1,
                    newCounts[i],
                    "[{0}] Newly added post-processor #{1} must run starting on the second emission. sourceId={2}, count={3}.",
                    variant,
                    i,
                    hostId.Id,
                    newCounts[i]
                );
            }
        }

        [Test]
        public void AddPostProcessorOnDifferentMessageHandlerDuringHandler(
            [ValueSource(nameof(AllVariants))] PostProcessorVariant variant
        )
        {
            GameObject hostA = new(
                nameof(AddPostProcessorOnDifferentMessageHandlerDuringHandler) + "_A_" + variant,
                typeof(EmptyMessageAwareComponent)
            );
            GameObject hostB = new(
                nameof(AddPostProcessorOnDifferentMessageHandlerDuringHandler) + "_B_" + variant,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(hostA);
            _spawned.Add(hostB);

            EmptyMessageAwareComponent componentA =
                hostA.GetComponent<EmptyMessageAwareComponent>();
            EmptyMessageAwareComponent componentB =
                hostB.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken tokenA = GetToken(componentA);
            MessageRegistrationToken tokenB = GetToken(componentB);

            InstanceId hostId = hostA;

            int existingPpCount = 0;
            int newPpCount = 0;
            bool added = false;

            _ = RegisterPostProcessor(variant, tokenA, hostId, () => ++existingPpCount);

            _ = RegisterHandler(
                variant,
                tokenA,
                hostId,
                () =>
                {
                    if (added)
                    {
                        return;
                    }

                    added = true;
                    _ = RegisterPostProcessor(variant, tokenB, hostId, () => ++newPpCount);
                }
            );

            Emit(variant, hostA);

            Assert.AreEqual(
                1,
                existingPpCount,
                "[{0}] Existing PP must fire once on first emission. sourceId={1}, existingPpCount={2}, newPpCount={3}.",
                variant,
                hostId.Id,
                existingPpCount,
                newPpCount
            );
            Assert.AreEqual(
                0,
                newPpCount,
                "[{0}] PP registered through a different MessageHandler during dispatch must not fire on the in-flight emission. sourceId={1}, existingPpCount={2}, newPpCount={3}.",
                variant,
                hostId.Id,
                existingPpCount,
                newPpCount
            );

            Emit(variant, hostA);

            Assert.AreEqual(
                2,
                existingPpCount,
                "[{0}] Existing PP must fire again on second emission. sourceId={1}, existingPpCount={2}, newPpCount={3}.",
                variant,
                hostId.Id,
                existingPpCount,
                newPpCount
            );
            Assert.AreEqual(
                1,
                newPpCount,
                "[{0}] Cross-handler PP must fire starting on the second emission. sourceId={1}, existingPpCount={2}, newPpCount={3}.",
                variant,
                hostId.Id,
                existingPpCount,
                newPpCount
            );
        }

        [Test]
        public void PostProcessorIgnoresEmissionFromDifferentSource(
            [ValueSource(nameof(SourceFilteredVariants))] PostProcessorVariant variant
        )
        {
            GameObject registeredHost = new(
                nameof(PostProcessorIgnoresEmissionFromDifferentSource) + "_reg_" + variant,
                typeof(EmptyMessageAwareComponent)
            );
            GameObject otherHost = new(
                nameof(PostProcessorIgnoresEmissionFromDifferentSource) + "_other_" + variant,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(registeredHost);
            _spawned.Add(otherHost);

            EmptyMessageAwareComponent registeredComponent =
                registeredHost.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(registeredComponent);

            InstanceId registeredSource = registeredHost;
            InstanceId emissionSource = otherHost;
            Assert.AreNotEqual(
                registeredSource.Id,
                emissionSource.Id,
                "[{0}] Negative-case fixture requires two distinct InstanceIds. registeredSourceId={1}, emissionSourceId={2}.",
                variant,
                registeredSource.Id,
                emissionSource.Id
            );

            int ppCount = 0;
            _ = RegisterPostProcessor(variant, token, registeredSource, () => ++ppCount);

            Emit(variant, otherHost);

            Assert.AreEqual(
                0,
                ppCount,
                "[{0}] Source-filtered post-processor must not fire when the emission source differs. registeredSourceId={1}, emissionSourceId={2}, ppCount={3}.",
                variant,
                registeredSource.Id,
                emissionSource.Id,
                ppCount
            );
        }

        private static MessageRegistrationHandle RegisterHandler(
            PostProcessorVariant variant,
            MessageRegistrationToken token,
            InstanceId source,
            Action onInvoked,
            int priority = 0
        )
        {
            switch (variant)
            {
                case PostProcessorVariant.Untargeted:
                {
                    return token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) => onInvoked(),
                        priority: priority
                    );
                }
                case PostProcessorVariant.Targeted:
                {
                    return token.RegisterTargeted<SimpleTargetedMessage>(
                        source,
                        (ref SimpleTargetedMessage _) => onInvoked(),
                        priority: priority
                    );
                }
                case PostProcessorVariant.Broadcast:
                {
                    return token.RegisterBroadcast<SimpleBroadcastMessage>(
                        source,
                        (ref SimpleBroadcastMessage _) => onInvoked(),
                        priority: priority
                    );
                }
                case PostProcessorVariant.TargetedWithoutTargeting:
                {
                    return token.RegisterTargetedWithoutTargeting<SimpleTargetedMessage>(
                        (_, _) => onInvoked(),
                        priority: priority
                    );
                }
                case PostProcessorVariant.BroadcastWithoutSource:
                {
                    return token.RegisterBroadcastWithoutSource<SimpleBroadcastMessage>(
                        (_, _) => onInvoked(),
                        priority: priority
                    );
                }
                default:
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(variant),
                        variant,
                        "Unsupported post-processor variant."
                    );
                }
            }
        }

        private static MessageRegistrationHandle RegisterPostProcessor(
            PostProcessorVariant variant,
            MessageRegistrationToken token,
            InstanceId source,
            Action onInvoked,
            int priority = 0
        )
        {
            switch (variant)
            {
                case PostProcessorVariant.Untargeted:
                {
                    return token.RegisterUntargetedPostProcessor<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) => onInvoked(),
                        priority: priority
                    );
                }
                case PostProcessorVariant.Targeted:
                {
                    return token.RegisterTargetedPostProcessor<SimpleTargetedMessage>(
                        source,
                        (ref SimpleTargetedMessage _) => onInvoked(),
                        priority: priority
                    );
                }
                case PostProcessorVariant.Broadcast:
                {
                    return token.RegisterBroadcastPostProcessor<SimpleBroadcastMessage>(
                        source,
                        (ref SimpleBroadcastMessage _) => onInvoked(),
                        priority: priority
                    );
                }
                case PostProcessorVariant.TargetedWithoutTargeting:
                {
                    return token.RegisterTargetedWithoutTargetingPostProcessor<SimpleTargetedMessage>(
                        (ref InstanceId _, ref SimpleTargetedMessage __) => onInvoked(),
                        priority: priority
                    );
                }
                case PostProcessorVariant.BroadcastWithoutSource:
                {
                    return token.RegisterBroadcastWithoutSourcePostProcessor<SimpleBroadcastMessage>(
                        (ref InstanceId _, ref SimpleBroadcastMessage __) => onInvoked(),
                        priority: priority
                    );
                }
                default:
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(variant),
                        variant,
                        "Unsupported post-processor variant."
                    );
                }
            }
        }

        private static void Emit(PostProcessorVariant variant, GameObject host)
        {
            switch (variant)
            {
                case PostProcessorVariant.Untargeted:
                {
                    SimpleUntargetedMessage untargeted = new();
                    untargeted.EmitUntargeted();
                    return;
                }
                case PostProcessorVariant.Targeted:
                case PostProcessorVariant.TargetedWithoutTargeting:
                {
                    SimpleTargetedMessage targeted = new();
                    targeted.EmitGameObjectTargeted(host);
                    return;
                }
                case PostProcessorVariant.Broadcast:
                case PostProcessorVariant.BroadcastWithoutSource:
                {
                    SimpleBroadcastMessage broadcast = new();
                    broadcast.EmitGameObjectBroadcast(host);
                    return;
                }
                default:
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(variant),
                        variant,
                        "Unsupported post-processor variant."
                    );
                }
            }
        }
    }
}

#endif
