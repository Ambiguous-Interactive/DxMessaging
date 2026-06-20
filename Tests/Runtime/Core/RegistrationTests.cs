#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;
    using Scripts.Components;
    using Scripts.Messages;
    using UnityEngine;
    using UnityEngine.TestTools;

    [Category("Stress")]
    public sealed class RegistrationTests : MessagingTestBase
    {
        private GameObject _test;
        private EmptyMessageAwareComponent _component;
        private MessageRegistrationToken _token;

        [SetUp]
        public override void Setup()
        {
            base.Setup();
            // RunRegistrationTest validates ordering and dispatch correctness
            // across every registered handler, so it relies on the legacy
            // stress-scale count rather than the smoke-check default.
            _numRegistrations = StressRegistrations;
        }

        [UnitySetUp]
        public override IEnumerator UnitySetup()
        {
            IEnumerator baseEnumerator = base.UnitySetup();
            while (baseEnumerator.MoveNext())
            {
                yield return baseEnumerator.Current;
            }

            _test = new(nameof(RegistrationTests), typeof(EmptyMessageAwareComponent));
            _spawned.Add(_test);
            _component = _test.GetComponent<EmptyMessageAwareComponent>();
            _token = GetToken(_component);
        }

        [Test]
        public void UntargetedNormal()
        {
            int count = 0;

            SimpleUntargetedMessage message = new();
            RunRegistrationTest(
                token => token.RegisterUntargeted<SimpleUntargetedMessage>(Handle),
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitUntargeted();
                },
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitUntargeted();
                    Assert.AreEqual(i + 1, count);
                },
                () =>
                {
                    message.EmitUntargeted();
                    Assert.AreEqual(0, count);
                },
                () =>
                {
                    count = 0;
                }
            );
            return;

            void Handle(SimpleUntargetedMessage message)
            {
                ++count;
            }
        }

        [Test]
        public void UntargetedNoCopy()
        {
            int count = 0;

            SimpleUntargetedMessage message = new();
            RunRegistrationTest(
                token => token.RegisterUntargeted<SimpleUntargetedMessage>(Handle),
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitUntargeted();
                },
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitUntargeted();
                    Assert.AreEqual(i + 1, count);
                },
                () =>
                {
                    message.EmitUntargeted();
                    Assert.AreEqual(0, count);
                },
                () =>
                {
                    count = 0;
                }
            );
            return;

            void Handle(ref SimpleUntargetedMessage message)
            {
                ++count;
            }
        }

        [Test]
        public void UntargetedPostProcessor()
        {
            int count = 0;

            SimpleUntargetedMessage message = new();
            RunRegistrationTest(
                token => token.RegisterUntargetedPostProcessor<SimpleUntargetedMessage>(Handle),
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitUntargeted();
                },
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitUntargeted();
                    Assert.AreEqual(i + 1, count);
                },
                () =>
                {
                    message.EmitUntargeted();
                    Assert.AreEqual(0, count);
                },
                () =>
                {
                    count = 0;
                }
            );
            return;

            void Handle(ref SimpleUntargetedMessage message)
            {
                ++count;
            }
        }

        [Test]
        public void UntargetedInterceptor()
        {
            int count = 0;

            SimpleUntargetedMessage message = new();
            RunRegistrationTest(
                token => token.RegisterUntargetedInterceptor<SimpleUntargetedMessage>(Intercept),
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitUntargeted();
                },
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitUntargeted();
                    Assert.AreEqual(i + 1, count);
                },
                () =>
                {
                    message.EmitUntargeted();
                    Assert.AreEqual(0, count);
                },
                () =>
                {
                    count = 0;
                }
            );
            return;

            bool Intercept(ref SimpleUntargetedMessage message)
            {
                ++count;
                return true;
            }
        }

        [Test]
        public void GameObjectTargetedNormal()
        {
            int count = 0;

            SimpleTargetedMessage message = new();
            RunRegistrationTest(
                token => token.RegisterGameObjectTargeted<SimpleTargetedMessage>(_test, Handle),
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitGameObjectTargeted(_test);
                },
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitGameObjectTargeted(_test);
                    Assert.AreEqual(i + 1, count);
                },
                () =>
                {
                    message.EmitGameObjectTargeted(_test);
                    message.EmitComponentTargeted(_component);
                    Assert.AreEqual(0, count);
                },
                () =>
                {
                    count = 0;
                }
            );
            return;

            void Handle(SimpleTargetedMessage message)
            {
                ++count;
            }
        }

        [Test]
        public void GameObjectTargetedNoCopy()
        {
            int count = 0;

            SimpleTargetedMessage message = new();
            RunRegistrationTest(
                token => token.RegisterGameObjectTargeted<SimpleTargetedMessage>(_test, Handle),
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitGameObjectTargeted(_test);
                },
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitGameObjectTargeted(_test);
                    Assert.AreEqual(i + 1, count);
                },
                () =>
                {
                    message.EmitGameObjectTargeted(_test);
                    message.EmitComponentTargeted(_component);
                    Assert.AreEqual(0, count);
                },
                () =>
                {
                    count = 0;
                }
            );
            return;

            void Handle(ref SimpleTargetedMessage message)
            {
                ++count;
            }
        }

        [Test]
        public void GameObjectTargetedPostProcessor()
        {
            int count = 0;

            SimpleTargetedMessage message = new();
            RunRegistrationTest(
                token =>
                    token.RegisterGameObjectTargetedPostProcessor<SimpleTargetedMessage>(
                        _test,
                        Handle
                    ),
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitGameObjectTargeted(_test);
                },
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitGameObjectTargeted(_test);
                    Assert.AreEqual(i + 1, count);
                },
                () =>
                {
                    message.EmitGameObjectTargeted(_test);
                    message.EmitComponentTargeted(_component);
                    Assert.AreEqual(0, count);
                },
                () =>
                {
                    count = 0;
                }
            );
            return;

            void Handle(ref SimpleTargetedMessage message)
            {
                ++count;
            }
        }

        [Test]
        public void ComponentTargetedNormal()
        {
            int count = 0;

            SimpleTargetedMessage message = new();
            RunRegistrationTest(
                token => token.RegisterComponentTargeted<SimpleTargetedMessage>(_component, Handle),
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitComponentTargeted(_component);
                },
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitComponentTargeted(_component);
                    Assert.AreEqual(i + 1, count);
                },
                () =>
                {
                    message.EmitGameObjectTargeted(_test);
                    message.EmitComponentTargeted(_component);
                    Assert.AreEqual(0, count);
                },
                () =>
                {
                    count = 0;
                }
            );
            return;

            void Handle(SimpleTargetedMessage message)
            {
                ++count;
            }
        }

        [Test]
        public void ComponentTargetedNoCopy()
        {
            int count = 0;

            SimpleTargetedMessage message = new();
            RunRegistrationTest(
                token => token.RegisterComponentTargeted<SimpleTargetedMessage>(_component, Handle),
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitComponentTargeted(_component);
                },
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitComponentTargeted(_component);
                    Assert.AreEqual(i + 1, count);
                },
                () =>
                {
                    message.EmitGameObjectTargeted(_test);
                    message.EmitComponentTargeted(_component);
                    Assert.AreEqual(0, count);
                },
                () =>
                {
                    count = 0;
                }
            );
            return;

            void Handle(ref SimpleTargetedMessage message)
            {
                ++count;
            }
        }

        [Test]
        public void ComponentTargetedPostProcessor()
        {
            int count = 0;

            SimpleTargetedMessage message = new();
            RunRegistrationTest(
                token =>
                    token.RegisterComponentTargetedPostProcessor<SimpleTargetedMessage>(
                        _component,
                        Handle
                    ),
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitComponentTargeted(_component);
                },
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitComponentTargeted(_component);
                    Assert.AreEqual(i + 1, count);
                },
                () =>
                {
                    message.EmitGameObjectTargeted(_test);
                    message.EmitComponentTargeted(_component);
                    Assert.AreEqual(0, count);
                },
                () =>
                {
                    count = 0;
                }
            );
            return;

            void Handle(ref SimpleTargetedMessage message)
            {
                ++count;
            }
        }

        [Test]
        public void TargetedInterceptor()
        {
            int count = 0;

            SimpleTargetedMessage message = new();
            RunRegistrationTest(
                token => token.RegisterTargetedInterceptor<SimpleTargetedMessage>(Intercept),
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitGameObjectTargeted(_test);
                },
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitComponentTargeted(_component);
                    Assert.AreEqual(i + 1, count);
                },
                () =>
                {
                    message.EmitGameObjectTargeted(_test);
                    message.EmitComponentTargeted(_component);
                    Assert.AreEqual(0, count);
                },
                () =>
                {
                    count = 0;
                }
            );
            return;

            bool Intercept(ref InstanceId target, ref SimpleTargetedMessage message)
            {
                ++count;
                return true;
            }
        }

        [Test]
        public void TargetedWithoutTargetingNormal()
        {
            int count = 0;

            SimpleTargetedMessage message = new();
            RunRegistrationTest(
                token => token.RegisterTargetedWithoutTargeting<SimpleTargetedMessage>(Handle),
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitGameObjectTargeted(_test);
                },
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitComponentTargeted(_component);
                    Assert.AreEqual(i + 1, count);
                },
                () =>
                {
                    message.EmitGameObjectTargeted(_test);
                    message.EmitComponentTargeted(_component);
                    Assert.AreEqual(0, count);
                },
                () =>
                {
                    count = 0;
                }
            );
            return;

            void Handle(InstanceId target, SimpleTargetedMessage message)
            {
                ++count;
            }
        }

        [Test]
        public void TargetedWithoutTargetingNoCopy()
        {
            int count = 0;

            SimpleTargetedMessage message = new();
            RunRegistrationTest(
                token => token.RegisterTargetedWithoutTargeting<SimpleTargetedMessage>(Handle),
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitGameObjectTargeted(_test);
                },
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitComponentTargeted(_component);
                    Assert.AreEqual(i + 1, count);
                },
                () =>
                {
                    message.EmitGameObjectTargeted(_test);
                    message.EmitComponentTargeted(_component);
                    Assert.AreEqual(0, count);
                },
                () =>
                {
                    count = 0;
                }
            );
            return;

            void Handle(ref InstanceId target, ref SimpleTargetedMessage message)
            {
                ++count;
            }
        }

        [Test]
        public void TargetedWithoutTargetingPostProcessorNormal()
        {
            int count = 0;

            SimpleTargetedMessage message = new();
            RunRegistrationTest(
                token =>
                    token.RegisterTargetedWithoutTargetingPostProcessor<SimpleTargetedMessage>(
                        Handle
                    ),
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitGameObjectTargeted(_test);
                },
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitComponentTargeted(_component);
                    Assert.AreEqual(i + 1, count);
                },
                () =>
                {
                    message.EmitGameObjectTargeted(_test);
                    message.EmitComponentTargeted(_component);
                    Assert.AreEqual(0, count);
                },
                () =>
                {
                    count = 0;
                }
            );
            return;

            void Handle(InstanceId target, SimpleTargetedMessage message)
            {
                ++count;
            }
        }

        [Test]
        public void TargetedWithoutTargetingPostProcessorNoCopy()
        {
            int count = 0;

            SimpleTargetedMessage message = new();
            RunRegistrationTest(
                token =>
                    token.RegisterTargetedWithoutTargetingPostProcessor<SimpleTargetedMessage>(
                        Handle
                    ),
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitGameObjectTargeted(_test);
                },
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitComponentTargeted(_component);
                    Assert.AreEqual(i + 1, count);
                },
                () =>
                {
                    message.EmitGameObjectTargeted(_test);
                    message.EmitComponentTargeted(_component);
                    Assert.AreEqual(0, count);
                },
                () =>
                {
                    count = 0;
                }
            );
            return;

            void Handle(ref InstanceId target, ref SimpleTargetedMessage message)
            {
                ++count;
            }
        }

        [Test]
        public void GameObjectBroadcastNormal()
        {
            int count = 0;

            SimpleBroadcastMessage message = new();
            RunRegistrationTest(
                token => token.RegisterGameObjectBroadcast<SimpleBroadcastMessage>(_test, Handle),
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitGameObjectBroadcast(_test);
                },
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitGameObjectBroadcast(_test);
                    Assert.AreEqual(i + 1, count);
                },
                () =>
                {
                    message.EmitGameObjectBroadcast(_test);
                    message.EmitComponentBroadcast(_component);
                    Assert.AreEqual(0, count);
                },
                () =>
                {
                    count = 0;
                }
            );
            return;

            void Handle(SimpleBroadcastMessage message)
            {
                ++count;
            }
        }

        [Test]
        public void GameObjectBroadcastNoCopy()
        {
            int count = 0;

            SimpleBroadcastMessage message = new();
            RunRegistrationTest(
                token => token.RegisterGameObjectBroadcast<SimpleBroadcastMessage>(_test, Handle),
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitGameObjectBroadcast(_test);
                },
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitGameObjectBroadcast(_test);
                    Assert.AreEqual(i + 1, count);
                },
                () =>
                {
                    message.EmitGameObjectBroadcast(_test);
                    message.EmitComponentBroadcast(_component);
                    Assert.AreEqual(0, count);
                },
                () =>
                {
                    count = 0;
                }
            );
            return;

            void Handle(ref SimpleBroadcastMessage message)
            {
                ++count;
            }
        }

        [Test]
        public void GameObjectBroadcastPostProcessor()
        {
            int count = 0;

            SimpleBroadcastMessage message = new();
            RunRegistrationTest(
                token =>
                    token.RegisterGameObjectBroadcastPostProcessor<SimpleBroadcastMessage>(
                        _test,
                        Handle
                    ),
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitGameObjectBroadcast(_test);
                },
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitGameObjectBroadcast(_test);
                    Assert.AreEqual(i + 1, count);
                },
                () =>
                {
                    message.EmitGameObjectBroadcast(_test);
                    message.EmitComponentBroadcast(_component);
                    Assert.AreEqual(0, count);
                },
                () =>
                {
                    count = 0;
                }
            );
            return;

            void Handle(ref SimpleBroadcastMessage message)
            {
                ++count;
            }
        }

        [Test]
        public void ComponentBroadcastNormal()
        {
            int count = 0;

            SimpleBroadcastMessage message = new();
            RunRegistrationTest(
                token =>
                    token.RegisterComponentBroadcast<SimpleBroadcastMessage>(_component, Handle),
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitComponentBroadcast(_component);
                },
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitComponentBroadcast(_component);
                    Assert.AreEqual(i + 1, count);
                },
                () =>
                {
                    message.EmitGameObjectBroadcast(_test);
                    message.EmitComponentBroadcast(_component);
                    Assert.AreEqual(0, count);
                },
                () =>
                {
                    count = 0;
                }
            );
            return;

            void Handle(SimpleBroadcastMessage message)
            {
                ++count;
            }
        }

        [Test]
        public void ComponentBroadcastNoCopy()
        {
            int count = 0;

            SimpleBroadcastMessage message = new();
            RunRegistrationTest(
                token =>
                    token.RegisterComponentBroadcast<SimpleBroadcastMessage>(_component, Handle),
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitComponentBroadcast(_component);
                },
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitComponentBroadcast(_component);
                    Assert.AreEqual(i + 1, count);
                },
                () =>
                {
                    message.EmitGameObjectBroadcast(_test);
                    message.EmitComponentBroadcast(_component);
                    Assert.AreEqual(0, count);
                },
                () =>
                {
                    count = 0;
                }
            );
            return;

            void Handle(ref SimpleBroadcastMessage message)
            {
                ++count;
            }
        }

        [Test]
        public void ComponentBroadcastPostProcessor()
        {
            int count = 0;

            SimpleBroadcastMessage message = new();
            RunRegistrationTest(
                token =>
                    token.RegisterComponentBroadcastPostProcessor<SimpleBroadcastMessage>(
                        _component,
                        Handle
                    ),
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitComponentBroadcast(_component);
                },
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitComponentBroadcast(_component);
                    Assert.AreEqual(i + 1, count);
                },
                () =>
                {
                    message.EmitGameObjectBroadcast(_test);
                    message.EmitComponentBroadcast(_component);
                    Assert.AreEqual(0, count);
                },
                () =>
                {
                    count = 0;
                }
            );
            return;

            void Handle(ref SimpleBroadcastMessage message)
            {
                ++count;
            }
        }

        [Test]
        public void BroadcastInterceptor()
        {
            int count = 0;

            SimpleBroadcastMessage message = new();
            RunRegistrationTest(
                token => token.RegisterBroadcastInterceptor<SimpleBroadcastMessage>(Intercept),
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitComponentBroadcast(_component);
                },
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitComponentBroadcast(_component);
                    Assert.AreEqual(i + 1, count);
                },
                () =>
                {
                    message.EmitGameObjectBroadcast(_test);
                    message.EmitComponentBroadcast(_component);
                    Assert.AreEqual(0, count);
                },
                () =>
                {
                    count = 0;
                }
            );
            return;

            bool Intercept(ref InstanceId target, ref SimpleBroadcastMessage message)
            {
                ++count;
                return true;
            }
        }

        [Test]
        public void BroadcastWithoutSourceNormal()
        {
            int count = 0;

            SimpleBroadcastMessage message = new();
            RunRegistrationTest(
                token => token.RegisterBroadcastWithoutSource<SimpleBroadcastMessage>(Handle),
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitComponentBroadcast(_component);
                },
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitComponentBroadcast(_component);
                    Assert.AreEqual(i + 1, count);
                },
                () =>
                {
                    message.EmitGameObjectBroadcast(_test);
                    message.EmitComponentBroadcast(_component);
                    Assert.AreEqual(0, count);
                },
                () =>
                {
                    count = 0;
                }
            );
            return;

            void Handle(InstanceId id, SimpleBroadcastMessage message)
            {
                ++count;
            }
        }

        [Test]
        public void BroadcastWithoutSourceNoCopy()
        {
            int count = 0;

            SimpleBroadcastMessage message = new();
            RunRegistrationTest(
                token => token.RegisterBroadcastWithoutSource<SimpleBroadcastMessage>(Handle),
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitComponentBroadcast(_component);
                },
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitComponentBroadcast(_component);
                    Assert.AreEqual(i + 1, count);
                },
                () =>
                {
                    message.EmitGameObjectBroadcast(_test);
                    message.EmitComponentBroadcast(_component);
                    Assert.AreEqual(0, count);
                },
                () =>
                {
                    count = 0;
                }
            );
            return;

            void Handle(ref InstanceId id, ref SimpleBroadcastMessage message)
            {
                ++count;
            }
        }

        [Test]
        public void BroadcastWithoutSourcePostProcessorNormal()
        {
            int count = 0;

            SimpleBroadcastMessage message = new();
            RunRegistrationTest(
                token =>
                    token.RegisterBroadcastWithoutSourcePostProcessor<SimpleBroadcastMessage>(
                        Handle
                    ),
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitComponentBroadcast(_component);
                },
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitComponentBroadcast(_component);
                    Assert.AreEqual(i + 1, count);
                },
                () =>
                {
                    message.EmitGameObjectBroadcast(_test);
                    message.EmitComponentBroadcast(_component);
                    Assert.AreEqual(0, count);
                },
                () =>
                {
                    count = 0;
                }
            );
            return;

            void Handle(InstanceId id, SimpleBroadcastMessage message)
            {
                ++count;
            }
        }

        [Test]
        public void BroadcastWithoutSourcePostProcessorNoCopy()
        {
            int count = 0;

            SimpleBroadcastMessage message = new();
            RunRegistrationTest(
                token =>
                    token.RegisterBroadcastWithoutSourcePostProcessor<SimpleBroadcastMessage>(
                        Handle
                    ),
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitComponentBroadcast(_component);
                },
                i =>
                {
                    Assert.AreEqual(i, count);
                    message.EmitComponentBroadcast(_component);
                    Assert.AreEqual(i + 1, count);
                },
                () =>
                {
                    message.EmitGameObjectBroadcast(_test);
                    message.EmitComponentBroadcast(_component);
                    Assert.AreEqual(0, count);
                },
                () =>
                {
                    count = 0;
                }
            );
            return;

            void Handle(ref InstanceId id, ref SimpleBroadcastMessage message)
            {
                ++count;
            }
        }

        [Test]
        public void GlobalAcceptAllNormal()
        {
            int targetedCount = 0;
            int broadcastCount = 0;
            int untargetedCount = 0;

            SimpleUntargetedMessage untargetedMessage = new();
            SimpleTargetedMessage targetedMessage = new();
            SimpleBroadcastMessage broadcastMessage = new();
            RunRegistrationTest(
                token =>
                    token.RegisterGlobalAcceptAll(
                        HandleUntargeted,
                        HandleTargeted,
                        HandleBroadcast
                    ),
                i =>
                {
                    Assert.AreEqual(i, targetedCount);
                    Assert.AreEqual(i, broadcastCount);
                    Assert.AreEqual(i, untargetedCount);
                    untargetedMessage.EmitUntargeted();
                    targetedMessage.EmitGameObjectTargeted(_test);
                    broadcastMessage.EmitComponentBroadcast(_component);
                },
                i =>
                {
                    Assert.AreEqual(i, targetedCount);
                    Assert.AreEqual(i, broadcastCount);
                    Assert.AreEqual(i, untargetedCount);
                    untargetedMessage.EmitUntargeted();
                    Assert.AreEqual(i, targetedCount);
                    Assert.AreEqual(i, broadcastCount);
                    Assert.AreEqual(i + 1, untargetedCount);
                    targetedMessage.EmitGameObjectTargeted(_test);
                    Assert.AreEqual(i + 1, targetedCount);
                    Assert.AreEqual(i, broadcastCount);
                    Assert.AreEqual(i + 1, untargetedCount);
                    broadcastMessage.EmitComponentBroadcast(_component);
                    Assert.AreEqual(i + 1, targetedCount);
                    Assert.AreEqual(i + 1, broadcastCount);
                    Assert.AreEqual(i + 1, untargetedCount);
                },
                () =>
                {
                    untargetedMessage.EmitUntargeted();
                    targetedMessage.EmitGameObjectTargeted(_test);
                    broadcastMessage.EmitComponentBroadcast(_component);
                    Assert.AreEqual(0, targetedCount);
                    Assert.AreEqual(0, broadcastCount);
                    Assert.AreEqual(0, untargetedCount);
                },
                () =>
                {
                    targetedCount = 0;
                    broadcastCount = 0;
                    untargetedCount = 0;
                }
            );
            return;

            void HandleUntargeted(IUntargetedMessage message)
            {
                ++untargetedCount;
            }

            void HandleTargeted(InstanceId id, ITargetedMessage message)
            {
                ++targetedCount;
            }

            void HandleBroadcast(InstanceId id, IBroadcastMessage message)
            {
                ++broadcastCount;
            }
        }

        [Test]
        public void GlobalAcceptAllNoCopy()
        {
            int targetedCount = 0;
            int broadcastCount = 0;
            int untargetedCount = 0;

            SimpleUntargetedMessage untargetedMessage = new();
            SimpleTargetedMessage targetedMessage = new();
            SimpleBroadcastMessage broadcastMessage = new();
            RunRegistrationTest(
                token =>
                    token.RegisterGlobalAcceptAll(
                        HandleUntargeted,
                        HandleTargeted,
                        HandleBroadcast
                    ),
                i =>
                {
                    Assert.AreEqual(i, targetedCount);
                    Assert.AreEqual(i, broadcastCount);
                    Assert.AreEqual(i, untargetedCount);
                    untargetedMessage.EmitUntargeted();
                    targetedMessage.EmitGameObjectTargeted(_test);
                    broadcastMessage.EmitComponentBroadcast(_component);
                },
                i =>
                {
                    Assert.AreEqual(i, targetedCount);
                    Assert.AreEqual(i, broadcastCount);
                    Assert.AreEqual(i, untargetedCount);
                    untargetedMessage.EmitUntargeted();
                    Assert.AreEqual(i, targetedCount);
                    Assert.AreEqual(i, broadcastCount);
                    Assert.AreEqual(i + 1, untargetedCount);
                    targetedMessage.EmitGameObjectTargeted(_test);
                    Assert.AreEqual(i + 1, targetedCount);
                    Assert.AreEqual(i, broadcastCount);
                    Assert.AreEqual(i + 1, untargetedCount);
                    broadcastMessage.EmitComponentBroadcast(_component);
                    Assert.AreEqual(i + 1, targetedCount);
                    Assert.AreEqual(i + 1, broadcastCount);
                    Assert.AreEqual(i + 1, untargetedCount);
                },
                () =>
                {
                    untargetedMessage.EmitUntargeted();
                    targetedMessage.EmitGameObjectTargeted(_test);
                    broadcastMessage.EmitComponentBroadcast(_component);
                    Assert.AreEqual(0, targetedCount);
                    Assert.AreEqual(0, broadcastCount);
                    Assert.AreEqual(0, untargetedCount);
                },
                () =>
                {
                    targetedCount = 0;
                    broadcastCount = 0;
                    untargetedCount = 0;
                }
            );
            return;

            void HandleUntargeted(ref IUntargetedMessage message)
            {
                ++untargetedCount;
            }

            void HandleTargeted(ref InstanceId id, ref ITargetedMessage message)
            {
                ++targetedCount;
            }

            void HandleBroadcast(ref InstanceId id, ref IBroadcastMessage message)
            {
                ++broadcastCount;
            }
        }

        private void RunRegistrationTest(
            Func<MessageRegistrationToken, MessageRegistrationHandle> registration,
            Action<int> normalAssert,
            Action<int> removalAssert,
            Action finalAssert,
            Action reset
        )
        {
            HashSet<MessageRegistrationHandle> handles = new(_numRegistrations);
            try
            {
                for (int i = 0; i < _numRegistrations; ++i)
                {
                    MessageRegistrationHandle handle = registration(_token);
                    bool neverSeen = handles.Add(handle);
                    Assert.IsTrue(
                        neverSeen,
                        "Handle {0} at count {1} was a duplicate.",
                        handle,
                        neverSeen
                    );
                }

                for (int i = 0; i < 100; ++i)
                {
                    normalAssert(i);
                }

                reset();
                int expected = 0;
                foreach (
                    MessageRegistrationHandle handle in handles
                        .OrderBy(_ => _random.Next())
                        .ToList()
                )
                {
                    removalAssert(expected++);
                    handles.Remove(handle);
                    _token.RemoveRegistration(handle);
                }

                reset();
                for (int i = 0; i < 100; ++i)
                {
                    finalAssert();
                }
            }
            finally
            {
                foreach (MessageRegistrationHandle handle in handles)
                {
                    _token.RemoveRegistration(handle);
                }
            }
        }
    }
}

#endif
