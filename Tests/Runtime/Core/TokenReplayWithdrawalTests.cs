namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Runtime;
    using NUnit.Framework;
    using BusType = DxMessaging.Core.MessageBus.MessageBus;

    /// <summary>Checks cancellation of staged registrations during token replay.</summary>
    /// <remarks>2026-10-17: A queued callback could activate after an earlier bus callback removed its handle.</remarks>
    [TestFixture]
    public sealed class TokenReplayWithdrawalTests
    {
        private static readonly InstanceId Context = new InstanceId(841);

        private static IEnumerable<(MessageScenario Scenario, bool Action, int Family)> Shapes()
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
                    yield return (scenario, action, 0);
                }
            }
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            {
                yield return (scenario, false, 1);
                yield return (scenario, false, 2);
                yield return (scenario, true, 2);
            }
        }

        private static IEnumerable<TestCaseData> ReplayCases()
        {
            foreach (var shape in Shapes())
            {
                foreach (bool diagnostics in new[] { false, true })
                {
                    foreach (int count in new[] { 2, 8 })
                    {
                        foreach (int mutation in new[] { 0, 1, 2, 3, 4 })
                        {
                            yield return new TestCaseData(
                                shape.Scenario,
                                shape.Action,
                                shape.Family,
                                diagnostics,
                                count,
                                mutation
                            ).SetName(
                                "ReplayWithdrawal_family"
                                    + shape.Family
                                    + "_"
                                    + shape.Scenario
                                    + "_action"
                                    + shape.Action
                                    + "_diagnostics"
                                    + diagnostics
                                    + "_count"
                                    + count
                                    + "_mutation"
                                    + mutation
                            );
                        }
                    }
                }
            }
        }

        [TestCaseSource(nameof(ReplayCases))]
        public void ReplaySkipsRegistrationsRemovedBeforeTheirTurn(
            MessageScenario scenario,
            bool action,
            int family,
            bool diagnostics,
            int count,
            int mutation
        )
        {
            string label =
                scenario
                + ", action="
                + action
                + ", family="
                + family
                + ", diagnostics="
                + diagnostics
                + ", count="
                + count
                + ", mutation="
                + mutation;
            using CaseState state = new CaseState(diagnostics);
            MessageRegistrationHandle[] handles = new MessageRegistrationHandle[count];
            List<string> all = new List<string>(count);
            for (int index = 0; index < count; ++index)
            {
                string callback =
                    index == 0 ? "first"
                    : index == count - 1 ? "removed"
                    : "keep-" + index;
                all.Add(callback);
                handles[index] = Register(
                    state.Token,
                    scenario,
                    action,
                    family,
                    () => state.Calls.Add(callback)
                );
            }
            MessageRegistrationHandle victim = handles[count - 1];
            MessageRegistrationHandle replacement = default;
            int mutations = 0;
            state.Bus.OnFirstRegistration = () =>
            {
                ++mutations;
                if (mutation == 0)
                {
                    return;
                }
                if (mutation == 3)
                {
                    state.Token.UnregisterAll();
                    return;
                }
                if (mutation == 4)
                {
                    state.Token.Dispose();
                    return;
                }
                state.Token.RemoveRegistration(victim);
                if (mutation == 2)
                {
                    replacement = Register(
                        state.Token,
                        scenario,
                        action,
                        family,
                        () => state.Calls.Add("replacement")
                    );
                }
            };
            state.Token.Enable();
            Assert.That(mutations, Is.EqualTo(1), label);
            if (mutation == 2)
            {
                Assert.That(
                    replacement.Slot,
                    Is.EqualTo(victim.Slot),
                    label + ": actual slot reuse"
                );
                Assert.That(replacement, Is.Not.EqualTo(victim), label + ": fresh opaque identity");
            }
            state.Emit(scenario);
            List<string> firstExpected = new List<string>(all);
            if (3 <= mutation)
            {
                firstExpected.RemoveRange(1, firstExpected.Count - 1);
            }
            else if (mutation != 0)
            {
                firstExpected.RemoveAt(firstExpected.Count - 1);
            }
            TestContext.WriteLine(
                label
                    + "; first="
                    + string.Join(",", state.Calls)
                    + "; metadata="
                    + state.Token._metadata.Count
            );
            Assert.That(
                state.Calls,
                Is.EqualTo(firstExpected),
                label + ": a registration removed before its replay turn must stay absent"
            );
            Assert.That(
                state.Token._metadata.Count,
                Is.EqualTo(
                    3 <= mutation ? 0
                    : mutation == 1 ? count - 1
                    : count
                ),
                label
            );

            state.Token.Disable();
            state.Token.Enable();
            state.Calls.Clear();
            state.Emit(scenario);
            List<string> replayExpected =
                3 <= mutation ? new List<string>() : new List<string>(firstExpected);
            if (mutation == 2)
            {
                replayExpected.Add("replacement");
            }
            Assert.That(
                state.Calls,
                Is.EqualTo(replayExpected),
                label + ": only current staged registrations activate on the next cycle"
            );
            if (mutation != 0)
            {
                state.Token.RemoveRegistration(victim);
                state.Calls.Clear();
                state.Emit(scenario);
                Assert.That(
                    state.Calls,
                    Is.EqualTo(replayExpected),
                    label + ": the removed handle cannot affect a reused slot"
                );
            }
        }

        private static MessageRegistrationHandle Register(
            MessageRegistrationToken token,
            MessageScenario scenario,
            bool action,
            int family,
            Action record
        )
        {
            return family switch
            {
                0 => RegisterHandler(token, scenario, action, record),
                1 => RegisterInterceptor(token, scenario, record),
                2 => RegisterGlobal(token, action, record),
                _ => throw new ArgumentOutOfRangeException(nameof(family)),
            };
        }

        private sealed class CaseState : IDisposable
        {
            internal readonly BusType Inner;
            internal readonly ReplayBus Bus;
            internal readonly MessageHandler Handler;
            internal readonly MessageRegistrationToken Token;
            internal readonly List<string> Calls = new List<string>();
            private readonly LeakWatcher _leaks;

            internal CaseState(bool diagnostics)
            {
                Inner = new BusType { DiagnosticsMode = diagnostics };
                Bus = new ReplayBus(Inner);
                Handler = new MessageHandler(new InstanceId(840), Bus) { active = true };
                _leaks = LeakWatcher.WatchWithSlots(Bus, handler: Handler);
                Token = MessageRegistrationToken.Create(Handler, Bus);
                Token.DiagnosticMode = diagnostics;
            }

            internal void Emit(MessageScenario scenario)
            {
                ProbeMessage message = default;
                InstanceId context = Context;
                switch (scenario.Kind)
                {
                    case MessageKind.Untargeted:
                        Bus.UntargetedBroadcast(ref message);
                        break;
                    case MessageKind.Targeted:
                    case MessageKind.TargetedWithoutTargeting:
                        Bus.TargetedBroadcast(ref context, ref message);
                        break;
                    case MessageKind.Broadcast:
                    case MessageKind.BroadcastWithoutSource:
                        Bus.SourcedBroadcast(ref context, ref message);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(scenario));
                }
            }

            public void Dispose()
            {
                try
                {
                    Token.Dispose();
                    Assert.That(
                        Token._metadata.Count,
                        Is.Zero,
                        "Successful disposal clears token metadata."
                    );
                    Assert.That(
                        Token._callCounts.Count,
                        Is.Zero,
                        "Successful disposal clears token call counts."
                    );
                    Assert.That(
                        Token._emissionBuffer.Count,
                        Is.Zero,
                        "Successful disposal clears token emission history."
                    );
                }
                finally
                {
                    Handler.active = false;
                    Inner.Trim(force: true);
                    _leaks.Dispose();
                }
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

        private static MessageRegistrationHandle RegisterInterceptor(
            MessageRegistrationToken token,
            MessageScenario scenario,
            Action record
        )
        {
            bool Scalar(ref ProbeMessage message)
            {
                record();
                return true;
            }
            bool Contextual(ref InstanceId context, ref ProbeMessage message)
            {
                record();
                return true;
            }
            return scenario.Kind switch
            {
                MessageKind.Untargeted => token.RegisterUntargetedInterceptor<ProbeMessage>(Scalar),
                MessageKind.Targeted => token.RegisterTargetedInterceptor<ProbeMessage>(Contextual),
                MessageKind.Broadcast => token.RegisterBroadcastInterceptor<ProbeMessage>(
                    Contextual
                ),
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

        private sealed class ReplayBus : DelegatingMessageBus
        {
            internal Action OnFirstRegistration;

            internal ReplayBus(IMessageBus inner)
                : base(inner) { }

            private MessageBusRegistration After(MessageBusRegistration registration)
            {
                Action callback = OnFirstRegistration;
                OnFirstRegistration = null;
                callback?.Invoke();
                return registration;
            }

            public override MessageBusRegistration RegisterUntargeted<T>(
                MessageHandler handler,
                int priority = 0
            )
            {
                return After(base.RegisterUntargeted<T>(handler, priority));
            }

            public override MessageBusRegistration RegisterUntargetedPostProcessor<T>(
                MessageHandler handler,
                int priority = 0
            )
            {
                return After(base.RegisterUntargetedPostProcessor<T>(handler, priority));
            }

            public override MessageBusRegistration RegisterTargeted<T>(
                InstanceId target,
                MessageHandler handler,
                int priority = 0
            )
            {
                return After(base.RegisterTargeted<T>(target, handler, priority));
            }

            public override MessageBusRegistration RegisterTargetedPostProcessor<T>(
                InstanceId target,
                MessageHandler handler,
                int priority = 0
            )
            {
                return After(base.RegisterTargetedPostProcessor<T>(target, handler, priority));
            }

            public override MessageBusRegistration RegisterTargetedWithoutTargeting<T>(
                MessageHandler handler,
                int priority = 0
            )
            {
                return After(base.RegisterTargetedWithoutTargeting<T>(handler, priority));
            }

            public override MessageBusRegistration RegisterTargetedWithoutTargetingPostProcessor<T>(
                MessageHandler handler,
                int priority = 0
            )
            {
                return After(
                    base.RegisterTargetedWithoutTargetingPostProcessor<T>(handler, priority)
                );
            }

            public override MessageBusRegistration RegisterSourcedBroadcast<T>(
                InstanceId source,
                MessageHandler handler,
                int priority = 0
            )
            {
                return After(base.RegisterSourcedBroadcast<T>(source, handler, priority));
            }

            public override MessageBusRegistration RegisterBroadcastPostProcessor<T>(
                InstanceId source,
                MessageHandler handler,
                int priority = 0
            )
            {
                return After(base.RegisterBroadcastPostProcessor<T>(source, handler, priority));
            }

            public override MessageBusRegistration RegisterSourcedBroadcastWithoutSource<T>(
                MessageHandler handler,
                int priority = 0
            )
            {
                return After(base.RegisterSourcedBroadcastWithoutSource<T>(handler, priority));
            }

            public override MessageBusRegistration RegisterBroadcastWithoutSourcePostProcessor<T>(
                MessageHandler handler,
                int priority = 0
            )
            {
                return After(
                    base.RegisterBroadcastWithoutSourcePostProcessor<T>(handler, priority)
                );
            }

            public override MessageBusRegistration RegisterGlobalAcceptAll(MessageHandler handler)
            {
                return After(base.RegisterGlobalAcceptAll(handler));
            }

            public override MessageBusRegistration RegisterUntargetedInterceptor<T>(
                IMessageBus.UntargetedInterceptor<T> interceptor,
                int priority = 0
            )
            {
                return After(base.RegisterUntargetedInterceptor(interceptor, priority));
            }

            public override MessageBusRegistration RegisterTargetedInterceptor<T>(
                IMessageBus.TargetedInterceptor<T> interceptor,
                int priority = 0
            )
            {
                return After(base.RegisterTargetedInterceptor(interceptor, priority));
            }

            public override MessageBusRegistration RegisterBroadcastInterceptor<T>(
                IMessageBus.BroadcastInterceptor<T> interceptor,
                int priority = 0
            )
            {
                return After(base.RegisterBroadcastInterceptor(interceptor, priority));
            }
        }

        private readonly struct ProbeMessage
            : IUntargetedMessage,
                ITargetedMessage,
                IBroadcastMessage
        {
            public Type MessageType => typeof(ProbeMessage);
        }
    }
}
