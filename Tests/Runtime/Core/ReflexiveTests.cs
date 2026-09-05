#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using DxMessaging.Core;
    using DxMessaging.Core.Configuration;
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
        public void SuccessfulReflexiveMethodsShareOneLeastRecentlyUsedBound()
        {
            WithReflexiveRetentionLimit(
                2,
                (bus, settings) =>
                {
                    GameObject host = CreateReflexiveHost("Lru");
                    SimpleMessageAwareComponent receiver =
                        host.GetComponent<SimpleMessageAwareComponent>();
                    ReflexiveReceiverComponent other =
                        host.AddComponent<ReflexiveReceiverComponent>();
                    int calls = 0;
                    receiver.reflexiveTwoArgumentHandler = () => ++calls;
                    receiver.reflexiveThreeArgumentHandler = () => ++calls;
                    ReflexiveMessage first = TwoArgumentReflexiveMessage();
                    ReflexiveMessage second = new(
                        nameof(SimpleMessageAwareComponent.HandleReflexiveMessageThreeArguments),
                        ReflexiveSendMode.Flat,
                        1,
                        2,
                        3
                    );
                    ReflexiveMessage third = new(
                        nameof(ReflexiveReceiverComponent.OnReflexive),
                        ReflexiveSendMode.Flat | ReflexiveSendMode.OnlyIncludeActive
                    );
                    InstanceId target = host;
                    first.EmitTargeted(target, bus);
                    second.EmitTargeted(target, bus);
                    Assert.That(
                        bus.ReflexiveMethodCacheCount,
                        Is.EqualTo(2),
                        "Only successful lookups may occupy the two-entry cache."
                    );
                    first.EmitTargeted(target, bus);
                    third.EmitTargeted(target, bus);
                    Assert.That(
                        bus.ReflexiveMethodCacheCount,
                        Is.EqualTo(2),
                        "Different component types must share the same global bound."
                    );
                    Assert.That(
                        bus.IsReflexiveMethodCached(
                            typeof(SimpleMessageAwareComponent),
                            first.signatureKey
                        ),
                        Is.True,
                        "A cache hit must refresh recency."
                    );
                    Assert.That(
                        bus.IsReflexiveMethodCached(
                            typeof(SimpleMessageAwareComponent),
                            second.signatureKey
                        ),
                        Is.False,
                        "The least recently used method must be evicted."
                    );
                    Assert.That(
                        bus.IsReflexiveMethodCached(
                            typeof(ReflexiveReceiverComponent),
                            third.signatureKey
                        ),
                        Is.True,
                        "The newest method must be retained."
                    );
                    Assert.That(calls, Is.EqualTo(3), "All three matching sends must invoke once.");
                    Assert.That(
                        other.InvocationCount,
                        Is.EqualTo(1),
                        "The other component type must receive its matching method."
                    );
                    settings._bufferMaxDistinctEntries = 1;
                    DxMessagingRuntimeSettings.RaiseSettingsChanged(settings);
                    Assert.That(
                        bus.ReflexiveMethodCacheCount,
                        Is.EqualTo(1),
                        "Hot reload must immediately shrink the cache."
                    );
                    Assert.That(
                        bus.IsReflexiveMethodCached(
                            typeof(ReflexiveReceiverComponent),
                            third.signatureKey
                        ),
                        Is.True,
                        "Shrinking must preserve the most recently used method."
                    );
                    bus.Trim(force: true);
                    Assert.That(
                        bus.ReflexiveMethodCacheCount,
                        Is.Zero,
                        "Forced trim must release method lookups."
                    );
                    first.EmitTargeted(target, bus);
                    Assert.That(
                        calls,
                        Is.EqualTo(4),
                        "A trimmed method must resolve and invoke again."
                    );
                    Assert.That(
                        bus.ReflexiveMethodCacheCount,
                        Is.EqualTo(1),
                        "Repopulation must respect the new capacity."
                    );
                }
            );
        }

        [TestCase("Force")]
        [TestCase("Shrink")]
        [TestCase("Reset")]
        public void ColdReflexiveCacheReclamationReleasesHistoricalBackingCapacity(string operation)
        {
            WithReflexiveRetentionLimit(
                32,
                (bus, settings) =>
                {
                    GameObject host = CreateReflexiveHost("Backing");
                    InstanceId target = host;
                    object[] values =
                    {
                        1,
                        "text",
                        1L,
                        true,
                        1f,
                        1d,
                        (byte)1,
                        (short)1,
                        'a',
                        1m,
                        1u,
                        1ul,
                        (ushort)1,
                        (sbyte)1,
                        Vector2.zero,
                        Vector3.zero,
                    };
                    foreach (object value in values)
                    {
                        ReflexiveMessage message = new(
                            nameof(
                                SimpleMessageAwareComponent.HandleReflexiveMessageObjectArgument
                            ),
                            ReflexiveSendMode.Flat | ReflexiveSendMode.OnlyIncludeActive,
                            value
                        );
                        message.EmitTargeted(target, bus);
                    }
                    Assert.That(
                        bus.ReflexiveMethodCacheCount,
                        Is.EqualTo(values.Length),
                        "Each successful object-argument signature must occupy one lookup entry."
                    );
                    int beforeCapacity = bus.ReflexiveMethodCacheCapacity;
                    if (operation == "Force")
                    {
                        bus.Trim(force: true);
                    }
                    else if (operation == "Reset")
                    {
                        bus.ResetState();
                    }
                    else
                    {
                        settings._bufferMaxDistinctEntries = 2;
                        DxMessagingRuntimeSettings.RaiseSettingsChanged(settings);
                    }
                    Assert.That(
                        bus.ReflexiveMethodCacheCount,
                        Is.EqualTo(operation == "Shrink" ? 2 : 0),
                        $"operation={operation}: cold reclamation must remove obsolete lookup references."
                    );
                    Assert.That(
                        bus.ReflexiveMethodCacheCapacity,
                        Is.LessThan(beforeCapacity),
                        $"operation={operation}: cold reclamation must release the historical bucket-array peak."
                    );
                }
            );
        }

        [Test]
        public void AmbiguousReflexiveLookupsWithoutExactOverloadAreNotRetained()
        {
            WithReflexiveRetentionLimit(
                2,
                (bus, settings) =>
                {
                    GameObject host = new("AmbiguousLookup", typeof(ReflexiveReceiverComponent));
                    _spawned.Add(host);
                    ReflexiveReceiverComponent receiver =
                        host.GetComponent<ReflexiveReceiverComponent>();
                    Assert.Throws<System.Reflection.AmbiguousMatchException>(
                        () =>
                            typeof(ReflexiveReceiverComponent).GetMethod(
                                nameof(ReflexiveReceiverComponent.OnAmbiguous),
                                new[] { typeof(string) }
                            ),
                        "The input must match two unrelated interface overloads without an exact match."
                    );
                    ReflexiveMessage message = new(
                        nameof(ReflexiveReceiverComponent.OnAmbiguous),
                        ReflexiveSendMode.Flat | ReflexiveSendMode.OnlyIncludeActive,
                        "text"
                    );
                    InstanceId target = host;
                    message.EmitTargeted(target, bus);
                    message.EmitTargeted(target, bus);
                    Assert.That(
                        receiver.InvocationCount,
                        Is.Zero,
                        "An ambiguous lookup without an exact overload must not invoke either method."
                    );
                    Assert.That(
                        bus.ReflexiveMethodCacheCount,
                        Is.Zero,
                        "Ambiguous misses must not occupy the successful-lookup cache."
                    );
                }
            );
        }

        [TestCase(0)]
        [TestCase(2)]
        public void MissingReflexiveMethodsAreNeverRetained(int capacity)
        {
            WithReflexiveRetentionLimit(
                capacity,
                (bus, settings) =>
                {
                    GameObject host = CreateReflexiveHost("Misses");
                    InstanceId target = host;
                    for (int index = 0; index < 24; ++index)
                    {
                        ReflexiveMessage missing = new(
                            "MissingReflexiveMethod" + index,
                            ReflexiveSendMode.Flat,
                            1,
                            2
                        );
                        missing.EmitTargeted(target, bus);
                        Assert.That(
                            bus.ReflexiveMethodCacheCount,
                            Is.Zero,
                            $"capacity={capacity}, miss={index}: failed lookups must not be retained."
                        );
                    }
                    int calls = 0;
                    host.GetComponent<SimpleMessageAwareComponent>().reflexiveTwoArgumentHandler =
                        () => ++calls;
                    ReflexiveMessage valid = TwoArgumentReflexiveMessage();
                    valid.EmitTargeted(target, bus);
                    valid.EmitTargeted(target, bus);
                    Assert.That(
                        calls,
                        Is.EqualTo(2),
                        $"capacity={capacity}: disabling caching must not disable dispatch."
                    );
                    Assert.That(
                        bus.ReflexiveMethodCacheCount,
                        Is.EqualTo(capacity == 0 ? 0 : 1),
                        $"capacity={capacity}: only successful lookups may be retained."
                    );
                }
            );
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void OversizedReflexiveScratchIsDroppedAfterRootAndNestedDispatch(
            bool nested,
            bool componentBurst
        )
        {
            WithReflexiveRetentionLimit(
                8,
                (bus, settings) =>
                {
                    GameObject burst = CreateReflexiveHost("Burst");
                    for (int index = 0; index < 16; ++index)
                    {
                        if (componentBurst)
                        {
                            burst.AddComponent<ReflexiveReceiverComponent>();
                        }
                        GameObject child = new("EmptyChild" + index);
                        _spawned.Add(child);
                        child.transform.SetParent(burst.transform);
                    }
                    int calls = 0;
                    burst.GetComponent<SimpleMessageAwareComponent>().reflexiveTwoArgumentHandler =
                        () => ++calls;
                    ReflexiveMessage burstMessage = TwoArgumentReflexiveMessage(
                        ReflexiveSendMode.Downwards
                    );
                    InstanceId burstTarget = burst;
                    GameObject outer = nested ? CreateReflexiveHost("Outer") : burst;
                    if (nested)
                    {
                        outer
                            .GetComponent<SimpleMessageAwareComponent>()
                            .reflexiveTwoArgumentHandler = () =>
                            burstMessage.EmitTargeted(burstTarget, bus);
                    }
                    ReflexiveMessage message = nested
                        ? TwoArgumentReflexiveMessage()
                        : burstMessage;
                    InstanceId target = outer;
                    message.EmitTargeted(target, bus);
                    Assert.That(
                        calls,
                        Is.EqualTo(1),
                        $"nested={nested}, componentBurst={componentBurst}: the burst must still invoke its receiver."
                    );
                    Assert.That(
                        bus.ReflexiveDispatchPoolDiagnostics.Cached,
                        Is.Zero,
                        $"nested={nested}, componentBurst={componentBurst}: oversized nested scratch must not enter the pool."
                    );
                    Assert.That(
                        bus.HasRetainedReflexiveDispatchState,
                        Is.EqualTo(nested),
                        $"nested={nested}, componentBurst={componentBurst}: only small outer scratch may survive."
                    );
                    Assert.That(
                        bus.ReflexiveDispatchRetainedCapacity,
                        Is.LessThanOrEqualTo(8),
                        $"nested={nested}, componentBurst={componentBurst}: retained capacity must be bounded after clearing."
                    );
                    message.EmitTargeted(target, bus);
                    Assert.That(
                        calls,
                        Is.EqualTo(2),
                        $"nested={nested}, componentBurst={componentBurst}: discarded state must be safely rebuilt."
                    );
                    Assert.That(
                        bus.ReflexiveDispatchPoolDiagnostics.Cached,
                        Is.Zero,
                        "Repeated oversized sends must not populate the nested pool."
                    );
                }
            );
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ForcedTrimDuringReflexiveDispatchPreservesTraversalButDropsActiveScratch(
            bool nested
        )
        {
            WithReflexiveRetentionLimit(
                32,
                (bus, settings) =>
                {
                    GameObject root = CreateReflexiveHost("TrimRoot");
                    GameObject child = CreateReflexiveHost("TrimChild");
                    child.transform.SetParent(root.transform);
                    GameObject nestedHost = CreateReflexiveHost("TrimNested");
                    InstanceId nestedTarget = nestedHost;
                    int childCalls = 0;
                    child.GetComponent<SimpleMessageAwareComponent>().reflexiveTwoArgumentHandler =
                        () => ++childCalls;
                    nestedHost
                        .GetComponent<SimpleMessageAwareComponent>()
                        .reflexiveTwoArgumentHandler = () => bus.Trim(force: true);
                    root.GetComponent<SimpleMessageAwareComponent>().reflexiveTwoArgumentHandler =
                        () =>
                        {
                            if (nested)
                            {
                                ReflexiveMessage inner = TwoArgumentReflexiveMessage();
                                inner.EmitTargeted(nestedTarget, bus);
                            }
                            else
                            {
                                bus.Trim(force: true);
                            }
                        };
                    ReflexiveMessage outer = TwoArgumentReflexiveMessage(
                        ReflexiveSendMode.Downwards
                    );
                    InstanceId target = root;
                    outer.EmitTargeted(target, bus);
                    Assert.That(
                        childCalls,
                        Is.EqualTo(1),
                        $"nested={nested}: trim must not clear active traversal."
                    );
                    Assert.That(
                        bus.HasRetainedReflexiveDispatchState,
                        Is.False,
                        $"nested={nested}: the active root must be discarded when its lease ends."
                    );
                    Assert.That(
                        bus.ReflexiveDispatchPoolDiagnostics.Cached,
                        Is.Zero,
                        $"nested={nested}: a lease predating trim must not re-enter its pool."
                    );
                    outer.EmitTargeted(target, bus);
                    Assert.That(
                        childCalls,
                        Is.EqualTo(2),
                        $"nested={nested}: subsequent dispatch must rebuild discarded state."
                    );
                }
            );
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RetentionDisabledDuringNestedDispatchClearsScratchEvenWhenHandlerThrows(
            bool throws
        )
        {
            WithReflexiveRetentionLimit(
                32,
                (bus, settings) =>
                {
                    GameObject outer = CreateReflexiveHost("DisableOuter");
                    GameObject inner = CreateReflexiveHost("DisableInner");
                    InstanceId innerTarget = inner;
                    int calls = 0;
                    inner.GetComponent<SimpleMessageAwareComponent>().reflexiveTwoArgumentHandler =
                        () =>
                        {
                            ++calls;
                            settings._bufferMaxDistinctEntries = 0;
                            DxMessagingRuntimeSettings.RaiseSettingsChanged(settings);
                            if (throws)
                            {
                                throw new System.InvalidOperationException(
                                    "Expected reflexive failure"
                                );
                            }
                        };
                    outer.GetComponent<SimpleMessageAwareComponent>().reflexiveTwoArgumentHandler =
                        () =>
                        {
                            ReflexiveMessage nested = TwoArgumentReflexiveMessage();
                            nested.EmitTargeted(innerTarget, bus);
                        };
                    ReflexiveMessage message = TwoArgumentReflexiveMessage();
                    InstanceId target = outer;
                    if (throws)
                    {
                        Assert.Throws<System.InvalidOperationException>(
                            () => message.EmitTargeted(target, bus),
                            "The original handler exception must propagate through both leases."
                        );
                    }
                    else
                    {
                        message.EmitTargeted(target, bus);
                    }
                    Assert.That(
                        calls,
                        Is.EqualTo(1),
                        $"throws={throws}: the nested callback must execute."
                    );
                    Assert.That(
                        bus.HasRetainedReflexiveDispatchState,
                        Is.False,
                        $"throws={throws}: a zero cap must discard root scratch on unwind."
                    );
                    Assert.That(
                        bus.ReflexiveDispatchPoolDiagnostics.Cached,
                        Is.Zero,
                        $"throws={throws}: a zero cap must discard nested scratch on unwind."
                    );
                    Assert.That(
                        bus.ReflexiveMethodCacheCount,
                        Is.Zero,
                        $"throws={throws}: hot reload must release successful lookups."
                    );
                    inner.GetComponent<SimpleMessageAwareComponent>().reflexiveTwoArgumentHandler =
                        () => ++calls;
                    message.EmitTargeted(target, bus);
                    Assert.That(
                        calls,
                        Is.EqualTo(2),
                        $"throws={throws}: the next dispatch must succeed with caching disabled."
                    );
                }
            );
        }

        [Test]
        public void LoweringReflexiveRetentionLimitReleasesIdleRootAndNestedPool()
        {
            WithReflexiveRetentionLimit(
                32,
                (bus, settings) =>
                {
                    GameObject outer = CreateReflexiveHost("IdleOuter");
                    GameObject inner = CreateReflexiveHost("IdleInner");
                    InstanceId innerTarget = inner;
                    outer.GetComponent<SimpleMessageAwareComponent>().reflexiveTwoArgumentHandler =
                        () =>
                        {
                            ReflexiveMessage nested = TwoArgumentReflexiveMessage();
                            nested.EmitTargeted(innerTarget, bus);
                        };
                    ReflexiveMessage message = TwoArgumentReflexiveMessage();
                    InstanceId target = outer;
                    message.EmitTargeted(target, bus);
                    Assert.That(
                        bus.HasRetainedReflexiveDispatchState,
                        Is.True,
                        "Small root scratch must be retained before the cap falls."
                    );
                    Assert.That(
                        bus.ReflexiveDispatchPoolDiagnostics.Cached,
                        Is.EqualTo(1),
                        "Warm nested state must be retained before the cap falls."
                    );
                    settings._bufferMaxDistinctEntries = 1;
                    DxMessagingRuntimeSettings.RaiseSettingsChanged(settings);
                    Assert.That(
                        bus.HasRetainedReflexiveDispatchState,
                        Is.False,
                        "A smaller nonzero cap must release oversized idle root scratch."
                    );
                    Assert.That(
                        bus.ReflexiveDispatchPoolDiagnostics.Cached,
                        Is.Zero,
                        "A lower cap must drain retained nested states before reuse."
                    );
                }
            );
        }

        private GameObject CreateReflexiveHost(string suffix)
        {
            GameObject host = new(
                "ReflexiveRetention" + suffix,
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(host);
            return host;
        }

        private static ReflexiveMessage TwoArgumentReflexiveMessage(
            ReflexiveSendMode mode = ReflexiveSendMode.Flat
        )
        {
            return new ReflexiveMessage(
                nameof(SimpleMessageAwareComponent.HandleReflexiveMessageTwoArguments),
                mode,
                1,
                2
            );
        }

        private static void WithReflexiveRetentionLimit(
            int capacity,
            System.Action<MessageBus, DxMessagingRuntimeSettings> test
        )
        {
            DxMessagingRuntimeSettings settings =
                ScriptableObject.CreateInstance<DxMessagingRuntimeSettings>();
            try
            {
                settings._bufferMaxDistinctEntries = capacity;
                using System.IDisposable settingsOverride =
                    DxMessagingRuntimeSettingsProvider.Override(settings);
                test(new MessageBus(), settings);
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }

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
