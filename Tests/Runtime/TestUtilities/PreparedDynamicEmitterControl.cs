#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime
{
    using System;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;

    /// <summary>Test-only bus/type binding that owns no route or sink references.</summary>
    internal sealed class PreparedUntargetedEmitter<TMessage>
        where TMessage : IUntargetedMessage
    {
        private readonly IMessageBus _bus;

        internal PreparedUntargetedEmitter(IMessageBus bus)
        {
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        }

        internal void Emit(ref TMessage message) => _bus.UntargetedBroadcast(ref message);
    }

    /// <summary>Test-only targeted binding; the caller supplies a live route key per call.</summary>
    internal sealed class PreparedTargetedEmitter<TMessage>
        where TMessage : ITargetedMessage
    {
        private readonly IMessageBus _bus;

        internal PreparedTargetedEmitter(IMessageBus bus)
        {
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        }

        internal void Emit(ref InstanceId target, ref TMessage message) =>
            _bus.TargetedBroadcast(ref target, ref message);
    }

    /// <summary>Test-only broadcast binding; the caller supplies a live source per call.</summary>
    internal sealed class PreparedBroadcastEmitter<TMessage>
        where TMessage : IBroadcastMessage
    {
        private readonly IMessageBus _bus;

        internal PreparedBroadcastEmitter(IMessageBus bus)
        {
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        }

        internal void Emit(ref InstanceId source, ref TMessage message) =>
            _bus.SourcedBroadcast(ref source, ref message);
    }
}
#endif
