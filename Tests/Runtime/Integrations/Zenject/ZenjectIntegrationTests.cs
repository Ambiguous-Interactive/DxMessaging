#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Zenject
{
#if ZENJECT_PRESENT
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using DxMessaging.Unity;
    using DxMessaging.Unity.Integrations.Zenject;
    using global::Zenject;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class ZenjectIntegrationTests : UnityFixtureBase
    {
        [Test]
        public void InstallerBindsMessageBusAndConfiguratorAppliesIt()
        {
            DiContainer container = new();
            container.BindInterfacesAndSelfTo<MessageBus>().AsSingle();

            IMessageBus resolvedBus = container.Resolve<IMessageBus>();
            Assert.IsNotNull(resolvedBus, "Zenject installer should bind IMessageBus.");

            GameObject go = Track(
                new GameObject(
                    nameof(InstallerBindsMessageBusAndConfiguratorAppliesIt),
                    typeof(MessagingComponent),
                    typeof(ZenjectConfiguredListener)
                )
            );

            ZenjectConfiguredListener listener = go.GetComponent<ZenjectConfiguredListener>();
            listener.Initialize(resolvedBus);

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted(resolvedBus);

            Assert.AreEqual(
                1,
                listener.ReceivedCount,
                "Listener should observe messages emitted through the container-provided bus."
            );
        }

        [Test]
        public void RegistrationInstallerProvidesBuilderBoundToContainerBus()
        {
            DiContainer container = new();
            container.BindInterfacesAndSelfTo<MessageBus>().AsSingle();

            DxMessagingRegistrationInstaller installer = Track(
                    new GameObject(nameof(RegistrationInstallerProvidesBuilderBoundToContainerBus))
                )
                .AddComponent<DxMessagingRegistrationInstaller>();
            installer.RunInstallBindings(container);

            IMessageRegistrationBuilder registrationBuilder =
                container.Resolve<IMessageRegistrationBuilder>();
            Assert.NotNull(registrationBuilder);

            using MessageRegistrationLease lease = registrationBuilder.Build(
                new MessageRegistrationBuildOptions()
            );
            Assert.AreSame(
                container.Resolve<IMessageBus>(),
                lease.MessageBus,
                "Builder resolved by the installer should default to the container-provided bus."
            );
        }

        [Test]
        public void RegistrationInstallerPrefersBoundProvider()
        {
            DiContainer container = new();
            MessageBus providerBus = new();
            container
                .Bind<IMessageBusProvider>()
                .FromInstance(new FixedMessageBusProvider(providerBus))
                .AsSingle();
            container.BindInterfacesAndSelfTo<MessageBus>().AsSingle();

            DxMessagingRegistrationInstaller installer = Track(
                    new GameObject(nameof(RegistrationInstallerPrefersBoundProvider))
                )
                .AddComponent<DxMessagingRegistrationInstaller>();
            installer.RunInstallBindings(container);

            IMessageRegistrationBuilder registrationBuilder =
                container.Resolve<IMessageRegistrationBuilder>();
            using MessageRegistrationLease lease = registrationBuilder.Build(
                new MessageRegistrationBuildOptions()
            );

            Assert.AreSame(
                providerBus,
                lease.MessageBus,
                "Builder should prefer the container-provided IMessageBusProvider when available."
            );
        }

        [Test]
        public void BindDxMessagingBusExposesBothContracts()
        {
            DiContainer container = new();
            container.BindDxMessagingBus();

            MessageBus concrete = container.Resolve<MessageBus>();
            IMessageBus iFace = container.Resolve<IMessageBus>();

            Assert.AreSame(
                concrete,
                iFace,
                "Resolving MessageBus and IMessageBus through the helper should yield the same singleton."
            );
        }

        [Test]
        public void BindDxMessagingBusExposesBothContractsWhenInterfaceResolvesFirst()
        {
            DiContainer container = new();
            container.BindDxMessagingBus();

            IMessageBus iFace = container.Resolve<IMessageBus>();
            MessageBus concrete = container.Resolve<MessageBus>();

            Assert.AreSame(
                iFace,
                concrete,
                "Resolving IMessageBus before MessageBus through the helper should yield the same singleton."
            );
        }

        [Test]
        public void BindDxMessagingBusWithFactoryUsesProvidedInstance()
        {
            MessageBus expected = new MessageBus();
            int factoryCalls = 0;
            bool contextWasProvided = false;
            DiContainer container = new();
            container.BindDxMessagingBus(context =>
            {
                ++factoryCalls;
                contextWasProvided = context != null;
                return expected;
            });

            MessageBus bus = container.Resolve<MessageBus>();
            IMessageBus iFace = container.Resolve<IMessageBus>();

            Assert.AreSame(
                expected,
                bus,
                "Factory overload should construct the bus exactly as the caller supplied."
            );
            Assert.AreSame(
                bus,
                iFace,
                "Factory overload should expose one singleton through MessageBus and IMessageBus."
            );
            Assert.AreEqual(
                1,
                factoryCalls,
                "Singleton factory should run once even when both MessageBus and IMessageBus are resolved."
            );
            Assert.IsTrue(
                contextWasProvided,
                "Factory overload should pass the active Zenject inject context to the caller."
            );
        }

        [Test]
        public void BindDxMessagingBusWithFactoryUsesProvidedInstanceWhenInterfaceResolvesFirst()
        {
            MessageBus expected = new MessageBus();
            int factoryCalls = 0;
            bool contextWasProvided = false;
            DiContainer container = new();
            container.BindDxMessagingBus(context =>
            {
                ++factoryCalls;
                contextWasProvided = context != null;
                return expected;
            });

            IMessageBus iFace = container.Resolve<IMessageBus>();
            MessageBus bus = container.Resolve<MessageBus>();

            Assert.AreSame(
                expected,
                iFace,
                "Factory overload should return the caller-supplied bus when IMessageBus resolves first."
            );
            Assert.AreSame(
                iFace,
                bus,
                "Factory overload should expose one singleton through IMessageBus and MessageBus."
            );
            Assert.AreEqual(
                1,
                factoryCalls,
                "Singleton factory should run once even when IMessageBus resolves before MessageBus."
            );
            Assert.IsTrue(
                contextWasProvided,
                "Factory overload should pass the active Zenject inject context to the caller."
            );
        }

        [Test]
        public void BindDxMessagingBusWithClockUsesInjectedClock()
        {
            FakeClock clock = new FakeClock(initialSeconds: 5d);
            DiContainer container = new();
            container.BindDxMessagingBus(clock);

            MessageBus bus = container.Resolve<MessageBus>();
            IMessageBus iFace = container.Resolve<IMessageBus>();

            Assert.AreEqual(
                5d,
                clock.NowSeconds,
                "Helper should construct the bus through CreateForInternalUse without advancing the clock."
            );
            Assert.GreaterOrEqual(
                clock.ReadCount,
                2,
                "Resolving the bus should read the injected clock during MessageBus construction, and the assertion reads it once more for diagnostics."
            );
            Assert.AreSame(
                bus,
                iFace,
                "Clock overload should expose the same bus through MessageBus and IMessageBus."
            );
            Assert.NotNull(bus);
        }

        [Test]
        public void BindDxMessagingBusWithClockUsesInjectedClockWhenInterfaceResolvesFirst()
        {
            FakeClock clock = new FakeClock(initialSeconds: 5d);
            DiContainer container = new();
            container.BindDxMessagingBus(clock);

            IMessageBus iFace = container.Resolve<IMessageBus>();
            MessageBus bus = container.Resolve<MessageBus>();

            Assert.AreSame(
                iFace,
                bus,
                "Clock overload should expose the same bus through IMessageBus and MessageBus."
            );
            Assert.AreEqual(
                5d,
                clock.NowSeconds,
                "Clock overload should construct the bus through CreateForInternalUse without advancing the clock."
            );
            Assert.GreaterOrEqual(
                clock.ReadCount,
                2,
                "Resolving the bus should read the injected clock during MessageBus construction, and the assertion reads it once more for diagnostics."
            );
        }

        private sealed class ZenjectConfiguredListener : MonoBehaviour
        {
            private IMessageBus _messageBus;
            private MessageRegistrationToken _token;

            internal int ReceivedCount { get; private set; }

            internal void Initialize(IMessageBus messageBus)
            {
                _messageBus = messageBus;
                MessagingComponent messagingComponent = GetComponent<MessagingComponent>();
                messagingComponent.Configure(_messageBus, MessageBusRebindMode.RebindActive);

                _token = messagingComponent.Create(this);
                _ = _token.RegisterUntargeted<SimpleUntargetedMessage>(_ => ++ReceivedCount);
                _token.Enable();
            }

            private void OnDestroy()
            {
                _token?.Disable();
                _token = null;
                _messageBus = null;
            }
        }

        private sealed class FixedMessageBusProvider : IMessageBusProvider
        {
            private readonly IMessageBus _bus;

            public FixedMessageBusProvider(IMessageBus bus)
            {
                _bus = bus;
            }

            public IMessageBus Resolve()
            {
                return _bus;
            }
        }
    }
#endif
}

#endif
