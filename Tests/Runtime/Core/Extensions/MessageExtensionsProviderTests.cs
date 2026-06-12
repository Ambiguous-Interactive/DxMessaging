namespace DxMessaging.Tests.Runtime.Core.Extensions
{
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;
    using MessageBus = DxMessaging.Core.MessageBus.MessageBus;

    [TestFixture]
    public sealed class MessageExtensionsProviderTests
    {
        private GlobalBusScope _globalBusScope;

        [SetUp]
        public void SetUp()
        {
            _globalBusScope = GlobalBusScope.CaptureAndReset();
        }

        [TearDown]
        public void TearDown()
        {
            _globalBusScope?.Dispose();
            _globalBusScope = null;
        }

        [Test]
        public void GlobalMessageBusProviderReturnsGlobalSingleton()
        {
            MessageBus customBus = new();
            MessageHandler.SetGlobalMessageBus(customBus);

            IMessageBus resolved = GlobalMessageBusProvider.Instance.Resolve();

            Assert.AreSame(customBus, resolved);
        }

        [Test]
        public void EmitUntargetedWithProviderUsesProvidedBus()
        {
            MessageBus providerBus = new();
            MessageHandler handler = new(new InstanceId(101), providerBus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, providerBus);
            int providerCount = 0;
            _ = token.RegisterUntargeted((ref TestUntargetedMessage _) => providerCount++);
            token.Enable();

            MessageHandler globalHandler = new(new InstanceId(102)) { active = true };
            MessageRegistrationToken globalToken = MessageRegistrationToken.Create(
                globalHandler,
                MessageHandler.MessageBus
            );
            int globalCount = 0;
            _ = globalToken.RegisterUntargeted((ref TestUntargetedMessage _) => globalCount++);
            globalToken.Enable();

            TestUntargetedMessage message = new(5);
            TestMessageBusProvider provider = new(providerBus);
            message.EmitUntargeted(messageBusProvider: provider);

            Assert.AreEqual(1, providerCount);
            Assert.AreEqual(0, globalCount);

            token.Disable();
            globalToken.Disable();
        }

        [Test]
        public void EmitUntargetedWithNullProviderFallsBackToGlobalBus()
        {
            MessageHandler globalHandler = new(new InstanceId(201)) { active = true };
            MessageRegistrationToken globalToken = MessageRegistrationToken.Create(
                globalHandler,
                MessageHandler.MessageBus
            );
            int globalCount = 0;
            _ = globalToken.RegisterUntargeted((ref TestUntargetedMessage _) => globalCount++);
            globalToken.Enable();

            TestUntargetedMessage message = new(9);
            NullMessageBusProvider provider = new();
            message.EmitUntargeted(messageBusProvider: provider);

            Assert.AreEqual(1, provider.ResolveCount);
            Assert.AreEqual(1, globalCount);

            globalToken.Disable();
        }

        [Test]
        public void EmitUntargetedPrefersExplicitBusOverProvider()
        {
            MessageBus explicitBus = new();
            MessageBus providerBus = new();

            MessageHandler explicitHandler = new(new InstanceId(301), explicitBus)
            {
                active = true,
            };
            MessageRegistrationToken explicitToken = MessageRegistrationToken.Create(
                explicitHandler,
                explicitBus
            );
            int explicitCount = 0;
            _ = explicitToken.RegisterUntargeted((ref TestUntargetedMessage _) => explicitCount++);
            explicitToken.Enable();

            MessageHandler providerHandler = new(new InstanceId(302), providerBus)
            {
                active = true,
            };
            MessageRegistrationToken providerToken = MessageRegistrationToken.Create(
                providerHandler,
                providerBus
            );
            int providerCount = 0;
            _ = providerToken.RegisterUntargeted((ref TestUntargetedMessage _) => providerCount++);
            providerToken.Enable();

            TestUntargetedMessage message = new(11);
            TestMessageBusProvider provider = new(providerBus);
            message.EmitUntargeted(explicitBus, provider);

            Assert.AreEqual(1, explicitCount);
            Assert.AreEqual(0, providerCount);

            explicitToken.Disable();
            providerToken.Disable();
        }

        [Test]
        public void EmitTargetedWithProviderRoutesToProviderBus()
        {
            MessageBus providerBus = new();
            InstanceId target = new(901);

            MessageHandler handler = new(new InstanceId(401), providerBus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, providerBus);
            int providerCount = 0;
            _ = token.RegisterTargeted(target, (ref TestTargetedMessage _) => providerCount++);
            token.Enable();

            MessageHandler globalHandler = new(new InstanceId(402)) { active = true };
            MessageRegistrationToken globalToken = MessageRegistrationToken.Create(
                globalHandler,
                MessageHandler.MessageBus
            );
            int globalCount = 0;
            _ = globalToken.RegisterTargeted(target, (ref TestTargetedMessage _) => globalCount++);
            globalToken.Enable();

            TestTargetedMessage message = new(17);
            TestMessageBusProvider provider = new(providerBus);
            message.EmitTargeted(target, messageBusProvider: provider);

            Assert.AreEqual(1, providerCount);
            Assert.AreEqual(0, globalCount);

            token.Disable();
            globalToken.Disable();
        }

        [Test]
        public void EmitBroadcastWithProviderRoutesToProviderBus()
        {
            MessageBus providerBus = new();
            InstanceId source = new(777);

            MessageHandler handler = new(new InstanceId(501), providerBus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, providerBus);
            int providerCount = 0;
            _ = token.RegisterBroadcast(source, (ref TestBroadcastMessage _) => providerCount++);
            token.Enable();

            MessageHandler globalHandler = new(new InstanceId(502)) { active = true };
            MessageRegistrationToken globalToken = MessageRegistrationToken.Create(
                globalHandler,
                MessageHandler.MessageBus
            );
            int globalCount = 0;
            _ = globalToken.RegisterBroadcast(
                source,
                (ref TestBroadcastMessage _) => globalCount++
            );
            globalToken.Enable();

            TestBroadcastMessage message = new(23);
            TestMessageBusProvider provider = new(providerBus);
            message.EmitBroadcast(source, messageBusProvider: provider);

            Assert.AreEqual(1, providerCount);
            Assert.AreEqual(0, globalCount);

            token.Disable();
            globalToken.Disable();
        }

        [Test]
        public void EmitFromWithProviderRoutesToProviderBus()
        {
            MessageBus providerBus = new();
            InstanceId source = new(778);

            MessageHandler handler = new(new InstanceId(503), providerBus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, providerBus);
            int providerCount = 0;
            _ = token.RegisterBroadcast(source, (ref TestBroadcastMessage _) => providerCount++);
            token.Enable();

            MessageHandler globalHandler = new(new InstanceId(504)) { active = true };
            MessageRegistrationToken globalToken = MessageRegistrationToken.Create(
                globalHandler,
                MessageHandler.MessageBus
            );
            int globalCount = 0;
            _ = globalToken.RegisterBroadcast(
                source,
                (ref TestBroadcastMessage _) => globalCount++
            );
            globalToken.Enable();

            TestBroadcastMessage message = new(24);
            TestMessageBusProvider provider = new(providerBus);
            message.EmitFrom(source, messageBusProvider: provider);

            Assert.AreEqual(1, providerCount);
            Assert.AreEqual(0, globalCount);

            token.Disable();
            globalToken.Disable();
        }

        [Test]
        public void EmitFromPrefersExplicitBusOverProvider()
        {
            MessageBus explicitBus = new();
            MessageBus providerBus = new();
            InstanceId source = new(779);

            MessageHandler explicitHandler = new(new InstanceId(505), explicitBus)
            {
                active = true,
            };
            MessageRegistrationToken explicitToken = MessageRegistrationToken.Create(
                explicitHandler,
                explicitBus
            );
            int explicitCount = 0;
            _ = explicitToken.RegisterBroadcast(
                source,
                (ref TestBroadcastMessage _) => explicitCount++
            );
            explicitToken.Enable();

            MessageHandler providerHandler = new(new InstanceId(506), providerBus)
            {
                active = true,
            };
            MessageRegistrationToken providerToken = MessageRegistrationToken.Create(
                providerHandler,
                providerBus
            );
            int providerCount = 0;
            _ = providerToken.RegisterBroadcast(
                source,
                (ref TestBroadcastMessage _) => providerCount++
            );
            providerToken.Enable();

            TestBroadcastMessage message = new(25);
            TestMessageBusProvider provider = new(providerBus);
            message.EmitFrom(source, explicitBus, provider);

            Assert.AreEqual(1, explicitCount);
            Assert.AreEqual(0, providerCount);

            explicitToken.Disable();
            providerToken.Disable();
        }

        [Test]
        public void StringEmitUsesProvidedBus()
        {
            MessageBus providerBus = new();
            InstanceId target = new(1234);

            MessageHandler handler = new(new InstanceId(601), providerBus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, providerBus);
            string received = null;
            _ = token.RegisterTargeted(target, (ref StringMessage m) => received = m.message);
            token.Enable();

            MessageHandler globalHandler = new(new InstanceId(602)) { active = true };
            MessageRegistrationToken globalToken = MessageRegistrationToken.Create(
                globalHandler,
                MessageHandler.MessageBus
            );
            string globalReceived = null;
            _ = globalToken.RegisterTargeted(
                target,
                (ref StringMessage m) => globalReceived = m.message
            );
            globalToken.Enable();

            TestMessageBusProvider provider = new(providerBus);
            "provider-route".Emit(target, messageBusProvider: provider);

            Assert.AreEqual("provider-route", received);
            Assert.IsNull(globalReceived);

            token.Disable();
            globalToken.Disable();
        }

        [Test]
        public void StringEmitFromUsesProvidedBus()
        {
            MessageBus providerBus = new();
            InstanceId source = new(1235);

            MessageHandler handler = new(new InstanceId(603), providerBus) { active = true };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, providerBus);
            string received = null;
            _ = token.RegisterBroadcast(
                source,
                (ref SourcedStringMessage m) => received = m.message
            );
            token.Enable();

            MessageHandler globalHandler = new(new InstanceId(604)) { active = true };
            MessageRegistrationToken globalToken = MessageRegistrationToken.Create(
                globalHandler,
                MessageHandler.MessageBus
            );
            string globalReceived = null;
            _ = globalToken.RegisterBroadcast(
                source,
                (ref SourcedStringMessage m) => globalReceived = m.message
            );
            globalToken.Enable();

            TestMessageBusProvider provider = new(providerBus);
            "provider-broadcast".EmitFrom(source, messageBusProvider: provider);

            Assert.AreEqual("provider-broadcast", received);
            Assert.IsNull(globalReceived);

            token.Disable();
            globalToken.Disable();
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

        private sealed class NullMessageBusProvider : IMessageBusProvider
        {
            public int ResolveCount { get; private set; }

            public IMessageBus Resolve()
            {
                ResolveCount++;
                return null;
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

        private readonly struct TestTargetedMessage : ITargetedMessage
        {
            public TestTargetedMessage(int value)
            {
                Value = value;
            }

            public int Value { get; }
        }

        private readonly struct TestBroadcastMessage : IBroadcastMessage
        {
            public TestBroadcastMessage(int value)
            {
                Value = value;
            }

            public int Value { get; }
        }
    }
}
