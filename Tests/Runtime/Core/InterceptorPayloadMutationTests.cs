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
    /// Pins the interceptor payload-transformation contract documented on
    /// <see cref="DxMessaging.Core.MessageBus.IMessageBus"/>: interceptors may
    /// rewrite the in-flight message, interceptors chain in ascending priority
    /// order, and every later pipeline stage (GlobalAcceptAll sinks, handlers,
    /// post-processors) observes the final transformed payload. The stage order
    /// pinned here matches the dispatch implementation for all three kinds:
    /// interceptors, then GlobalAcceptAll, then handlers, then post-processors
    /// (see MessageBus.UntargetedBroadcast / TargetedBroadcast /
    /// SourcedBroadcast).
    /// </summary>
    public sealed class InterceptorPayloadMutationTests : MessagingTestBase
    {
        [Test]
        public void SingleInterceptorMutationIsObservedByHandler(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(SingleInterceptorMutationIsObservedByHandler) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            Guid originalId = Guid.NewGuid();
            Guid mutatedId = Guid.NewGuid();
            List<Guid> observedByInterceptor = new();
            List<Guid> observedByHandler = new();
            List<MessageRegistrationHandle> handles = new();

            using (LeakWatcher watcher = LeakWatcher.Watch(label: scenario.DisplayName))
            {
                handles.Add(
                    RegisterMutatingInterceptor(
                        scenario,
                        token,
                        priority: 0,
                        onIntercepted: observedByInterceptor.Add,
                        replacement: mutatedId
                    )
                );
                handles.Add(
                    RegisterRecordingHandler(scenario, token, hostId, observedByHandler.Add)
                );

                EmitComplex(scenario, hostId, originalId);
                RemoveAll(token, handles);
            }

            Assert.AreEqual(
                1,
                observedByInterceptor.Count,
                "[{0}] Interceptor must run exactly once. interceptorCount={1}.",
                scenario.Kind,
                observedByInterceptor.Count
            );
            Assert.AreEqual(
                originalId,
                observedByInterceptor[0],
                "[{0}] Interceptor must observe the originally emitted payload {1} but saw {2}.",
                scenario.Kind,
                originalId,
                observedByInterceptor[0]
            );
            Assert.AreEqual(
                1,
                observedByHandler.Count,
                "[{0}] Handler must run exactly once. handlerCount={1}.",
                scenario.Kind,
                observedByHandler.Count
            );
            Assert.AreEqual(
                mutatedId,
                observedByHandler[0],
                "[{0}] Handler must observe the interceptor-mutated payload {1}, not the original {2}; saw {3}.",
                scenario.Kind,
                mutatedId,
                originalId,
                observedByHandler[0]
            );
        }

        [Test]
        public void ChainedInterceptorsMutateInPriorityOrder(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(ChainedInterceptorsMutateInPriorityOrder) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            Guid originalId = Guid.NewGuid();
            Guid firstMutation = Guid.NewGuid();
            Guid secondMutation = Guid.NewGuid();
            List<Guid> observedByEarly = new();
            List<Guid> observedByLate = new();
            List<Guid> observedByHandler = new();
            List<MessageRegistrationHandle> handles = new();

            using (LeakWatcher watcher = LeakWatcher.Watch(label: scenario.DisplayName))
            {
                // Register the LATER-priority interceptor first so any accidental
                // dependence on registration order (instead of priority order)
                // fails loudly below.
                handles.Add(
                    RegisterMutatingInterceptor(
                        scenario,
                        token,
                        priority: 10,
                        onIntercepted: observedByLate.Add,
                        replacement: secondMutation
                    )
                );
                handles.Add(
                    RegisterMutatingInterceptor(
                        scenario,
                        token,
                        priority: 0,
                        onIntercepted: observedByEarly.Add,
                        replacement: firstMutation
                    )
                );
                handles.Add(
                    RegisterRecordingHandler(scenario, token, hostId, observedByHandler.Add)
                );

                EmitComplex(scenario, hostId, originalId);
                RemoveAll(token, handles);
            }

            Assert.AreEqual(
                1,
                observedByEarly.Count,
                "[{0}] Priority-0 interceptor must run exactly once. count={1}.",
                scenario.Kind,
                observedByEarly.Count
            );
            Assert.AreEqual(
                originalId,
                observedByEarly[0],
                "[{0}] Priority-0 interceptor runs first and must observe the original payload {1}; saw {2}.",
                scenario.Kind,
                originalId,
                observedByEarly[0]
            );
            Assert.AreEqual(
                1,
                observedByLate.Count,
                "[{0}] Priority-10 interceptor must run exactly once. count={1}.",
                scenario.Kind,
                observedByLate.Count
            );
            Assert.AreEqual(
                firstMutation,
                observedByLate[0],
                "[{0}] Priority-10 interceptor must observe the priority-0 mutation {1} (lower priority runs earlier); saw {2}.",
                scenario.Kind,
                firstMutation,
                observedByLate[0]
            );
            Assert.AreEqual(
                1,
                observedByHandler.Count,
                "[{0}] Handler must run exactly once. count={1}.",
                scenario.Kind,
                observedByHandler.Count
            );
            Assert.AreEqual(
                secondMutation,
                observedByHandler[0],
                "[{0}] Handler must observe the final chained mutation {1}; saw {2}.",
                scenario.Kind,
                secondMutation,
                observedByHandler[0]
            );
        }

        [Test]
        public void PostProcessorObservesFinalMutatedPayload(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(PostProcessorObservesFinalMutatedPayload) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            Guid originalId = Guid.NewGuid();
            Guid firstMutation = Guid.NewGuid();
            Guid finalMutation = Guid.NewGuid();
            List<Guid> observedByPostProcessor = new();
            List<MessageRegistrationHandle> handles = new();

            using (LeakWatcher watcher = LeakWatcher.Watch(label: scenario.DisplayName))
            {
                handles.Add(
                    RegisterMutatingInterceptor(
                        scenario,
                        token,
                        priority: 0,
                        onIntercepted: null,
                        replacement: firstMutation
                    )
                );
                handles.Add(
                    RegisterMutatingInterceptor(
                        scenario,
                        token,
                        priority: 5,
                        onIntercepted: null,
                        replacement: finalMutation
                    )
                );
                handles.Add(
                    RegisterRecordingPostProcessor(
                        scenario,
                        token,
                        hostId,
                        observedByPostProcessor.Add
                    )
                );

                EmitComplex(scenario, hostId, originalId);
                RemoveAll(token, handles);
            }

            Assert.AreEqual(
                1,
                observedByPostProcessor.Count,
                "[{0}] Post-processor must run exactly once. count={1}.",
                scenario.Kind,
                observedByPostProcessor.Count
            );
            Assert.AreEqual(
                finalMutation,
                observedByPostProcessor[0],
                "[{0}] Post-processor must observe the final interceptor mutation {1}, not the original {2} or intermediate {3}; saw {4}.",
                scenario.Kind,
                finalMutation,
                originalId,
                firstMutation,
                observedByPostProcessor[0]
            );
        }

        /// <summary>
        /// GlobalAcceptAll sinks run AFTER interceptors and BEFORE handlers for
        /// all three kinds (MessageBus.UntargetedBroadcast boxes the message
        /// only once interceptors approve; TargetedBroadcast / SourcedBroadcast
        /// do the same), so a global listener observes the final transformed
        /// payload and is invoked before any typed handler.
        /// </summary>
        [Test]
        public void GlobalAcceptAllObservesMutatedPayloadBetweenInterceptorsAndHandlers(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(GlobalAcceptAllObservesMutatedPayloadBetweenInterceptorsAndHandlers)
                    + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            Guid originalId = Guid.NewGuid();
            Guid mutatedId = Guid.NewGuid();
            List<Guid> observedByGlobal = new();
            List<string> stageOrder = new();
            List<MessageRegistrationHandle> handles = new();

            using (LeakWatcher watcher = LeakWatcher.Watch(label: scenario.DisplayName))
            {
                handles.Add(
                    RegisterMutatingInterceptor(
                        scenario,
                        token,
                        priority: 0,
                        onIntercepted: _ => stageOrder.Add("interceptor"),
                        replacement: mutatedId
                    )
                );
                handles.Add(
                    RegisterRecordingGlobal(
                        token,
                        observed =>
                        {
                            observedByGlobal.Add(observed);
                            stageOrder.Add("global");
                        }
                    )
                );
                handles.Add(
                    RegisterRecordingHandler(
                        scenario,
                        token,
                        hostId,
                        _ => stageOrder.Add("handler")
                    )
                );
                handles.Add(
                    RegisterRecordingPostProcessor(
                        scenario,
                        token,
                        hostId,
                        _ => stageOrder.Add("postProcessor")
                    )
                );

                EmitComplex(scenario, hostId, originalId);
                RemoveAll(token, handles);
            }

            Assert.AreEqual(
                1,
                observedByGlobal.Count,
                "[{0}] GlobalAcceptAll must observe exactly one emission. count={1}.",
                scenario.Kind,
                observedByGlobal.Count
            );
            Assert.AreEqual(
                mutatedId,
                observedByGlobal[0],
                "[{0}] GlobalAcceptAll runs after interceptors and must observe the mutated payload {1}, not the original {2}; saw {3}.",
                scenario.Kind,
                mutatedId,
                originalId,
                observedByGlobal[0]
            );
            Assert.AreEqual(
                "interceptor>global>handler>postProcessor",
                string.Join(">", stageOrder),
                "[{0}] Dispatch stage order must be interceptor, then GlobalAcceptAll, then handler, then post-processor. actual={1}.",
                scenario.Kind,
                string.Join(">", stageOrder)
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

        private static MessageRegistrationHandle RegisterMutatingInterceptor(
            MessageScenario scenario,
            MessageRegistrationToken token,
            int priority,
            Action<Guid> onIntercepted,
            Guid replacement
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    return ScenarioHarness.RegisterUntargetedInterceptor<ComplexUntargetedMessage>(
                        scenario,
                        token,
                        (ref ComplexUntargetedMessage message) =>
                        {
                            onIntercepted?.Invoke(message.firstId);
                            message = new ComplexUntargetedMessage(replacement);
                            return true;
                        },
                        priority
                    );
                }
                case MessageKind.Targeted:
                {
                    return ScenarioHarness.RegisterTargetedInterceptor<ComplexTargetedMessage>(
                        scenario,
                        token,
                        (ref InstanceId _, ref ComplexTargetedMessage message) =>
                        {
                            onIntercepted?.Invoke(message.firstId);
                            message = new ComplexTargetedMessage(replacement);
                            return true;
                        },
                        priority
                    );
                }
                case MessageKind.Broadcast:
                {
                    return ScenarioHarness.RegisterBroadcastInterceptor<ComplexBroadcastMessage>(
                        scenario,
                        token,
                        (ref InstanceId _, ref ComplexBroadcastMessage message) =>
                        {
                            onIntercepted?.Invoke(message.firstId);
                            message = new ComplexBroadcastMessage(replacement);
                            return true;
                        },
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

        private static MessageRegistrationHandle RegisterRecordingHandler(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId target,
            Action<Guid> onHandled
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    return ScenarioHarness.RegisterUntargeted<ComplexUntargetedMessage>(
                        scenario,
                        token,
                        (ref ComplexUntargetedMessage message) => onHandled(message.firstId)
                    );
                }
                case MessageKind.Targeted:
                {
                    return ScenarioHarness.RegisterTargeted<ComplexTargetedMessage>(
                        scenario,
                        token,
                        target,
                        (ref ComplexTargetedMessage message) => onHandled(message.firstId)
                    );
                }
                case MessageKind.Broadcast:
                {
                    return ScenarioHarness.RegisterBroadcast<ComplexBroadcastMessage>(
                        scenario,
                        token,
                        target,
                        (ref ComplexBroadcastMessage message) => onHandled(message.firstId)
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

        private static MessageRegistrationHandle RegisterRecordingPostProcessor(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId target,
            Action<Guid> onPostProcessed
        )
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    return ScenarioHarness.RegisterUntargetedPostProcessor<ComplexUntargetedMessage>(
                        scenario,
                        token,
                        (ref ComplexUntargetedMessage message) => onPostProcessed(message.firstId)
                    );
                }
                case MessageKind.Targeted:
                {
                    return ScenarioHarness.RegisterTargetedPostProcessor<ComplexTargetedMessage>(
                        scenario,
                        token,
                        target,
                        (ref ComplexTargetedMessage message) => onPostProcessed(message.firstId)
                    );
                }
                case MessageKind.Broadcast:
                {
                    return ScenarioHarness.RegisterBroadcastPostProcessor<ComplexBroadcastMessage>(
                        scenario,
                        token,
                        target,
                        (ref ComplexBroadcastMessage message) => onPostProcessed(message.firstId)
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

        private static MessageRegistrationHandle RegisterRecordingGlobal(
            MessageRegistrationToken token,
            Action<Guid> onObserved
        )
        {
            return token.RegisterGlobalAcceptAll(
                (IUntargetedMessage message) =>
                {
                    if (message is ComplexUntargetedMessage typed)
                    {
                        onObserved(typed.firstId);
                    }
                },
                (InstanceId _, ITargetedMessage message) =>
                {
                    if (message is ComplexTargetedMessage typed)
                    {
                        onObserved(typed.firstId);
                    }
                },
                (InstanceId _, IBroadcastMessage message) =>
                {
                    if (message is ComplexBroadcastMessage typed)
                    {
                        onObserved(typed.firstId);
                    }
                }
            );
        }

        private static void EmitComplex(MessageScenario scenario, InstanceId target, Guid firstId)
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    ComplexUntargetedMessage message = new(firstId);
                    ScenarioHarness.EmitUntargeted(scenario, ref message);
                    return;
                }
                case MessageKind.Targeted:
                {
                    ComplexTargetedMessage message = new(firstId);
                    ScenarioHarness.EmitTargeted(scenario, ref message, target);
                    return;
                }
                case MessageKind.Broadcast:
                {
                    ComplexBroadcastMessage message = new(firstId);
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
