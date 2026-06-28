#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime
{
    using System;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;

    /// <summary>
    /// Reusable <see cref="IMessageBus"/> test double that forwards every member to an
    /// inner bus. All members are virtual so fixtures can derive and override just the
    /// surface they pin (for example <see cref="Trim"/>). When constructed without an
    /// inner bus, every non-overridden member throws
    /// <see cref="NotImplementedException"/> so a refactor that starts touching
    /// additional bus surface fails loudly instead of silently passing a wrong default
    /// value through.
    /// </summary>
    public class DelegatingMessageBus : IMessageBus
    {
        private readonly IMessageBus _inner;

        public DelegatingMessageBus(IMessageBus inner = null)
        {
            _inner = inner;
        }

        /// <summary>
        /// The wrapped bus. Throws <see cref="NotImplementedException"/> when the stub
        /// was constructed without an inner bus, so partial stubs fail loudly on any
        /// member they did not override.
        /// </summary>
        protected IMessageBus Inner => _inner ?? throw new NotImplementedException();

        public virtual bool DiagnosticsMode => Inner.DiagnosticsMode;

        public virtual int RegisteredGlobalSequentialIndex => Inner.RegisteredGlobalSequentialIndex;

        public virtual int OccupiedTypeSlots => Inner.OccupiedTypeSlots;

        public virtual int OccupiedTargetSlots => Inner.OccupiedTargetSlots;

        public virtual int RegisteredBroadcast => Inner.RegisteredBroadcast;

        public virtual int RegisteredTargeted => Inner.RegisteredTargeted;

        public virtual int RegisteredUntargeted => Inner.RegisteredUntargeted;

        public virtual int RegisteredInterceptors => Inner.RegisteredInterceptors;

        public virtual int RegisteredPostProcessors => Inner.RegisteredPostProcessors;

        public virtual int RegisteredGlobalAcceptAll => Inner.RegisteredGlobalAcceptAll;

        public virtual RegistrationLog Log => Inner.Log;

        public virtual long EmissionId => Inner.EmissionId;

        public virtual IMessageBus.TrimResult Trim(bool force = false) => Inner.Trim(force);

        public virtual MessageBusRegistration RegisterUntargeted<T>(
            MessageHandler messageHandler,
            int priority = 0
        )
            where T : IUntargetedMessage => Inner.RegisterUntargeted<T>(messageHandler, priority);

        public virtual MessageBusRegistration RegisterUntargetedPostProcessor<T>(
            MessageHandler messageHandler,
            int priority = 0
        )
            where T : IUntargetedMessage =>
            Inner.RegisterUntargetedPostProcessor<T>(messageHandler, priority);

        public virtual MessageBusRegistration RegisterTargeted<T>(
            InstanceId target,
            MessageHandler messageHandler,
            int priority = 0
        )
            where T : ITargetedMessage =>
            Inner.RegisterTargeted<T>(target, messageHandler, priority);

        public virtual MessageBusRegistration RegisterTargetedPostProcessor<T>(
            InstanceId target,
            MessageHandler messageHandler,
            int priority = 0
        )
            where T : ITargetedMessage =>
            Inner.RegisterTargetedPostProcessor<T>(target, messageHandler, priority);

        public virtual MessageBusRegistration RegisterTargetedWithoutTargeting<T>(
            MessageHandler messageHandler,
            int priority = 0
        )
            where T : ITargetedMessage =>
            Inner.RegisterTargetedWithoutTargeting<T>(messageHandler, priority);

        public virtual MessageBusRegistration RegisterTargetedWithoutTargetingPostProcessor<T>(
            MessageHandler messageHandler,
            int priority = 0
        )
            where T : ITargetedMessage =>
            Inner.RegisterTargetedWithoutTargetingPostProcessor<T>(messageHandler, priority);

        public virtual MessageBusRegistration RegisterSourcedBroadcast<T>(
            InstanceId source,
            MessageHandler messageHandler,
            int priority = 0
        )
            where T : IBroadcastMessage =>
            Inner.RegisterSourcedBroadcast<T>(source, messageHandler, priority);

        public virtual MessageBusRegistration RegisterBroadcastPostProcessor<T>(
            InstanceId source,
            MessageHandler messageHandler,
            int priority = 0
        )
            where T : IBroadcastMessage =>
            Inner.RegisterBroadcastPostProcessor<T>(source, messageHandler, priority);

        public virtual MessageBusRegistration RegisterSourcedBroadcastWithoutSource<T>(
            MessageHandler messageHandler,
            int priority = 0
        )
            where T : IBroadcastMessage =>
            Inner.RegisterSourcedBroadcastWithoutSource<T>(messageHandler, priority);

        public virtual MessageBusRegistration RegisterBroadcastWithoutSourcePostProcessor<T>(
            MessageHandler messageHandler,
            int priority = 0
        )
            where T : IBroadcastMessage =>
            Inner.RegisterBroadcastWithoutSourcePostProcessor<T>(messageHandler, priority);

        public virtual MessageBusRegistration RegisterGlobalAcceptAll(
            MessageHandler messageHandler
        ) => Inner.RegisterGlobalAcceptAll(messageHandler);

        public virtual MessageBusRegistration RegisterUntargetedInterceptor<T>(
            IMessageBus.UntargetedInterceptor<T> interceptor,
            int priority = 0
        )
            where T : IUntargetedMessage =>
            Inner.RegisterUntargetedInterceptor(interceptor, priority);

        public virtual MessageBusRegistration RegisterTargetedInterceptor<T>(
            IMessageBus.TargetedInterceptor<T> interceptor,
            int priority = 0
        )
            where T : ITargetedMessage => Inner.RegisterTargetedInterceptor(interceptor, priority);

        public virtual MessageBusRegistration RegisterBroadcastInterceptor<T>(
            IMessageBus.BroadcastInterceptor<T> interceptor,
            int priority = 0
        )
            where T : IBroadcastMessage =>
            Inner.RegisterBroadcastInterceptor(interceptor, priority);

        public virtual void Deregister<T>(in MessageBusRegistration registration)
            where T : IMessage => Inner.Deregister<T>(in registration);

        public virtual void UntypedUntargetedBroadcast(IUntargetedMessage typedMessage) =>
            Inner.UntypedUntargetedBroadcast(typedMessage);

        public virtual void UntargetedBroadcast<TMessage>(ref TMessage typedMessage)
            where TMessage : IUntargetedMessage => Inner.UntargetedBroadcast(ref typedMessage);

        public virtual void UntypedTargetedBroadcast(
            InstanceId target,
            ITargetedMessage typedMessage
        ) => Inner.UntypedTargetedBroadcast(target, typedMessage);

        public virtual void TargetedBroadcast<TMessage>(
            ref InstanceId target,
            ref TMessage typedMessage
        )
            where TMessage : ITargetedMessage =>
            Inner.TargetedBroadcast(ref target, ref typedMessage);

        public virtual void UntypedSourcedBroadcast(
            InstanceId source,
            IBroadcastMessage typedMessage
        ) => Inner.UntypedSourcedBroadcast(source, typedMessage);

        public virtual void SourcedBroadcast<TMessage>(
            ref InstanceId source,
            ref TMessage typedMessage
        )
            where TMessage : IBroadcastMessage =>
            Inner.SourcedBroadcast(ref source, ref typedMessage);
    }
}
#endif
