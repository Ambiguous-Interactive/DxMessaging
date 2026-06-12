#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections;
    using DxMessaging.Core;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using UnityEngine;
    using UnityEngine.TestTools;

    // Bus does not emit framework-level logs on handler/interceptor/post-processor throws; LogAssert.Expect intentionally not used.

    /// <summary>
    /// Pins the current behavior of the message bus when handlers, interceptors, and
    /// post-processors throw. The bus does not wrap dispatched delegates in try/catch,
    /// so exceptions propagate out of the emit call and any siblings scheduled to run
    /// after the throwing delegate are skipped for the current dispatch. These tests
    /// capture that contract so any future change to swallow-and-log behavior fails
    /// loudly and forces a deliberate review.
    /// </summary>
    public sealed class HandlerExceptionTests : MessagingTestBase
    {
        private const string ThrowingHandlerMessage = "DxMessaging-test-handler-throw";
        private const string ThrowingInterceptorMessage = "DxMessaging-test-interceptor-throw";
        private const string ThrowingPostProcessorMessage = "DxMessaging-test-post-processor-throw";
        private const string ThrowingGlobalAcceptAllMessage =
            "DxMessaging-test-global-accept-all-throw";
        private const string ThrowingWithoutContextMessage =
            "DxMessaging-test-without-context-throw";

        /// <summary>
        /// Pins that a throwing handler aborts the rest of the current dispatch:
        /// previously ordered handlers run, the throwing handler runs, and any
        /// subsequent handler scheduled after it is skipped for that emission.
        /// </summary>
        [UnityTest]
        public IEnumerator HandlerThrowPreventsSubsequentHandlers(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(HandlerThrowPreventsSubsequentHandlers) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int firstCount = 0;
            int secondCount = 0;
            int thirdCount = 0;

            ScenarioCallbacks.RegisterCountingHandler(
                scenario,
                token,
                hostId,
                priority: 0,
                onInvoked: () => ++firstCount
            );
            ScenarioCallbacks.RegisterCountingHandler(
                scenario,
                token,
                hostId,
                priority: 0,
                onInvoked: () =>
                {
                    ++secondCount;
                    throw new InvalidOperationException(ThrowingHandlerMessage);
                }
            );
            ScenarioCallbacks.RegisterCountingHandler(
                scenario,
                token,
                hostId,
                priority: 0,
                onInvoked: () => ++thirdCount
            );

            InvalidOperationException captured = Assert.Throws<InvalidOperationException>(() =>
                ScenarioCallbacks.EmitForKind(scenario, hostId)
            );

            Assert.AreEqual(ThrowingHandlerMessage, captured.Message);
            Assert.AreEqual(1, firstCount, "First handler must run before the throwing handler.");
            Assert.AreEqual(1, secondCount, "Throwing handler must execute before propagating.");
            // Pinning current behavior: the bus does not wrap handlers in try/catch, so
            // siblings scheduled after the throwing one are skipped during this dispatch.
            // If that ever changes (e.g. the bus starts swallow-and-log) update this assertion.
            Assert.AreEqual(
                0,
                thirdCount,
                "Subsequent handler must not run once propagation begins."
            );
            yield break;
        }

        [UnityTest]
        public IEnumerator HandlerThrowDoesNotCorruptDispatchPool(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(HandlerThrowDoesNotCorruptDispatchPool) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int safeCount = 0;
            int throwingCount = 0;

            ScenarioCallbacks.RegisterCountingHandler(
                scenario,
                token,
                hostId,
                priority: 0,
                onInvoked: () => ++safeCount
            );
            ScenarioCallbacks.RegisterCountingHandler(
                scenario,
                token,
                hostId,
                priority: 1,
                onInvoked: () =>
                {
                    ++throwingCount;
                    throw new InvalidOperationException(ThrowingHandlerMessage);
                }
            );

            const int Iterations = 10;
            for (int i = 0; i < Iterations; ++i)
            {
                InvalidOperationException captured = Assert.Throws<InvalidOperationException>(() =>
                    ScenarioCallbacks.EmitForKind(scenario, hostId)
                );
                Assert.AreEqual(ThrowingHandlerMessage, captured.Message);
            }

            Assert.AreEqual(
                Iterations,
                safeCount,
                "Safe handler must run on every emission even when later handler throws."
            );
            Assert.AreEqual(
                Iterations,
                throwingCount,
                "Throwing handler must execute on every emission with no double-fire or skip."
            );
            yield break;
        }

        /// <summary>
        /// Pins that a throwing handler aborts the dispatch before post-processors
        /// run. Handler exceptions propagate out of the emit call without invoking
        /// any post-processors registered for the same message. If post-processors
        /// are later moved into a finally block this contract must be revisited.
        /// </summary>
        [UnityTest]
        public IEnumerator HandlerThrowPreventsPostProcessorsFromRunning(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(HandlerThrowPreventsPostProcessorsFromRunning) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int handlerCount = 0;
            int postProcessorCount = 0;

            ScenarioCallbacks.RegisterCountingHandler(
                scenario,
                token,
                hostId,
                priority: 0,
                onInvoked: () =>
                {
                    ++handlerCount;
                    throw new InvalidOperationException(ThrowingHandlerMessage);
                }
            );
            ScenarioCallbacks.RegisterCountingPostProcessor(
                scenario,
                token,
                hostId,
                priority: 0,
                onInvoked: () => ++postProcessorCount
            );

            InvalidOperationException captured = Assert.Throws<InvalidOperationException>(() =>
                ScenarioCallbacks.EmitForKind(scenario, hostId)
            );

            Assert.AreEqual(ThrowingHandlerMessage, captured.Message);
            Assert.AreEqual(1, handlerCount, "Throwing handler must execute exactly once.");
            // Pinning current behavior: a handler exception aborts the dispatch before
            // post-processors run. If post-processors are later moved into a finally
            // block the assertion below will need to be inverted.
            Assert.AreEqual(
                0,
                postProcessorCount,
                "Post-processor must not run when an earlier handler throws."
            );
            yield break;
        }

        [UnityTest]
        public IEnumerator HandlerThrowDoesNotPreventDeregistration(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(HandlerThrowDoesNotPreventDeregistration) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int throwingCount = 0;
            MessageRegistrationHandle handle = ScenarioCallbacks.RegisterCountingHandler(
                scenario,
                token,
                hostId,
                priority: 0,
                onInvoked: () =>
                {
                    ++throwingCount;
                    throw new InvalidOperationException(ThrowingHandlerMessage);
                }
            );

            InvalidOperationException firstCaptured = Assert.Throws<InvalidOperationException>(() =>
                ScenarioCallbacks.EmitForKind(scenario, hostId)
            );
            Assert.AreEqual(ThrowingHandlerMessage, firstCaptured.Message);
            Assert.AreEqual(1, throwingCount);

            token.RemoveRegistration(handle);

            ScenarioCallbacks.EmitForKind(scenario, hostId);
            Assert.AreEqual(
                1,
                throwingCount,
                "Handler must not fire after RemoveRegistration even if a previous emit threw."
            );

            // After deregistering the throwing handler, registering a fresh
            // non-throwing handler must produce a clean dispatch with no residue
            // from the previous failure.
            int replacementCount = 0;
            MessageRegistrationHandle replacementHandle = ScenarioCallbacks.RegisterCountingHandler(
                scenario,
                token,
                hostId,
                priority: 0,
                onInvoked: () => ++replacementCount
            );

            ScenarioCallbacks.EmitForKind(scenario, hostId);
            Assert.AreEqual(
                1,
                replacementCount,
                "Replacement handler registered after the throw must run on the next emission."
            );
            Assert.AreEqual(
                1,
                throwingCount,
                "Removed throwing handler must remain inert after replacement is registered."
            );

            token.RemoveRegistration(replacementHandle);
            yield break;
        }

        [UnityTest]
        public IEnumerator InterceptorThrowFallsBackGracefully(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(InterceptorThrowFallsBackGracefully) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int handlerCount = 0;
            int interceptorCount = 0;

            ScenarioCallbacks.RegisterCountingHandler(
                scenario,
                token,
                hostId,
                priority: 0,
                onInvoked: () => ++handlerCount
            );
            RegisterThrowingInterceptor(scenario, token, onInvoked: () => ++interceptorCount);

            InvalidOperationException captured = Assert.Throws<InvalidOperationException>(() =>
                ScenarioCallbacks.EmitForKind(scenario, hostId)
            );

            Assert.AreEqual(ThrowingInterceptorMessage, captured.Message);
            Assert.AreEqual(
                1,
                interceptorCount,
                "Interceptor must execute and throw exactly once."
            );
            // Behavior pinned to current implementation: interceptor exceptions
            // propagate before handlers run, so handlers do not see the message.
            Assert.AreEqual(
                0,
                handlerCount,
                "Handler must not run when an interceptor throws during the same emission."
            );

            // Sanity: a follow-up emission after the throwing interceptor still raises again,
            // proving no infinite loop or NullReferenceException is masked behind the throw.
            InvalidOperationException secondCaptured = Assert.Throws<InvalidOperationException>(
                () =>
                    ScenarioCallbacks.EmitForKind(scenario, hostId)
            );
            Assert.AreEqual(ThrowingInterceptorMessage, secondCaptured.Message);
            Assert.AreEqual(2, interceptorCount);
            Assert.AreEqual(0, handlerCount);
            yield break;
        }

        [UnityTest]
        public IEnumerator PostProcessorThrowDoesNotAffectNextEmission(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(PostProcessorThrowDoesNotAffectNextEmission) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int handlerCount = 0;
            int throwingPostProcessorCount = 0;
            int trailingPostProcessorCount = 0;

            ScenarioCallbacks.RegisterCountingHandler(
                scenario,
                token,
                hostId,
                priority: 0,
                onInvoked: () => ++handlerCount
            );
            // Throwing post-processor at priority 1 (runs after the trailing one
            // at priority 2 if priority is purely lower-first, OR before depending
            // on order). To force a deterministic order where the throwing PP runs
            // first and skips the trailing one, register the throwing PP at the
            // earlier priority and the trailing PP at a later priority.
            ScenarioCallbacks.RegisterCountingPostProcessor(
                scenario,
                token,
                hostId,
                priority: 0,
                onInvoked: () =>
                {
                    ++throwingPostProcessorCount;
                    throw new InvalidOperationException(ThrowingPostProcessorMessage);
                }
            );
            ScenarioCallbacks.RegisterCountingPostProcessor(
                scenario,
                token,
                hostId,
                priority: 1,
                onInvoked: () => ++trailingPostProcessorCount
            );

            InvalidOperationException firstCaptured = Assert.Throws<InvalidOperationException>(() =>
                ScenarioCallbacks.EmitForKind(scenario, hostId)
            );
            Assert.AreEqual(ThrowingPostProcessorMessage, firstCaptured.Message);
            Assert.AreEqual(1, handlerCount, "Handler must run before throwing post-processor.");
            Assert.AreEqual(1, throwingPostProcessorCount);
            Assert.AreEqual(
                0,
                trailingPostProcessorCount,
                "Trailing post-processor must not run when an earlier post-processor throws."
            );

            InvalidOperationException secondCaptured = Assert.Throws<InvalidOperationException>(
                () =>
                    ScenarioCallbacks.EmitForKind(scenario, hostId)
            );
            Assert.AreEqual(ThrowingPostProcessorMessage, secondCaptured.Message);
            Assert.AreEqual(
                2,
                handlerCount,
                "Handler must continue to run on subsequent emissions."
            );
            Assert.AreEqual(2, throwingPostProcessorCount);
            Assert.AreEqual(
                0,
                trailingPostProcessorCount,
                "Trailing post-processor must remain skipped on every emission while the earlier one throws."
            );
            yield break;
        }

        /// <summary>
        /// Pins that a throwing GlobalAcceptAll sink aborts the remainder of the
        /// dispatch. GlobalAcceptAll sinks run after interceptors but before
        /// typed handlers (MessageBus.UntargetedBroadcast / TargetedBroadcast /
        /// SourcedBroadcast), and the bus does not wrap them in try/catch, so the
        /// exception propagates out of the emit call and neither typed handlers
        /// nor post-processors run for that emission. The dispatch-depth lease is
        /// scoped in a using block (MessageBus.EnterDispatch), so the bus is NOT
        /// corrupted by the throw: removing the throwing global restores fully
        /// functional dispatch, which the second half of this test proves.
        /// </summary>
        [UnityTest]
        public IEnumerator GlobalAcceptAllThrowAbortsTypedHandlersAndPostProcessors(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(GlobalAcceptAllThrowAbortsTypedHandlersAndPostProcessors) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int globalCount = 0;
            int handlerCount = 0;
            int postProcessorCount = 0;

            MessageRegistrationHandle globalHandle = RegisterThrowingGlobalAcceptAll(
                token,
                onInvoked: () => ++globalCount
            );
            ScenarioCallbacks.RegisterCountingHandler(
                scenario,
                token,
                hostId,
                priority: 0,
                onInvoked: () => ++handlerCount
            );
            ScenarioCallbacks.RegisterCountingPostProcessor(
                scenario,
                token,
                hostId,
                priority: 0,
                onInvoked: () => ++postProcessorCount
            );

            InvalidOperationException captured = Assert.Throws<InvalidOperationException>(() =>
                ScenarioCallbacks.EmitForKind(scenario, hostId)
            );

            Assert.AreEqual(ThrowingGlobalAcceptAllMessage, captured.Message);
            Assert.AreEqual(
                1,
                globalCount,
                "Throwing GlobalAcceptAll sink must execute exactly once before propagating."
            );
            // Pinning current behavior: GlobalAcceptAll sinks dispatch before typed
            // handlers and the bus does not wrap them in try/catch, so a throwing
            // global aborts the typed-handler and post-processor phases entirely.
            Assert.AreEqual(
                0,
                handlerCount,
                "Typed handler must not run when a GlobalAcceptAll sink throws earlier in the dispatch."
            );
            Assert.AreEqual(
                0,
                postProcessorCount,
                "Post-processor must not run when a GlobalAcceptAll sink throws earlier in the dispatch."
            );

            // The throw must not leave the bus corrupted (the dispatch-depth lease is
            // released by a using block even when the emission throws). Removing the
            // throwing global must restore a clean dispatch for the same message type.
            token.RemoveRegistration(globalHandle);

            ScenarioCallbacks.EmitForKind(scenario, hostId);
            Assert.AreEqual(
                1,
                globalCount,
                "Removed GlobalAcceptAll sink must not run after RemoveRegistration."
            );
            Assert.AreEqual(
                1,
                handlerCount,
                "Typed handler must run normally once the throwing GlobalAcceptAll sink is removed."
            );
            Assert.AreEqual(
                1,
                postProcessorCount,
                "Post-processor must run normally once the throwing GlobalAcceptAll sink is removed."
            );
            yield break;
        }

        /// <summary>
        /// Pins cross-sink interplay when the target/source-specific handler
        /// throws: WithoutTargeting / WithoutSource handlers for the same message
        /// type dispatch AFTER the specific handlers
        /// (MessageBus.TargetedBroadcast runs targeted handlers, then
        /// InternalTargetedWithoutTargetingBroadcast; SourcedBroadcast mirrors
        /// this for broadcast), so a throwing specific handler skips the
        /// without-context sink for that emission.
        /// </summary>
        [UnityTest]
        public IEnumerator SpecificHandlerThrowSkipsWithoutContextHandlersForSameEmission(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.KindsWithComponentTarget)
            )]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(SpecificHandlerThrowSkipsWithoutContextHandlersForSameEmission)
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int specificCount = 0;
            int withoutContextCount = 0;

            ScenarioCallbacks.RegisterCountingHandler(
                scenario,
                token,
                hostId,
                priority: 0,
                onInvoked: () =>
                {
                    ++specificCount;
                    throw new InvalidOperationException(ThrowingHandlerMessage);
                }
            );
            RegisterWithoutContextCountingHandler(
                scenario,
                token,
                onInvoked: () => ++withoutContextCount
            );

            InvalidOperationException captured = Assert.Throws<InvalidOperationException>(() =>
                ScenarioCallbacks.EmitForKind(scenario, hostId)
            );

            Assert.AreEqual(ThrowingHandlerMessage, captured.Message);
            Assert.AreEqual(
                1,
                specificCount,
                "Throwing specific handler must execute exactly once before propagating."
            );
            // Pinning current behavior: without-context handlers dispatch after the
            // target/source-specific handlers, so the specific handler's exception
            // aborts the without-context phase for this emission.
            Assert.AreEqual(
                0,
                withoutContextCount,
                "WithoutTargeting/WithoutSource handler must not run when a specific handler "
                    + "throws earlier in the same emission."
            );
            yield break;
        }

        /// <summary>
        /// Mirror of <see cref="SpecificHandlerThrowSkipsWithoutContextHandlersForSameEmission"/>:
        /// when the WithoutTargeting / WithoutSource handler throws, the
        /// target/source-specific handlers have ALREADY run (they dispatch
        /// earlier), and all post-processors (specific and without-context) are
        /// skipped because they dispatch after every handler phase. The second
        /// emission proves the shape is stable, with no double-fire or skip.
        /// </summary>
        [UnityTest]
        public IEnumerator WithoutContextHandlerThrowStillRunsSpecificHandlersButSkipsPostProcessors(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.KindsWithComponentTarget)
            )]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(WithoutContextHandlerThrowStillRunsSpecificHandlersButSkipsPostProcessors)
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int specificCount = 0;
            int withoutContextCount = 0;
            int specificPostProcessorCount = 0;
            int withoutContextPostProcessorCount = 0;

            ScenarioCallbacks.RegisterCountingHandler(
                scenario,
                token,
                hostId,
                priority: 0,
                onInvoked: () => ++specificCount
            );
            RegisterWithoutContextCountingHandler(
                scenario,
                token,
                onInvoked: () =>
                {
                    ++withoutContextCount;
                    throw new InvalidOperationException(ThrowingWithoutContextMessage);
                }
            );
            ScenarioCallbacks.RegisterCountingPostProcessor(
                scenario,
                token,
                hostId,
                priority: 0,
                onInvoked: () => ++specificPostProcessorCount
            );
            RegisterWithoutContextCountingPostProcessor(
                scenario,
                token,
                onInvoked: () => ++withoutContextPostProcessorCount
            );

            InvalidOperationException captured = Assert.Throws<InvalidOperationException>(() =>
                ScenarioCallbacks.EmitForKind(scenario, hostId)
            );

            Assert.AreEqual(ThrowingWithoutContextMessage, captured.Message);
            Assert.AreEqual(
                1,
                specificCount,
                "Specific handler dispatches before the without-context handler, so it must "
                    + "already have run when the without-context handler throws."
            );
            Assert.AreEqual(
                1,
                withoutContextCount,
                "Throwing without-context handler must execute exactly once before propagating."
            );
            // Pinning current behavior: post-processors (specific and without-context)
            // dispatch after every handler phase, so the without-context throw skips
            // both post-processor sinks for this emission.
            Assert.AreEqual(
                0,
                specificPostProcessorCount,
                "Specific post-processor must not run when a without-context handler throws."
            );
            Assert.AreEqual(
                0,
                withoutContextPostProcessorCount,
                "Without-context post-processor must not run when a without-context handler throws."
            );

            InvalidOperationException secondCaptured = Assert.Throws<InvalidOperationException>(
                () =>
                    ScenarioCallbacks.EmitForKind(scenario, hostId)
            );
            Assert.AreEqual(ThrowingWithoutContextMessage, secondCaptured.Message);
            Assert.AreEqual(
                2,
                specificCount,
                "Specific handler must continue to run on subsequent emissions."
            );
            Assert.AreEqual(
                2,
                withoutContextCount,
                "Throwing without-context handler must execute on every emission with no "
                    + "double-fire or skip."
            );
            Assert.AreEqual(
                0,
                specificPostProcessorCount,
                "Specific post-processor must remain skipped while the without-context handler throws."
            );
            Assert.AreEqual(
                0,
                withoutContextPostProcessorCount,
                "Without-context post-processor must remain skipped while the without-context handler throws."
            );
            yield break;
        }

        private static MessageRegistrationHandle RegisterThrowingGlobalAcceptAll(
            MessageRegistrationToken token,
            Action onInvoked
        )
        {
            return token.RegisterGlobalAcceptAll(
                (IUntargetedMessage _) =>
                {
                    onInvoked();
                    throw new InvalidOperationException(ThrowingGlobalAcceptAllMessage);
                },
                (InstanceId _, ITargetedMessage _) =>
                {
                    onInvoked();
                    throw new InvalidOperationException(ThrowingGlobalAcceptAllMessage);
                },
                (InstanceId _, IBroadcastMessage _) =>
                {
                    onInvoked();
                    throw new InvalidOperationException(ThrowingGlobalAcceptAllMessage);
                }
            );
        }

        private static MessageRegistrationHandle RegisterWithoutContextCountingHandler(
            MessageScenario scenario,
            MessageRegistrationToken token,
            Action onInvoked
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Targeted:
                {
                    return token.RegisterTargetedWithoutTargeting(
                        (ref InstanceId _, ref SimpleTargetedMessage _) => onInvoked()
                    );
                }
                case MessageKind.Broadcast:
                {
                    return token.RegisterBroadcastWithoutSource(
                        (ref InstanceId _, ref SimpleBroadcastMessage _) => onInvoked()
                    );
                }
                default:
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(scenario),
                        scenario.Kind,
                        "Without-context registration requires Targeted or Broadcast."
                    );
                }
            }
        }

        private static MessageRegistrationHandle RegisterWithoutContextCountingPostProcessor(
            MessageScenario scenario,
            MessageRegistrationToken token,
            Action onInvoked
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Targeted:
                {
                    return token.RegisterTargetedWithoutTargetingPostProcessor(
                        (ref InstanceId _, ref SimpleTargetedMessage _) => onInvoked()
                    );
                }
                case MessageKind.Broadcast:
                {
                    return token.RegisterBroadcastWithoutSourcePostProcessor(
                        (ref InstanceId _, ref SimpleBroadcastMessage _) => onInvoked()
                    );
                }
                default:
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(scenario),
                        scenario.Kind,
                        "Without-context registration requires Targeted or Broadcast."
                    );
                }
            }
        }

        /// <summary>
        /// Registers an interceptor that runs <paramref name="onInvoked"/> and then
        /// throws <see cref="InvalidOperationException"/> with
        /// <see cref="ThrowingInterceptorMessage"/>.
        /// </summary>
        private static MessageRegistrationHandle RegisterThrowingInterceptor(
            MessageScenario scenario,
            MessageRegistrationToken token,
            Action onInvoked
        )
        {
            return ScenarioCallbacks.RegisterCountingInterceptor(
                scenario,
                token,
                result: () => throw new InvalidOperationException(ThrowingInterceptorMessage),
                onIntercepted: onInvoked
            );
        }
    }
}
#endif
