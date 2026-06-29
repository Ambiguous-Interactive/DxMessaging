#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using NUnit.Framework;
    using UnityEngine;

    /// <summary>
    /// Systematic correctness coverage for registering many handlers and then DEREGISTERING them
    /// in ARBITRARY / interleaved order (forward, reverse, evens-then-odds, middle-out, and
    /// seeded shuffles), across every message kind. After each removal the surviving handlers MUST
    /// still fire exactly once each, in their original registration order (or, for mixed
    /// priorities, in priority-then-registration order), and no over-deregistration error may be
    /// logged (the base routes <see cref="LogLevel.Error"/> to <c>Debug.LogError</c>, which fails
    /// the test). Also covers arbitrary-order removal followed by re-registration, idempotent
    /// double-removal, and refcounted (same-delegate) registration removed in arbitrary order.
    ///
    /// This pins the contract that deregistration order is irrelevant to the surviving set's
    /// dispatch behaviour — the load-bearing invariant for any change to the registration storage
    /// (the handle is a unique, never-reused id; storage must not alias or reorder on out-of-order
    /// removal).
    /// </summary>
    public sealed class MixedOrderDeregistrationTests : MessagingTestBase
    {
        private const int HandlerCount = 8;

        [Test]
        public void ArbitraryOrderDeregistrationKeepsSurvivorsInRegistrationOrder(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            foreach (int[] removal in RemovalPermutations(HandlerCount))
            {
                (MessageRegistrationToken token, InstanceId hostId) = NewHost(scenario);
                List<int> order = new();
                MessageRegistrationHandle[] handles = new MessageRegistrationHandle[HandlerCount];
                List<int> liveInRegistrationOrder = new();
                for (int i = 0; i < HandlerCount; i++)
                {
                    int label = i;
                    handles[i] = ScenarioCallbacks.RegisterCountingHandler(
                        scenario,
                        token,
                        hostId,
                        () => order.Add(label)
                    );
                    liveInRegistrationOrder.Add(i);
                }

                AssertEmitOrder(
                    scenario,
                    hostId,
                    order,
                    liveInRegistrationOrder.ToArray(),
                    $"anchor (all live), perm=[{string.Join(",", removal)}]"
                );

                foreach (int idx in removal)
                {
                    token.RemoveRegistration(handles[idx]);
                    _ = liveInRegistrationOrder.Remove(idx);
                    AssertEmitOrder(
                        scenario,
                        hostId,
                        order,
                        liveInRegistrationOrder.ToArray(),
                        $"after removing {idx}, perm=[{string.Join(",", removal)}]"
                    );
                }

                // Everything removed: nothing fires.
                order.Clear();
                ScenarioCallbacks.EmitForKind(scenario, hostId);
                Assert.AreEqual(
                    0,
                    order.Count,
                    $"no handler may fire after all are removed, perm=[{string.Join(",", removal)}]"
                );
            }
        }

        [Test]
        public void MixedPriorityArbitraryOrderDeregistrationKeepsSurvivorsInDispatchOrder(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            int[] priorities = { 1, 0, 2, 0, 1, 2, 0, 1 };
            int n = priorities.Length;

            foreach (int[] removal in RemovalPermutations(n))
            {
                (MessageRegistrationToken token, InstanceId hostId) = NewHost(scenario);
                List<int> order = new();
                MessageRegistrationHandle[] handles = new MessageRegistrationHandle[n];
                List<int> liveInRegistrationOrder = new();
                for (int i = 0; i < n; i++)
                {
                    int label = i;
                    handles[i] = ScenarioCallbacks.RegisterCountingHandler(
                        scenario,
                        token,
                        hostId,
                        () => order.Add(label),
                        priorities[i]
                    );
                    liveInRegistrationOrder.Add(i);
                }

                AssertEmitOrder(
                    scenario,
                    hostId,
                    order,
                    ExpectedByPriority(liveInRegistrationOrder, priorities),
                    $"anchor (all live), perm=[{string.Join(",", removal)}]"
                );

                foreach (int idx in removal)
                {
                    token.RemoveRegistration(handles[idx]);
                    _ = liveInRegistrationOrder.Remove(idx);
                    AssertEmitOrder(
                        scenario,
                        hostId,
                        order,
                        ExpectedByPriority(liveInRegistrationOrder, priorities),
                        $"after removing {idx}, perm=[{string.Join(",", removal)}]"
                    );
                }
            }
        }

        [Test]
        public void ArbitraryOrderRemovalThenReRegistrationMaintainsRegistrationOrder(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            (MessageRegistrationToken token, InstanceId hostId) = NewHost(scenario);
            List<int> order = new();
            MessageRegistrationHandle[] handles = new MessageRegistrationHandle[6];
            List<int> live = new();
            for (int i = 0; i < 6; i++)
            {
                int label = i;
                handles[i] = ScenarioCallbacks.RegisterCountingHandler(
                    scenario,
                    token,
                    hostId,
                    () => order.Add(label)
                );
                live.Add(i);
            }

            // Remove a non-contiguous subset out of order: 3, then 1, then 4.
            foreach (int idx in new[] { 3, 1, 4 })
            {
                token.RemoveRegistration(handles[idx]);
                _ = live.Remove(idx);
            }
            AssertEmitOrder(
                scenario,
                hostId,
                order,
                live.ToArray(),
                "after arbitrary-order removal"
            );

            // Re-register two new handlers; they append to the LIVE registration order.
            for (int i = 6; i < 8; i++)
            {
                int label = i;
                _ = ScenarioCallbacks.RegisterCountingHandler(
                    scenario,
                    token,
                    hostId,
                    () => order.Add(label)
                );
                live.Add(i);
            }
            AssertEmitOrder(
                scenario,
                hostId,
                order,
                live.ToArray(),
                "after re-registration following arbitrary-order removal"
            );
        }

        [Test]
        public void RemovingTheSameHandleTwiceIsIdempotent(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            (MessageRegistrationToken token, InstanceId hostId) = NewHost(scenario);
            List<int> order = new();
            MessageRegistrationHandle[] handles = new MessageRegistrationHandle[3];
            for (int i = 0; i < 3; i++)
            {
                int label = i;
                handles[i] = ScenarioCallbacks.RegisterCountingHandler(
                    scenario,
                    token,
                    hostId,
                    () => order.Add(label)
                );
            }

            token.RemoveRegistration(handles[1]);
            // Removing an already-removed handle must be a silent no-op: no exception, and no
            // over-deregistration error (the base fails the test on any logged Error).
            Assert.DoesNotThrow(() => token.RemoveRegistration(handles[1]));
            Assert.DoesNotThrow(() => token.RemoveRegistration(handles[1]));

            AssertEmitOrder(
                scenario,
                hostId,
                order,
                new[] { 0, 2 },
                "survivors after idempotent double-removal of the middle handle"
            );
        }

        [Test]
        public void RefcountUntargetedHandlerSurvivesArbitraryOrderPartialDeregistration()
        {
            MessageScenario untargeted = MessageScenario.Untargeted();
            (MessageRegistrationToken token, InstanceId hostId) = NewHost(untargeted);
            int invocations = 0;
            // ONE delegate instance, registered repeatedly: the bus refcounts the same
            // (handler, priority) into a SINGLE dispatch entry, so it fires ONCE per emit
            // regardless of count, until every registration is removed.
            System.Action<DxMessaging.Tests.Runtime.Scripts.Messages.SimpleUntargetedMessage> shared =
                _ => invocations++;

            MessageRegistrationHandle[] handles = new MessageRegistrationHandle[4];
            for (int i = 0; i < 4; i++)
            {
                handles[i] =
                    token.RegisterUntargeted<DxMessaging.Tests.Runtime.Scripts.Messages.SimpleUntargetedMessage>(
                        shared
                    );
            }

            AssertFiresOnce(untargeted, hostId, ref invocations, "refcount 4");

            // Remove three of the four registrations in arbitrary order; the handler still fires.
            foreach (int idx in new[] { 2, 0, 3 })
            {
                token.RemoveRegistration(handles[idx]);
                AssertFiresOnce(
                    untargeted,
                    hostId,
                    ref invocations,
                    $"after removing refcount handle {idx}"
                );
            }

            // Remove the last registration: count -> 0, handler stops firing.
            token.RemoveRegistration(handles[1]);
            invocations = 0;
            ScenarioCallbacks.EmitForKind(untargeted, hostId);
            Assert.AreEqual(
                0,
                invocations,
                "after the final refcount registration is removed the handler must stop firing"
            );
        }

        private static void AssertFiresOnce(
            MessageScenario scenario,
            InstanceId hostId,
            ref int invocations,
            string ctx
        )
        {
            invocations = 0;
            ScenarioCallbacks.EmitForKind(scenario, hostId);
            Assert.AreEqual(
                1,
                invocations,
                $"a refcounted handler must fire exactly once per emit while its count >= 1 ({ctx})"
            );
        }

        [Test]
        public void ArbitraryOrderPostProcessorDeregistrationKeepsSurvivorsInRegistrationOrder(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            foreach (int[] removal in RemovalPermutations(HandlerCount))
            {
                (MessageRegistrationToken token, InstanceId hostId) = NewHost(scenario);
                List<int> order = new();
                MessageRegistrationHandle[] handles = new MessageRegistrationHandle[HandlerCount];
                List<int> live = new();
                for (int i = 0; i < HandlerCount; i++)
                {
                    int label = i;
                    handles[i] = ScenarioCallbacks.RegisterCountingPostProcessor(
                        scenario,
                        token,
                        hostId,
                        () => order.Add(label)
                    );
                    live.Add(i);
                }

                AssertEmitOrder(
                    scenario,
                    hostId,
                    order,
                    live.ToArray(),
                    $"post-processors anchor, perm=[{string.Join(",", removal)}]"
                );
                foreach (int idx in removal)
                {
                    token.RemoveRegistration(handles[idx]);
                    _ = live.Remove(idx);
                    AssertEmitOrder(
                        scenario,
                        hostId,
                        order,
                        live.ToArray(),
                        $"post-processors after removing {idx}, perm=[{string.Join(",", removal)}]"
                    );
                }
            }
        }

        [Test]
        public void ArbitraryOrderInterceptorDeregistrationKeepsSurvivorsInRegistrationOrder(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            foreach (int[] removal in RemovalPermutations(HandlerCount))
            {
                (MessageRegistrationToken token, InstanceId hostId) = NewHost(scenario);
                List<int> order = new();
                // A sink handler so the message is actually dispatched (interceptors run on emit).
                _ = ScenarioCallbacks.RegisterCountingHandler(scenario, token, hostId, () => { });

                MessageRegistrationHandle[] handles = new MessageRegistrationHandle[HandlerCount];
                List<int> live = new();
                for (int i = 0; i < HandlerCount; i++)
                {
                    int label = i;
                    handles[i] = ScenarioCallbacks.RegisterCountingInterceptor(
                        scenario,
                        token,
                        result: () => true,
                        onIntercepted: () => order.Add(label)
                    );
                    live.Add(i);
                }

                AssertEmitOrder(
                    scenario,
                    hostId,
                    order,
                    live.ToArray(),
                    $"interceptors anchor, perm=[{string.Join(",", removal)}]"
                );
                foreach (int idx in removal)
                {
                    token.RemoveRegistration(handles[idx]);
                    _ = live.Remove(idx);
                    AssertEmitOrder(
                        scenario,
                        hostId,
                        order,
                        live.ToArray(),
                        $"interceptors after removing {idx}, perm=[{string.Join(",", removal)}]"
                    );
                }
            }
        }

        private (MessageRegistrationToken token, InstanceId hostId) NewHost(
            MessageScenario scenario
        )
        {
            GameObject host = new(
                $"{nameof(MixedOrderDeregistrationTests)}_{scenario.Kind}",
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            return (GetToken(component), host);
        }

        private static void AssertEmitOrder(
            MessageScenario scenario,
            InstanceId hostId,
            List<int> order,
            int[] expected,
            string ctx
        )
        {
            order.Clear();
            ScenarioCallbacks.EmitForKind(scenario, hostId);
            Assert.AreEqual(
                expected,
                order.ToArray(),
                $"surviving handlers must fire exactly once each in dispatch order — {ctx}"
            );
        }

        // Stable order by ascending priority, preserving registration order within a priority.
        private static int[] ExpectedByPriority(List<int> liveInRegistrationOrder, int[] priorities)
        {
            int maxPriority = 0;
            foreach (int p in priorities)
            {
                if (p > maxPriority)
                {
                    maxPriority = p;
                }
            }

            List<int> expected = new(liveInRegistrationOrder.Count);
            for (int p = 0; p <= maxPriority; p++)
            {
                foreach (int idx in liveInRegistrationOrder)
                {
                    if (priorities[idx] == p)
                    {
                        expected.Add(idx);
                    }
                }
            }

            return expected.ToArray();
        }

        // Deterministic removal permutations of [0..n): forward, reverse, evens-then-odds,
        // odds-then-evens, middle-out, plus two seeded Fisher-Yates shuffles (seeded by the base's
        // TestSeed via _random, so the case is reproducible).
        private IEnumerable<int[]> RemovalPermutations(int n)
        {
            int[] forward = new int[n];
            for (int i = 0; i < n; i++)
            {
                forward[i] = i;
            }
            yield return forward;

            int[] reverse = new int[n];
            for (int i = 0; i < n; i++)
            {
                reverse[i] = n - 1 - i;
            }
            yield return reverse;

            List<int> evensThenOdds = new(n);
            for (int i = 0; i < n; i += 2)
            {
                evensThenOdds.Add(i);
            }
            for (int i = 1; i < n; i += 2)
            {
                evensThenOdds.Add(i);
            }
            yield return evensThenOdds.ToArray();

            List<int> oddsThenEvens = new(n);
            for (int i = 1; i < n; i += 2)
            {
                oddsThenEvens.Add(i);
            }
            for (int i = 0; i < n; i += 2)
            {
                oddsThenEvens.Add(i);
            }
            yield return oddsThenEvens.ToArray();

            List<int> middleOut = new(n);
            int lo = (n - 1) / 2;
            int hi = lo + 1;
            bool takeLow = true;
            while (lo >= 0 || hi < n)
            {
                if (takeLow && lo >= 0)
                {
                    middleOut.Add(lo--);
                }
                else if (hi < n)
                {
                    middleOut.Add(hi++);
                }
                takeLow = !takeLow;
            }
            yield return middleOut.ToArray();

            for (int shuffle = 0; shuffle < 2; shuffle++)
            {
                int[] order = new int[n];
                for (int i = 0; i < n; i++)
                {
                    order[i] = i;
                }
                for (int i = n - 1; i > 0; i--)
                {
                    int j = _random.Next(i + 1);
                    (order[i], order[j]) = (order[j], order[i]);
                }
                yield return order;
            }
        }
    }
}
#endif
