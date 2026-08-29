#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.Attributes;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using NUnit.Framework;
    using UnityEngine;
    using BusType = DxMessaging.Core.MessageBus.MessageBus;

    public sealed class UntypedDispatchTests : MessagingTestBase
    {
        [Test]
        public void UntypedDispatchUsesKindSpecificDelegateCaches(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(UntypedDispatchUsesKindSpecificDelegateCaches) + "_" + scenario,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            IMessageBus bus = MessageHandler.MessageBus;
            InstanceId target = new InstanceId(0x6A17_1001);
            InstanceId source = new InstanceId(0x6A17_1002);
            List<MessageRegistrationHandle> handles = new();
            int untargetedCount = 0;
            int targetedCount = 0;
            int broadcastCount = 0;

            using (LeakWatcher.Watch(nameof(UntypedDispatchUsesKindSpecificDelegateCaches)))
            {
                try
                {
                    handles.Add(
                        token.RegisterUntargeted<MultiKindMessage>(
                            (in MultiKindMessage _) => ++untargetedCount
                        )
                    );
                    handles.Add(
                        token.RegisterTargeted<MultiKindMessage>(
                            target,
                            (in MultiKindMessage _) => ++targetedCount
                        )
                    );
                    handles.Add(
                        token.RegisterBroadcast<MultiKindMessage>(
                            source,
                            (in MultiKindMessage _) => ++broadcastCount
                        )
                    );

                    DispatchUntyped(bus, MessageKind.Untargeted, target, source);
                    DispatchUntyped(bus, MessageKind.Targeted, target, source);
                    DispatchUntyped(bus, MessageKind.Broadcast, target, source);

                    AssertCounts(1, 1, 1);

                    DispatchUntyped(bus, scenario.Kind, target, source);
                    switch (scenario.Kind)
                    {
                        case MessageKind.Untargeted:
                            AssertCounts(2, 1, 1);
                            break;
                        case MessageKind.Targeted:
                            AssertCounts(1, 2, 1);
                            break;
                        case MessageKind.Broadcast:
                            AssertCounts(1, 1, 2);
                            break;
                        default:
                            Assert.Fail("Unhandled MessageKind: {0}.", scenario.Kind);
                            break;
                    }
                }
                finally
                {
                    foreach (MessageRegistrationHandle handle in handles)
                    {
                        token.RemoveRegistration(handle);
                    }
                }
            }

            return;

            void AssertCounts(int expectedUntargeted, int expectedTargeted, int expectedBroadcast)
            {
                Assert.AreEqual(expectedUntargeted, untargetedCount, "Untargeted count mismatch.");
                Assert.AreEqual(expectedTargeted, targetedCount, "Targeted count mismatch.");
                Assert.AreEqual(expectedBroadcast, broadcastCount, "Broadcast count mismatch.");
            }
        }

        [Test]
        public void TypedDispatchSeedsBridgeForPrivateManualMessageBeforeUntypedDispatch(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            IMessageBus bus = MessageHandler.MessageBus;
            InstanceId target = new InstanceId(0x6A17_2001);
            InstanceId source = new InstanceId(0x6A17_2002);

            DispatchTypedPrivateManualMessage(bus, scenario.Kind, target, source);
            Assert.DoesNotThrow(
                () => DispatchUntypedPrivateManualMessage(bus, scenario.Kind, target, source),
                "A typed dispatch should root the AOT bridge needed by later untyped dispatch."
            );
        }

        [Test]
        public void FirstUntypedDispatchUsesGeneratedAotBridge(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            IMessageBus bus = new BusType();
            InstanceId target = new(0x6A17_3001);
            InstanceId source = new(0x6A17_3002);

            Assert.DoesNotThrow(
                () => DispatchFirstGeneratedMessage(bus, scenario.Kind, target, source),
                "[{0}] A source-generated message must support its first-ever untyped dispatch without a prior registration or typed emit.",
                scenario.Kind
            );
            Assert.AreEqual(
                1,
                bus.EmissionId,
                "[{0}] The generated bridge must enter the typed dispatch path exactly once.",
                scenario.Kind
            );
        }

        public readonly struct MultiKindMessage
            : IUntargetedMessage,
                ITargetedMessage,
                IBroadcastMessage
        {
            public Type MessageType => typeof(MultiKindMessage);
        }

        private static void DispatchUntyped(
            IMessageBus bus,
            MessageKind kind,
            InstanceId target,
            InstanceId source
        )
        {
            MultiKindMessage message = new();
            switch (kind)
            {
                case MessageKind.Untargeted:
                    bus.UntypedUntargetedBroadcast(message);
                    break;
                case MessageKind.Targeted:
                    bus.UntypedTargetedBroadcast(target, message);
                    break;
                case MessageKind.Broadcast:
                    bus.UntypedSourcedBroadcast(source, message);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
            }
        }

        private readonly struct PrivateManualMessage
            : IUntargetedMessage,
                ITargetedMessage,
                IBroadcastMessage
        {
            public Type MessageType => typeof(PrivateManualMessage);
        }

        private static void DispatchTypedPrivateManualMessage(
            IMessageBus bus,
            MessageKind kind,
            InstanceId target,
            InstanceId source
        )
        {
            PrivateManualMessage message = new();
            switch (kind)
            {
                case MessageKind.Untargeted:
                    bus.UntargetedBroadcast(ref message);
                    break;
                case MessageKind.Targeted:
                    bus.TargetedBroadcast(ref target, ref message);
                    break;
                case MessageKind.Broadcast:
                    bus.SourcedBroadcast(ref source, ref message);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
            }
        }

        private static void DispatchUntypedPrivateManualMessage(
            IMessageBus bus,
            MessageKind kind,
            InstanceId target,
            InstanceId source
        )
        {
            PrivateManualMessage message = new();
            switch (kind)
            {
                case MessageKind.Untargeted:
                    bus.UntypedUntargetedBroadcast(message);
                    break;
                case MessageKind.Targeted:
                    bus.UntypedTargetedBroadcast(target, message);
                    break;
                case MessageKind.Broadcast:
                    bus.UntypedSourcedBroadcast(source, message);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
            }
        }

        private static void DispatchFirstGeneratedMessage(
            IMessageBus bus,
            MessageKind kind,
            InstanceId target,
            InstanceId source
        )
        {
            switch (kind)
            {
                case MessageKind.Untargeted:
                    bus.UntypedUntargetedBroadcast(new ColdUntargetedMessage());
                    break;
                case MessageKind.Targeted:
                    bus.UntypedTargetedBroadcast(target, new ColdTargetedMessage());
                    break;
                case MessageKind.Broadcast:
                    bus.UntypedSourcedBroadcast(source, new ColdBroadcastMessage());
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
            }
        }
    }

    [DxUntargetedMessage]
    internal readonly partial struct ColdUntargetedMessage { }

    [DxTargetedMessage]
    internal readonly partial struct ColdTargetedMessage { }

    [DxBroadcastMessage]
    internal readonly partial struct ColdBroadcastMessage { }
}
#endif
