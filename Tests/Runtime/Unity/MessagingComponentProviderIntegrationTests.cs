#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Unity
{
    using System.Collections;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Runtime;
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
        public void InstallerExplicitBusOverridesProviderForChildren()
        {
            MessageBus providerBus = new();
            MessageBus explicitBus = new();
            TestProvider provider = new(providerBus);

            GameObject root = Track(new GameObject("InstallerPrecedenceRoot"));
            MessagingComponentInstaller installer =
                root.AddComponent<MessagingComponentInstaller>();
            installer.SetProvider(MessageBusProviderHandle.FromProvider(provider));
            installer.SetExplicitMessageBus(explicitBus);

            GameObject child = Track(new GameObject("InstallerPrecedenceChild"));
            child.transform.SetParent(root.transform);
            MessagingComponent messagingComponent = child.AddComponent<MessagingComponent>();

            installer.ApplyConfiguration();

            IMessageRegistrationBuilder builder = messagingComponent.CreateRegistrationBuilder();
            using (
                MessageRegistrationLease lease = builder.Build(
                    new MessageRegistrationBuildOptions()
                )
            )
            {
                Assert.AreSame(
                    explicitBus,
                    lease.MessageBus,
                    "The explicit installer bus should override its configured provider."
                );
            }
        }

        [Test]
        public void InstallerRegistrationBuilderPrefersExplicitBusOverProvider()
        {
            MessageBus providerBus = new();
            MessageBus explicitBus = new();
            TestProvider provider = new(providerBus);

            GameObject owner = Track(new GameObject("InstallerBuilderPrecedenceOwner"));
            MessagingComponentInstaller installer =
                owner.AddComponent<MessagingComponentInstaller>();
            installer.SetProvider(MessageBusProviderHandle.FromProvider(provider));
            installer.SetExplicitMessageBus(explicitBus);

            IMessageRegistrationBuilder builder = installer.CreateRegistrationBuilder();
            using (
                MessageRegistrationLease lease = builder.Build(
                    new MessageRegistrationBuildOptions()
                )
            )
            {
                Assert.AreSame(
                    explicitBus,
                    lease.MessageBus,
                    "The installer builder should use the explicit bus before its provider."
                );
            }
        }

        [Test]
        public void InstallerRegistrationBuilderFallsBackToProviderAfterExplicitBusCleared()
        {
            MessageBus providerBus = new();
            TestProvider provider = new(providerBus);

            GameObject owner = Track(new GameObject("InstallerBuilderFallbackOwner"));
            MessagingComponentInstaller installer =
                owner.AddComponent<MessagingComponentInstaller>();
            installer.SetProvider(MessageBusProviderHandle.FromProvider(provider));
            installer.SetExplicitMessageBus(new MessageBus());
            installer.SetExplicitMessageBus(null);

            IMessageRegistrationBuilder builder = installer.CreateRegistrationBuilder();
            using (
                MessageRegistrationLease lease = builder.Build(
                    new MessageRegistrationBuildOptions()
                )
            )
            {
                Assert.AreSame(providerBus, lease.MessageBus);
            }
        }

        [Test]
        public void CreateRegistrationBuilderTracksConfiguredProviderAvailability()
        {
            MessageBus providerBus = new();
            TestScriptableMessageBusProvider provider = Track(
                ScriptableObject.CreateInstance<TestScriptableMessageBusProvider>()
            );
            provider.Configure(providerBus);

            GameObject owner = Track(new GameObject("BuilderOwner"));
            MessagingComponent messagingComponent = owner.AddComponent<MessagingComponent>();
            messagingComponent.Configure(provider, MessageBusRebindMode.RebindActive);

            AssertRegistrationBuilderUsesBus(
                messagingComponent,
                providerBus,
                "A live configured provider should supply the registration bus."
            );

            Object.DestroyImmediate(provider);

            AssertRegistrationBuilderUsesBus(
                messagingComponent,
                null,
                "A destroyed configured provider should use the builder's global-bus fallback."
            );

            MessageBus serializedBus = new();
            TestScriptableMessageBusProvider serializedProvider = Track(
                ScriptableObject.CreateInstance<TestScriptableMessageBusProvider>()
            );
            serializedProvider.Configure(serializedBus);
            MessageBus runtimeBus = new();
            TestScriptableMessageBusProvider runtimeProvider = Track(
                ScriptableObject.CreateInstance<TestScriptableMessageBusProvider>()
            );
            runtimeProvider.Configure(runtimeBus);
            MessageBusProviderHandle handle = new MessageBusProviderHandle(
                serializedProvider
            ).WithRuntimeProvider(runtimeProvider);
            messagingComponent.Configure(handle, MessageBusRebindMode.RebindActive);

            AssertRegistrationBuilderUsesBus(
                messagingComponent,
                runtimeBus,
                "A live runtime provider should override the serialized provider."
            );

            Object.DestroyImmediate(runtimeProvider);

            AssertRegistrationBuilderUsesBus(
                messagingComponent,
                serializedBus,
                "Destroying the runtime override should reveal the serialized provider."
            );
        }

        private static void AssertRegistrationBuilderUsesBus(
            MessagingComponent messagingComponent,
            IMessageBus expectedBus,
            string message
        )
        {
            IMessageRegistrationBuilder builder = messagingComponent.CreateRegistrationBuilder();
            using (
                MessageRegistrationLease lease = builder.Build(
                    new MessageRegistrationBuildOptions()
                )
            )
            {
                Assert.AreSame(expectedBus, lease.MessageBus, message);
                Assert.IsFalse(
                    lease.Token.Enabled,
                    "A newly built registration token should remain disabled by default."
                );
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

            AssertClearingProviderConfigurationRevertsToGlobalBus(
                ProviderConfigurationClearKind.NullProvider
            );
            AssertClearingProviderConfigurationRevertsToGlobalBus(
                ProviderConfigurationClearKind.EmptyHandle
            );
        }

        private void AssertClearingProviderConfigurationRevertsToGlobalBus(
            ProviderConfigurationClearKind clearKind
        )
        {
            MessageBus previousExplicitBus = new();
            IMessageBus globalBus = MessageHandler.MessageBus;
            Assert.IsNotNull(globalBus);
            int received = 0;
            int receivedFromPreviousBus;
            int receivedAfterGlobalBus;

            GameObject owner = Track(new GameObject("ProviderConfigurationClearOwner"));
            MessagingComponent messagingComponent = owner.AddComponent<MessagingComponent>();
            messagingComponent.Configure(previousExplicitBus, MessageBusRebindMode.RebindActive);

            ClearProviderConfiguration(messagingComponent, clearKind);

            using (
                LeakWatcher globalWatcher = new(
                    globalBus,
                    label: $"MessagingComponentClear-{clearKind}-global"
                )
            )
            using (
                LeakWatcher previousWatcher = new(
                    previousExplicitBus,
                    label: $"MessagingComponentClear-{clearKind}-previous"
                )
            )
            using (
                MessageRegistrationLease lease = messagingComponent
                    .CreateRegistrationBuilder()
                    .Build(
                        new MessageRegistrationBuildOptions
                        {
                            ActivateOnBuild = true,
                            Configure = token =>
                                _ = token.RegisterUntargeted<TestUntargetedMessage>(
                                    (in TestUntargetedMessage _) => ++received
                                ),
                        }
                    )
            )
            {
                TestUntargetedMessage message = new(1);
                previousExplicitBus.UntargetedBroadcast(ref message);
                receivedFromPreviousBus = received;

                message = new TestUntargetedMessage(2);
                globalBus.UntargetedBroadcast(ref message);
                receivedAfterGlobalBus = received;
            }

            Assert.AreEqual(
                0,
                receivedFromPreviousBus,
                $"Clearing with {clearKind} should detach the prior explicit bus."
            );
            Assert.AreEqual(
                1,
                receivedAfterGlobalBus,
                $"Clearing with {clearKind} should restore the global bus."
            );
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

        private static void ClearProviderConfiguration(
            MessagingComponent messagingComponent,
            ProviderConfigurationClearKind clearKind
        )
        {
            switch (clearKind)
            {
                case ProviderConfigurationClearKind.NullProvider:
                    messagingComponent.Configure(
                        (IMessageBusProvider)null,
                        MessageBusRebindMode.RebindActive
                    );
                    return;
                case ProviderConfigurationClearKind.EmptyHandle:
                    messagingComponent.Configure(
                        MessageBusProviderHandle.Empty,
                        MessageBusRebindMode.RebindActive
                    );
                    return;
                default:
                    Assert.Fail($"Unsupported provider configuration clear kind: {clearKind}.");
                    return;
            }
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

            private void OnUntargetedMessage(in TestUntargetedMessage message)
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

        private enum ProviderConfigurationClearKind
        {
            NullProvider,
            EmptyHandle,
        }
    }
}

#endif
