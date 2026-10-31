#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.Configuration;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using NUnit.Framework;
    using UnityEngine;
    using Object = UnityEngine.Object;

    public sealed class ReflexiveReentryCacheOwnershipTests : MessagingTestBase
    {
        private static IEnumerable<TestCaseData> Cases()
        {
            foreach (int capacity in new[] { 0, 1, 2, 4 })
            {
                foreach (int slot in new[] { 0, 1 })
                {
                    foreach (bool fresh in new[] { false, true })
                    {
                        yield return new TestCaseData(capacity, slot, fresh).SetName(
                            $"ReflexiveReentryOwnership_capacity{capacity}_slot{slot}_fresh{fresh}"
                        );
                    }
                }
            }
        }

        /// <remarks>
        /// 2026-10-31: nested metadata lookup published the same key before the outer
        /// Dictionary.Add. Reusing its node must also preserve eviction and reclamation.
        /// Read nodes directly after Unity 2022 IL2CPP could not reflect their List property.
        /// </remarks>
        [TestCaseSource(nameof(Cases))]
        public void ReentrantPublicationPreservesCacheOwnership(
            int capacity,
            int metadataSlot,
            bool freshSignature
        )
        {
            DxMessagingRuntimeSettings settings =
                ScriptableObject.CreateInstance<DxMessagingRuntimeSettings>();
            try
            {
                settings._bufferMaxDistinctEntries = capacity;
                using IDisposable settingsOverride = RuntimeSettingsFixture.Override(settings);
                MessageBus bus = new();
                try
                {
                    for (int cycle = 0; cycle < 10; ++cycle)
                    {
                        settings._bufferMaxDistinctEntries = capacity;
                        DxMessagingRuntimeSettings.RaiseSettingsChanged(settings);
                        bus.ResetState();
                        GameObject host = new("ReentryOwnership" + cycle);
                        _spawned.Add(host);
                        ReflexiveReentryReceiver receiver =
                            host.AddComponent<ReflexiveReentryReceiver>();
                        SignatureSnapshotReceiverComponent thirdReceiver =
                            host.AddComponent<SignatureSnapshotReceiverComponent>();
                        InstanceId target = host;
                        int deliveries = 0;
                        receiver.OnMessage = (method, first, second) =>
                        {
                            Assert.That(
                                first,
                                Is.EqualTo(1),
                                "First argument must survive reentry."
                            );
                            Assert.That(
                                second,
                                Is.EqualTo(2),
                                "Second argument must survive reentry."
                            );
                            ++deliveries;
                        };
                        ReflexiveMessage other = new(
                            nameof(ReflexiveReentryReceiver.ReceiveOther),
                            ReflexiveSendMode.Flat,
                            1,
                            2
                        );
                        other.EmitTargeted(target, bus);
                        AssertOwnership(bus, Math.Min(capacity, 1));
                        deliveries = 0;
                        bool armed = false;
                        int callbacks = 0;
                        int nestedReturns = 0;
                        ReflexiveMessage message = default;
                        Type[] types = { typeof(int), typeof(int) };
                        types[metadataSlot] = new CallbackType(() =>
                        {
                            if (!armed)
                            {
                                return;
                            }
                            armed = false;
                            ++callbacks;
                            ReflexiveMessage nested = freshSignature
                                ? new ReflexiveMessage(
                                    nameof(ReflexiveReentryReceiver.Receive),
                                    ReflexiveSendMode.Flat,
                                    new object[] { 1, 2 },
                                    types
                                )
                                : message;
                            nested.EmitTargeted(target, bus);
                            ++nestedReturns;
                        });
                        message = new ReflexiveMessage(
                            nameof(ReflexiveReentryReceiver.Receive),
                            ReflexiveSendMode.Flat,
                            new object[] { 1, 2 },
                            types
                        );
                        long generation = MessageBus.GetResetGeneration(bus);
                        armed = true;
                        message.EmitTargeted(target, bus);
                        Assert.That(callbacks, Is.EqualTo(1), "Cold metadata must call back once.");
                        Assert.That(nestedReturns, Is.EqualTo(1), "Nested delivery must return.");
                        Assert.That(
                            deliveries,
                            Is.EqualTo(2),
                            "Nested and outer sends must deliver."
                        );
                        Assert.That(
                            MessageBus.GetResetGeneration(bus),
                            Is.EqualTo(generation),
                            "Same-generation reentry must not reset the bus."
                        );
                        AssertOwnership(bus, Math.Min(capacity, 2));
                        AssertCached(bus, message, 0 < capacity);
                        AssertCached(bus, other, 1 < capacity);

                        ReflexiveMessage third = new(
                            nameof(SignatureSnapshotReceiverComponent.ReceiveSignature),
                            ReflexiveSendMode.Flat,
                            1,
                            2
                        );
                        third.EmitTargeted(target, bus);
                        Assert.That(thirdReceiver.IntegerCalls, Is.EqualTo(1));
                        AssertOwnership(bus, Math.Min(capacity, 3));
                        AssertCached(bus, message, 1 < capacity);
                        AssertCached(bus, other, 2 < capacity);
                        Assert.That(
                            bus.IsReflexiveMethodCached(
                                typeof(SignatureSnapshotReceiverComponent),
                                third.signatureKey
                            ),
                            Is.EqualTo(0 < capacity),
                            "The newest method must remain cached when retention is enabled."
                        );
                        settings._bufferMaxDistinctEntries = Math.Min(capacity, 1);
                        DxMessagingRuntimeSettings.RaiseSettingsChanged(settings);
                        AssertOwnership(bus, Math.Min(capacity, 1));
                        AssertCached(bus, message, false);
                        AssertCached(bus, other, false);
                        bus.Trim(force: true);
                        AssertOwnership(bus, 0);
                        message.EmitTargeted(target, bus);
                        Assert.That(deliveries, Is.EqualTo(3), "Trimmed lookup must recover.");
                        AssertOwnership(bus, Math.Min(capacity, 1));
                        bus.ResetState();
                        AssertOwnership(bus, 0);
                        TestContext.Out.WriteLine(
                            $"DXM_REFLEXIVE_REENTRY_OWNERSHIP capacity={capacity}; slot={metadataSlot}; fresh={freshSignature}; cycle={cycle}; callbacks={callbacks}; nestedReturns={nestedReturns}; deliveries={deliveries}; finalCacheCount={MessageBusInspection.ReflexiveMethodCacheCount(bus)}"
                        );
                    }
                }
                finally
                {
                    bus.ResetState();
                }
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }

        private static void AssertCached(MessageBus bus, ReflexiveMessage message, bool expected)
        {
            Assert.That(
                bus.IsReflexiveMethodCached(typeof(ReflexiveReentryReceiver), message.signatureKey),
                Is.EqualTo(expected),
                "Expected cache membership for " + message.method
            );
        }

        private static void AssertOwnership(MessageBus bus, int expectedCount)
        {
            Dictionary<
                (Type, MethodSignatureKey),
                LinkedListNode<MessageBus.ReflexiveMethodEntry>
            > cache =
                (Dictionary<
                    (Type, MethodSignatureKey),
                    LinkedListNode<MessageBus.ReflexiveMethodEntry>
                >)
                    MessageBusInspection.ReadField(bus, "_methodCache");
            LinkedList<MessageBus.ReflexiveMethodEntry> lru =
                (LinkedList<MessageBus.ReflexiveMethodEntry>)
                    MessageBusInspection.ReadField(bus, "_methodCacheLru");
            Assert.That(cache.Count, Is.EqualTo(expectedCount), "Dictionary must obey capacity.");
            Assert.That(lru.Count, Is.EqualTo(expectedCount), "LRU must have one node per key.");
            HashSet<LinkedListNode<MessageBus.ReflexiveMethodEntry>> nodes = new();
            foreach (
                KeyValuePair<
                    (Type, MethodSignatureKey),
                    LinkedListNode<MessageBus.ReflexiveMethodEntry>
                > entry in cache
            )
            {
                Assert.That(nodes.Add(entry.Value), Is.True, "Every key must own a distinct node.");
                Assert.That(
                    entry.Value.List,
                    Is.SameAs(lru),
                    "Every dictionary node must belong to this LRU."
                );
            }
            int visited = 0;
            for (
                LinkedListNode<MessageBus.ReflexiveMethodEntry> node = lru.First;
                node != null;
                node = node.Next
            )
            {
                Assert.That(nodes.Remove(node), Is.True, "LRU must contain only dictionary nodes.");
                ++visited;
            }
            Assert.That(visited, Is.EqualTo(expectedCount), "Each LRU node must be visited once.");
            Assert.That(nodes, Is.Empty, "Every dictionary node must be reachable from the LRU.");
        }

        private sealed class CallbackType : System.Reflection.TypeDelegator
        {
            private readonly Action _callback;

            internal CallbackType(Action callback)
                : base(typeof(int)) => _callback = callback;

            public override Type UnderlyingSystemType
            {
                get
                {
                    _callback();
                    return typeof(int);
                }
            }

            public override int GetHashCode() => typeof(int).GetHashCode();
        }
    }
}
#endif
