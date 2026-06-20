#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Unity
{
    using System.Collections;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Unity;
    using NUnit.Framework;
    using UnityEngine;
    using UnityEngine.TestTools;

    public sealed class MessagingComponentProviderIntegrationTests
    {
        private readonly List<Object> _objectsToDestroy = new();

        [SetUp]
        public void SetUp()
        {
            _objectsToDestroy.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _objectsToDestroy.Count; ++i)
            {
                Object obj = _objectsToDestroy[i];
                if (obj != null)
                {
                    Object.DestroyImmediate(obj);
                }
            }

            _objectsToDestroy.Clear();
        }

        [UnityTest]
        public IEnumerator ConfigureWithProviderHandleRoutesThroughProviderBus()
        {
            MessageBus messageBus = new();
            TestScriptableMessageBusProvider provider =
                ScriptableObject.CreateInstance<TestScriptableMessageBusProvider>();
            provider.Configure(messageBus);
            Track(provider);
            MessageBusProviderHandle handle = new(provider);

            GameObject owner = Track(new GameObject("MessagingComponentOwner"));
            MessagingComponent messagingComponent = owner.AddComponent<MessagingComponent>();
            messagingComponent.Configure(handle, MessageBusRebindMode.RebindActive);

            TestListener listener = owner.AddComponent<TestListener>();
            listener.Initialize(messagingComponent);

            yield return null;

            TestUntargetedMessage message = new(42);
            messageBus.UntargetedBroadcast(ref message);

            Assert.AreEqual(1, listener.ReceivedCount);
        }

        [UnityTest]
        public IEnumerator InstallerAppliesConfigurationToChildren()
        {
            MessageBus messageBus = new();
            TestScriptableMessageBusProvider provider =
                ScriptableObject.CreateInstance<TestScriptableMessageBusProvider>();
            provider.Configure(messageBus);
            Track(provider);
            MessageBusProviderHandle handle = new(provider);

            GameObject root = Track(new GameObject("InstallerRoot"));
            MessagingComponentInstaller installer =
                root.AddComponent<MessagingComponentInstaller>();
            installer.SetProvider(handle);

            GameObject child = Track(new GameObject("InstallerListener"));
            child.transform.SetParent(root.transform);
            MessagingComponent messagingComponent = child.AddComponent<MessagingComponent>();

            installer.ApplyConfiguration();

            TestListener listener = child.AddComponent<TestListener>();
            listener.Initialize(messagingComponent);

            yield return null;

            TestUntargetedMessage message = new(19);
            messageBus.UntargetedBroadcast(ref message);

            Assert.AreEqual(1, listener.ReceivedCount);
        }

        [Test]
        public void CreateRegistrationBuilderUsesConfiguredProviderBus()
        {
            MessageBus messageBus = new();
            TestProvider provider = new(messageBus);

            GameObject owner = Track(new GameObject("BuilderOwner"));
            MessagingComponent messagingComponent = owner.AddComponent<MessagingComponent>();
            messagingComponent.Configure(provider, MessageBusRebindMode.RebindActive);

            IMessageRegistrationBuilder builder = messagingComponent.CreateRegistrationBuilder();
            using (
                MessageRegistrationLease lease = builder.Build(
                    new MessageRegistrationBuildOptions()
                )
            )
            {
                Assert.AreSame(messageBus, lease.MessageBus);
                Assert.IsFalse(lease.Token.Enabled);
            }
        }

        [Test]
        public void CreateRegistrationBuilderUsesOverrideBusWhenNoProvider()
        {
            MessageBus messageBus = new();

            GameObject owner = Track(new GameObject("OverrideOwner"));
            MessagingComponent messagingComponent = owner.AddComponent<MessagingComponent>();
            messagingComponent.Configure(messageBus, MessageBusRebindMode.RebindActive);

            IMessageRegistrationBuilder builder = messagingComponent.CreateRegistrationBuilder();
            using (
                MessageRegistrationLease lease = builder.Build(
                    new MessageRegistrationBuildOptions()
                )
            )
            {
                Assert.AreSame(messageBus, lease.MessageBus);
            }
        }

        [UnityTest]
        public IEnumerator InstallerWithoutConfigurationLogsWarning()
        {
            GameObject root = Track(new GameObject("InstallerWarningRoot"));
            MessagingComponentInstaller installer =
                root.AddComponent<MessagingComponentInstaller>();

            GameObject child = Track(new GameObject("InstallerWarningChild"));
            child.transform.SetParent(root.transform);
            _ = child.AddComponent<MessagingComponent>();

            LogAssert.Expect(
                LogType.Warning,
                new Regex(
                    "MessagingComponentInstaller.+has no provider or explicit message bus configured"
                )
            );

            installer.ApplyConfiguration();
            yield return null;

            yield break;
        }

        [UnityTest]
        public IEnumerator PreserveRegistrationsKeepsExistingHandlersOnOriginalBus()
        {
            MessageBus originalBus = new();
            MessageBus newBus = new();

            GameObject owner = Track(new GameObject("PreserveOwner"));
            MessagingComponent messagingComponent = owner.AddComponent<MessagingComponent>();
            messagingComponent.Configure(originalBus, MessageBusRebindMode.RebindActive);

            TestListener originalListener = owner.AddComponent<TestListener>();
            originalListener.Initialize(messagingComponent);

            yield return null;

            TestUntargetedMessage message = new(1);
            originalBus.UntargetedBroadcast(ref message);
            Assert.AreEqual(
                1,
                originalListener.ReceivedCount,
                "Original listener should observe messages on the initial bus."
            );

            messagingComponent.Configure(newBus, MessageBusRebindMode.PreserveRegistrations);

            message = new TestUntargetedMessage(2);
            originalBus.UntargetedBroadcast(ref message);
            Assert.AreEqual(
                2,
                originalListener.ReceivedCount,
                "Existing listener should remain bound to the original bus when preserving registrations."
            );

            message = new TestUntargetedMessage(3);
            newBus.UntargetedBroadcast(ref message);
            Assert.AreEqual(
                2,
                originalListener.ReceivedCount,
                "Existing listener should not observe messages on the new bus when preserving registrations."
            );

            TestListener newListener = owner.AddComponent<TestListener>();
            newListener.Initialize(messagingComponent);

            yield return null;

            message = new TestUntargetedMessage(4);
            newBus.UntargetedBroadcast(ref message);
            Assert.AreEqual(
                1,
                newListener.ReceivedCount,
                "New listener should bind to the new bus after preservation."
            );
            Assert.AreEqual(2, originalListener.ReceivedCount);

            message = new TestUntargetedMessage(5);
            originalBus.UntargetedBroadcast(ref message);
            Assert.AreEqual(
                1,
                newListener.ReceivedCount,
                "New listener should not observe messages sent on the original bus."
            );
            Assert.AreEqual(3, originalListener.ReceivedCount);

            yield break;
        }

        [UnityTest]
        public IEnumerator RebindActiveMovesExistingHandlersToNewBus()
        {
            MessageBus originalBus = new();
            MessageBus newBus = new();

            GameObject owner = Track(new GameObject("RebindOwner"));
            MessagingComponent messagingComponent = owner.AddComponent<MessagingComponent>();
            messagingComponent.Configure(originalBus, MessageBusRebindMode.RebindActive);

            TestListener listener = owner.AddComponent<TestListener>();
            listener.Initialize(messagingComponent);

            yield return null;

            TestUntargetedMessage message = new(1);
            originalBus.UntargetedBroadcast(ref message);
            Assert.AreEqual(
                1,
                listener.ReceivedCount,
                "Listener should observe messages on the initial bus prior to rebind."
            );

            messagingComponent.Configure(newBus, MessageBusRebindMode.RebindActive);

            message = new TestUntargetedMessage(2);
            originalBus.UntargetedBroadcast(ref message);
            Assert.AreEqual(
                1,
                listener.ReceivedCount,
                "Listener should no longer receive messages on the original bus after rebinding."
            );

            message = new TestUntargetedMessage(3);
            newBus.UntargetedBroadcast(ref message);
            Assert.AreEqual(
                2,
                listener.ReceivedCount,
                "Listener should observe messages on the new bus after rebinding."
            );

            yield break;
        }

        private T Track<T>(T unityObject)
            where T : Object
        {
            if (unityObject != null)
            {
                _objectsToDestroy.Add(unityObject);
            }

            return unityObject;
        }

        private sealed class TestListener : MonoBehaviour
        {
            private MessageRegistrationToken _token;
            public int ReceivedCount { get; private set; }

            public void Initialize(MessagingComponent messagingComponent)
            {
                _token = messagingComponent.Create(this);
                _ = _token.RegisterUntargeted<TestUntargetedMessage>(OnUntargetedMessage);
                _token.Enable();
            }

            private void OnUntargetedMessage(ref TestUntargetedMessage message)
            {
                ReceivedCount++;
            }

            private void OnDestroy()
            {
                _token?.Disable();
            }
        }

        private sealed class TestScriptableMessageBusProvider : ScriptableMessageBusProvider
        {
            private IMessageBus _bus;

            public void Configure(IMessageBus bus)
            {
                _bus = bus;
            }

            public override IMessageBus Resolve()
            {
                return _bus;
            }
        }

        private sealed class TestProvider : IMessageBusProvider
        {
            private readonly IMessageBus _messageBus;

            public TestProvider(IMessageBus messageBus)
            {
                _messageBus = messageBus;
            }

            public IMessageBus Resolve()
            {
                return _messageBus;
            }
        }

        private readonly struct TestUntargetedMessage : IUntargetedMessage
        {
            public TestUntargetedMessage(int value)
            {
                Value = value;
            }

            public int Value { get; }
        }
    }
}

#endif
