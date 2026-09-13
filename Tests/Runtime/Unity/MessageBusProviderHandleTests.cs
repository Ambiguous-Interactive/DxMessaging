#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Unity
{
    using System.Collections.Generic;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Unity;
    using NUnit.Framework;
    using UnityEngine;

    [TestFixture]
    public sealed class MessageBusProviderHandleTests
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

        [Test]
        public void TryGetProviderReturnsRuntimeProvider()
        {
            TestMessageBusProvider runtimeProvider = new(new MessageBus());
            MessageBusProviderHandle handle = MessageBusProviderHandle.FromProvider(
                runtimeProvider
            );

            bool resolved = handle.TryGetProvider(out IMessageBusProvider provider);

            Assert.IsTrue(resolved);
            Assert.AreSame(runtimeProvider, provider);
        }

        [Test]
        public void TryGetProviderReturnsAssetProvider()
        {
            TestScriptableMessageBusProvider providerAsset =
                ScriptableObject.CreateInstance<TestScriptableMessageBusProvider>();
            providerAsset.Configure(new MessageBus());
            MessageBusProviderHandle handle = new(providerAsset);
            TrackForCleanup(providerAsset);

            bool resolved = handle.TryGetProvider(out IMessageBusProvider provider);

            Assert.IsTrue(resolved);
            Assert.AreSame(providerAsset, provider);
        }

        [Test]
        public void DestroyedProviderAssetBecomesUnavailable()
        {
            TestScriptableMessageBusProvider providerAsset =
                ScriptableObject.CreateInstance<TestScriptableMessageBusProvider>();
            providerAsset.Configure(new MessageBus());
            MessageBusProviderHandle handle = new(providerAsset);
            TrackForCleanup(providerAsset);

            Object.DestroyImmediate(providerAsset);

            Assert.IsFalse(
                handle.TryGetProvider(out IMessageBusProvider provider),
                "A destroyed provider asset must not survive through its interface reference."
            );
            Assert.IsNull(provider);
            Assert.IsNull(handle.ResolveBus());
        }

        [Test]
        public void ResolveBusReturnsNullWhenUnassigned()
        {
            MessageBusProviderHandle handle = MessageBusProviderHandle.Empty;

            IMessageBus bus = handle.ResolveBus();

            Assert.IsNull(bus);
        }

        [Test]
        public void HandleSurvivesSerializationCycle()
        {
            TestScriptableMessageBusProvider providerAsset =
                ScriptableObject.CreateInstance<TestScriptableMessageBusProvider>();
            providerAsset.Configure(new MessageBus());
            MessageBusProviderHandle handle = new(providerAsset);
            TrackForCleanup(providerAsset);

            HandleWrapper wrapper = new() { Handle = handle };
            string json = JsonUtility.ToJson(wrapper);
            HandleWrapper deserialized = JsonUtility.FromJson<HandleWrapper>(json);

            bool resolved = deserialized.Handle.TryGetProvider(out IMessageBusProvider provider);

            Assert.IsTrue(resolved);
            Assert.AreSame(providerAsset, provider);
        }

        [Test]
        public void WithRuntimeProviderOverridesSerializedProvider()
        {
            MessageBus serializedBus = new();
            TestScriptableMessageBusProvider providerAsset =
                ScriptableObject.CreateInstance<TestScriptableMessageBusProvider>();
            providerAsset.Configure(serializedBus);
            MessageBusProviderHandle handle = new(providerAsset);
            TrackForCleanup(providerAsset);

            MessageBus runtimeBus = new();
            TestMessageBusProvider runtimeProvider = new(runtimeBus);
            MessageBusProviderHandle runtimeHandle = handle.WithRuntimeProvider(runtimeProvider);

            Assert.IsTrue(runtimeHandle.TryGetProvider(out IMessageBusProvider provider));
            Assert.AreSame(runtimeProvider, provider);
            Assert.AreSame(runtimeBus, runtimeHandle.ResolveBus());

            // Original handle should remain associated with the serialized provider asset.
            Assert.IsTrue(handle.TryGetProvider(out IMessageBusProvider serializedProvider));
            Assert.AreSame(providerAsset, serializedProvider);
            Assert.AreSame(serializedBus, handle.ResolveBus());
        }

        [Test]
        public void DestroyedRuntimeOverrideFallsBackToSerializedProvider()
        {
            MessageBus serializedBus = new();
            TestScriptableMessageBusProvider serializedProvider =
                ScriptableObject.CreateInstance<TestScriptableMessageBusProvider>();
            serializedProvider.Configure(serializedBus);
            TrackForCleanup(serializedProvider);
            TestScriptableMessageBusProvider runtimeProvider =
                ScriptableObject.CreateInstance<TestScriptableMessageBusProvider>();
            runtimeProvider.Configure(new MessageBus());
            TrackForCleanup(runtimeProvider);
            MessageBusProviderHandle handle = new MessageBusProviderHandle(
                serializedProvider
            ).WithRuntimeProvider(runtimeProvider);

            Object.DestroyImmediate(runtimeProvider);

            Assert.IsTrue(handle.TryGetProvider(out IMessageBusProvider provider));
            Assert.AreSame(
                serializedProvider,
                provider,
                "A destroyed runtime override must reveal the still-live serialized provider."
            );
            Assert.AreSame(serializedBus, handle.ResolveBus());
        }

        [Test]
        public void RuntimeProviderIsNotSerialized()
        {
            MessageBus serializedBus = new();
            TestScriptableMessageBusProvider providerAsset =
                ScriptableObject.CreateInstance<TestScriptableMessageBusProvider>();
            providerAsset.Configure(serializedBus);
            MessageBusProviderHandle handle = new(providerAsset);
            TrackForCleanup(providerAsset);

            MessageBus runtimeBus = new();
            TestMessageBusProvider runtimeProvider = new(runtimeBus);
            handle = handle.WithRuntimeProvider(runtimeProvider);

            HandleWrapper wrapper = new() { Handle = handle };
            string json = JsonUtility.ToJson(wrapper);
            HandleWrapper deserialized = JsonUtility.FromJson<HandleWrapper>(json);

            Assert.IsTrue(deserialized.Handle.TryGetProvider(out IMessageBusProvider provider));
            Assert.AreSame(
                providerAsset,
                provider,
                "Serialized handle should resolve the asset provider."
            );
            Assert.AreSame(serializedBus, deserialized.Handle.ResolveBus());
        }

        private sealed class TestMessageBusProvider : IMessageBusProvider
        {
            private readonly IMessageBus _bus;

            public TestMessageBusProvider(IMessageBus bus)
            {
                _bus = bus;
            }

            public IMessageBus Resolve()
            {
                return _bus;
            }
        }

        private void TrackForCleanup(Object unityObject)
        {
            if (unityObject != null)
            {
                _objectsToDestroy.Add(unityObject);
            }
        }

        [System.Serializable]
        private struct HandleWrapper
        {
            public MessageBusProviderHandle Handle;
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
    }
}

#endif
