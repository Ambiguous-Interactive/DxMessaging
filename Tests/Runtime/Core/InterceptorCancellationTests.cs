#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using DxMessaging.Core;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using UnityEngine;

    public sealed class InterceptorCancellationTests : MessagingTestBase
    {
        [Test]
        public void InterceptorCancelsHandlersAndPostProcessors(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(InterceptorCancelsHandlersAndPostProcessors) + "_" + scenario,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int handled = 0;
            int postProcessed = 0;
            int laterRan = 0;

            _ = ScenarioCallbacks.RegisterCountingHandler(scenario, token, hostId, () => handled++);
            _ = ScenarioCallbacks.RegisterCountingPostProcessor(
                scenario,
                token,
                hostId,
                () => postProcessed++
            );

            // Register a canceling interceptor (always false)
            _ = ScenarioCallbacks.RegisterCountingInterceptor(scenario, token, () => false);

            // Also register a later interceptor that would be skipped if earlier cancels
            _ = ScenarioCallbacks.RegisterCountingInterceptor(
                scenario,
                token,
                () =>
                {
                    laterRan++;
                    return true;
                },
                priority: 10
            );

            ScenarioCallbacks.EmitForKind(scenario, hostId);

            Assert.AreEqual(0, handled, "Handlers must not run when interceptor cancels.");
            Assert.AreEqual(
                0,
                postProcessed,
                "Post-processors must not run when interceptor cancels."
            );
            Assert.AreEqual(0, laterRan, "Later interceptors must not run after cancellation.");
        }

        /// <summary>
        /// Interceptors run BEFORE GlobalAcceptAll sinks for every message kind
        /// (MessageBus.UntargetedBroadcast / TargetedBroadcast / SourcedBroadcast
        /// return on interceptor cancellation before the global broadcast pass),
        /// so a cancelled message must be invisible to GlobalAcceptAll listeners.
        /// The second emission, after the cancelling interceptor is removed,
        /// proves the listener itself works so the zero-count assertion cannot
        /// pass vacuously.
        /// </summary>
        [Test]
        public void CancelledMessageIsHiddenFromGlobalAcceptAll(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(CancelledMessageIsHiddenFromGlobalAcceptAll) + "_" + scenario,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            int globalCount = 0;
            int globalCountAfterCancelledEmit;
            int globalCountAfterAllowedEmit;

            using (LeakWatcher watcher = LeakWatcher.Watch(label: scenario.DisplayName))
            {
                MessageRegistrationHandle globalHandle = RegisterGlobalCounter(
                    token,
                    () => globalCount++
                );
                MessageRegistrationHandle cancelHandle =
                    ScenarioCallbacks.RegisterCountingInterceptor(scenario, token, () => false);

                ScenarioCallbacks.EmitForKind(scenario, hostId);
                globalCountAfterCancelledEmit = globalCount;

                token.RemoveRegistration(cancelHandle);
                ScenarioCallbacks.EmitForKind(scenario, hostId);
                globalCountAfterAllowedEmit = globalCount;

                token.RemoveRegistration(globalHandle);
            }

            Assert.AreEqual(
                0,
                globalCountAfterCancelledEmit,
                "[{0}] GlobalAcceptAll must NOT observe a message cancelled by an interceptor "
                    + "(interceptors run before global sinks). globalCountAfterCancelledEmit={1}.",
                scenario.Kind,
                globalCountAfterCancelledEmit
            );
            Assert.AreEqual(
                1,
                globalCountAfterAllowedEmit,
                "[{0}] GlobalAcceptAll must observe the emission once the cancelling interceptor "
                    + "is removed. globalCountAfterAllowedEmit={1}.",
                scenario.Kind,
                globalCountAfterAllowedEmit
            );
        }

        private static MessageRegistrationHandle RegisterGlobalCounter(
            MessageRegistrationToken token,
            Action onInvoked
        )
        {
            return token.RegisterGlobalAcceptAll(
                (IUntargetedMessage message) =>
                {
                    if (message is SimpleUntargetedMessage)
                    {
                        onInvoked();
                    }
                },
                (InstanceId _, ITargetedMessage message) =>
                {
                    if (message is SimpleTargetedMessage)
                    {
                        onInvoked();
                    }
                },
                (InstanceId _, IBroadcastMessage message) =>
                {
                    if (message is SimpleBroadcastMessage)
                    {
                        onInvoked();
                    }
                }
            );
        }
    }
}

#endif
