namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.Diagnostics;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Runtime;
    using NUnit.Framework;

    /// <summary>Preserves a diagnosed delivery's origin across successful teardown.</summary>
    /// <remarks>
    /// 2026-10-20: The previous bus anchor can retain an old bus after disabled
    /// retargeting, but clearing it during successful teardown loses the trace
    /// still owed by the current callback. These tests preserve that contract
    /// before any ownership optimization; no Runtime change is part of this fixture.
    /// </remarks>
    [TestFixture]
    public sealed class DiagnosticOriginAfterDisableTests
    {
        private static readonly InstanceId Context = new InstanceId(981);

        private static IEnumerable<TestCaseData> Cases()
        {
            foreach (
                MessageScenario scenario in MessageScenarios.WithAndWithoutPostProcessorIncludingWithoutContext
            )
            {
                foreach (bool action in new[] { false, true })
                {
                    if (
                        scenario.Kind == MessageKind.Untargeted
                        && scenario.UsePostProcessor
                        && action
                    )
                    {
                        continue;
                    }
                    foreach (bool retarget in new[] { false, true })
                    {
                        yield return Case(scenario, action, false, retarget);
                    }
                }
            }
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            {
                foreach (bool action in new[] { false, true })
                {
                    foreach (bool retarget in new[] { false, true })
                    {
                        yield return Case(scenario, action, true, retarget);
                    }
                }
            }
        }

        private static TestCaseData Case(
            MessageScenario scenario,
            bool action,
            bool global,
            bool retarget
        ) =>
            new TestCaseData(scenario, action, global, retarget).SetName(
                $"DiagnosticOrigin_{scenario.Kind}_Post{scenario.UsePostProcessor}_Action{action}_Global{global}_Retarget{retarget}"
            );

        [TestCaseSource(nameof(Cases))]
        public void SuccessfulDisablePreservesTheOriginUntilTheCallbackReturns(
            MessageScenario scenario,
            bool action,
            bool global,
            bool retarget
        )
        {
            string label = $"[{scenario.Kind}] action={action},global={global},retarget={retarget}";
            bool previousDebug = MessagingDebug.enabled;
            MessagingDebug.enabled = false;
            MessageBus original = new MessageBus { DiagnosticsMode = true };
            MessageBus replacement = new MessageBus { DiagnosticsMode = true };
            MessageBus neutral = new MessageBus();
            using LeakWatcher oldLeaks = new LeakWatcher(original, label: label + ": original");
            using LeakWatcher newLeaks = new LeakWatcher(
                replacement,
                label: label + ": replacement"
            );
            MessageHandler handler = new MessageHandler(new InstanceId(982), neutral)
            {
                active = true,
            };
            MessageRegistrationToken token = MessageRegistrationToken.Create(handler, original);
            token.DiagnosticMode = true;
            bool armed = true;
            int calls = 0;
            MessageBus observedBus = original;
            List<long> observedOrigins = new List<long>();
            Action callback = () =>
            {
                calls++;
                observedOrigins.Add(observedBus.EmissionId);
                if (armed)
                {
                    armed = false;
                    token.Disable();
                    if (retarget)
                    {
                        token.RetargetMessageBus(
                            replacement,
                            MessageBusRebindMode.PreserveRegistrations
                        );
                    }
                }
            };
            try
            {
                MessageRegistrationHandle handle = global
                    ? RegisterGlobal(token, action, callback)
                    : RegisterHandler(token, scenario, action, callback);
                token.Enable();
                Assert.That(
                    MessageBusInspection.CountLiveRegistrations(original),
                    Is.EqualTo(1),
                    label + ": actual original registration"
                );
                Emit(original, scenario);
                Assert.That(calls, Is.EqualTo(1), label + ": exactly one original callback");
                Assert.That(token.Enabled, Is.False, label + ": callback disable succeeds");
                Assert.That(
                    MessageBusInspection.CountLiveRegistrations(original),
                    Is.Zero,
                    label + ": consumed original cleanup"
                );
                Assert.That(
                    MessageBusInspection.CountLiveRegistrations(replacement),
                    Is.Zero,
                    label + ": retarget does not enable staging"
                );
                Assert.That(
                    token._metadata.Count,
                    Is.EqualTo(1),
                    label + ": original staged handle survives"
                );
                Assert.That(
                    token._emissionBuffer.Count,
                    Is.EqualTo(1),
                    label + ": one diagnosed original delivery"
                );
                MessageEmissionData first = token._emissionBuffer[0];
                Assert.That(
                    0L < observedOrigins[0],
                    Is.True,
                    label + ": positive originating emission ID"
                );
                Assert.That(
                    first.traceId,
                    Is.EqualTo(observedOrigins[0]),
                    label + ": preserve in-flight origin after disable/retarget"
                );
                Assert.That(
                    first.registrationHandle,
                    Is.EqualTo(handle),
                    label + ": observing original handle"
                );
                Assert.That(
                    first.message.MessageType,
                    Is.EqualTo(typeof(ProbeMessage)),
                    label + ": diagnosed payload type"
                );
                Assert.That(
                    token._callCounts[handle],
                    Is.EqualTo(1),
                    label + ": original call count"
                );
                MessageBus next = retarget ? replacement : original;
                for (int i = 0; i < 4; i++)
                {
                    QuietMessage quiet = default;
                    next.UntargetedBroadcast(ref quiet);
                }
                Assert.That(calls, Is.EqualTo(1), label + ": disabled staging stays silent");
                observedBus = next;
                token.Enable();
                Emit(next, scenario);
                Assert.That(calls, Is.EqualTo(2), label + ": staged callback replays once");
                Assert.That(
                    token._emissionBuffer.Count,
                    Is.EqualTo(2),
                    label + ": both diagnosed deliveries retained"
                );
                MessageEmissionData second = token._emissionBuffer[1];
                Assert.That(
                    observedOrigins[0] < observedOrigins[1],
                    Is.True,
                    label + ": deliberately distinct next emission ID"
                );
                Assert.That(
                    second.traceId,
                    Is.EqualTo(observedOrigins[1]),
                    label + ": fresh registration has its own actual origin"
                );
                Assert.That(
                    second.registrationHandle,
                    Is.EqualTo(handle),
                    label + ": same staged handle after enable"
                );
                Assert.That(
                    token._callCounts[handle],
                    Is.EqualTo(2),
                    label + ": exact cumulative delivery count"
                );
                token.Dispose();
                Assert.That(
                    MessageBusInspection.CountLiveRegistrations(original),
                    Is.Zero,
                    label + ": final original cleanup"
                );
                Assert.That(
                    MessageBusInspection.CountLiveRegistrations(replacement),
                    Is.Zero,
                    label + ": final replacement cleanup"
                );
                Assert.That(token._metadata.Count, Is.Zero, label + ": disposal clears staging");
                Assert.That(
                    token._emissionBuffer.Count,
                    Is.Zero,
                    label + ": disposal clears diagnostic history"
                );
                Emit(original, scenario);
                Emit(replacement, scenario);
                Assert.That(
                    calls,
                    Is.EqualTo(2),
                    label + ": disposed callbacks stay absent on both buses"
                );
            }
            finally
            {
                token.Dispose();
                handler.active = false;
                _ = original.Trim(force: true);
                _ = replacement.Trim(force: true);
                _ = neutral.Trim(force: true);
                MessagingDebug.enabled = previousDebug;
            }
        }

        private static void Emit(MessageBus bus, MessageScenario scenario)
        {
            ProbeMessage message = default;
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

        private static MessageRegistrationHandle RegisterHandler(
            MessageRegistrationToken token,
            MessageScenario scenario,
            bool action,
            Action record
        )
        {
            Action<ProbeMessage> scalar = _ => record();
            Action<InstanceId, ProbeMessage> context = (_, __) => record();
            MessageHandler.FastHandler<ProbeMessage> fast = (in ProbeMessage _) => record();
            MessageHandler.FastHandlerWithContext<ProbeMessage> fastContext = (
                in InstanceId _,
                in ProbeMessage __
            ) => record();
            return (scenario.Kind, scenario.UsePostProcessor, action) switch
            {
                (MessageKind.Untargeted, false, true) => token.RegisterUntargeted(scalar),
                (MessageKind.Untargeted, false, false) => token.RegisterUntargeted(fast),
                (MessageKind.Untargeted, true, _) => token.RegisterUntargetedPostProcessor(fast),
                (MessageKind.Targeted, false, true) => token.RegisterTargeted(Context, scalar),
                (MessageKind.Targeted, false, false) => token.RegisterTargeted(Context, fast),
                (MessageKind.Targeted, true, true) => token.RegisterTargetedPostProcessor(
                    Context,
                    scalar
                ),
                (MessageKind.Targeted, true, false) => token.RegisterTargetedPostProcessor(
                    Context,
                    fast
                ),
                (MessageKind.Broadcast, false, true) => token.RegisterBroadcast(Context, scalar),
                (MessageKind.Broadcast, false, false) => token.RegisterBroadcast(Context, fast),
                (MessageKind.Broadcast, true, true) => token.RegisterBroadcastPostProcessor(
                    Context,
                    scalar
                ),
                (MessageKind.Broadcast, true, false) => token.RegisterBroadcastPostProcessor(
                    Context,
                    fast
                ),
                (MessageKind.TargetedWithoutTargeting, false, true) =>
                    token.RegisterTargetedWithoutTargeting(context),
                (MessageKind.TargetedWithoutTargeting, false, false) =>
                    token.RegisterTargetedWithoutTargeting(fastContext),
                (MessageKind.TargetedWithoutTargeting, true, true) =>
                    token.RegisterTargetedWithoutTargetingPostProcessor(context),
                (MessageKind.TargetedWithoutTargeting, true, false) =>
                    token.RegisterTargetedWithoutTargetingPostProcessor(fastContext),
                (MessageKind.BroadcastWithoutSource, false, true) =>
                    token.RegisterBroadcastWithoutSource(context),
                (MessageKind.BroadcastWithoutSource, false, false) =>
                    token.RegisterBroadcastWithoutSource(fastContext),
                (MessageKind.BroadcastWithoutSource, true, true) =>
                    token.RegisterBroadcastWithoutSourcePostProcessor(context),
                (MessageKind.BroadcastWithoutSource, true, false) =>
                    token.RegisterBroadcastWithoutSourcePostProcessor(fastContext),
                _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
            };
        }

        private static MessageRegistrationHandle RegisterGlobal(
            MessageRegistrationToken token,
            bool action,
            Action record
        )
        {
            if (action)
            {
                return token.RegisterGlobalAcceptAll(
                    (IUntargetedMessage _) => record(),
                    (InstanceId _, ITargetedMessage __) => record(),
                    (InstanceId _, IBroadcastMessage __) => record()
                );
            }
            return token.RegisterGlobalAcceptAll(
                (in IUntargetedMessage _) => record(),
                (in InstanceId _, in ITargetedMessage __) => record(),
                (in InstanceId _, in IBroadcastMessage __) => record()
            );
        }

        private readonly struct ProbeMessage
            : IUntargetedMessage,
                ITargetedMessage,
                IBroadcastMessage
        {
            public Type MessageType => typeof(ProbeMessage);
        }

        private readonly struct QuietMessage : IUntargetedMessage
        {
            public Type MessageType => typeof(QuietMessage);
        }
    }
}
