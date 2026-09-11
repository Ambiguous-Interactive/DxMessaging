#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.VContainer
{
#if VCONTAINER_PRESENT
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Pooling;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using DxMessaging.Unity;
    using DxMessaging.Unity.Integrations.VContainer;
    using global::VContainer;
    using global::VContainer.Unity;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class VContainerIntegrationTests : UnityFixtureBase
    {
        [Test]
        public void ContainerInjectsMessagingComponentWithRegisteredBus()
        {
            ContainerBuilder builder = new();
            builder.RegisterDxMessagingBus();

            IObjectResolver resolver = TrackDisposable(builder.Build());
            IMessageBus bus = resolver.Resolve<IMessageBus>();
            Assert.NotNull(bus);

            GameObject go = Track(
                new GameObject(
                    nameof(ContainerInjectsMessagingComponentWithRegisteredBus),
                    typeof(MessagingComponent),
                    typeof(VContainerConfiguredListener)
                )
            );

            resolver.InjectGameObject(go);
            VContainerConfiguredListener listener = go.GetComponent<VContainerConfiguredListener>();
            listener.Initialize(bus);

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted(bus);

            Assert.AreEqual(
                1,
                listener.ReceivedCount,
                "Listener should receive messages emitted via the VContainer-resolved bus."
            );
        }

        [Test]
        public void ContainerInjectsMessagingComponentWithFactoryRegisteredBus()
        {
            ContainerBuilder builder = new();
            builder.RegisterDxMessagingBus(_ => new MessageBus());

            IObjectResolver resolver = TrackDisposable(builder.Build());
            IMessageBus bus = resolver.Resolve<IMessageBus>();
            Assert.NotNull(bus);

            GameObject go = Track(
                new GameObject(
                    nameof(ContainerInjectsMessagingComponentWithFactoryRegisteredBus),
                    typeof(MessagingComponent),
                    typeof(VContainerConfiguredListener)
                )
            );

            resolver.InjectGameObject(go);
            VContainerConfiguredListener listener = go.GetComponent<VContainerConfiguredListener>();
            listener.Initialize(bus);

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted(bus);

            Assert.AreEqual(
                1,
                listener.ReceivedCount,
                "Listener should receive messages emitted via the factory-registered bus."
            );
        }

        [Test]
        public void ContainerInjectsMessagingComponentWithInstanceRegisteredBus()
        {
            MessageBus instance = new MessageBus();
            ContainerBuilder builder = new();
            builder.RegisterInstance(instance).As<IMessageBus>();

            IObjectResolver resolver = TrackDisposable(builder.Build());
            IMessageBus bus = resolver.Resolve<IMessageBus>();
            Assert.AreSame(instance, bus, "Resolved bus should match registered instance.");

            GameObject go = Track(
                new GameObject(
                    nameof(ContainerInjectsMessagingComponentWithInstanceRegisteredBus),
                    typeof(MessagingComponent),
                    typeof(VContainerConfiguredListener)
                )
            );

            resolver.InjectGameObject(go);
            VContainerConfiguredListener listener = go.GetComponent<VContainerConfiguredListener>();
            listener.Initialize(bus);

            SimpleUntargetedMessage message = new();
            message.EmitUntargeted(bus);

            Assert.AreEqual(
                1,
                listener.ReceivedCount,
                "Listener should receive messages emitted via the instance-registered bus."
            );
        }

        [Test]
        public void RegisterDxMessagingBusReturnsSingletonByDefault()
        {
            ContainerBuilder builder = new();
            builder.RegisterDxMessagingBus();

            IObjectResolver resolver = TrackDisposable(builder.Build());
            IMessageBus first = resolver.Resolve<IMessageBus>();
            IMessageBus second = resolver.Resolve<IMessageBus>();

            Assert.AreSame(
                first,
                second,
                "Repeated resolutions of IMessageBus should return the same singleton instance."
            );
        }

        [Test]
        public void RegisterDxMessagingBusHonoursTransientLifetime()
        {
            ContainerBuilder builder = new();
            builder.RegisterDxMessagingBus(Lifetime.Transient);

            IObjectResolver resolver = TrackDisposable(builder.Build());
            IMessageBus first = resolver.Resolve<IMessageBus>();
            IMessageBus second = resolver.Resolve<IMessageBus>();

            Assert.AreNotSame(
                first,
                second,
                "Transient lifetime should produce a new bus per resolution."
            );
        }

        [Test]
        public void RegisterDxMessagingBusHonoursScopedLifetime()
        {
            ContainerBuilder builder = new();
            builder.RegisterDxMessagingBus(Lifetime.Scoped);

            IObjectResolver resolver = TrackDisposable(builder.Build());
            IMessageBus first = resolver.Resolve<IMessageBus>();
            IMessageBus second = resolver.Resolve<IMessageBus>();

            Assert.AreSame(
                first,
                second,
                "Scoped lifetime should return the same bus within a single scope."
            );
        }

        [TestCaseSource(nameof(RegisterDxMessagingBusContractCases))]
        public void RegisterDxMessagingBusExposesSameInstanceAcrossContracts(
            BusRegistrationCase registrationCase
        )
        {
            ContainerBuilder builder = new();
            registrationCase.Register(builder);

            IObjectResolver resolver = TrackDisposable(builder.Build());
            MessageBus concrete = ResolveWithDiagnostic<MessageBus>(
                resolver,
                registrationCase.DisplayName
            );
            IMessageBus iFace = ResolveWithDiagnostic<IMessageBus>(
                resolver,
                registrationCase.DisplayName
            );

            Assert.AreSame(
                concrete,
                iFace,
                $"{registrationCase.DisplayName}: resolving MessageBus and IMessageBus should yield the same singleton."
            );
            registrationCase.Verify(concrete, iFace);
        }

        [TestCaseSource(nameof(RegisterDxMessagingBusContractCases))]
        public void RegisterDxMessagingBusExposesSameInstanceWhenInterfaceResolvesFirst(
            BusRegistrationCase registrationCase
        )
        {
            ContainerBuilder builder = new();
            registrationCase.Register(builder);

            IObjectResolver resolver = TrackDisposable(builder.Build());
            IMessageBus iFace = ResolveWithDiagnostic<IMessageBus>(
                resolver,
                registrationCase.DisplayName
            );
            MessageBus concrete = ResolveWithDiagnostic<MessageBus>(
                resolver,
                registrationCase.DisplayName
            );

            Assert.AreSame(
                iFace,
                concrete,
                $"{registrationCase.DisplayName}: resolving IMessageBus before MessageBus should still yield the same singleton."
            );
            registrationCase.Verify(concrete, iFace);
        }

        [Test]
        public void BareRegisterMessageBusFailsBecauseClockIsUnregistered()
        {
            // Pins the documented failure mode that motivates RegisterDxMessagingBus. VContainer's
            // TypeAnalyzer scans both public and non-public constructors via
            // BindingFlags.Public | BindingFlags.NonPublic, then prefers the constructor with the
            // most parameters when no [Inject] is present. Even after the production change that
            // demoted the IDxMessagingClock-taking ctor to private, the analyzer still latches onto
            // it; the dependency is not registered with the container, so resolution throws.
            // VContainer surfaces the failure either at Build time (graph validation) or at the
            // first Resolve call depending on the version, so the test wraps the entire flow.
            ContainerBuilder builder = new();
            builder.Register<MessageBus>(Lifetime.Singleton).As<IMessageBus>();

            Assert.Throws<VContainerException>(
                () =>
                {
                    IObjectResolver resolver = TrackDisposable(builder.Build());
                    _ = resolver.Resolve<IMessageBus>();
                },
                "Bare MessageBus registration must fail because VContainer reflects onto a clock-taking ctor whose IDxMessagingClock is not registered."
            );
        }

        [Test]
        public void RegisterDxMessagingBusRejectsNullArguments()
        {
            IContainerBuilder nullBuilder = null;

            AssertArgumentNull(
                () => nullBuilder.RegisterDxMessagingBus(),
                "builder",
                "Default overload should reject a null VContainer builder with the correct argument name."
            );
            AssertArgumentNull(
                () => nullBuilder.RegisterDxMessagingBus(_ => new MessageBus()),
                "builder",
                "Factory overload should reject a null VContainer builder with the correct argument name."
            );
            AssertArgumentNull(
                () => nullBuilder.RegisterDxMessagingBus(new FakeClock()),
                "builder",
                "Clock overload should reject a null VContainer builder with the correct argument name."
            );
            AssertArgumentNull(
                () => nullBuilder.RegisterMessageRegistrationBuilder(),
                "builder",
                "Builder-registration helper should reject a null VContainer builder with the correct argument name."
            );

            ContainerBuilder builder = new();
            AssertArgumentNull(
                () => builder.RegisterDxMessagingBus((Func<IObjectResolver, MessageBus>)null),
                "factory",
                "Factory overload should reject a null bus factory."
            );
            AssertArgumentNull(
                () => builder.RegisterDxMessagingBus((IDxMessagingClock)null),
                "clock",
                "Clock overload should reject a null clock."
            );
        }

        [Test]
        public void RegistrationExtensionsExposeBuilder()
        {
            ContainerBuilder builder = new();
            builder.RegisterDxMessagingBus();
            builder.RegisterMessageRegistrationBuilder();

            IObjectResolver resolver = TrackDisposable(builder.Build());
            IMessageRegistrationBuilder registrationBuilder =
                resolver.Resolve<IMessageRegistrationBuilder>();
            using MessageRegistrationLease lease = registrationBuilder.Build(
                new MessageRegistrationBuildOptions()
            );

            Assert.AreSame(
                resolver.Resolve<IMessageBus>(),
                lease.MessageBus,
                "Registration extension should resolve a builder backed by the container bus."
            );
        }

        [Test]
        public void RegistrationExtensionsPreferResolvedProvider()
        {
            ContainerBuilder builder = new();
            builder.RegisterDxMessagingBus();

            MessageBus providerBus = new MessageBus();
            builder
                .RegisterInstance<IMessageBusProvider>(new StaticMessageBusProvider(providerBus))
                .As<IMessageBusProvider>();
            builder.RegisterMessageRegistrationBuilder();

            IObjectResolver resolver = TrackDisposable(builder.Build());
            IMessageRegistrationBuilder registrationBuilder =
                resolver.Resolve<IMessageRegistrationBuilder>();
            using MessageRegistrationLease lease = registrationBuilder.Build(
                new MessageRegistrationBuildOptions()
            );

            Assert.AreSame(
                providerBus,
                lease.MessageBus,
                "Registration extensions should prefer an explicitly registered IMessageBusProvider."
            );
        }

        private sealed class VContainerConfiguredListener : MonoBehaviour, IDisposable
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

            public void Dispose()
            {
                _token?.Disable();
                _token = null;
                _messageBus = null;
            }
        }

        private sealed class StaticMessageBusProvider : IMessageBusProvider
        {
            private readonly IMessageBus _bus;

            public StaticMessageBusProvider(IMessageBus bus)
            {
                _bus = bus;
            }

            public IMessageBus Resolve()
            {
                return _bus;
            }
        }

        private static IEnumerable<TestCaseData> RegisterDxMessagingBusContractCases()
        {
            yield return new TestCaseData(
                new BusRegistrationCase(
                    "default overload",
                    builder => builder.RegisterDxMessagingBus(),
                    (_, _) => { }
                )
            );

            FakeClock clock = new FakeClock(initialSeconds: 42d);
            yield return new TestCaseData(
                new BusRegistrationCase(
                    "clock overload",
                    builder => builder.RegisterDxMessagingBus(clock),
                    (_, _) =>
                    {
                        Assert.AreEqual(
                            42d,
                            clock.NowSeconds,
                            "Clock overload should construct the bus through CreateForInternalUse without advancing the clock."
                        );
                        Assert.GreaterOrEqual(
                            clock.ReadCount,
                            2,
                            "Resolving the bus should read the injected clock during MessageBus construction, and the assertion reads it once more for diagnostics."
                        );
                    }
                )
            );

            MessageBus expected = null;
            int factoryCalls = 0;
            bool resolverWasProvided = false;
            yield return new TestCaseData(
                new BusRegistrationCase(
                    "factory overload",
                    builder =>
                    {
                        expected = new MessageBus();
                        factoryCalls = 0;
                        resolverWasProvided = false;
                        builder.RegisterDxMessagingBus(resolver =>
                        {
                            ++factoryCalls;
                            resolverWasProvided = resolver != null;
                            return expected;
                        });
                    },
                    (concrete, _) =>
                    {
                        Assert.AreSame(
                            expected,
                            concrete,
                            "Factory overload should construct the bus exactly as the caller supplied."
                        );
                        Assert.AreEqual(
                            1,
                            factoryCalls,
                            "Singleton factory should run once even when both MessageBus and IMessageBus are resolved."
                        );
                        Assert.IsTrue(
                            resolverWasProvided,
                            "Factory overload should pass the active VContainer resolver to the caller."
                        );
                    }
                )
            );
        }

        private static T ResolveWithDiagnostic<T>(
            IObjectResolver resolver,
            string registrationDisplayName
        )
        {
            try
            {
                return resolver.Resolve<T>();
            }
            catch (Exception exception)
            {
                Assert.Fail(
                    $"{registrationDisplayName}: RegisterDxMessagingBus should expose {typeof(T).FullName}, but VContainer threw: {exception}"
                );
                throw;
            }
        }

        private static void AssertArgumentNull(
            TestDelegate action,
            string expectedParameterName,
            string message
        )
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(action, message);
            Assert.AreEqual(expectedParameterName, exception.ParamName, message);
        }

        public sealed class BusRegistrationCase
        {
            public BusRegistrationCase(
                string displayName,
                Action<ContainerBuilder> register,
                Action<MessageBus, IMessageBus> verify
            )
            {
                DisplayName = displayName;
                Register = register;
                Verify = verify;
            }

            public string DisplayName { get; }
            public Action<ContainerBuilder> Register { get; }
            public Action<MessageBus, IMessageBus> Verify { get; }

            public override string ToString()
            {
                return DisplayName;
            }
        }
    }
#endif
}

#endif
