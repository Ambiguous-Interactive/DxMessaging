#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System.Collections.Generic;
    using System.Linq;
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;
    using Scripts.Components;
    using Scripts.Messages;
    using UnityEngine;

    /// <summary>
    /// Broadcast-only emission tests preserved verbatim from the original
    /// <c>BroadcastTests.cs</c>. Logic that is provably common across all three
    /// message kinds is consolidated in <see cref="EmitTests"/>; the tests in
    /// this file remain kind-specific because their assertion semantics
    /// (per-source routing, GameObject vs Component source, broadcast-without-
    /// source fan-out, dual-mode counts) do not translate cleanly to untargeted
    /// dispatch.
    /// </summary>
    public sealed class EmitBroadcastSpecificTests : MessagingTestBase
    {
        [Test]
        public void SimpleGameObjectBroadcastNormal()
        {
            GameObject test1 = new(
                nameof(SimpleGameObjectBroadcastNormal) + "1",
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test1);
            GameObject test2 = new(
                nameof(SimpleGameObjectBroadcastNormal) + "2",
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test2);
            EmptyMessageAwareComponent component1 =
                test1.GetComponent<EmptyMessageAwareComponent>();
            EmptyMessageAwareComponent component2 =
                test2.GetComponent<EmptyMessageAwareComponent>();

            int test1ReceiveCount = 0;
            int test2ReceiveCount = 0;

            MessageRegistrationToken token1 = GetToken(component1);
            MessageRegistrationToken token2 = GetToken(component2);

            _ = token1.RegisterGameObjectBroadcast<SimpleBroadcastMessage>(
                test1,
                _ => ++test1ReceiveCount
            );
            _ = token2.RegisterGameObjectBroadcast<SimpleBroadcastMessage>(
                test2,
                _ => ++test2ReceiveCount
            );

            SimpleBroadcastMessage message = new();
            message.EmitGameObjectBroadcast(test1);
            Assert.AreEqual(1, test1ReceiveCount);
            Assert.AreEqual(0, test2ReceiveCount);

            message.EmitGameObjectBroadcast(test2);
            Assert.AreEqual(1, test1ReceiveCount);
            Assert.AreEqual(1, test2ReceiveCount);

            GameObject test3 = new(nameof(SimpleGameObjectBroadcastNormal) + "3");
            _spawned.Add(test3);
            message.EmitGameObjectBroadcast(test3);
            Assert.AreEqual(1, test1ReceiveCount);
            Assert.AreEqual(1, test2ReceiveCount);

            _ = test3.AddComponent<EmptyMessageAwareComponent>();
            message.EmitGameObjectBroadcast(test3);
            Assert.AreEqual(1, test1ReceiveCount);
            Assert.AreEqual(1, test2ReceiveCount);

            for (int i = 0; i < 100; ++i)
            {
                message.EmitGameObjectBroadcast(test1);
                Assert.AreEqual(2 + i, test1ReceiveCount);
                Assert.AreEqual(1 + i, test2ReceiveCount);

                message.EmitGameObjectBroadcast(test2);
                Assert.AreEqual(2 + i, test1ReceiveCount);
                Assert.AreEqual(2 + i, test2ReceiveCount);
            }
        }

        [Test]
        public void SimpleGameObjectBroadcastNoCopy()
        {
            GameObject test1 = new(
                nameof(SimpleGameObjectBroadcastNoCopy) + "1",
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test1);
            GameObject test2 = new(
                nameof(SimpleGameObjectBroadcastNoCopy) + "2",
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test2);
            EmptyMessageAwareComponent component1 =
                test1.GetComponent<EmptyMessageAwareComponent>();
            EmptyMessageAwareComponent component2 =
                test2.GetComponent<EmptyMessageAwareComponent>();

            int test1ReceiveCount = 0;

            int test2ReceiveCount = 0;

            MessageRegistrationToken token1 = GetToken(component1);
            MessageRegistrationToken token2 = GetToken(component2);

            _ = token1.RegisterGameObjectBroadcast<SimpleBroadcastMessage>(test1, Test1Receive);
            _ = token2.RegisterGameObjectBroadcast<SimpleBroadcastMessage>(test2, Test2Receive);

            SimpleBroadcastMessage message = new();
            message.EmitGameObjectBroadcast(test1);
            Assert.AreEqual(1, test1ReceiveCount);
            Assert.AreEqual(0, test2ReceiveCount);

            message.EmitGameObjectBroadcast(test2);
            Assert.AreEqual(1, test1ReceiveCount);
            Assert.AreEqual(1, test2ReceiveCount);

            GameObject test3 = new(nameof(SimpleGameObjectBroadcastNoCopy) + "3");
            message.EmitGameObjectBroadcast(test3);
            Assert.AreEqual(1, test1ReceiveCount);
            Assert.AreEqual(1, test2ReceiveCount);

            _ = test3.AddComponent<EmptyMessageAwareComponent>();
            _spawned.Add(test3);
            message.EmitGameObjectBroadcast(test3);
            Assert.AreEqual(1, test1ReceiveCount);
            Assert.AreEqual(1, test2ReceiveCount);

            for (int i = 0; i < 100; ++i)
            {
                message.EmitGameObjectBroadcast(test1);
                Assert.AreEqual(2 + i, test1ReceiveCount);
                Assert.AreEqual(1 + i, test2ReceiveCount);

                message.EmitGameObjectBroadcast(test2);
                Assert.AreEqual(2 + i, test1ReceiveCount);
                Assert.AreEqual(2 + i, test2ReceiveCount);
            }
            return;

            void Test1Receive(ref SimpleBroadcastMessage message)
            {
                ++test1ReceiveCount;
            }

            void Test2Receive(ref SimpleBroadcastMessage message)
            {
                ++test2ReceiveCount;
            }
        }

        [Test]
        public void SimpleGameObjectBroadcastDualMode()
        {
            GameObject test1 = new(
                nameof(SimpleGameObjectBroadcastDualMode) + "1",
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test1);
            GameObject test2 = new(
                nameof(SimpleGameObjectBroadcastDualMode) + "2",
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test2);
            EmptyMessageAwareComponent component1 =
                test1.GetComponent<EmptyMessageAwareComponent>();
            EmptyMessageAwareComponent component2 =
                test2.GetComponent<EmptyMessageAwareComponent>();

            int test1ReceiveCount = 0;

            int test2ReceiveCount = 0;

            MessageRegistrationToken token1 = GetToken(component1);
            MessageRegistrationToken token2 = GetToken(component2);

            HashSet<MessageRegistrationHandle> handles = new();
            MessageRegistrationHandle handle =
                token1.RegisterGameObjectBroadcast<SimpleBroadcastMessage>(test1, Test1Receive);
            _ = handles.Add(handle);
            handle = token1.RegisterGameObjectBroadcast<SimpleBroadcastMessage>(
                test1,
                _ => ++test1ReceiveCount
            );
            handle = token2.RegisterGameObjectBroadcast<SimpleBroadcastMessage>(
                test2,
                Test2Receive
            );
            _ = handles.Add(handle);

            SimpleBroadcastMessage message = new();
            message.EmitGameObjectBroadcast(test1);
            Assert.AreEqual(2, test1ReceiveCount);
            Assert.AreEqual(0, test2ReceiveCount);

            message.EmitGameObjectBroadcast(test2);
            Assert.AreEqual(2, test1ReceiveCount);
            Assert.AreEqual(1, test2ReceiveCount);

            GameObject test3 = new(nameof(SimpleGameObjectBroadcastDualMode) + "3");
            _spawned.Add(test3);
            message.EmitGameObjectBroadcast(test3);
            Assert.AreEqual(2, test1ReceiveCount);
            Assert.AreEqual(1, test2ReceiveCount);

            _ = test3.AddComponent<EmptyMessageAwareComponent>();
            message.EmitGameObjectBroadcast(test3);
            Assert.AreEqual(2, test1ReceiveCount);
            Assert.AreEqual(1, test2ReceiveCount);

            for (int i = 0; i < 100; ++i)
            {
                message.EmitGameObjectBroadcast(test1);
                Assert.AreEqual(2 + (1 + i) * 2, test1ReceiveCount);
                Assert.AreEqual(1 + i, test2ReceiveCount);

                message.EmitGameObjectBroadcast(test2);
                Assert.AreEqual(2 + (1 + i) * 2, test1ReceiveCount);
                Assert.AreEqual(2 + i, test2ReceiveCount);
            }
            return;

            void Test1Receive(ref SimpleBroadcastMessage message)
            {
                ++test1ReceiveCount;
            }

            void Test2Receive(ref SimpleBroadcastMessage message)
            {
                ++test2ReceiveCount;
            }
        }

        [Test]
        public void SimpleComponentBroadcastNormal()
        {
            GameObject test1 = new(
                nameof(SimpleComponentBroadcastNormal) + "1",
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test1);
            GameObject test2 = new(
                nameof(SimpleComponentBroadcastNormal) + "3",
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test2);
            EmptyMessageAwareComponent component1 =
                test1.GetComponent<EmptyMessageAwareComponent>();
            EmptyMessageAwareComponent component2 =
                test2.GetComponent<EmptyMessageAwareComponent>();

            int test1ReceiveCount = 0;
            int test2ReceiveCount = 0;

            MessageRegistrationToken token1 = GetToken(component1);
            MessageRegistrationToken token2 = GetToken(component2);

            _ = token1.RegisterComponentBroadcast<SimpleBroadcastMessage>(
                component1,
                _ => ++test1ReceiveCount
            );
            _ = token2.RegisterComponentBroadcast<SimpleBroadcastMessage>(
                component2,
                _ => ++test2ReceiveCount
            );

            SimpleBroadcastMessage message = new();
            message.EmitComponentBroadcast(component1);
            Assert.AreEqual(1, test1ReceiveCount);
            Assert.AreEqual(0, test2ReceiveCount);

            message.EmitComponentBroadcast(component2);
            Assert.AreEqual(1, test1ReceiveCount);
            Assert.AreEqual(1, test2ReceiveCount);

            GameObject test3 = new(nameof(SimpleComponentBroadcastNormal) + "3");
            _spawned.Add(test3);
            message.EmitComponentBroadcast(test3.transform);
            Assert.AreEqual(1, test1ReceiveCount);
            Assert.AreEqual(1, test2ReceiveCount);

            EmptyMessageAwareComponent component3 =
                test3.AddComponent<EmptyMessageAwareComponent>();
            message.EmitComponentBroadcast(component3);
            Assert.AreEqual(1, test1ReceiveCount);
            Assert.AreEqual(1, test2ReceiveCount);

            for (int i = 0; i < 100; ++i)
            {
                message.EmitComponentBroadcast(component1);
                Assert.AreEqual(2 + i, test1ReceiveCount);
                Assert.AreEqual(1 + i, test2ReceiveCount);

                message.EmitComponentBroadcast(component2);
                Assert.AreEqual(2 + i, test1ReceiveCount);
                Assert.AreEqual(2 + i, test2ReceiveCount);
            }
        }

        [Test]
        public void SimpleComponentBroadcastNoCopy()
        {
            GameObject test1 = new(
                nameof(SimpleComponentBroadcastNoCopy) + "1",
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test1);
            GameObject test2 = new(
                nameof(SimpleComponentBroadcastNoCopy) + "2",
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test2);
            EmptyMessageAwareComponent component1 =
                test1.GetComponent<EmptyMessageAwareComponent>();
            EmptyMessageAwareComponent component2 =
                test2.GetComponent<EmptyMessageAwareComponent>();

            int test1ReceiveCount = 0;

            int test2ReceiveCount = 0;

            MessageRegistrationToken token1 = GetToken(component1);
            MessageRegistrationToken token2 = GetToken(component2);

            _ = token1.RegisterComponentBroadcast<SimpleBroadcastMessage>(component1, Test1Receive);
            _ = token2.RegisterComponentBroadcast<SimpleBroadcastMessage>(component2, Test2Receive);

            SimpleBroadcastMessage message = new();
            message.EmitComponentBroadcast(component1);
            Assert.AreEqual(1, test1ReceiveCount);
            Assert.AreEqual(0, test2ReceiveCount);

            message.EmitComponentBroadcast(component2);
            Assert.AreEqual(1, test1ReceiveCount);
            Assert.AreEqual(1, test2ReceiveCount);

            GameObject test3 = new(nameof(SimpleComponentBroadcastNoCopy) + "3");
            _spawned.Add(test3);
            message.EmitComponentBroadcast(test3.transform);
            Assert.AreEqual(1, test1ReceiveCount);
            Assert.AreEqual(1, test2ReceiveCount);

            EmptyMessageAwareComponent component3 =
                test3.AddComponent<EmptyMessageAwareComponent>();
            message.EmitComponentBroadcast(component3);
            Assert.AreEqual(1, test1ReceiveCount);
            Assert.AreEqual(1, test2ReceiveCount);

            for (int i = 0; i < 100; ++i)
            {
                message.EmitComponentBroadcast(component1);
                Assert.AreEqual(2 + i, test1ReceiveCount);
                Assert.AreEqual(1 + i, test2ReceiveCount);

                message.EmitComponentBroadcast(component2);
                Assert.AreEqual(2 + i, test1ReceiveCount);
                Assert.AreEqual(2 + i, test2ReceiveCount);
            }

            return;

            void Test1Receive(ref SimpleBroadcastMessage message)
            {
                ++test1ReceiveCount;
            }

            void Test2Receive(ref SimpleBroadcastMessage message)
            {
                ++test2ReceiveCount;
            }
        }

        [Test]
        public void SimpleComponentBroadcastDualMode()
        {
            GameObject test1 = new(
                nameof(SimpleComponentBroadcastDualMode) + "1",
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test1);
            GameObject test2 = new(
                nameof(SimpleComponentBroadcastDualMode) + "2",
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test2);
            EmptyMessageAwareComponent component1 =
                test1.GetComponent<EmptyMessageAwareComponent>();
            EmptyMessageAwareComponent component2 =
                test2.GetComponent<EmptyMessageAwareComponent>();

            int test1ReceiveCount = 0;

            int test2ReceiveCount = 0;

            MessageRegistrationToken token1 = GetToken(component1);
            MessageRegistrationToken token2 = GetToken(component2);

            _ = token1.RegisterComponentBroadcast<SimpleBroadcastMessage>(component1, Test1Receive);
            _ = token1.RegisterComponentBroadcast<SimpleBroadcastMessage>(
                component1,
                _ => ++test1ReceiveCount
            );
            _ = token2.RegisterComponentBroadcast<SimpleBroadcastMessage>(component2, Test2Receive);

            SimpleBroadcastMessage message = new();
            message.EmitComponentBroadcast(component1);
            Assert.AreEqual(2, test1ReceiveCount);
            Assert.AreEqual(0, test2ReceiveCount);

            message.EmitComponentBroadcast(component2);
            Assert.AreEqual(2, test1ReceiveCount);
            Assert.AreEqual(1, test2ReceiveCount);

            GameObject test3 = new(nameof(SimpleComponentBroadcastDualMode) + "3");
            _spawned.Add(test3);
            message.EmitComponentBroadcast(test3.transform);
            Assert.AreEqual(2, test1ReceiveCount);
            Assert.AreEqual(1, test2ReceiveCount);

            EmptyMessageAwareComponent component3 =
                test3.AddComponent<EmptyMessageAwareComponent>();
            message.EmitComponentBroadcast(component3);
            Assert.AreEqual(2, test1ReceiveCount);
            Assert.AreEqual(1, test2ReceiveCount);

            for (int i = 0; i < 100; ++i)
            {
                message.EmitComponentBroadcast(component1);
                Assert.AreEqual(2 + (i + 1) * 2, test1ReceiveCount);
                Assert.AreEqual(1 + i, test2ReceiveCount);

                message.EmitComponentBroadcast(component2);
                Assert.AreEqual(2 + (i + 1) * 2, test1ReceiveCount);
                Assert.AreEqual(2 + i, test2ReceiveCount);
            }

            return;

            void Test1Receive(ref SimpleBroadcastMessage message)
            {
                ++test1ReceiveCount;
            }

            void Test2Receive(ref SimpleBroadcastMessage message)
            {
                ++test2ReceiveCount;
            }
        }

        [Test]
        public void SimpleBroadcastWithoutSourceNormal()
        {
            GameObject test1 = new(
                nameof(SimpleBroadcastWithoutSourceNormal) + "1",
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test1);
            GameObject test2 = new(
                nameof(SimpleBroadcastWithoutSourceNormal) + "2",
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test2);
            EmptyMessageAwareComponent component1 =
                test1.GetComponent<EmptyMessageAwareComponent>();
            EmptyMessageAwareComponent component2 =
                test2.GetComponent<EmptyMessageAwareComponent>();

            int test1ReceiveCount = 0;

            int test2ReceiveCount = 0;

            MessageRegistrationToken token1 = GetToken(component1);
            MessageRegistrationToken token2 = GetToken(component2);

            _ = token1.RegisterBroadcastWithoutSource<SimpleBroadcastMessage>(Test1Receive);
            _ = token2.RegisterBroadcastWithoutSource<SimpleBroadcastMessage>(Test2Receive);

            SimpleBroadcastMessage message = new();
            message.EmitComponentBroadcast(component1);
            Assert.AreEqual(1, test1ReceiveCount);
            Assert.AreEqual(1, test2ReceiveCount);

            message.EmitComponentBroadcast(component2);
            Assert.AreEqual(2, test1ReceiveCount);
            Assert.AreEqual(2, test2ReceiveCount);

            GameObject test3 = new(nameof(SimpleBroadcastWithoutSourceNormal) + "3");
            _spawned.Add(test3);
            message.EmitComponentBroadcast(test3.transform);
            Assert.AreEqual(3, test1ReceiveCount);
            Assert.AreEqual(3, test2ReceiveCount);

            EmptyMessageAwareComponent component3 =
                test3.AddComponent<EmptyMessageAwareComponent>();
            message.EmitComponentBroadcast(component3);
            Assert.AreEqual(4, test1ReceiveCount);
            Assert.AreEqual(4, test2ReceiveCount);

            for (int i = 0; i < 100; ++i)
            {
                message.EmitGameObjectBroadcast(test1);
                Assert.AreEqual(5 + (i * 2), test1ReceiveCount);
                Assert.AreEqual(5 + (i * 2), test2ReceiveCount);

                message.EmitComponentBroadcast(component2);
                Assert.AreEqual(4 + ((i + 1) * 2), test1ReceiveCount);
                Assert.AreEqual(4 + ((i + 1) * 2), test2ReceiveCount);
            }

            return;

            void Test1Receive(InstanceId id, SimpleBroadcastMessage message)
            {
                ++test1ReceiveCount;
            }

            void Test2Receive(InstanceId id, SimpleBroadcastMessage message)
            {
                ++test2ReceiveCount;
            }
        }

        [Test]
        public void SimpleBroadcastWithoutSourceNoCopy()
        {
            GameObject test1 = new(
                nameof(SimpleBroadcastWithoutSourceNoCopy) + "1",
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test1);
            GameObject test2 = new(
                nameof(SimpleBroadcastWithoutSourceNoCopy) + "2",
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test2);
            EmptyMessageAwareComponent component1 =
                test1.GetComponent<EmptyMessageAwareComponent>();
            EmptyMessageAwareComponent component2 =
                test2.GetComponent<EmptyMessageAwareComponent>();

            int test1ReceiveCount = 0;

            int test2ReceiveCount = 0;

            MessageRegistrationToken token1 = GetToken(component1);
            MessageRegistrationToken token2 = GetToken(component2);

            _ = token1.RegisterBroadcastWithoutSource<SimpleBroadcastMessage>(Test1Receive);
            _ = token2.RegisterBroadcastWithoutSource<SimpleBroadcastMessage>(Test2Receive);

            SimpleBroadcastMessage message = new();
            message.EmitComponentBroadcast(component1);
            Assert.AreEqual(1, test1ReceiveCount);
            Assert.AreEqual(1, test2ReceiveCount);

            message.EmitComponentBroadcast(component2);
            Assert.AreEqual(2, test1ReceiveCount);
            Assert.AreEqual(2, test2ReceiveCount);

            GameObject test3 = new(nameof(SimpleBroadcastWithoutSourceNoCopy) + "3");
            _spawned.Add(test3);
            message.EmitComponentBroadcast(test3.transform);
            Assert.AreEqual(3, test1ReceiveCount);
            Assert.AreEqual(3, test2ReceiveCount);

            EmptyMessageAwareComponent component3 =
                test3.AddComponent<EmptyMessageAwareComponent>();
            message.EmitComponentBroadcast(component3);
            Assert.AreEqual(4, test1ReceiveCount);
            Assert.AreEqual(4, test2ReceiveCount);

            for (int i = 0; i < 100; ++i)
            {
                message.EmitGameObjectBroadcast(test1);
                Assert.AreEqual(5 + (i * 2), test1ReceiveCount);
                Assert.AreEqual(5 + (i * 2), test2ReceiveCount);

                message.EmitComponentBroadcast(component2);
                Assert.AreEqual(4 + ((i + 1) * 2), test1ReceiveCount);
                Assert.AreEqual(4 + ((i + 1) * 2), test2ReceiveCount);
            }

            return;

            void Test1Receive(ref InstanceId id, ref SimpleBroadcastMessage message)
            {
                ++test1ReceiveCount;
            }

            void Test2Receive(ref InstanceId id, ref SimpleBroadcastMessage message)
            {
                ++test2ReceiveCount;
            }
        }

        [Test]
        public void SimpleBroadcastWithoutSourceDualMode()
        {
            GameObject test1 = new(
                nameof(SimpleBroadcastWithoutSourceDualMode) + "1",
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test1);
            GameObject test2 = new(
                nameof(SimpleBroadcastWithoutSourceDualMode) + "2",
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test2);
            EmptyMessageAwareComponent component1 =
                test1.GetComponent<EmptyMessageAwareComponent>();
            EmptyMessageAwareComponent component2 =
                test2.GetComponent<EmptyMessageAwareComponent>();

            int test1ReceiveCount = 0;

            int test2ReceiveCount = 0;

            // Assign them to the same token for simplicity
            MessageRegistrationToken token1 = GetToken(component1);

            _ = token1.RegisterBroadcastWithoutSource<SimpleBroadcastMessage>(Test1Receive);
            _ = token1.RegisterBroadcastWithoutSource<SimpleBroadcastMessage>(Test2Receive);

            SimpleBroadcastMessage message = new();
            message.EmitComponentBroadcast(component1);
            Assert.AreEqual(1, test1ReceiveCount);
            Assert.AreEqual(1, test2ReceiveCount);

            message.EmitComponentBroadcast(component2);
            Assert.AreEqual(2, test1ReceiveCount);
            Assert.AreEqual(2, test2ReceiveCount);

            GameObject test3 = new(nameof(SimpleBroadcastWithoutSourceDualMode) + "3");
            _spawned.Add(test3);
            message.EmitComponentBroadcast(test3.transform);
            Assert.AreEqual(3, test1ReceiveCount);
            Assert.AreEqual(3, test2ReceiveCount);

            EmptyMessageAwareComponent component3 =
                test3.AddComponent<EmptyMessageAwareComponent>();
            message.EmitComponentBroadcast(component3);
            Assert.AreEqual(4, test1ReceiveCount);
            Assert.AreEqual(4, test2ReceiveCount);

            for (int i = 0; i < 100; ++i)
            {
                message.EmitGameObjectBroadcast(test1);
                Assert.AreEqual(5 + (i * 2), test1ReceiveCount);
                Assert.AreEqual(5 + (i * 2), test2ReceiveCount);

                message.EmitComponentBroadcast(component2);
                Assert.AreEqual(4 + ((i + 1) * 2), test1ReceiveCount);
                Assert.AreEqual(4 + ((i + 1) * 2), test2ReceiveCount);
            }

            return;

            void Test1Receive(ref InstanceId id, ref SimpleBroadcastMessage message)
            {
                ++test1ReceiveCount;
            }

            void Test2Receive(InstanceId id, SimpleBroadcastMessage message)
            {
                ++test2ReceiveCount;
            }
        }

        [Test]
        public void BroadcastUntyped()
        {
            GameObject test = new(
                nameof(BroadcastUntyped) + "1",
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test);

            int gameObjectCount = 0;
            int componentCount = 0;

            EmptyMessageAwareComponent component = test.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            token.RegisterGameObjectBroadcast<SimpleBroadcastMessage>(test, ReceiveGameObject);
            token.RegisterComponentBroadcast<SimpleBroadcastMessage>(component, ReceiveComponent);

            IBroadcastMessage message = new SimpleBroadcastMessage();
            message.EmitComponentBroadcast(component);
            Assert.AreEqual(1, componentCount);
            Assert.AreEqual(0, gameObjectCount);
            message.EmitGameObjectBroadcast(test);
            Assert.AreEqual(1, componentCount);
            Assert.AreEqual(1, gameObjectCount);
            message.EmitComponentBroadcast(component);
            Assert.AreEqual(2, componentCount);
            Assert.AreEqual(1, gameObjectCount);
            message.EmitGameObjectBroadcast(test);
            Assert.AreEqual(2, componentCount);
            Assert.AreEqual(2, gameObjectCount);

            return;

            void ReceiveGameObject(ref SimpleBroadcastMessage message)
            {
                ++gameObjectCount;
            }

            void ReceiveComponent(ref SimpleBroadcastMessage message)
            {
                ++componentCount;
            }
        }

        [Test]
        public void PriorityGameObject()
        {
            GameObject test = new(
                nameof(PriorityGameObject) + "1",
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test);

            int[] received = new int[100];
            EmptyMessageAwareComponent component = test.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            for (int i = 0; i < received.Length; ++i)
            {
                int priority = i;
                token.RegisterGameObjectBroadcast(
                    test,
                    (ref SimpleBroadcastMessage _) =>
                    {
                        int previous = received[priority]++;
                        if (0 < priority)
                        {
                            Assert.AreEqual(previous + 1, received[priority - 1]);
                        }
                        for (int j = priority + 1; j < received.Length; ++j)
                        {
                            Assert.AreEqual(previous, received[j]);
                        }
                    },
                    priority: priority
                );
                token.RegisterGameObjectBroadcastPostProcessor(
                    test,
                    (ref SimpleBroadcastMessage _) =>
                    {
                        int previous = received[priority]++;
                        Assert.AreEqual(1, previous % 2);
                        if (0 < priority)
                        {
                            Assert.AreEqual(previous + 1, received[priority - 1]);
                        }
                        for (int j = priority + 1; j < received.Length; ++j)
                        {
                            Assert.AreEqual(previous, received[j]);
                        }
                    },
                    priority: priority
                );
            }

            SimpleBroadcastMessage message = new();
            const int numRuns = 100;
            for (int i = 0; i < numRuns; ++i)
            {
                // Should do something
                message.EmitGameObjectBroadcast(test);
                // Should do nothing
                message.EmitComponentBroadcast(component);
            }

            Assert.AreEqual(
                1,
                received.Distinct().Count(),
                "Expected received to be uniform, found: [{0}].",
                string.Join(",", received.Distinct().OrderBy(x => x))
            );

            Assert.AreEqual(numRuns * 2, received.Distinct().Single());
        }

        [Test]
        public void PriorityComponent()
        {
            GameObject test = new(
                nameof(PriorityComponent) + "1",
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test);

            int[] received = new int[100];
            EmptyMessageAwareComponent component = test.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            for (int i = 0; i < received.Length; ++i)
            {
                int priority = i;
                token.RegisterComponentBroadcast(
                    component,
                    (ref SimpleBroadcastMessage _) =>
                    {
                        int previous = received[priority]++;
                        for (int j = priority - 1; j >= 0; --j)
                        {
                            Assert.AreEqual(previous + 1, received[j]);
                        }
                        for (int j = priority + 1; j < received.Length; ++j)
                        {
                            Assert.AreEqual(previous, received[j]);
                        }
                    },
                    priority: priority
                );
                token.RegisterComponentBroadcastPostProcessor(
                    component,
                    (ref SimpleBroadcastMessage _) =>
                    {
                        int previous = received[priority]++;
                        Assert.AreEqual(1, previous % 2);
                        for (int j = priority - 1; j >= 0; --j)
                        {
                            Assert.AreEqual(previous + 1, received[j]);
                        }

                        for (int j = priority + 1; j < received.Length; ++j)
                        {
                            Assert.AreEqual(previous, received[j]);
                        }
                    },
                    priority: priority
                );
            }

            SimpleBroadcastMessage message = new();
            const int numRuns = 100;
            for (int i = 0; i < numRuns; ++i)
            {
                // Should do something
                message.EmitComponentBroadcast(component);
                // Should do nothing
                message.EmitGameObjectBroadcast(test);
            }

            Assert.AreEqual(
                1,
                received.Distinct().Count(),
                "Expected received to be uniform, found: [{0}].",
                string.Join(",", received.Distinct().OrderBy(x => x))
            );

            Assert.AreEqual(numRuns * 2, received.Distinct().Single());
        }

        [Test]
        public void Interceptor()
        {
            GameObject test = new(nameof(Interceptor), typeof(EmptyMessageAwareComponent));
            _spawned.Add(test);

            int[] received = new int[100];
            EmptyMessageAwareComponent component = test.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            for (int i = 0; i < received.Length; ++i)
            {
                int priority = i;
                token.RegisterBroadcastInterceptor(
                    (ref InstanceId _, ref SimpleBroadcastMessage _) =>
                    {
                        int previous = received[priority]++;
                        for (int j = priority - 1; j >= 0; --j)
                        {
                            Assert.AreEqual(previous + 1, received[j]);
                        }
                        for (int j = priority + 1; j < received.Length; ++j)
                        {
                            Assert.AreEqual(previous, received[j]);
                        }

                        return true;
                    },
                    priority: priority
                );
            }

            SimpleBroadcastMessage message = new();
            const int numRuns = 100;
            for (int i = 0; i < numRuns; ++i)
            {
                message.EmitComponentBroadcast(component);
                message.EmitGameObjectBroadcast(test);
            }

            Assert.AreEqual(
                1,
                received.Distinct().Count(),
                "Expected received to be uniform, found: [{0}].",
                string.Join(",", received.Distinct().OrderBy(x => x))
            );

            Assert.AreEqual(numRuns * 2, received.Distinct().Single());
        }
    }
}

#endif
