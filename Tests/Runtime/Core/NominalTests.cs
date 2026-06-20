#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;
    using Scripts.Components;
    using Scripts.Messages;
    using UnityEngine;
    using UnityEngine.TestTools;
    using static DxMessaging.Tests.Runtime.RegistrationCountAssertions;
    using Object = UnityEngine.Object;

    [Category("Stress")]
    public sealed class NominalTests : MessagingTestBase
    {
        // Per-shape registration entry counts for the Lifetime test. Hoisted to
        // a single source of truth so the same expected shape used by every
        // lifecycle transition cannot drift between call sites.
        // SimpleMessageAwareComponent contributes the registration entries
        // documented in the docblock on Lifetime.
        private const int SingleComponentUntargeted = 1;
        private const int SingleComponentTargeted = 5;
        private const int SingleComponentBroadcast = 3;
        private const int TwoComponentsUntargeted = 1;
        private const int TwoComponentsTargeted = 7;
        private const int TwoComponentsBroadcast = 4;

        [SetUp]
        public override void Setup()
        {
            base.Setup();
            // Run(...) helpers below loop _numRegistrations times across many
            // tests; restore the legacy stress fan-out so coverage matches the
            // pre-Phase-A baseline.
            _numRegistrations = StressRegistrations;
        }

        [UnityTest]
        public IEnumerator Nominal()
        {
            GameObject test = new(nameof(Nominal), typeof(SimpleMessageAwareComponent));
            _spawned.Add(test);
            GameObject nonTest = new("Non-Test", typeof(SimpleMessageAwareComponent));
            _spawned.Add(nonTest);

            SimpleMessageAwareComponent firstComponent =
                test.GetComponent<SimpleMessageAwareComponent>();

            int untargetedWorks = 0;
            int targetedWorks = 0;
            int targetedWithoutTargetingWorks = 0;
            int broadcastWorks = 0;
            int broadcastWithoutSourceWorks = 0;
            Dictionary<object, int> componentTargetedWorks = new();
            Dictionary<object, int> componentBroadcastWorks = new();

            SetupComponent(firstComponent);

            // Generate non-component targeted methods
            SimpleUntargetedMessage untargetedMessage = new();
            untargetedMessage.EmitUntargeted();
            Assert.AreEqual(1, untargetedWorks);
            Assert.AreEqual(0, targetedWorks);
            Assert.AreEqual(0, broadcastWorks);
            Assert.AreEqual(0, componentTargetedWorks.Count);
            Assert.AreEqual(0, componentBroadcastWorks.Count);
            Assert.AreEqual(0, targetedWithoutTargetingWorks);
            Assert.AreEqual(0, broadcastWithoutSourceWorks);

            untargetedMessage.EmitUntargeted();
            Assert.AreEqual(2, untargetedWorks);
            Assert.AreEqual(0, targetedWorks);
            Assert.AreEqual(0, broadcastWorks);
            Assert.AreEqual(0, componentTargetedWorks.Count);
            Assert.AreEqual(0, componentBroadcastWorks.Count);
            Assert.AreEqual(0, targetedWithoutTargetingWorks);
            Assert.AreEqual(0, broadcastWithoutSourceWorks);

            SimpleTargetedMessage targetedMessage = new();
            targetedMessage.EmitGameObjectTargeted(test);
            Assert.AreEqual(2, untargetedWorks);
            Assert.AreEqual(1, targetedWorks);
            Assert.AreEqual(0, broadcastWorks);
            Assert.AreEqual(0, componentTargetedWorks.Count);
            Assert.AreEqual(0, componentBroadcastWorks.Count);
            Assert.AreEqual(1, targetedWithoutTargetingWorks);
            Assert.AreEqual(0, broadcastWithoutSourceWorks);

            SimpleBroadcastMessage broadcastMessage = new();
            broadcastMessage.EmitGameObjectBroadcast(test);
            Assert.AreEqual(2, untargetedWorks);
            Assert.AreEqual(1, targetedWorks);
            Assert.AreEqual(1, broadcastWorks);
            Assert.AreEqual(0, componentTargetedWorks.Count);
            Assert.AreEqual(0, componentBroadcastWorks.Count);
            Assert.AreEqual(1, targetedWithoutTargetingWorks);
            Assert.AreEqual(1, broadcastWithoutSourceWorks);

            yield return null;

            /*
            If we add a second component, our Untargeted, GameObjectTargeted,
            and GameObjectBroadcast receivers will be called *twice* for each
            message emission, once per component.
         */
            SimpleMessageAwareComponent secondComponent =
                test.AddComponent<SimpleMessageAwareComponent>();
            SetupComponent(secondComponent);

            // Targeted
            targetedMessage.EmitGameObjectTargeted(test);
            Assert.AreEqual(2, untargetedWorks);
            Assert.AreEqual(3, targetedWorks);
            Assert.AreEqual(1, broadcastWorks);
            Assert.AreEqual(0, componentTargetedWorks.Count);
            Assert.AreEqual(0, componentBroadcastWorks.Count);
            Assert.AreEqual(3, targetedWithoutTargetingWorks);
            Assert.AreEqual(1, broadcastWithoutSourceWorks);

            targetedMessage.EmitGameObjectTargeted(nonTest);
            Assert.AreEqual(2, untargetedWorks);
            Assert.AreEqual(3, targetedWorks);
            Assert.AreEqual(1, broadcastWorks);
            Assert.AreEqual(0, componentTargetedWorks.Count);
            Assert.AreEqual(0, componentBroadcastWorks.Count);
            Assert.AreEqual(5, targetedWithoutTargetingWorks);
            Assert.AreEqual(1, broadcastWithoutSourceWorks);

            // Component Targeted
            targetedMessage.EmitComponentTargeted(firstComponent);
            Assert.AreEqual(2, untargetedWorks);
            Assert.AreEqual(3, targetedWorks);
            Assert.AreEqual(1, broadcastWorks);
            Assert.AreEqual(1, componentTargetedWorks.Count);
            Assert.AreEqual(1, componentTargetedWorks[firstComponent]);
            Assert.AreEqual(0, componentBroadcastWorks.Count);
            Assert.AreEqual(7, targetedWithoutTargetingWorks);
            Assert.AreEqual(1, broadcastWithoutSourceWorks);

            targetedMessage.EmitComponentTargeted(secondComponent);
            Assert.AreEqual(2, untargetedWorks);
            Assert.AreEqual(3, targetedWorks);
            Assert.AreEqual(1, broadcastWorks);
            Assert.AreEqual(2, componentTargetedWorks.Count);
            Assert.AreEqual(1, componentTargetedWorks[firstComponent]);
            Assert.AreEqual(1, componentTargetedWorks[secondComponent]);
            Assert.AreEqual(0, componentBroadcastWorks.Count);
            Assert.AreEqual(9, targetedWithoutTargetingWorks);
            Assert.AreEqual(1, broadcastWithoutSourceWorks);

            targetedMessage.EmitComponentTargeted(firstComponent);
            Assert.AreEqual(2, untargetedWorks);
            Assert.AreEqual(3, targetedWorks);
            Assert.AreEqual(1, broadcastWorks);
            Assert.AreEqual(2, componentTargetedWorks.Count);
            Assert.AreEqual(2, componentTargetedWorks[firstComponent]);
            Assert.AreEqual(1, componentTargetedWorks[secondComponent]);
            Assert.AreEqual(0, componentBroadcastWorks.Count);
            Assert.AreEqual(11, targetedWithoutTargetingWorks);
            Assert.AreEqual(1, broadcastWithoutSourceWorks);

            targetedMessage.EmitComponentTargeted(secondComponent);
            Assert.AreEqual(2, untargetedWorks);
            Assert.AreEqual(3, targetedWorks);
            Assert.AreEqual(1, broadcastWorks);
            Assert.AreEqual(2, componentTargetedWorks.Count);
            Assert.AreEqual(2, componentTargetedWorks[firstComponent]);
            Assert.AreEqual(2, componentTargetedWorks[secondComponent]);
            Assert.AreEqual(0, componentBroadcastWorks.Count);
            Assert.AreEqual(13, targetedWithoutTargetingWorks);
            Assert.AreEqual(1, broadcastWithoutSourceWorks);

            // Broadcast
            broadcastMessage.EmitGameObjectBroadcast(nonTest);
            Assert.AreEqual(2, untargetedWorks);
            Assert.AreEqual(3, targetedWorks);
            Assert.AreEqual(1, broadcastWorks);
            Assert.AreEqual(2, componentTargetedWorks.Count);
            Assert.AreEqual(2, componentTargetedWorks[firstComponent]);
            Assert.AreEqual(2, componentTargetedWorks[secondComponent]);
            Assert.AreEqual(0, componentBroadcastWorks.Count);
            Assert.AreEqual(13, targetedWithoutTargetingWorks);
            Assert.AreEqual(3, broadcastWithoutSourceWorks);

            broadcastMessage.EmitGameObjectBroadcast(test);
            Assert.AreEqual(2, untargetedWorks);
            Assert.AreEqual(3, targetedWorks);
            Assert.AreEqual(3, broadcastWorks);
            Assert.AreEqual(2, componentTargetedWorks.Count);
            Assert.AreEqual(2, componentTargetedWorks[firstComponent]);
            Assert.AreEqual(2, componentTargetedWorks[secondComponent]);
            Assert.AreEqual(0, componentBroadcastWorks.Count);
            Assert.AreEqual(13, targetedWithoutTargetingWorks);
            Assert.AreEqual(5, broadcastWithoutSourceWorks);

            // Component Broadcast
            broadcastMessage.EmitComponentBroadcast(firstComponent);
            Assert.AreEqual(2, untargetedWorks);
            Assert.AreEqual(3, targetedWorks);
            Assert.AreEqual(3, broadcastWorks);
            Assert.AreEqual(2, componentTargetedWorks.Count);
            Assert.AreEqual(2, componentTargetedWorks[firstComponent]);
            Assert.AreEqual(2, componentTargetedWorks[secondComponent]);
            Assert.AreEqual(1, componentBroadcastWorks.Count);
            Assert.AreEqual(1, componentBroadcastWorks[firstComponent]);
            Assert.AreEqual(13, targetedWithoutTargetingWorks);
            Assert.AreEqual(7, broadcastWithoutSourceWorks);

            broadcastMessage.EmitComponentBroadcast(secondComponent);
            Assert.AreEqual(2, untargetedWorks);
            Assert.AreEqual(3, targetedWorks);
            Assert.AreEqual(3, broadcastWorks);
            Assert.AreEqual(2, componentTargetedWorks.Count);
            Assert.AreEqual(2, componentTargetedWorks[firstComponent]);
            Assert.AreEqual(2, componentTargetedWorks[secondComponent]);
            Assert.AreEqual(2, componentBroadcastWorks.Count);
            Assert.AreEqual(1, componentBroadcastWorks[firstComponent]);
            Assert.AreEqual(1, componentBroadcastWorks[secondComponent]);
            Assert.AreEqual(13, targetedWithoutTargetingWorks);
            Assert.AreEqual(9, broadcastWithoutSourceWorks);

            broadcastMessage.EmitComponentBroadcast(firstComponent);
            Assert.AreEqual(2, untargetedWorks);
            Assert.AreEqual(3, targetedWorks);
            Assert.AreEqual(3, broadcastWorks);
            Assert.AreEqual(2, componentTargetedWorks.Count);
            Assert.AreEqual(2, componentTargetedWorks[firstComponent]);
            Assert.AreEqual(2, componentTargetedWorks[secondComponent]);
            Assert.AreEqual(2, componentBroadcastWorks.Count);
            Assert.AreEqual(2, componentBroadcastWorks[firstComponent]);
            Assert.AreEqual(1, componentBroadcastWorks[secondComponent]);
            Assert.AreEqual(13, targetedWithoutTargetingWorks);
            Assert.AreEqual(11, broadcastWithoutSourceWorks);

            broadcastMessage.EmitComponentBroadcast(secondComponent);
            Assert.AreEqual(2, untargetedWorks);
            Assert.AreEqual(3, targetedWorks);
            Assert.AreEqual(3, broadcastWorks);
            Assert.AreEqual(2, componentTargetedWorks.Count);
            Assert.AreEqual(2, componentTargetedWorks[firstComponent]);
            Assert.AreEqual(2, componentTargetedWorks[secondComponent]);
            Assert.AreEqual(2, componentBroadcastWorks.Count);
            Assert.AreEqual(2, componentBroadcastWorks[firstComponent]);
            Assert.AreEqual(2, componentBroadcastWorks[secondComponent]);
            Assert.AreEqual(13, targetedWithoutTargetingWorks);
            Assert.AreEqual(13, broadcastWithoutSourceWorks);

            // Finally, re-emit the targeted message - it should be received by both components
            untargetedMessage.EmitUntargeted();
            Assert.AreEqual(4, untargetedWorks);
            Assert.AreEqual(3, targetedWorks);
            Assert.AreEqual(3, broadcastWorks);
            Assert.AreEqual(2, componentTargetedWorks.Count);
            Assert.AreEqual(2, componentTargetedWorks[firstComponent]);
            Assert.AreEqual(2, componentTargetedWorks[secondComponent]);
            Assert.AreEqual(2, componentBroadcastWorks.Count);
            Assert.AreEqual(2, componentBroadcastWorks[firstComponent]);
            Assert.AreEqual(2, componentBroadcastWorks[secondComponent]);
            Assert.AreEqual(13, targetedWithoutTargetingWorks);
            Assert.AreEqual(13, broadcastWithoutSourceWorks);
            yield break;

            void SetupComponent(SimpleMessageAwareComponent toSetup)
            {
                toSetup.untargetedHandler = () => ++untargetedWorks;
                toSetup.targetedHandler = () => ++targetedWorks;
                toSetup.targetedWithoutTargetingHandler = () => ++targetedWithoutTargetingWorks;
                toSetup.broadcastHandler = () => ++broadcastWorks;
                toSetup.broadcastWithoutSourceHandler = () => ++broadcastWithoutSourceWorks;
                toSetup.componentTargetedHandler = () =>
                {
                    int existing = componentTargetedWorks.GetValueOrDefault(toSetup, 0);

                    componentTargetedWorks[toSetup] = ++existing;
                };
                toSetup.componentBroadcastHandler = () =>
                {
                    int existing = componentBroadcastWorks.GetValueOrDefault(toSetup, 0);

                    componentBroadcastWorks[toSetup] = ++existing;
                };
            }
        }

        [UnityTest]
        public IEnumerator Lifetime()
        {
            IMessageBus messageBus = MessageHandler.MessageBus;
            Assert.IsNotNull(messageBus);

            // Bracket the full lifecycle churn in a LeakWatcher. The
            // per-counter assertions below only cover Untargeted/Targeted/
            // Broadcast handler counters; LeakWatcher additionally guards
            // RegisteredInterceptors, RegisteredPostProcessors, and
            // RegisteredGlobalAcceptAll, so a regression that leaks any of
            // those auxiliary counters during create/teardown surfaces here
            // even though the per-counter (0,0,0) assertion at the end would
            // miss it.
            using LeakWatcher watcher = new(messageBus, label: nameof(Lifetime));

            GameObject test = new(nameof(Lifetime), typeof(SimpleMessageAwareComponent));
            _spawned.Add(test);

            SimpleMessageAwareComponent firstComponent =
                test.GetComponent<SimpleMessageAwareComponent>();

            // SimpleMessageAwareComponent registers per active component
            // (entries are distinct (type, target, priority) registration
            // entries; identical (type, target, priority) tuples collapse):
            //   Untargeted bucket (1 registration entry):
            //     - RegisterUntargeted<SimpleUntargetedMessage>
            //   Targeted bucket (5 registration entries):
            //     - RegisterGameObjectTargeted<SimpleTargetedMessage> x2     (collapse to 1 entry)
            //     - RegisterGameObjectTargeted<ComplexTargetedMessage> x2    (collapse to 1 entry)
            //     - RegisterComponentTargeted<SimpleTargetedMessage>         (1 entry, distinct context)
            //     - RegisterComponentTargeted<ComplexTargetedMessage>        (1 entry, distinct context)
            //     - RegisterTargetedWithoutTargeting<SimpleTargetedMessage>  (1 entry, scalar TargetedWithoutContext sink)
            //   Broadcast bucket (3 registration entries):
            //     - RegisterGameObjectBroadcast<SimpleBroadcastMessage>      (1 entry)
            //     - RegisterComponentBroadcast<SimpleBroadcastMessage>       (1 entry, distinct context)
            //     - RegisterBroadcastWithoutSource<SimpleBroadcastMessage>   (1 entry, scalar BroadcastWithoutContext sink)
            // Adding another component on the SAME GameObject reuses the
            // (type, gameObject) and the scalar (type) registration entries,
            // so those do NOT double; only the per-component context
            // entries multiply, giving the +2 / +1 deltas seen below.
            AssertRegistrationCounts(
                messageBus,
                untargeted: SingleComponentUntargeted,
                targeted: SingleComponentTargeted,
                broadcast: SingleComponentBroadcast,
                context: "first component spawned"
            );

            yield return null;

            SimpleMessageAwareComponent secondComponent =
                test.AddComponent<SimpleMessageAwareComponent>();
            // Adding a second component contributes:
            //   Untargeted: +0 (same scalar (SimpleUntargetedMessage) entry)
            //   Targeted:   +2 (new (SimpleTargetedMessage, second-component)
            //                   and (ComplexTargetedMessage, second-component)
            //                   context entries; gameObject + scalar entries
            //                   reused)
            //   Broadcast:  +1 (new (SimpleBroadcastMessage, second-component)
            //                   context entry; gameObject + scalar entries
            //                   reused)
            AssertRegistrationCounts(
                messageBus,
                untargeted: TwoComponentsUntargeted,
                targeted: TwoComponentsTargeted,
                broadcast: TwoComponentsBroadcast,
                context: "second component added"
            );

            secondComponent.enabled = false;
            yield return null;

            // Disabling the second component removes only its
            // per-component context entries; gameObject + scalar entries
            // stay because the first component still registers them.
            AssertRegistrationCounts(
                messageBus,
                untargeted: SingleComponentUntargeted,
                targeted: SingleComponentTargeted,
                broadcast: SingleComponentBroadcast,
                context: "second component disabled"
            );

            firstComponent.enabled = false;
            yield return null;

            // No active scripts, no active handlers.
            AssertRegistrationCounts(
                messageBus,
                untargeted: 0,
                targeted: 0,
                broadcast: 0,
                context: "first component disabled (no active scripts)"
            );

            test.SetActive(false);
            yield return null;

            AssertRegistrationCounts(
                messageBus,
                untargeted: 0,
                targeted: 0,
                broadcast: 0,
                context: "GameObject deactivated"
            );

            firstComponent.enabled = true;
            yield return null;

            // Game object is still disabled - shouldn't have active child scripts.
            AssertRegistrationCounts(
                messageBus,
                untargeted: 0,
                targeted: 0,
                broadcast: 0,
                context: "first component enabled while GameObject inactive"
            );

            test.SetActive(true);
            yield return null;

            // Only the first component is enabled - back to the single-component shape.
            AssertRegistrationCounts(
                messageBus,
                untargeted: SingleComponentUntargeted,
                targeted: SingleComponentTargeted,
                broadcast: SingleComponentBroadcast,
                context: "GameObject reactivated with first component only"
            );

            Object.Destroy(firstComponent);
            yield return null;

            AssertRegistrationCounts(
                messageBus,
                untargeted: 0,
                targeted: 0,
                broadcast: 0,
                context: "first component destroyed (second still disabled)"
            );

            secondComponent.enabled = true;
            yield return null;

            // Re-enabling the surviving component yields the single-component shape again.
            AssertRegistrationCounts(
                messageBus,
                untargeted: SingleComponentUntargeted,
                targeted: SingleComponentTargeted,
                broadcast: SingleComponentBroadcast,
                context: "second component re-enabled"
            );

            Object.Destroy(test);
            yield return null;

            // Belt-and-braces: the per-counter triple must read (0,0,0) before
            // LeakWatcher.Dispose runs. The watcher additionally enforces that
            // the interceptor/post-processor/global-accept-all counters did not
            // drift during the lifecycle churn.
            AssertRegistrationCounts(
                messageBus,
                untargeted: 0,
                targeted: 0,
                broadcast: 0,
                context: "GameObject destroyed"
            );
        }

        [Test]
        public void NonMessagingObjects()
        {
            IMessageBus messageBus = MessageHandler.MessageBus;
            Assert.IsNotNull(messageBus);

            // Bracket the GameObject churn in a LeakWatcher even though we
            // expect no registrations to be created - if a refactor of
            // MessageHandler ever auto-registered something on construction
            // for a bare GameObject, the watcher would catch the auxiliary
            // counters (Interceptors, PostProcessors, GlobalAcceptAll) that
            // the per-bucket assertions below do not cover.
            using LeakWatcher watcher = new(messageBus, label: nameof(NonMessagingObjects));

            GameObject test1 = new("NonMessaging1");
            _spawned.Add(test1);

            AssertRegistrationCounts(
                messageBus,
                untargeted: 0,
                targeted: 0,
                broadcast: 0,
                context: "plain GameObject (no MessageAware components)"
            );

            GameObject test2 = new("NonMessaging1", typeof(SpriteRenderer), typeof(MessageHandler));
            _spawned.Add(test2);

            AssertRegistrationCounts(
                messageBus,
                untargeted: 0,
                targeted: 0,
                broadcast: 0,
                context: "GameObject with SpriteRenderer + bare MessageHandler"
            );
        }

        [UnityTest]
        public IEnumerator DedupedRegistration()
        {
            GameObject test = new(nameof(DedupedRegistration), typeof(SimpleMessageAwareComponent));
            _spawned.Add(test);

            SimpleMessageAwareComponent component =
                test.GetComponent<SimpleMessageAwareComponent>();

            int unTargetedCount = 0;
            int targetedCount = 0;
            int broadcastCount = 0;
            int componentTargetedCount = 0;
            int componentBroadcastCount = 0;
            component.untargetedHandler = () => ++unTargetedCount;
            component.targetedHandler = () => ++targetedCount;
            component.broadcastHandler = () => ++broadcastCount;
            component.componentTargetedHandler = () => ++componentTargetedCount;
            component.componentBroadcastHandler = () => ++componentBroadcastCount;

            MessageRegistrationToken token = GetToken(component);
            HashSet<MessageRegistrationHandle> handles = new();
            try
            {
                IEnumerator TestLifecycle(
                    Action emitMessage,
                    MessageRegistrationHandle handle,
                    Func<int> count
                )
                {
                    _ = handles.Add(handle);
                    emitMessage();
                    Assert.AreEqual(1, count());

                    component.enabled = false;
                    yield return null;

                    emitMessage();
                    Assert.AreEqual(1, count());
                    emitMessage();
                    Assert.AreEqual(1, count());
                    component.enabled = true;
                    yield return null;

                    emitMessage();
                    Assert.AreEqual(2, count());

                    _ = handles.Remove(handle);
                    token.RemoveRegistration(handle);

                    emitMessage();
                    // The existing handler should have picked it up
                    Assert.AreEqual(3, count());

                    emitMessage();
                    // The existing handler should have picked it up
                    Assert.AreEqual(4, count());
                }

                // Untargeted
                SimpleUntargetedMessage untargetedMessage = new();
                MessageRegistrationHandle untargetedHandle =
                    token.RegisterUntargeted<SimpleUntargetedMessage>(
                        component.HandleSimpleUntargetedMessage
                    );
                IEnumerator untargetedLifecycle = TestLifecycle(
                    () => untargetedMessage.EmitUntargeted(),
                    untargetedHandle,
                    () => unTargetedCount
                );
                while (untargetedLifecycle.MoveNext())
                {
                    yield return untargetedLifecycle.Current;
                }

                // Targeted
                MessageRegistrationHandle targetedHandle =
                    token.RegisterGameObjectTargeted<SimpleTargetedMessage>(
                        test,
                        component.HandleSimpleTargetedMessage
                    );
                SimpleTargetedMessage targetedMessage = new();
                IEnumerator targetedLifecycle = TestLifecycle(
                    () => targetedMessage.EmitGameObjectTargeted(test),
                    targetedHandle,
                    () => targetedCount
                );
                while (targetedLifecycle.MoveNext())
                {
                    yield return targetedLifecycle.Current;
                }

                // Broadcast
                MessageRegistrationHandle broadcastHandle =
                    token.RegisterGameObjectBroadcast<SimpleBroadcastMessage>(
                        test,
                        component.HandleSimpleBroadcastMessage
                    );
                SimpleBroadcastMessage broadcastMessage = new();
                IEnumerator broadcastLifecycle = TestLifecycle(
                    () => broadcastMessage.EmitGameObjectBroadcast(test),
                    broadcastHandle,
                    () => broadcastCount
                );
                while (broadcastLifecycle.MoveNext())
                {
                    yield return broadcastLifecycle.Current;
                }

                // Component Targeted
                MessageRegistrationHandle componentTargetedHandle =
                    token.RegisterComponentTargeted<SimpleTargetedMessage>(
                        component,
                        component.HandleSimpleComponentTargetedMessage
                    );
                IEnumerator componentTargetedLifecycle = TestLifecycle(
                    () => targetedMessage.EmitComponentTargeted(component),
                    componentTargetedHandle,
                    () => componentTargetedCount
                );
                while (componentTargetedLifecycle.MoveNext())
                {
                    yield return componentTargetedLifecycle.Current;
                }

                // Component Broadcast
                MessageRegistrationHandle componentBroadcastHandle =
                    token.RegisterGameObjectBroadcast<SimpleBroadcastMessage>(
                        test,
                        component.HandleSimpleBroadcastMessage
                    );
                IEnumerator componentBroadcastLifecycle = TestLifecycle(
                    () => broadcastMessage.EmitComponentBroadcast(component),
                    componentBroadcastHandle,
                    () => componentBroadcastCount
                );
                while (componentBroadcastLifecycle.MoveNext())
                {
                    yield return componentBroadcastLifecycle.Current;
                }
            }
            finally
            {
                foreach (MessageRegistrationHandle handle in handles)
                {
                    token.RemoveRegistration(handle);
                }
            }
        }

        [Test]
        public void Interceptors()
        {
            GameObject test = new(nameof(Interceptors), typeof(SimpleMessageAwareComponent));
            _spawned.Add(test);

            SimpleMessageAwareComponent component =
                test.GetComponent<SimpleMessageAwareComponent>();

            int unTargetedCount = 0;
            int targetedCount = 0;
            int broadcastCount = 0;
            int componentTargetedCount = 0;
            int componentBroadcastCount = 0;
            component.untargetedHandler = () => ++unTargetedCount;
            component.targetedHandler = () => ++targetedCount;
            component.broadcastHandler = () => ++broadcastCount;
            component.componentTargetedHandler = () => ++componentTargetedCount;
            component.componentBroadcastHandler = () => ++componentBroadcastCount;

            MessageRegistrationToken token = GetToken(component);
            HashSet<MessageRegistrationHandle> handles = new();
            try
            {
                bool allowed = true;

                bool UntargetedInterceptor(ref SimpleUntargetedMessage message)
                {
                    return allowed;
                }

                bool TargetedInterceptor(ref InstanceId target, ref SimpleTargetedMessage message)
                {
                    return allowed;
                }

                bool BroadcastInterceptor(ref InstanceId source, ref SimpleBroadcastMessage message)
                {
                    return allowed;
                }

                // Double register to ensure no bugs
                MessageRegistrationHandle untargetedInterceptor1 =
                    token.RegisterUntargetedInterceptor<SimpleUntargetedMessage>(
                        UntargetedInterceptor
                    );
                handles.Add(untargetedInterceptor1);
                MessageRegistrationHandle untargetedInterceptor2 =
                    token.RegisterUntargetedInterceptor<SimpleUntargetedMessage>(
                        UntargetedInterceptor
                    );
                handles.Add(untargetedInterceptor2);
                MessageRegistrationHandle targetedInterceptor1 =
                    token.RegisterTargetedInterceptor<SimpleTargetedMessage>(TargetedInterceptor);
                handles.Add(targetedInterceptor1);
                MessageRegistrationHandle targetedInterceptor2 =
                    token.RegisterTargetedInterceptor<SimpleTargetedMessage>(TargetedInterceptor);
                handles.Add(targetedInterceptor2);
                MessageRegistrationHandle broadcastInterceptor1 =
                    token.RegisterBroadcastInterceptor<SimpleBroadcastMessage>(
                        BroadcastInterceptor
                    );
                handles.Add(broadcastInterceptor1);
                MessageRegistrationHandle broadcastInterceptor2 =
                    token.RegisterBroadcastInterceptor<SimpleBroadcastMessage>(
                        BroadcastInterceptor
                    );
                handles.Add(broadcastInterceptor2);

                void RunTest(Action emitMessage, Func<int> count)
                {
                    allowed = true;
                    emitMessage();
                    Assert.AreEqual(1, count());
                    allowed = false;
                    emitMessage();
                    Assert.AreEqual(1, count());
                    emitMessage();
                    Assert.AreEqual(1, count());
                    allowed = true;
                    emitMessage();
                    Assert.AreEqual(2, count());
                    emitMessage();
                    Assert.AreEqual(3, count());
                    allowed = false;
                    emitMessage();
                    Assert.AreEqual(3, count());
                }

                SimpleUntargetedMessage untargetedMessage = new();
                RunTest(() => untargetedMessage.EmitUntargeted(), () => unTargetedCount);
                SimpleTargetedMessage targetedMessage = new();
                RunTest(() => targetedMessage.EmitGameObjectTargeted(test), () => targetedCount);
                RunTest(
                    () => targetedMessage.EmitComponentTargeted(component),
                    () => componentTargetedCount
                );
                SimpleBroadcastMessage broadcastMessage = new();
                RunTest(() => broadcastMessage.EmitGameObjectBroadcast(test), () => broadcastCount);
                RunTest(
                    () => broadcastMessage.EmitComponentBroadcast(component),
                    () => componentBroadcastCount
                );
            }
            finally
            {
                foreach (MessageRegistrationHandle handle in handles)
                {
                    token.RemoveRegistration(handle);
                }
            }
        }

        [Test]
        public void PostProcessors()
        {
            GameObject test = new(nameof(PostProcessors), typeof(SimpleMessageAwareComponent));
            _spawned.Add(test);

            SimpleMessageAwareComponent component =
                test.GetComponent<SimpleMessageAwareComponent>();

            int unTargetedCount = 0;
            int targetedCount = 0;
            int broadcastCount = 0;
            int componentTargetedCount = 0;
            int componentBroadcastCount = 0;
            int expectedTargetedWithoutTargetingCount = 0;
            int expectedBroadcastWithoutSourceCount = 0;
            int? lastSeenUntargetedCount = null;
            int? lastSeenTargetedCount = null;
            int? lastSeenComponentTargetedCount = null;
            int? lastSeenBroadcastCount = null;
            int? lastSeenComponentBroadcastCount = null;
            component.untargetedHandler = () => ++unTargetedCount;
            component.targetedHandler = () =>
            {
                ++targetedCount;
                ++expectedTargetedWithoutTargetingCount;
            };
            component.broadcastHandler = () =>
            {
                ++broadcastCount;
                ++expectedBroadcastWithoutSourceCount;
            };
            component.componentTargetedHandler = () =>
            {
                ++componentTargetedCount;
                ++expectedTargetedWithoutTargetingCount;
            };
            component.componentBroadcastHandler = () =>
            {
                ++componentBroadcastCount;
                ++expectedBroadcastWithoutSourceCount;
            };

            MessageRegistrationToken token = GetToken(component);
            HashSet<MessageRegistrationHandle> handles = new();
            try
            {
                void UntargetedPostProcessor(ref SimpleUntargetedMessage message)
                {
                    if (lastSeenUntargetedCount == null)
                    {
                        lastSeenUntargetedCount = unTargetedCount;
                        return;
                    }

                    Assert.AreEqual(lastSeenUntargetedCount + 1, unTargetedCount);
                    lastSeenUntargetedCount = unTargetedCount;
                }

                void GameObjectTargetedPostProcessor(ref SimpleTargetedMessage message)
                {
                    if (lastSeenTargetedCount == null)
                    {
                        return;
                    }

                    Assert.AreEqual(lastSeenTargetedCount + 1, targetedCount);
                }

                void ComponentTargetedPostProcessor(ref SimpleTargetedMessage message)
                {
                    if (lastSeenComponentTargetedCount == null)
                    {
                        return;
                    }

                    Assert.AreEqual(lastSeenComponentTargetedCount + 1, componentTargetedCount);
                }

                void TargetedWithoutTargetingPostProcessor(
                    ref InstanceId target,
                    ref SimpleTargetedMessage message
                )
                {
                    switch (target.Object)
                    {
                        case GameObject _:
                        {
                            if (lastSeenTargetedCount != null)
                            {
                                Assert.AreEqual(lastSeenTargetedCount + 1, targetedCount);
                            }

                            lastSeenTargetedCount = targetedCount;
                            break;
                        }
                        case Component _:
                        {
                            if (lastSeenComponentTargetedCount != null)
                            {
                                Assert.AreEqual(
                                    lastSeenComponentTargetedCount + 1,
                                    componentTargetedCount
                                );
                            }

                            lastSeenComponentTargetedCount = componentTargetedCount;
                            break;
                        }
                        default:
                            Assert.Fail("Unexpected Object type - {0}.", target.Object?.GetType());
                            break;
                    }

                    Assert.AreEqual(
                        expectedTargetedWithoutTargetingCount,
                        targetedCount + componentTargetedCount
                    );
                }

                void GameObjectBroadcastPostProcessor(ref SimpleBroadcastMessage message)
                {
                    if (lastSeenBroadcastCount == null)
                    {
                        return;
                    }

                    Assert.AreEqual(lastSeenBroadcastCount + 1, broadcastCount);
                }

                void ComponentBroadcastPostProcessor(ref SimpleBroadcastMessage message)
                {
                    if (lastSeenComponentBroadcastCount == null)
                    {
                        return;
                    }

                    Assert.AreEqual(lastSeenComponentBroadcastCount + 1, componentBroadcastCount);
                }

                void BroadcastWithoutSourcePostProcessor(
                    ref InstanceId source,
                    ref SimpleBroadcastMessage message
                )
                {
                    switch (source.Object)
                    {
                        case GameObject _:
                        {
                            if (lastSeenBroadcastCount != null)
                            {
                                Assert.AreEqual(lastSeenBroadcastCount + 1, broadcastCount);
                            }

                            lastSeenBroadcastCount = broadcastCount;
                            break;
                        }
                        case Component _:
                        {
                            if (lastSeenComponentBroadcastCount != null)
                            {
                                Assert.AreEqual(
                                    lastSeenComponentBroadcastCount + 1,
                                    componentBroadcastCount
                                );
                            }

                            lastSeenComponentBroadcastCount = componentBroadcastCount;
                            break;
                        }
                        default:
                            Assert.Fail("Unexpected Object type - {0}.", source.Object?.GetType());
                            break;
                    }

                    Assert.AreEqual(
                        expectedBroadcastWithoutSourceCount,
                        broadcastCount + componentBroadcastCount
                    );
                }

                MessageRegistrationHandle untargetedPostProcessor =
                    token.RegisterUntargetedPostProcessor<SimpleUntargetedMessage>(
                        UntargetedPostProcessor
                    );
                handles.Add(untargetedPostProcessor);
                MessageRegistrationHandle gameObjectTargetedPostProcessor =
                    token.RegisterGameObjectTargetedPostProcessor<SimpleTargetedMessage>(
                        test,
                        GameObjectTargetedPostProcessor
                    );
                handles.Add(gameObjectTargetedPostProcessor);
                MessageRegistrationHandle componentTargetedPostProcessor =
                    token.RegisterComponentTargetedPostProcessor<SimpleTargetedMessage>(
                        component,
                        ComponentTargetedPostProcessor
                    );
                handles.Add(componentTargetedPostProcessor);
                MessageRegistrationHandle targetedWithoutTargetingPostProcessor =
                    token.RegisterTargetedWithoutTargetingPostProcessor<SimpleTargetedMessage>(
                        TargetedWithoutTargetingPostProcessor
                    );
                handles.Add(targetedWithoutTargetingPostProcessor);
                MessageRegistrationHandle gameObjectBroadcastPostProcessor =
                    token.RegisterGameObjectBroadcastPostProcessor<SimpleBroadcastMessage>(
                        test,
                        GameObjectBroadcastPostProcessor
                    );
                handles.Add(gameObjectBroadcastPostProcessor);
                MessageRegistrationHandle componentBroadcastPostProcessor =
                    token.RegisterComponentBroadcastPostProcessor<SimpleBroadcastMessage>(
                        component,
                        ComponentBroadcastPostProcessor
                    );
                handles.Add(componentBroadcastPostProcessor);
                MessageRegistrationHandle broadcastWithoutSourcePostProcessor =
                    token.RegisterBroadcastWithoutSourcePostProcessor<SimpleBroadcastMessage>(
                        BroadcastWithoutSourcePostProcessor
                    );
                handles.Add(broadcastWithoutSourcePostProcessor);

                void RunTest(Action emitMessage, Func<int> count)
                {
                    Assert.AreEqual(0, count());
                    component.enabled = true;
                    emitMessage();
                    Assert.AreEqual(1, count());
                    component.enabled = false;
                    emitMessage();
                    Assert.AreEqual(1, count());
                    emitMessage();
                    Assert.AreEqual(1, count());
                    component.enabled = true;
                    emitMessage();
                    Assert.AreEqual(2, count());
                    emitMessage();
                    Assert.AreEqual(3, count());
                    component.enabled = false;
                    emitMessage();
                    Assert.AreEqual(3, count());
                }

                SimpleUntargetedMessage untargetedMessage = new();
                RunTest(() => untargetedMessage.EmitUntargeted(), () => unTargetedCount);
                SimpleTargetedMessage targetedMessage = new();
                RunTest(() => targetedMessage.EmitGameObjectTargeted(test), () => targetedCount);
                RunTest(
                    () => targetedMessage.EmitComponentTargeted(component),
                    () => componentTargetedCount
                );
                SimpleBroadcastMessage broadcastMessage = new();
                RunTest(() => broadcastMessage.EmitGameObjectBroadcast(test), () => broadcastCount);
                RunTest(
                    () => broadcastMessage.EmitComponentBroadcast(component),
                    () => componentBroadcastCount
                );
            }
            finally
            {
                foreach (MessageRegistrationHandle handle in handles)
                {
                    token.RemoveRegistration(handle);
                }
            }
        }

        [Test]
        public void InstanceId()
        {
            GameObject test = new(nameof(InstanceId), typeof(SimpleMessageAwareComponent));
            _spawned.Add(test);

            // Message-aware instance ids
            SimpleMessageAwareComponent component =
                test.GetComponent<SimpleMessageAwareComponent>();
            Assert.AreEqual((InstanceId)test, (InstanceId)test);
            Assert.AreEqual((InstanceId)component, (InstanceId)component);
            Assert.AreNotEqual((InstanceId)test, (InstanceId)component);
            Assert.AreNotEqual((InstanceId)component, (InstanceId)test);

            // Non-message-aware instance ids
            GameObject test2 = new(nameof(InstanceId) + " - 2");
            _spawned.Add(test2);
            Assert.AreNotEqual((InstanceId)test2, (InstanceId)test2.transform);
            Assert.AreNotEqual((InstanceId)test2.transform, (InstanceId)test2);

            // Null checks
            bool caught = false;
            try
            {
                _ = (InstanceId)(GameObject)null;
            }
            catch
            {
                caught = true;
            }

            Assert.IsTrue(caught);

            caught = false;
            try
            {
                _ = (InstanceId)(Component)null;
            }
            catch
            {
                caught = true;
            }

            Assert.IsTrue(caught);
        }

        [Test]
        public void GlobalAcceptAll()
        {
            GameObject test = new(nameof(GlobalAcceptAll), typeof(SimpleMessageAwareComponent));
            _spawned.Add(test);
            SimpleMessageAwareComponent component =
                test.GetComponent<SimpleMessageAwareComponent>();

            MessageRegistrationToken token = GetToken(component);

            int untargetedCount = 0;
            int targetedCount = 0;
            int broadcastCount = 0;
            int fastUntargetedCount = 0;
            int fastTargetedCount = 0;
            int fastBroadcastCount = 0;

            HashSet<MessageRegistrationHandle> handles = new();
            try
            {
                MessageRegistrationHandle firstHandle = token.RegisterGlobalAcceptAll(
                    HandleUntargeted,
                    HandleTargeted,
                    HandleBroadcast
                );
                _ = handles.Add(firstHandle);

                // Untargeted
                SimpleUntargetedMessage untargetedMessage = new();
                untargetedMessage.EmitUntargeted();
                Assert.AreEqual(1, untargetedCount);
                Assert.AreEqual(0, targetedCount);
                Assert.AreEqual(0, broadcastCount);
                Assert.AreEqual(0, fastUntargetedCount);
                Assert.AreEqual(0, fastTargetedCount);
                Assert.AreEqual(0, fastBroadcastCount);
                untargetedMessage.EmitUntargeted();
                Assert.AreEqual(2, untargetedCount);
                Assert.AreEqual(0, targetedCount);
                Assert.AreEqual(0, broadcastCount);
                Assert.AreEqual(0, fastUntargetedCount);
                Assert.AreEqual(0, fastTargetedCount);
                Assert.AreEqual(0, fastBroadcastCount);

                // Targeted
                SimpleTargetedMessage targetedMessage = new();
                targetedMessage.EmitGameObjectTargeted(test);
                Assert.AreEqual(2, untargetedCount);
                Assert.AreEqual(1, targetedCount);
                Assert.AreEqual(0, broadcastCount);
                Assert.AreEqual(0, fastUntargetedCount);
                Assert.AreEqual(0, fastTargetedCount);
                Assert.AreEqual(0, fastBroadcastCount);
                targetedMessage.EmitComponentTargeted(component);
                Assert.AreEqual(2, untargetedCount);
                Assert.AreEqual(2, targetedCount);
                Assert.AreEqual(0, broadcastCount);
                Assert.AreEqual(0, fastUntargetedCount);
                Assert.AreEqual(0, fastTargetedCount);
                Assert.AreEqual(0, fastBroadcastCount);

                // Broadcast
                SimpleBroadcastMessage broadcastMessage = new();
                broadcastMessage.EmitGameObjectBroadcast(test);
                Assert.AreEqual(2, untargetedCount);
                Assert.AreEqual(2, targetedCount);
                Assert.AreEqual(1, broadcastCount);
                Assert.AreEqual(0, fastUntargetedCount);
                Assert.AreEqual(0, fastTargetedCount);
                Assert.AreEqual(0, fastBroadcastCount);
                broadcastMessage.EmitComponentBroadcast(component);
                Assert.AreEqual(2, untargetedCount);
                Assert.AreEqual(2, targetedCount);
                Assert.AreEqual(2, broadcastCount);
                Assert.AreEqual(0, fastUntargetedCount);
                Assert.AreEqual(0, fastTargetedCount);
                Assert.AreEqual(0, fastBroadcastCount);

                component.enabled = false;
                int noMatchingCount = 0;
                Action<LogLevel, string> previousLog = MessagingDebug.LogFunction;
                try
                {
                    MessagingDebug.LogFunction = (_, logMessage) =>
                    {
                        if (logMessage.Contains("matching"))
                        {
                            ++noMatchingCount;
                        }
                    };
                    untargetedMessage.EmitUntargeted();
                    Assert.AreEqual(1, noMatchingCount);
                    targetedMessage.EmitGameObjectTargeted(test);
                    Assert.AreEqual(2, noMatchingCount);
                    targetedMessage.EmitComponentTargeted(component);
                    Assert.AreEqual(3, noMatchingCount);
                    broadcastMessage.EmitGameObjectBroadcast(test);
                    Assert.AreEqual(4, noMatchingCount);
                    broadcastMessage.EmitComponentBroadcast(component);
                    Assert.AreEqual(5, noMatchingCount);
                }
                finally
                {
                    MessagingDebug.LogFunction = previousLog;
                }

                Assert.AreEqual(2, untargetedCount);
                Assert.AreEqual(2, targetedCount);
                Assert.AreEqual(2, broadcastCount);
                Assert.AreEqual(0, fastUntargetedCount);
                Assert.AreEqual(0, fastTargetedCount);
                Assert.AreEqual(0, fastBroadcastCount);

                component.enabled = true;
                MessageRegistrationHandle secondHandle = token.RegisterGlobalAcceptAll(
                    HandleUntargeted,
                    HandleTargeted,
                    HandleBroadcast
                );
                _ = handles.Add(secondHandle);
                untargetedMessage.EmitUntargeted();
                targetedMessage.EmitGameObjectTargeted(test);
                targetedMessage.EmitComponentTargeted(component);
                broadcastMessage.EmitGameObjectBroadcast(test);
                broadcastMessage.EmitComponentBroadcast(component);
                Assert.AreEqual(3, untargetedCount);
                Assert.AreEqual(4, targetedCount);
                Assert.AreEqual(4, broadcastCount);
                Assert.AreEqual(0, fastUntargetedCount);
                Assert.AreEqual(0, fastTargetedCount);
                Assert.AreEqual(0, fastBroadcastCount);

                MessageRegistrationHandle thirdHandle = token.RegisterGlobalAcceptAll(
                    HandleFastUntargeted,
                    HandleFastTargeted,
                    HandleFastBroadcast
                );
                _ = handles.Add(thirdHandle);
                untargetedMessage.EmitUntargeted();
                targetedMessage.EmitGameObjectTargeted(test);
                targetedMessage.EmitComponentTargeted(component);
                broadcastMessage.EmitGameObjectBroadcast(test);
                broadcastMessage.EmitComponentBroadcast(component);
                Assert.AreEqual(4, untargetedCount);
                Assert.AreEqual(6, targetedCount);
                Assert.AreEqual(6, broadcastCount);
                Assert.AreEqual(1, fastUntargetedCount);
                Assert.AreEqual(2, fastTargetedCount);
                Assert.AreEqual(2, fastBroadcastCount);

                MessageRegistrationHandle fourthHandle = token.RegisterGlobalAcceptAll(
                    HandleFastUntargeted,
                    HandleFastTargeted,
                    HandleFastBroadcast
                );
                _ = handles.Add(fourthHandle);
                untargetedMessage.EmitUntargeted();
                targetedMessage.EmitGameObjectTargeted(test);
                targetedMessage.EmitComponentTargeted(component);
                broadcastMessage.EmitGameObjectBroadcast(test);
                broadcastMessage.EmitComponentBroadcast(component);
                Assert.AreEqual(5, untargetedCount);
                Assert.AreEqual(8, targetedCount);
                Assert.AreEqual(8, broadcastCount);
                Assert.AreEqual(2, fastUntargetedCount);
                Assert.AreEqual(4, fastTargetedCount);
                Assert.AreEqual(4, fastBroadcastCount);
            }
            finally
            {
                foreach (MessageRegistrationHandle handle in handles)
                {
                    token.RemoveRegistration(handle);
                }
            }

            return;

            void HandleUntargeted(IUntargetedMessage message)
            {
                ++untargetedCount;
            }

            void HandleFastUntargeted(ref IUntargetedMessage message)
            {
                ++fastUntargetedCount;
            }

            void HandleTargeted(InstanceId target, ITargetedMessage message)
            {
                ++targetedCount;
            }

            void HandleFastTargeted(ref InstanceId target, ref ITargetedMessage message)
            {
                ++fastTargetedCount;
            }

            void HandleBroadcast(InstanceId source, IBroadcastMessage message)
            {
                ++broadcastCount;
            }

            void HandleFastBroadcast(ref InstanceId source, ref IBroadcastMessage message)
            {
                ++fastBroadcastCount;
            }
        }

        [Test]
        public void InterceptorOrder()
        {
            GameObject test = new(nameof(InterceptorOrder), typeof(SimpleMessageAwareComponent));
            _spawned.Add(test);

            SimpleMessageAwareComponent component =
                test.GetComponent<SimpleMessageAwareComponent>();

            bool seen = false;
            component.untargetedHandler = () =>
            {
                // ReSharper disable once AccessToModifiedClosure
                Assert.IsFalse(seen);
                seen = true;
            };

            MessageRegistrationToken token = GetToken(component);
            HashSet<MessageRegistrationHandle> handles = new();
            try
            {
                int seenCount = 0;

                bool UntargetedInterceptorFirstPriority(ref SimpleUntargetedMessage message)
                {
                    return seenCount++ % 3 == 0;
                }

                bool UntargetedInterceptorSecondPriority(ref SimpleUntargetedMessage message)
                {
                    return seenCount++ % 3 == 1;
                }

                bool UntargetedInterceptorThirdPriority(ref SimpleUntargetedMessage message)
                {
                    return seenCount++ % 3 == 2;
                }

                int interceptorRunCount = 0;

                bool UntargetedInterceptorFourthPriority(ref SimpleUntargetedMessage message)
                {
                    // We should be running close to first and last
                    if (++interceptorRunCount % 2 == 0)
                    {
                        return seenCount % 3 == 0;
                    }

                    return true;
                }

                MessageRegistrationHandle secondInterceptor =
                    token.RegisterUntargetedInterceptor<SimpleUntargetedMessage>(
                        UntargetedInterceptorSecondPriority,
                        100
                    );
                _ = handles.Add(secondInterceptor);
                MessageRegistrationHandle thirdInterceptor =
                    token.RegisterUntargetedInterceptor<SimpleUntargetedMessage>(
                        UntargetedInterceptorThirdPriority,
                        101
                    );
                _ = handles.Add(thirdInterceptor);
                MessageRegistrationHandle firstInterceptor =
                    token.RegisterUntargetedInterceptor<SimpleUntargetedMessage>(
                        UntargetedInterceptorFirstPriority,
                        -1
                    );
                _ = handles.Add(firstInterceptor);
                MessageRegistrationHandle fourthInterceptorFirstPriority =
                    token.RegisterUntargetedInterceptor<SimpleUntargetedMessage>(
                        UntargetedInterceptorFourthPriority,
                        -1
                    );
                _ = handles.Add(fourthInterceptorFirstPriority);
                MessageRegistrationHandle fourthInterceptorSecondPriority =
                    token.RegisterUntargetedInterceptor<SimpleUntargetedMessage>(
                        UntargetedInterceptorFourthPriority,
                        102
                    );
                _ = handles.Add(fourthInterceptorSecondPriority);

                SimpleUntargetedMessage message = new();
                message.EmitUntargeted();
                Assert.IsTrue(seen);
                Assert.AreEqual(2, interceptorRunCount);
                seen = false;

                message.EmitUntargeted();
                Assert.IsTrue(seen);
                Assert.AreEqual(4, interceptorRunCount);
                seen = false;

                MessageRegistrationHandle doubleRegistrationOne =
                    token.RegisterUntargetedInterceptor<SimpleUntargetedMessage>(
                        UntargetedInterceptorFirstPriority,
                        -1
                    );
                _ = handles.Add(doubleRegistrationOne);
                message.EmitUntargeted();
                Assert.IsTrue(seen);
                Assert.AreEqual(6, interceptorRunCount);
                seen = false;

                _ = handles.Remove(firstInterceptor);
                token.RemoveRegistration(firstInterceptor);
                message.EmitUntargeted();
                Assert.IsTrue(seen);
                Assert.AreEqual(8, interceptorRunCount);
                seen = false;

                // Double remove
                token.RemoveRegistration(firstInterceptor);
                token.RemoveRegistration(firstInterceptor);
                message.EmitUntargeted();
                Assert.IsTrue(seen);
                Assert.AreEqual(10, interceptorRunCount);
            }
            finally
            {
                foreach (MessageRegistrationHandle handle in handles)
                {
                    token.RemoveRegistration(handle);
                }
            }
        }

        [Test]
        public void UntargetedRemoveOrder()
        {
            GameObject test = new(
                nameof(UntargetedRemoveOrder),
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(test);

            SimpleMessageAwareComponent component =
                test.GetComponent<SimpleMessageAwareComponent>();

            MessageRegistrationToken token = GetToken(component);

            int callCount = 0;
            int fastCallCount = 0;

            SimpleUntargetedMessage message = new();
            int expectedCallCount = 0;
            Run(
                () => new[] { token.RegisterUntargeted<SimpleUntargetedMessage>(HandleUntargeted) },
                () => message.EmitUntargeted(),
                () =>
                {
                    Assert.AreEqual(++expectedCallCount, callCount);
                    Assert.AreEqual(0, fastCallCount);
                },
                () =>
                {
                    Assert.AreEqual(expectedCallCount, callCount);
                    Assert.AreEqual(0, fastCallCount);
                },
                token
            );

            callCount = 0;
            expectedCallCount = 0;
            Run(
                () =>
                    new[]
                    {
                        token.RegisterUntargeted<SimpleUntargetedMessage>(HandleFastUntargeted),
                    },
                () => message.EmitUntargeted(),
                () =>
                {
                    Assert.AreEqual(++expectedCallCount, fastCallCount);
                    Assert.AreEqual(0, callCount);
                },
                () =>
                {
                    Assert.AreEqual(expectedCallCount, fastCallCount);
                    Assert.AreEqual(0, callCount);
                },
                token
            );

            callCount = 0;
            fastCallCount = 0;
            Run(
                () =>
                {
                    return new[]
                    {
                        token.RegisterUntargeted<SimpleUntargetedMessage>(HandleFastUntargeted),
                        token.RegisterUntargeted<SimpleUntargetedMessage>(HandleUntargeted),
                    };
                },
                () => message.EmitUntargeted(),
                () => { },
                () =>
                {
                    Assert.AreNotEqual(callCount, fastCallCount);
                },
                token
            );

            return;

            void HandleUntargeted(SimpleUntargetedMessage message)
            {
                ++callCount;
            }

            void HandleFastUntargeted(ref SimpleUntargetedMessage message)
            {
                ++fastCallCount;
            }
        }

        [Test]
        public void TargetedRemoveOrder()
        {
            GameObject test = new(nameof(TargetedRemoveOrder), typeof(SimpleMessageAwareComponent));
            _spawned.Add(test);

            SimpleMessageAwareComponent component =
                test.GetComponent<SimpleMessageAwareComponent>();

            MessageRegistrationToken token = GetToken(component);

            int callCount = 0;
            int fastCallCount = 0;

            SimpleTargetedMessage message = new();
            int expectedCallCount = 0;
            Run(
                () =>
                    new[]
                    {
                        token.RegisterGameObjectTargeted<SimpleTargetedMessage>(
                            test,
                            HandleTargeted
                        ),
                    },
                () =>
                {
                    message.EmitComponentTargeted(component);
                    message.EmitGameObjectTargeted(test);
                },
                () =>
                {
                    Assert.AreEqual(++expectedCallCount, callCount);
                    Assert.AreEqual(0, fastCallCount);
                },
                () =>
                {
                    Assert.AreEqual(expectedCallCount, callCount);
                    Assert.AreEqual(0, fastCallCount);
                },
                token
            );

            callCount = 0;
            expectedCallCount = 0;
            Run(
                () =>
                    new[]
                    {
                        token.RegisterGameObjectTargeted<SimpleTargetedMessage>(
                            test,
                            HandleFastTargeted
                        ),
                    },
                () =>
                {
                    message.EmitComponentTargeted(component);
                    message.EmitGameObjectTargeted(test);
                },
                () =>
                {
                    Assert.AreEqual(++expectedCallCount, fastCallCount);
                    Assert.AreEqual(0, callCount);
                },
                () =>
                {
                    Assert.AreEqual(expectedCallCount, fastCallCount);
                    Assert.AreEqual(0, callCount);
                },
                token
            );

            callCount = 0;
            fastCallCount = 0;
            Run(
                () =>
                {
                    return new[]
                    {
                        token.RegisterGameObjectTargeted<SimpleTargetedMessage>(
                            test,
                            HandleFastTargeted
                        ),
                        token.RegisterGameObjectTargeted<SimpleTargetedMessage>(
                            test,
                            HandleTargeted
                        ),
                    };
                },
                () =>
                {
                    message.EmitComponentTargeted(component);
                    message.EmitGameObjectTargeted(test);
                },
                () => { },
                () =>
                {
                    Assert.AreNotEqual(callCount, fastCallCount);
                },
                token
            );

            callCount = 0;
            fastCallCount = 0;
            Run(
                () =>
                {
                    return new[]
                    {
                        token.RegisterComponentTargeted<SimpleTargetedMessage>(
                            component,
                            HandleFastTargeted
                        ),
                        token.RegisterGameObjectTargeted<SimpleTargetedMessage>(
                            test,
                            HandleTargeted
                        ),
                    };
                },
                () =>
                {
                    message.EmitComponentTargeted(component);
                    message.EmitGameObjectTargeted(test);
                },
                () => { },
                () =>
                {
                    Assert.AreNotEqual(callCount, fastCallCount);
                },
                token
            );

            callCount = 0;
            fastCallCount = 0;
            Run(
                () =>
                {
                    return new[]
                    {
                        token.RegisterComponentTargeted<SimpleTargetedMessage>(
                            component,
                            HandleFastTargeted
                        ),
                        token.RegisterComponentTargeted<SimpleTargetedMessage>(
                            component,
                            HandleTargeted
                        ),
                    };
                },
                () =>
                {
                    message.EmitComponentTargeted(component);
                    message.EmitGameObjectTargeted(test);
                },
                () => { },
                () =>
                {
                    Assert.AreNotEqual(callCount, fastCallCount);
                },
                token
            );

            return;

            void HandleTargeted(SimpleTargetedMessage message)
            {
                ++callCount;
            }

            void HandleFastTargeted(ref SimpleTargetedMessage message)
            {
                ++fastCallCount;
            }
        }

        [Test]
        public void BroadcastRemoveOrder()
        {
            GameObject test = new(
                nameof(BroadcastRemoveOrder),
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(test);

            SimpleMessageAwareComponent component =
                test.GetComponent<SimpleMessageAwareComponent>();

            MessageRegistrationToken token = GetToken(component);
            int callCount = 0;
            int fastCallCount = 0;

            SimpleBroadcastMessage message = new();
            int expectedCallCount = 0;
            Run(
                () =>
                    new[]
                    {
                        token.RegisterGameObjectBroadcast<SimpleBroadcastMessage>(
                            test,
                            HandleBroadcast
                        ),
                    },
                () =>
                {
                    message.EmitComponentBroadcast(component);
                    message.EmitGameObjectBroadcast(test);
                },
                () =>
                {
                    Assert.AreEqual(++expectedCallCount, callCount);
                    Assert.AreEqual(0, fastCallCount);
                },
                () =>
                {
                    Assert.AreEqual(expectedCallCount, callCount);
                    Assert.AreEqual(0, fastCallCount);
                },
                token
            );

            callCount = 0;
            expectedCallCount = 0;
            Run(
                () =>
                    new[]
                    {
                        token.RegisterGameObjectBroadcast<SimpleBroadcastMessage>(
                            test,
                            HandleFastBroadcast
                        ),
                    },
                () =>
                {
                    message.EmitComponentBroadcast(component);
                    message.EmitGameObjectBroadcast(test);
                },
                () =>
                {
                    Assert.AreEqual(++expectedCallCount, fastCallCount);
                    Assert.AreEqual(0, callCount);
                },
                () =>
                {
                    Assert.AreEqual(expectedCallCount, fastCallCount);
                    Assert.AreEqual(0, callCount);
                },
                token
            );

            callCount = 0;
            fastCallCount = 0;
            Run(
                () =>
                {
                    return new[]
                    {
                        token.RegisterGameObjectBroadcast<SimpleBroadcastMessage>(
                            test,
                            HandleFastBroadcast
                        ),
                        token.RegisterGameObjectBroadcast<SimpleBroadcastMessage>(
                            test,
                            HandleBroadcast
                        ),
                    };
                },
                () =>
                {
                    message.EmitComponentBroadcast(component);
                    message.EmitGameObjectBroadcast(test);
                },
                () => { },
                () =>
                {
                    Assert.AreNotEqual(callCount, fastCallCount);
                },
                token
            );

            callCount = 0;
            fastCallCount = 0;
            Run(
                () =>
                {
                    return new[]
                    {
                        token.RegisterGameObjectBroadcast<SimpleBroadcastMessage>(
                            test,
                            HandleFastBroadcast
                        ),
                        token.RegisterGameObjectBroadcast<SimpleBroadcastMessage>(
                            test,
                            HandleBroadcast
                        ),
                    };
                },
                () =>
                {
                    message.EmitComponentBroadcast(component);
                    message.EmitGameObjectBroadcast(test);
                },
                () => { },
                () =>
                {
                    Assert.AreNotEqual(callCount, fastCallCount);
                },
                token
            );

            callCount = 0;
            fastCallCount = 0;
            Run(
                () =>
                {
                    return new[]
                    {
                        token.RegisterGameObjectBroadcast<SimpleBroadcastMessage>(
                            test,
                            HandleFastBroadcast
                        ),
                        token.RegisterGameObjectBroadcast<SimpleBroadcastMessage>(
                            test,
                            HandleBroadcast
                        ),
                    };
                },
                () =>
                {
                    message.EmitComponentBroadcast(component);
                    message.EmitGameObjectBroadcast(test);
                },
                () => { },
                () =>
                {
                    Assert.AreNotEqual(callCount, fastCallCount);
                },
                token
            );

            return;

            void HandleBroadcast(SimpleBroadcastMessage message)
            {
                ++callCount;
            }

            void HandleFastBroadcast(ref SimpleBroadcastMessage message)
            {
                ++fastCallCount;
            }
        }
    }
}

#endif
