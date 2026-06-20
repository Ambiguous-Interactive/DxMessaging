#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System.Linq;
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;
    using Scripts.Components;
    using Scripts.Messages;
    using UnityEngine;

    /// <summary>
    /// Targeted-only emission tests preserved verbatim from the original
    /// <c>TargetedTests.cs</c>. Logic that is provably common across all three
    /// message kinds is consolidated in <see cref="EmitTests"/>; the tests in
    /// this file remain kind-specific because their assertion semantics
    /// (per-target routing, GameObject vs Component target, targeted-without-
    /// targeting fan-out, dual-mode counts) do not translate cleanly to
    /// untargeted dispatch.
    /// </summary>
    public sealed class EmitTargetedSpecificTests : MessagingTestBase
    {
        [Test]
        public void SimpleGameObjectTargetedNormal()
        {
            GameObject test1 = new(
                nameof(SimpleGameObjectTargetedNormal) + "1",
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test1);
            GameObject test2 = new(
                nameof(SimpleGameObjectTargetedNormal) + "2",
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test2);
            EmptyMessageAwareComponent component1 =
                test1.GetComponent<EmptyMessageAwareComponent>();
            EmptyMessageAwareComponent component2 =
                test2.GetComponent<EmptyMessageAwareComponent>();

            int test1TargetedCount = 0;
            int test2TargetedCount = 0;

            MessageRegistrationToken token1 = GetToken(component1);
            MessageRegistrationToken token2 = GetToken(component2);

            _ = token1.RegisterGameObjectTargeted<SimpleTargetedMessage>(
                test1,
                _ => ++test1TargetedCount
            );
            _ = token2.RegisterGameObjectTargeted<SimpleTargetedMessage>(
                test2,
                _ => ++test2TargetedCount
            );

            SimpleTargetedMessage message = new();
            message.EmitGameObjectTargeted(test1);
            Assert.AreEqual(1, test1TargetedCount);
            Assert.AreEqual(0, test2TargetedCount);

            message.EmitGameObjectTargeted(test2);
            Assert.AreEqual(1, test1TargetedCount);
            Assert.AreEqual(1, test2TargetedCount);

            GameObject test3 = new(nameof(SimpleGameObjectTargetedNormal) + "3");
            _spawned.Add(test3);
            message.EmitGameObjectTargeted(test3);
            Assert.AreEqual(1, test1TargetedCount);
            Assert.AreEqual(1, test2TargetedCount);

            _ = test3.AddComponent<EmptyMessageAwareComponent>();
            message.EmitGameObjectTargeted(test3);
            Assert.AreEqual(1, test1TargetedCount);
            Assert.AreEqual(1, test2TargetedCount);

            for (int i = 0; i < 100; ++i)
            {
                message.EmitGameObjectTargeted(test1);
                Assert.AreEqual(2 + i, test1TargetedCount);
                Assert.AreEqual(1 + i, test2TargetedCount);

                message.EmitGameObjectTargeted(test2);
                Assert.AreEqual(2 + i, test1TargetedCount);
                Assert.AreEqual(2 + i, test2TargetedCount);
            }
        }

        [Test]
        public void SimpleGameObjectTargetedNoCopy()
        {
            GameObject test1 = new(
                nameof(SimpleGameObjectTargetedNoCopy) + "1",
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test1);
            GameObject test2 = new(
                nameof(SimpleGameObjectTargetedNoCopy) + "2",
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test2);
            EmptyMessageAwareComponent component1 =
                test1.GetComponent<EmptyMessageAwareComponent>();
            EmptyMessageAwareComponent component2 =
                test2.GetComponent<EmptyMessageAwareComponent>();

            int test1TargetedCount = 0;

            int test2TargetedCount = 0;

            MessageRegistrationToken token1 = GetToken(component1);
            MessageRegistrationToken token2 = GetToken(component2);

            _ = token1.RegisterGameObjectTargeted<SimpleTargetedMessage>(test1, Test1Receive);
            _ = token2.RegisterGameObjectTargeted<SimpleTargetedMessage>(test2, Test2Receive);

            SimpleTargetedMessage message = new();
            message.EmitGameObjectTargeted(test1);
            Assert.AreEqual(1, test1TargetedCount);
            Assert.AreEqual(0, test2TargetedCount);

            message.EmitGameObjectTargeted(test2);
            Assert.AreEqual(1, test1TargetedCount);
            Assert.AreEqual(1, test2TargetedCount);

            GameObject test3 = new(nameof(SimpleGameObjectTargetedNoCopy) + "3");
            message.EmitGameObjectTargeted(test3);
            Assert.AreEqual(1, test1TargetedCount);
            Assert.AreEqual(1, test2TargetedCount);

            _ = test3.AddComponent<EmptyMessageAwareComponent>();
            _spawned.Add(test3);
            message.EmitGameObjectTargeted(test3);
            Assert.AreEqual(1, test1TargetedCount);
            Assert.AreEqual(1, test2TargetedCount);

            for (int i = 0; i < 100; ++i)
            {
                message.EmitGameObjectTargeted(test1);
                Assert.AreEqual(2 + i, test1TargetedCount);
                Assert.AreEqual(1 + i, test2TargetedCount);

                message.EmitGameObjectTargeted(test2);
                Assert.AreEqual(2 + i, test1TargetedCount);
                Assert.AreEqual(2 + i, test2TargetedCount);
            }
            return;

            void Test1Receive(ref SimpleTargetedMessage message)
            {
                ++test1TargetedCount;
            }

            void Test2Receive(ref SimpleTargetedMessage message)
            {
                ++test2TargetedCount;
            }
        }

        [Test]
        public void SimpleGameObjectTargetedDualMode()
        {
            GameObject test1 = new(
                nameof(SimpleGameObjectTargetedDualMode) + "1",
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test1);
            GameObject test2 = new(
                nameof(SimpleGameObjectTargetedDualMode) + "2",
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

            _ = token1.RegisterGameObjectTargeted<SimpleTargetedMessage>(test1, Test1Receive);
            _ = token1.RegisterGameObjectTargeted<SimpleTargetedMessage>(
                test1,
                _ => ++test1ReceiveCount
            );
            _ = token2.RegisterGameObjectTargeted<SimpleTargetedMessage>(test2, Test2Receive);

            SimpleTargetedMessage message = new();
            message.EmitGameObjectTargeted(test1);
            Assert.AreEqual(2, test1ReceiveCount);
            Assert.AreEqual(0, test2ReceiveCount);

            message.EmitGameObjectTargeted(test2);
            Assert.AreEqual(2, test1ReceiveCount);
            Assert.AreEqual(1, test2ReceiveCount);

            GameObject test3 = new(nameof(SimpleGameObjectTargetedDualMode) + "3");
            _spawned.Add(test3);
            message.EmitGameObjectTargeted(test3);
            Assert.AreEqual(2, test1ReceiveCount);
            Assert.AreEqual(1, test2ReceiveCount);

            _ = test3.AddComponent<EmptyMessageAwareComponent>();
            message.EmitGameObjectTargeted(test3);
            Assert.AreEqual(2, test1ReceiveCount);
            Assert.AreEqual(1, test2ReceiveCount);

            for (int i = 0; i < 100; ++i)
            {
                message.EmitGameObjectTargeted(test1);
                Assert.AreEqual(2 + (1 + i) * 2, test1ReceiveCount);
                Assert.AreEqual(1 + i, test2ReceiveCount);

                message.EmitGameObjectTargeted(test2);
                Assert.AreEqual(2 + (1 + i) * 2, test1ReceiveCount);
                Assert.AreEqual(2 + i, test2ReceiveCount);
            }
            return;

            void Test1Receive(ref SimpleTargetedMessage message)
            {
                ++test1ReceiveCount;
            }

            void Test2Receive(ref SimpleTargetedMessage message)
            {
                ++test2ReceiveCount;
            }
        }

        [Test]
        public void SimpleComponentTargetedNormal()
        {
            GameObject test1 = new(
                nameof(SimpleComponentTargetedNormal) + "1",
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test1);
            GameObject test2 = new(
                nameof(SimpleComponentTargetedNormal) + "3",
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

            _ = token1.RegisterComponentTargeted<SimpleTargetedMessage>(
                component1,
                _ => ++test1ReceiveCount
            );
            _ = token2.RegisterComponentTargeted<SimpleTargetedMessage>(
                component2,
                _ => ++test2ReceiveCount
            );

            SimpleTargetedMessage message = new();
            message.EmitComponentTargeted(component1);
            Assert.AreEqual(1, test1ReceiveCount);
            Assert.AreEqual(0, test2ReceiveCount);

            message.EmitComponentTargeted(component2);
            Assert.AreEqual(1, test1ReceiveCount);
            Assert.AreEqual(1, test2ReceiveCount);

            GameObject test3 = new(nameof(SimpleComponentTargetedNormal) + "3");
            _spawned.Add(test3);
            message.EmitComponentTargeted(test3.transform);
            Assert.AreEqual(1, test1ReceiveCount);
            Assert.AreEqual(1, test2ReceiveCount);

            EmptyMessageAwareComponent component3 =
                test3.AddComponent<EmptyMessageAwareComponent>();
            message.EmitComponentTargeted(component3);
            Assert.AreEqual(1, test1ReceiveCount);
            Assert.AreEqual(1, test2ReceiveCount);

            for (int i = 0; i < 100; ++i)
            {
                message.EmitComponentTargeted(component1);
                Assert.AreEqual(2 + i, test1ReceiveCount);
                Assert.AreEqual(1 + i, test2ReceiveCount);

                message.EmitComponentTargeted(component2);
                Assert.AreEqual(2 + i, test1ReceiveCount);
                Assert.AreEqual(2 + i, test2ReceiveCount);
            }
        }

        [Test]
        public void SimpleComponentTargetedNoCopy()
        {
            GameObject test1 = new(
                nameof(SimpleComponentTargetedNoCopy) + "1",
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test1);
            GameObject test2 = new(
                nameof(SimpleComponentTargetedNoCopy) + "2",
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

            _ = token1.RegisterComponentTargeted<SimpleTargetedMessage>(component1, Test1Receive);
            _ = token2.RegisterComponentTargeted<SimpleTargetedMessage>(component2, Test2Receive);

            SimpleTargetedMessage message = new();
            message.EmitComponentTargeted(component1);
            Assert.AreEqual(1, test1ReceiveCount);
            Assert.AreEqual(0, test2ReceiveCount);

            message.EmitComponentTargeted(component2);
            Assert.AreEqual(1, test1ReceiveCount);
            Assert.AreEqual(1, test2ReceiveCount);

            GameObject test3 = new(nameof(SimpleComponentTargetedNoCopy) + "3");
            _spawned.Add(test3);
            message.EmitComponentTargeted(test3.transform);
            Assert.AreEqual(1, test1ReceiveCount);
            Assert.AreEqual(1, test2ReceiveCount);

            EmptyMessageAwareComponent component3 =
                test3.AddComponent<EmptyMessageAwareComponent>();
            message.EmitComponentTargeted(component3);
            Assert.AreEqual(1, test1ReceiveCount);
            Assert.AreEqual(1, test2ReceiveCount);

            for (int i = 0; i < 100; ++i)
            {
                message.EmitComponentTargeted(component1);
                Assert.AreEqual(2 + i, test1ReceiveCount);
                Assert.AreEqual(1 + i, test2ReceiveCount);

                message.EmitComponentTargeted(component2);
                Assert.AreEqual(2 + i, test1ReceiveCount);
                Assert.AreEqual(2 + i, test2ReceiveCount);
            }

            return;

            void Test1Receive(ref SimpleTargetedMessage message)
            {
                ++test1ReceiveCount;
            }

            void Test2Receive(ref SimpleTargetedMessage message)
            {
                ++test2ReceiveCount;
            }
        }

        [Test]
        public void SimpleComponentTargetedDualMode()
        {
            GameObject test1 = new(
                nameof(SimpleComponentTargetedDualMode) + "1",
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test1);
            GameObject test2 = new(
                nameof(SimpleComponentTargetedDualMode) + "2",
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

            _ = token1.RegisterComponentTargeted<SimpleTargetedMessage>(component1, Test1Receive);
            _ = token1.RegisterComponentTargeted<SimpleTargetedMessage>(
                component1,
                _ => ++test1ReceiveCount
            );
            _ = token2.RegisterComponentTargeted<SimpleTargetedMessage>(component2, Test2Receive);

            SimpleTargetedMessage message = new();
            message.EmitComponentTargeted(component1);
            Assert.AreEqual(2, test1ReceiveCount);
            Assert.AreEqual(0, test2ReceiveCount);

            message.EmitComponentTargeted(component2);
            Assert.AreEqual(2, test1ReceiveCount);
            Assert.AreEqual(1, test2ReceiveCount);

            GameObject test3 = new(nameof(SimpleComponentTargetedDualMode) + "3");
            _spawned.Add(test3);
            message.EmitComponentTargeted(test3.transform);
            Assert.AreEqual(2, test1ReceiveCount);
            Assert.AreEqual(1, test2ReceiveCount);

            EmptyMessageAwareComponent component3 =
                test3.AddComponent<EmptyMessageAwareComponent>();
            message.EmitComponentTargeted(component3);
            Assert.AreEqual(2, test1ReceiveCount);
            Assert.AreEqual(1, test2ReceiveCount);

            for (int i = 0; i < 100; ++i)
            {
                message.EmitComponentTargeted(component1);
                Assert.AreEqual(2 + (i + 1) * 2, test1ReceiveCount);
                Assert.AreEqual(1 + i, test2ReceiveCount);

                message.EmitComponentTargeted(component2);
                Assert.AreEqual(2 + (i + 1) * 2, test1ReceiveCount);
                Assert.AreEqual(2 + i, test2ReceiveCount);
            }

            return;

            void Test1Receive(ref SimpleTargetedMessage message)
            {
                ++test1ReceiveCount;
            }

            void Test2Receive(ref SimpleTargetedMessage message)
            {
                ++test2ReceiveCount;
            }
        }

        [Test]
        public void SimpleTargetedWithoutTargetingNormal()
        {
            GameObject test1 = new(
                nameof(SimpleTargetedWithoutTargetingNormal) + "1",
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test1);
            GameObject test2 = new(
                nameof(SimpleTargetedWithoutTargetingNormal) + "2",
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

            _ = token1.RegisterTargetedWithoutTargeting<SimpleTargetedMessage>(Test1Receive);
            _ = token2.RegisterTargetedWithoutTargeting<SimpleTargetedMessage>(Test2Receive);

            SimpleTargetedMessage message = new();
            message.EmitComponentTargeted(component1);
            Assert.AreEqual(1, test1ReceiveCount);
            Assert.AreEqual(1, test2ReceiveCount);

            message.EmitComponentTargeted(component2);
            Assert.AreEqual(2, test1ReceiveCount);
            Assert.AreEqual(2, test2ReceiveCount);

            GameObject test3 = new(nameof(SimpleTargetedWithoutTargetingNormal) + "3");
            _spawned.Add(test3);
            message.EmitComponentTargeted(test3.transform);
            Assert.AreEqual(3, test1ReceiveCount);
            Assert.AreEqual(3, test2ReceiveCount);

            EmptyMessageAwareComponent component3 =
                test3.AddComponent<EmptyMessageAwareComponent>();
            message.EmitComponentTargeted(component3);
            Assert.AreEqual(4, test1ReceiveCount);
            Assert.AreEqual(4, test2ReceiveCount);

            for (int i = 0; i < 100; ++i)
            {
                message.EmitGameObjectTargeted(test1);
                Assert.AreEqual(5 + (i * 2), test1ReceiveCount);
                Assert.AreEqual(5 + (i * 2), test2ReceiveCount);

                message.EmitComponentTargeted(component2);
                Assert.AreEqual(4 + ((i + 1) * 2), test1ReceiveCount);
                Assert.AreEqual(4 + ((i + 1) * 2), test2ReceiveCount);
            }

            return;

            void Test1Receive(InstanceId id, SimpleTargetedMessage message)
            {
                ++test1ReceiveCount;
            }

            void Test2Receive(InstanceId id, SimpleTargetedMessage message)
            {
                ++test2ReceiveCount;
            }
        }

        [Test]
        public void SimpleTargetedWithoutTargetingNoCopy()
        {
            GameObject test1 = new(
                nameof(SimpleTargetedWithoutTargetingNoCopy) + "1",
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test1);
            GameObject test2 = new(
                nameof(SimpleTargetedWithoutTargetingNoCopy) + "2",
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

            _ = token1.RegisterTargetedWithoutTargeting<SimpleTargetedMessage>(Test1Receive);
            _ = token2.RegisterTargetedWithoutTargeting<SimpleTargetedMessage>(Test2Receive);

            SimpleTargetedMessage message = new();
            message.EmitComponentTargeted(component1);
            Assert.AreEqual(1, test1ReceiveCount);
            Assert.AreEqual(1, test2ReceiveCount);

            message.EmitComponentTargeted(component2);
            Assert.AreEqual(2, test1ReceiveCount);
            Assert.AreEqual(2, test2ReceiveCount);

            GameObject test3 = new(nameof(SimpleTargetedWithoutTargetingNoCopy) + "3");
            _spawned.Add(test3);
            message.EmitComponentTargeted(test3.transform);
            Assert.AreEqual(3, test1ReceiveCount);
            Assert.AreEqual(3, test2ReceiveCount);

            EmptyMessageAwareComponent component3 =
                test3.AddComponent<EmptyMessageAwareComponent>();
            message.EmitComponentTargeted(component3);
            Assert.AreEqual(4, test1ReceiveCount);
            Assert.AreEqual(4, test2ReceiveCount);

            for (int i = 0; i < 100; ++i)
            {
                message.EmitGameObjectTargeted(test1);
                Assert.AreEqual(5 + (i * 2), test1ReceiveCount);
                Assert.AreEqual(5 + (i * 2), test2ReceiveCount);

                message.EmitComponentTargeted(component2);
                Assert.AreEqual(4 + ((i + 1) * 2), test1ReceiveCount);
                Assert.AreEqual(4 + ((i + 1) * 2), test2ReceiveCount);
            }

            return;

            void Test1Receive(ref InstanceId id, ref SimpleTargetedMessage message)
            {
                ++test1ReceiveCount;
            }

            void Test2Receive(ref InstanceId id, ref SimpleTargetedMessage message)
            {
                ++test2ReceiveCount;
            }
        }

        [Test]
        public void SimpleTargetedWithoutTargetingDualMode()
        {
            GameObject test1 = new(
                nameof(SimpleTargetedWithoutTargetingDualMode) + "1",
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test1);
            GameObject test2 = new(
                nameof(SimpleTargetedWithoutTargetingDualMode) + "2",
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

            _ = token1.RegisterTargetedWithoutTargeting<SimpleTargetedMessage>(Test1Receive);
            _ = token1.RegisterTargetedWithoutTargeting<SimpleTargetedMessage>(Test2Receive);

            SimpleTargetedMessage message = new();
            message.EmitComponentTargeted(component1);
            Assert.AreEqual(1, test1ReceiveCount);
            Assert.AreEqual(1, test2ReceiveCount);

            message.EmitComponentTargeted(component2);
            Assert.AreEqual(2, test1ReceiveCount);
            Assert.AreEqual(2, test2ReceiveCount);

            GameObject test3 = new(nameof(SimpleTargetedWithoutTargetingDualMode) + "3");
            _spawned.Add(test3);
            message.EmitComponentTargeted(test3.transform);
            Assert.AreEqual(3, test1ReceiveCount);
            Assert.AreEqual(3, test2ReceiveCount);

            EmptyMessageAwareComponent component3 =
                test3.AddComponent<EmptyMessageAwareComponent>();
            message.EmitComponentTargeted(component3);
            Assert.AreEqual(4, test1ReceiveCount);
            Assert.AreEqual(4, test2ReceiveCount);

            for (int i = 0; i < 100; ++i)
            {
                message.EmitGameObjectTargeted(test1);
                Assert.AreEqual(5 + (i * 2), test1ReceiveCount);
                Assert.AreEqual(5 + (i * 2), test2ReceiveCount);

                message.EmitComponentTargeted(component2);
                Assert.AreEqual(4 + ((i + 1) * 2), test1ReceiveCount);
                Assert.AreEqual(4 + ((i + 1) * 2), test2ReceiveCount);
            }

            return;

            void Test1Receive(ref InstanceId id, ref SimpleTargetedMessage message)
            {
                ++test1ReceiveCount;
            }

            void Test2Receive(InstanceId id, SimpleTargetedMessage message)
            {
                ++test2ReceiveCount;
            }
        }

        [Test]
        public void TargetedUntyped()
        {
            GameObject test = new(
                nameof(TargetedUntyped) + "1",
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test);

            int gameObjectCount = 0;
            int componentCount = 0;

            EmptyMessageAwareComponent component = test.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            token.RegisterGameObjectTargeted<SimpleTargetedMessage>(test, ReceiveGameObject);
            token.RegisterComponentTargeted<SimpleTargetedMessage>(component, ReceiveComponent);

            ITargetedMessage message = new SimpleTargetedMessage();
            message.EmitComponentTargeted(component);
            Assert.AreEqual(1, componentCount);
            Assert.AreEqual(0, gameObjectCount);
            message.EmitGameObjectTargeted(test);
            Assert.AreEqual(1, componentCount);
            Assert.AreEqual(1, gameObjectCount);
            message.EmitComponentTargeted(component);
            Assert.AreEqual(2, componentCount);
            Assert.AreEqual(1, gameObjectCount);
            message.EmitGameObjectTargeted(test);
            Assert.AreEqual(2, componentCount);
            Assert.AreEqual(2, gameObjectCount);

            return;

            void ReceiveGameObject(ref SimpleTargetedMessage message)
            {
                ++gameObjectCount;
            }

            void ReceiveComponent(ref SimpleTargetedMessage message)
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
                token.RegisterGameObjectTargeted(
                    test,
                    (ref SimpleTargetedMessage _) =>
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
                token.RegisterGameObjectTargetedPostProcessor(
                    test,
                    (ref SimpleTargetedMessage _) =>
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

            SimpleTargetedMessage message = new();
            const int numRuns = 100;
            for (int i = 0; i < numRuns; ++i)
            {
                // Should do something
                message.EmitGameObjectTargeted(test);
                // Should do nothing
                message.EmitComponentTargeted(component);
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
                token.RegisterComponentTargeted(
                    component,
                    (ref SimpleTargetedMessage _) =>
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
                token.RegisterComponentTargetedPostProcessor(
                    component,
                    (ref SimpleTargetedMessage _) =>
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

            SimpleTargetedMessage message = new();
            const int numRuns = 100;
            for (int i = 0; i < numRuns; ++i)
            {
                // Should do something
                message.EmitComponentTargeted(component);
                // Should do nothing
                message.EmitGameObjectTargeted(test);
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
                token.RegisterTargetedInterceptor(
                    (ref InstanceId _, ref SimpleTargetedMessage _) =>
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

            SimpleTargetedMessage message = new();
            const int numRuns = 100;
            for (int i = 0; i < numRuns; ++i)
            {
                message.EmitComponentTargeted(component);
                message.EmitGameObjectTargeted(test);
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
