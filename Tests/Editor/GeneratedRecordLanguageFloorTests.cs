namespace DxMessaging.Tests.Editor
{
    using System;
    using DxMessaging.Core;
    using DxMessaging.Core.Attributes;
    using NUnit.Framework;

    /// <summary>Compiles record declarations through the shipped generators.</summary>
    /// <remarks>
    /// 2026-10-29: generated record-class syntax required C# 10 on C# 9 hosts.
    /// Keep these records free of init-only properties and Unity serialization.
    /// </remarks>
    [TestFixture]
    public sealed partial class GeneratedRecordLanguageFloorTests
    {
        [Test]
        public void ReferenceRecordConstructorAssignsItsField()
        {
            ConstructorRecord message = new(7);
            Assert.That(
                message.Value,
                Is.EqualTo(7),
                "Generated record constructor lost its value."
            );
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void ReferenceRecordReportsItsConcreteMessageType(int kind)
        {
            IMessage message = kind switch
            {
                0 => new UntargetedRecord(),
                1 => new TargetedRecord(),
                2 => new BroadcastRecord(),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
            Assert.That(
                message.MessageType,
                Is.EqualTo(message.GetType()),
                $"kind={kind}: generated record message identity differs from its concrete type."
            );
        }

        [Test]
        public void StructInsideRecordHasItsConstructorAndMessageIdentity()
        {
            RecordContainer.NestedMessage message = new(11);
            Assert.That(
                message.Value,
                Is.EqualTo(11),
                "Nested generated constructor lost its value."
            );
            Assert.That(
                ((IMessage)message).MessageType,
                Is.EqualTo(typeof(RecordContainer.NestedMessage)),
                "Nested generated message identity differs from its concrete type."
            );
        }

        [DxAutoConstructor]
        private partial record ConstructorRecord
        {
            public readonly int Value;
        }

        [DxUntargetedMessage]
        private partial record UntargetedRecord;

        [DxTargetedMessage]
        private partial record TargetedRecord;

        [DxBroadcastMessage]
        private partial record BroadcastRecord;

        private partial record RecordContainer
        {
            [DxUntargetedMessage]
            [DxAutoConstructor]
            public readonly partial struct NestedMessage
            {
                public readonly int Value;
            }
        }
    }
}
