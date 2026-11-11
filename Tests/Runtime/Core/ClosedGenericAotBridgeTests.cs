#if UNITY_2021_3_OR_NEWER
using DxMessaging.Core.Attributes;

[assembly: DxAotMessage(
    typeof(DxMessaging.Tests.Runtime.Core.ClosedAotMessages<bool>.UntargetedValue)
)]
[assembly: DxAotMessage(
    typeof(DxMessaging.Tests.Runtime.Core.ClosedAotMessages<bool>.UntargetedReference)
)]
[assembly: DxAotMessage(
    typeof(DxMessaging.Tests.Runtime.Core.ClosedAotMessages<bool>.TargetedValue)
)]
[assembly: DxAotMessage(
    typeof(DxMessaging.Tests.Runtime.Core.ClosedAotMessages<bool>.TargetedReference)
)]
[assembly: DxAotMessage(
    typeof(DxMessaging.Tests.Runtime.Core.ClosedAotMessages<bool>.BroadcastValue)
)]
[assembly: DxAotMessage(
    typeof(DxMessaging.Tests.Runtime.Core.ClosedAotMessages<bool>.BroadcastReference)
)]
[assembly: DxAotMessage(
    typeof(DxMessaging.Tests.Runtime.Core.ClosedAotMessages<DxMessaging.Tests.Runtime.Core.AotChoice>.UntargetedValue)
)]
[assembly: DxAotMessage(
    typeof(DxMessaging.Tests.Runtime.Core.ClosedAotMessages<DxMessaging.Tests.Runtime.Core.AotChoice>.UntargetedReference)
)]
[assembly: DxAotMessage(
    typeof(DxMessaging.Tests.Runtime.Core.ClosedAotMessages<DxMessaging.Tests.Runtime.Core.AotChoice>.TargetedValue)
)]
[assembly: DxAotMessage(
    typeof(DxMessaging.Tests.Runtime.Core.ClosedAotMessages<DxMessaging.Tests.Runtime.Core.AotChoice>.TargetedReference)
)]
[assembly: DxAotMessage(
    typeof(DxMessaging.Tests.Runtime.Core.ClosedAotMessages<DxMessaging.Tests.Runtime.Core.AotChoice>.BroadcastValue)
)]
[assembly: DxAotMessage(
    typeof(DxMessaging.Tests.Runtime.Core.ClosedAotMessages<DxMessaging.Tests.Runtime.Core.AotChoice>.BroadcastReference)
)]

namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using NUnit.Framework;
    using BusType = DxMessaging.Core.MessageBus.MessageBus;

    /// <summary>Exercises startup roots without typed registration or typed emission.</summary>
    /// <remarks>
    /// 2026-11-11: Closed generic messages previously had no generated startup bridges.
    /// These dedicated payloads never pass through a concrete typed emit or registration.
    /// The IL2CPP standalone leg proves the AOT path; Mono uses its reflection fallback.
    /// </remarks>
    [Category("Contract")]
    public sealed class ClosedGenericAotBridgeTests
    {
        [Test]
        public void FirstUntypedDispatchUsesSelectedClosedGenericBridge(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(false, true)] bool referencePayload,
            [Values(false, true)] bool enumArgument
        )
        {
            BusType bus = new();
            using LeakWatcher leaks = new(
                bus: bus,
                label: $"{scenario.Kind}/{referencePayload}/{enumArgument}"
            );
            InstanceId context = new(0x6A17_4001);
            Assert.DoesNotThrow(
                () =>
                {
                    if (enumArgument)
                    {
                        Dispatch(bus, scenario.Kind, referencePayload, AotChoice.Second, context);
                    }
                    else
                    {
                        Dispatch(bus, scenario.Kind, referencePayload, true, context);
                    }
                },
                $"[{scenario.Kind}] First untyped closed-generic dispatch must have a startup bridge."
            );
            Assert.That(
                bus.EmissionId,
                Is.EqualTo(1),
                $"[{scenario.Kind}] The bridge must reach typed bus dispatch exactly once."
            );
        }

        private static void Dispatch<T>(
            IMessageBus bus,
            MessageKind kind,
            bool referencePayload,
            T value,
            InstanceId context
        )
        {
            switch (kind)
            {
                case MessageKind.Untargeted:
                    if (referencePayload)
                    {
                        bus.UntypedUntargetedBroadcast(
                            new ClosedAotMessages<T>.UntargetedReference(value)
                        );
                    }
                    else
                    {
                        bus.UntypedUntargetedBroadcast(
                            new ClosedAotMessages<T>.UntargetedValue(value)
                        );
                    }
                    break;
                case MessageKind.Targeted:
                    if (referencePayload)
                    {
                        bus.UntypedTargetedBroadcast(
                            context,
                            new ClosedAotMessages<T>.TargetedReference(value)
                        );
                    }
                    else
                    {
                        bus.UntypedTargetedBroadcast(
                            context,
                            new ClosedAotMessages<T>.TargetedValue(value)
                        );
                    }
                    break;
                case MessageKind.Broadcast:
                    if (referencePayload)
                    {
                        bus.UntypedSourcedBroadcast(
                            context,
                            new ClosedAotMessages<T>.BroadcastReference(value)
                        );
                    }
                    else
                    {
                        bus.UntypedSourcedBroadcast(
                            context,
                            new ClosedAotMessages<T>.BroadcastValue(value)
                        );
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
            }
        }
    }

    public enum AotChoice
    {
        First,
        Second,
    }

    public static partial class ClosedAotMessages<T>
    {
        [DxUntargetedMessage]
        public readonly partial struct UntargetedValue
        {
            public readonly T value;

            public UntargetedValue(T value)
            {
                this.value = value;
            }
        }

        public sealed class UntargetedReference
            : DxMessaging.Core.Messages.IUntargetedMessage<UntargetedReference>
        {
            public readonly T value;

            public UntargetedReference(T value)
            {
                this.value = value;
            }
        }

        [DxTargetedMessage]
        public readonly partial struct TargetedValue
        {
            public readonly T value;

            public TargetedValue(T value)
            {
                this.value = value;
            }
        }

        public sealed class TargetedReference
            : DxMessaging.Core.Messages.ITargetedMessage<TargetedReference>
        {
            public readonly T value;

            public TargetedReference(T value)
            {
                this.value = value;
            }
        }

        [DxBroadcastMessage]
        public readonly partial struct BroadcastValue
        {
            public readonly T value;

            public BroadcastValue(T value)
            {
                this.value = value;
            }
        }

        public sealed class BroadcastReference
            : DxMessaging.Core.Messages.IBroadcastMessage<BroadcastReference>
        {
            public readonly T value;

            public BroadcastReference(T value)
            {
                this.value = value;
            }
        }
    }
}
#endif
