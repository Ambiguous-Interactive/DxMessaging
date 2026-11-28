#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Unity
{
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Unity;
    using NUnit.Framework;
    using UnityEngine;

    [TestFixture]
    public sealed class MessageBusProviderAssetTests : UnityFixtureBase
    {
        [Test]
        public void GlobalProviderRespectsCurrentGlobalBus()
        {
            CurrentGlobalMessageBusProvider provider = Track(ScriptableObject.CreateInstance<CurrentGlobalMessageBusProvider>());

            MessageBus overrideBus = new();
            using (MessageHandler.OverrideGlobalMessageBus(overrideBus))
            {
                IMessageBus resolved = provider.Resolve();
                Assert.AreSame(overrideBus, resolved, "Global provider should resolve the currently configured global bus.");
            }
        }

        [Test]
        public void InitialProviderAlwaysReturnsStartupBus()
        {
            InitialGlobalMessageBusProvider provider = Track(ScriptableObject.CreateInstance<InitialGlobalMessageBusProvider>());

            IMessageBus startupBus = provider.Resolve();
            Assert.AreSame(MessageHandler.InitialGlobalMessageBus, startupBus, "Initial provider should resolve the startup global bus.");

            using (MessageHandler.OverrideGlobalMessageBus(new MessageBus()))
            {
                IMessageBus resolvedDuringOverride = provider.Resolve();
                Assert.AreSame(startupBus, resolvedDuringOverride, "Initial provider should ignore overrides and continue returning the startup bus.");
            }
        }
    }
}

#endif
