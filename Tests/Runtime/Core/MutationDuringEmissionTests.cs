#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using UnityEngine;

    /// <summary>
    /// Tests that mutate registration state during emission and ensure snapshot semantics:
    /// - Current pass uses the listeners present when emission begins
    /// - Newly added listeners only run on subsequent emissions
    /// - Removing listeners during emission does not throw or cause message loss for the current pass
    /// </summary>
    public sealed class MutationDuringEmissionTests : MessagingTestBase
    {
        private const int ManyCount = 6; // Forces default iteration paths (>5)

        [Test]
        [Category("Stress")]
        public void AddLocalHandlerMany(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(AddLocalHandlerMany) + "_" + scenario,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int[] counts = new int[ManyCount + 1];
            MessageRegistrationHandle[] handles = new MessageRegistrationHandle[ManyCount + 1];
            bool added = false;

            // Register ManyCount handlers on a single MessageHandler to stress TypedHandler iteration
            for (int i = 0; i < ManyCount; i++)
            {
                int idx = i;
                Action onInvoke = () =>
                {
                    counts[idx]++;
                    if (!added && idx == 0)
                    {
                        added = true;
                        handles[ManyCount] = ScenarioCallbacks.RegisterCountingHandler(
                            scenario,
                            token,
                            hostId,
                            () => counts[ManyCount]++
                        );
                    }
                };
                handles[idx] = ScenarioCallbacks.RegisterCountingHandler(
                    scenario,
                    token,
                    hostId,
                    onInvoke
                );
            }

            ScenarioCallbacks.EmitForKind(scenario, hostId);
            int expected = ManyCount;
            int total = 0;
            for (int i = 0; i < ManyCount; i++)
            {
                total += counts[i];
            }

            Assert.AreEqual(expected, total, "All baseline handlers should run on first emission.");
            Assert.AreEqual(
                0,
                counts[ManyCount],
                "Newly added handler must not run in the same emission."
            );

            ScenarioCallbacks.EmitForKind(scenario, hostId);
            total = 0;
            for (int i = 0; i < ManyCount; i++)
            {
                total += counts[i];
            }

            Assert.AreEqual(
                expected + ManyCount,
                total,
                "Baseline handlers should run again on second emission."
            );
            Assert.AreEqual(
                1,
                counts[ManyCount],
                "New handler should run starting on the second emission."
            );

            for (int i = 0; i < handles.Length; i++)
            {
                if (handles[i] != default)
                {
                    token.RemoveRegistration(handles[i]);
                }
            }
        }

        /// <summary>
        /// A handler that registers a NEW delegate of a DIFFERENT shape
        /// (default <see cref="Action{T}"/> from inside a fast handler) on the
        /// SAME <see cref="MessageHandler"/> for the same message type
        /// mid-emission: the new delegate must NOT fire in the current
        /// emission and MUST fire in the next one. The flattened dispatch
        /// gates this uniformly for every kind; the legacy per-handler
        /// prefreeze could leak this exact shape through caches that did not
        /// exist (or were not stamped) at emission start, firing it
        /// same-emission depending on unrelated handler counts.
        /// </summary>
        [Test]
        public void SameHandlerCrossShapeRegistrationDoesNotFireSameEmission(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(SameHandlerCrossShapeRegistrationDoesNotFireSameEmission) + "_" + scenario,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int fastCount = 0;
            int defaultCount = 0;
            bool added = false;
            MessageRegistrationHandle defaultHandle = default;

            // RegisterCountingHandler registers a FAST handler; from inside it,
            // register a DEFAULT (Action<T>) delegate for the same type on
            // the same MessageHandler - the previously leaking shape.
            MessageRegistrationHandle fastHandle = ScenarioCallbacks.RegisterCountingHandler(
                scenario,
                token,
                hostId,
                () =>
                {
                    ++fastCount;
                    if (!added)
                    {
                        added = true;
                        defaultHandle = ScenarioCallbacks.RegisterDefaultHandler(
                            scenario,
                            token,
                            hostId,
                            () => ++defaultCount
                        );
                    }
                }
            );

            ScenarioCallbacks.EmitForKind(scenario, hostId);
            Assert.AreEqual(
                1,
                fastCount,
                "[{0}] The registering fast handler must run on the first emission.",
                scenario.Kind
            );
            Assert.AreEqual(
                0,
                defaultCount,
                "[{0}] A default-shape delegate registered mid-emission on the SAME "
                    + "MessageHandler must NOT fire in the emission that registered it.",
                scenario.Kind
            );

            ScenarioCallbacks.EmitForKind(scenario, hostId);
            Assert.AreEqual(
                2,
                fastCount,
                "[{0}] The fast handler must run again on the second emission.",
                scenario.Kind
            );
            Assert.AreEqual(
                1,
                defaultCount,
                "[{0}] The mid-emission registration must fire starting with the "
                    + "next emission.",
                scenario.Kind
            );

            token.RemoveRegistration(fastHandle);
            if (defaultHandle != default)
            {
                token.RemoveRegistration(defaultHandle);
            }
        }

        [Test]
        [Category("Stress")]
        public void UntargetedRemoveSelfMany()
        {
            GameObject host = new(
                nameof(UntargetedRemoveSelfMany),
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);

            int[] counts = new int[ManyCount];
            MessageRegistrationHandle[] handles = new MessageRegistrationHandle[ManyCount];

            for (int i = 0; i < ManyCount; i++)
            {
                int idx = i;
                MessageRegistrationHandle h = default;
                h = token.RegisterUntargeted<SimpleUntargetedMessage>(_ =>
                {
                    counts[idx]++;
                    token.RemoveRegistration(h);
                });
                handles[idx] = h;
            }

            SimpleUntargetedMessage msg = new();
            msg.EmitUntargeted();

            int total = 0;
            for (int i = 0; i < ManyCount; i++)
            {
                total += counts[i];
            }

            Assert.AreEqual(
                ManyCount,
                total,
                "Every baseline handler should run exactly once during the emission."
            );

            msg.EmitUntargeted();
            total = 0;
            for (int i = 0; i < ManyCount; i++)
            {
                total += counts[i];
            }

            Assert.AreEqual(
                ManyCount,
                total,
                "No handler should run again after removing itself in the previous pass."
            );
        }

        [Test]
        [Category("Stress")]
        public void AddHandlerAcrossHandlersMany(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            // Many distinct MessageHandlers (bus-level list growth during iteration)
            InstanceId targetId = default;
            if (scenario.Kind != MessageKind.Untargeted)
            {
                GameObject targetGo = new(
                    nameof(AddHandlerAcrossHandlersMany) + "_" + scenario + "_Target"
                );
                _spawned.Add(targetGo);
                targetId = targetGo;
            }

            List<(EmptyMessageAwareComponent comp, MessageRegistrationToken token)> listeners =
                new();
            for (int i = 0; i < ManyCount; i++)
            {
                GameObject go = new(
                    $"{nameof(AddHandlerAcrossHandlersMany)}_{scenario}_Bus_{i}",
                    typeof(EmptyMessageAwareComponent)
                );
                _spawned.Add(go);
                EmptyMessageAwareComponent c = go.GetComponent<EmptyMessageAwareComponent>();
                listeners.Add((c, GetToken(c)));
            }

            int[] counts = new int[ManyCount + 1];
            List<(MessageRegistrationToken token, MessageRegistrationHandle handle)> handles =
                new();
            bool added = false;

            for (int i = 0; i < listeners.Count; i++)
            {
                int idx = i;
                MessageRegistrationToken listenerToken = listeners[i].token;
                Action onInvoke = () =>
                {
                    counts[idx]++;
                    if (!added && idx == 0)
                    {
                        added = true;
                        GameObject extra = new(
                            $"{nameof(AddHandlerAcrossHandlersMany)}_{scenario}_Bus_Extra",
                            typeof(EmptyMessageAwareComponent)
                        );
                        _spawned.Add(extra);
                        EmptyMessageAwareComponent extraComp =
                            extra.GetComponent<EmptyMessageAwareComponent>();
                        MessageRegistrationToken extraToken = GetToken(extraComp);
                        MessageRegistrationHandle extraHandle =
                            ScenarioCallbacks.RegisterCountingHandler(
                                scenario,
                                extraToken,
                                targetId,
                                () => counts[ManyCount]++
                            );
                        handles.Add((extraToken, extraHandle));
                    }
                };
                MessageRegistrationHandle handle = ScenarioCallbacks.RegisterCountingHandler(
                    scenario,
                    listenerToken,
                    targetId,
                    onInvoke
                );
                handles.Add((listenerToken, handle));
            }

            ScenarioCallbacks.EmitForKind(scenario, targetId);

            int total = 0;
            for (int i = 0; i < ManyCount; i++)
            {
                total += counts[i];
            }

            Assert.AreEqual(
                ManyCount,
                total,
                "All baseline handlers should run on first emission."
            );
            Assert.AreEqual(
                0,
                counts[ManyCount],
                "Newly added MessageHandler must not run in the same emission."
            );

            ScenarioCallbacks.EmitForKind(scenario, targetId);
            total = 0;
            for (int i = 0; i < ManyCount; i++)
            {
                total += counts[i];
            }

            Assert.AreEqual(
                ManyCount * 2,
                total,
                "Baseline handlers should run again on second emission."
            );
            Assert.AreEqual(
                1,
                counts[ManyCount],
                "Newly added MessageHandler should run starting on the second emission."
            );

            foreach (
                (MessageRegistrationToken token, MessageRegistrationHandle handle) entry in handles
            )
            {
                entry.token.RemoveRegistration(entry.handle);
            }
        }

        [Test]
        [Category("Stress")]
        public void TargetedWithoutTargetingAddLocalHandlerMany()
        {
            GameObject host = new(
                nameof(TargetedWithoutTargetingAddLocalHandlerMany),
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);

            int[] counts = new int[ManyCount + 1];
            MessageRegistrationHandle[] handles = new MessageRegistrationHandle[ManyCount + 1];
            bool added = false;

            for (int i = 0; i < ManyCount; i++)
            {
                int idx = i;
                handles[idx] = token.RegisterTargetedWithoutTargeting<SimpleTargetedMessage>(
                    (_, _) =>
                    {
                        counts[idx]++;
                        if (!added && idx == 0)
                        {
                            added = true;
                            handles[ManyCount] =
                                token.RegisterTargetedWithoutTargeting<SimpleTargetedMessage>(
                                    (_, _) => counts[ManyCount]++
                                );
                        }
                    }
                );
            }

            SimpleTargetedMessage msg = new();
            msg.EmitGameObjectTargeted(host);
            int total = 0;
            for (int i = 0; i < ManyCount; i++)
            {
                total += counts[i];
            }

            Assert.AreEqual(
                ManyCount,
                total,
                "All baseline targeted-without-targeting handlers should run on first emission."
            );
            Assert.AreEqual(
                0,
                counts[ManyCount],
                "Newly added handler must not run in the same emission."
            );

            msg.EmitGameObjectTargeted(host);
            total = 0;
            for (int i = 0; i < ManyCount; i++)
            {
                total += counts[i];
            }

            Assert.AreEqual(
                ManyCount * 2,
                total,
                "Baseline handlers should run again on second emission."
            );
            Assert.AreEqual(
                1,
                counts[ManyCount],
                "New handler should run starting on the second emission."
            );

            for (int i = 0; i < handles.Length; i++)
            {
                if (handles[i] != default)
                {
                    token.RemoveRegistration(handles[i]);
                }
            }
        }

        [Test]
        [Category("Stress")]
        public void BroadcastWithoutSourceAddLocalHandlerMany()
        {
            GameObject host = new(
                nameof(BroadcastWithoutSourceAddLocalHandlerMany),
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);

            int[] counts = new int[ManyCount + 1];
            MessageRegistrationHandle[] handles = new MessageRegistrationHandle[ManyCount + 1];
            bool added = false;

            for (int i = 0; i < ManyCount; i++)
            {
                int idx = i;
                handles[idx] = token.RegisterBroadcastWithoutSource<SimpleBroadcastMessage>(
                    (_, _) =>
                    {
                        counts[idx]++;
                        if (!added && idx == 0)
                        {
                            added = true;
                            handles[ManyCount] =
                                token.RegisterBroadcastWithoutSource<SimpleBroadcastMessage>(
                                    (_, _) => counts[ManyCount]++
                                );
                        }
                    }
                );
            }

            SimpleBroadcastMessage msg = new();
            msg.EmitComponentBroadcast(component);
            int total = 0;
            for (int i = 0; i < ManyCount; i++)
            {
                total += counts[i];
            }

            Assert.AreEqual(
                ManyCount,
                total,
                "All baseline broadcast-without-source handlers should run on first emission."
            );
            Assert.AreEqual(
                0,
                counts[ManyCount],
                "Newly added handler must not run in the same emission."
            );

            msg.EmitComponentBroadcast(component);
            total = 0;
            for (int i = 0; i < ManyCount; i++)
            {
                total += counts[i];
            }

            Assert.AreEqual(
                ManyCount * 2,
                total,
                "Baseline handlers should run again on second emission."
            );
            Assert.AreEqual(
                1,
                counts[ManyCount],
                "New handler should run starting on the second emission."
            );

            for (int i = 0; i < handles.Length; i++)
            {
                if (handles[i] != default)
                {
                    token.RemoveRegistration(handles[i]);
                }
            }
        }

        /// <summary>
        /// Snapshot semantics regression: a handler at one priority bucket must
        /// be allowed to deregister a handler at a later priority bucket, and
        /// the deregistered handler must still fire on the in-flight emission
        /// because its delegate was captured by the snapshot taken before any
        /// handler ran. The TargetedWithoutTargeting dispatch path used to
        /// snapshot per-bucket lazily inside the dispatch loop, so the
        /// later-bucket snapshot was rebuilt after the earlier bucket's
        /// handler had already mutated the typed cache, dropping the entry.
        /// </summary>
        [Test]
        public void TargetedWithoutTargetingDeregisterAcrossPrioritiesIsHonouredOnCurrentSnapshot()
        {
            GameObject host = new(
                nameof(
                    TargetedWithoutTargetingDeregisterAcrossPrioritiesIsHonouredOnCurrentSnapshot
                ),
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);

            int firstCount = 0;
            int secondCount = 0;
            MessageRegistrationHandle secondHandle = default;

            _ = token.RegisterTargetedWithoutTargeting<SimpleTargetedMessage>(
                (_, _) =>
                {
                    ++firstCount;
                    if (secondHandle != default)
                    {
                        token.RemoveRegistration(secondHandle);
                        secondHandle = default;
                    }
                },
                priority: 0
            );

            secondHandle = token.RegisterTargetedWithoutTargeting<SimpleTargetedMessage>(
                (_, _) => ++secondCount,
                priority: 1
            );

            SimpleTargetedMessage msg = new();
            msg.EmitGameObjectTargeted(host);
            Assert.AreEqual(
                1,
                firstCount,
                "First emission must invoke primary exactly once. firstCount={0}, secondCount={1}.",
                firstCount,
                secondCount
            );
            Assert.AreEqual(
                1,
                secondCount,
                "Snapshot frozen at emission start must invoke handler scheduled for removal. firstCount={0}, secondCount={1}.",
                firstCount,
                secondCount
            );

            msg.EmitGameObjectTargeted(host);
            Assert.AreEqual(
                2,
                firstCount,
                "Second emission must invoke primary again. firstCount={0}, secondCount={1}.",
                firstCount,
                secondCount
            );
            Assert.AreEqual(
                1,
                secondCount,
                "Removed handler must not run on the next emission once snapshot is rebuilt. firstCount={0}, secondCount={1}.",
                firstCount,
                secondCount
            );
        }

        /// <summary>
        /// Snapshot semantics regression mirror for BroadcastWithoutSource. A
        /// historical dispatch path prefroze per-MessageHandler typed caches
        /// lazily per priority bucket, so a removal performed by an earlier
        /// bucket polluted the later bucket's snapshot; the flattened dispatch
        /// resolves every delegate into a frozen array at snapshot build, which
        /// makes the cross-priority removal unobservable by construction.
        /// </summary>
        [Test]
        public void BroadcastWithoutSourceDeregisterAcrossPrioritiesIsHonouredOnCurrentSnapshot()
        {
            GameObject host = new(
                nameof(BroadcastWithoutSourceDeregisterAcrossPrioritiesIsHonouredOnCurrentSnapshot),
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);

            int firstCount = 0;
            int secondCount = 0;
            MessageRegistrationHandle secondHandle = default;

            _ = token.RegisterBroadcastWithoutSource<SimpleBroadcastMessage>(
                (_, _) =>
                {
                    ++firstCount;
                    if (secondHandle != default)
                    {
                        token.RemoveRegistration(secondHandle);
                        secondHandle = default;
                    }
                },
                priority: 0
            );

            secondHandle = token.RegisterBroadcastWithoutSource<SimpleBroadcastMessage>(
                (_, _) => ++secondCount,
                priority: 1
            );

            SimpleBroadcastMessage msg = new();
            msg.EmitComponentBroadcast(component);
            Assert.AreEqual(
                1,
                firstCount,
                "First emission must invoke primary exactly once. firstCount={0}, secondCount={1}.",
                firstCount,
                secondCount
            );
            Assert.AreEqual(
                1,
                secondCount,
                "Snapshot frozen at emission start must invoke handler scheduled for removal. firstCount={0}, secondCount={1}.",
                firstCount,
                secondCount
            );

            msg.EmitComponentBroadcast(component);
            Assert.AreEqual(
                2,
                firstCount,
                "Second emission must invoke primary again. firstCount={0}, secondCount={1}.",
                firstCount,
                secondCount
            );
            Assert.AreEqual(
                1,
                secondCount,
                "Removed handler must not run on the next emission once snapshot is rebuilt. firstCount={0}, secondCount={1}.",
                firstCount,
                secondCount
            );
        }

        [Test]
        [Category("Stress")]
        public void GlobalAcceptAllAddDuringHandlerMany()
        {
            // Create several listeners that globally accept all; add one more during handling; ensure it runs next pass only
            List<(EmptyMessageAwareComponent comp, MessageRegistrationToken token)> listeners =
                new();
            for (int i = 0; i < ManyCount; i++)
            {
                GameObject go = new($"Global_{i}", typeof(EmptyMessageAwareComponent));
                _spawned.Add(go);
                EmptyMessageAwareComponent c = go.GetComponent<EmptyMessageAwareComponent>();
                listeners.Add((c, GetToken(c)));
            }

            int[] counts = new int[ManyCount + 1];
            List<(MessageRegistrationToken token, MessageRegistrationHandle handle)> handles =
                new();
            bool added = false;

            for (int i = 0; i < listeners.Count; i++)
            {
                int idx = i;
                MessageRegistrationHandle h = listeners[i]
                    .token.RegisterGlobalAcceptAll(
                        _ => counts[idx]++,
                        (_, _) => counts[idx]++,
                        (_, _) => counts[idx]++
                    );
                handles.Add((listeners[i].token, h));
            }

            // Add a new global listener from inside a local untargeted handler on first pass
            MessageRegistrationHandle adderHandle = listeners[0]
                .token.RegisterUntargeted<SimpleUntargetedMessage>(_ =>
                {
                    if (!added)
                    {
                        added = true;
                        GameObject extra = new("Global_Extra", typeof(EmptyMessageAwareComponent));
                        _spawned.Add(extra);
                        EmptyMessageAwareComponent extraComp =
                            extra.GetComponent<EmptyMessageAwareComponent>();
                        MessageRegistrationToken extraToken = GetToken(extraComp);
                        MessageRegistrationHandle globalHandle = extraToken.RegisterGlobalAcceptAll(
                            _ => counts[ManyCount]++,
                            (_, _) => counts[ManyCount]++,
                            (_, _) => counts[ManyCount]++
                        );
                        handles.Add((extraToken, globalHandle));
                    }
                });
            handles.Add((listeners[0].token, adderHandle));

            SimpleUntargetedMessage msg = new();
            msg.EmitUntargeted();
            int total = 0;
            for (int i = 0; i < ManyCount; i++)
            {
                total += counts[i];
            }

            Assert.AreEqual(
                ManyCount,
                total,
                "All global listeners should run once for the emitted category on first emission."
            );
            Assert.AreEqual(
                0,
                counts[ManyCount],
                "New global listener must not run in the same emission."
            );

            msg.EmitUntargeted();
            total = 0;
            for (int i = 0; i < ManyCount; i++)
            {
                total += counts[i];
            }

            Assert.AreEqual(
                ManyCount * 2,
                total,
                "Global listeners should run again on second emission for the emitted category."
            );
            Assert.AreEqual(
                1,
                counts[ManyCount],
                "New global listener should run on second emission for the emitted category."
            );

            foreach (
                (MessageRegistrationToken token, MessageRegistrationHandle handle) entry in handles
            )
            {
                entry.token.RemoveRegistration(entry.handle);
            }
        }

        [Test]
        public void AddInterceptorDuringInterceptorDoesNotRunInSameEmission(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(AddInterceptorDuringInterceptorDoesNotRunInSameEmission) + "_" + scenario,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int firstCount = 0;
            int secondCount = 0;
            MessageRegistrationHandle? second = null;

            MessageRegistrationHandle first = ScenarioCallbacks.RegisterCountingInterceptor(
                scenario,
                token,
                () =>
                {
                    firstCount++;
                    if (second == null)
                    {
                        second = ScenarioCallbacks.RegisterCountingInterceptor(
                            scenario,
                            token,
                            () =>
                            {
                                secondCount++;
                                return true;
                            }
                        );
                    }

                    return true;
                }
            );

            ScenarioCallbacks.EmitForKind(scenario, hostId);
            Assert.AreEqual(
                1,
                firstCount,
                "First interceptor should run exactly once in first emission."
            );
            Assert.AreEqual(0, secondCount, "New interceptor should not run in the same emission.");

            ScenarioCallbacks.EmitForKind(scenario, hostId);
            Assert.AreEqual(
                2,
                firstCount,
                "First interceptor should run again on second emission."
            );
            Assert.AreEqual(
                1,
                secondCount,
                "New interceptor should run starting on the second emission."
            );

            token.RemoveRegistration(first);
            if (second.HasValue)
            {
                token.RemoveRegistration(second.Value);
            }
        }

        [Test]
        [Category("Stress")]
        public void AddPostProcessorDuringHandlerDoesNotRunInSameEmissionMany(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(AddPostProcessorDuringHandlerDoesNotRunInSameEmissionMany) + "_" + scenario,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int[] handlerCounts = new int[ManyCount];
            int[] ppCounts = new int[ManyCount + 1];
            MessageRegistrationHandle[] handlerHandles = new MessageRegistrationHandle[ManyCount];
            MessageRegistrationHandle ppHandle = default;
            bool added = false;

            for (int i = 0; i < ManyCount; i++)
            {
                int idx = i;
                handlerHandles[idx] = ScenarioCallbacks.RegisterCountingHandler(
                    scenario,
                    token,
                    hostId,
                    () =>
                    {
                        handlerCounts[idx]++;
                        if (!added && idx == 0)
                        {
                            added = true;
                            ppHandle = ScenarioCallbacks.RegisterCountingPostProcessor(
                                scenario,
                                token,
                                hostId,
                                () => ppCounts[ManyCount]++
                            );
                        }
                    }
                );
                _ = ScenarioCallbacks.RegisterCountingPostProcessor(
                    scenario,
                    token,
                    hostId,
                    () => ppCounts[idx]++
                );
            }

            ScenarioCallbacks.EmitForKind(scenario, hostId);

            int handlerTotal = 0;
            for (int i = 0; i < ManyCount; i++)
            {
                handlerTotal += handlerCounts[i];
            }

            Assert.AreEqual(
                ManyCount,
                handlerTotal,
                "All baseline handlers should run on first emission."
            );

            int ppTotal = 0;
            for (int i = 0; i < ManyCount; i++)
            {
                ppTotal += ppCounts[i];
            }

            Assert.AreEqual(
                ManyCount,
                ppTotal,
                "All existing post-processors should run on first emission."
            );
            Assert.AreEqual(
                0,
                ppCounts[ManyCount],
                "Newly added post-processor must not run in the same emission."
            );

            ScenarioCallbacks.EmitForKind(scenario, hostId);
            ppTotal = 0;
            for (int i = 0; i < ManyCount; i++)
            {
                ppTotal += ppCounts[i];
            }

            Assert.AreEqual(
                ManyCount * 2,
                ppTotal,
                "Baseline post-processors should run again on second emission."
            );
            Assert.AreEqual(
                1,
                ppCounts[ManyCount],
                "New post-processor should run starting on the second emission."
            );

            foreach (MessageRegistrationHandle h in handlerHandles)
            {
                token.RemoveRegistration(h);
            }
            if (ppHandle != default)
            {
                token.RemoveRegistration(ppHandle);
            }
        }

        [Test]
        [Category("Stress")]
        public void AddPostProcessorDuringPostProcessorDoesNotRunInSameEmissionMany(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(AddPostProcessorDuringPostProcessorDoesNotRunInSameEmissionMany)
                    + "_"
                    + scenario,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int[] ppCounts = new int[ManyCount + 1];
            MessageRegistrationHandle[] ppHandles = new MessageRegistrationHandle[ManyCount + 1];

            // Ensure there is at least one handler so post-processors will run
            MessageRegistrationHandle hdl = ScenarioCallbacks.RegisterCountingHandler(
                scenario,
                token,
                hostId,
                () => { }
            );

            bool added = false;
            for (int i = 0; i < ManyCount; i++)
            {
                int idx = i;
                ppHandles[idx] = ScenarioCallbacks.RegisterCountingPostProcessor(
                    scenario,
                    token,
                    hostId,
                    () =>
                    {
                        ppCounts[idx]++;
                        if (!added && idx == 0)
                        {
                            added = true;
                            ppHandles[ManyCount] = ScenarioCallbacks.RegisterCountingPostProcessor(
                                scenario,
                                token,
                                hostId,
                                () => ppCounts[ManyCount]++
                            );
                        }
                    }
                );
            }

            ScenarioCallbacks.EmitForKind(scenario, hostId);
            int total = 0;
            for (int i = 0; i < ManyCount; i++)
            {
                total += ppCounts[i];
            }

            Assert.AreEqual(
                ManyCount,
                total,
                "All baseline post-processors should run on first emission."
            );
            Assert.AreEqual(
                0,
                ppCounts[ManyCount],
                "Newly added post-processor must not run in the same emission."
            );

            ScenarioCallbacks.EmitForKind(scenario, hostId);
            total = 0;
            for (int i = 0; i < ManyCount; i++)
            {
                total += ppCounts[i];
            }

            Assert.AreEqual(
                ManyCount * 2,
                total,
                "Baseline post-processors should run again on second emission."
            );
            Assert.AreEqual(
                1,
                ppCounts[ManyCount],
                "New post-processor should run starting on the second emission."
            );

            token.RemoveRegistration(hdl);
            for (int i = 0; i < ppHandles.Length; i++)
            {
                if (ppHandles[i] != default)
                {
                    token.RemoveRegistration(ppHandles[i]);
                }
            }
        }

        [Test]
        [Category("Stress")]
        public void TargetedWithoutTargetingAddHandlerAcrossHandlersMany()
        {
            List<(EmptyMessageAwareComponent comp, MessageRegistrationToken token)> listeners =
                new();
            for (int i = 0; i < ManyCount; i++)
            {
                GameObject go = new($"TWTBus_{i}", typeof(EmptyMessageAwareComponent));
                _spawned.Add(go);
                EmptyMessageAwareComponent c = go.GetComponent<EmptyMessageAwareComponent>();
                listeners.Add((c, GetToken(c)));
            }

            GameObject target = new("TWT_Target");
            _spawned.Add(target);

            int[] counts = new int[ManyCount + 1];
            List<(MessageRegistrationToken token, MessageRegistrationHandle handle)> handles =
                new();
            bool added = false;

            for (int i = 0; i < listeners.Count; i++)
            {
                int idx = i;
                MessageRegistrationHandle handle = listeners[i]
                    .token.RegisterTargetedWithoutTargeting<SimpleTargetedMessage>(
                        (_, _) =>
                        {
                            counts[idx]++;
                            if (!added && idx == 0)
                            {
                                added = true;
                                GameObject extra = new(
                                    "TWTBus_Extra",
                                    typeof(EmptyMessageAwareComponent)
                                );
                                _spawned.Add(extra);
                                EmptyMessageAwareComponent extraComp =
                                    extra.GetComponent<EmptyMessageAwareComponent>();
                                MessageRegistrationToken extraToken = GetToken(extraComp);
                                MessageRegistrationHandle extraHandle =
                                    extraToken.RegisterTargetedWithoutTargeting<SimpleTargetedMessage>(
                                        (_, _) => counts[ManyCount]++
                                    );
                                handles.Add((extraToken, extraHandle));
                            }
                        }
                    );
                handles.Add((listeners[i].token, handle));
            }

            SimpleTargetedMessage msg = new();
            msg.EmitGameObjectTargeted(target);
            int total = 0;
            for (int i = 0; i < ManyCount; i++)
            {
                total += counts[i];
            }

            Assert.AreEqual(ManyCount, total);
            Assert.AreEqual(0, counts[ManyCount]);

            msg.EmitGameObjectTargeted(target);
            total = 0;
            for (int i = 0; i < ManyCount; i++)
            {
                total += counts[i];
            }

            Assert.AreEqual(ManyCount * 2, total);
            Assert.AreEqual(1, counts[ManyCount]);

            foreach (
                (MessageRegistrationToken token, MessageRegistrationHandle handle) entry in handles
            )
            {
                entry.token.RemoveRegistration(entry.handle);
            }
        }

        [Test]
        [Category("Stress")]
        public void BroadcastWithoutSourceAddHandlerAcrossHandlersMany()
        {
            List<(EmptyMessageAwareComponent comp, MessageRegistrationToken token)> listeners =
                new();
            for (int i = 0; i < ManyCount; i++)
            {
                GameObject go = new($"BWOBus_{i}", typeof(EmptyMessageAwareComponent));
                _spawned.Add(go);
                EmptyMessageAwareComponent c = go.GetComponent<EmptyMessageAwareComponent>();
                listeners.Add((c, GetToken(c)));
            }

            int[] counts = new int[ManyCount + 1];
            List<(MessageRegistrationToken token, MessageRegistrationHandle handle)> handles =
                new();
            bool added = false;

            for (int i = 0; i < listeners.Count; i++)
            {
                int idx = i;
                MessageRegistrationHandle handle = listeners[i]
                    .token.RegisterBroadcastWithoutSource<SimpleBroadcastMessage>(
                        (_, _) =>
                        {
                            counts[idx]++;
                            if (!added && idx == 0)
                            {
                                added = true;
                                GameObject extra = new(
                                    "BWOBus_Extra",
                                    typeof(EmptyMessageAwareComponent)
                                );
                                _spawned.Add(extra);
                                EmptyMessageAwareComponent extraComp =
                                    extra.GetComponent<EmptyMessageAwareComponent>();
                                MessageRegistrationToken extraToken = GetToken(extraComp);
                                MessageRegistrationHandle extraHandle =
                                    extraToken.RegisterBroadcastWithoutSource<SimpleBroadcastMessage>(
                                        (_, _) => counts[ManyCount]++
                                    );
                                handles.Add((extraToken, extraHandle));
                            }
                        }
                    );
                handles.Add((listeners[i].token, handle));
            }

            SimpleBroadcastMessage msg = new();
            msg.EmitComponentBroadcast(listeners[0].comp);
            int total = 0;
            for (int i = 0; i < ManyCount; i++)
            {
                total += counts[i];
            }

            Assert.AreEqual(ManyCount, total);
            Assert.AreEqual(0, counts[ManyCount]);

            msg.EmitComponentBroadcast(listeners[0].comp);
            total = 0;
            for (int i = 0; i < ManyCount; i++)
            {
                total += counts[i];
            }

            Assert.AreEqual(ManyCount * 2, total);
            Assert.AreEqual(1, counts[ManyCount]);

            foreach (
                (MessageRegistrationToken token, MessageRegistrationHandle handle) entry in handles
            )
            {
                entry.token.RemoveRegistration(entry.handle);
            }
        }

        [Test]
        [Category("Stress")]
        public void TargetedWithoutTargetingRemoveOtherAcrossHandlersDuringEmissionMany()
        {
            List<(EmptyMessageAwareComponent comp, MessageRegistrationToken token)> listeners =
                new();
            for (int i = 0; i < ManyCount; i++)
            {
                GameObject go = new($"TWTBusRem_{i}", typeof(EmptyMessageAwareComponent));
                _spawned.Add(go);
                EmptyMessageAwareComponent c = go.GetComponent<EmptyMessageAwareComponent>();
                listeners.Add((c, GetToken(c)));
            }

            MessageRegistrationHandle[] handles = new MessageRegistrationHandle[ManyCount];
            int[] counts = new int[ManyCount];

            for (int i = 0; i < listeners.Count; i++)
            {
                int idx = i;
                handles[idx] = listeners[i]
                    .token.RegisterTargetedWithoutTargeting<SimpleTargetedMessage>(
                        (_, _) =>
                        {
                            counts[idx]++;
                            if (idx == 0)
                            {
                                listeners[1].token.RemoveRegistration(handles[1]);
                            }
                        }
                    );
            }

            GameObject target = new("TWT_Target_Rem");
            _spawned.Add(target);
            SimpleTargetedMessage msg = new();
            msg.EmitGameObjectTargeted(target);
            Assert.AreEqual(1, counts[0]);
            Assert.AreEqual(1, counts[1]);

            msg.EmitGameObjectTargeted(target);
            Assert.AreEqual(2, counts[0]);
            Assert.AreEqual(1, counts[1]);

            for (int i = 0; i < handles.Length; i++)
            {
                if (i == 1)
                {
                    continue;
                }
                listeners[i].token.RemoveRegistration(handles[i]);
            }
        }

        [Test]
        [Category("Stress")]
        public void BroadcastWithoutSourceRemoveOtherAcrossHandlersDuringEmissionMany()
        {
            List<(EmptyMessageAwareComponent comp, MessageRegistrationToken token)> listeners =
                new();
            for (int i = 0; i < ManyCount; i++)
            {
                GameObject go = new($"BWOBusRem_{i}", typeof(EmptyMessageAwareComponent));
                _spawned.Add(go);
                EmptyMessageAwareComponent c = go.GetComponent<EmptyMessageAwareComponent>();
                listeners.Add((c, GetToken(c)));
            }

            MessageRegistrationHandle[] handles = new MessageRegistrationHandle[ManyCount];
            int[] counts = new int[ManyCount];

            for (int i = 0; i < listeners.Count; i++)
            {
                int idx = i;
                handles[idx] = listeners[i]
                    .token.RegisterBroadcastWithoutSource<SimpleBroadcastMessage>(
                        (_, _) =>
                        {
                            counts[idx]++;
                            if (idx == 0)
                            {
                                listeners[1].token.RemoveRegistration(handles[1]);
                            }
                        }
                    );
            }

            // Emit from any component
            SimpleBroadcastMessage msg = new();
            msg.EmitComponentBroadcast(listeners[0].comp);
            Assert.AreEqual(1, counts[0]);
            Assert.AreEqual(1, counts[1]);

            msg.EmitComponentBroadcast(listeners[0].comp);
            Assert.AreEqual(2, counts[0]);
            Assert.AreEqual(1, counts[1]);

            for (int i = 0; i < handles.Length; i++)
            {
                if (i == 1)
                {
                    continue;
                }
                listeners[i].token.RemoveRegistration(handles[i]);
            }
        }

        [Test]
        [Category("Stress")]
        public void PostProcessorRemoveOtherDuringPostProcessingMany(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(PostProcessorRemoveOtherDuringPostProcessingMany) + "_" + scenario,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent comp = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(comp);
            InstanceId hostId = host;

            // Ensure processing stage reached
            _ = ScenarioCallbacks.RegisterCountingHandler(scenario, token, hostId, () => { });

            MessageRegistrationHandle[] pp = new MessageRegistrationHandle[ManyCount];
            int[] counts = new int[ManyCount];
            for (int i = 0; i < ManyCount; i++)
            {
                int idx = i;
                pp[idx] = ScenarioCallbacks.RegisterCountingPostProcessor(
                    scenario,
                    token,
                    hostId,
                    () =>
                    {
                        counts[idx]++;
                        if (idx == 0)
                        {
                            token.RemoveRegistration(pp[1]);
                        }
                    }
                );
            }

            ScenarioCallbacks.EmitForKind(scenario, hostId);
            Assert.AreEqual(1, counts[0]);
            Assert.AreEqual(1, counts[1]);

            ScenarioCallbacks.EmitForKind(scenario, hostId);
            Assert.AreEqual(2, counts[0]);
            Assert.AreEqual(1, counts[1]);

            for (int i = 0; i < pp.Length; i++)
            {
                if (i == 1)
                {
                    continue;
                }
                token.RemoveRegistration(pp[i]);
            }
        }

        [Test]
        [Category("Stress")]
        public void RemoveOtherLocalHandlerDuringEmissionMany(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(RemoveOtherLocalHandlerDuringEmissionMany) + "_" + scenario,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int[] counts = new int[ManyCount];
            MessageRegistrationHandle[] handles = new MessageRegistrationHandle[ManyCount];

            for (int i = 0; i < ManyCount; i++)
            {
                int idx = i;
                handles[idx] = ScenarioCallbacks.RegisterCountingHandler(
                    scenario,
                    token,
                    hostId,
                    () =>
                    {
                        counts[idx]++;
                        if (idx == 0)
                        {
                            token.RemoveRegistration(handles[1]);
                        }
                    }
                );
            }

            ScenarioCallbacks.EmitForKind(scenario, hostId);
            Assert.AreEqual(1, counts[0]);
            Assert.AreEqual(1, counts[1]);

            ScenarioCallbacks.EmitForKind(scenario, hostId);
            Assert.AreEqual(2, counts[0]);
            Assert.AreEqual(1, counts[1]);

            for (int i = 0; i < handles.Length; i++)
            {
                if (i == 1)
                {
                    continue;
                }
                token.RemoveRegistration(handles[i]);
            }
        }

        [Test]
        public void UntargetedAddSameDelegateDuringEmissionDoesNotDuplicateInvocation()
        {
            GameObject host = new("SameDelegateHost", typeof(EmptyMessageAwareComponent));
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);

            int count = 0;
            MessageRegistrationHandle firstHandle = default;
            MessageRegistrationHandle? secondHandle = null;

            firstHandle = token.RegisterUntargeted<SimpleUntargetedMessage>(Local);

            SimpleUntargetedMessage msg = new();
            msg.EmitUntargeted();
            Assert.AreEqual(1, count);

            msg.EmitUntargeted();
            Assert.AreEqual(2, count);

            token.RemoveRegistration(firstHandle);
            if (secondHandle.HasValue)
            {
                token.RemoveRegistration(secondHandle.Value);
            }
            return;

            void Local(SimpleUntargetedMessage _)
            {
                count++;
                if (secondHandle == null)
                {
                    secondHandle = token.RegisterUntargeted<SimpleUntargetedMessage>(Local);
                }
            }
        }

        [Test]
        public void UntargetedAddLowerPriorityDuringEmissionRespectsNextEmissionOrder()
        {
            GameObject host = new("PriorityHost", typeof(EmptyMessageAwareComponent));
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);

            List<int> order = new();
            MessageRegistrationHandle lowHandle = default;
            bool added = false;

            MessageRegistrationHandle highHandle =
                token.RegisterUntargeted<SimpleUntargetedMessage>(
                    _ =>
                    {
                        order.Add(1);
                        if (!added)
                        {
                            added = true;
                            lowHandle = token.RegisterUntargeted<SimpleUntargetedMessage>(
                                _ => order.Add(0),
                                priority: 0
                            );
                        }
                    },
                    priority: 1
                );

            SimpleUntargetedMessage msg = new();
            msg.EmitUntargeted();
            CollectionAssert.AreEqual(new[] { 1 }, order);

            order.Clear();
            msg.EmitUntargeted();
            CollectionAssert.AreEqual(new[] { 0, 1 }, order);

            token.RemoveRegistration(highHandle);
            if (lowHandle != default)
            {
                token.RemoveRegistration(lowHandle);
            }
        }
    }
}

#endif
