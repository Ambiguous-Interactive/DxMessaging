#if UNITY_2021_3_OR_NEWER
// ReSharper disable AccessToModifiedClosure
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections;
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using DxMessaging.Unity;
    using NUnit.Framework;
    using UnityEngine;
    using UnityEngine.TestTools;

    public sealed class EdgeCaseTests : MessagingTestBase
    {
        [Test]
        public void UnregisterAllClearsRegistrations()
        {
            GameObject test = new(
                nameof(UnregisterAllClearsRegistrations),
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test);
            EmptyMessageAwareComponent component = test.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);

            int count = 0;
            _ = token.RegisterUntargeted<SimpleUntargetedMessage>(_ => ++count);

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted();
            Assert.AreEqual(1, count);

            token.UnregisterAll();
            count = 0;

            message.EmitUntargeted();
            Assert.AreEqual(0, count);

            token.Enable();
            message.EmitUntargeted();
            Assert.AreEqual(0, count);

            token.UnregisterAll();
            message.EmitUntargeted();
            Assert.AreEqual(0, count);
        }

        [Test]
        public void RemoveRegistrationInsideUntargetedHandler()
        {
            GameObject test = new(
                nameof(RemoveRegistrationInsideUntargetedHandler),
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test);
            EmptyMessageAwareComponent component = test.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);

            int count = 0;
            MessageRegistrationHandle handle = default;
            handle = token.RegisterUntargeted<SimpleUntargetedMessage>(_ =>
            {
                ++count;
                token.RemoveRegistration(handle);
            });

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted();
            Assert.AreEqual(1, count);

            message.EmitUntargeted();
            Assert.AreEqual(1, count);
        }

        [Test]
        public void AddRegistrationDuringUntargetedEmission()
        {
            GameObject test = new(
                nameof(AddRegistrationDuringUntargetedEmission),
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test);
            EmptyMessageAwareComponent component = test.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);

            int primaryCount = 0;
            int secondaryCount = 0;
            MessageRegistrationHandle? secondaryHandle = null;

            MessageRegistrationHandle primaryHandle =
                token.RegisterUntargeted<SimpleUntargetedMessage>(_ =>
                {
                    ++primaryCount;
                    secondaryHandle ??= token.RegisterUntargeted<SimpleUntargetedMessage>(_ =>
                        ++secondaryCount
                    );
                });

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted();
            Assert.AreEqual(1, primaryCount);
            Assert.AreEqual(0, secondaryCount);

            message.EmitUntargeted();
            Assert.AreEqual(2, primaryCount);
            Assert.AreEqual(1, secondaryCount);

            message.EmitUntargeted();
            Assert.AreEqual(3, primaryCount);
            Assert.AreEqual(2, secondaryCount);

            token.RemoveRegistration(primaryHandle);
            if (secondaryHandle.HasValue)
            {
                token.RemoveRegistration(secondaryHandle.Value);
            }
        }

        [Test]
        public void RegisterWhileDisabledRegistersOnEnable()
        {
            GameObject test = new(
                nameof(RegisterWhileDisabledRegistersOnEnable),
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test);
            EmptyMessageAwareComponent component = test.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);

            token.Disable();

            int count = 0;
            MessageRegistrationHandle handle = token.RegisterUntargeted<SimpleUntargetedMessage>(
                _ => ++count
            );
            SimpleUntargetedMessage message = new();

            message.EmitUntargeted();
            Assert.AreEqual(0, count);

            token.Enable();
            message.EmitUntargeted();
            Assert.AreEqual(1, count);

            token.Disable();
            message.EmitUntargeted();
            Assert.AreEqual(1, count);

            token.Enable();
            message.EmitUntargeted();
            Assert.AreEqual(2, count);

            token.Enable();
            message.EmitUntargeted();
            Assert.AreEqual(3, count);

            token.RemoveRegistration(handle);
        }

        [Test]
        public void RemoveRegistrationInsideTargetedHandler()
        {
            GameObject test = new(
                nameof(RemoveRegistrationInsideTargetedHandler),
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test);
            EmptyMessageAwareComponent component = test.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);

            int count = 0;
            MessageRegistrationHandle handle = default;
            handle = token.RegisterGameObjectTargeted<SimpleTargetedMessage>(
                test,
                _ =>
                {
                    ++count;
                    token.RemoveRegistration(handle);
                }
            );

            SimpleTargetedMessage message = new();
            message.EmitGameObjectTargeted(test);
            Assert.AreEqual(1, count);

            message.EmitGameObjectTargeted(test);
            Assert.AreEqual(1, count);
        }

        [Test]
        public void RemoveRegistrationInsideBroadcastHandler()
        {
            GameObject test = new(
                nameof(RemoveRegistrationInsideBroadcastHandler),
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test);
            EmptyMessageAwareComponent component = test.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);

            int count = 0;
            MessageRegistrationHandle handle = default;
            handle = token.RegisterGameObjectBroadcast<SimpleBroadcastMessage>(
                test,
                _ =>
                {
                    ++count;
                    token.RemoveRegistration(handle);
                }
            );

            SimpleBroadcastMessage message = new();
            message.EmitGameObjectBroadcast(test);
            Assert.AreEqual(1, count);

            message.EmitGameObjectBroadcast(test);
            Assert.AreEqual(1, count);
        }

        [Test]
        public void RemoveRegistrationInsideInterceptor()
        {
            GameObject test = new(
                nameof(RemoveRegistrationInsideInterceptor),
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test);
            EmptyMessageAwareComponent component = test.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);

            int count = 0;
            MessageRegistrationHandle handle = default;
            handle = token.RegisterUntargetedInterceptor(
                (ref SimpleUntargetedMessage _) =>
                {
                    ++count;
                    token.RemoveRegistration(handle);
                    return true;
                }
            );

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted();
            Assert.AreEqual(1, count);

            message.EmitUntargeted();
            Assert.AreEqual(1, count);
        }

        [Test]
        public void MessagingComponentCreateThrowsForNullListener()
        {
            GameObject test = new(
                nameof(MessagingComponentCreateThrowsForNullListener),
                typeof(MessagingComponent)
            );
            _spawned.Add(test);
            MessagingComponent messagingComponent = test.GetComponent<MessagingComponent>();

            Assert.Throws<ArgumentNullException>(() => messagingComponent.Create(null));
        }

        [Test]
        public void MessagingComponentCreateThrowsForForeignListener()
        {
            GameObject owner = new(
                nameof(MessagingComponentCreateThrowsForForeignListener),
                typeof(MessagingComponent)
            );
            _spawned.Add(owner);
            MessagingComponent messagingComponent = owner.GetComponent<MessagingComponent>();

            GameObject foreign = new("ForeignListener", typeof(ManualListenerComponent));
            _spawned.Add(foreign);
            ManualListenerComponent listener = foreign.GetComponent<ManualListenerComponent>();

            Assert.Throws<ArgumentException>(() => messagingComponent.Create(listener));
        }

        [Test]
        public void MessagingComponentCreateReturnsSameToken()
        {
            GameObject test = new(
                nameof(MessagingComponentCreateReturnsSameToken),
                typeof(MessagingComponent),
                typeof(ManualListenerComponent)
            );
            _spawned.Add(test);
            MessagingComponent messagingComponent = test.GetComponent<MessagingComponent>();
            ManualListenerComponent listener = test.GetComponent<ManualListenerComponent>();

            MessageRegistrationToken first = messagingComponent.Create(listener);
            MessageRegistrationToken second = messagingComponent.Create(listener);
            Assert.AreSame(first, second);
        }

        [UnityTest]
        public IEnumerator MessagingComponentStopsEmittingWhenDisabled()
        {
            GameObject test = new(
                nameof(MessagingComponentStopsEmittingWhenDisabled),
                typeof(MessagingComponent),
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(test);
            MessagingComponent messagingComponent = test.GetComponent<MessagingComponent>();
            SimpleMessageAwareComponent component =
                test.GetComponent<SimpleMessageAwareComponent>();

            int count = 0;
            component.untargetedHandler = () => ++count;
            SimpleUntargetedMessage message = new();

            message.EmitUntargeted();
            Assert.AreEqual(1, count);

            messagingComponent.enabled = false;
            yield return null;

            message.EmitUntargeted();
            Assert.AreEqual(1, count);

            messagingComponent.enabled = true;
            yield return null;

            message.EmitUntargeted();
            Assert.AreEqual(2, count);
        }

        [UnityTest]
        public IEnumerator MessagingComponentContinuesEmittingWhenConfigured()
        {
            GameObject test = new(
                nameof(MessagingComponentContinuesEmittingWhenConfigured),
                typeof(MessagingComponent),
                typeof(SimpleMessageAwareComponent)
            );
            _spawned.Add(test);
            MessagingComponent messagingComponent = test.GetComponent<MessagingComponent>();
            messagingComponent.emitMessagesWhenDisabled = true;
            SimpleMessageAwareComponent component =
                test.GetComponent<SimpleMessageAwareComponent>();

            int count = 0;
            component.untargetedHandler = () => ++count;
            SimpleUntargetedMessage message = new();

            message.EmitUntargeted();
            Assert.AreEqual(1, count);

            messagingComponent.enabled = false;
            yield return null;

            message.EmitUntargeted();
            Assert.AreEqual(2, count);

            messagingComponent.enabled = true;
            yield return null;

            message.EmitUntargeted();
            Assert.AreEqual(3, count);
        }

        [Test]
        public void AddRegistrationDuringTargetedEmission()
        {
            GameObject test = new(
                nameof(AddRegistrationDuringTargetedEmission),
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test);
            EmptyMessageAwareComponent component = test.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);

            int primaryCount = 0;
            int secondaryCount = 0;
            MessageRegistrationHandle? secondaryHandle = null;

            MessageRegistrationHandle primaryHandle =
                token.RegisterGameObjectTargeted<SimpleTargetedMessage>(
                    test,
                    _ =>
                    {
                        ++primaryCount;
                        secondaryHandle ??= token.RegisterGameObjectTargeted<SimpleTargetedMessage>(
                            test,
                            _ => ++secondaryCount
                        );
                    }
                );

            SimpleTargetedMessage message = new();
            message.EmitGameObjectTargeted(test);
            Assert.AreEqual(1, primaryCount);
            Assert.AreEqual(0, secondaryCount);

            message.EmitGameObjectTargeted(test);
            Assert.AreEqual(2, primaryCount);
            Assert.AreEqual(1, secondaryCount);

            message.EmitGameObjectTargeted(test);
            Assert.AreEqual(3, primaryCount);
            Assert.AreEqual(2, secondaryCount);

            token.RemoveRegistration(primaryHandle);
            if (secondaryHandle.HasValue)
            {
                token.RemoveRegistration(secondaryHandle.Value);
            }
        }

        [Test]
        public void AddRegistrationDuringBroadcastEmission()
        {
            GameObject test = new(
                nameof(AddRegistrationDuringBroadcastEmission),
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test);
            EmptyMessageAwareComponent component = test.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);

            int primaryCount = 0;
            int secondaryCount = 0;
            MessageRegistrationHandle? secondaryHandle = null;

            MessageRegistrationHandle primaryHandle =
                token.RegisterGameObjectBroadcast<SimpleBroadcastMessage>(
                    test,
                    _ =>
                    {
                        ++primaryCount;
                        secondaryHandle ??=
                            token.RegisterGameObjectBroadcast<SimpleBroadcastMessage>(
                                test,
                                _ => ++secondaryCount
                            );
                    }
                );

            SimpleBroadcastMessage message = new();
            message.EmitGameObjectBroadcast(test);
            Assert.AreEqual(1, primaryCount);
            Assert.AreEqual(0, secondaryCount);

            message.EmitGameObjectBroadcast(test);
            Assert.AreEqual(2, primaryCount);
            Assert.AreEqual(1, secondaryCount);

            message.EmitGameObjectBroadcast(test);
            Assert.AreEqual(3, primaryCount);
            Assert.AreEqual(2, secondaryCount);

            token.RemoveRegistration(primaryHandle);
            if (secondaryHandle.HasValue)
            {
                token.RemoveRegistration(secondaryHandle.Value);
            }
        }

        [Test]
        public void AddRegistrationDuringTargetedWithoutTargetingEmission()
        {
            GameObject test = new(
                nameof(AddRegistrationDuringTargetedWithoutTargetingEmission),
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test);
            EmptyMessageAwareComponent component = test.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);

            int primaryCount = 0;
            int secondaryCount = 0;
            MessageRegistrationHandle? secondaryHandle = null;

            MessageRegistrationHandle primaryHandle =
                token.RegisterTargetedWithoutTargeting<SimpleTargetedMessage>(
                    (_, _) =>
                    {
                        ++primaryCount;
                        if (secondaryHandle == null)
                        {
                            secondaryHandle =
                                token.RegisterTargetedWithoutTargeting<SimpleTargetedMessage>(
                                    (_, _) => ++secondaryCount
                                );
                        }
                    }
                );

            SimpleTargetedMessage message = new();
            message.EmitGameObjectTargeted(test);
            Assert.AreEqual(1, primaryCount);
            Assert.AreEqual(0, secondaryCount);

            message.EmitGameObjectTargeted(test);
            Assert.AreEqual(2, primaryCount);
            Assert.AreEqual(1, secondaryCount);

            message.EmitGameObjectTargeted(test);
            Assert.AreEqual(3, primaryCount);
            Assert.AreEqual(2, secondaryCount);

            token.RemoveRegistration(primaryHandle);
            if (secondaryHandle.HasValue)
            {
                token.RemoveRegistration(secondaryHandle.Value);
            }
        }

        [Test]
        public void AddRegistrationDuringBroadcastWithoutSourceEmission()
        {
            GameObject test = new(
                nameof(AddRegistrationDuringBroadcastWithoutSourceEmission),
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(test);
            EmptyMessageAwareComponent component = test.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);

            int primaryCount = 0;
            int secondaryCount = 0;
            MessageRegistrationHandle? secondaryHandle = null;

            MessageRegistrationHandle primaryHandle =
                token.RegisterBroadcastWithoutSource<SimpleBroadcastMessage>(
                    (_, _) =>
                    {
                        ++primaryCount;
                        if (secondaryHandle == null)
                        {
                            secondaryHandle =
                                token.RegisterBroadcastWithoutSource<SimpleBroadcastMessage>(
                                    (_, _) => ++secondaryCount
                                );
                        }
                    }
                );

            SimpleBroadcastMessage message = new();
            message.EmitComponentBroadcast(component);
            Assert.AreEqual(1, primaryCount);
            Assert.AreEqual(0, secondaryCount);

            message.EmitComponentBroadcast(component);
            Assert.AreEqual(2, primaryCount);
            Assert.AreEqual(1, secondaryCount);

            message.EmitComponentBroadcast(component);
            Assert.AreEqual(3, primaryCount);
            Assert.AreEqual(2, secondaryCount);

            token.RemoveRegistration(primaryHandle);
            if (secondaryHandle.HasValue)
            {
                token.RemoveRegistration(secondaryHandle.Value);
            }
        }
    }
}

#endif
