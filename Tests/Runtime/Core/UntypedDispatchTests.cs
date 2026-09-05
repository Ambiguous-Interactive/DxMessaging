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
    /// <remarks>
    /// 2026-09-05 (#529): IL2CPP 2021 found the registered bridge but lacked native code for
    /// its generic interface dispatch target. Registration-only cases deliberately never emit
    /// this payload through a concrete typed call, which could mask the missing native target.
    /// Custom-bus rooting uses a distinct payload type. Interceptors replace readonly payloads
    /// through the ref parameter to preserve mutation coverage without an analyzer suppression.
    /// </remarks>
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
            NonEmptyManualMessage original = new(marker, value);
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
            object boxed = original;
            Action emit = CreateEmit<NonEmptyManualMessage>(
                bus,
                scenario.Kind,
                context,
                boxed,
                invokeAotBridge
            );
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
                    message = new NonEmptyManualMessage(message.Marker + 9, message.Value + 7);
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

        [Test]
        public void AotBridgeInvokesSuppliedCustomBusWithoutUnwrappingIt(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            BusType bus = new() { DiagnosticsMode = false };
            using LeakWatcher leaks = new(bus: bus, label: $"{scenario.Kind} custom AOT bridge");
            DispatchSpy customBus = new(bus);
            InstanceId context = new(0x6A17_5002);
            CustomBusMessage payload = new(173);
            MessageHandler handler = new(new InstanceId(0x6A17_5001), bus) { active = true };
            using MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            token.DiagnosticMode = false;
            token.Enable();
            int calls = 0;
            int received = 0;
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                    // Root this test-only custom generic implementation explicitly. Its distinct
                    // message type must never root the registration-only regression payload.
                    customBus.UntargetedBroadcast(ref payload);
                    _ = token.RegisterUntargeted<CustomBusMessage>(Receive);
                    break;
                case MessageKind.Targeted:
                    customBus.TargetedBroadcast(ref context, ref payload);
                    _ = token.RegisterTargeted<CustomBusMessage>(context, Receive);
                    break;
                case MessageKind.Broadcast:
                    customBus.SourcedBroadcast(ref context, ref payload);
                    _ = token.RegisterBroadcast<CustomBusMessage>(context, Receive);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario));
            }
            string label = $"[{scenario.Kind}] custom AOT bridge";
            Assert.That(
                customBus.Calls,
                Is.EqualTo(1),
                label + ": setup must root exactly one custom typed call."
            );
            Assert.That(
                bus.EmissionId,
                Is.EqualTo(1),
                label + ": the explicit custom seed must forward once."
            );
            Assert.That(
                calls,
                Is.Zero,
                label + ": setup must run before the callback is registered."
            );
            object boxed = payload;
            Action emit = CreateEmit<CustomBusMessage>(
                customBus,
                scenario.Kind,
                context,
                boxed,
                invokeAotBridge: true
            );
            emit();
            Assert.That(
                customBus.Calls,
                Is.EqualTo(2),
                label + ": the real bridge must invoke the supplied custom bus."
            );
            Assert.That(
                bus.EmissionId,
                Is.EqualTo(2),
                label + ": the bridge must forward once after the seed."
            );
            Assert.That(
                calls,
                Is.EqualTo(1),
                label + ": only the bridge dispatch must reach the callback."
            );
            Assert.That(
                received,
                Is.EqualTo(173),
                label + ": custom forwarding must preserve the boxed payload."
            );
            return;

            void Receive(in CustomBusMessage message)
            {
                ++calls;
                received = message.Value;
            }
        }

        private sealed class DispatchSpy : DelegatingMessageBus
        {
            internal DispatchSpy(IMessageBus inner)
                : base(inner) { }

            internal int Calls { get; private set; }

            public override void UntargetedBroadcast<TMessage>(ref TMessage message)
            {
                ++Calls;
                base.UntargetedBroadcast(ref message);
            }

            public override void TargetedBroadcast<TMessage>(
                ref InstanceId target,
                ref TMessage message
            )
            {
                ++Calls;
                base.TargetedBroadcast(ref target, ref message);
            }

            public override void SourcedBroadcast<TMessage>(
                ref InstanceId source,
                ref TMessage message
            )
            {
                ++Calls;
                base.SourcedBroadcast(ref source, ref message);
            }
        }

        private readonly struct CustomBusMessage
            : IUntargetedMessage,
                ITargetedMessage,
                IBroadcastMessage
        {
            internal CustomBusMessage(int value) => Value = value;

            internal int Value { get; }
            public Type MessageType => typeof(CustomBusMessage);
        }

        private static Action CreateEmit<T>(
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
            // Registration must root this exact closed bridge and its typed dispatch target.
            // Reflection lets Mono/.NET execute the same AOT body as IL2CPP public dispatch.
            MethodInfo method = typeof(BusType)
                .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
                ?.MakeGenericMethod(typeof(T));
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

        private readonly struct NonEmptyManualMessage
            : IUntargetedMessage,
                ITargetedMessage,
                IBroadcastMessage
        {
            internal readonly long Marker;
            internal readonly int Value;

            internal NonEmptyManualMessage(long marker, int value)
            {
                Marker = marker;
                Value = value;
            }

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
