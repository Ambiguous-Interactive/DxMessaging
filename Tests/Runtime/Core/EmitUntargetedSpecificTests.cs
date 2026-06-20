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
    /// Untargeted-only emission tests preserved verbatim from the original
    /// <c>UntargetedTests.cs</c>. Logic that is provably common across all three
    /// message kinds is consolidated in <see cref="EmitTests"/>; the tests in
    /// this file remain kind-specific because their assertion semantics do not
    /// translate cleanly to targeted or broadcast dispatch (untargeted fan-out
    /// to every registered handler differs from targeted/broadcast routing).
    /// </summary>
    public sealed class EmitUntargetedSpecificTests : MessagingTestBase
    {
        [Test]
        public void SimpleNormal()
        {
            GameObject test1 = new(nameof(SimpleNormal) + "1", typeof(EmptyMessageAwareComponent));
            _spawned.Add(test1);
            GameObject test2 = new(nameof(SimpleNormal) + "2", typeof(EmptyMessageAwareComponent));
            _spawned.Add(test2);

            EmptyMessageAwareComponent component1 =
                test1.GetComponent<EmptyMessageAwareComponent>();
            EmptyMessageAwareComponent component2 =
                test2.GetComponent<EmptyMessageAwareComponent>();

            int count1 = 0;
            MessageRegistrationToken token1 = GetToken(component1);
            _ = token1.RegisterUntargeted<SimpleUntargetedMessage>(_ => ++count1);
            int count2 = 0;
            MessageRegistrationToken token2 = GetToken(component2);
            _ = token2.RegisterUntargeted<SimpleUntargetedMessage>(_ => ++count2);
            SimpleUntargetedMessage message = new();
            for (int i = 0; i < 100; ++i)
            {
                Assert.AreEqual(i, count1);
                Assert.AreEqual(i, count2);
                message.EmitUntargeted();
            }
        }

        [Test]
        public void SimpleNoCopy()
        {
            GameObject test1 = new(nameof(SimpleNormal) + "1", typeof(EmptyMessageAwareComponent));
            _spawned.Add(test1);
            GameObject test2 = new(nameof(SimpleNormal) + "2", typeof(EmptyMessageAwareComponent));
            _spawned.Add(test2);

            EmptyMessageAwareComponent component1 =
                test1.GetComponent<EmptyMessageAwareComponent>();
            EmptyMessageAwareComponent component2 =
                test2.GetComponent<EmptyMessageAwareComponent>();

            int count1 = 0;
            MessageRegistrationToken token1 = GetToken(component1);
            _ = token1.RegisterUntargeted<SimpleUntargetedMessage>(Receive1);

            int count2 = 0;
            MessageRegistrationToken token2 = GetToken(component2);
            _ = token2.RegisterUntargeted<SimpleUntargetedMessage>(Receive2);

            SimpleUntargetedMessage message = new();
            for (int i = 0; i < 100; ++i)
            {
                Assert.AreEqual(i, count1);
                Assert.AreEqual(i, count2);
                message.EmitUntargeted();
            }

            return;

            void Receive1(ref SimpleUntargetedMessage message)
            {
                ++count1;
            }

            void Receive2(ref SimpleUntargetedMessage message)
            {
                ++count2;
            }
        }

        [Test]
        public void SimpleDualMode()
        {
            GameObject test1 = new(nameof(SimpleNormal) + "1", typeof(EmptyMessageAwareComponent));
            _spawned.Add(test1);
            GameObject test2 = new(nameof(SimpleNormal) + "2", typeof(EmptyMessageAwareComponent));
            _spawned.Add(test2);

            EmptyMessageAwareComponent component1 =
                test1.GetComponent<EmptyMessageAwareComponent>();
            EmptyMessageAwareComponent component2 =
                test2.GetComponent<EmptyMessageAwareComponent>();

            int count1 = 0;
            MessageRegistrationToken token1 = GetToken(component1);
            _ = token1.RegisterUntargeted<SimpleUntargetedMessage>(Receive1);
            _ = token1.RegisterUntargeted<SimpleUntargetedMessage>(_ => ++count1);

            int count2 = 0;
            MessageRegistrationToken token2 = GetToken(component2);
            _ = token2.RegisterUntargeted<SimpleUntargetedMessage>(Receive2);

            SimpleUntargetedMessage message = new();
            for (int i = 0; i < 100; ++i)
            {
                Assert.AreEqual(i * 2, count1);
                Assert.AreEqual(i, count2);
                message.EmitUntargeted();
            }

            return;

            void Receive1(ref SimpleUntargetedMessage message)
            {
                ++count1;
            }

            void Receive2(ref SimpleUntargetedMessage message)
            {
                ++count2;
            }
        }

        [Test]
        public void UntargetedUntyped()
        {
            GameObject test = new(
                nameof(UntargetedUntyped) + "1",
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test);

            int count = 0;

            EmptyMessageAwareComponent component = test.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            token.RegisterUntargeted<SimpleUntargetedMessage>(Receive);

            IUntargetedMessage message = new SimpleUntargetedMessage();
            message.EmitUntargeted();
            Assert.AreEqual(1, count);
            message.EmitUntargeted();
            Assert.AreEqual(2, count);
            message.EmitUntargeted();
            Assert.AreEqual(3, count);
            message.EmitUntargeted();
            Assert.AreEqual(4, count);

            return;

            void Receive(ref SimpleUntargetedMessage message)
            {
                ++count;
            }
        }

        [Test]
        public void Priority()
        {
            GameObject test = new(nameof(Priority) + "1", typeof(EmptyMessageAwareComponent));
            _spawned.Add(test);

            int[] received = new int[100];
            EmptyMessageAwareComponent component = test.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            for (int i = 0; i < received.Length; ++i)
            {
                int priority = i;
                token.RegisterUntargeted(
                    (ref SimpleUntargetedMessage _) =>
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
                token.RegisterUntargetedPostProcessor(
                    (ref SimpleUntargetedMessage _) =>
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

            SimpleUntargetedMessage message = new();
            const int numRuns = 100;
            for (int i = 0; i < numRuns; ++i)
            {
                // Should do something
                message.EmitUntargeted();
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
                token.RegisterUntargetedInterceptor(
                    (ref SimpleUntargetedMessage _) =>
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

            SimpleUntargetedMessage message = new();
            const int numRuns = 100;
            for (int i = 0; i < numRuns; ++i)
            {
                message.EmitUntargeted();
            }

            Assert.AreEqual(
                1,
                received.Distinct().Count(),
                "Expected received to be uniform, found: [{0}].",
                string.Join(",", received.Distinct().OrderBy(x => x))
            );

            Assert.AreEqual(numRuns, received.Distinct().Single());
        }
    }
}

#endif
