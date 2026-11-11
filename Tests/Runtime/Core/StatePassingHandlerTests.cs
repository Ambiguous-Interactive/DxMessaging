namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;

    /// <summary>Checks explicit state bindings across token routes and lifecycle replay.</summary>
    /// <remarks>2026-11-11: immutable message probes keep readonly-reference callbacks valid under the CI analyzer contract.</remarks>
    [TestFixture]
    public sealed class StatePassingHandlerTests
    {
        private static readonly InstanceId Context = new InstanceId(962);

        /// <summary>Checks copied value state and shared reference state survive replay and bus retargeting.</summary>
        [Test]
        public void StateBindingsPreserveValuesAcrossReplayAndBusRetarget(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.WithAndWithoutPostProcessorIncludingWithoutContext)
            )]
                MessageScenario scenario,
            [Values(false, true)] bool valueState
        )
        {
            MessageBus first = new MessageBus();
            MessageBus second = new MessageBus();
            using LeakWatcher firstLeaks = new LeakWatcher(first);
            using LeakWatcher secondLeaks = new LeakWatcher(second);
            MessageHandler handler = new MessageHandler(new InstanceId(961), first)
            {
                active = true,
            };
            using MessageRegistrationToken token = MessageRegistrationToken.Create(handler, first);
            Counter counter = new Counter { Seed = 7 };
            MessageRegistrationHandle handle;
            if (valueState)
            {
                ValueState state = new ValueState { Counter = counter, Seed = 7 };
                handle = Register(token, scenario, state, RecordValue, RecordValueWithContext);
                state.Seed = 99;
            }
            else
            {
                handle = Register(
                    token,
                    scenario,
                    counter,
                    RecordReference,
                    RecordReferenceWithContext
                );
                counter.Seed = 9;
            }
            Emit(first, scenario);
            Assert.That(
                counter.Values,
                Is.Empty,
                $"[{scenario}, {valueState}] Disabled staging must not invoke the binding."
            );
            token.Enable();
            Emit(first, scenario);
            token.Disable();
            Emit(first, scenario);
            token.Enable();
            Emit(first, scenario);
            token.RetargetMessageBus(second, MessageBusRebindMode.RebindActive);
            Emit(first, scenario);
            Emit(second, scenario);
            int expected = valueState ? 10 : 12;
            Assert.That(
                counter.Values,
                Is.EqualTo(new[] { expected, expected, expected }),
                $"[{scenario}, {valueState}] Value state is copied; reference state stays shared across replays."
            );
            bool contextAware =
                scenario.Kind == MessageKind.TargetedWithoutTargeting
                || scenario.Kind == MessageKind.BroadcastWithoutSource;
            Assert.That(
                counter.Contexts,
                Is.EqualTo(
                    contextAware ? new[] { Context, Context, Context } : Array.Empty<InstanceId>()
                ),
                $"[{scenario}, {valueState}] Context-free registrations receive the actual context."
            );
            token.RemoveRegistration(handle);
            token.Disable();
            token.Enable();
            Emit(second, scenario);
            Assert.That(
                counter.Values.Count,
                Is.EqualTo(3),
                $"[{scenario}, {valueState}] Removing the returned handle cancels the state binding permanently."
            );
        }

        [Test]
        public void SeparateBindingsToTheSameStaticHandlerKeepTheirOwnState(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.WithAndWithoutPostProcessorIncludingWithoutContext)
            )]
                MessageScenario scenario
        )
        {
            MessageBus bus = new MessageBus();
            using LeakWatcher leaks = new LeakWatcher(bus);
            MessageHandler handler = new MessageHandler(new InstanceId(961), bus) { active = true };
            using MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            Counter first = new Counter { Seed = 1 };
            Counter second = new Counter { Seed = 2 };
            MessageRegistrationHandle firstHandle = Register(
                token,
                scenario,
                first,
                RecordReference,
                RecordReferenceWithContext
            );
            Register(token, scenario, second, RecordReference, RecordReferenceWithContext);
            token.Enable();
            Emit(bus, scenario);
            Assert.That(
                first.Values,
                Is.EqualTo(new[] { 4 }),
                $"[{scenario}] First state must remain independent."
            );
            Assert.That(
                second.Values,
                Is.EqualTo(new[] { 5 }),
                $"[{scenario}] Second state must not be deduplicated with another state."
            );
            token.RemoveRegistration(firstHandle);
            Emit(bus, scenario);
            Assert.That(
                first.Values.Count,
                Is.EqualTo(1),
                $"[{scenario}] Removing one state must retire only that binding."
            );
            Assert.That(
                second.Values,
                Is.EqualTo(new[] { 5, 5 }),
                $"[{scenario}] Other state must continue receiving messages."
            );
        }

        /// <summary>Checks null callbacks are rejected before any registration is staged.</summary>
        [Test]
        public void NullHandlerIsRejectedWithoutStaging(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.WithAndWithoutPostProcessorIncludingWithoutContext)
            )]
                MessageScenario scenario,
            [Values(false, true)] bool enabled
        )
        {
            MessageBus bus = new MessageBus();
            using LeakWatcher leaks = new LeakWatcher(bus);
            MessageHandler handler = new MessageHandler(new InstanceId(961), bus) { active = true };
            using MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            if (enabled)
            {
                token.Enable();
            }
            ArgumentNullException error = Assert.Throws<ArgumentNullException>(
                () => Register<Counter>(token, scenario, null, null, null),
                $"[{scenario}, {enabled}] Null callback must fail at the registration call."
            );
            Assert.That(
                error.ParamName,
                Is.EqualTo("handler"),
                $"[{scenario}, {enabled}] The error identifies the missing callback."
            );
            Assert.That(
                token._metadata.Count,
                Is.Zero,
                $"[{scenario}, {enabled}] Rejected registration cannot leave inaccessible state behind."
            );
        }

        [Test]
        public void NullReferenceStateIsPassedToTheHandler()
        {
            MessageBus bus = new MessageBus();
            using LeakWatcher leaks = new LeakWatcher(bus);
            MessageHandler handler = new MessageHandler(new InstanceId(961), bus) { active = true };
            using MessageRegistrationToken token = MessageRegistrationToken.Create(handler, bus);
            token.RegisterUntargeted<ReferenceMessage, object>(
                null,
                static (object state, in ReferenceMessage message) =>
                    message.SeenNull = state == null
            );
            token.Enable();
            ReferenceMessage message = new ReferenceMessage();
            bus.UntargetedBroadcast(ref message);
            Assert.That(
                message.SeenNull,
                Is.True,
                "Null state is a valid value and must reach the user callback."
            );
        }

        /// <summary>Registers one probe callback on the selected message route and processing phase.</summary>
        private static MessageRegistrationHandle Register<TState>(
            MessageRegistrationToken token,
            MessageScenario scenario,
            TState state,
            MessageRegistrationToken.StateHandler<ProbeMessage, TState> scalar,
            MessageRegistrationToken.StateHandlerWithContext<ProbeMessage, TState> contextual
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                    return scenario.UsePostProcessor
                        ? token.RegisterUntargetedPostProcessor(state, scalar)
                        : token.RegisterUntargeted(state, scalar);
                case MessageKind.Targeted:
                    return scenario.UsePostProcessor
                        ? token.RegisterTargetedPostProcessor(Context, state, scalar)
                        : token.RegisterTargeted(Context, state, scalar);
                case MessageKind.Broadcast:
                    return scenario.UsePostProcessor
                        ? token.RegisterBroadcastPostProcessor(Context, state, scalar)
                        : token.RegisterBroadcast(Context, state, scalar);
                case MessageKind.TargetedWithoutTargeting:
                    return scenario.UsePostProcessor
                        ? token.RegisterTargetedWithoutTargetingPostProcessor(state, contextual)
                        : token.RegisterTargetedWithoutTargeting(state, contextual);
                case MessageKind.BroadcastWithoutSource:
                    return scenario.UsePostProcessor
                        ? token.RegisterBroadcastWithoutSourcePostProcessor(state, contextual)
                        : token.RegisterBroadcastWithoutSource(state, contextual);
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario));
            }
        }

        /// <summary>Emits one populated probe on the selected message route.</summary>
        private static void Emit(IMessageBus bus, MessageScenario scenario)
        {
            ProbeMessage message = new ProbeMessage(3);
            InstanceId context = Context;
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                    bus.UntargetedBroadcast(ref message);
                    break;
                case MessageKind.Targeted:
                case MessageKind.TargetedWithoutTargeting:
                    bus.TargetedBroadcast(ref context, ref message);
                    break;
                case MessageKind.Broadcast:
                case MessageKind.BroadcastWithoutSource:
                    bus.SourcedBroadcast(ref context, ref message);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario));
            }
        }

        private static void RecordReference(Counter state, in ProbeMessage message) =>
            state.Values.Add(state.Seed + message.Value);

        private static void RecordReferenceWithContext(
            Counter state,
            in InstanceId context,
            in ProbeMessage message
        )
        {
            RecordReference(state, in message);
            state.Contexts.Add(context);
        }

        private static void RecordValue(ValueState state, in ProbeMessage message) =>
            state.Counter.Values.Add(state.Seed + message.Value);

        private static void RecordValueWithContext(
            ValueState state,
            in InstanceId context,
            in ProbeMessage message
        )
        {
            RecordValue(state, in message);
            state.Counter.Contexts.Add(context);
        }

        private sealed class Counter
        {
            internal int Seed;
            internal readonly List<int> Values = new List<int>();
            internal readonly List<InstanceId> Contexts = new List<InstanceId>();
        }

        private struct ValueState
        {
            internal Counter Counter;
            internal int Seed;
        }

        private readonly struct ProbeMessage
            : IUntargetedMessage,
                ITargetedMessage,
                IBroadcastMessage
        {
            internal readonly int Value;
            public Type MessageType => typeof(ProbeMessage);

            internal ProbeMessage(int value)
            {
                Value = value;
            }
        }

        private sealed class ReferenceMessage : IUntargetedMessage
        {
            internal bool SeenNull;
            public Type MessageType => typeof(ReferenceMessage);
        }
    }
}
