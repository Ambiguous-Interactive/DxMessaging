#if UNITY_EDITOR && UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Editor
{
    using System.Collections.Generic;
    using DxMessaging.Core;
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

            foreach (string assetPath in _createdAssetPaths)
            {
                if (!string.IsNullOrEmpty(assetPath))
                {
                    AssetDatabase.DeleteAsset(assetPath);
                }
            }
            _createdAssetPaths.Clear();
        }

        /// <remarks>
        /// Investigation (2026-08-10): teardown formerly refreshed the entire AssetDatabase
        /// after deleting its temporary provider. DeleteAsset already completes that owned
        /// cleanup; the global refresh also imported unrelated malformed host assets and made
        /// this isolated serialization contract fail on project state it did not create.
        /// </remarks>
        [Test]
        public void SerializedProviderHandleSurvivesJsonRoundtrip()
        {
            CurrentGlobalMessageBusProvider provider = Track(
                ScriptableObject.CreateInstance<CurrentGlobalMessageBusProvider>()
            );
            string assetPath = AssetDatabase.GenerateUniqueAssetPath(
                "Assets/__TempTestProvider.asset"
            );
            _createdAssetPaths.Add(assetPath);
            AssetDatabase.CreateAsset(provider, assetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(assetPath);

            GameObject owner = Track(
                EditorUtility.CreateGameObjectWithHideFlags(
                    "OriginalComponentOwner",
                    HideFlags.HideAndDontSave
                )
            );
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

            GameObject cloneOwner = Track(
                EditorUtility.CreateGameObjectWithHideFlags(
                    "ClonedComponentOwner",
                    HideFlags.HideAndDontSave
                )
            );
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
                MessageHandler.MessageBus,
                resolvedBus,
                "The deserialized provider should retain its production resolution behavior."
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
    }
}
#endif
