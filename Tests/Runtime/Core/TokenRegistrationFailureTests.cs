namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;

    /// <summary>Immediate registration failures must not create inaccessible staged callbacks.</summary>
    /// <remarks>
    /// 2026-10-17: enabled registration published an arena slot before the bus accepted it.
    /// A rejected call returned no handle but its callback reappeared after Disable/Enable.
    /// Disabled staging returns a handle before replay and deliberately remains retryable.
    /// </remarks>
    [TestFixture]
#if !UNITY_2021_3_OR_NEWER
    [NonParallelizable]
#endif
    public sealed class TokenRegistrationFailureTests
    {
        private static readonly InstanceId Context = new InstanceId(872);

        private static IEnumerable<TestCaseData> HandlerCases()
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
                        continue; // Untargeted post-processors expose only the fast overload.
                    }
                    foreach (bool diagnostics in new[] { false, true })
                    {
                        yield return new TestCaseData(scenario, action, diagnostics);
                    }
                }
            }
        }

        [TestCaseSource(nameof(HandlerCases))]
        public void RejectedHandlerDoesNotBecomeStaged(
            MessageScenario scenario,
            bool action,
            bool diagnostics
        )
        {
            using CaseState state = new CaseState(diagnostics);
            VerifyImmediateFailure(
                state,
                scenario,
                record => RegisterHandler(state.Token, scenario, action, record),
                scenario + ", action=" + action + ", diagnostics=" + diagnostics
            );
        }

        [TestCaseSource(nameof(HandlerCases))]
        public void FailedDisabledHandlerReplayKeepsReturnedHandle(
            MessageScenario scenario,
            bool action,
            bool diagnostics
        )
        {
            using CaseState state = new CaseState(diagnostics);
            VerifyDisabledReplay(
                state,
                scenario,
                record => RegisterHandler(state.Token, scenario, action, record),
                scenario + ", action=" + action + ", diagnostics=" + diagnostics
            );
        }

        [Test]
        public void RejectedInterceptorDoesNotBecomeStaged(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(false, true)] bool diagnostics
        )
        {
            using CaseState state = new CaseState(diagnostics);
            VerifyImmediateFailure(
                state,
                scenario,
                record => RegisterInterceptor(state.Token, scenario, record),
                scenario + ", diagnostics=" + diagnostics,
                recordsDiagnostics: false
            );
        }

        [Test]
        public void FailedDisabledInterceptorReplayKeepsReturnedHandle(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(false, true)] bool diagnostics
        )
        {
            using CaseState state = new CaseState(diagnostics);
            VerifyDisabledReplay(
                state,
                scenario,
                record => RegisterInterceptor(state.Token, scenario, record),
                scenario + ", diagnostics=" + diagnostics
            );
        }

        [Test]
        public void RejectedGlobalDoesNotBecomeStaged(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(false, true)] bool action,
            [Values(false, true)] bool diagnostics
        )
        {
            using CaseState state = new CaseState(diagnostics);
            VerifyImmediateFailure(
                state,
                scenario,
                record => RegisterGlobal(state.Token, action, record),
                scenario + ", action=" + action + ", diagnostics=" + diagnostics
            );
        }

        [Test]
        public void FailedDisabledGlobalReplayKeepsReturnedHandle(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario,
            [Values(false, true)] bool action,
            [Values(false, true)] bool diagnostics
        )
        {
            using CaseState state = new CaseState(diagnostics);
            VerifyDisabledReplay(
                state,
                scenario,
                record => RegisterGlobal(state.Token, action, record),
                scenario + ", action=" + action + ", diagnostics=" + diagnostics
            );
        }

        [TestCase(false, 1)]
        [TestCase(false, 8)]
        [TestCase(true, 1)]
        [TestCase(true, 8)]
        public void FailurePreservesReentrantRegistrations(bool clearAndReuse, int nestedCount)
        {
            string label = "clearAndReuse=" + clearAndReuse + ", nestedCount=" + nestedCount;
            using CaseState state = new CaseState(true);
            MessageScenario scenario = MessageScenario.Untargeted();
            List<MessageRegistrationHandle> nested = new List<MessageRegistrationHandle>();
            state.Token.Enable();
            state.Bus.BeforeRejection = () =>
            {
                if (clearAndReuse)
                {
                    state.Token.UnregisterAll();
                    state.Token.Enable();
                }
                for (int index = 0; index < nestedCount; ++index)
                {
                    int captured = index;
                    nested.Add(
                        RegisterHandler(
                            state.Token,
                            scenario,
                            false,
                            () => state.Calls.Add(captured.ToString())
                        )
                    );
                }
            };
            state.Bus.RejectNext = true;
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
                RegisterHandler(state.Token, scenario, false, () => state.Calls.Add("rejected"))
            );
            state.Emit(scenario);
            string[] expected = new string[nestedCount];
            for (int index = 0; index < expected.Length; ++index)
            {
                expected[index] = index.ToString();
            }
            TestContext.WriteLine(
                label
                    + ": metadata="
                    + state.Token._metadata.Count
                    + "; calls="
                    + string.Join(",", state.Calls)
            );
            Assert.That(exception, Is.SameAs(state.Bus.Failure), label);
            Assert.That(state.Token._metadata.Count, Is.EqualTo(nestedCount), label);
            Assert.That(state.Calls, Is.EqualTo(expected), label);
            foreach (MessageRegistrationHandle handle in nested)
            {
                state.Token.RemoveRegistration(handle);
            }
            state.Token.Disable();
            state.Token.Enable();
            state.Calls.Clear();
            state.Emit(scenario);
            Assert.That(state.Calls, Is.Empty, label);
            Assert.That(state.Token._metadata.Count, Is.Zero, label);
        }

        private static void VerifyImmediateFailure(
            CaseState state,
            MessageScenario scenario,
            Func<Action, MessageRegistrationHandle> register,
            string label,
            bool recordsDiagnostics = true
        )
        {
            state.Token.Enable();
            MessageRegistrationHandle first = register(() => state.Calls.Add("first"));
            state.Emit(scenario);
            Assert.That(state.Calls, Is.EqualTo(new[] { "first" }), label + ": accepted control");
            // Interceptors are forwarded directly; only augmented handler/global invokers record.
            int expectedDiagnosticCount = state.Token.DiagnosticMode && recordsDiagnostics ? 1 : 0;
            int beforeCount = state.Token._callCounts.TryGetValue(first, out int recorded)
                ? recorded
                : 0;
            Assert.That(
                beforeCount,
                Is.EqualTo(expectedDiagnosticCount),
                label + ": accepted diagnostic control"
            );
            state.Calls.Clear();
            state.Bus.RejectNext = true;
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => register(() => state.Calls.Add("rejected")),
                label
            );
            int metadataAfterFailure = state.Token._metadata.Count;
            bool enabledAfterFailure = state.Token.Enabled;
            int firstCallCountAfterFailure = state.Token._callCounts.TryGetValue(
                first,
                out int count
            )
                ? count
                : 0;
            state.Emit(scenario);
            string[] immediately = state.Calls.ToArray();
            state.Calls.Clear();
            MessageRegistrationHandle next = register(() => state.Calls.Add("next"));
            state.Token.Disable();
            state.Token.Enable();
            state.Emit(scenario);
            string[] afterReplay = state.Calls.ToArray();
            state.Token.RemoveRegistration(first);
            state.Token.RemoveRegistration(next);
            state.Token.Disable();
            state.Token.Enable();
            state.Calls.Clear();
            state.Emit(scenario);
            TestContext.WriteLine(
                label
                    + ": metadataAfterFailure="
                    + metadataAfterFailure
                    + "; immediate="
                    + string.Join(",", immediately)
                    + "; replay="
                    + string.Join(",", afterReplay)
                    + "; afterRemovingReturnedHandles="
                    + string.Join(",", state.Calls)
            );
            Assert.That(exception, Is.SameAs(state.Bus.Failure), label);
            Assert.That(enabledAfterFailure, Is.True, label);
            Assert.That(
                firstCallCountAfterFailure,
                Is.EqualTo(expectedDiagnosticCount),
                label + ": existing diagnostics"
            );
            Assert.That(metadataAfterFailure, Is.EqualTo(1), label);
            Assert.That(immediately, Is.EqualTo(new[] { "first" }), label);
            Assert.That(afterReplay, Is.EqualTo(new[] { "first", "next" }), label);
            Assert.That(state.Calls, Is.Empty, label);
            Assert.That(state.Token._metadata.Count, Is.Zero, label);
            Assert.That(state.Token._callCounts, Is.Empty, label + ": diagnostic counts clear");
            Assert.That(
                state.Token._emissionBuffer.Count,
                Is.Zero,
                label + ": diagnostic history clears"
            );
        }

        private static void VerifyDisabledReplay(
            CaseState state,
            MessageScenario scenario,
            Func<Action, MessageRegistrationHandle> register,
            string label
        )
        {
            MessageRegistrationHandle handle = register(() => state.Calls.Add("staged"));
            state.Bus.RejectNext = true;
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                state.Token.Enable,
                label
            );
            Assert.That(exception, Is.SameAs(state.Bus.Failure), label);
            Assert.That(state.Token.Enabled, Is.False, label);
            Assert.That(state.Token._metadata.Count, Is.EqualTo(1), label);
            state.Emit(scenario);
            Assert.That(state.Calls, Is.Empty, label);
            state.Token.Enable();
            state.Emit(scenario);
            Assert.That(state.Calls, Is.EqualTo(new[] { "staged" }), label);
            state.Token.RemoveRegistration(handle);
            state.Token.Disable();
            state.Token.Enable();
            state.Calls.Clear();
            state.Emit(scenario);
            Assert.That(state.Calls, Is.Empty, label);
            Assert.That(state.Token._metadata.Count, Is.Zero, label);
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

        private sealed class CaseState : IDisposable
        {
            internal readonly MessageBus Inner = new MessageBus();
            internal readonly RejectingBus Bus;
            internal readonly MessageHandler Handler;
            internal readonly MessageRegistrationToken Token;
            internal readonly List<string> Calls = new List<string>();
            private readonly LeakWatcher _leaks;

            internal CaseState(bool diagnostics)
            {
                _leaks = new LeakWatcher(Inner);
                Bus = new RejectingBus(Inner);
                Handler = new MessageHandler(new InstanceId(871), Bus) { active = true };
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
                Token.Dispose();
                Handler.active = false;
                Inner.Trim(true);
                _leaks.Dispose();
            }
        }

        private sealed class RejectingBus : DelegatingMessageBus
        {
            internal readonly InvalidOperationException Failure = new InvalidOperationException(
                "Registration rejected before forwarding."
            );
            internal bool RejectNext;
            internal Action BeforeRejection;

            internal RejectingBus(IMessageBus inner)
                : base(inner) { }

            private void Before()
            {
                if (!RejectNext)
                {
                    return;
                }
                RejectNext = false;
                Action callback = BeforeRejection;
                BeforeRejection = null;
                callback?.Invoke();
                throw Failure;
            }

            public override MessageBusRegistration RegisterUntargeted<T>(
                MessageHandler handler,
                int priority = 0
            )
            {
                Before();
                return base.RegisterUntargeted<T>(handler, priority);
            }

            public override MessageBusRegistration RegisterUntargetedPostProcessor<T>(
                MessageHandler handler,
                int priority = 0
            )
            {
                Before();
                return base.RegisterUntargetedPostProcessor<T>(handler, priority);
            }

            public override MessageBusRegistration RegisterTargeted<T>(
                InstanceId target,
                MessageHandler handler,
                int priority = 0
            )
            {
                Before();
                return base.RegisterTargeted<T>(target, handler, priority);
            }

            public override MessageBusRegistration RegisterTargetedPostProcessor<T>(
                InstanceId target,
                MessageHandler handler,
                int priority = 0
            )
            {
                Before();
                return base.RegisterTargetedPostProcessor<T>(target, handler, priority);
            }

            public override MessageBusRegistration RegisterTargetedWithoutTargeting<T>(
                MessageHandler handler,
                int priority = 0
            )
            {
                Before();
                return base.RegisterTargetedWithoutTargeting<T>(handler, priority);
            }

            public override MessageBusRegistration RegisterTargetedWithoutTargetingPostProcessor<T>(
                MessageHandler handler,
                int priority = 0
            )
            {
                Before();
                return base.RegisterTargetedWithoutTargetingPostProcessor<T>(handler, priority);
            }

            public override MessageBusRegistration RegisterSourcedBroadcast<T>(
                InstanceId source,
                MessageHandler handler,
                int priority = 0
            )
            {
                Before();
                return base.RegisterSourcedBroadcast<T>(source, handler, priority);
            }

            public override MessageBusRegistration RegisterBroadcastPostProcessor<T>(
                InstanceId source,
                MessageHandler handler,
                int priority = 0
            )
            {
                Before();
                return base.RegisterBroadcastPostProcessor<T>(source, handler, priority);
            }

            public override MessageBusRegistration RegisterSourcedBroadcastWithoutSource<T>(
                MessageHandler handler,
                int priority = 0
            )
            {
                Before();
                return base.RegisterSourcedBroadcastWithoutSource<T>(handler, priority);
            }

            public override MessageBusRegistration RegisterBroadcastWithoutSourcePostProcessor<T>(
                MessageHandler handler,
                int priority = 0
            )
            {
                Before();
                return base.RegisterBroadcastWithoutSourcePostProcessor<T>(handler, priority);
            }

            public override MessageBusRegistration RegisterGlobalAcceptAll(MessageHandler handler)
            {
                Before();
                return base.RegisterGlobalAcceptAll(handler);
            }

            public override MessageBusRegistration RegisterUntargetedInterceptor<T>(
                IMessageBus.UntargetedInterceptor<T> interceptor,
                int priority = 0
            )
            {
                Before();
                return base.RegisterUntargetedInterceptor(interceptor, priority);
            }

            public override MessageBusRegistration RegisterTargetedInterceptor<T>(
                IMessageBus.TargetedInterceptor<T> interceptor,
                int priority = 0
            )
            {
                Before();
                return base.RegisterTargetedInterceptor(interceptor, priority);
            }

            public override MessageBusRegistration RegisterBroadcastInterceptor<T>(
                IMessageBus.BroadcastInterceptor<T> interceptor,
                int priority = 0
            )
            {
                Before();
                return base.RegisterBroadcastInterceptor(interceptor, priority);
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
