#if UNITY_EDITOR && UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Editor
{
    using System.Collections.Generic;
    using Core;
    using Core.MessageBus;
    using Core.Messages;
    using DxMessaging.Editor.Testing;
    using DxMessaging.Unity;
    using NUnit.Framework;
    using UnityEditor;
    using UnityEngine;
    using Object = UnityEngine.Object;

    [TestFixture]
    public sealed class MessagingComponentEditorHarnessTests
    {
        private readonly List<Object> _createdObjects = new();

        [TearDown]
        public void TearDown()
        {
            foreach (Object instance in _createdObjects)
            {
                if (instance != null)
                {
                    Object.DestroyImmediate(instance);
                }
            }
            _createdObjects.Clear();

            if (MessageHandler.MessageBus is MessageBus messageBus)
            {
                messageBus.DiagnosticsMode = false;
                messageBus._emissionBuffer.Clear();
            }
        }

        [Test]
        public void CaptureReflectsListenerDiagnostics()
        {
            GameObject host = CreateTrackedObject("HarnessHost");
            MessagingComponent messagingComponent = host.AddComponent<MessagingComponent>();
            TestListener listener = host.AddComponent<TestListener>();

            MessageRegistrationToken token = messagingComponent.Create(listener);
            token.DiagnosticMode = true;
            token.RegisterUntargeted<TestHarnessMessage>(listener.OnMessage);
            token.Enable();

            MessageBus messageBus = MessageHandler.MessageBus as MessageBus;
            Assert.That(
                messageBus,
                Is.Not.Null,
                "Default message bus should be the DxMessaging MessageBus implementation."
            );
            messageBus.DiagnosticsMode = true;
            messageBus._emissionBuffer.Clear();

            try
            {
                TestHarnessMessage message = default;
                MessageHandler.MessageBus.UntargetedBroadcast(ref message);

                MessagingComponentInspectorState state = MessagingComponentEditorHarness.Capture(
                    messagingComponent
                );

                Assert.That(state.GlobalDiagnosticsEnabled, Is.True);
                Assert.That(
                    state.GlobalEmissionHistory.Count,
                    Is.GreaterThan(0),
                    "Global emission buffer should contain recorded messages."
                );
                Assert.That(state.Listeners.Count, Is.EqualTo(1));

                ListenerDiagnosticsView listenerView = state.Listeners[0];
                Assert.That(listenerView.Listener, Is.EqualTo(listener));
                Assert.That(listenerView.DiagnosticsEnabled, Is.True);
                Assert.That(listenerView.TokenEnabled, Is.True);
                Assert.That(listenerView.Registrations.Count, Is.EqualTo(1));
                Assert.That(listenerView.Registrations[0].CallCount, Is.EqualTo(1));
                Assert.That(listenerView.EmissionHistory.Count, Is.GreaterThan(0));
            }
            finally
            {
                token.Disable();
                messageBus.DiagnosticsMode = false;
            }
        }

        [Test]
        public void AutoConfigureWithoutSerializedProviderEmitsWarning()
        {
            GameObject host = CreateTrackedObject("ProviderWarningHost");
            MessagingComponent messagingComponent = host.AddComponent<MessagingComponent>();

            SerializedObject serializedObject = new(messagingComponent);
            SerializedProperty autoConfigureProperty = serializedObject.FindProperty(
                "autoConfigureSerializedProviderOnAwake"
            );
            Assert.That(autoConfigureProperty, Is.Not.Null);
            autoConfigureProperty.boolValue = true;
            serializedObject.ApplyModifiedPropertiesWithoutUndo();

            MessagingComponentInspectorState state = MessagingComponentEditorHarness.Capture(
                messagingComponent
            );

            Assert.That(
                state.ProviderDiagnostics.SerializedProviderMissingWarning,
                Is.True,
                "Inspector should warn when auto-configure is enabled without a serialized provider."
            );
        }

        [Test]
        public void AssignedSerializedProviderClearsWarning()
        {
            MessageBus messageBus = new();
            TestScriptableMessageBusProvider provider = CreateTrackedObject(
                ScriptableObject.CreateInstance<TestScriptableMessageBusProvider>()
            );
            provider.Configure(messageBus);

            GameObject host = CreateTrackedObject("ProviderAssignedHost");
            MessagingComponent messagingComponent = host.AddComponent<MessagingComponent>();
            messagingComponent.Configure(
                new MessageBusProviderHandle(provider),
                MessageBusRebindMode.RebindActive
            );

            SerializedObject serializedObject = new(messagingComponent);
            SerializedProperty autoConfigureProperty = serializedObject.FindProperty(
                "autoConfigureSerializedProviderOnAwake"
            );
            Assert.That(autoConfigureProperty, Is.Not.Null);
            autoConfigureProperty.boolValue = true;
            serializedObject.ApplyModifiedPropertiesWithoutUndo();

            MessagingComponentInspectorState state = MessagingComponentEditorHarness.Capture(
                messagingComponent
            );

            Assert.That(
                state.ProviderDiagnostics.SerializedProviderMissingWarning,
                Is.False,
                "Inspector warning should clear when a serialized provider is assigned."
            );
            Assert.That(
                state.ProviderDiagnostics.SerializedProviderNullBusWarning,
                Is.False,
                "Provider should resolve a message bus without triggering the null-bus warning."
            );
        }

        [Test]
        public void NullResolvingSerializedProviderEmitsWarning()
        {
            GameObject host = CreateTrackedObject("NullProviderHost");
            MessagingComponent messagingComponent = host.AddComponent<MessagingComponent>();

            NullBusProvider provider = CreateTrackedObject(
                ScriptableObject.CreateInstance<NullBusProvider>()
            );
            messagingComponent.Configure(
                new MessageBusProviderHandle(provider),
                MessageBusRebindMode.RebindActive
            );

            SerializedObject serializedObject = new(messagingComponent);
            SerializedProperty autoConfigureProperty = serializedObject.FindProperty(
                "autoConfigureSerializedProviderOnAwake"
            );
            Assert.That(autoConfigureProperty, Is.Not.Null);
            autoConfigureProperty.boolValue = true;
            serializedObject.ApplyModifiedPropertiesWithoutUndo();

            MessagingComponentInspectorState state = MessagingComponentEditorHarness.Capture(
                messagingComponent
            );

            Assert.That(
                state.ProviderDiagnostics.SerializedProviderMissingWarning,
                Is.False,
                "Warning should not claim the provider asset is missing when assigned."
            );
            Assert.That(
                state.ProviderDiagnostics.SerializedProviderNullBusWarning,
                Is.True,
                "Inspector should warn when the serialized provider does not resolve a message bus."
            );
        }

        private GameObject CreateTrackedObject(string name)
        {
            GameObject gameObject = EditorUtility.CreateGameObjectWithHideFlags(
                name,
                HideFlags.HideAndDontSave
            );
            _createdObjects.Add(gameObject);
            return gameObject;
        }

        private T CreateTrackedObject<T>(T unityObject)
            where T : Object
        {
            if (unityObject != null)
            {
                _createdObjects.Add(unityObject);
            }
            return unityObject;
        }

        private sealed class TestListener : MonoBehaviour
        {
            public void OnMessage(in TestHarnessMessage message) { }
        }

        private readonly struct TestHarnessMessage : IUntargetedMessage { }

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

        private sealed class NullBusProvider : ScriptableMessageBusProvider
        {
            public override IMessageBus Resolve()
            {
                return null;
            }
        }
    }
}
#endif
