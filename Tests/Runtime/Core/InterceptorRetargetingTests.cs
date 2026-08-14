#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using UnityEngine;

    /// <summary>
    /// Pins the redirection semantics implied by the targeted/broadcast
    /// interceptor delegates (<c>ref InstanceId target</c> /
    /// <c>ref InstanceId source</c> on
    /// <see cref="DxMessaging.Core.MessageBus.IMessageBus.TargetedInterceptor{TMessage}"/> and
    /// <see cref="DxMessaging.Core.MessageBus.IMessageBus.BroadcastInterceptor{TMessage}"/>):
    /// when an interceptor rewrites the context id, dispatch must be routed
    /// end-to-end against the NEW id. Handlers, without-context sinks, and
    /// GlobalAcceptAll listeners all observe the rewritten id, and
    /// post-processors follow it as well: when interceptors rewrite the id,
    /// MessageBus.TargetedBroadcast / SourcedBroadcast re-resolve the
    /// post-process snapshot for the FINAL id (the pre-frozen snapshot is
    /// used only when the id is unchanged).
    /// </summary>
    public sealed class InterceptorRetargetingTests : MessagingTestBase
    {
        public enum PostProcessorDelegateShape
        {
            Fast,
            Action,
        }

        [Test]
        public void RewrittenContextRoutesHandlersToNewId(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.KindsWithComponentTarget)
            )]
                MessageScenario scenario
        )
        {
            GameObject originalHost = new(
                nameof(RewrittenContextRoutesHandlersToNewId) + "_Original_" + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(originalHost);
            GameObject redirectedHost = new(
                nameof(RewrittenContextRoutesHandlersToNewId) + "_Redirected_" + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(redirectedHost);

            MessageRegistrationToken token = GetToken(
                originalHost.GetComponent<EmptyMessageAwareComponent>()
            );
            InstanceId originalId = originalHost;
            InstanceId redirectedId = redirectedHost;

            int originalCount = 0;
            int redirectedCount = 0;
            List<MessageRegistrationHandle> handles = new();

            using (LeakWatcher watcher = LeakWatcher.Watch(label: scenario.DisplayName))
            {
                handles.Add(
                    RegisterRewritingInterceptor(scenario, token, originalId, redirectedId)
                );
                handles.Add(
                    RegisterCountingHandler(scenario, token, originalId, () => originalCount++)
                );
                handles.Add(
                    RegisterCountingHandler(scenario, token, redirectedId, () => redirectedCount++)
                );

                EmitForScenario(scenario, originalId);
                RemoveAll(token, handles);
            }

            Assert.AreEqual(
                1,
                redirectedCount,
                "[{0}] Handler registered for the rewritten id must receive the redirected dispatch exactly once. redirectedCount={1}, originalCount={2}.",
                scenario.Kind,
                redirectedCount,
                originalCount
            );
            Assert.AreEqual(
                0,
                originalCount,
                "[{0}] Handler registered for the original id must NOT run once the interceptor redirects. redirectedCount={1}, originalCount={2}.",
                scenario.Kind,
                redirectedCount,
                originalCount
            );
        }

        [Test]
        public void RewrittenContextIsObservedByWithoutContextSinksAndGlobals(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.KindsWithComponentTarget)
            )]
                MessageScenario scenario
        )
        {
            GameObject originalHost = new(
                nameof(RewrittenContextIsObservedByWithoutContextSinksAndGlobals)
                    + "_Original_"
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(originalHost);
            GameObject redirectedHost = new(
                nameof(RewrittenContextIsObservedByWithoutContextSinksAndGlobals)
                    + "_Redirected_"
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(redirectedHost);

            MessageRegistrationToken token = GetToken(
                originalHost.GetComponent<EmptyMessageAwareComponent>()
            );
            InstanceId originalId = originalHost;
            InstanceId redirectedId = redirectedHost;

            List<InstanceId> observedByWithoutContext = new();
            List<InstanceId> observedByGlobal = new();
            List<MessageRegistrationHandle> handles = new();

            using (LeakWatcher watcher = LeakWatcher.Watch(label: scenario.DisplayName))
            {
                handles.Add(
                    RegisterRewritingInterceptor(scenario, token, originalId, redirectedId)
                );
                handles.Add(
                    RegisterWithoutContextRecorder(scenario, token, observedByWithoutContext.Add)
                );
                handles.Add(RegisterGlobalContextRecorder(token, observedByGlobal.Add));

                EmitForScenario(scenario, originalId);
                RemoveAll(token, handles);
            }

            Assert.AreEqual(
                1,
                observedByWithoutContext.Count,
                "[{0}] Without-context sink must observe exactly one dispatch. count={1}.",
                scenario.Kind,
                observedByWithoutContext.Count
            );
            Assert.AreEqual(
                redirectedId,
                observedByWithoutContext[0],
                "[{0}] Without-context sink must observe the rewritten id {1}, not the original {2}; saw {3}.",
                scenario.Kind,
                redirectedId,
                originalId,
                observedByWithoutContext[0]
            );
            Assert.AreEqual(
                1,
                observedByGlobal.Count,
                "[{0}] GlobalAcceptAll must observe exactly one dispatch. count={1}.",
                scenario.Kind,
                observedByGlobal.Count
            );
            Assert.AreEqual(
                redirectedId,
                observedByGlobal[0],
                "[{0}] GlobalAcceptAll runs after interceptors and must observe the rewritten id {1}, not the original {2}; saw {3}.",
                scenario.Kind,
                redirectedId,
                originalId,
                observedByGlobal[0]
            );
        }

        /// <summary>
        /// Coherent redirection semantics: a post-processor registered for the
        /// REWRITTEN id must run, exactly as a handler for the rewritten id
        /// does. Both paths honor this: MessageBus.TargetedBroadcast and
        /// MessageBus.SourcedBroadcast re-resolve the post-process snapshot
        /// for the rewritten id after interceptors run (the pre-frozen,
        /// original-id snapshot is preferred only when the id is unchanged).
        /// </summary>
        [Test]
        public void RewrittenContextRoutesPostProcessorsToNewId(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.KindsWithComponentTarget)
            )]
                MessageScenario scenario
        )
        {
            GameObject originalHost = new(
                nameof(RewrittenContextRoutesPostProcessorsToNewId) + "_Original_" + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(originalHost);
            GameObject redirectedHost = new(
                nameof(RewrittenContextRoutesPostProcessorsToNewId)
                    + "_Redirected_"
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(redirectedHost);

            MessageRegistrationToken token = GetToken(
                originalHost.GetComponent<EmptyMessageAwareComponent>()
            );
            InstanceId originalId = originalHost;
            InstanceId redirectedId = redirectedHost;

            int redirectedPostCount = 0;
            List<MessageRegistrationHandle> handles = new();

            using (LeakWatcher watcher = LeakWatcher.Watch(label: scenario.DisplayName))
            {
                handles.Add(
                    RegisterRewritingInterceptor(scenario, token, originalId, redirectedId)
                );
                handles.Add(
                    RegisterCountingPostProcessor(
                        scenario,
                        token,
                        redirectedId,
                        () => redirectedPostCount++
                    )
                );

                EmitForScenario(scenario, originalId);
                RemoveAll(token, handles);
            }

            Assert.AreEqual(
                1,
                redirectedPostCount,
                "[{0}] Post-processor registered for the rewritten id must run exactly once when the interceptor redirects to it. redirectedPostCount={1}.",
                scenario.Kind,
                redirectedPostCount
            );
        }

        /// <summary>
        /// Coherent redirection semantics: once the interceptor redirects away
        /// from the original id, post-processors registered for the ORIGINAL id
        /// must not observe the message, mirroring handler routing.
        /// </summary>
        [Test]
        public void RewrittenContextSkipsPostProcessorsForOriginalId(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.KindsWithComponentTarget)
            )]
                MessageScenario scenario
        )
        {
            GameObject originalHost = new(
                nameof(RewrittenContextSkipsPostProcessorsForOriginalId)
                    + "_Original_"
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(originalHost);
            GameObject redirectedHost = new(
                nameof(RewrittenContextSkipsPostProcessorsForOriginalId)
                    + "_Redirected_"
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(redirectedHost);

            MessageRegistrationToken token = GetToken(
                originalHost.GetComponent<EmptyMessageAwareComponent>()
            );
            InstanceId originalId = originalHost;
            InstanceId redirectedId = redirectedHost;

            int originalPostCount = 0;
            List<MessageRegistrationHandle> handles = new();

            using (LeakWatcher watcher = LeakWatcher.Watch(label: scenario.DisplayName))
            {
                handles.Add(
                    RegisterRewritingInterceptor(scenario, token, originalId, redirectedId)
                );
                handles.Add(
                    RegisterCountingPostProcessor(
                        scenario,
                        token,
                        originalId,
                        () => originalPostCount++
                    )
                );

                EmitForScenario(scenario, originalId);
                RemoveAll(token, handles);
            }

            Assert.AreEqual(
                0,
                originalPostCount,
                "[{0}] Post-processor registered for the original id must NOT run once the interceptor redirects away from it. originalPostCount={1}.",
                scenario.Kind,
                originalPostCount
            );
        }

        /// <summary>
        /// Coherent redirection semantics with post-processors for BOTH ids
        /// living on DISTINCT components: only the rewritten id's
        /// post-processor observes the message. Pins the re-resolution in
        /// MessageBus.TargetedBroadcast / SourcedBroadcast: when interceptors
        /// rewrite the id, the post-process snapshot is re-acquired for the
        /// FINAL id instead of preferring the stale pre-interceptor snapshot.
        /// </summary>
        [Test]
        public void RewrittenContextPostProcessorsOnDistinctComponentsFollowNewId(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.KindsWithComponentTarget)
            )]
                MessageScenario scenario
        )
        {
            GameObject originalHost = new(
                nameof(RewrittenContextPostProcessorsOnDistinctComponentsFollowNewId)
                    + "_Original_"
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(originalHost);
            GameObject redirectedHost = new(
                nameof(RewrittenContextPostProcessorsOnDistinctComponentsFollowNewId)
                    + "_Redirected_"
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(redirectedHost);

            MessageRegistrationToken originalToken = GetToken(
                originalHost.GetComponent<EmptyMessageAwareComponent>()
            );
            MessageRegistrationToken redirectedToken = GetToken(
                redirectedHost.GetComponent<EmptyMessageAwareComponent>()
            );
            InstanceId originalId = originalHost;
            InstanceId redirectedId = redirectedHost;

            int originalPostCount = 0;
            int redirectedPostCount = 0;
            List<MessageRegistrationHandle> originalHandles = new();
            List<MessageRegistrationHandle> redirectedHandles = new();

            using (LeakWatcher watcher = LeakWatcher.Watch(label: scenario.DisplayName))
            {
                originalHandles.Add(
                    RegisterRewritingInterceptor(scenario, originalToken, originalId, redirectedId)
                );
                originalHandles.Add(
                    RegisterCountingPostProcessor(
                        scenario,
                        originalToken,
                        originalId,
                        () => originalPostCount++
                    )
                );
                redirectedHandles.Add(
                    RegisterCountingPostProcessor(
                        scenario,
                        redirectedToken,
                        redirectedId,
                        () => redirectedPostCount++
                    )
                );

                EmitForScenario(scenario, originalId);
                RemoveAll(originalToken, originalHandles);
                RemoveAll(redirectedToken, redirectedHandles);
            }

            Assert.AreEqual(
                1,
                redirectedPostCount,
                "[{0}] Post-processor for the rewritten id must run exactly once after redirection. redirectedPostCount={1}, originalPostCount={2}.",
                scenario.Kind,
                redirectedPostCount,
                originalPostCount
            );
            Assert.AreEqual(
                0,
                originalPostCount,
                "[{0}] Post-processor for the original id must NOT run after redirection. redirectedPostCount={1}, originalPostCount={2}.",
                scenario.Kind,
                redirectedPostCount,
                originalPostCount
            );
        }

        [Test]
        public void PostProcessorAddedForRewrittenContextWaitsUntilNextEmission(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.KindsWithComponentTarget)
            )]
                MessageScenario scenario,
            [Values] PostProcessorDelegateShape delegateShape,
            [Values] bool hasExistingProcessor
        )
        {
            GameObject originalHost = new(
                nameof(PostProcessorAddedForRewrittenContextWaitsUntilNextEmission)
                    + "_Original_"
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(originalHost);
            GameObject redirectedHost = new(
                nameof(PostProcessorAddedForRewrittenContextWaitsUntilNextEmission)
                    + "_Redirected_"
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(redirectedHost);

            MessageRegistrationToken token = GetToken(
                originalHost.GetComponent<EmptyMessageAwareComponent>()
            );
            InstanceId originalId = originalHost;
            InstanceId redirectedId = redirectedHost;
            int existingPostCount = 0;
            int latePostCount = 0;
            bool added = false;
            List<MessageRegistrationHandle> handles = new();
            int firstExistingPostCount = 0;
            int firstLatePostCount = 0;
            int historyCountAfterFirstEmission = -1;

            using (
                LeakWatcher watcher = LeakWatcher.Watch(
                    label: scenario.Kind + "/" + delegateShape + "/existing=" + hasExistingProcessor
                )
            )
            {
                if (hasExistingProcessor)
                {
                    handles.Add(
                        RegisterCountingPostProcessor(
                            scenario,
                            token,
                            redirectedId,
                            () => ++existingPostCount,
                            delegateShape
                        )
                    );
                }
                handles.Add(
                    RegisterRewritingInterceptor(
                        scenario,
                        token,
                        originalId,
                        redirectedId,
                        () =>
                        {
                            if (!added)
                            {
                                added = true;
                                handles.Add(
                                    RegisterCountingPostProcessor(
                                        scenario,
                                        token,
                                        redirectedId,
                                        () => ++latePostCount,
                                        delegateShape
                                    )
                                );
                            }
                        }
                    )
                );

                EmitForScenario(scenario, originalId);
                firstExistingPostCount = existingPostCount;
                firstLatePostCount = latePostCount;
                historyCountAfterFirstEmission = GetPostRouteSnapshotHistoryCount();

                EmitForScenario(scenario, originalId);
                RemoveAll(token, handles);

                Assert.AreEqual(
                    hasExistingProcessor ? 1 : 0,
                    firstExistingPostCount,
                    "[{0}] A processor already registered for the rewritten context must run "
                        + "on the first emission. firstExistingPostCount={1}, "
                        + "firstLatePostCount={2}.",
                    scenario.Kind + "/" + delegateShape + "/existing=" + hasExistingProcessor,
                    firstExistingPostCount,
                    firstLatePostCount
                );
                Assert.AreEqual(
                    0,
                    firstLatePostCount,
                    "[{0}] A processor registered during the interceptor must wait until the "
                        + "next emission. firstExistingPostCount={1}, "
                        + "firstLatePostCount={2}.",
                    scenario.Kind + "/" + delegateShape + "/existing=" + hasExistingProcessor,
                    firstExistingPostCount,
                    firstLatePostCount
                );
                Assert.AreEqual(
                    hasExistingProcessor ? 2 : 0,
                    existingPostCount,
                    "[{0}] The existing processor must run once per emission. "
                        + "existingPostCount={1}, latePostCount={2}.",
                    scenario.Kind + "/" + delegateShape + "/existing=" + hasExistingProcessor,
                    existingPostCount,
                    latePostCount
                );
                Assert.AreEqual(
                    1,
                    latePostCount,
                    "[{0}] The late processor must start on the next emission. "
                        + "existingPostCount={1}, latePostCount={2}.",
                    scenario.Kind + "/" + delegateShape + "/existing=" + hasExistingProcessor,
                    existingPostCount,
                    latePostCount
                );
            }

            Assert.AreEqual(
                0,
                historyCountAfterFirstEmission,
                "[{0}] The outermost dispatch lease must release and clear every pre-mutation "
                    + "route snapshot. Retaining entries would keep handlers alive between "
                    + "emissions.",
                scenario.Kind + "/" + delegateShape + "/existing=" + hasExistingProcessor
            );
        }

        [Test]
        public void PostProcessorRemovedForRewrittenContextFinishesCurrentEmission(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.KindsWithComponentTarget)
            )]
                MessageScenario scenario,
            [Values] PostProcessorDelegateShape delegateShape
        )
        {
            GameObject originalHost = new(
                nameof(PostProcessorRemovedForRewrittenContextFinishesCurrentEmission)
                    + "_Original_"
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(originalHost);
            GameObject redirectedHost = new(
                nameof(PostProcessorRemovedForRewrittenContextFinishesCurrentEmission)
                    + "_Redirected_"
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(redirectedHost);

            MessageRegistrationToken token = GetToken(
                originalHost.GetComponent<EmptyMessageAwareComponent>()
            );
            InstanceId originalId = originalHost;
            InstanceId redirectedId = redirectedHost;
            int postCount = 0;
            bool removed = false;
            List<MessageRegistrationHandle> handles = new();

            using (
                LeakWatcher watcher = LeakWatcher.Watch(label: scenario.Kind + "/" + delegateShape)
            )
            {
                MessageRegistrationHandle postHandle = RegisterCountingPostProcessor(
                    scenario,
                    token,
                    redirectedId,
                    () => ++postCount,
                    delegateShape
                );
                handles.Add(postHandle);
                handles.Add(
                    RegisterRewritingInterceptor(
                        scenario,
                        token,
                        originalId,
                        redirectedId,
                        () =>
                        {
                            if (removed)
                            {
                                return;
                            }

                            removed = true;
                            token.RemoveRegistration(postHandle);
                            _ = handles.Remove(postHandle);
                        }
                    )
                );

                EmitForScenario(scenario, originalId);
                int firstEmissionCount = postCount;
                EmitForScenario(scenario, originalId);
                RemoveAll(token, handles);

                Assert.AreEqual(
                    1,
                    firstEmissionCount,
                    "[{0}, {1}] A final-route processor removed by the rewriting interceptor "
                        + "must finish the frozen emission. firstEmissionCount={2}, totalCount={3}.",
                    scenario.Kind,
                    delegateShape,
                    firstEmissionCount,
                    postCount
                );
                Assert.AreEqual(
                    1,
                    postCount,
                    "[{0}, {1}] The removed final-route processor must disappear on the next "
                        + "emission. firstEmissionCount={2}, totalCount={3}.",
                    scenario.Kind,
                    delegateShape,
                    firstEmissionCount,
                    postCount
                );
            }
        }

        [Test]
        public void NestedRewriteUsesEachEmissionsDestinationSnapshot(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.KindsWithComponentTarget)
            )]
                MessageScenario scenario
        )
        {
            GameObject originalHost = new(
                nameof(NestedRewriteUsesEachEmissionsDestinationSnapshot)
                    + "_Original_"
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(originalHost);
            GameObject redirectedHost = new(
                nameof(NestedRewriteUsesEachEmissionsDestinationSnapshot)
                    + "_Redirected_"
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(redirectedHost);

            MessageRegistrationToken token = GetToken(
                originalHost.GetComponent<EmptyMessageAwareComponent>()
            );
            InstanceId originalId = originalHost;
            InstanceId redirectedId = redirectedHost;
            int postCount = 0;
            bool inNestedEmission = false;
            MessageRegistrationHandle nestedPostHandle = default;
            List<MessageRegistrationHandle> handles = new();

            using (LeakWatcher watcher = LeakWatcher.Watch(label: scenario.DisplayName))
            {
                handles.Add(
                    RegisterRewritingInterceptor(
                        scenario,
                        token,
                        originalId,
                        redirectedId,
                        () =>
                        {
                            if (!inNestedEmission)
                            {
                                nestedPostHandle = RegisterCountingPostProcessor(
                                    scenario,
                                    token,
                                    redirectedId,
                                    () => ++postCount
                                );
                                handles.Add(nestedPostHandle);
                                inNestedEmission = true;
                                EmitForScenario(scenario, originalId);
                                inNestedEmission = false;
                                return;
                            }

                            token.RemoveRegistration(nestedPostHandle);
                            _ = handles.Remove(nestedPostHandle);
                        }
                    )
                );

                EmitForScenario(scenario, originalId);
                RemoveAll(token, handles);

                Assert.AreEqual(
                    1,
                    postCount,
                    "[{0}] The nested emission starts after registration and must see the final-"
                        + "route processor once; the outer emission started before registration "
                        + "and must not see it. postCount={1}.",
                    scenario.Kind,
                    postCount
                );
            }
        }

        [Test]
        public void RepeatedRouteMutationsRetainOneSnapshotPerRouteAndDropOversizedHistory(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.KindsWithComponentTarget)
            )]
                MessageScenario scenario
        )
        {
            GameObject originalHost = new(
                nameof(RepeatedRouteMutationsRetainOneSnapshotPerRouteAndDropOversizedHistory)
                    + "_Original_"
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(originalHost);
            GameObject redirectedHost = new(
                nameof(RepeatedRouteMutationsRetainOneSnapshotPerRouteAndDropOversizedHistory)
                    + "_Redirected_"
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(redirectedHost);

            MessageRegistrationToken token = GetToken(
                originalHost.GetComponent<EmptyMessageAwareComponent>()
            );
            InstanceId originalId = originalHost;
            InstanceId redirectedId = redirectedHost;
            List<MessageRegistrationHandle> handles = new();
            using IDisposable poolCapScope = new ContextMapPoolCapScope(16);
            int maxRetained = DxMessaging
                .Core.MessageBus.MessageBus.ObserveContextMapPoolForBenchmark()
                .MaxRetained;
            int distinctRouteCount = checked(maxRetained + 1);
            const int sameRouteMutationCount = 64;
            int historyCountDuringMutation = -1;
            bool historyRetainedAfterEmission = true;

            using (LeakWatcher watcher = LeakWatcher.Watch(label: scenario.DisplayName))
            {
                handles.Add(
                    RegisterRewritingInterceptor(
                        scenario,
                        token,
                        originalId,
                        redirectedId,
                        () =>
                        {
                            for (int i = 0; i < sameRouteMutationCount; ++i)
                            {
                                handles.Add(
                                    RegisterCountingPostProcessor(
                                        scenario,
                                        token,
                                        redirectedId,
                                        () => { }
                                    )
                                );
                            }

                            for (int i = 0; i < distinctRouteCount; ++i)
                            {
                                handles.Add(
                                    RegisterCountingPostProcessor(
                                        scenario,
                                        token,
                                        new InstanceId(0x6000_0000 + i),
                                        () => { }
                                    )
                                );
                            }

                            historyCountDuringMutation = GetPostRouteSnapshotHistoryCount();
                        }
                    )
                );

                EmitForScenario(scenario, originalId);
                historyRetainedAfterEmission = GetPostRouteSnapshotHistory() != null;
                RemoveAll(token, handles);
            }

            Assert.AreEqual(
                distinctRouteCount + 1,
                historyCountDuringMutation,
                "[{0}] Repeated mutations of one route need one pre-mutation snapshot; each "
                    + "distinct route needs one. sameRouteMutations={1}, distinctRoutes={2}.",
                scenario.Kind,
                sameRouteMutationCount,
                distinctRouteCount
            );
            Assert.IsFalse(
                historyRetainedAfterEmission,
                "[{0}] A history whose capacity crossed the configured retention cap must be "
                    + "dropped when the outermost lease exits. maxRetained={1}.",
                scenario.Kind,
                maxRetained
            );
        }

        [Test]
        public void SequentialNestedSiblingMutationsReleaseSupersededRouteSnapshots(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.KindsWithComponentTarget)
            )]
                MessageScenario scenario
        )
        {
            GameObject originalHost = new(
                nameof(SequentialNestedSiblingMutationsReleaseSupersededRouteSnapshots)
                    + "_Original_"
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(originalHost);
            GameObject redirectedHost = new(
                nameof(SequentialNestedSiblingMutationsReleaseSupersededRouteSnapshots)
                    + "_Redirected_"
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(redirectedHost);

            MessageRegistrationToken token = GetToken(
                originalHost.GetComponent<EmptyMessageAwareComponent>()
            );
            InstanceId originalId = originalHost;
            InstanceId redirectedId = redirectedHost;
            List<MessageRegistrationHandle> handles = new();
            const int siblingCount = 32;
            bool emittingNestedSibling = false;
            int maximumHistoryCount = 0;
            int maximumRetainedEntryCount = 0;
            bool supersededSnapshotsReleased = true;
            bool allSnapshotsReleasedAfterEmission = false;
            bool compactionIndexRetainedBeforeForceTrim = false;
            bool historyRetainedBeforeForceTrim = false;
            bool compactionIndexReused = true;
            object firstCompactionIndex = null;
            List<object> capturedSnapshots = new();

            using (LeakWatcher watcher = LeakWatcher.Watch(label: scenario.DisplayName))
            {
                handles.Add(
                    RegisterRewritingInterceptor(
                        scenario,
                        token,
                        originalId,
                        redirectedId,
                        () =>
                        {
                            if (emittingNestedSibling)
                            {
                                handles.Add(
                                    RegisterCountingPostProcessor(
                                        scenario,
                                        token,
                                        redirectedId,
                                        () => { }
                                    )
                                );
                                maximumHistoryCount = Math.Max(
                                    maximumHistoryCount,
                                    GetPostRouteSnapshotHistoryCount()
                                );
                                maximumRetainedEntryCount = Math.Max(
                                    maximumRetainedEntryCount,
                                    GetPostRouteSnapshotHistoryResolvedEntryCount()
                                );
                                capturedSnapshots.Add(GetLatestPostRouteSnapshot());
                                return;
                            }

                            for (int i = 0; i < siblingCount; ++i)
                            {
                                emittingNestedSibling = true;
                                try
                                {
                                    EmitForScenario(scenario, originalId);
                                }
                                finally
                                {
                                    emittingNestedSibling = false;
                                }

                                if (
                                    i > 0
                                    && (
                                        capturedSnapshots.Count <= i
                                        || !IsDispatchSnapshotReleased(capturedSnapshots[i])
                                    )
                                )
                                {
                                    supersededSnapshotsReleased = false;
                                }

                                object currentCompactionIndex = GetPostRouteCompactionIndex();
                                if (i == 0)
                                {
                                    firstCompactionIndex = currentCompactionIndex;
                                }
                                else if (
                                    !ReferenceEquals(firstCompactionIndex, currentCompactionIndex)
                                )
                                {
                                    compactionIndexReused = false;
                                }
                            }
                        }
                    )
                );

                try
                {
                    EmitForScenario(scenario, originalId);
                    allSnapshotsReleasedAfterEmission = capturedSnapshots.TrueForAll(
                        IsDispatchSnapshotReleased
                    );
                    compactionIndexRetainedBeforeForceTrim = GetPostRouteCompactionIndex() != null;
                    historyRetainedBeforeForceTrim = GetPostRouteSnapshotHistory() != null;
                }
                finally
                {
                    RemoveAll(token, handles);
                }
            }

            _ = MessageHandler.MessageBus.Trim(force: true);
            bool compactionIndexRetainedAfterForceTrim = GetPostRouteCompactionIndex() != null;
            bool historyRetainedAfterForceTrim = GetPostRouteSnapshotHistory() != null;

            Assert.LessOrEqual(
                maximumHistoryCount,
                2,
                "[{0}] Each nested sibling may add one working snapshot beside the parent's "
                    + "earliest snapshot, but completed siblings must not accumulate. siblings={1}.",
                scenario.Kind,
                siblingCount
            );
            Assert.LessOrEqual(
                maximumRetainedEntryCount,
                siblingCount,
                "[{0}] Sequential siblings must retain O(n), not triangular O(n^2), resolved "
                    + "route entries. siblings={1}.",
                scenario.Kind,
                siblingCount
            );
            Assert.AreEqual(
                siblingCount,
                capturedSnapshots.Count,
                "[{0}] Every nested sibling must capture one pre-mutation route snapshot.",
                scenario.Kind
            );
            Assert.IsTrue(
                supersededSnapshotsReleased,
                "[{0}] Each completed sibling after the first must release its superseded flat "
                    + "snapshot immediately.",
                scenario.Kind
            );
            Assert.IsTrue(
                allSnapshotsReleasedAfterEmission,
                "[{0}] The outermost lease must release the first snapshot promoted to its frame.",
                scenario.Kind
            );
            Assert.IsTrue(
                compactionIndexReused,
                "[{0}] Sequential siblings must reuse the same bus-owned compaction index.",
                scenario.Kind
            );
            Assert.IsTrue(
                compactionIndexRetainedBeforeForceTrim,
                "[{0}] A small compaction index should remain available for reuse until trimmed.",
                scenario.Kind
            );
            Assert.IsTrue(
                historyRetainedBeforeForceTrim,
                "[{0}] A small cleared route-history list should remain available until trimmed.",
                scenario.Kind
            );
            Assert.IsFalse(
                compactionIndexRetainedAfterForceTrim,
                "[{0}] Force trim must reclaim the bus-owned compaction index.",
                scenario.Kind
            );
            Assert.IsFalse(
                historyRetainedAfterForceTrim,
                "[{0}] Force trim must reclaim the cleared route-history list.",
                scenario.Kind
            );
        }

        [Test]
        public void ManyDistinctNestedRoutesUseBoundedCompactionIndex(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.KindsWithComponentTarget)
            )]
                MessageScenario scenario
        )
        {
            GameObject originalHost = new(
                nameof(ManyDistinctNestedRoutesUseBoundedCompactionIndex)
                    + "_Original_"
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(originalHost);
            GameObject redirectedHost = new(
                nameof(ManyDistinctNestedRoutesUseBoundedCompactionIndex)
                    + "_Redirected_"
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(redirectedHost);

            MessageRegistrationToken token = GetToken(
                originalHost.GetComponent<EmptyMessageAwareComponent>()
            );
            InstanceId originalId = originalHost;
            InstanceId redirectedId = redirectedHost;
            List<MessageRegistrationHandle> handles = new();
            using IDisposable poolCapScope = new ContextMapPoolCapScope(16);
            int maxRetained = DxMessaging
                .Core.MessageBus.MessageBus.ObserveContextMapPoolForBenchmark()
                .MaxRetained;
            int distinctRouteCount = checked(maxRetained + 1);
            bool emittingNested = false;
            int historyCountAfterNested = -1;
            bool compactionIndexRetainedAfterNested = false;
            bool compactionIndexReused = true;
            bool compactionIndexRetainedAfterEmission = true;
            bool historyRetainedAfterEmission = true;
            object firstCompactionIndex = null;

            using (LeakWatcher watcher = LeakWatcher.Watch(label: scenario.DisplayName))
            {
                handles.Add(
                    RegisterRewritingInterceptor(
                        scenario,
                        token,
                        originalId,
                        redirectedId,
                        () =>
                        {
                            if (emittingNested)
                            {
                                for (int i = 0; i < distinctRouteCount; ++i)
                                {
                                    handles.Add(
                                        RegisterCountingPostProcessor(
                                            scenario,
                                            token,
                                            new InstanceId(0x7000_0000 + i),
                                            () => { }
                                        )
                                    );
                                }

                                return;
                            }

                            for (int sibling = 0; sibling < 2; ++sibling)
                            {
                                emittingNested = true;
                                try
                                {
                                    EmitForScenario(scenario, originalId);
                                }
                                finally
                                {
                                    emittingNested = false;
                                }

                                object currentCompactionIndex = GetPostRouteCompactionIndex();
                                if (sibling == 0)
                                {
                                    firstCompactionIndex = currentCompactionIndex;
                                }
                                else if (
                                    !ReferenceEquals(firstCompactionIndex, currentCompactionIndex)
                                )
                                {
                                    compactionIndexReused = false;
                                }
                            }

                            historyCountAfterNested = GetPostRouteSnapshotHistoryCount();
                            compactionIndexRetainedAfterNested =
                                GetPostRouteCompactionIndex() != null;
                        }
                    )
                );

                try
                {
                    EmitForScenario(scenario, originalId);
                    compactionIndexRetainedAfterEmission = GetPostRouteCompactionIndex() != null;
                    historyRetainedAfterEmission = GetPostRouteSnapshotHistory() != null;
                }
                finally
                {
                    RemoveAll(token, handles);
                }
            }

            Assert.AreEqual(
                distinctRouteCount,
                historyCountAfterNested,
                "[{0}] A nested child must promote the earliest snapshot for every distinct "
                    + "route to its parent. distinctRoutes={1}.",
                scenario.Kind,
                distinctRouteCount
            );
            Assert.IsTrue(
                compactionIndexRetainedAfterNested,
                "[{0}] An oversized compaction index must remain reusable while its outer "
                    + "dispatch is active. maxRetained={1}.",
                scenario.Kind,
                maxRetained
            );
            Assert.IsTrue(
                compactionIndexReused,
                "[{0}] Oversized sequential siblings must reuse one compaction index.",
                scenario.Kind
            );
            Assert.IsFalse(
                compactionIndexRetainedAfterEmission,
                "[{0}] The outermost lease must drop an oversized compaction index.",
                scenario.Kind
            );
            Assert.IsFalse(
                historyRetainedAfterEmission,
                "[{0}] The outermost lease must drop an oversized history list.",
                scenario.Kind
            );
        }

        [Test]
        public void ExceptionalRewrittenRouteMutationReleasesSnapshotHistory(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.KindsWithComponentTarget)
            )]
                MessageScenario scenario
        )
        {
            GameObject originalHost = new(
                nameof(ExceptionalRewrittenRouteMutationReleasesSnapshotHistory)
                    + "_Original_"
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(originalHost);
            GameObject redirectedHost = new(
                nameof(ExceptionalRewrittenRouteMutationReleasesSnapshotHistory)
                    + "_Redirected_"
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(redirectedHost);

            MessageRegistrationToken token = GetToken(
                originalHost.GetComponent<EmptyMessageAwareComponent>()
            );
            InstanceId originalId = originalHost;
            InstanceId redirectedId = redirectedHost;
            List<MessageRegistrationHandle> handles = new();
            int historyCountAfterException = -1;
            Exception thrown = null;

            using (LeakWatcher watcher = LeakWatcher.Watch(label: scenario.DisplayName))
            {
                handles.Add(
                    RegisterRewritingInterceptor(
                        scenario,
                        token,
                        originalId,
                        redirectedId,
                        () =>
                        {
                            handles.Add(
                                RegisterCountingPostProcessor(
                                    scenario,
                                    token,
                                    redirectedId,
                                    () => { }
                                )
                            );
                            throw new InvalidOperationException("expected route mutation failure");
                        }
                    )
                );

                try
                {
                    EmitForScenario(scenario, originalId);
                }
                catch (Exception exception)
                {
                    thrown = exception;
                }
                finally
                {
                    historyCountAfterException = GetPostRouteSnapshotHistoryCount();
                    RemoveAll(token, handles);
                }
            }

            Assert.IsInstanceOf<InvalidOperationException>(
                thrown,
                "[{0}] The interceptor must propagate its expected exception after mutating the "
                    + "rewritten route.",
                scenario.Kind
            );
            Assert.AreEqual(
                "expected route mutation failure",
                thrown.Message,
                "[{0}] The propagated exception must be the interceptor's sentinel.",
                scenario.Kind
            );
            Assert.AreEqual(
                0,
                historyCountAfterException,
                "[{0}] DispatchLease.Dispose must clear the route history while unwinding an "
                    + "exceptional interceptor.",
                scenario.Kind
            );
        }

        [Test]
        public void PostResetNestedEmissionDoesNotReuseStaleOuterCaptureIdentity(
            [ValueSource(
                typeof(MessageScenarios),
                nameof(MessageScenarios.KindsWithComponentTarget)
            )]
                MessageScenario scenario
        )
        {
            GameObject originalHost = new(
                nameof(PostResetNestedEmissionDoesNotReuseStaleOuterCaptureIdentity)
                    + "_Original_"
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(originalHost);
            GameObject redirectedHost = new(
                nameof(PostResetNestedEmissionDoesNotReuseStaleOuterCaptureIdentity)
                    + "_Redirected_"
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(redirectedHost);

            MessageRegistrationToken originalToken = GetToken(
                originalHost.GetComponent<EmptyMessageAwareComponent>()
            );
            InstanceId originalId = originalHost;
            InstanceId redirectedId = redirectedHost;
            MessageRegistrationToken postResetToken = null;
            int stage = 0;
            int existingPostCount = 0;
            int latePostCount = 0;
            bool latePostAdded = false;

            _ = RegisterRewritingInterceptor(
                scenario,
                originalToken,
                originalId,
                redirectedId,
                () =>
                {
                    if (stage == 1)
                    {
                        DxMessagingStaticState.Reset();
                        return;
                    }

                    if (stage != 0)
                    {
                        return;
                    }

                    stage = 1;
                    EmitForScenario(scenario, originalId);

                    GameObject postResetHost = new(
                        nameof(PostResetNestedEmissionDoesNotReuseStaleOuterCaptureIdentity)
                            + "_PostReset_"
                            + scenario.Kind,
                        typeof(EmptyMessageAwareComponent)
                    );
                    _spawned.Add(postResetHost);
                    postResetToken = GetToken(
                        postResetHost.GetComponent<EmptyMessageAwareComponent>()
                    );
                    _ = RegisterCountingPostProcessor(
                        scenario,
                        postResetToken,
                        redirectedId,
                        () => ++existingPostCount
                    );
                    _ = RegisterRewritingInterceptor(
                        scenario,
                        postResetToken,
                        originalId,
                        redirectedId,
                        () =>
                        {
                            if (latePostAdded)
                            {
                                return;
                            }

                            latePostAdded = true;
                            _ = RegisterCountingPostProcessor(
                                scenario,
                                postResetToken,
                                redirectedId,
                                () => ++latePostCount
                            );
                        }
                    );

                    stage = 2;
                    EmitForScenario(scenario, originalId);
                    stage = 3;
                }
            );

            Assert.DoesNotThrow(() => EmitForScenario(scenario, originalId));
            postResetToken?.UnregisterAll();

            Assert.AreEqual(
                1,
                existingPostCount,
                "[{0}] The valid post-reset nested emission must dispatch the processor that "
                    + "existed when it began.",
                scenario.Kind
            );
            Assert.AreEqual(
                0,
                latePostCount,
                "[{0}] The first processor added by the valid post-reset nested emission must "
                    + "wait. A stale outer capture identity must not deduplicate its snapshot.",
                scenario.Kind
            );
        }

        private static void RemoveAll(
            MessageRegistrationToken token,
            List<MessageRegistrationHandle> handles
        )
        {
            foreach (MessageRegistrationHandle handle in handles)
            {
                token.RemoveRegistration(handle);
            }

            handles.Clear();
        }

        private static int GetPostRouteSnapshotHistoryCount()
        {
            return GetPostRouteSnapshotHistory()?.Count ?? 0;
        }

        private static int GetPostRouteSnapshotHistoryResolvedEntryCount()
        {
            System.Collections.ICollection history = GetPostRouteSnapshotHistory();
            if (history == null)
            {
                return 0;
            }

            int entryCount = 0;
            foreach (object historyEntry in history)
            {
                System.Reflection.FieldInfo snapshotField = historyEntry
                    .GetType()
                    .GetField(
                        "snapshot",
                        System.Reflection.BindingFlags.Instance
                            | System.Reflection.BindingFlags.Public
                    );
                Assert.IsNotNull(
                    snapshotField,
                    "Each route history entry must expose its frozen snapshot."
                );

                object snapshot = snapshotField.GetValue(historyEntry);
                Assert.IsNotNull(snapshot, "A route history entry must own a snapshot instance.");
                System.Reflection.FieldInfo entryCountField = snapshot
                    .GetType()
                    .GetField(
                        "entryCount",
                        System.Reflection.BindingFlags.Instance
                            | System.Reflection.BindingFlags.Public
                    );
                Assert.IsNotNull(
                    entryCountField,
                    "Each frozen snapshot must expose its resolved dispatch entry count."
                );
                object value = entryCountField.GetValue(snapshot);
                Assert.IsInstanceOf<int>(
                    value,
                    "A frozen snapshot's resolved dispatch entry count must be an integer."
                );
                entryCount += (int)value;
            }

            return entryCount;
        }

        private static object GetLatestPostRouteSnapshot()
        {
            System.Collections.ICollection history = GetPostRouteSnapshotHistory();
            Assert.IsNotNull(history, "A route mutation during dispatch must create history.");
            Assert.Greater(history.Count, 0, "Route history must contain the current mutation.");

            object latestEntry = null;
            foreach (object historyEntry in history)
            {
                latestEntry = historyEntry;
            }

            System.Reflection.FieldInfo snapshotField = latestEntry
                .GetType()
                .GetField(
                    "snapshot",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                );
            Assert.IsNotNull(snapshotField, "A route history entry must expose its snapshot.");
            object snapshot = snapshotField.GetValue(latestEntry);
            Assert.IsNotNull(snapshot, "A route history entry must own a snapshot instance.");
            return snapshot;
        }

        private static bool IsDispatchSnapshotReleased(object snapshot)
        {
            System.Type snapshotType = snapshot.GetType();
            System.Reflection.FieldInfo entryCountField = snapshotType.GetField(
                "entryCount",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
            );
            System.Reflection.FieldInfo flatField = snapshotType.GetField(
                "flat",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
            );
            Assert.IsNotNull(entryCountField, "A frozen snapshot must expose its entry count.");
            Assert.IsNotNull(flatField, "A frozen snapshot must expose its flat dispatch holder.");
            object value = entryCountField.GetValue(snapshot);
            Assert.IsInstanceOf<int>(value, "A frozen snapshot entry count must be an integer.");
            return (int)value == 0 && flatField.GetValue(snapshot) == null;
        }

        private static object GetPostRouteCompactionIndex()
        {
            System.Reflection.FieldInfo indexField =
                typeof(DxMessaging.Core.MessageBus.MessageBus).GetField(
                    "_postRouteCompactionRoutes",
                    System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic
                );
            Assert.IsNotNull(
                indexField,
                "MessageBus must expose the transient route compaction index to structural tests."
            );
            return indexField.GetValue(MessageHandler.MessageBus);
        }

        private sealed class ContextMapPoolCapScope : IDisposable
        {
            private readonly DxMessaging.Core.MessageBus.MessageBus.ContextMapPoolBenchmarkObservation _baseline;
            private readonly IDisposable _isolation;
            private bool _disposed;

            public ContextMapPoolCapScope(int maxRetained)
            {
                _baseline =
                    DxMessaging.Core.MessageBus.MessageBus.ObserveContextMapPoolForBenchmark();
                _isolation =
                    DxMessaging.Core.MessageBus.MessageBus.IsolateContextMapPoolForBenchmark();
                try
                {
                    DxMessaging.Core.MessageBus.MessageBus.ConfigureContextMapPoolForBenchmark(
                        _baseline.UseLru,
                        maxRetained
                    );
                }
                catch
                {
                    _isolation.Dispose();
                    throw;
                }
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                try
                {
                    DxMessaging.Core.MessageBus.MessageBus.ConfigureContextMapPoolForBenchmark(
                        _baseline.UseLru,
                        _baseline.MaxRetained
                    );
                }
                finally
                {
                    _isolation.Dispose();
                }
            }
        }

        private static System.Collections.ICollection GetPostRouteSnapshotHistory()
        {
            System.Reflection.FieldInfo historyField =
                typeof(DxMessaging.Core.MessageBus.MessageBus).GetField(
                    "_postRouteSnapshotHistory",
                    System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic
                );
            Assert.IsNotNull(
                historyField,
                "MessageBus must retain the pre-mutation route history field."
            );

            object value = historyField.GetValue(MessageHandler.MessageBus);
            if (value == null)
            {
                return null;
            }

            Assert.IsInstanceOf<System.Collections.ICollection>(
                value,
                "The route history must expose collection count semantics for structural tests."
            );
            return (System.Collections.ICollection)value;
        }

        private static MessageRegistrationHandle RegisterRewritingInterceptor(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId from,
            InstanceId to,
            Action onRewritten = null
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Targeted:
                {
                    return ScenarioHarness.RegisterTargetedInterceptor<SimpleTargetedMessage>(
                        scenario,
                        token,
                        (ref InstanceId target, ref SimpleTargetedMessage _) =>
                        {
                            if (target == from)
                            {
                                target = to;
                                onRewritten?.Invoke();
                            }

                            return true;
                        }
                    );
                }
                case MessageKind.Broadcast:
                {
                    return ScenarioHarness.RegisterBroadcastInterceptor<SimpleBroadcastMessage>(
                        scenario,
                        token,
                        (ref InstanceId source, ref SimpleBroadcastMessage _) =>
                        {
                            if (source == from)
                            {
                                source = to;
                                onRewritten?.Invoke();
                            }

                            return true;
                        }
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

        private static MessageRegistrationHandle RegisterCountingHandler(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId context,
            Action onInvoked
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Targeted:
                {
                    return ScenarioHarness.RegisterTargeted<SimpleTargetedMessage>(
                        scenario,
                        token,
                        context,
                        (ref SimpleTargetedMessage _) => onInvoked()
                    );
                }
                case MessageKind.Broadcast:
                {
                    return ScenarioHarness.RegisterBroadcast<SimpleBroadcastMessage>(
                        scenario,
                        token,
                        context,
                        (ref SimpleBroadcastMessage _) => onInvoked()
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

        private static MessageRegistrationHandle RegisterCountingPostProcessor(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId context,
            Action onInvoked,
            PostProcessorDelegateShape delegateShape = PostProcessorDelegateShape.Fast
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Targeted:
                {
                    if (delegateShape == PostProcessorDelegateShape.Action)
                    {
                        return token.RegisterTargetedPostProcessor<SimpleTargetedMessage>(
                            context,
                            (SimpleTargetedMessage _) => onInvoked()
                        );
                    }

                    return ScenarioHarness.RegisterTargetedPostProcessor<SimpleTargetedMessage>(
                        scenario,
                        token,
                        context,
                        (ref SimpleTargetedMessage _) => onInvoked()
                    );
                }
                case MessageKind.Broadcast:
                {
                    if (delegateShape == PostProcessorDelegateShape.Action)
                    {
                        return token.RegisterBroadcastPostProcessor<SimpleBroadcastMessage>(
                            context,
                            (SimpleBroadcastMessage _) => onInvoked()
                        );
                    }

                    return ScenarioHarness.RegisterBroadcastPostProcessor<SimpleBroadcastMessage>(
                        scenario,
                        token,
                        context,
                        (ref SimpleBroadcastMessage _) => onInvoked()
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

        private static MessageRegistrationHandle RegisterWithoutContextRecorder(
            MessageScenario scenario,
            MessageRegistrationToken token,
            Action<InstanceId> onObserved
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Targeted:
                {
                    return token.RegisterTargetedWithoutTargeting<SimpleTargetedMessage>(
                        (InstanceId target, SimpleTargetedMessage _) => onObserved(target)
                    );
                }
                case MessageKind.Broadcast:
                {
                    return token.RegisterBroadcastWithoutSource<SimpleBroadcastMessage>(
                        (InstanceId source, SimpleBroadcastMessage _) => onObserved(source)
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

        private static MessageRegistrationHandle RegisterGlobalContextRecorder(
            MessageRegistrationToken token,
            Action<InstanceId> onObserved
        )
        {
            return token.RegisterGlobalAcceptAll(
                (IUntargetedMessage _) => { },
                (InstanceId target, ITargetedMessage message) =>
                {
                    if (message is SimpleTargetedMessage)
                    {
                        onObserved(target);
                    }
                },
                (InstanceId source, IBroadcastMessage message) =>
                {
                    if (message is SimpleBroadcastMessage)
                    {
                        onObserved(source);
                    }
                }
            );
        }

        private static void EmitForScenario(MessageScenario scenario, InstanceId context)
        {
            switch (scenario.Kind)
            {
                case MessageKind.Targeted:
                {
                    SimpleTargetedMessage message = new();
                    ScenarioHarness.EmitTargeted(scenario, ref message, context);
                    return;
                }
                case MessageKind.Broadcast:
                {
                    SimpleBroadcastMessage message = new();
                    ScenarioHarness.EmitBroadcast(scenario, ref message, context);
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
