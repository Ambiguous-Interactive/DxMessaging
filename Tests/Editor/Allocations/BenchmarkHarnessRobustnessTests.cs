#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Editor.Allocations
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using DxMessaging.Core;
    using DxMessaging.Core.Extensions;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Editor.Benchmarks;
    using DxMessaging.Tests.Runtime.Benchmarks;
    using DxMessaging.Tests.Runtime.Scripts.Components;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;
    using UnityEngine;
    using UnityEngine.TestTools;

    [Category("Performance")]
    public sealed class BenchmarkHarnessRobustnessTests : BenchmarkTestBase
    {
        [TestCase("untargeted")]
        [TestCase("targeted-game-object")]
        [TestCase("targeted-component")]
        public void RunWithComponentProvidesEnabledTokenForCoreRegistrationShapes(string mode)
        {
            RunWithComponent(
                (component, token) =>
                {
                    Assert.IsNotNull(token, "Benchmark harness should always provide a token.");
                    Assert.IsTrue(
                        token.Enabled,
                        "Benchmark token should be enabled before registration."
                    );

                    int count = 0;
                    switch (mode)
                    {
                        case "untargeted":
                            token.RegisterUntargeted<SimpleUntargetedMessage>(
                                (ref SimpleUntargetedMessage _) => ++count
                            );
                            SimpleUntargetedMessage untargetedMessage = new();
                            untargetedMessage.EmitUntargeted();
                            break;
                        case "targeted-game-object":
                            token.RegisterGameObjectTargeted<SimpleTargetedMessage>(
                                component.gameObject,
                                (ref SimpleTargetedMessage _) => ++count
                            );
                            SimpleTargetedMessage targetedGameObjectMessage = new();
                            targetedGameObjectMessage.EmitGameObjectTargeted(component.gameObject);
                            break;
                        case "targeted-component":
                            token.RegisterComponentTargeted<SimpleTargetedMessage>(
                                component,
                                (ref SimpleTargetedMessage _) => ++count
                            );
                            SimpleTargetedMessage targetedComponentMessage = new();
                            targetedComponentMessage.EmitComponentTargeted(component);
                            break;
                        default:
                            Assert.Fail($"Unhandled benchmark registration mode '{mode}'.");
                            break;
                    }

                    Assert.AreEqual(
                        1,
                        count,
                        $"Expected mode '{mode}' to receive exactly one message."
                    );
                }
            );
        }

        [UnityTest]
        public IEnumerator RunWithComponentUnregistersHandlersBetweenInvocationsSinglePass()
        {
            yield return RunWithComponentUnregistersHandlersBetweenInvocationsCore(1);
        }

        [UnityTest]
        public IEnumerator RunWithComponentUnregistersHandlersBetweenInvocationsTwoPasses()
        {
            yield return RunWithComponentUnregistersHandlersBetweenInvocationsCore(2);
        }

        [UnityTest]
        public IEnumerator RunWithComponentUnregistersHandlersBetweenInvocationsFourPasses()
        {
            yield return RunWithComponentUnregistersHandlersBetweenInvocationsCore(4);
        }

        [TestCase(0)]
        [TestCase(-1)]
        public void RunWithComponentUnregistersHandlersBetweenInvocationsRejectsNonPositiveCounts(
            int invocations
        )
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ValidateInvocationCount(invocations));
        }

        [Test]
        public void RunWithComponentUnregistersHandlersWhenBenchmarkActionThrows()
        {
            int leakedInvocationCount = 0;
            SimpleUntargetedMessage message = new();

            Assert.Throws<InvalidOperationException>(() =>
                RunWithComponent(
                    (_, token) =>
                    {
                        token.RegisterUntargeted<SimpleUntargetedMessage>(
                            (ref SimpleUntargetedMessage _) => ++leakedInvocationCount
                        );
                        throw new InvalidOperationException(
                            "Intentional benchmark action failure."
                        );
                    }
                )
            );

            message.EmitUntargeted();
            Assert.Zero(
                leakedInvocationCount,
                $"RunWithComponent should unregister handlers even when the benchmark action throws. {DescribeMessageBusState(MessageHandler.MessageBus, includeLog: true)}"
            );
            AssertMessageBusCounts(
                expectedUntargeted: 0,
                expectedTargeted: 0,
                expectedBroadcast: 0,
                "after benchmark action exception"
            );
        }

        private IEnumerator RunWithComponentUnregistersHandlersBetweenInvocationsCore(
            int invocations
        )
        {
            ValidateInvocationCount(invocations);
            string scenario = $"invocations={invocations}";

            yield return WaitUntilMessageHandlerIsFresh();
            AssertMessageBusCounts(
                expectedUntargeted: 0,
                expectedTargeted: 0,
                expectedBroadcast: 0,
                $"before scenario {scenario}"
            );

            int cumulativeInvocationCount = 0;
            for (int i = 0; i < invocations; ++i)
            {
                SimpleUntargetedMessage message = new();
                int invocationStart = cumulativeInvocationCount;
                RunWithComponent(
                    (_, token) =>
                    {
                        token.RegisterUntargeted<SimpleUntargetedMessage>(
                            (ref SimpleUntargetedMessage _) => ++cumulativeInvocationCount
                        );
                        message.EmitUntargeted();

                        Assert.AreEqual(
                            invocationStart + 1,
                            cumulativeInvocationCount,
                            $"Expected exactly one invocation for pass {i + 1}/{invocations} ({scenario})."
                        );
                    }
                );

                // Explicitly verify cross-invocation isolation so stale bus state is caught at the source.
                yield return WaitUntilMessageHandlerIsFresh();
                Assert.AreEqual(
                    i + 1,
                    cumulativeInvocationCount,
                    $"Invocation count drift after pass {i + 1}/{invocations} ({scenario}). {DescribeMessageBusState(MessageHandler.MessageBus, includeLog: true)}"
                );
                AssertMessageBusCounts(
                    expectedUntargeted: 0,
                    expectedTargeted: 0,
                    expectedBroadcast: 0,
                    $"after invocation {i + 1}/{invocations} ({scenario})"
                );
            }
        }

        [TestCase(1)]
        [TestCase(8)]
        [TestCase(32)]
        public void RunWithComponentInvokesUntargetedHandlersDeterministically(int emissions)
        {
            RunWithComponent(
                (_, token) =>
                {
                    int count = 0;
                    SimpleUntargetedMessage message = new();
                    token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) => ++count
                    );

                    for (int i = 0; i < emissions; ++i)
                    {
                        message.EmitUntargeted();
                    }

                    Assert.AreEqual(
                        emissions,
                        count,
                        "Benchmark harness should invoke untargeted handlers exactly once per emission."
                    );
                }
            );
        }

        [Test]
        public void RunWithComponentSupportsTokenDisableEnableCycle()
        {
            RunWithComponent(
                (_, token) =>
                {
                    int count = 0;
                    SimpleUntargetedMessage message = new();
                    token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (ref SimpleUntargetedMessage _) => ++count
                    );

                    token.Disable();
                    Assert.IsFalse(token.Enabled);

                    message.EmitUntargeted();
                    Assert.AreEqual(0, count);

                    token.Enable();
                    Assert.IsTrue(token.Enabled);

                    message.EmitUntargeted();
                    Assert.AreEqual(
                        1,
                        count,
                        "Handler should resume after the benchmark token is re-enabled."
                    );
                }
            );
        }

        [Test]
        public void RunWithComponentPreparesMonoBehavioursForSendMessageInEditMode()
        {
            RunWithComponent(
                (component, _) =>
                {
                    MonoBehaviour[] behaviours =
                        component.gameObject.GetComponents<MonoBehaviour>();
                    Assert.Greater(
                        behaviours.Length,
                        0,
                        "Benchmark harness should create at least one MonoBehaviour on the target GameObject."
                    );

                    foreach (MonoBehaviour behaviour in behaviours)
                    {
                        Assert.IsTrue(
                            behaviour.enabled,
                            $"Expected benchmark MonoBehaviour '{behaviour.GetType().Name}' to be enabled before dispatch."
                        );

#if UNITY_EDITOR
                        if (!Application.isPlaying)
                        {
                            Assert.IsTrue(
                                behaviour.runInEditMode,
                                $"Expected benchmark MonoBehaviour '{behaviour.GetType().Name}' to run in EditMode for SendMessage-based dispatch."
                            );
                        }
#endif
                    }
                }
            );
        }

        [TestCase(ReflexiveSendMode.Flat, false, 0, 1, 0)]
        [TestCase(ReflexiveSendMode.Downwards, false, 0, 1, 1)]
        [TestCase(ReflexiveSendMode.Upwards, true, 1, 1, 1)]
        public void RunWithComponentDeliversReflexiveOneArgumentMessagesAcrossFastPathModes(
            ReflexiveSendMode sendMode,
            bool targetChild,
            int expectedGrandParentCount,
            int expectedParentCount,
            int expectedChildCount
        )
        {
            RunWithComponent(
                (component, _) =>
                {
                    GameObject parent = component.gameObject;
                    if (!parent.TryGetComponent(out SimpleMessageAwareComponent parentReceiver))
                    {
                        parentReceiver = parent.AddComponent<SimpleMessageAwareComponent>();
                    }

                    GameObject grandParent = new(
                        "BenchmarkReflexiveGrandParent",
                        typeof(SimpleMessageAwareComponent)
                    );
                    _spawned.Add(grandParent);
                    parent.transform.SetParent(grandParent.transform);

                    GameObject child = new(
                        "BenchmarkReflexiveChild",
                        typeof(SimpleMessageAwareComponent)
                    );
                    _spawned.Add(child);
                    child.transform.SetParent(parent.transform);

                    SimpleMessageAwareComponent grandParentReceiver =
                        grandParent.GetComponent<SimpleMessageAwareComponent>();
                    SimpleMessageAwareComponent childReceiver =
                        child.GetComponent<SimpleMessageAwareComponent>();
                    PrepareBenchmarkBehaviourForSendMessage(parentReceiver);
                    PrepareBenchmarkBehaviourForSendMessage(grandParentReceiver);
                    PrepareBenchmarkBehaviourForSendMessage(childReceiver);

                    int grandParentCount = 0;
                    int parentCount = 0;
                    int childCount = 0;
                    grandParentReceiver.slowComplexTargetedHandler = () => ++grandParentCount;
                    parentReceiver.slowComplexTargetedHandler = () => ++parentCount;
                    childReceiver.slowComplexTargetedHandler = () => ++childCount;

                    ComplexTargetedMessage payload = new(Guid.NewGuid());
                    ReflexiveMessage message = new(
                        nameof(SimpleMessageAwareComponent.HandleSlowComplexTargetedMessage),
                        sendMode,
                        payload
                    );

                    InstanceId target = targetChild ? child : parent;
                    message.EmitTargeted(target);

                    string scenario = $"sendMode '{sendMode}', targetChild={targetChild}";
                    Assert.AreEqual(
                        expectedGrandParentCount,
                        grandParentCount,
                        $"Unexpected grand-parent invocation count for {scenario}."
                    );
                    Assert.AreEqual(
                        expectedParentCount,
                        parentCount,
                        $"Unexpected parent invocation count for {scenario}."
                    );
                    Assert.AreEqual(
                        expectedChildCount,
                        childCount,
                        $"Unexpected child invocation count for {scenario}."
                    );
                }
            );
        }

        // Data-driven over EVERY DispatchBenchmarkScenario so adding an enum value without
        // wiring up its metadata (Key/DisplayName) fails this suite automatically. These
        // metadata cases are deliberately cheap (no measurement window) so they stay in the
        // fast gate; the run-the-scenario lock below carries the heavy PerfBench category.
        private static IEnumerable<TestCaseData> DispatchScenarioCases()
        {
            foreach (DispatchBenchmarkScenario scenario in DispatchBenchmarkScenarios.All)
            {
                yield return new TestCaseData(scenario).SetName($"DispatchScenario_{scenario}");
            }
        }

        [Test]
        [TestCaseSource(nameof(DispatchScenarioCases))]
        public void DispatchScenarioHasNonEmptyKeyAndDisplayName(DispatchBenchmarkScenario scenario)
        {
            Assert.IsNotEmpty(
                DispatchBenchmarkScenarios.Key(scenario),
                $"Dispatch scenario '{scenario}' must declare a non-empty stable Key."
            );
            Assert.IsNotEmpty(
                DispatchBenchmarkScenarios.DisplayName(scenario),
                $"Dispatch scenario '{scenario}' must declare a non-empty DisplayName."
            );
        }

        [Test]
        public void DispatchScenarioKeysAreUnique()
        {
            string[] keys = DispatchBenchmarkScenarios
                .All.Select(DispatchBenchmarkScenarios.Key)
                .ToArray();
            CollectionAssert.AllItemsAreUnique(
                keys,
                "Dispatch scenario Keys must be unique; they are stable join keys for the baseline CSV and perf doc."
            );
        }

        [Test]
        public void DispatchScenarioDisplayNamesAreUnique()
        {
            string[] displayNames = DispatchBenchmarkScenarios
                .All.Select(DispatchBenchmarkScenarios.DisplayName)
                .ToArray();
            CollectionAssert.AllItemsAreUnique(
                displayNames,
                "Dispatch scenario DisplayNames must be unique so rendered docs never collide two scenarios under one label."
            );
        }

        [Test]
        public void BenchmarkMethodologyConstantsAreLocked()
        {
            Assert.AreEqual(
                5,
                BenchmarkProtocol.MeasurementSeconds,
                "The shared benchmark measurement window must remain 5 seconds."
            );
            Assert.AreEqual(
                TimeSpan.FromSeconds(BenchmarkProtocol.MeasurementSeconds),
                BenchmarkProtocol.MeasurementWindow,
                "MeasurementWindow must equal MeasurementSeconds so the methodology stays consistent."
            );
            Assert.AreEqual(
                BenchmarkProtocol.BatchSize,
                NumInvocationsPerIteration,
                "The editor benchmark harness must share the single batch-size constant with BenchmarkProtocol."
            );
        }

        [Test]
        public void WarmupEmitsSkipsFloodAndKeepsDefaultForEmitScenarios()
        {
            Assert.AreEqual(
                0,
                DispatchBenchmarkScenarios.WarmupEmits(
                    DispatchBenchmarkScenario.RegistrationFlood1000TypesFromColdBus
                ),
                "The cold-bus registration flood must perform no warm-up flood so it measures first-touch registration cost."
            );
            Assert.AreEqual(
                BenchmarkProtocol.WarmupEmits,
                DispatchBenchmarkScenarios.WarmupEmits(
                    DispatchBenchmarkScenario.UntargetedFloodOneHandler
                ),
                "Emit scenarios must keep the shared warm-up emit count."
            );
            Assert.AreEqual(
                10_000,
                DispatchBenchmarkScenarios.WarmupEmits(
                    DispatchBenchmarkScenario.UntargetedFloodOneHandler
                ),
                "The shared warm-up emit count is locked at 10,000 for emit scenarios."
            );
        }

        // The "every scenario captures allocation bytes + CSV stays 7 columns" lock. This runs
        // the real 5s measurement window per scenario, so it carries the PerfBench category and
        // stays out of the fast metadata gate above.
        [Test, Category("PerfBench")]
        [TestCaseSource(nameof(DispatchScenarioCases))]
        public void DispatchScenarioRunEmitsSevenColumnCsvWithAllocationBytes(
            DispatchBenchmarkScenario scenario
        )
        {
            DispatchBenchmarkResult result = DispatchThroughputBenchmarks.RunScenario(
                scenario,
                logResult: false
            );

            string csvRow = result.ToCsvRow();
            string[] fields = csvRow.Split(',');
            Assert.AreEqual(
                7,
                fields.Length,
                $"Scenario '{scenario}' CSV row must stay exactly 7 columns. Row: '{csvRow}'."
            );
            Assert.IsNotEmpty(
                fields[5],
                $"Scenario '{scenario}' must populate the allocated-bytes field (index 5). Row: '{csvRow}'."
            );
            Assert.GreaterOrEqual(
                result.AllocatedBytesDelta,
                0,
                $"Scenario '{scenario}' must report a non-negative allocated-bytes delta."
            );
        }

        private static void AssertMessageBusCounts(
            int expectedUntargeted,
            int expectedTargeted,
            int expectedBroadcast,
            string context
        )
        {
            IMessageBus messageBus = MessageHandler.MessageBus;
            Assert.IsNotNull(messageBus, $"MessageBus was null while validating {context}.");

            Assert.AreEqual(
                expectedUntargeted,
                messageBus.RegisteredUntargeted,
                $"Unexpected untargeted registration count {context}."
            );
            Assert.AreEqual(
                expectedTargeted,
                messageBus.RegisteredTargeted,
                $"Unexpected targeted registration count {context}."
            );
            Assert.AreEqual(
                expectedBroadcast,
                messageBus.RegisteredBroadcast,
                $"Unexpected broadcast registration count {context}."
            );
        }

        private static void ValidateInvocationCount(int invocations)
        {
            if (invocations <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(invocations),
                    invocations,
                    "Invocation count must be positive."
                );
            }
        }
    }
}

#endif
