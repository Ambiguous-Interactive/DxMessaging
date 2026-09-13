#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Unity.Integrations.Zenject
{
#if ZENJECT_PRESENT
    using System;
    using Core.MessageBus;
    using Core.Pooling;
    using global::Zenject;

    /// <summary>
    /// Optional installer that exposes <see cref="IMessageRegistrationBuilder"/> using the scoped Zenject container.
    /// </summary>
    public sealed class DxMessagingRegistrationInstaller : MonoInstaller
    {
        internal void RunInstallBindings(DiContainer container)
        {
            Container = container;
            InstallBindings();
        }

        /// <summary>
        /// Registers the DxMessaging builder within the Zenject container.
        /// </summary>
        public override void InstallBindings()
        {
            Container.Bind<IMessageRegistrationBuilder>().FromMethod(CreateBuilder).AsTransient();
        }

        private static IMessageRegistrationBuilder CreateBuilder(InjectContext context)
        {
            IMessageBusProvider provider = context.Container.TryResolve<IMessageBusProvider>();
            if (MessageBusProviderUtility.IsAvailable(provider))
            {
                return new MessageRegistrationBuilder(provider);
            }

            IMessageBus messageBus = context.Container.Resolve<IMessageBus>();
            return new MessageRegistrationBuilder(
                new ContainerMessageBusProvider(context.Container, messageBus)
            );
        }

        public sealed class ContainerMessageBusProvider : IMessageBusProvider
        {
            private readonly DiContainer _container;
            private readonly IMessageBus _cachedBus;

            /// <summary>
            /// Creates a provider that uses the container-supplied bus, falling back to resolving on demand.
            /// </summary>
            /// <param name="container">Zenject container used to resolve services.</param>
            /// <param name="cachedBus">Cached bus instance to return if available.</param>
            public ContainerMessageBusProvider(DiContainer container, IMessageBus cachedBus)
            {
                _container = container;
                _cachedBus = cachedBus;
            }

            /// <summary>
            /// Resolves the message bus for the current scope.
            /// </summary>
            /// <returns>Cached bus if provided; otherwise resolves from the container.</returns>
            public IMessageBus Resolve()
            {
                return _cachedBus ?? _container.Resolve<IMessageBus>();
            }
        }
    }

    /// <summary>
    /// Provides convenience helpers for wiring a <see cref="MessageBus"/> into Zenject containers.
    /// Zenject's <c>BindInterfacesAndSelfTo</c> defaults to selecting the public parameterless
    /// constructor, so today the bare <c>Container.BindInterfacesAndSelfTo&lt;MessageBus&gt;().AsSingle()</c>
    /// pattern resolves correctly. Behaviour is version-sensitive: future Zenject releases that
    /// broaden constructor scanning could pick a non-public clock-taking overload whose
    /// dependency is not registered, mirroring the VContainer failure mode -- always prefer the
    /// helper below for clarity and forward compatibility. Calling this helper alongside an
    /// existing bare bind raises a Zenject binding-conflict exception when the container is
    /// validated.
    /// </summary>
    public static class ZenjectRegistrationExtensions
    {
        /// <summary>
        /// Binds a singleton <see cref="MessageBus"/> exposed as <see cref="IMessageBus"/> using
        /// an explicit method, sidestepping reflection-based constructor selection.
        /// </summary>
        /// <param name="container">Container receiving the registration.</param>
        public static void BindDxMessagingBus(this DiContainer container)
        {
            if (container == null)
            {
                throw new ArgumentNullException(nameof(container));
            }
            container
                .BindInterfacesAndSelfTo<MessageBus>()
                .FromMethod(_ => new MessageBus())
                .AsSingle();
        }

        /// <summary>
        /// Binds a singleton <see cref="MessageBus"/> exposed as <see cref="IMessageBus"/> using
        /// the supplied factory. Allows callers to inject a custom <see cref="IDxMessagingClock"/>
        /// via <see cref="MessageBus.CreateForInternalUse"/>.
        /// </summary>
        /// <param name="container">Container receiving the registration.</param>
        /// <param name="factory">Delegate that constructs the <see cref="MessageBus"/> instance using the inject context.</param>
        public static void BindDxMessagingBus(
            this DiContainer container,
            Func<InjectContext, MessageBus> factory
        )
        {
            if (container == null)
            {
                throw new ArgumentNullException(nameof(container));
            }
            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }
            container.BindInterfacesAndSelfTo<MessageBus>().FromMethod(factory).AsSingle();
        }

        /// <summary>
        /// Binds a singleton <see cref="MessageBus"/> exposed as <see cref="IMessageBus"/> using
        /// the supplied <see cref="IDxMessagingClock"/>.
        /// </summary>
        /// <param name="container">Container receiving the registration.</param>
        /// <param name="clock">Clock implementation injected into the bus. Must not be null.</param>
        public static void BindDxMessagingBus(this DiContainer container, IDxMessagingClock clock)
        {
            if (container == null)
            {
                throw new ArgumentNullException(nameof(container));
            }
            if (clock == null)
            {
                throw new ArgumentNullException(nameof(clock));
            }
            container
                .BindInterfacesAndSelfTo<MessageBus>()
                .FromMethod(_ => MessageBus.CreateForInternalUse(clock))
                .AsSingle();
        }
    }
#endif
}
#endif
