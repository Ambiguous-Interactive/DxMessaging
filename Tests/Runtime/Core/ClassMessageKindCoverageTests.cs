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
    /// Functional coverage for reference-type (class) messages across all three
    /// dispatch kinds. Class messages are dispatched by reference - no copy is
    /// taken - so handlers observe the exact emitted instance, mutations made by
    /// one handler are visible to later handlers and post-processors of the same
    /// emission, and null fields flow through untouched.
    /// </summary>
    public sealed class ClassMessageKindCoverageTests : MessagingTestBase
    {
        private sealed class MutableClassUntargetedMessage
            : IUntargetedMessage<MutableClassUntargetedMessage>
        {
            public int counter;
            public string label;
        }

        private sealed class MutableClassTargetedMessage
            : ITargetedMessage<MutableClassTargetedMessage>
        {
            public int counter;
            public string label;
        }

        private sealed class MutableClassBroadcastMessage
            : IBroadcastMessage<MutableClassBroadcastMessage>
        {
            public int counter;
            public string label;
        }

        [UnityTest]
        public IEnumerator ClassMessageDispatchDeliversTheExactEmittedInstance(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(ClassMessageDispatchDeliversTheExactEmittedInstance) + "_" + scenario,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            using (LeakWatcher watcher = LeakWatcher.Watch(label: scenario.DisplayName))
            {
                int count = 0;
                object received = null;
                string observedText = "sentinel";
                List<MessageRegistrationHandle> handles = new();

                switch (scenario.Kind)
                {
                    case MessageKind.Untargeted:
                    {
                        ClassUntargetedMessage emitted = new("payload");
                        handles.Add(
                            ScenarioHarness.RegisterUntargeted<ClassUntargetedMessage>(
                                scenario,
                                token,
                                (ref ClassUntargetedMessage message) =>
                                {
                                    ++count;
                                    received = message;
                                    observedText = message.text;
                                }
                            )
                        );
                        ScenarioHarness.EmitUntargeted(scenario, emitted);
                        AssertSingleAliasedDelivery(scenario, count, received, emitted);
                        break;
                    }
                    case MessageKind.Targeted:
                    {
                        ClassTargetedMessage emitted = new("payload");
                        handles.Add(
                            ScenarioHarness.RegisterTargeted<ClassTargetedMessage>(
                                scenario,
                                token,
                                hostId,
                                (ref ClassTargetedMessage message) =>
                                {
                                    ++count;
                                    received = message;
                                    observedText = message.text;
                                }
                            )
                        );
                        ScenarioHarness.EmitTargeted(scenario, emitted, hostId);
                        AssertSingleAliasedDelivery(scenario, count, received, emitted);
                        break;
                    }
                    case MessageKind.Broadcast:
                    {
                        ClassBroadcastMessage emitted = new("payload");
                        handles.Add(
                            ScenarioHarness.RegisterBroadcast<ClassBroadcastMessage>(
                                scenario,
                                token,
                                hostId,
                                (ref ClassBroadcastMessage message) =>
                                {
                                    ++count;
                                    received = message;
                                    observedText = message.text;
                                }
                            )
                        );
                        ScenarioHarness.EmitBroadcast(scenario, emitted, hostId);
                        AssertSingleAliasedDelivery(scenario, count, received, emitted);
                        break;
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

                Assert.AreEqual(
                    "payload",
                    observedText,
                    "Handler must observe the emitted field value for scenario {0}.",
                    scenario
                );

                foreach (MessageRegistrationHandle handle in handles)
                {
                    token.RemoveRegistration(handle);
                }
            }

            yield break;
        }

        [UnityTest]
        public IEnumerator ClassMessageWithNullFieldDispatchesWithoutThrowing(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(ClassMessageWithNullFieldDispatchesWithoutThrowing) + "_" + scenario,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            using (LeakWatcher watcher = LeakWatcher.Watch(label: scenario.DisplayName))
            {
                int count = 0;
                string observedText = "sentinel";
                List<MessageRegistrationHandle> handles = new();

                switch (scenario.Kind)
                {
                    case MessageKind.Untargeted:
                    {
                        ClassUntargetedMessage emitted = new();
                        handles.Add(
                            ScenarioHarness.RegisterUntargeted<ClassUntargetedMessage>(
                                scenario,
                                token,
                                (ref ClassUntargetedMessage message) =>
                                {
                                    ++count;
                                    observedText = message.text;
                                }
                            )
                        );
                        Assert.DoesNotThrow(
                            () => ScenarioHarness.EmitUntargeted(scenario, emitted),
                            "Emitting a class message with a null field must not throw."
                        );
                        break;
                    }
                    case MessageKind.Targeted:
                    {
                        ClassTargetedMessage emitted = new();
                        handles.Add(
                            ScenarioHarness.RegisterTargeted<ClassTargetedMessage>(
                                scenario,
                                token,
                                hostId,
                                (ref ClassTargetedMessage message) =>
                                {
                                    ++count;
                                    observedText = message.text;
                                }
                            )
                        );
                        Assert.DoesNotThrow(
                            () => ScenarioHarness.EmitTargeted(scenario, emitted, hostId),
                            "Emitting a class message with a null field must not throw."
                        );
                        break;
                    }
                    case MessageKind.Broadcast:
                    {
                        ClassBroadcastMessage emitted = new();
                        handles.Add(
                            ScenarioHarness.RegisterBroadcast<ClassBroadcastMessage>(
                                scenario,
                                token,
                                hostId,
                                (ref ClassBroadcastMessage message) =>
                                {
                                    ++count;
                                    observedText = message.text;
                                }
                            )
                        );
                        Assert.DoesNotThrow(
                            () => ScenarioHarness.EmitBroadcast(scenario, emitted, hostId),
                            "Emitting a class message with a null field must not throw."
                        );
                        break;
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

                Assert.AreEqual(
                    1,
                    count,
                    "Handler must run exactly once for scenario {0}.",
                    scenario
                );
                Assert.IsNull(
                    observedText,
                    "Handler must observe the null field unchanged for scenario {0}.",
                    scenario
                );

                foreach (MessageRegistrationHandle handle in handles)
                {
                    token.RemoveRegistration(handle);
                }
            }

            yield break;
        }

        [UnityTest]
        public IEnumerator ClassMessageMutationsAreVisibleToLaterHandlersAndPostProcessors(
            [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))]
                MessageScenario scenario
        )
        {
            GameObject host = new(
                nameof(ClassMessageMutationsAreVisibleToLaterHandlersAndPostProcessors)
                    + "_"
                    + scenario,
                typeof(EmptyMessageAwareComponent)
            );
            _spawned.Add(host);
            EmptyMessageAwareComponent component = host.GetComponent<EmptyMessageAwareComponent>();
            MessageRegistrationToken token = GetToken(component);
            InstanceId hostId = host;

            using (LeakWatcher watcher = LeakWatcher.Watch(label: scenario.DisplayName))
            {
                int observedByLaterHandler = -1;
                string labelSeenByLaterHandler = null;
                int observedByPostProcessor = -1;
                bool postProcessorSawSameInstance = false;
                int callerVisibleCounter = -1;
                List<MessageRegistrationHandle> handles = new();

                switch (scenario.Kind)
                {
                    case MessageKind.Untargeted:
                    {
                        MutableClassUntargetedMessage emitted = new() { counter = 0, label = null };
                        handles.Add(
                            ScenarioHarness.RegisterUntargeted<MutableClassUntargetedMessage>(
                                scenario,
                                token,
                                (ref MutableClassUntargetedMessage message) =>
                                {
                                    message.counter += 5;
                                    message.label = "mutated";
                                },
                                priority: 0
                            )
                        );
                        handles.Add(
                            ScenarioHarness.RegisterUntargeted<MutableClassUntargetedMessage>(
                                scenario,
                                token,
                                (ref MutableClassUntargetedMessage message) =>
                                {
                                    observedByLaterHandler = message.counter;
                                    labelSeenByLaterHandler = message.label;
                                },
                                priority: 10
                            )
                        );
                        handles.Add(
                            ScenarioHarness.RegisterUntargetedPostProcessor<MutableClassUntargetedMessage>(
                                scenario,
                                token,
                                (ref MutableClassUntargetedMessage message) =>
                                {
                                    observedByPostProcessor = message.counter;
                                    postProcessorSawSameInstance = ReferenceEquals(
                                        message,
                                        emitted
                                    );
                                }
                            )
                        );
                        ScenarioHarness.EmitUntargeted(scenario, emitted);
                        callerVisibleCounter = emitted.counter;
                        break;
                    }
                    case MessageKind.Targeted:
                    {
                        MutableClassTargetedMessage emitted = new() { counter = 0, label = null };
                        handles.Add(
                            ScenarioHarness.RegisterTargeted<MutableClassTargetedMessage>(
                                scenario,
                                token,
                                hostId,
                                (ref MutableClassTargetedMessage message) =>
                                {
                                    message.counter += 5;
                                    message.label = "mutated";
                                },
                                priority: 0
                            )
                        );
                        handles.Add(
                            ScenarioHarness.RegisterTargeted<MutableClassTargetedMessage>(
                                scenario,
                                token,
                                hostId,
                                (ref MutableClassTargetedMessage message) =>
                                {
                                    observedByLaterHandler = message.counter;
                                    labelSeenByLaterHandler = message.label;
                                },
                                priority: 10
                            )
                        );
                        handles.Add(
                            ScenarioHarness.RegisterTargetedPostProcessor<MutableClassTargetedMessage>(
                                scenario,
                                token,
                                hostId,
                                (ref MutableClassTargetedMessage message) =>
                                {
                                    observedByPostProcessor = message.counter;
                                    postProcessorSawSameInstance = ReferenceEquals(
                                        message,
                                        emitted
                                    );
                                }
                            )
                        );
                        ScenarioHarness.EmitTargeted(scenario, emitted, hostId);
                        callerVisibleCounter = emitted.counter;
                        break;
                    }
                    case MessageKind.Broadcast:
                    {
                        MutableClassBroadcastMessage emitted = new() { counter = 0, label = null };
                        handles.Add(
                            ScenarioHarness.RegisterBroadcast<MutableClassBroadcastMessage>(
                                scenario,
                                token,
                                hostId,
                                (ref MutableClassBroadcastMessage message) =>
                                {
                                    message.counter += 5;
                                    message.label = "mutated";
                                },
                                priority: 0
                            )
                        );
                        handles.Add(
                            ScenarioHarness.RegisterBroadcast<MutableClassBroadcastMessage>(
                                scenario,
                                token,
                                hostId,
                                (ref MutableClassBroadcastMessage message) =>
                                {
                                    observedByLaterHandler = message.counter;
                                    labelSeenByLaterHandler = message.label;
                                },
                                priority: 10
                            )
                        );
                        handles.Add(
                            ScenarioHarness.RegisterBroadcastPostProcessor<MutableClassBroadcastMessage>(
                                scenario,
                                token,
                                hostId,
                                (ref MutableClassBroadcastMessage message) =>
                                {
                                    observedByPostProcessor = message.counter;
                                    postProcessorSawSameInstance = ReferenceEquals(
                                        message,
                                        emitted
                                    );
                                }
                            )
                        );
                        ScenarioHarness.EmitBroadcast(scenario, emitted, hostId);
                        callerVisibleCounter = emitted.counter;
                        break;
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

                Assert.AreEqual(
                    5,
                    observedByLaterHandler,
                    "Later handler must observe the earlier handler's field mutation "
                        + "(class messages alias a single instance) for scenario {0}.",
                    scenario
                );
                Assert.AreEqual(
                    "mutated",
                    labelSeenByLaterHandler,
                    "Later handler must observe the earlier handler's reference-field "
                        + "mutation for scenario {0}.",
                    scenario
                );
                Assert.AreEqual(
                    5,
                    observedByPostProcessor,
                    "Post-processor must observe the mutated state for scenario {0}.",
                    scenario
                );
                Assert.IsTrue(
                    postProcessorSawSameInstance,
                    "Post-processor must receive the exact emitted instance (no copy) "
                        + "for scenario {0}.",
                    scenario
                );
                Assert.AreEqual(
                    5,
                    callerVisibleCounter,
                    "Handler mutations must be visible to the emitter after dispatch "
                        + "for scenario {0}.",
                    scenario
                );

                foreach (MessageRegistrationHandle handle in handles)
                {
                    token.RemoveRegistration(handle);
                }
            }

            yield break;
        }

        private static void AssertSingleAliasedDelivery(
            MessageScenario scenario,
            int count,
            object received,
            object emitted
        )
        {
            Assert.AreEqual(1, count, "Handler must run exactly once for scenario {0}.", scenario);
            Assert.IsTrue(
                ReferenceEquals(received, emitted),
                "Class-message dispatch must deliver the exact emitted instance "
                    + "(aliasing, no copy) for scenario {0}.",
                scenario
            );
        }
    }
}
#endif
