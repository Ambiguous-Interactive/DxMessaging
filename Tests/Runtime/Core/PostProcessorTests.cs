#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using NUnit.Framework;
    using Scripts.Components;
    using Scripts.Messages;
    using UnityEngine;

    [Category("Stress")]
    public sealed class PostProcessorTests : MessagingTestBase
    {
        [SetUp]
        public override void Setup()
        {
            base.Setup();
            // Run(...) helpers below loop _numRegistrations times across many
            // tests; restore the legacy stress fan-out so coverage matches the
            // pre-Phase-A baseline.
            _numRegistrations = StressRegistrations;
        }

        [Test]
        public void Untargeted()
        {
            GameObject test = new(nameof(Untargeted), typeof(EmptyMessageAwareComponent));
            _spawned.Add(test);

            EmptyMessageAwareComponent component = test.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);

            int lastSeenCount = 0;
            int count = 0;

            int finalCount = 0;

            Action assertion;

            count = 1;
            assertion = () =>
            {
                Assert.AreEqual(lastSeenCount + 1, count);
            };

            SimpleUntargetedMessage message = new();
            Run(
                () =>
                    new[]
                    {
                        token.RegisterUntargetedPostProcessor<SimpleUntargetedMessage>(
                            PostProcessor
                        ),
                    },
                () => message.EmitUntargeted(),
                () =>
                {
                    lastSeenCount = count++;
                    finalCount = count;
                },
                () =>
                {
                    Assert.AreEqual(finalCount - 1, lastSeenCount);
                    Assert.AreEqual(finalCount, count);
                },
                token
            );

            ResetCount();
            assertion = () =>
            {
                Assert.AreEqual(lastSeenCount + 1, count);
                lastSeenCount = count;
            };
            Run(
                () =>
                    new[]
                    {
                        token.RegisterUntargetedPostProcessor<SimpleUntargetedMessage>(
                            PostProcessor
                        ),
                        token.RegisterUntargeted<SimpleUntargetedMessage>(_ => ++count),
                    },
                () => message.EmitUntargeted(),
                () =>
                {
                    Assert.AreEqual(lastSeenCount, count);
                    finalCount = count;
                },
                () =>
                {
                    Assert.AreEqual(finalCount, lastSeenCount);
                    Assert.AreEqual(finalCount, count);
                },
                token,
                synchronizeDeregistrations: true
            );

            ResetCount();
            assertion = () =>
            {
                ++count;
            };
            Run(
                () =>
                    new[]
                    {
                        token.RegisterUntargetedPostProcessor<SimpleUntargetedMessage>(
                            PostProcessor
                        ),
                        token.RegisterUntargetedPostProcessor<SimpleUntargetedMessage>(
                            PostProcessor
                        ),
                    },
                () => message.EmitUntargeted(),
                () => Assert.AreEqual(++lastSeenCount, count),
                () =>
                {
                    Assert.AreEqual(lastSeenCount, count);
                },
                token,
                synchronizeDeregistrations: true
            );

            return;

            void ResetCount()
            {
                lastSeenCount = 0;
                count = 0;
                finalCount = 0;
            }

            void PostProcessor(ref SimpleUntargetedMessage message)
            {
                assertion.Invoke();
            }
        }

        [Test]
        public void GameObjectTargeted()
        {
            GameObject test = new(nameof(GameObjectTargeted), typeof(EmptyMessageAwareComponent));
            _spawned.Add(test);

            EmptyMessageAwareComponent component = test.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);

            int lastSeenCount = 0;
            int count = 0;

            int finalCount = 0;

            Action assertion;

            count = 1;
            assertion = () =>
            {
                Assert.AreEqual(lastSeenCount + 1, count);
            };

            SimpleTargetedMessage message = new();
            Run(
                () =>
                    new[]
                    {
                        token.RegisterGameObjectTargetedPostProcessor<SimpleTargetedMessage>(
                            test,
                            PostProcessor
                        ),
                    },
                () => message.EmitGameObjectTargeted(test),
                () =>
                {
                    lastSeenCount = count++;
                    finalCount = count;
                },
                () =>
                {
                    Assert.AreEqual(finalCount - 1, lastSeenCount);
                    Assert.AreEqual(finalCount, count);
                },
                token
            );

            ResetCount();
            assertion = () =>
            {
                Assert.AreEqual(lastSeenCount + 1, count);
                lastSeenCount = count;
            };
            Run(
                () =>
                    new[]
                    {
                        token.RegisterGameObjectTargetedPostProcessor<SimpleTargetedMessage>(
                            test,
                            PostProcessor
                        ),
                        token.RegisterGameObjectTargeted<SimpleTargetedMessage>(test, _ => ++count),
                    },
                () => message.EmitGameObjectTargeted(test),
                () =>
                {
                    Assert.AreEqual(lastSeenCount, count);
                    finalCount = count;
                },
                () =>
                {
                    Assert.AreEqual(finalCount, lastSeenCount);
                    Assert.AreEqual(finalCount, count);
                },
                token,
                synchronizeDeregistrations: true
            );

            ResetCount();
            assertion = () =>
            {
                ++count;
            };
            Run(
                () =>
                    new[]
                    {
                        token.RegisterGameObjectTargetedPostProcessor<SimpleTargetedMessage>(
                            test,
                            PostProcessor
                        ),
                        token.RegisterGameObjectTargetedPostProcessor<SimpleTargetedMessage>(
                            test,
                            PostProcessor
                        ),
                    },
                () => message.EmitGameObjectTargeted(test),
                () => Assert.AreEqual(++lastSeenCount, count),
                () =>
                {
                    Assert.AreEqual(lastSeenCount, count);
                },
                token,
                synchronizeDeregistrations: true
            );

            ResetCount();
            assertion = () =>
            {
                Assert.Fail("Should never be called, we're emitting the wrong thing");
            };
            Run(
                () =>
                    new[]
                    {
                        token.RegisterGameObjectTargetedPostProcessor<SimpleTargetedMessage>(
                            test,
                            PostProcessor
                        ),
                    },
                () => message.EmitComponentTargeted(component),
                () =>
                {
                    Assert.AreEqual(0, count);
                    Assert.AreEqual(0, lastSeenCount);
                },
                () =>
                {
                    Assert.AreEqual(0, count);
                    Assert.AreEqual(0, lastSeenCount);
                },
                token
            );

            return;

            void ResetCount()
            {
                lastSeenCount = 0;
                count = 0;
                finalCount = 0;
            }

            void PostProcessor(ref SimpleTargetedMessage message)
            {
                assertion.Invoke();
            }
        }

        [Test]
        public void GameObjectTargetedWithoutTargeting()
        {
            GameObject test = new(
                nameof(GameObjectTargetedWithoutTargeting),
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test);

            EmptyMessageAwareComponent component = test.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);

            int lastSeenCount = 0;
            int count = 0;

            int finalCount = 0;

            Action assertion;

            count = 1;
            assertion = () =>
            {
                Assert.AreEqual(lastSeenCount + 1, count);
            };

            SimpleTargetedMessage message = new();
            Run(
                () =>
                    new[]
                    {
                        token.RegisterTargetedWithoutTargeting<SimpleTargetedMessage>(
                            PostProcessor
                        ),
                    },
                () => message.EmitGameObjectTargeted(test),
                () =>
                {
                    lastSeenCount = count++;
                    finalCount = count;
                },
                () =>
                {
                    Assert.AreEqual(finalCount - 1, lastSeenCount);
                    Assert.AreEqual(finalCount, count);
                },
                token
            );

            ResetCount();
            assertion = () =>
            {
                Assert.AreEqual(lastSeenCount + 1, count);
                lastSeenCount = count;
            };
            Run(
                () =>
                    new[]
                    {
                        token.RegisterTargetedWithoutTargeting<SimpleTargetedMessage>(
                            PostProcessor
                        ),
                        token.RegisterGameObjectTargeted<SimpleTargetedMessage>(test, _ => ++count),
                    },
                () => message.EmitGameObjectTargeted(test),
                () =>
                {
                    Assert.AreEqual(lastSeenCount, count);
                    finalCount = count;
                },
                () =>
                {
                    Assert.AreEqual(finalCount, lastSeenCount);
                    Assert.AreEqual(finalCount, count);
                },
                token,
                synchronizeDeregistrations: true
            );

            ResetCount();
            assertion = () =>
            {
                ++count;
            };
            Run(
                () =>
                    new[]
                    {
                        token.RegisterTargetedWithoutTargeting<SimpleTargetedMessage>(
                            PostProcessor
                        ),
                        token.RegisterTargetedWithoutTargeting<SimpleTargetedMessage>(
                            PostProcessor
                        ),
                    },
                () => message.EmitGameObjectTargeted(test),
                () => Assert.AreEqual(++lastSeenCount, count),
                () =>
                {
                    Assert.AreEqual(lastSeenCount, count);
                },
                token,
                synchronizeDeregistrations: true
            );

            return;

            void ResetCount()
            {
                lastSeenCount = 0;
                count = 0;
                finalCount = 0;
            }

            void PostProcessor(ref InstanceId target, ref SimpleTargetedMessage message)
            {
                assertion.Invoke();
            }
        }

        [Test]
        public void ComponentTargeted()
        {
            GameObject test = new(nameof(ComponentTargeted), typeof(EmptyMessageAwareComponent));
            _spawned.Add(test);

            EmptyMessageAwareComponent component = test.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);

            int lastSeenCount = 0;
            int count = 0;

            int finalCount = 0;

            Action assertion;

            count = 1;
            assertion = () =>
            {
                Assert.AreEqual(lastSeenCount + 1, count);
            };

            SimpleTargetedMessage message = new();
            Run(
                () =>
                    new[]
                    {
                        token.RegisterComponentTargetedPostProcessor<SimpleTargetedMessage>(
                            component,
                            PostProcessor
                        ),
                    },
                () => message.EmitComponentTargeted(component),
                () =>
                {
                    lastSeenCount = count++;
                    finalCount = count;
                },
                () =>
                {
                    Assert.AreEqual(finalCount - 1, lastSeenCount);
                    Assert.AreEqual(finalCount, count);
                },
                token
            );

            ResetCount();
            assertion = () =>
            {
                Assert.AreEqual(lastSeenCount + 1, count);
                lastSeenCount = count;
            };
            Run(
                () =>
                    new[]
                    {
                        token.RegisterComponentTargetedPostProcessor<SimpleTargetedMessage>(
                            component,
                            PostProcessor
                        ),
                        token.RegisterComponentTargeted<SimpleTargetedMessage>(
                            component,
                            _ => ++count
                        ),
                    },
                () => message.EmitComponentTargeted(component),
                () =>
                {
                    Assert.AreEqual(lastSeenCount, count);
                    finalCount = count;
                },
                () =>
                {
                    Assert.AreEqual(finalCount, lastSeenCount);
                    Assert.AreEqual(finalCount, count);
                },
                token,
                synchronizeDeregistrations: true
            );

            ResetCount();
            assertion = () =>
            {
                ++count;
            };
            Run(
                () =>
                    new[]
                    {
                        token.RegisterComponentTargetedPostProcessor<SimpleTargetedMessage>(
                            component,
                            PostProcessor
                        ),
                        token.RegisterComponentTargetedPostProcessor<SimpleTargetedMessage>(
                            component,
                            PostProcessor
                        ),
                    },
                () => message.EmitComponentTargeted(component),
                () => Assert.AreEqual(++lastSeenCount, count),
                () =>
                {
                    Assert.AreEqual(lastSeenCount, count);
                },
                token,
                synchronizeDeregistrations: true
            );

            ResetCount();
            assertion = () =>
            {
                Assert.Fail("Should never be called, we're emitting the wrong thing");
            };
            Run(
                () =>
                    new[]
                    {
                        token.RegisterComponentTargetedPostProcessor<SimpleTargetedMessage>(
                            component,
                            PostProcessor
                        ),
                    },
                () => message.EmitGameObjectTargeted(test),
                () =>
                {
                    Assert.AreEqual(0, count);
                    Assert.AreEqual(0, lastSeenCount);
                },
                () =>
                {
                    Assert.AreEqual(0, count);
                    Assert.AreEqual(0, lastSeenCount);
                },
                token
            );

            return;

            void ResetCount()
            {
                lastSeenCount = 0;
                count = 0;
                finalCount = 0;
            }

            void PostProcessor(ref SimpleTargetedMessage message)
            {
                assertion.Invoke();
            }
        }

        [Test]
        public void ComponentTargetedWithoutTargeting()
        {
            GameObject test = new(
                nameof(ComponentTargetedWithoutTargeting),
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test);

            EmptyMessageAwareComponent component = test.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);

            int lastSeenCount = 0;
            int count = 0;

            int finalCount = 0;

            Action assertion;

            count = 1;
            assertion = () =>
            {
                Assert.AreEqual(lastSeenCount + 1, count);
            };

            SimpleTargetedMessage message = new();
            Run(
                () =>
                    new[]
                    {
                        token.RegisterTargetedWithoutTargetingPostProcessor<SimpleTargetedMessage>(
                            PostProcessor
                        ),
                    },
                () => message.EmitComponentTargeted(component),
                () =>
                {
                    lastSeenCount = count++;
                    finalCount = count;
                },
                () =>
                {
                    Assert.AreEqual(finalCount - 1, lastSeenCount);
                    Assert.AreEqual(finalCount, count);
                },
                token
            );

            ResetCount();
            assertion = () =>
            {
                Assert.AreEqual(lastSeenCount + 1, count);
                lastSeenCount = count;
            };
            Run(
                () =>
                    new[]
                    {
                        token.RegisterTargetedWithoutTargetingPostProcessor<SimpleTargetedMessage>(
                            PostProcessor
                        ),
                        token.RegisterComponentTargeted<SimpleTargetedMessage>(
                            component,
                            _ => ++count
                        ),
                    },
                () => message.EmitComponentTargeted(component),
                () =>
                {
                    Assert.AreEqual(lastSeenCount, count);
                    finalCount = count;
                },
                () =>
                {
                    Assert.AreEqual(finalCount, lastSeenCount);
                    Assert.AreEqual(finalCount, count);
                },
                token,
                synchronizeDeregistrations: true
            );

            ResetCount();
            assertion = () =>
            {
                ++count;
            };
            Run(
                () =>
                    new[]
                    {
                        token.RegisterTargetedWithoutTargetingPostProcessor<SimpleTargetedMessage>(
                            PostProcessor
                        ),
                        token.RegisterTargetedWithoutTargetingPostProcessor<SimpleTargetedMessage>(
                            PostProcessor
                        ),
                    },
                () => message.EmitComponentTargeted(component),
                () => Assert.AreEqual(++lastSeenCount, count),
                () =>
                {
                    Assert.AreEqual(lastSeenCount, count);
                },
                token,
                synchronizeDeregistrations: true
            );

            ResetCount();

            return;

            void ResetCount()
            {
                lastSeenCount = 0;
                count = 0;
                finalCount = 0;
            }

            void PostProcessor(ref InstanceId target, ref SimpleTargetedMessage message)
            {
                assertion.Invoke();
            }
        }

        [Test]
        public void GameObjectBroadcast()
        {
            GameObject test = new(nameof(GameObjectBroadcast), typeof(EmptyMessageAwareComponent));
            _spawned.Add(test);

            EmptyMessageAwareComponent component = test.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);

            int lastSeenCount = 0;
            int count = 0;

            int finalCount = 0;

            Action assertion;

            count = 1;
            assertion = () =>
            {
                Assert.AreEqual(lastSeenCount + 1, count);
            };

            SimpleBroadcastMessage message = new();
            Run(
                () =>
                    new[]
                    {
                        token.RegisterGameObjectBroadcastPostProcessor<SimpleBroadcastMessage>(
                            test,
                            PostProcessor
                        ),
                    },
                () => message.EmitGameObjectBroadcast(test),
                () =>
                {
                    lastSeenCount = count++;
                    finalCount = count;
                },
                () =>
                {
                    Assert.AreEqual(finalCount - 1, lastSeenCount);
                    Assert.AreEqual(finalCount, count);
                },
                token
            );

            ResetCount();
            assertion = () =>
            {
                Assert.AreEqual(lastSeenCount + 1, count, "GameObjectBroadcast was not handled!");
                lastSeenCount = count;
            };
            Run(
                () =>
                    new[]
                    {
                        token.RegisterGameObjectBroadcastPostProcessor<SimpleBroadcastMessage>(
                            test,
                            PostProcessor
                        ),
                        token.RegisterGameObjectBroadcast<SimpleBroadcastMessage>(
                            test,
                            _ => ++count
                        ),
                    },
                () => message.EmitGameObjectBroadcast(test),
                () =>
                {
                    Assert.AreEqual(
                        lastSeenCount,
                        count,
                        "GameObjectBroadcast PostProcessor was not run!"
                    );
                    finalCount = count;
                },
                () =>
                {
                    Assert.AreEqual(finalCount, lastSeenCount);
                    Assert.AreEqual(finalCount, count);
                },
                token,
                synchronizeDeregistrations: true
            );

            ResetCount();
            assertion = () =>
            {
                ++count;
            };
            Run(
                () =>
                    new[]
                    {
                        token.RegisterGameObjectBroadcastPostProcessor<SimpleBroadcastMessage>(
                            test,
                            PostProcessor
                        ),
                        token.RegisterGameObjectBroadcastPostProcessor<SimpleBroadcastMessage>(
                            test,
                            PostProcessor
                        ),
                    },
                () => message.EmitGameObjectBroadcast(test),
                () =>
                    Assert.AreEqual(
                        ++lastSeenCount,
                        count,
                        "GameObjectBroadcast PostProcessor was ran twice!"
                    ),
                () =>
                {
                    Assert.AreEqual(
                        lastSeenCount,
                        count,
                        "GameObjectPostProcessor ran without any receivers subscribed!"
                    );
                },
                token,
                synchronizeDeregistrations: true
            );

            ResetCount();
            assertion = () =>
            {
                Assert.Fail("Should never be called, we're emitting the wrong thing");
            };
            Run(
                () =>
                    new[]
                    {
                        token.RegisterGameObjectBroadcastPostProcessor<SimpleBroadcastMessage>(
                            test,
                            PostProcessor
                        ),
                    },
                () => message.EmitComponentBroadcast(component),
                () =>
                {
                    Assert.AreEqual(0, count);
                    Assert.AreEqual(0, lastSeenCount);
                },
                () =>
                {
                    Assert.AreEqual(0, count);
                    Assert.AreEqual(0, lastSeenCount);
                },
                token
            );

            return;

            void ResetCount()
            {
                lastSeenCount = 0;
                count = 0;
                finalCount = 0;
            }

            void PostProcessor(ref SimpleBroadcastMessage message)
            {
                assertion.Invoke();
            }
        }

        [Test]
        public void GameObjectBroadcastWithoutSource()
        {
            GameObject test = new(
                nameof(GameObjectBroadcastWithoutSource),
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test);

            EmptyMessageAwareComponent component = test.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);

            int lastSeenCount = 0;
            int count = 0;

            int finalCount = 0;

            Action assertion;

            count = 1;
            assertion = () =>
            {
                Assert.AreEqual(lastSeenCount + 1, count);
            };

            SimpleBroadcastMessage message = new();
            Run(
                () =>
                    new[]
                    {
                        token.RegisterBroadcastWithoutSourcePostProcessor<SimpleBroadcastMessage>(
                            PostProcessor
                        ),
                    },
                () => message.EmitGameObjectBroadcast(test),
                () =>
                {
                    lastSeenCount = count++;
                    finalCount = count;
                },
                () =>
                {
                    Assert.AreEqual(finalCount - 1, lastSeenCount);
                    Assert.AreEqual(finalCount, count);
                },
                token
            );

            ResetCount();
            assertion = () =>
            {
                Assert.AreEqual(lastSeenCount + 1, count, "GameObjectBroadcast was not handled!");
                lastSeenCount = count;
            };
            Run(
                () =>
                    new[]
                    {
                        token.RegisterBroadcastWithoutSourcePostProcessor<SimpleBroadcastMessage>(
                            PostProcessor
                        ),
                        token.RegisterGameObjectBroadcast<SimpleBroadcastMessage>(
                            test,
                            _ => ++count
                        ),
                    },
                () => message.EmitGameObjectBroadcast(test),
                () =>
                {
                    Assert.AreEqual(
                        lastSeenCount,
                        count,
                        "GameObjectBroadcast PostProcessor was not run!"
                    );
                    finalCount = count;
                },
                () =>
                {
                    Assert.AreEqual(finalCount, lastSeenCount);
                    Assert.AreEqual(finalCount, count);
                },
                token,
                synchronizeDeregistrations: true
            );

            ResetCount();
            assertion = () =>
            {
                ++count;
            };
            Run(
                () =>
                    new[]
                    {
                        token.RegisterBroadcastWithoutSourcePostProcessor<SimpleBroadcastMessage>(
                            PostProcessor
                        ),
                        token.RegisterBroadcastWithoutSourcePostProcessor<SimpleBroadcastMessage>(
                            PostProcessor
                        ),
                    },
                () => message.EmitGameObjectBroadcast(test),
                () =>
                    Assert.AreEqual(
                        ++lastSeenCount,
                        count,
                        "GameObjectBroadcast PostProcessor was ran twice!"
                    ),
                () =>
                {
                    Assert.AreEqual(
                        lastSeenCount,
                        count,
                        "GameObjectPostProcessor ran without any receivers subscribed!"
                    );
                },
                token,
                synchronizeDeregistrations: true
            );

            return;

            void ResetCount()
            {
                lastSeenCount = 0;
                count = 0;
                finalCount = 0;
            }

            void PostProcessor(ref InstanceId target, ref SimpleBroadcastMessage message)
            {
                assertion.Invoke();
            }
        }

        [Test]
        public void ComponentBroadcast()
        {
            GameObject test = new(nameof(ComponentBroadcast), typeof(EmptyMessageAwareComponent));
            _spawned.Add(test);

            EmptyMessageAwareComponent component = test.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);

            int lastSeenCount = 0;
            int count = 0;

            int finalCount = 0;

            Action assertion;

            count = 1;
            assertion = () =>
            {
                Assert.AreEqual(lastSeenCount + 1, count);
            };

            SimpleBroadcastMessage message = new();
            Run(
                () =>
                    new[]
                    {
                        token.RegisterComponentBroadcastPostProcessor<SimpleBroadcastMessage>(
                            component,
                            PostProcessor
                        ),
                    },
                () => message.EmitComponentBroadcast(component),
                () =>
                {
                    lastSeenCount = count++;
                    finalCount = count;
                },
                () =>
                {
                    Assert.AreEqual(finalCount - 1, lastSeenCount);
                    Assert.AreEqual(finalCount, count);
                },
                token
            );

            ResetCount();
            assertion = () =>
            {
                Assert.AreEqual(lastSeenCount + 1, count);
                lastSeenCount = count;
            };
            Run(
                () =>
                    new[]
                    {
                        token.RegisterComponentBroadcastPostProcessor<SimpleBroadcastMessage>(
                            component,
                            PostProcessor
                        ),
                        token.RegisterComponentBroadcast<SimpleBroadcastMessage>(
                            component,
                            _ => ++count
                        ),
                    },
                () => message.EmitComponentBroadcast(component),
                () =>
                {
                    Assert.AreEqual(lastSeenCount, count);
                    finalCount = count;
                },
                () =>
                {
                    Assert.AreEqual(finalCount, lastSeenCount);
                    Assert.AreEqual(finalCount, count);
                },
                token,
                synchronizeDeregistrations: true
            );

            ResetCount();
            assertion = () =>
            {
                ++count;
            };
            Run(
                () =>
                    new[]
                    {
                        token.RegisterComponentBroadcastPostProcessor<SimpleBroadcastMessage>(
                            component,
                            PostProcessor
                        ),
                        token.RegisterComponentBroadcastPostProcessor<SimpleBroadcastMessage>(
                            component,
                            PostProcessor
                        ),
                    },
                () => message.EmitComponentBroadcast(component),
                () => Assert.AreEqual(++lastSeenCount, count),
                () =>
                {
                    Assert.AreEqual(lastSeenCount, count);
                },
                token,
                synchronizeDeregistrations: true
            );

            ResetCount();
            assertion = () =>
            {
                Assert.Fail("Should never be called, we're emitting the wrong thing");
            };
            Run(
                () =>
                    new[]
                    {
                        token.RegisterComponentBroadcastPostProcessor<SimpleBroadcastMessage>(
                            component,
                            PostProcessor
                        ),
                    },
                () => message.EmitGameObjectBroadcast(test),
                () =>
                {
                    Assert.AreEqual(0, count);
                    Assert.AreEqual(0, lastSeenCount);
                },
                () =>
                {
                    Assert.AreEqual(0, count);
                    Assert.AreEqual(0, lastSeenCount);
                },
                token
            );

            return;

            void ResetCount()
            {
                lastSeenCount = 0;
                count = 0;
                finalCount = 0;
            }

            void PostProcessor(ref SimpleBroadcastMessage message)
            {
                assertion.Invoke();
            }
        }

        [Test]
        public void ComponentBroadcastWithoutSource()
        {
            GameObject test = new(
                nameof(ComponentBroadcastWithoutSource),
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test);

            EmptyMessageAwareComponent component = test.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);

            int lastSeenCount = 0;
            int count = 0;

            int finalCount = 0;

            Action assertion;

            count = 1;
            assertion = () =>
            {
                Assert.AreEqual(lastSeenCount + 1, count);
            };

            SimpleBroadcastMessage message = new();
            Run(
                () =>
                    new[]
                    {
                        token.RegisterBroadcastWithoutSourcePostProcessor<SimpleBroadcastMessage>(
                            PostProcessor
                        ),
                    },
                () => message.EmitComponentBroadcast(component),
                () =>
                {
                    lastSeenCount = count++;
                    finalCount = count;
                },
                () =>
                {
                    Assert.AreEqual(finalCount - 1, lastSeenCount);
                    Assert.AreEqual(finalCount, count);
                },
                token
            );

            ResetCount();
            assertion = () =>
            {
                Assert.AreEqual(lastSeenCount + 1, count);
                lastSeenCount = count;
            };
            Run(
                () =>
                    new[]
                    {
                        token.RegisterBroadcastWithoutSourcePostProcessor<SimpleBroadcastMessage>(
                            PostProcessor
                        ),
                        token.RegisterComponentBroadcast<SimpleBroadcastMessage>(
                            component,
                            _ => ++count
                        ),
                    },
                () => message.EmitComponentBroadcast(component),
                () =>
                {
                    Assert.AreEqual(lastSeenCount, count);
                    finalCount = count;
                },
                () =>
                {
                    Assert.AreEqual(finalCount, lastSeenCount);
                    Assert.AreEqual(finalCount, count);
                },
                token,
                synchronizeDeregistrations: true
            );

            ResetCount();
            assertion = () =>
            {
                ++count;
            };
            Run(
                () =>
                    new[]
                    {
                        token.RegisterBroadcastWithoutSourcePostProcessor<SimpleBroadcastMessage>(
                            PostProcessor
                        ),
                        token.RegisterBroadcastWithoutSourcePostProcessor<SimpleBroadcastMessage>(
                            PostProcessor
                        ),
                    },
                () => message.EmitComponentBroadcast(component),
                () => Assert.AreEqual(++lastSeenCount, count),
                () =>
                {
                    Assert.AreEqual(lastSeenCount, count);
                },
                token,
                synchronizeDeregistrations: true
            );

            return;

            void ResetCount()
            {
                lastSeenCount = 0;
                count = 0;
                finalCount = 0;
            }

            void PostProcessor(ref InstanceId source, ref SimpleBroadcastMessage message)
            {
                assertion.Invoke();
            }
        }
    }
}

#endif
