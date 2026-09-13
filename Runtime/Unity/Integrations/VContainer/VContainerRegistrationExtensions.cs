#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Unity.Integrations.VContainer
{
#if VCONTAINER_PRESENT
    using System;
    using global::VContainer;
    using Core.MessageBus;
    using Core.Pooling;

    /// <summary>
    /// Provides convenience extension methods for wiring DxMessaging services inside
    /// VContainer scopes. The helper covers the bus registration as well as the
    /// <see cref="IMessageRegistrationBuilder"/> binding so consumers do not have to
    /// drive VContainer reflection-based constructor selection on internal types.
    /// </summary>
    public static class VContainerRegistrationExtensions
    {
        /// <summary>
        /// Registers a <see cref="MessageBus"/> exposed as itself and
        /// <see cref="IMessageBus"/> using an explicit factory backed by the
        /// parameterless <see cref="MessageBus"/> constructor. VContainer's
        /// <c>TypeAnalyzer</c> scans both public and non-public constructors using
        /// <c>BindingFlags.Public | BindingFlags.NonPublic</c>, so the bare
        /// <c>Register&lt;MessageBus&gt;(Lifetime.Singleton).As&lt;IMessageBus&gt;()</c>
        /// pattern would still latch onto a clock-taking private overload whose
        /// <see cref="IDxMessagingClock"/> dependency is not registered. Always prefer this
        /// helper. Calling this helper alongside a bare bus registration raises a
        /// <c>VContainerException</c> at <see cref="IContainerBuilder.Build"/> time.
        /// </summary>
        /// <param name="builder">Container builder receiving the registration.</param>
        /// <param name="lifetime">Lifetime to assign to the registration. Defaults to <see cref="Lifetime.Singleton"/>.</param>
        /// <returns>The underlying registration builder for further chaining.</returns>
        public static RegistrationBuilder RegisterDxMessagingBus(
            this IContainerBuilder builder,
            Lifetime lifetime = Lifetime.Singleton
        )
        {
            if (builder == null)
            {
                throw new ArgumentNullException(nameof(builder));
            }

            return builder
                .Register<MessageBus>(CreateMessageBus, lifetime)
                .As(typeof(MessageBus), typeof(IMessageBus));
        }

        /// <summary>
        /// Registers a <see cref="MessageBus"/> exposed as itself and
        /// <see cref="IMessageBus"/> using the supplied factory. Use this overload when
        /// callers need to inject a custom <see cref="IDxMessagingClock"/> (for example a
        /// deterministic test clock) or configure eviction options through
        /// <see cref="MessageBus.CreateForInternalUse"/>.
        /// </summary>
        /// <param name="builder">Container builder receiving the registration.</param>
        /// <param name="factory">Delegate that constructs the <see cref="MessageBus"/> instance using the resolver.</param>
        /// <param name="lifetime">Lifetime to assign to the registration. Defaults to <see cref="Lifetime.Singleton"/>.</param>
        /// <returns>The underlying registration builder for further chaining.</returns>
        public static RegistrationBuilder RegisterDxMessagingBus(
            this IContainerBuilder builder,
            Func<IObjectResolver, MessageBus> factory,
            Lifetime lifetime = Lifetime.Singleton
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
            return builder
                .Register<MessageBus>(factory, lifetime)
                .As(typeof(MessageBus), typeof(IMessageBus));
        }

        /// <summary>
        /// Registers a <see cref="MessageBus"/> exposed as itself and
        /// <see cref="IMessageBus"/> using the supplied <see cref="IDxMessagingClock"/>.
        /// Builds the bus through <see cref="MessageBus.CreateForInternalUse"/>, which
        /// is visible to the integration assembly via <c>InternalsVisibleTo</c>.
        /// </summary>
        /// <param name="builder">Container builder receiving the registration.</param>
        /// <param name="clock">Clock implementation injected into the bus. Must not be null.</param>
        /// <param name="lifetime">Lifetime to assign to the registration. Defaults to <see cref="Lifetime.Singleton"/>.</param>
        /// <returns>The underlying registration builder for further chaining.</returns>
        public static RegistrationBuilder RegisterDxMessagingBus(
            this IContainerBuilder builder,
            IDxMessagingClock clock,
            Lifetime lifetime = Lifetime.Singleton
        )
        {
            if (builder == null)
            {
                throw new ArgumentNullException(nameof(builder));
            }
            if (clock == null)
            {
                throw new ArgumentNullException(nameof(clock));
            }
            return builder
                .Register<MessageBus>(_ => MessageBus.CreateForInternalUse(clock), lifetime)
                .As(typeof(MessageBus), typeof(IMessageBus));
        }

        /// <summary>
        /// Registers <see cref="IMessageRegistrationBuilder"/> as a transient service backed by the scoped message bus.
        /// </summary>
        /// <param name="builder">Container builder receiving the registration.</param>
        public static void RegisterMessageRegistrationBuilder(this IContainerBuilder builder)
        {
            if (builder == null)
            {
                throw new ArgumentNullException(nameof(builder));
            }

            builder.Register<IMessageRegistrationBuilder>(CreateBuilder, Lifetime.Transient);
        }

        private static MessageBus CreateMessageBus(IObjectResolver resolver)
        {
            return new MessageBus();
        }

        private static IMessageRegistrationBuilder CreateBuilder(IObjectResolver resolver)
        {
            if (
                resolver.TryResolve(out IMessageBusProvider provider)
                && MessageBusProviderUtility.IsAvailable(provider)
            )
            {
                return new MessageRegistrationBuilder(provider);
            }

            return new MessageRegistrationBuilder(new ResolverMessageBusProvider(resolver));
        }

        /// <summary>
        /// Wraps a VContainer <see cref="IObjectResolver"/> as an <see cref="IMessageBusProvider"/>.
        /// </summary>
        public sealed class ResolverMessageBusProvider : IMessageBusProvider
        {
            private readonly IObjectResolver _resolver;

            /// <summary>
            /// Wraps a VContainer resolver as an <see cref="IMessageBusProvider"/>.
            /// </summary>
            /// <param name="resolver">Resolver used to obtain <see cref="IMessageBus"/> instances.</param>
            public ResolverMessageBusProvider(IObjectResolver resolver)
            {
                _resolver = resolver;
            }

            /// <summary>
            /// Resolves an <see cref="IMessageBus"/> from the current VContainer scope.
            /// </summary>
            /// <returns>Scoped message bus.</returns>
            public IMessageBus Resolve()
            {
                return _resolver.Resolve<IMessageBus>();
            }
        }
    }
#endif
}
#endif
