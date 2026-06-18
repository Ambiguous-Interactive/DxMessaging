#if UNITY_EDITOR && UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Editor
{
    using System.Collections.Generic;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Unity;
    using NUnit.Framework;
    using UnityEditor;
    using UnityEngine;
    using Object = UnityEngine.Object;

    [TestFixture]
    public sealed class MessagingComponentSerializationTests
    {
        private readonly List<Object> _createdObjects = new();
        private readonly List<string> _createdAssetPaths = new();

        [TearDown]
        public void TearDown()
        {
            foreach (Object instance in _createdObjects)
            {
                if (instance != null && !EditorUtility.IsPersistent(instance))
                {
                    Object.DestroyImmediate(instance);
                }
            }
            _createdObjects.Clear();

            bool deletedAnyAsset = false;
            foreach (string assetPath in _createdAssetPaths)
            {
                if (!string.IsNullOrEmpty(assetPath))
                {
                    AssetDatabase.DeleteAsset(assetPath);
                    deletedAnyAsset = true;
                }
            }
            _createdAssetPaths.Clear();

            // DeleteAsset already updates the database; the Refresh is only needed
            // to settle the import state of assets this fixture actually created on
            // disk. Guarding it on deletedAnyAsset keeps that Refresh correct while
            // ensuring a future test that creates no on-disk asset pays no
            // AssetDatabase sweep. The one current test does create an asset, so the
            // Refresh still runs for it -- this guard is defensive, not a saving today.
            if (deletedAnyAsset)
            {
                AssetDatabase.Refresh();
            }
        }

        [Test]
        public void SerializedProviderHandleSurvivesJsonRoundtrip()
        {
            MessageBus messageBus = new();
            TestScriptableMessageBusProvider provider =
                ScriptableObject.CreateInstance<TestScriptableMessageBusProvider>();
            provider.Configure(messageBus);
            string assetPath = AssetDatabase.GenerateUniqueAssetPath(
                "Assets/__TempTestProvider.asset"
            );
            AssetDatabase.CreateAsset(provider, assetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(assetPath);
            _createdAssetPaths.Add(assetPath);

            GameObject owner = Track(new GameObject("OriginalComponentOwner"));
            MessagingComponent original = owner.AddComponent<MessagingComponent>();
            original.Configure(
                new MessageBusProviderHandle(provider),
                MessageBusRebindMode.RebindActive
            );

            SerializedObject originalSerialized = new(original);
            SerializedProperty autoConfigureProperty = originalSerialized.FindProperty(
                "autoConfigureSerializedProviderOnAwake"
            );
            Assert.IsNotNull(autoConfigureProperty, "Expected auto configure property to exist.");
            autoConfigureProperty.boolValue = true;
            originalSerialized.ApplyModifiedPropertiesWithoutUndo();

            string serializedJson = EditorJsonUtility.ToJson(original);
            Assert.IsFalse(
                string.IsNullOrEmpty(serializedJson),
                "JSON serialization should produce content."
            );

            GameObject cloneOwner = Track(new GameObject("ClonedComponentOwner"));
            MessagingComponent clone = cloneOwner.AddComponent<MessagingComponent>();
            EditorJsonUtility.FromJsonOverwrite(serializedJson, clone);

            Assert.IsTrue(
                clone.SerializedProviderHandle.TryGetProvider(
                    out IMessageBusProvider resolvedProvider
                ),
                "Serialized provider handle should still resolve after JSON roundtrip."
            );
            Assert.AreSame(provider, resolvedProvider, "Provider reference should be preserved.");
            Assert.AreSame(
                provider,
                clone.SerializedProviderHandle.SerializedProvider,
                "Provider asset reference should be preserved."
            );

            IMessageBus resolvedBus = clone.SerializedProviderHandle.ResolveBus();
            Assert.AreSame(
                messageBus,
                resolvedBus,
                "Resolved bus should match the original provider configuration."
            );
        }

        private T Track<T>(T unityObject)
            where T : Object
        {
            if (unityObject != null)
            {
                _createdObjects.Add(unityObject);
            }

            return unityObject;
        }

        private sealed class TestScriptableMessageBusProvider : ScriptableMessageBusProvider
        {
            private IMessageBus _bus;

            internal void Configure(IMessageBus bus)
            {
                _bus = bus;
            }

            public override IMessageBus Resolve()
            {
                return _bus;
            }
        }
    }
}
#endif
