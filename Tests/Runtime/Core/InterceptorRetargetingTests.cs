#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using UnityEngine;
    using UnityEngine.TestTools;

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
        [UnityTest]
        public IEnumerator RewrittenContextRoutesHandlersToNewId(
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
            yield break;
        }

        [UnityTest]
        public IEnumerator RewrittenContextIsObservedByWithoutContextSinksAndGlobals(
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
            yield break;
        }

        /// <summary>
        /// Coherent redirection semantics: a post-processor registered for the
        /// REWRITTEN id must run, exactly as a handler for the rewritten id
        /// does. Both paths honor this: MessageBus.TargetedBroadcast and
        /// MessageBus.SourcedBroadcast re-resolve the post-process snapshot
        /// for the rewritten id after interceptors run (the pre-frozen,
        /// original-id snapshot is preferred only when the id is unchanged).
        /// </summary>
        [UnityTest]
        public IEnumerator RewrittenContextRoutesPostProcessorsToNewId(
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
            yield break;
        }

        /// <summary>
        /// Coherent redirection semantics: once the interceptor redirects away
        /// from the original id, post-processors registered for the ORIGINAL id
        /// must not observe the message, mirroring handler routing.
        /// </summary>
        [UnityTest]
        public IEnumerator RewrittenContextSkipsPostProcessorsForOriginalId(
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
            yield break;
        }

        /// <summary>
        /// Coherent redirection semantics with post-processors for BOTH ids
        /// living on DISTINCT components: only the rewritten id's
        /// post-processor observes the message. Pins the re-resolution in
        /// MessageBus.TargetedBroadcast / SourcedBroadcast: when interceptors
        /// rewrite the id, the post-process snapshot is re-acquired for the
        /// FINAL id instead of preferring the stale pre-interceptor snapshot.
        /// </summary>
        [UnityTest]
        public IEnumerator RewrittenContextPostProcessorsOnDistinctComponentsFollowNewId(
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
            yield break;
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

        private static MessageRegistrationHandle RegisterRewritingInterceptor(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId from,
            InstanceId to
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
            Action onInvoked
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Targeted:
                {
                    return ScenarioHarness.RegisterTargetedPostProcessor<SimpleTargetedMessage>(
                        scenario,
                        token,
                        context,
                        (ref SimpleTargetedMessage _) => onInvoked()
                    );
                }
                case MessageKind.Broadcast:
                {
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
