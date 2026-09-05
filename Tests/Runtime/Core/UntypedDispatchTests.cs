#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
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

    /// <summary>Boxed struct payloads must be copied into typed dispatch locals before invocation.</summary>
    public sealed class UntypedStructPayloadTests
    {
        [Test]
        public void BoxedPayloadValuesAndInterceptorMutationsSurviveEveryBridge(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(false, true)] bool invokeAotBridge,
            [Values(false, true)] bool mutate
        )
        {
            const long marker = 0x12345678_76543210L;
            const int value = 0x13572468;
            BusType bus = new() { DiagnosticsMode = false };
            using LeakWatcher leaks = new(
                bus: bus,
                label: $"{scenario.Kind}, aot={invokeAotBridge}, mutate={mutate}"
            );
            MessageHandler handler = new(new InstanceId(0x6A17_4001), bus) { active = true };
            using MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            token.DiagnosticMode = false;
            token.Enable();
            InstanceId context = new(0x6A17_4002);
            List<(long Marker, int Value)> observed = new();
            int interceptions = 0;
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                    _ = token.RegisterUntargeted<NonEmptyManualMessage>(Record);
                    _ = token.RegisterUntargetedInterceptor<NonEmptyManualMessage>(Intercept);
                    break;
                case MessageKind.Targeted:
                    _ = token.RegisterTargeted<NonEmptyManualMessage>(context, Record);
                    _ = token.RegisterTargetedInterceptor<NonEmptyManualMessage>(
                        InterceptWithContext
                    );
                    break;
                case MessageKind.Broadcast:
                    _ = token.RegisterBroadcast<NonEmptyManualMessage>(context, Record);
                    _ = token.RegisterBroadcastInterceptor<NonEmptyManualMessage>(
                        InterceptWithContext
                    );
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario));
            }
            NonEmptyManualMessage original = new() { Marker = marker, Value = value };
            object boxed = original;
            Action emit = CreateEmit(bus, scenario.Kind, context, boxed, invokeAotBridge);
            emit();
            emit();
            string label = $"[{scenario.Kind}] aot={invokeAotBridge}, mutate={mutate}";
            (long Marker, int Value) expected = mutate ? (marker + 9, value + 7) : (marker, value);
            CollectionAssert.AreEqual(
                new[] { expected, expected },
                observed,
                label
                    + ": both cold and cached dispatch must deliver the actual struct fields and interceptor changes."
            );
            Assert.That(
                interceptions,
                Is.EqualTo(2),
                label + ": both dispatches must execute the real interceptor."
            );
            NonEmptyManualMessage unchanged = (NonEmptyManualMessage)boxed;
            Assert.That(
                (unchanged.Marker, unchanged.Value),
                Is.EqualTo((marker, value)),
                label
                    + ": untyped dispatch must mutate a typed local copy, preserving the caller's preboxed payload."
            );
            Assert.That(
                bus.EmissionId,
                Is.EqualTo(2),
                label + ": each bridge must enter typed dispatch exactly once."
            );
            return;

            void Record(in NonEmptyManualMessage message) =>
                observed.Add((message.Marker, message.Value));

            bool Intercept(ref NonEmptyManualMessage message)
            {
                ++interceptions;
                if (mutate)
                {
                    message.Marker += 9;
                    message.Value += 7;
                }
                return true;
            }

            bool InterceptWithContext(
                ref InstanceId routedContext,
                ref NonEmptyManualMessage message
            )
            {
                Assert.That(
                    routedContext,
                    Is.EqualTo(context),
                    $"[{scenario.Kind}]: the bridge must preserve the actual target/source."
                );
                return Intercept(ref message);
            }
        }

        private static Action CreateEmit(
            IMessageBus bus,
            MessageKind kind,
            InstanceId context,
            object boxed,
            bool invokeAotBridge
        )
        {
            if (!invokeAotBridge)
            {
                return kind switch
                {
                    MessageKind.Untargeted => () =>
                        bus.UntypedUntargetedBroadcast((IUntargetedMessage)boxed),
                    MessageKind.Targeted => () =>
                        bus.UntypedTargetedBroadcast(context, (ITargetedMessage)boxed),
                    MessageKind.Broadcast => () =>
                        bus.UntypedSourcedBroadcast(context, (IBroadcastMessage)boxed),
                    _ => throw new ArgumentOutOfRangeException(nameof(kind)),
                };
            }
            string name = kind switch
            {
                MessageKind.Untargeted => "AotUntargetedBroadcast",
                MessageKind.Targeted => "AotTargetedBroadcast",
                MessageKind.Broadcast => "AotSourcedBroadcast",
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
            // Registration roots this exact closed bridge on IL2CPP. Reflection also lets
            // Mono/.NET execute the real AOT body, whose normal public path uses reflection helpers.
            MethodInfo method = typeof(BusType)
                .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
                ?.MakeGenericMethod(typeof(NonEmptyManualMessage));
            Assert.That(method, Is.Not.Null, $"[{kind}]: the production AOT bridge must exist.");
            switch (kind)
            {
                case MessageKind.Untargeted:
                    Action<IMessageBus, IUntargetedMessage> untargeted =
                        (Action<IMessageBus, IUntargetedMessage>)
                            Delegate.CreateDelegate(
                                typeof(Action<IMessageBus, IUntargetedMessage>),
                                method
                            );
                    return () => untargeted(bus, (IUntargetedMessage)boxed);
                case MessageKind.Targeted:
                    Action<IMessageBus, InstanceId, ITargetedMessage> targeted =
                        (Action<IMessageBus, InstanceId, ITargetedMessage>)
                            Delegate.CreateDelegate(
                                typeof(Action<IMessageBus, InstanceId, ITargetedMessage>),
                                method
                            );
                    return () => targeted(bus, context, (ITargetedMessage)boxed);
                default:
                    Action<IMessageBus, InstanceId, IBroadcastMessage> broadcast =
                        (Action<IMessageBus, InstanceId, IBroadcastMessage>)
                            Delegate.CreateDelegate(
                                typeof(Action<IMessageBus, InstanceId, IBroadcastMessage>),
                                method
                            );
                    return () => broadcast(bus, context, (IBroadcastMessage)boxed);
            }
        }

        private struct NonEmptyManualMessage
            : IUntargetedMessage,
                ITargetedMessage,
                IBroadcastMessage
        {
            internal long Marker;
            internal int Value;
            public Type MessageType => typeof(NonEmptyManualMessage);
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
