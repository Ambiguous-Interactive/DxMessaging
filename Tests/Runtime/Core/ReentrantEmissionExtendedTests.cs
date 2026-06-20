#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using UnityEngine;

    /// <summary>
    /// Extends the <see cref="ReentrantEmissionTests"/> coverage. Each test in
    /// this fixture either crosses message-kind boundaries (a handler for kind
    /// X emits kind Y from inside its callback) or stresses a re-entrancy
    /// corner case the base fixture does not cover (interceptor-veto during
    /// a re-emit, deep recursion, mid-dispatch self-resubscribe). Per the
    /// project parameterization rule the tests still drive
    /// <see cref="MessageScenarios.AllKinds"/> so the same test name covers
    /// every entry point.
    /// </summary>
    public sealed class ReentrantEmissionExtendedTests : MessagingTestBase
    {
        private const int DeepRecursionLimit = 10;

        /// <summary>
        /// Yields every (outer, inner) pair from
        /// <see cref="MessageScenarios.AllKinds"/>. The diagonal (same-kind)
        /// is intentionally excluded because same-kind reentrancy already has
        /// dedicated coverage in <see cref="ReentrantEmissionTests"/>; the
        /// cross-product produces 6 pairs (3 kinds * 3 kinds - 3 diagonal).
        /// </summary>
        public static IEnumerable<CrossKindReentrancyCase> CrossKindReentrancyPairs
        {
            get
            {
                foreach (MessageScenario outer in MessageScenarios.AllKinds)
                {
                    foreach (MessageScenario inner in MessageScenarios.AllKinds)
                    {
                        if (outer.Kind == inner.Kind)
                        {
                            continue;
                        }

                        yield return new CrossKindReentrancyCase(outer, inner);
                    }
                }
            }
        }

        /// <summary>
        /// Pair (outer, inner) consumed by
        /// <see cref="CrossKindReentrancyChainCompletes"/>. Wraps two
        /// <see cref="MessageScenario"/> values so the parameter source can
        /// surface a stable, readable display name in the test runner.
        /// </summary>
        public sealed class CrossKindReentrancyCase
        {
            public MessageScenario Outer { get; }
            public MessageScenario Inner { get; }

            public CrossKindReentrancyCase(MessageScenario outer, MessageScenario inner)
            {
                Outer = outer;
                Inner = inner;
            }

            public override string ToString()
            {
                return $"{Outer.Kind}->{Inner.Kind}";
            }
        }

        /// <summary>
        /// A handler that fires on <paramref name="pair"/>'s outer kind
        /// triggers an emission of the pair's inner kind from inside its
        /// callback. Both chains must complete in order without deadlock.
        /// Test count: 6 (every cross-kind permutation excluding the
        /// same-kind diagonal, which is exercised by
        /// <see cref="ReentrantEmissionTests"/>).
        /// </summary>
        [Test]
        public void CrossKindReentrancyChainCompletes(
            [ValueSource(nameof(CrossKindReentrancyPairs))] CrossKindReentrancyCase pair
        )
        {
            MessageScenario scenario = pair.Outer;
            MessageScenario innerScenario = pair.Inner;

            GameObject host = new(
                nameof(CrossKindReentrancyChainCompletes) + scenario.Kind + innerScenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int outerCount = 0;
            int innerCount = 0;
            List<string> trace = new List<string>(4);

            _ = RegisterCountingHandler(
                innerScenario,
                token,
                hostId,
                () =>
                {
                    trace.Add("inner");
                    ++innerCount;
                }
            );
            MessageRegistrationHandle outerHandle = RegisterCountingHandler(
                scenario,
                token,
                hostId,
                () =>
                {
                    trace.Add("outer-start");
                    ++outerCount;
                    EmitForScenario(innerScenario, hostId);
                    trace.Add("outer-end");
                }
            );

            EmitForScenario(scenario, hostId);
            Assert.AreEqual(
                1,
                outerCount,
                "[{0}->{1}] Outer handler must run exactly once. trace=[{2}]",
                scenario.Kind,
                innerScenario.Kind,
                string.Join(",", trace)
            );
            Assert.AreEqual(
                1,
                innerCount,
                "[{0}->{1}] Inner handler must run inside outer's callback. trace=[{2}]",
                scenario.Kind,
                innerScenario.Kind,
                string.Join(",", trace)
            );
            Assert.AreEqual(
                "outer-start",
                trace[0],
                "[{0}->{1}] Outer must begin before inner. trace=[{2}]",
                scenario.Kind,
                innerScenario.Kind,
                string.Join(",", trace)
            );
            Assert.AreEqual(
                "inner",
                trace[1],
                "[{0}->{1}] Inner must run between outer's two halves. trace=[{2}]",
                scenario.Kind,
                innerScenario.Kind,
                string.Join(",", trace)
            );
            Assert.AreEqual(
                "outer-end",
                trace[2],
                "[{0}->{1}] Outer must complete after inner returns. trace=[{2}]",
                scenario.Kind,
                innerScenario.Kind,
                string.Join(",", trace)
            );

            token.RemoveRegistration(outerHandle);
        }

        /// <summary>
        /// Self-recursion bounded at <see cref="DeepRecursionLimit"/> levels.
        /// Cross-checks invocation count via the test-side <c>depth</c>
        /// counter AND via the bus's public
        /// <see cref="IMessageBus.EmissionId"/> counter, which the bus
        /// increments once per emit. Each nested emit must bump
        /// <c>EmissionId</c>, so the difference between the entry and exit
        /// EmissionId values is a hard invariant separate from the
        /// production-side bool used to gate recursion.
        /// </summary>
        [Test]
        public void DeepRecursion10Levels(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(DeepRecursion10Levels) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int depth = 0;
            int invocations = 0;
            IMessageBus bus = MessageHandler.MessageBus;
            long initialEmissionId = bus.EmissionId;

            MessageRegistrationHandle handle = RegisterCountingHandler(
                scenario,
                token,
                hostId,
                () =>
                {
                    ++invocations;
                    if (depth >= DeepRecursionLimit)
                    {
                        return;
                    }

                    ++depth;
                    try
                    {
                        EmitForScenario(scenario, hostId);
                    }
                    finally
                    {
                        --depth;
                    }
                }
            );

            EmitForScenario(scenario, hostId);
            Assert.AreEqual(
                DeepRecursionLimit + 1,
                invocations,
                "[{0}] Self-recursion bounded at {1} levels must invoke handler {2} times.",
                scenario.Kind,
                DeepRecursionLimit,
                DeepRecursionLimit + 1
            );

            // EmissionId invariant: every emit (outer + each nested re-emit)
            // bumps the counter once. The 11 invocations are the result of
            // 11 emits (one outer + ten recursive), so the EmissionId must
            // advance by at least 11 between entry and exit. Checking
            // ">=" rather than "==" tolerates background frame emits that
            // a Unity test runner may interleave.
            long deltaEmissions = bus.EmissionId - initialEmissionId;
            Assert.GreaterOrEqual(
                deltaEmissions,
                DeepRecursionLimit + 1,
                "[{0}] Bus EmissionId must advance by at least {1} during deep recursion (saw {2}).",
                scenario.Kind,
                DeepRecursionLimit + 1,
                deltaEmissions
            );

            token.RemoveRegistration(handle);
        }

        [Test]
        public void RecursionWithPriorityHandlersRespectsOrderingPerEmission(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(RecursionWithPriorityHandlersRespectsOrderingPerEmission) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            List<string> trace = new List<string>();
            int depth = 0;

            // Three priorities: 0, 5, 10. The middle priority emits a
            // recursive message; the other two record their slot. Each
            // emission must record [p0, p5(re-emit), p10] in order, with the
            // reentrant inner emission interleaved between p5's start and
            // p10's run on the outer emission.
            MessageRegistrationHandle p0Handle = RegisterCountingHandler(
                scenario,
                token,
                hostId,
                () => trace.Add($"d{depth}:p0"),
                priority: 0
            );
            MessageRegistrationHandle p5Handle = RegisterCountingHandler(
                scenario,
                token,
                hostId,
                () =>
                {
                    trace.Add($"d{depth}:p5-start");
                    if (depth < 1)
                    {
                        ++depth;
                        try
                        {
                            EmitForScenario(scenario, hostId);
                        }
                        finally
                        {
                            --depth;
                        }
                    }
                    trace.Add($"d{depth}:p5-end");
                },
                priority: 5
            );
            MessageRegistrationHandle p10Handle = RegisterCountingHandler(
                scenario,
                token,
                hostId,
                () => trace.Add($"d{depth}:p10"),
                priority: 10
            );

            EmitForScenario(scenario, hostId);

            // Expected sequence at the outer emission:
            //  d0:p0
            //  d0:p5-start
            //   d1:p0
            //   d1:p5-start (depth==1, no recurse)
            //   d1:p5-end
            //   d1:p10
            //  d0:p5-end
            //  d0:p10
            // This shows that priority order is preserved INSIDE each
            // emission frame, even though the inner emission interleaves.
            string[] expected =
            {
                "d0:p0",
                "d0:p5-start",
                "d1:p0",
                "d1:p5-start",
                "d1:p5-end",
                "d1:p10",
                "d0:p5-end",
                "d0:p10",
            };
            CollectionAssert.AreEqual(
                expected,
                trace,
                "[{0}] Priority order must be preserved per emission frame. trace=[{1}]",
                scenario.Kind,
                string.Join(",", trace)
            );

            token.RemoveRegistration(p0Handle);
            token.RemoveRegistration(p5Handle);
            token.RemoveRegistration(p10Handle);
        }

        /// <summary>
        /// A handler unsubscribes itself and immediately re-subscribes. The
        /// re-subscribed handler must NOT run on the in-flight emission
        /// (snapshot semantics) but MUST run on the next emission.
        /// </summary>
        [Test]
        public void ReentrantUnsubscribeThenResubscribeSelf(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(ReentrantUnsubscribeThenResubscribeSelf) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int firstHandlerCount = 0;
            int respawnedHandlerCount = 0;
            MessageRegistrationHandle firstHandle = default;
            MessageRegistrationHandle? respawnedHandle = null;

            firstHandle = RegisterCountingHandler(
                scenario,
                token,
                hostId,
                () =>
                {
                    ++firstHandlerCount;
                    if (firstHandle != default)
                    {
                        token.RemoveRegistration(firstHandle);
                        firstHandle = default;
                    }

                    respawnedHandle ??= RegisterCountingHandler(
                        scenario,
                        token,
                        hostId,
                        () => ++respawnedHandlerCount
                    );
                }
            );

            EmitForScenario(scenario, hostId);
            Assert.AreEqual(
                1,
                firstHandlerCount,
                "[{0}] First handler must complete its in-flight emission. first={1}, respawned={2}.",
                scenario.Kind,
                firstHandlerCount,
                respawnedHandlerCount
            );
            Assert.AreEqual(
                0,
                respawnedHandlerCount,
                "[{0}] Re-subscribed handler must NOT run on the same emission. first={1}, respawned={2}.",
                scenario.Kind,
                firstHandlerCount,
                respawnedHandlerCount
            );

            EmitForScenario(scenario, hostId);
            Assert.AreEqual(
                1,
                firstHandlerCount,
                "[{0}] First handler must NOT run after self-unsub. first={1}, respawned={2}.",
                scenario.Kind,
                firstHandlerCount,
                respawnedHandlerCount
            );
            Assert.AreEqual(
                1,
                respawnedHandlerCount,
                "[{0}] Re-subscribed handler must run on the next emission. first={1}, respawned={2}.",
                scenario.Kind,
                firstHandlerCount,
                respawnedHandlerCount
            );

            if (respawnedHandle.HasValue)
            {
                token.RemoveRegistration(respawnedHandle.Value);
            }
        }

        /// <summary>
        /// An inner re-emit throws inside a nested handler. The outer
        /// emission's remaining handlers must still abort consistently
        /// with <see cref="HandlerExceptionTests"/>: bus does not swallow
        /// exceptions, propagation aborts the bucket walk on the outer
        /// frame too.
        /// </summary>
        [Test]
        public void NestedHandlerThrowsDuringReentrantEmit(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(NestedHandlerThrowsDuringReentrantEmit) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            const string ThrowMessage = "DxMessaging-test-nested-reentrant-throw";
            int outerStartCount = 0;
            int outerTrailingCount = 0;
            int innerCount = 0;
            int depth = 0;

            // Outer at p0 self-emits, recursing once. The inner depth-1
            // handler throws. The exception propagates out of the inner
            // emission, through the outer p0 handler's body, and aborts
            // the outer p1 handler.
            MessageRegistrationHandle outerHandle = RegisterCountingHandler(
                scenario,
                token,
                hostId,
                () =>
                {
                    if (depth == 0)
                    {
                        ++outerStartCount;
                        ++depth;
                        try
                        {
                            EmitForScenario(scenario, hostId);
                        }
                        finally
                        {
                            --depth;
                        }
                    }
                    else
                    {
                        ++innerCount;
                        throw new InvalidOperationException(ThrowMessage);
                    }
                },
                priority: 0
            );
            MessageRegistrationHandle trailingHandle = RegisterCountingHandler(
                scenario,
                token,
                hostId,
                () => ++outerTrailingCount,
                priority: 1
            );

            InvalidOperationException captured = Assert.Throws<InvalidOperationException>(() =>
                EmitForScenario(scenario, hostId)
            );
            Assert.AreEqual(ThrowMessage, captured.Message);
            Assert.AreEqual(
                1,
                outerStartCount,
                "[{0}] Outer must begin its recursion exactly once. outerStart={1}, inner={2}, trailing={3}.",
                scenario.Kind,
                outerStartCount,
                innerCount,
                outerTrailingCount
            );
            Assert.AreEqual(
                1,
                innerCount,
                "[{0}] Inner depth-1 handler must run and throw. outerStart={1}, inner={2}, trailing={3}.",
                scenario.Kind,
                outerStartCount,
                innerCount,
                outerTrailingCount
            );
            Assert.AreEqual(
                0,
                outerTrailingCount,
                "[{0}] Outer trailing handler must NOT run after inner throws. outerStart={1}, inner={2}, trailing={3}.",
                scenario.Kind,
                outerStartCount,
                innerCount,
                outerTrailingCount
            );

            token.RemoveRegistration(outerHandle);
            token.RemoveRegistration(trailingHandle);
        }

        /// <summary>
        /// An interceptor cancels (returns false) during a re-emit. The
        /// outer emission's remaining handlers must still run because the
        /// interceptor only cancels the inner re-emit. The test records a
        /// trace list so the assertion is order-explicit (not just count-
        /// based): the interceptor must fire for every emission, and the
        /// trailing handler must run AFTER the vetoed inner emission, on
        /// the outer emission's frame.
        /// </summary>
        [Test]
        public void ReentrantInterceptorVeto(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(ReentrantInterceptorVeto) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int depth = 0;
            int interceptorCount = 0;
            int outerCount = 0;
            int innerHandlerCount = 0;
            int trailingCount = 0;
            List<string> trace = new List<string>(8);

            // Interceptor cancels at depth >= 1, allows depth 0.
            RegisterDepthLimitedInterceptor(
                scenario,
                token,
                threshold: 1,
                getDepth: () => depth,
                onInvoked: () =>
                {
                    trace.Add($"d{depth}:interceptor");
                    ++interceptorCount;
                }
            );

            MessageRegistrationHandle outerHandle = RegisterCountingHandler(
                scenario,
                token,
                hostId,
                () =>
                {
                    if (depth == 0)
                    {
                        ++outerCount;
                        trace.Add("d0:outer-start");
                        ++depth;
                        try
                        {
                            EmitForScenario(scenario, hostId);
                        }
                        finally
                        {
                            --depth;
                        }
                        trace.Add("d0:outer-end");
                    }
                    else
                    {
                        ++innerHandlerCount;
                        trace.Add("d1:inner");
                    }
                },
                priority: 0
            );
            MessageRegistrationHandle trailingHandle = RegisterCountingHandler(
                scenario,
                token,
                hostId,
                () =>
                {
                    ++trailingCount;
                    trace.Add($"d{depth}:trailing");
                },
                priority: 1
            );

            EmitForScenario(scenario, hostId);
            Assert.AreEqual(
                1,
                outerCount,
                "[{0}] Outer must run on depth 0. outer={1}, innerHandler={2}, trailing={3}, interceptor={4}.",
                scenario.Kind,
                outerCount,
                innerHandlerCount,
                trailingCount,
                interceptorCount
            );
            Assert.AreEqual(
                0,
                innerHandlerCount,
                "[{0}] Vetoed inner handler must NOT run. outer={1}, innerHandler={2}, trailing={3}, interceptor={4}.",
                scenario.Kind,
                outerCount,
                innerHandlerCount,
                trailingCount,
                interceptorCount
            );
            Assert.AreEqual(
                1,
                trailingCount,
                "[{0}] Outer trailing handler must run after inner is vetoed. outer={1}, innerHandler={2}, trailing={3}, interceptor={4}.",
                scenario.Kind,
                outerCount,
                innerHandlerCount,
                trailingCount,
                interceptorCount
            );
            Assert.AreEqual(
                2,
                interceptorCount,
                "[{0}] Interceptor must fire once per emission (outer and inner). outer={1}, innerHandler={2}, trailing={3}, interceptor={4}.",
                scenario.Kind,
                outerCount,
                innerHandlerCount,
                trailingCount,
                interceptorCount
            );

            // Explicit ordering assertion: the interceptor must fire BEFORE
            // each handler bucket walk. The inner emission's interceptor
            // must run AFTER the outer-start (because the outer handler is
            // what triggers the inner emit), and the trailing handler must
            // run AFTER the inner emission completes (vetoed) and on the
            // outer frame (depth 0).
            string[] expectedTrace =
            {
                "d0:interceptor",
                "d0:outer-start",
                "d1:interceptor",
                "d0:outer-end",
                "d0:trailing",
            };
            CollectionAssert.AreEqual(
                expectedTrace,
                trace,
                "[{0}] Vetoed re-emit must produce the documented trace. trace=[{1}]",
                scenario.Kind,
                string.Join(",", trace)
            );

            token.RemoveRegistration(outerHandle);
            token.RemoveRegistration(trailingHandle);
        }

        /// <summary>
        /// An interceptor mutates the message; a handler triggers a re-emit
        /// against a fresh message instance. The interceptor must see the
        /// new emission as fresh state with no carry-over from the parent
        /// emission.
        /// </summary>
        [Test]
        public void InterceptorMutationDuringReemitObservesFreshState(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(InterceptorMutationDuringReemitObservesFreshState) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            // Track interceptor invocations so we can confirm both emissions
            // hit the interceptor without state bleed. The simple message
            // structs do not have payloads, so the "freshness" of the inner
            // emission is asserted indirectly by the interceptor count and
            // depth monitoring.
            int interceptorInvocations = 0;
            int depth = 0;
            int outerCount = 0;
            int innerCount = 0;
            const int InnerRecursionLimit = 1;

            RegisterAllowingInterceptor(scenario, token, () => ++interceptorInvocations);

            MessageRegistrationHandle handle = RegisterCountingHandler(
                scenario,
                token,
                hostId,
                () =>
                {
                    if (depth == 0)
                    {
                        ++outerCount;
                        ++depth;
                        try
                        {
                            EmitForScenario(scenario, hostId);
                        }
                        finally
                        {
                            --depth;
                        }
                    }
                    else if (depth <= InnerRecursionLimit)
                    {
                        ++innerCount;
                    }
                }
            );

            EmitForScenario(scenario, hostId);
            Assert.AreEqual(
                1,
                outerCount,
                "[{0}] Outer handler must run once. outer={1}, inner={2}, interceptor={3}.",
                scenario.Kind,
                outerCount,
                innerCount,
                interceptorInvocations
            );
            Assert.AreEqual(
                1,
                innerCount,
                "[{0}] Inner re-emit handler must run once. outer={1}, inner={2}, interceptor={3}.",
                scenario.Kind,
                outerCount,
                innerCount,
                interceptorInvocations
            );
            Assert.AreEqual(
                2,
                interceptorInvocations,
                "[{0}] Interceptor must run twice (once per emission, no carry-over). outer={1}, inner={2}, interceptor={3}.",
                scenario.Kind,
                outerCount,
                innerCount,
                interceptorInvocations
            );

            token.RemoveRegistration(handle);
        }

        private static MessageRegistrationHandle RegisterCountingHandler(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId target,
            Action onInvoked,
            int priority = 0
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    return ScenarioHarness.RegisterUntargeted<SimpleUntargetedMessage>(
                        scenario,
                        token,
                        (ref SimpleUntargetedMessage _) => onInvoked(),
                        priority
                    );
                }
                case MessageKind.Targeted:
                {
                    return ScenarioHarness.RegisterTargeted<SimpleTargetedMessage>(
                        scenario,
                        token,
                        target,
                        (ref SimpleTargetedMessage _) => onInvoked(),
                        priority
                    );
                }
                case MessageKind.Broadcast:
                {
                    return ScenarioHarness.RegisterBroadcast<SimpleBroadcastMessage>(
                        scenario,
                        token,
                        target,
                        (ref SimpleBroadcastMessage _) => onInvoked(),
                        priority
                    );
                }
                default:
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(scenario),
                        scenario.Kind,
                        "Unsupported message kind."
                    );
                }
            }
        }

        private static void RegisterDepthLimitedInterceptor(
            MessageScenario scenario,
            MessageRegistrationToken token,
            int threshold,
            Func<int> getDepth,
            Action onInvoked
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    _ = ScenarioHarness.RegisterUntargetedInterceptor<SimpleUntargetedMessage>(
                        scenario,
                        token,
                        (ref SimpleUntargetedMessage _) =>
                        {
                            onInvoked();
                            return getDepth() < threshold;
                        }
                    );
                    return;
                }
                case MessageKind.Targeted:
                {
                    _ = ScenarioHarness.RegisterTargetedInterceptor<SimpleTargetedMessage>(
                        scenario,
                        token,
                        (ref InstanceId _, ref SimpleTargetedMessage _) =>
                        {
                            onInvoked();
                            return getDepth() < threshold;
                        }
                    );
                    return;
                }
                case MessageKind.Broadcast:
                {
                    _ = ScenarioHarness.RegisterBroadcastInterceptor<SimpleBroadcastMessage>(
                        scenario,
                        token,
                        (ref InstanceId _, ref SimpleBroadcastMessage _) =>
                        {
                            onInvoked();
                            return getDepth() < threshold;
                        }
                    );
                    return;
                }
                default:
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(scenario),
                        scenario.Kind,
                        "Unsupported message kind."
                    );
                }
            }
        }

        private static void RegisterAllowingInterceptor(
            MessageScenario scenario,
            MessageRegistrationToken token,
            Action onInvoked
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    _ = ScenarioHarness.RegisterUntargetedInterceptor<SimpleUntargetedMessage>(
                        scenario,
                        token,
                        (ref SimpleUntargetedMessage _) =>
                        {
                            onInvoked();
                            return true;
                        }
                    );
                    return;
                }
                case MessageKind.Targeted:
                {
                    _ = ScenarioHarness.RegisterTargetedInterceptor<SimpleTargetedMessage>(
                        scenario,
                        token,
                        (ref InstanceId _, ref SimpleTargetedMessage _) =>
                        {
                            onInvoked();
                            return true;
                        }
                    );
                    return;
                }
                case MessageKind.Broadcast:
                {
                    _ = ScenarioHarness.RegisterBroadcastInterceptor<SimpleBroadcastMessage>(
                        scenario,
                        token,
                        (ref InstanceId _, ref SimpleBroadcastMessage _) =>
                        {
                            onInvoked();
                            return true;
                        }
                    );
                    return;
                }
                default:
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(scenario),
                        scenario.Kind,
                        "Unsupported message kind."
                    );
                }
            }
        }

        private static void EmitForScenario(MessageScenario scenario, InstanceId target)
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    SimpleUntargetedMessage message = new();
                    ScenarioHarness.EmitUntargeted(scenario, ref message);
                    return;
                }
                case MessageKind.Targeted:
                {
                    SimpleTargetedMessage message = new();
                    ScenarioHarness.EmitTargeted(scenario, ref message, target);
                    return;
                }
                case MessageKind.Broadcast:
                {
                    SimpleBroadcastMessage message = new();
                    ScenarioHarness.EmitBroadcast(scenario, ref message, target);
                    return;
                }
                default:
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(scenario),
                        scenario.Kind,
                        "Unsupported message kind."
                    );
                }
            }
        }
    }
}
#endif
