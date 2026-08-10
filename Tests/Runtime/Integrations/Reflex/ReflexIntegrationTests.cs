#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Reflex
{
#if REFLEX_PRESENT
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using DxMessaging.Unity;
    using DxMessaging.Unity.Integrations.Reflex;
    using global::Reflex.Core;
    using global::Reflex.Injectors;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class ReflexIntegrationTests : UnityFixtureBase
    {
        [Test]
        public void InstallerRegistersMessageBusAndConfiguratorAppliesIt()
        {
            ContainerBuilder builder = new();
            DxMessagingInstaller installer = new();
            installer.InstallBindings(builder);

            Container container = TrackDisposable(builder.Build());
            IMessageBus bus = container.Resolve<IMessageBus>();
            Assert.NotNull(bus, "Reflex installer should bind IMessageBus.");

            GameObject go = Track(
                new GameObject(
                    nameof(InstallerRegistersMessageBusAndConfiguratorAppliesIt),
                    typeof(MessagingComponent),
                    typeof(ReflexConfiguredListener)
                )
            );

            ReflexConfiguredListener listener = go.GetComponent<ReflexConfiguredListener>();
            AttributeInjector.Inject(listener, container);
            listener.Initialize(bus);

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted(bus);

            Assert.AreEqual(
                1,
                listener.ReceivedCount,
                "Listener should observe messages emitted through the Reflex container's bus."
            );
        }

        [Test]
        public void RegistrationInstallerBindsBuilderAgainstContainerBus()
        {
            ContainerBuilder builder = new();
            DxMessagingInstaller coreInstaller = new();
            coreInstaller.InstallBindings(builder);

            ReflexContainerBuilderCompatibility.RegisterSingletonType(
                builder,
                typeof(StaticMessageBusProvider),
                typeof(StaticMessageBusProvider),
                typeof(IMessageBusProvider)
            );

            DxMessagingRegistrationInstaller registrationInstaller = new();
            registrationInstaller.InstallBindings(builder);

            Container container = TrackDisposable(builder.Build());

            StaticMessageBusProvider provider = container.Resolve<StaticMessageBusProvider>();

            IMessageRegistrationBuilder registrationBuilder =
                container.Resolve<IMessageRegistrationBuilder>();
            using MessageRegistrationLease lease = registrationBuilder.Build(
                new MessageRegistrationBuildOptions()
            );

            Assert.AreSame(
                provider.Bus,
                lease.MessageBus,
                "Reflex registration installer should prefer the registered IMessageBusProvider."
            );
        }

        [Test]
        public void RegistrationInstallerExposesConcreteBuilder()
        {
            ContainerBuilder builder = new();
            DxMessagingInstaller coreInstaller = new();
            coreInstaller.InstallBindings(builder);

            DxMessagingRegistrationInstaller registrationInstaller = new();
            registrationInstaller.InstallBindings(builder);

            Container container = TrackDisposable(builder.Build());

            DxMessagingRegistrationInstaller.ContainerMessageRegistrationBuilder concrete =
                container.Resolve<DxMessagingRegistrationInstaller.ContainerMessageRegistrationBuilder>();
            Assert.IsNotNull(
                concrete,
                "Container should resolve the concrete registration builder type."
            );

            IMessageRegistrationBuilder builderInterface =
                container.Resolve<IMessageRegistrationBuilder>();
            Assert.IsTrue(
                ReferenceEquals(concrete, builderInterface),
                "Container should return the same instance for concrete and interface resolutions."
            );
        }

        [Test]
        public void AddDxMessagingBusExposesBothContracts()
        {
            ContainerBuilder builder = new();
            builder.AddDxMessagingBus();

            Container container = TrackDisposable(builder.Build());
            MessageBus concrete = container.Resolve<MessageBus>();
            IMessageBus iFace = container.Resolve<IMessageBus>();

            Assert.AreSame(
                concrete,
                iFace,
                "Resolving MessageBus and IMessageBus through the helper should yield the same singleton."
            );
        }

        [Test]
        public void AddDxMessagingBusExposesBothContractsWhenInterfaceResolvesFirst()
        {
            ContainerBuilder builder = new();
            builder.AddDxMessagingBus();

            Container container = TrackDisposable(builder.Build());
            IMessageBus iFace = container.Resolve<IMessageBus>();
            MessageBus concrete = container.Resolve<MessageBus>();

            Assert.AreSame(
                iFace,
                concrete,
                "Resolving IMessageBus before MessageBus through the helper should yield the same singleton."
            );
        }

        [Test]
        public void AddDxMessagingBusWithFactoryUsesProvidedInstance()
        {
            MessageBus expected = new MessageBus();
            int factoryCalls = 0;
            bool containerWasProvided = false;
            ContainerBuilder builder = new();
            builder.AddDxMessagingBus(container =>
            {
                ++factoryCalls;
                containerWasProvided = container != null;
                return expected;
            });

            Container container = TrackDisposable(builder.Build());
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
                containerWasProvided,
                "Factory overload should pass the active Reflex container to the caller."
            );
        }

        [Test]
        public void AddDxMessagingBusWithFactoryUsesProvidedInstanceWhenInterfaceResolvesFirst()
        {
            MessageBus expected = new MessageBus();
            int factoryCalls = 0;
            bool containerWasProvided = false;
            ContainerBuilder builder = new();
            builder.AddDxMessagingBus(container =>
            {
                ++factoryCalls;
                containerWasProvided = container != null;
                return expected;
            });

            Container container = TrackDisposable(builder.Build());
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
                containerWasProvided,
                "Factory overload should pass the active Reflex container to the caller."
            );
        }

        [Test]
        public void AddDxMessagingBusWithClockUsesInjectedClock()
        {
            FakeClock clock = new FakeClock(initialSeconds: 17d);
            ContainerBuilder builder = new();
            builder.AddDxMessagingBus(clock);

            Container container = TrackDisposable(builder.Build());
            MessageBus bus = container.Resolve<MessageBus>();
            IMessageBus iFace = container.Resolve<IMessageBus>();

            Assert.AreEqual(
                17d,
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
        public void AddDxMessagingBusWithClockUsesInjectedClockWhenInterfaceResolvesFirst()
        {
            FakeClock clock = new FakeClock(initialSeconds: 17d);
            ContainerBuilder builder = new();
            builder.AddDxMessagingBus(clock);

            Container container = TrackDisposable(builder.Build());
            IMessageBus iFace = container.Resolve<IMessageBus>();
            MessageBus bus = container.Resolve<MessageBus>();

            Assert.AreSame(
                iFace,
                bus,
                "Clock overload should expose the same bus through IMessageBus and MessageBus."
            );
            Assert.AreEqual(
                17d,
                clock.NowSeconds,
                "Clock overload should construct the bus through CreateForInternalUse without advancing the clock."
            );
            Assert.GreaterOrEqual(
                clock.ReadCount,
                2,
                "Resolving the bus should read the injected clock during MessageBus construction, and the assertion reads it once more for diagnostics."
            );
        }

        private sealed class DxMessagingInstaller : IInstaller
        {
            public void InstallBindings(ContainerBuilder containerBuilder)
            {
                ReflexContainerBuilderCompatibility.RegisterSingletonType(
                    containerBuilder,
                    typeof(MessageBus),
                    typeof(MessageBus),
                    typeof(IMessageBus)
                );
            }
        }

        private sealed class ReflexConfiguredListener : MonoBehaviour
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

        private sealed class StaticMessageBusProvider : IMessageBusProvider
        {
            private readonly IMessageBus _bus = new MessageBus();

            public IMessageBus Bus => _bus;

            public IMessageBus Resolve()
            {
                return _bus;
            }
        }
    }
#endif
}

#endif
