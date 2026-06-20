#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections;
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;
    using Scripts.Components;
    using Scripts.Messages;
    using UnityEngine;
    using UnityEngine.TestTools;

    public sealed class GlobalAcceptAllTests : MessagingTestBase
    {
        private GameObject _test;

        [UnitySetUp]
        public override IEnumerator UnitySetup()
        {
            IEnumerator baseSetup = base.UnitySetup();
            while (baseSetup.MoveNext())
            {
                yield return baseSetup.Current;
            }

            _test = new(nameof(GlobalAcceptAllTests), typeof(EmptyMessageAwareComponent));
            _spawned.Add(_test);
        }

        [Test]
        public void SimpleNormal()
        {
            int untargetedCount = 0;
            int targetedCount = 0;
            int broadcastCount = 0;

            SimpleUntargetedMessage untargetedMessage = new();
            SimpleTargetedMessage targetedMessage = new();
            SimpleBroadcastMessage broadcastMessage = new();
            RunGlobalAcceptAllTest(
                token =>
                    token.RegisterGlobalAcceptAll(
                        HandleUntargeted,
                        HandleTargeted,
                        HandleBroadcast
                    ),
                i =>
                {
                    Assert.AreEqual(i, untargetedCount);
                    untargetedMessage.EmitUntargeted();
                    Assert.AreEqual(i + 1, untargetedCount);
                },
                i =>
                {
                    Assert.AreEqual(i, targetedCount);
                    if (_random.Next() % 2 == 0)
                    {
                        targetedMessage.EmitGameObjectTargeted(_test);
                    }
                    else
                    {
                        targetedMessage.EmitComponentTargeted(_test.transform);
                    }

                    Assert.AreEqual(i + 1, targetedCount);
                },
                i =>
                {
                    Assert.AreEqual(i, broadcastCount);
                    if (_random.Next() % 2 == 0)
                    {
                        broadcastMessage.EmitGameObjectBroadcast(_test);
                    }
                    else
                    {
                        broadcastMessage.EmitComponentBroadcast(_test.transform);
                    }

                    Assert.AreEqual(i + 1, broadcastCount);
                }
            );
            return;

            void HandleUntargeted(IUntargetedMessage message)
            {
                ++untargetedCount;
            }

            void HandleTargeted(InstanceId target, ITargetedMessage message)
            {
                ++targetedCount;
            }

            void HandleBroadcast(InstanceId source, IBroadcastMessage message)
            {
                ++broadcastCount;
            }
        }

        [Test]
        public void SimpleNoCopy()
        {
            int untargetedCount = 0;
            int targetedCount = 0;
            int broadcastCount = 0;

            SimpleUntargetedMessage untargetedMessage = new();
            SimpleTargetedMessage targetedMessage = new();
            SimpleBroadcastMessage broadcastMessage = new();
            RunGlobalAcceptAllTest(
                token =>
                    token.RegisterGlobalAcceptAll(
                        HandleUntargeted,
                        HandleTargeted,
                        HandleBroadcast
                    ),
                i =>
                {
                    Assert.AreEqual(i, untargetedCount);
                    untargetedMessage.EmitUntargeted();
                    Assert.AreEqual(i + 1, untargetedCount);
                },
                i =>
                {
                    Assert.AreEqual(i, targetedCount);
                    if (_random.Next() % 2 == 0)
                    {
                        targetedMessage.EmitGameObjectTargeted(_test);
                    }
                    else
                    {
                        targetedMessage.EmitComponentTargeted(_test.transform);
                    }

                    Assert.AreEqual(i + 1, targetedCount);
                },
                i =>
                {
                    Assert.AreEqual(i, broadcastCount);
                    if (_random.Next() % 2 == 0)
                    {
                        broadcastMessage.EmitGameObjectBroadcast(_test);
                    }
                    else
                    {
                        broadcastMessage.EmitComponentBroadcast(_test.transform);
                    }

                    Assert.AreEqual(i + 1, broadcastCount);
                }
            );
            return;

            void HandleUntargeted(ref IUntargetedMessage message)
            {
                ++untargetedCount;
            }

            void HandleTargeted(ref InstanceId target, ref ITargetedMessage message)
            {
                ++targetedCount;
            }

            void HandleBroadcast(ref InstanceId source, ref IBroadcastMessage message)
            {
                ++broadcastCount;
            }
        }

        private void RunGlobalAcceptAllTest(
            Action<MessageRegistrationToken> register,
            Action<int> untargeted,
            Action<int> targeted,
            Action<int> broadcast
        )
        {
            EmptyMessageAwareComponent component = _test.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);

            register(token);
            for (int i = 0; i < 100; ++i)
            {
                untargeted(i);
                targeted(i);
                broadcast(i);
            }
        }
    }
}

#endif
