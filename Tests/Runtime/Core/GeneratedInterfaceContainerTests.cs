namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using DxMessaging.Core;
    using DxMessaging.Core.Attributes;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;

    public partial interface IGeneratedMessageContracts
    {
        [DxUntargetedMessage]
        [DxAutoConstructor]
        public readonly partial struct UntargetedValue
        {
            public readonly int Value;
        }

        [DxTargetedMessage]
        [DxAutoConstructor]
        public readonly partial struct TargetedValue
        {
            public readonly int Value;
        }

        [DxBroadcastMessage]
        [DxAutoConstructor]
        public readonly partial struct BroadcastValue
        {
            public readonly int Value;
        }

        [DxUntargetedMessage]
        [DxAutoConstructor]
        public sealed partial class UntargetedReference
        {
            public readonly int Value;
        }

        [DxTargetedMessage]
        [DxAutoConstructor]
        public sealed partial class TargetedReference
        {
            public readonly int Value;
        }

        [DxBroadcastMessage]
        [DxAutoConstructor]
        public sealed partial class BroadcastReference
        {
            public readonly int Value;
        }
    }

    public partial interface IGeneratedMessageContracts<T>
    {
        [DxUntargetedMessage]
        [DxAutoConstructor]
        public readonly partial struct UntargetedValue
        {
            public readonly T Value;
        }

        [DxTargetedMessage]
        [DxAutoConstructor]
        public readonly partial struct TargetedValue
        {
            public readonly T Value;
        }

        [DxBroadcastMessage]
        [DxAutoConstructor]
        public readonly partial struct BroadcastValue
        {
            public readonly T Value;
        }

        [DxUntargetedMessage]
        [DxAutoConstructor]
        public sealed partial class UntargetedReference
        {
            public readonly T Value;
        }

        [DxTargetedMessage]
        [DxAutoConstructor]
        public sealed partial class TargetedReference
        {
            public readonly T Value;
        }

        [DxBroadcastMessage]
        [DxAutoConstructor]
        public sealed partial class BroadcastReference
        {
            public readonly T Value;
        }
    }

    public sealed class GeneratedInterfaceContainerTests
    {
        [Test]
        public void InterfaceMessagesRetainConstructedValuesAndConcreteIdentity(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(false, true)] bool referenceMessage,
            [Values(false, true)] bool genericContainer
        )
        {
            string context =
                $"[{scenario.Kind}] referenceMessage={referenceMessage}, genericContainer={genericContainer}";
            (IMessage message, int value) = CreateMessage(
                scenario.Kind,
                referenceMessage,
                genericContainer
            );
            Assert.That(
                value,
                Is.EqualTo(42),
                "The generated constructor assigns the field: " + context
            );
            Assert.That(
                message.MessageType,
                Is.EqualTo(message.GetType()),
                "The generated property preserves the closed concrete identity: " + context
            );
            Type expectedInterface = scenario.Kind switch
            {
                MessageKind.Untargeted => typeof(IUntargetedMessage),
                MessageKind.Targeted => typeof(ITargetedMessage),
                MessageKind.Broadcast => typeof(IBroadcastMessage),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(scenario),
                    scenario,
                    "Unknown message kind"
                ),
            };
            Assert.That(
                message,
                Is.InstanceOf(expectedInterface),
                "The declared route interface is implemented: " + context
            );
        }

        private static (IMessage Message, int Value) CreateMessage(
            MessageKind kind,
            bool referenceMessage,
            bool genericContainer
        )
        {
            return (kind, referenceMessage, genericContainer) switch
            {
                (MessageKind.Untargeted, false, false) => Read(
                    new IGeneratedMessageContracts.UntargetedValue(42),
                    static message => message.Value
                ),
                (MessageKind.Targeted, false, false) => Read(
                    new IGeneratedMessageContracts.TargetedValue(42),
                    static message => message.Value
                ),
                (MessageKind.Broadcast, false, false) => Read(
                    new IGeneratedMessageContracts.BroadcastValue(42),
                    static message => message.Value
                ),
                (MessageKind.Untargeted, true, false) => Read(
                    new IGeneratedMessageContracts.UntargetedReference(42),
                    static message => message.Value
                ),
                (MessageKind.Targeted, true, false) => Read(
                    new IGeneratedMessageContracts.TargetedReference(42),
                    static message => message.Value
                ),
                (MessageKind.Broadcast, true, false) => Read(
                    new IGeneratedMessageContracts.BroadcastReference(42),
                    static message => message.Value
                ),
                (MessageKind.Untargeted, false, true) => Read(
                    new IGeneratedMessageContracts<int>.UntargetedValue(42),
                    static message => message.Value
                ),
                (MessageKind.Targeted, false, true) => Read(
                    new IGeneratedMessageContracts<int>.TargetedValue(42),
                    static message => message.Value
                ),
                (MessageKind.Broadcast, false, true) => Read(
                    new IGeneratedMessageContracts<int>.BroadcastValue(42),
                    static message => message.Value
                ),
                (MessageKind.Untargeted, true, true) => Read(
                    new IGeneratedMessageContracts<int>.UntargetedReference(42),
                    static message => message.Value
                ),
                (MessageKind.Targeted, true, true) => Read(
                    new IGeneratedMessageContracts<int>.TargetedReference(42),
                    static message => message.Value
                ),
                (MessageKind.Broadcast, true, true) => Read(
                    new IGeneratedMessageContracts<int>.BroadcastReference(42),
                    static message => message.Value
                ),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(kind),
                    kind,
                    "Unknown message kind"
                ),
            };
        }

        private static (IMessage Message, int Value) Read<TMessage>(
            TMessage message,
            Func<TMessage, int> read
        )
            where TMessage : IMessage
        {
            return (message, read(message));
        }
    }
}
