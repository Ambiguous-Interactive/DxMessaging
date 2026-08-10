#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Unity.Integrations.Reflex
{
#if REFLEX_PRESENT
    using System;
    using Core.MessageBus;
    using Core.Pooling;
    using global::Reflex.Attributes;
    using global::Reflex.Core;
#if REFLEX_14_OR_NEWER
    using global::Reflex.Enums;
#endif

    /// <summary>
    /// Optional installer that binds <see cref="IMessageRegistrationBuilder"/> for Reflex containers.
    /// </summary>
    public sealed class DxMessagingRegistrationInstaller : IInstaller
    {
        /// <summary>
        /// Registers the DxMessaging builder services within the Reflex container.
        /// </summary>
        /// <param name="containerBuilder">Container builder receiving the registrations.</param>
        public void InstallBindings(ContainerBuilder containerBuilder)
        {
            ReflexContainerBuilderCompatibility.RegisterSingletonType(
                containerBuilder,
                typeof(ContainerMessageRegistrationBuilder),
                typeof(ContainerMessageRegistrationBuilder),
                typeof(IMessageRegistrationBuilder)
            );
        }

        internal sealed class ContainerMessageRegistrationBuilder : IMessageRegistrationBuilder
        {
            [Inject]
            private Container _container;

            /// <summary>
            /// Builds a leasing wrapper using the container-aware provider resolution logic.
            /// </summary>
            /// <param name="options">Build options provided by the caller.</param>
            /// <returns>Lease produced by the underlying <see cref="MessageRegistrationBuilder"/>.</returns>
            public MessageRegistrationLease Build(MessageRegistrationBuildOptions options)
            {
                MessageRegistrationBuilder innerBuilder = ResolveInnerBuilder();
                return innerBuilder.Build(options);
            }

            private MessageRegistrationBuilder ResolveInnerBuilder()
            {
                IMessageBusProvider provider = TryResolveProvider();
                if (provider != null)
                {
                    return new MessageRegistrationBuilder(provider);
                }

                return new MessageRegistrationBuilder(new ContainerMessageBusProvider(_container));
            }

            private IMessageBusProvider TryResolveProvider()
            {
                try
                {
                    return _container.Resolve<IMessageBusProvider>();
                }
                catch
                {
                    return null;
                }
            }
        }

        public sealed class ContainerMessageBusProvider : IMessageBusProvider
        {
            private readonly Container _container;

            /// <summary>
            /// Wraps a Reflex container as an <see cref="IMessageBusProvider"/>.
            /// </summary>
            /// <param name="container">Container used to resolve <see cref="IMessageBus"/> instances.</param>
            public ContainerMessageBusProvider(Container container)
            {
                _container = container;
            }

            /// <summary>
            /// Resolves an <see cref="IMessageBus"/> from the underlying container.
            /// </summary>
            /// <returns>Message bus resolved from Reflex.</returns>
            public IMessageBus Resolve()
            {
                return _container.Resolve<IMessageBus>();
            }
        }
    }

    /// <summary>
    /// Provides convenience helpers for wiring a <see cref="MessageBus"/> into Reflex containers.
    /// The helpers use an explicit factory and adapt to the registration API introduced by
    /// Reflex 14 while retaining compatibility with earlier Reflex releases.
    /// </summary>
    public static class ReflexRegistrationExtensions
    {
        /// <summary>
        /// Registers a singleton <see cref="MessageBus"/> exposed as <see cref="IMessageBus"/>
        /// using an explicit factory.
        /// </summary>
        /// <param name="builder">Container builder receiving the registration.</param>
        public static void AddDxMessagingBus(this ContainerBuilder builder)
        {
            if (builder == null)
            {
                throw new ArgumentNullException(nameof(builder));
            }
            ReflexContainerBuilderCompatibility.RegisterSingletonFactory(
                builder,
                _ => new MessageBus(),
                typeof(MessageBus),
                typeof(IMessageBus)
            );
        }

        /// <summary>
        /// Registers a singleton <see cref="MessageBus"/> exposed as <see cref="IMessageBus"/>
        /// using the supplied factory. Allows callers to inject a custom
        /// <see cref="IDxMessagingClock"/> via <see cref="MessageBus.CreateForInternalUse"/>.
        /// </summary>
        /// <param name="builder">Container builder receiving the registration.</param>
        /// <param name="factory">Delegate that constructs the <see cref="MessageBus"/> instance using the resolver.</param>
        public static void AddDxMessagingBus(
            this ContainerBuilder builder,
            Func<Container, MessageBus> factory
        )
        {
            if (builder == null)
            {
                throw new ArgumentNullException(nameof(builder));
            }
            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }
            ReflexContainerBuilderCompatibility.RegisterSingletonFactory(
                builder,
                factory,
                typeof(MessageBus),
                typeof(IMessageBus)
            );
        }

        /// <summary>
        /// Registers a singleton <see cref="MessageBus"/> exposed as <see cref="IMessageBus"/>
        /// using the supplied <see cref="IDxMessagingClock"/>.
        /// </summary>
        /// <param name="builder">Container builder receiving the registration.</param>
        /// <param name="clock">Clock implementation injected into the bus. Must not be null.</param>
        public static void AddDxMessagingBus(this ContainerBuilder builder, IDxMessagingClock clock)
        {
            if (builder == null)
            {
                throw new ArgumentNullException(nameof(builder));
            }
            if (clock == null)
            {
                throw new ArgumentNullException(nameof(clock));
            }
            ReflexContainerBuilderCompatibility.RegisterSingletonFactory(
                builder,
                _ => MessageBus.CreateForInternalUse(clock),
                typeof(MessageBus),
                typeof(IMessageBus)
            );
        }
    }

    internal static class ReflexContainerBuilderCompatibility
    {
        internal static void RegisterSingletonType(
            ContainerBuilder builder,
            Type concrete,
            params Type[] contracts
        )
        {
#if REFLEX_14_OR_NEWER
            builder.RegisterType(concrete, contracts, Lifetime.Singleton, Resolution.Lazy);
#else
            builder.AddSingleton(concrete, contracts);
#endif
        }

        internal static void RegisterSingletonFactory<T>(
            ContainerBuilder builder,
            Func<Container, T> factory,
            params Type[] contracts
        )
        {
#if REFLEX_14_OR_NEWER
            builder.RegisterFactory(factory, contracts, Lifetime.Singleton, Resolution.Lazy);
#else
            builder.AddSingleton(factory, contracts);
#endif
        }
    }
#endif
}
#endif
