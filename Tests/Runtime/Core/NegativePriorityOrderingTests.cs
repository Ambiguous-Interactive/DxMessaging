#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Tests.Runtime;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using UnityEngine;
    using UnityEngine.TestTools;

    /// <summary>
    /// Pins that negative priorities participate in the documented "lower
    /// priority runs earlier" total order for handlers, interceptors, and
    /// post-processors: negative before zero before positive, with the exact
    /// ascending order -100, -1, 0, 1, 100. Registrations are deliberately made
    /// in scrambled (non-sorted) order so a pass proves the bus sorts by
    /// priority rather than replaying registration order.
    /// </summary>
    public sealed class NegativePriorityOrderingTests : MessagingTestBase
    {
        /// <summary>
        /// Priorities registered in deliberately scrambled order; expected
        /// dispatch order is the ascending sort of this set.
        /// </summary>
        private static readonly int[] ScrambledPriorities = { 1, -100, 100, 0, -1 };

        private static readonly string[] ExpectedAscendingLabels =
        {
            "-100",
            "-1",
            "0",
            "1",
            "100",
        };

        [UnityTest]
        public IEnumerator HandlersRunLowestPriorityFirstIncludingNegatives(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(HandlersRunLowestPriorityFirstIncludingNegatives) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            List<string> order = new();
            using (LeakWatcher watcher = LeakWatcher.Watch(label: scenario.DisplayName))
            {
                List<MessageRegistrationHandle> handles = new();
                foreach (int priority in ScrambledPriorities)
                {
                    handles.Add(
                        RegisterHandlerAtPriority(scenario, token, hostId, order, priority)
                    );
                }

                EmitForScenario(scenario, hostId);

                foreach (MessageRegistrationHandle handle in handles)
                {
                    token.RemoveRegistration(handle);
                }
            }

            Assert.AreEqual(
                ExpectedAscendingLabels,
                order.ToArray(),
                "Handlers must run in ascending priority order (negative before zero before "
                    + "positive): lower priority is documented to run earlier."
            );
            yield break;
        }

        [UnityTest]
        public IEnumerator InterceptorsRunLowestPriorityFirstIncludingNegatives(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(InterceptorsRunLowestPriorityFirstIncludingNegatives) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            List<string> order = new();
            using (LeakWatcher watcher = LeakWatcher.Watch(label: scenario.DisplayName))
            {
                List<MessageRegistrationHandle> handles = new();
                foreach (int priority in ScrambledPriorities)
                {
                    handles.Add(RegisterInterceptorAtPriority(scenario, token, order, priority));
                }

                EmitForScenario(scenario, hostId);

                foreach (MessageRegistrationHandle handle in handles)
                {
                    token.RemoveRegistration(handle);
                }
            }

            Assert.AreEqual(
                ExpectedAscendingLabels,
                order.ToArray(),
                "Interceptors must run in ascending priority order (negative before zero "
                    + "before positive): lower priority is documented to run earlier."
            );
            yield break;
        }

        [UnityTest]
        public IEnumerator PostProcessorsRunLowestPriorityFirstIncludingNegatives(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(PostProcessorsRunLowestPriorityFirstIncludingNegatives) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            List<string> order = new();
            using (LeakWatcher watcher = LeakWatcher.Watch(label: scenario.DisplayName))
            {
                List<MessageRegistrationHandle> handles = new();
                foreach (int priority in ScrambledPriorities)
                {
                    handles.Add(
                        RegisterPostProcessorAtPriority(scenario, token, hostId, order, priority)
                    );
                }

                EmitForScenario(scenario, hostId);

                foreach (MessageRegistrationHandle handle in handles)
                {
                    token.RemoveRegistration(handle);
                }
            }

            Assert.AreEqual(
                ExpectedAscendingLabels,
                order.ToArray(),
                "Post-processors must run in ascending priority order (negative before zero "
                    + "before positive): lower priority is documented to run earlier."
            );
            yield break;
        }

        /// <summary>
        /// Priority sorts WITHIN a pipeline stage, never across stages: a
        /// negative-priority post-processor must still run after a
        /// positive-priority handler, and a positive-priority interceptor must
        /// still run before a negative-priority handler.
        /// </summary>
        [UnityTest]
        public IEnumerator NegativePriorityDoesNotReorderPipelineStages(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(NegativePriorityDoesNotReorderPipelineStages) + scenario.Kind,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            List<string> stages = new();
            using (LeakWatcher watcher = LeakWatcher.Watch(label: scenario.DisplayName))
            {
                List<MessageRegistrationHandle> handles = new()
                {
                    RegisterInterceptorAtPriority(scenario, token, stages, 100, label: "I"),
                    RegisterHandlerAtPriority(scenario, token, hostId, stages, -100, label: "H"),
                    RegisterPostProcessorAtPriority(
                        scenario,
                        token,
                        hostId,
                        stages,
                        -100,
                        label: "P"
                    ),
                };

                EmitForScenario(scenario, hostId);

                foreach (MessageRegistrationHandle handle in handles)
                {
                    token.RemoveRegistration(handle);
                }
            }

            Assert.AreEqual(
                new[] { "I", "H", "P" },
                stages.ToArray(),
                "Pipeline stages must stay Interceptors -> Handlers -> Post-Processors even "
                    + "when the interceptor priority (+100) is numerically greater than the "
                    + "handler and post-processor priorities (-100): priority orders within a "
                    + "stage, never across stages."
            );
            yield break;
        }

        private static MessageRegistrationHandle RegisterHandlerAtPriority(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId context,
            List<string> order,
            int priority,
            string label = null
        )
        {
            string effectiveLabel = label ?? priority.ToString();
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    return ScenarioHarness.RegisterUntargeted<SimpleUntargetedMessage>(
                        scenario,
                        token,
                        (ref SimpleUntargetedMessage _) => order.Add(effectiveLabel),
                        priority
                    );
                }
                case MessageKind.Targeted:
                {
                    return ScenarioHarness.RegisterTargeted<SimpleTargetedMessage>(
                        scenario,
                        token,
                        context,
                        (ref SimpleTargetedMessage _) => order.Add(effectiveLabel),
                        priority
                    );
                }
                case MessageKind.Broadcast:
                {
                    return ScenarioHarness.RegisterBroadcast<SimpleBroadcastMessage>(
                        scenario,
                        token,
                        context,
                        (ref SimpleBroadcastMessage _) => order.Add(effectiveLabel),
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

        private static MessageRegistrationHandle RegisterInterceptorAtPriority(
            MessageScenario scenario,
            MessageRegistrationToken token,
            List<string> order,
            int priority,
            string label = null
        )
        {
            string effectiveLabel = label ?? priority.ToString();
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    return ScenarioHarness.RegisterUntargetedInterceptor<SimpleUntargetedMessage>(
                        scenario,
                        token,
                        (ref SimpleUntargetedMessage _) =>
                        {
                            order.Add(effectiveLabel);
                            return true;
                        },
                        priority
                    );
                }
                case MessageKind.Targeted:
                {
                    return ScenarioHarness.RegisterTargetedInterceptor<SimpleTargetedMessage>(
                        scenario,
                        token,
                        (ref InstanceId _, ref SimpleTargetedMessage _) =>
                        {
                            order.Add(effectiveLabel);
                            return true;
                        },
                        priority
                    );
                }
                case MessageKind.Broadcast:
                {
                    return ScenarioHarness.RegisterBroadcastInterceptor<SimpleBroadcastMessage>(
                        scenario,
                        token,
                        (ref InstanceId _, ref SimpleBroadcastMessage _) =>
                        {
                            order.Add(effectiveLabel);
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

        private static MessageRegistrationHandle RegisterPostProcessorAtPriority(
            MessageScenario scenario,
            MessageRegistrationToken token,
            InstanceId context,
            List<string> order,
            int priority,
            string label = null
        )
        {
            string effectiveLabel = label ?? priority.ToString();
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    return ScenarioHarness.RegisterUntargetedPostProcessor<SimpleUntargetedMessage>(
                        scenario,
                        token,
                        (ref SimpleUntargetedMessage _) => order.Add(effectiveLabel),
                        priority
                    );
                }
                case MessageKind.Targeted:
                {
                    return ScenarioHarness.RegisterTargetedPostProcessor<SimpleTargetedMessage>(
                        scenario,
                        token,
                        context,
                        (ref SimpleTargetedMessage _) => order.Add(effectiveLabel),
                        priority
                    );
                }
                case MessageKind.Broadcast:
                {
                    return ScenarioHarness.RegisterBroadcastPostProcessor<SimpleBroadcastMessage>(
                        scenario,
                        token,
                        context,
                        (ref SimpleBroadcastMessage _) => order.Add(effectiveLabel),
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

        private static void EmitForScenario(MessageScenario scenario, InstanceId context)
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
