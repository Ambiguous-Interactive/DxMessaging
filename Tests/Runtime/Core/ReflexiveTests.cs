#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Core.Pooling;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class ReflexiveTests : MessagingTestBase
    {
        [Test]
        public void ReflexiveSendModesRespectHierarchy()
        {
            GameObject grandParent = new(
                nameof(ReflexiveSendModesRespectHierarchy) + "_Grand",
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(grandParent);
            GameObject parent = new(
                nameof(ReflexiveSendModesRespectHierarchy) + "_Parent",
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(parent);
            GameObject child = new(
                nameof(ReflexiveSendModesRespectHierarchy) + "_Child",
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(child);

            parent.transform.SetParent(grandParent.transform);
            child.transform.SetParent(parent.transform);

            SimpleMessageAwareComponent grandComponent =
                grandParent.GetComponent<SimpleMessageAwareComponent>();
            SimpleMessageAwareComponent parentComponent =
                parent.GetComponent<SimpleMessageAwareComponent>();
            SimpleMessageAwareComponent childComponent =
                child.GetComponent<SimpleMessageAwareComponent>();

            int grandCount = 0;
            int parentCount = 0;
            int childCount = 0;
            grandComponent.reflexiveTwoArgumentHandler = () => ++grandCount;
            parentComponent.reflexiveTwoArgumentHandler = () => ++parentCount;
            childComponent.reflexiveTwoArgumentHandler = () => ++childCount;

            // Flat should only target the specified GameObject
            ResetCounters();
            ReflexiveMessage flat = new(
                nameof(SimpleMessageAwareComponent.HandleReflexiveMessageTwoArguments),
                ReflexiveSendMode.Flat,
                1,
                2
            );
            InstanceId parentId = parent;
            flat.EmitTargeted(parentId);
            Assert.AreEqual(0, grandCount);
            Assert.AreEqual(1, parentCount);
            Assert.AreEqual(0, childCount);

            // Downwards should reach parent and descendants
            ResetCounters();
            ReflexiveMessage downwards = new(
                nameof(SimpleMessageAwareComponent.HandleReflexiveMessageTwoArguments),
                ReflexiveSendMode.Downwards,
                1,
                2
            );
            downwards.EmitTargeted(parentId);
            Assert.AreEqual(0, grandCount);
            Assert.AreEqual(1, parentCount);
            Assert.AreEqual(1, childCount);

            // Upwards should reach target and all ancestors
            ResetCounters();
            ReflexiveMessage upwards = new(
                nameof(SimpleMessageAwareComponent.HandleReflexiveMessageTwoArguments),
                ReflexiveSendMode.Upwards,
                1,
                2
            );
            InstanceId childId = child;
            upwards.EmitTargeted(childId);
            Assert.AreEqual(1, grandCount);
            Assert.AreEqual(1, parentCount);
            Assert.AreEqual(1, childCount);

            // Combination of Upwards & Downwards should reach entire hierarchy once
            ResetCounters();
            ReflexiveMessage bothDirections = new(
                nameof(SimpleMessageAwareComponent.HandleReflexiveMessageTwoArguments),
                ReflexiveSendMode.Upwards | ReflexiveSendMode.Downwards,
                1,
                2
            );
            bothDirections.EmitTargeted(parentId);
            Assert.AreEqual(1, grandCount);
            Assert.AreEqual(1, parentCount);
            Assert.AreEqual(1, childCount);

            // OnlyIncludeActive should skip disabled receivers
            ResetCounters();
            childComponent.enabled = false;
            ReflexiveMessage downwardsActiveOnly = new(
                nameof(SimpleMessageAwareComponent.HandleReflexiveMessageTwoArguments),
                ReflexiveSendMode.Downwards | ReflexiveSendMode.OnlyIncludeActive,
                1,
                2
            );
            downwardsActiveOnly.EmitTargeted(parentId);
            Assert.AreEqual(0, grandCount);
            Assert.AreEqual(1, parentCount);
            Assert.AreEqual(0, childCount);
            childComponent.enabled = true;

            return;

            void ResetCounters()
            {
                grandCount = 0;
                parentCount = 0;
                childCount = 0;
            }
        }

        [Test]
        public void ReflexiveHandlesMultipleParameters()
        {
            GameObject host = new(
                nameof(ReflexiveHandlesMultipleParameters),
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(host);
            SimpleMessageAwareComponent component =
                host.GetComponent<SimpleMessageAwareComponent>();

            int twoArgCount = 0;
            int threeArgCount = 0;
            component.reflexiveTwoArgumentHandler = () => ++twoArgCount;
            component.reflexiveThreeArgumentHandler = () => ++threeArgCount;

            ReflexiveMessage twoArguments = new(
                nameof(SimpleMessageAwareComponent.HandleReflexiveMessageTwoArguments),
                ReflexiveSendMode.Flat,
                27,
                42
            );
            InstanceId hostId = host;
            twoArguments.EmitTargeted(hostId);
            Assert.AreEqual(1, twoArgCount);
            Assert.AreEqual(0, threeArgCount);

            ReflexiveMessage threeArguments = new(
                nameof(SimpleMessageAwareComponent.HandleReflexiveMessageThreeArguments),
                ReflexiveSendMode.Flat,
                1,
                2,
                3
            );
            threeArguments.EmitTargeted(hostId);
            Assert.AreEqual(1, twoArgCount);
            Assert.AreEqual(1, threeArgCount);
        }

        [TestCase(ReflexiveSendMode.Upwards | ReflexiveSendMode.Flat)]
        [TestCase(ReflexiveSendMode.Downwards | ReflexiveSendMode.Flat)]
        [TestCase(ReflexiveSendMode.Upwards | ReflexiveSendMode.Downwards)]
        public void CombinedModesIncludeDisabledReceiversWithoutActiveFilter(
            ReflexiveSendMode sendMode
        )
        {
            GameObject host = new(
                nameof(CombinedModesIncludeDisabledReceiversWithoutActiveFilter),
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(host);
            SimpleMessageAwareComponent component =
                host.GetComponent<SimpleMessageAwareComponent>();
            component.enabled = false;
            int callCount = 0;
            component.reflexiveTwoArgumentHandler = () => ++callCount;

            ReflexiveMessage message = new(
                nameof(SimpleMessageAwareComponent.HandleReflexiveMessageTwoArguments),
                sendMode,
                1,
                2
            );
            InstanceId hostId = host;
            message.EmitTargeted(hostId);

            Assert.That(
                callCount,
                Is.EqualTo(1),
                $"[{sendMode}] A disabled receiver must remain eligible when OnlyIncludeActive is absent."
            );
        }

        [Test]
        public void CombinedDownwardsModeIncludesInactiveDescendantsWithoutActiveFilter()
        {
            GameObject parent = new(
                nameof(CombinedDownwardsModeIncludesInactiveDescendantsWithoutActiveFilter)
                    + "_Parent",
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(parent);
            GameObject child = new(
                nameof(CombinedDownwardsModeIncludesInactiveDescendantsWithoutActiveFilter)
                    + "_Child",
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(child);
            child.transform.SetParent(parent.transform);
            SimpleMessageAwareComponent childComponent =
                child.GetComponent<SimpleMessageAwareComponent>();
            int childCallCount = 0;
            childComponent.reflexiveTwoArgumentHandler = () => ++childCallCount;
            child.SetActive(false);

            ReflexiveMessage message = new(
                nameof(SimpleMessageAwareComponent.HandleReflexiveMessageTwoArguments),
                ReflexiveSendMode.Downwards | ReflexiveSendMode.Flat,
                1,
                2
            );
            InstanceId parentId = parent;
            message.EmitTargeted(parentId);

            Assert.That(
                childCallCount,
                Is.EqualTo(1),
                "Downward traversal must include inactive descendants when OnlyIncludeActive is absent."
            );
        }

        [Test]
        public void ActiveFilterExcludesReceiverOnInactiveGameObject()
        {
            GameObject host = new(
                nameof(ActiveFilterExcludesReceiverOnInactiveGameObject),
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(host);
            SimpleMessageAwareComponent component =
                host.GetComponent<SimpleMessageAwareComponent>();
            int callCount = 0;
            component.reflexiveTwoArgumentHandler = () => ++callCount;
            InstanceId hostId = host;
            host.SetActive(false);

            ReflexiveMessage message = new(
                nameof(SimpleMessageAwareComponent.HandleReflexiveMessageTwoArguments),
                ReflexiveSendMode.Flat | ReflexiveSendMode.OnlyIncludeActive,
                1,
                2
            );
            message.EmitTargeted(hostId);

            Assert.That(
                callCount,
                Is.Zero,
                "OnlyIncludeActive must exclude enabled components on inactive GameObjects."
            );
        }

        [Test]
        public void NestedReflexiveEmissionPreservesOuterTraversalAndDeduplication()
        {
            GameObject grandParent = new(
                nameof(NestedReflexiveEmissionPreservesOuterTraversalAndDeduplication) + "_Grand",
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(grandParent);
            GameObject parent = new(
                nameof(NestedReflexiveEmissionPreservesOuterTraversalAndDeduplication) + "_Parent",
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(parent);
            GameObject child = new(
                nameof(NestedReflexiveEmissionPreservesOuterTraversalAndDeduplication) + "_Child",
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(child);
            GameObject nestedTarget = new(
                nameof(NestedReflexiveEmissionPreservesOuterTraversalAndDeduplication) + "_Nested",
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(nestedTarget);
            parent.transform.SetParent(grandParent.transform);
            child.transform.SetParent(parent.transform);

            SimpleMessageAwareComponent grandComponent =
                grandParent.GetComponent<SimpleMessageAwareComponent>();
            SimpleMessageAwareComponent parentComponent =
                parent.GetComponent<SimpleMessageAwareComponent>();
            SimpleMessageAwareComponent childComponent =
                child.GetComponent<SimpleMessageAwareComponent>();
            SimpleMessageAwareComponent nestedComponent =
                nestedTarget.GetComponent<SimpleMessageAwareComponent>();
            MessageBus messageBus = new();
            InstanceId nestedTargetId = nestedTarget;
            int grandCount = 0;
            int parentCount = 0;
            int childCount = 0;
            int nestedCount = 0;
            grandComponent.reflexiveTwoArgumentHandler = () => ++grandCount;
            childComponent.reflexiveTwoArgumentHandler = () => ++childCount;
            nestedComponent.reflexiveTwoArgumentHandler = () => ++nestedCount;
            parentComponent.reflexiveTwoArgumentHandler = () =>
            {
                ++parentCount;
                ReflexiveMessage nested = new(
                    nameof(SimpleMessageAwareComponent.HandleReflexiveMessageTwoArguments),
                    ReflexiveSendMode.Flat,
                    3,
                    4
                );
                nested.EmitTargeted(nestedTargetId, messageBus);
            };

            ReflexiveMessage outer = new(
                nameof(SimpleMessageAwareComponent.HandleReflexiveMessageTwoArguments),
                ReflexiveSendMode.Upwards | ReflexiveSendMode.Downwards,
                1,
                2
            );
            InstanceId parentId = parent;
            outer.EmitTargeted(parentId, messageBus);

            CollectionPoolDiagnostics firstPool = messageBus.ReflexiveDispatchPoolDiagnostics;
            Assert.That(firstPool.Cached, Is.EqualTo(1), "Nested state must return to the pool.");
            Assert.That(firstPool.Misses, Is.EqualTo(1), "First nested send must warm one state.");

            outer.EmitTargeted(parentId, messageBus);

            CollectionPoolDiagnostics reusedPool = messageBus.ReflexiveDispatchPoolDiagnostics;
            Assert.That(reusedPool.Cached, Is.EqualTo(1), "Reused state must return to the pool.");
            Assert.That(reusedPool.Hits, Is.EqualTo(1), "Second nested send must reuse state.");

            Assert.That(grandCount, Is.EqualTo(2), "Outer upwards traversal must resume.");
            Assert.That(parentCount, Is.EqualTo(2), "Combined traversal must deduplicate target.");
            Assert.That(childCount, Is.EqualTo(2), "Outer downwards traversal must resume.");
            Assert.That(
                nestedCount,
                Is.EqualTo(2),
                "Nested reflexive delivery must run once per send."
            );

            IMessageBus.TrimResult trimResult = messageBus.Trim(force: true);
            Assert.That(
                trimResult.PooledCollectionsEvicted,
                Is.GreaterThanOrEqualTo(1),
                "Forced trim must evict the retained nested dispatch state."
            );
            Assert.That(
                messageBus.ReflexiveDispatchPoolDiagnostics.Cached,
                Is.Zero,
                "Forced trim must leave no retained nested dispatch state."
            );
        }

        [Test]
        public void DestroyingLaterReceiverDuringTraversalSkipsDestroyedEntry()
        {
            GameObject root = new(
                nameof(DestroyingLaterReceiverDuringTraversalSkipsDestroyedEntry) + "_Root"
            );
            _spawned.Add(root);
            GameObject destroyer = new(
                nameof(DestroyingLaterReceiverDuringTraversalSkipsDestroyedEntry) + "_Destroyer",
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(destroyer);
            GameObject victim = new(
                nameof(DestroyingLaterReceiverDuringTraversalSkipsDestroyedEntry) + "_Victim",
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(victim);
            destroyer.transform.SetParent(root.transform);
            victim.transform.SetParent(root.transform);

            SimpleMessageAwareComponent destroyerComponent =
                destroyer.GetComponent<SimpleMessageAwareComponent>();
            SimpleMessageAwareComponent victimComponent =
                victim.GetComponent<SimpleMessageAwareComponent>();
            int destroyerCount = 0;
            int victimCount = 0;
            destroyerComponent.reflexiveTwoArgumentHandler = () =>
            {
                ++destroyerCount;
                Object.DestroyImmediate(victimComponent);
            };
            victimComponent.reflexiveTwoArgumentHandler = () => ++victimCount;

            ReflexiveMessage message = new(
                nameof(SimpleMessageAwareComponent.HandleReflexiveMessageTwoArguments),
                ReflexiveSendMode.Downwards,
                1,
                2
            );
            InstanceId rootId = root;

            Assert.DoesNotThrow(
                () => message.EmitTargeted(rootId),
                "Traversal must ignore a cached receiver destroyed by an earlier callback."
            );
            Assert.That(destroyerCount, Is.EqualTo(1), "Destroyer must receive once.");
            Assert.That(victimCount, Is.Zero, "Destroyed receiver must not run.");
            Assert.That(victimComponent == null, Is.True, "Test setup must destroy the victim.");
        }

        [Test]
        public void ResetDuringReflexiveTraversalStopsRemainingReceivers()
        {
            GameObject root = new(
                nameof(ResetDuringReflexiveTraversalStopsRemainingReceivers) + "_Root"
            );
            _spawned.Add(root);
            GameObject resettingReceiver = new(
                nameof(ResetDuringReflexiveTraversalStopsRemainingReceivers) + "_Resetting",
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(resettingReceiver);
            GameObject trailingReceiver = new(
                nameof(ResetDuringReflexiveTraversalStopsRemainingReceivers) + "_Trailing",
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(trailingReceiver);
            resettingReceiver.transform.SetParent(root.transform);
            trailingReceiver.transform.SetParent(root.transform);

            SimpleMessageAwareComponent resettingComponent =
                resettingReceiver.GetComponent<SimpleMessageAwareComponent>();
            SimpleMessageAwareComponent trailingComponent =
                trailingReceiver.GetComponent<SimpleMessageAwareComponent>();
            int resettingCount = 0;
            int trailingCount = 0;
            resettingComponent.reflexiveTwoArgumentHandler = () =>
            {
                ++resettingCount;
                DxMessagingStaticState.Reset();
            };
            trailingComponent.reflexiveTwoArgumentHandler = () => ++trailingCount;

            ReflexiveMessage message = new(
                nameof(SimpleMessageAwareComponent.HandleReflexiveMessageTwoArguments),
                ReflexiveSendMode.Downwards,
                1,
                2
            );
            InstanceId rootId = root;

            Assert.DoesNotThrow(
                () => message.EmitTargeted(rootId),
                "Reset from a reflexive receiver must stop traversal without corrupting its state."
            );
            Assert.That(resettingCount, Is.EqualTo(1), "Resetting receiver must run once.");
            Assert.That(
                trailingCount,
                Is.Zero,
                "Reset must stop remaining receivers in the in-flight traversal."
            );
        }

        [Test]
        public void ResetFromGlobalAcceptAllStopsReflexiveDeliveryBeforeFirstReceiver()
        {
            GameObject host = new(
                nameof(ResetFromGlobalAcceptAllStopsReflexiveDeliveryBeforeFirstReceiver),
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(host);
            SimpleMessageAwareComponent component =
                host.GetComponent<SimpleMessageAwareComponent>();
            MessageBus messageBus = new();
            InstanceId hostId = host;
            MessageHandler handler = new(hostId, messageBus) { active = true };
            using MessageRegistrationToken token = MessageRegistrationToken.Create(
                handler,
                messageBus
            );
            int globalCount = 0;
            int reflexiveCount = 0;
            component.reflexiveTwoArgumentHandler = () => ++reflexiveCount;
            _ = token.RegisterGlobalAcceptAll(
                acceptAllUntargeted: _ => { },
                acceptAllTargeted: (_, _) =>
                {
                    ++globalCount;
                    messageBus.ResetState();
                },
                acceptAllBroadcast: (_, _) => { }
            );
            token.Enable();

            ReflexiveMessage message = new(
                nameof(SimpleMessageAwareComponent.HandleReflexiveMessageTwoArguments),
                ReflexiveSendMode.Flat,
                1,
                2
            );

            Assert.DoesNotThrow(
                () => message.EmitTargeted(hostId, messageBus),
                "Reset from global accept-all must stop reflexive routing without throwing."
            );
            Assert.That(globalCount, Is.EqualTo(1), "Global accept-all must run once.");
            Assert.That(
                reflexiveCount,
                Is.Zero,
                "A reset before reflexive routing starts must suppress the first receiver."
            );
            Assert.That(
                messageBus.RegisteredGlobalAcceptAll,
                Is.Zero,
                "Reset must clear the global accept-all registration."
            );
        }

        [TestCase(
            ReflexiveSendMode.Upwards,
            TestName = "DestroyingCurrentGameObjectDuringUpwardsTraversalContinuesAtCapturedParent"
        )]
        [TestCase(
            ReflexiveSendMode.Upwards | ReflexiveSendMode.Downwards,
            TestName = "DestroyingOriginDuringCombinedTraversalSkipsDownwardEnumeration"
        )]
        public void DestroyingCurrentGameObjectDuringUpwardsTraversalContinuesAtCapturedParent(
            ReflexiveSendMode sendMode
        )
        {
            GameObject parent = new(
                nameof(DestroyingCurrentGameObjectDuringUpwardsTraversalContinuesAtCapturedParent)
                    + sendMode
                    + "_Parent",
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(parent);
            GameObject child = new(
                nameof(DestroyingCurrentGameObjectDuringUpwardsTraversalContinuesAtCapturedParent)
                    + sendMode
                    + "_Child",
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(child);
            child.transform.SetParent(parent.transform);

            SimpleMessageAwareComponent parentComponent =
                parent.GetComponent<SimpleMessageAwareComponent>();
            SimpleMessageAwareComponent childComponent =
                child.GetComponent<SimpleMessageAwareComponent>();
            int parentCount = 0;
            int childCount = 0;
            parentComponent.reflexiveTwoArgumentHandler = () => ++parentCount;
            childComponent.reflexiveTwoArgumentHandler = () =>
            {
                ++childCount;
                Object.DestroyImmediate(child);
            };

            ReflexiveMessage message = new(
                nameof(SimpleMessageAwareComponent.HandleReflexiveMessageTwoArguments),
                sendMode,
                1,
                2
            );
            InstanceId childId = child;

            Assert.DoesNotThrow(
                () => message.EmitTargeted(childId),
                $"[{sendMode}] Traversal must not read a destroyed origin or Transform."
            );
            Assert.That(childCount, Is.EqualTo(1), $"[{sendMode}] Current receiver must run once.");
            Assert.That(
                parentCount,
                Is.EqualTo(1),
                $"[{sendMode}] Captured parent must still receive once."
            );
            Assert.That(
                child == null,
                Is.True,
                $"[{sendMode}] Test setup must destroy the current GameObject."
            );
        }
    }
}

#endif
