#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class MutationPostProcessorAcrossHandlersTests : MessagingTestBase
    {
        [Test]
        public void RemoveOtherPostProcessorAcrossHandlersDuringDispatch(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.AllKindsIncludingWithoutContext)
            )]
                MessageScenario scenario
        )
        {
            (EmptyMessageAwareComponent[] components, MessageRegistrationToken[] tokens) =
                SpawnTwoListeners(scenario, "RemoveOtherPp_");

            using LeakWatcher watcher = LeakWatcher.Watch(label: scenario.DisplayName);

            MessageRegistrationHandle[] pp = new MessageRegistrationHandle[2];
            int[] counts = new int[2];
            List<int> order = new();
            InstanceId emissionTarget = ResolveEmissionTarget(scenario, components);

            // Ensure dispatch reaches the post-processor stage by registering a no-op handler
            // on the same MessageHandler that owns pp[0].
            MessageRegistrationHandle noop = RegisterHandler(
                scenario,
                tokens[0],
                emissionTarget,
                () => { }
            );

            pp[0] = RegisterPostProcessor(
                scenario,
                tokens[0],
                emissionTarget,
                () =>
                {
                    counts[0]++;
                    order.Add(0);
                    tokens[1].RemoveRegistration(pp[1]);
                }
            );
            pp[1] = RegisterPostProcessor(
                scenario,
                tokens[1],
                emissionTarget,
                () =>
                {
                    counts[1]++;
                    order.Add(1);
                }
            );

            EmitForScenario(scenario, components[0]);
            Assert.AreEqual(
                1,
                counts[0],
                "[{0}] pp[0] must run on the first emission. counts=({1}, {2}).",
                scenario,
                counts[0],
                counts[1]
            );
            Assert.AreEqual(
                1,
                counts[1],
                "[{0}] pp[1] was registered when emission started so it must still run on the first emission, even though pp[0] removed it mid-dispatch. counts=({1}, {2}).",
                scenario,
                counts[0],
                counts[1]
            );
            CollectionAssert.AreEqual(
                new List<int> { 0, 1 },
                order,
                "[{0}] pp[0] must run before pp[1] within the first emission.",
                scenario
            );

            EmitForScenario(scenario, components[0]);
            Assert.AreEqual(
                2,
                counts[0],
                "[{0}] pp[0] must run again on the second emission. counts=({1}, {2}).",
                scenario,
                counts[0],
                counts[1]
            );
            Assert.AreEqual(
                1,
                counts[1],
                "[{0}] pp[1] was removed during the first emission so it must not run on the second emission. counts=({1}, {2}).",
                scenario,
                counts[0],
                counts[1]
            );

            tokens[0].RemoveRegistration(pp[0]);
            tokens[0].RemoveRegistration(noop);
        }

        [Test]
        public void RemoveSelfPostProcessorAcrossHandlersDuringDispatch(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.AllKindsIncludingWithoutContext)
            )]
                MessageScenario scenario
        )
        {
            (EmptyMessageAwareComponent[] components, MessageRegistrationToken[] tokens) =
                SpawnTwoListeners(scenario, "RemoveSelfPp_");

            using LeakWatcher watcher = LeakWatcher.Watch(label: scenario.DisplayName);

            MessageRegistrationHandle[] pp = new MessageRegistrationHandle[2];
            int[] counts = new int[2];
            InstanceId emissionTarget = ResolveEmissionTarget(scenario, components);

            MessageRegistrationHandle noop = RegisterHandler(
                scenario,
                tokens[0],
                emissionTarget,
                () => { }
            );

            pp[0] = RegisterPostProcessor(
                scenario,
                tokens[0],
                emissionTarget,
                () =>
                {
                    counts[0]++;
                    tokens[0].RemoveRegistration(pp[0]);
                }
            );
            pp[1] = RegisterPostProcessor(scenario, tokens[1], emissionTarget, () => counts[1]++);

            EmitForScenario(scenario, components[0]);
            Assert.AreEqual(
                1,
                counts[0],
                "[{0}] pp[0] must run once before removing itself. counts=({1}, {2}).",
                scenario,
                counts[0],
                counts[1]
            );
            Assert.AreEqual(
                1,
                counts[1],
                "[{0}] pp[1] on the sibling handler must still run on the first emission. counts=({1}, {2}).",
                scenario,
                counts[0],
                counts[1]
            );

            EmitForScenario(scenario, components[0]);
            Assert.AreEqual(
                1,
                counts[0],
                "[{0}] pp[0] removed itself during the previous emission so it must not run again. counts=({1}, {2}).",
                scenario,
                counts[0],
                counts[1]
            );
            Assert.AreEqual(
                2,
                counts[1],
                "[{0}] pp[1] must continue to run on subsequent emissions. counts=({1}, {2}).",
                scenario,
                counts[0],
                counts[1]
            );

            tokens[1].RemoveRegistration(pp[1]);
            tokens[0].RemoveRegistration(noop);
        }

        [Test]
        public void RemoveAllPostProcessorsAcrossHandlersDuringDispatch(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.AllKindsIncludingWithoutContext)
            )]
                MessageScenario scenario
        )
        {
            (EmptyMessageAwareComponent[] components, MessageRegistrationToken[] tokens) =
                SpawnTwoListeners(scenario, "RemoveAllPp_");

            using LeakWatcher watcher = LeakWatcher.Watch(label: scenario.DisplayName);

            MessageRegistrationHandle[] pp = new MessageRegistrationHandle[2];
            int[] counts = new int[2];
            InstanceId emissionTarget = ResolveEmissionTarget(scenario, components);

            MessageRegistrationHandle noop = RegisterHandler(
                scenario,
                tokens[0],
                emissionTarget,
                () => { }
            );

            pp[0] = RegisterPostProcessor(
                scenario,
                tokens[0],
                emissionTarget,
                () =>
                {
                    counts[0]++;
                    tokens[0].RemoveRegistration(pp[0]);
                    tokens[1].RemoveRegistration(pp[1]);
                }
            );
            pp[1] = RegisterPostProcessor(scenario, tokens[1], emissionTarget, () => counts[1]++);

            EmitForScenario(scenario, components[0]);
            Assert.AreEqual(
                1,
                counts[0],
                "[{0}] pp[0] must run on the first emission before tearing down both pp registrations. counts=({1}, {2}).",
                scenario,
                counts[0],
                counts[1]
            );
            Assert.AreEqual(
                1,
                counts[1],
                "[{0}] pp[1] was registered when emission started so the snapshot must still dispatch it on the first emission. counts=({1}, {2}).",
                scenario,
                counts[0],
                counts[1]
            );

            EmitForScenario(scenario, components[0]);
            Assert.AreEqual(
                1,
                counts[0],
                "[{0}] pp[0] removed itself so it must not fire on the second emission. counts=({1}, {2}).",
                scenario,
                counts[0],
                counts[1]
            );
            Assert.AreEqual(
                1,
                counts[1],
                "[{0}] pp[1] was removed mid-emit during the first emission so it must not fire on the second emission. counts=({1}, {2}).",
                scenario,
                counts[0],
                counts[1]
            );

            tokens[0].RemoveRegistration(noop);
        }

        [Test]
        public void AddNewPostProcessorAcrossHandlersDuringDispatch(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.AllKindsIncludingWithoutContext)
            )]
                MessageScenario scenario
        )
        {
            (EmptyMessageAwareComponent[] components, MessageRegistrationToken[] tokens) =
                SpawnTwoListeners(scenario, "AddNewPp_");

            using LeakWatcher watcher = LeakWatcher.Watch(label: scenario.DisplayName);

            int existingCount = 0;
            int addedCount = 0;
            bool added = false;
            MessageRegistrationHandle addedHandle = default;
            MessageRegistrationHandle existingHandle;
            InstanceId emissionTarget = ResolveEmissionTarget(scenario, components);

            MessageRegistrationHandle noop = RegisterHandler(
                scenario,
                tokens[0],
                emissionTarget,
                () => { }
            );
            existingHandle = RegisterPostProcessor(
                scenario,
                tokens[0],
                emissionTarget,
                () =>
                {
                    existingCount++;
                    if (!added)
                    {
                        added = true;
                        addedHandle = RegisterPostProcessor(
                            scenario,
                            tokens[1],
                            emissionTarget,
                            () => addedCount++
                        );
                    }
                }
            );

            EmitForScenario(scenario, components[0]);
            Assert.AreEqual(
                1,
                existingCount,
                "[{0}] Existing post-processor must run on the first emission. existing={1}, added={2}.",
                scenario,
                existingCount,
                addedCount
            );
            Assert.AreEqual(
                0,
                addedCount,
                "[{0}] Cross-handler post-processor added during dispatch must not run on the in-flight emission. existing={1}, added={2}.",
                scenario,
                existingCount,
                addedCount
            );

            EmitForScenario(scenario, components[0]);
            Assert.AreEqual(
                2,
                existingCount,
                "[{0}] Existing post-processor must run again on the second emission. existing={1}, added={2}.",
                scenario,
                existingCount,
                addedCount
            );
            Assert.AreEqual(
                1,
                addedCount,
                "[{0}] Cross-handler post-processor added during the first emission must start running on the second emission. existing={1}, added={2}.",
                scenario,
                existingCount,
                addedCount
            );

            tokens[1].RemoveRegistration(addedHandle);
            tokens[0].RemoveRegistration(existingHandle);
            tokens[0].RemoveRegistration(noop);
        }

        [Test]
        public void RemoveOtherHandlerAcrossHandlersDuringDispatch(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.AllKindsIncludingWithoutContext)
            )]
                MessageScenario scenario
        )
        {
            (EmptyMessageAwareComponent[] components, MessageRegistrationToken[] tokens) =
                SpawnTwoListeners(scenario, "RemoveOtherHandler_");

            using LeakWatcher watcher = LeakWatcher.Watch(label: scenario.DisplayName);

            MessageRegistrationHandle[] handlers = new MessageRegistrationHandle[2];
            int[] counts = new int[2];
            InstanceId emissionTarget = ResolveEmissionTarget(scenario, components);

            handlers[0] = RegisterHandler(
                scenario,
                tokens[0],
                emissionTarget,
                () =>
                {
                    counts[0]++;
                    tokens[1].RemoveRegistration(handlers[1]);
                }
            );
            handlers[1] = RegisterHandler(scenario, tokens[1], emissionTarget, () => counts[1]++);

            EmitForScenario(scenario, components[0]);
            Assert.AreEqual(
                1,
                counts[0],
                "[{0}] handlers[0] must run on the first emission. counts=({1}, {2}).",
                scenario,
                counts[0],
                counts[1]
            );
            Assert.AreEqual(
                1,
                counts[1],
                "[{0}] handlers[1] was registered at emission start so the snapshot must still dispatch it. counts=({1}, {2}).",
                scenario,
                counts[0],
                counts[1]
            );

            EmitForScenario(scenario, components[0]);
            Assert.AreEqual(
                2,
                counts[0],
                "[{0}] handlers[0] must continue running on subsequent emissions. counts=({1}, {2}).",
                scenario,
                counts[0],
                counts[1]
            );
            Assert.AreEqual(
                1,
                counts[1],
                "[{0}] handlers[1] was removed mid-emit so subsequent emissions must skip it. counts=({1}, {2}).",
                scenario,
                counts[0],
                counts[1]
            );

            tokens[0].RemoveRegistration(handlers[0]);
        }

        /// <summary>
        /// Documents the global accept-all (RegisterGlobalAcceptAll) cross-handler
        /// removal behavior. Unlike per-kind dispatch, the bus prefreezes the
        /// global accept-all caches lazily per-entry inside the dispatch loop,
        /// so a sibling MessageHandler that removes another's global registration
        /// during the same emission causes the removed handler to be SKIPPED on
        /// the in-flight emission. This contrasts with the in-flight snapshot
        /// semantics observed by the per-kind dispatch surfaces. Pinning the
        /// behavior here so a future change to the global dispatch model
        /// (upfront prefreeze) cannot land silently.
        /// </summary>
        [Test]
        public void RemoveOtherGlobalAcceptAllAcrossHandlersDuringDispatch()
        {
            EmptyMessageAwareComponent[] components = new EmptyMessageAwareComponent[2];
            MessageRegistrationToken[] tokens = new MessageRegistrationToken[2];
            for (int i = 0; i < 2; i++)
            {
                GameObject go = new(
                    "GlobalAcceptAll_RemoveOther_" + i,
                    typeof(EmptyMessageAwareComponent)
                );
                _spawned.Add(go);
                components[i] = go.GetComponent<EmptyMessageAwareComponent>();
                tokens[i] = GetToken(components[i]);
            }

            using LeakWatcher watcher = LeakWatcher.Watch(label: "GlobalAcceptAll");

            MessageRegistrationHandle[] global = new MessageRegistrationHandle[2];
            int[] counts = new int[2];

            global[0] = tokens[0]
                .RegisterGlobalAcceptAll(
                    acceptAllUntargeted: _ =>
                    {
                        counts[0]++;
                        tokens[1].RemoveRegistration(global[1]);
                    },
                    acceptAllTargeted: (_, _) => { },
                    acceptAllBroadcast: (_, _) => { }
                );
            global[1] = tokens[1]
                .RegisterGlobalAcceptAll(
                    acceptAllUntargeted: _ => counts[1]++,
                    acceptAllTargeted: (_, _) => { },
                    acceptAllBroadcast: (_, _) => { }
                );

            SimpleUntargetedMessage msg = new();
            msg.EmitUntargeted();
            Assert.AreEqual(
                1,
                counts[0],
                "global[0] must fire on the first emission. counts=({0}, {1}).",
                counts[0],
                counts[1]
            );
            // Documented behavior: the global accept-all path uses lazy
            // per-entry prefreeze, so global[1] is dropped during the same
            // emission that global[0] removes it. If a future change adds an
            // upfront prefreeze for global handlers (mirroring the per-kind
            // dispatch surfaces), this assertion must flip to expect counts[1]
            // == 1 on the first emission.
            Assert.AreEqual(
                0,
                counts[1],
                "global[1] is expected to be skipped on the first emission because the global accept-all path prefreezes lazily per-entry; if this assertion flips to 1, the bus has switched to upfront global prefreeze and the snapshot semantics now match the per-kind paths. counts=({0}, {1}).",
                counts[0],
                counts[1]
            );

            msg.EmitUntargeted();
            Assert.AreEqual(
                2,
                counts[0],
                "global[0] must fire on the second emission. counts=({0}, {1}).",
                counts[0],
                counts[1]
            );
            Assert.AreEqual(
                0,
                counts[1],
                "global[1] was removed before the second emission so it must not fire. counts=({0}, {1}).",
                counts[0],
                counts[1]
            );

            tokens[0].RemoveRegistration(global[0]);
        }

        private (
            EmptyMessageAwareComponent[] components,
            MessageRegistrationToken[] tokens
        ) SpawnTwoListeners(MessageScenario scenario, string namePrefix)
        {
            EmptyMessageAwareComponent[] components = new EmptyMessageAwareComponent[2];
            MessageRegistrationToken[] tokens = new MessageRegistrationToken[2];
            for (int i = 0; i < 2; i++)
            {
                GameObject go = new(
                    namePrefix + scenario.Kind + "_" + i,
                    typeof(EmptyMessageAwareComponent)
                );
                _spawned.Add(go);
                components[i] = go.GetComponent<EmptyMessageAwareComponent>();
                tokens[i] = GetToken(components[i]);
            }
            return (components, tokens);
        }

        private static InstanceId ResolveEmissionTarget(
            MessageScenario scenario,
            EmptyMessageAwareComponent[] components
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                case MessageKind.TargetedWithoutTargeting:
                case MessageKind.BroadcastWithoutSource:
                case MessageKind.Targeted:
                case MessageKind.Broadcast:
                {
                    return components[0];
                }
                default:
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(scenario),
                        scenario.Kind,
                        "Unsupported message kind."
                    );
                }
            }
        }

        private static MessageRegistrationHandle RegisterHandler(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId source,
            Action onInvoked,
            int priority = 0
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    return token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) => onInvoked(),
                        priority: priority
                    );
                }
                case MessageKind.Targeted:
                {
                    return token.RegisterTargeted<SimpleTargetedMessage>(
                        source,
                        (ref SimpleTargetedMessage _) => onInvoked(),
                        priority: priority
                    );
                }
                case MessageKind.Broadcast:
                {
                    return token.RegisterBroadcast<SimpleBroadcastMessage>(
                        source,
                        (ref SimpleBroadcastMessage _) => onInvoked(),
                        priority: priority
                    );
                }
                case MessageKind.TargetedWithoutTargeting:
                {
                    return token.RegisterTargetedWithoutTargeting<SimpleTargetedMessage>(
                        (_, _) => onInvoked(),
                        priority: priority
                    );
                }
                case MessageKind.BroadcastWithoutSource:
                {
                    return token.RegisterBroadcastWithoutSource<SimpleBroadcastMessage>(
                        (_, _) => onInvoked(),
                        priority: priority
                    );
                }
                default:
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(scenario),
                        scenario.Kind,
                        "Unsupported message kind."
                    );
                }
            }
        }

        private static MessageRegistrationHandle RegisterPostProcessor(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId source,
            Action onInvoked,
            int priority = 0
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    return token.RegisterUntargetedPostProcessor<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) => onInvoked(),
                        priority: priority
                    );
                }
                case MessageKind.Targeted:
                {
                    return token.RegisterTargetedPostProcessor<SimpleTargetedMessage>(
                        source,
                        (ref SimpleTargetedMessage _) => onInvoked(),
                        priority: priority
                    );
                }
                case MessageKind.Broadcast:
                {
                    return token.RegisterBroadcastPostProcessor<SimpleBroadcastMessage>(
                        source,
                        (ref SimpleBroadcastMessage _) => onInvoked(),
                        priority: priority
                    );
                }
                case MessageKind.TargetedWithoutTargeting:
                {
                    return token.RegisterTargetedWithoutTargetingPostProcessor<SimpleTargetedMessage>(
                        (ref InstanceId _, ref SimpleTargetedMessage __) => onInvoked(),
                        priority: priority
                    );
                }
                case MessageKind.BroadcastWithoutSource:
                {
                    return token.RegisterBroadcastWithoutSourcePostProcessor<SimpleBroadcastMessage>(
                        (ref InstanceId _, ref SimpleBroadcastMessage __) => onInvoked(),
                        priority: priority
                    );
                }
                default:
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(scenario),
                        scenario.Kind,
                        "Unsupported message kind."
                    );
                }
            }
        }

        private static void EmitForScenario(
            MessageScenario scenario,
            EmptyMessageAwareComponent component
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    SimpleUntargetedMessage untargeted = new();
                    untargeted.EmitUntargeted();
                    return;
                }
                case MessageKind.Targeted:
                case MessageKind.TargetedWithoutTargeting:
                {
                    SimpleTargetedMessage targeted = new();
                    targeted.EmitComponentTargeted(component);
                    return;
                }
                case MessageKind.Broadcast:
                case MessageKind.BroadcastWithoutSource:
                {
                    SimpleBroadcastMessage broadcast = new();
                    broadcast.EmitComponentBroadcast(component);
                    return;
                }
                default:
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(scenario),
                        scenario.Kind,
                        "Unsupported message kind."
                    );
                }
            }
        }
    }
}

#endif
