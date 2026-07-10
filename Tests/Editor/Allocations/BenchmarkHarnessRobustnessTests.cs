#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Editor.Allocations
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
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

        private static IEnumerable<TestCaseData> DispatchBaselineSetupCases()
        {
            yield return new TestCaseData(
                DispatchBenchmarkScenario.EmptyBusDispatch,
                0,
                0,
                0
            ).SetName("DispatchBaselineSetup_EmptyBus");
            yield return new TestCaseData(
                DispatchBenchmarkScenario.TargetedFloodNoMatchingTarget,
                0,
                1,
                1
            ).SetName("DispatchBaselineSetup_TargetedNoMatchingTarget");
            yield return new TestCaseData(
                DispatchBenchmarkScenario.UntargetedFloodTwoHandlersOnePriority,
                2,
                2,
                1
            ).SetName("DispatchBaselineSetup_UntargetedTwoFlatEntries");
            yield return new TestCaseData(
                DispatchBenchmarkScenario.UntargetedFloodThreeHandlersOnePriority,
                3,
                3,
                1
            ).SetName("DispatchBaselineSetup_UntargetedThreeFlatEntries");
            yield return new TestCaseData(
                DispatchBenchmarkScenario.UntargetedFloodSixteenHandlersOnePriority,
                16,
                16,
                1
            ).SetName("DispatchBaselineSetup_UntargetedSixteenFlatEntries");
            yield return new TestCaseData(
                DispatchBenchmarkScenario.UntargetedFloodOneInactiveHandler,
                0,
                1,
                1
            ).SetName("DispatchBaselineSetup_InactiveHandler");
        }

        [Test]
        [TestCaseSource(nameof(DispatchBaselineSetupCases))]
        public void DispatchBaselineScenarioSetupAndSingleEmitMatchDeclaredFanOut(
            DispatchBenchmarkScenario scenario,
            int expectedFanOut,
            int expectedControlFanOut,
            int expectedRegistrationBuckets
        )
        {
            Assert.AreEqual(
                expectedFanOut,
                DispatchThroughputBenchmarks.ExpectedHandlerInvocationsPerEmit(scenario),
                $"Scenario '{scenario}' must declare the requested exact fan-out."
            );
            DispatchThroughputBenchmarks.DispatchScenarioContractObservation observation =
                DispatchThroughputBenchmarks.ConfigureAndEmitOnceForContract(scenario);
            Assert.AreEqual(
                expectedFanOut,
                observation.ScenarioFanOut,
                $"Scenario '{scenario}' setup and routing must produce its declared fan-out for one emit."
            );
            Assert.AreEqual(
                expectedControlFanOut,
                observation.ControlFanOut,
                $"Scenario '{scenario}' control emit must prove the configured topology is live."
            );
            Assert.AreEqual(
                expectedRegistrationBuckets,
                observation.RegistrationBuckets,
                $"Scenario '{scenario}' must configure the expected public bus registration buckets."
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
        public void DispatchInvocationCounterUsesLongCount()
        {
            Type counterType = typeof(DispatchThroughputBenchmarks).GetNestedType(
                "InvocationCounter",
                BindingFlags.NonPublic
            );
            Assert.IsNotNull(
                counterType,
                "DispatchThroughputBenchmarks must keep an invocation counter."
            );

            PropertyInfo countProperty = counterType.GetProperty(
                "Count",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
            );
            Assert.IsNotNull(countProperty, "InvocationCounter must expose a Count property.");
            Assert.AreEqual(
                typeof(long),
                countProperty.PropertyType,
                "InvocationCounter.Count must be long so high-throughput 5s fan-out windows cannot overflow an int."
            );
        }

        // REGRESSION GUARD (fan-out accounting). BenchmarkProtocol.Measure drives ONE extra
        // emitBatch under AllocationProbe AFTER the timed window. Those ops are real -- they advance
        // any side-effect counter (e.g. a fan-out ProgressMarker) -- but are excluded from
        // TotalOperations/throughput because the batch is untimed. They MUST be reported as
        // AllocationProbeOperations so callers that assert an exact invocation total
        // (ComparisonHarness) can reconcile via TotalEmittedOperations; pre-fix they were dropped,
        // undercounting every comparison fan-out check by exactly one BatchSize. Runs the real 5s
        // window, so it is PerfBench (matching this file's other measurement-window tests).
        [Test, Category("PerfBench")]
        public void MeasureCountsAllocationProbeBatchOperationsExactlyOneBatch()
        {
            long observedEmits = 0;
            BenchmarkMeasurement measurement = BenchmarkProtocol.Measure(
                warmup: null,
                emitBatch: () =>
                {
                    for (int i = 0; i < BenchmarkProtocol.BatchSize; i++)
                    {
                        observedEmits++;
                    }

                    return BenchmarkProtocol.BatchSize;
                }
            );

            Assert.AreEqual(
                BenchmarkProtocol.BatchSize,
                measurement.AllocationProbeOperations,
                "Measure must run exactly one allocation-probe batch (BatchSize ops) after the timed "
                    + "window and report its operation count. A 0 here means the post-window probe "
                    + "batch's ops are being dropped (the fan-out-undercount regression)."
            );
            Assert.AreEqual(
                measurement.TotalOperations + measurement.AllocationProbeOperations,
                measurement.TotalEmittedOperations,
                "TotalEmittedOperations must equal timed ops plus the probe batch ops."
            );
            Assert.AreEqual(
                measurement.TotalEmittedOperations,
                observedEmits,
                $"Every emitBatch the protocol drives must be accounted for: observed {observedEmits} "
                    + $"actual emits but TotalEmittedOperations reported {measurement.TotalEmittedOperations} "
                    + $"(timed {measurement.TotalOperations} + probe {measurement.AllocationProbeOperations})."
            );
            Assert.Greater(
                measurement.TotalOperations,
                0,
                "The timed window must run at least one batch."
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
                0,
                DispatchBenchmarkScenarios.WarmupEmits(
                    DispatchBenchmarkScenario.MessageBusConstruction1000
                ),
                "MessageBus construction must not run an emit warm-up."
            );
            Assert.AreEqual(
                0,
                DispatchBenchmarkScenarios.WarmupEmits(
                    DispatchBenchmarkScenario.MessageRegistrationTokenConstruction1000
                ),
                "Registration-token construction must not run an emit warm-up."
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

        [Test]
        public void MedianDoubleOfOddCountReturnsMiddleElement()
        {
            double[] samples = { 5d, 1d, 3d };
            Assert.AreEqual(3d, BenchmarkProtocol.Median(samples), 1e-9);
        }

        [Test]
        public void MedianDoubleOfEvenCountAveragesTwoMiddleElements()
        {
            double[] samples = { 1d, 2d, 3d, 4d };
            Assert.AreEqual(2.5d, BenchmarkProtocol.Median(samples), 1e-9);
        }

        [Test]
        public void MedianDoubleOfSingleElementReturnsThatElement()
        {
            double[] samples = { 42d };
            Assert.AreEqual(42d, BenchmarkProtocol.Median(samples), 1e-9);
        }

        [Test]
        public void MedianDoubleDoesNotMutateCallerArray()
        {
            double[] samples = { 5d, 1d, 3d, 2d };
            double[] expected = { 5d, 1d, 3d, 2d };
            _ = BenchmarkProtocol.Median(samples);
            CollectionAssert.AreEqual(
                expected,
                samples,
                "Median(double[]) must not mutate (sort) the caller's array."
            );
        }

        [Test]
        public void MedianLongOfOddCountReturnsMiddleElement()
        {
            long[] samples = { 50L, 10L, 30L };
            Assert.AreEqual(30L, BenchmarkProtocol.Median(samples));
        }

        [Test]
        public void MedianLongOfEvenCountAveragesTwoMiddleElements()
        {
            long[] samples = { 10L, 20L, 30L, 40L };
            Assert.AreEqual(25L, BenchmarkProtocol.Median(samples));
        }

        [Test]
        public void MedianLongOfSingleElementReturnsThatElement()
        {
            long[] samples = { 7L };
            Assert.AreEqual(7L, BenchmarkProtocol.Median(samples));
        }

        [Test]
        public void MedianLongDoesNotMutateCallerArray()
        {
            long[] samples = { 50L, 10L, 30L, 20L };
            long[] expected = { 50L, 10L, 30L, 20L };
            _ = BenchmarkProtocol.Median(samples);
            CollectionAssert.AreEqual(
                expected,
                samples,
                "Median(long[]) must not mutate (sort) the caller's array."
            );
        }

        [Test]
        public void MedianLongEvenAverageOfTwoLargeValuesDoesNotOverflow()
        {
            // Two equal large values whose naive (a + b) sum overflows long: the overflow-safe
            // integer midpoint must land on the true value, never a wrapped or double-rounded one.
            long[] samples = { long.MaxValue - 1L, long.MaxValue - 1L };
            Assert.AreEqual(long.MaxValue - 1L, BenchmarkProtocol.Median(samples));
        }

        [Test]
        public void MedianOfMeasuredAllRealReturnsPlainMedian()
        {
            // When no sample is the sentinel, MedianOfMeasured behaves exactly like Median.
            long[] samples = { 100L, 300L, 200L };
            Assert.AreEqual(200L, BenchmarkProtocol.MedianOfMeasured(samples));
        }

        [Test]
        public void MedianOfMeasuredFiltersTheUnmeasuredSentinelBeforeMedianing()
        {
            // THE HONESTY FIX (session 068): a byte sample can be Unmeasured (-1) for a single
            // trial that crossed a frame boundary even on a functional backend. Feeding that -1
            // into the plain Median midpoint would launder it into a fabricated magnitude
            // (e.g. Median({-1,100,200,300}) = 150, or Median({-1,100}) = 49). MedianOfMeasured
            // must drop the sentinel and median ONLY the real survivors.
            long[] mixed = { AllocationProbe.Unmeasured, 100L, 200L, 300L };
            Assert.AreEqual(
                200L,
                BenchmarkProtocol.MedianOfMeasured(mixed),
                "MedianOfMeasured must median {100,200,300} (the survivors), not launder -1 into "
                    + "the midpoint arithmetic."
            );

            long[] twoWithSentinel = { AllocationProbe.Unmeasured, 100L };
            Assert.AreEqual(
                100L,
                BenchmarkProtocol.MedianOfMeasured(twoWithSentinel),
                "A single real survivor must be reported as-is, never blended with the sentinel "
                    + "(plain Median would return 49 here)."
            );
        }

        [Test]
        public void MedianOfMeasuredAllSentinelReportsUnmeasured()
        {
            // When EVERY sample is the sentinel (the byte probe is genuinely non-functional on
            // this backend) the honest result is Unmeasured, never a fabricated number.
            long[] allSentinel =
            {
                AllocationProbe.Unmeasured,
                AllocationProbe.Unmeasured,
                AllocationProbe.Unmeasured,
            };
            Assert.AreEqual(
                AllocationProbe.Unmeasured,
                BenchmarkProtocol.MedianOfMeasured(allSentinel)
            );
        }

        [Test]
        public void MedianOfMeasuredThrowsOnEmptyOrNull()
        {
            Assert.Throws<ArgumentException>(() =>
                BenchmarkProtocol.MedianOfMeasured(Array.Empty<long>())
            );
            Assert.Throws<ArgumentNullException>(() => BenchmarkProtocol.MedianOfMeasured(null));
        }

        [Test]
        public void MeasureColdLatencyRunsSetUpTimedAndTearDownPerTrialAndReportsTrials()
        {
            const int trials = 4;
            int setUpCount = 0;
            int timedCount = 0;
            int tearDownCount = 0;

            ColdLatencyMeasurement measurement = BenchmarkProtocol.MeasureColdLatency(
                trials,
                trialIndex =>
                {
                    setUpCount++;
                    return trialIndex;
                },
                _ => timedCount++,
                _ => tearDownCount++
            );

            Assert.AreEqual(trials, setUpCount, "setUpTrial must run once per trial.");
            Assert.AreEqual(trials, timedCount, "timedOperation must run once per trial.");
            Assert.AreEqual(trials, tearDownCount, "tearDownTrial must run once per trial.");
            Assert.AreEqual(
                trials,
                measurement.Trials,
                "MeasureColdLatency must report the trial count it ran."
            );
            Assert.GreaterOrEqual(
                measurement.MedianWallClockMs,
                0d,
                "MeasureColdLatency must report a non-negative median wall clock."
            );
        }

        [Test]
        public void MeasureColdLatencyTearsDownEvenWhenTimedOperationThrows()
        {
            int tearDownCount = 0;

            Assert.Throws<InvalidOperationException>(() =>
                BenchmarkProtocol.MeasureColdLatency<int>(
                    1,
                    _ => 0,
                    _ => throw new InvalidOperationException("Intentional timed-op failure."),
                    _ => tearDownCount++
                )
            );

            Assert.AreEqual(
                1,
                tearDownCount,
                "MeasureColdLatency must dispose trial state even when the timed operation throws."
            );
        }

        // Data-driven over every wall-clock scenario: construction, registration /
        // deregistration floods, marginal registration, and cold first dispatch. Each result
        // reports latency rather than throughput.
        private static IEnumerable<TestCaseData> WallClockScenarioCases()
        {
            yield return new TestCaseData(
                DispatchBenchmarkScenario.MessageBusConstruction1000
            ).SetName("WallClock_MessageBusConstruction1000");
            yield return new TestCaseData(
                DispatchBenchmarkScenario.MessageRegistrationTokenConstruction1000
            ).SetName("WallClock_MessageRegistrationTokenConstruction1000");
            yield return new TestCaseData(
                DispatchBenchmarkScenario.RegistrationFlood1000TypesFromColdBus
            ).SetName("WallClock_RegistrationFloodColdBus");
            yield return new TestCaseData(
                DispatchBenchmarkScenario.RegistrationFlood1000TypesWarmJit
            ).SetName("WallClock_RegistrationFloodWarmJit");
            yield return new TestCaseData(
                DispatchBenchmarkScenario.UntargetedRegistrationMarginal
            ).SetName("WallClock_UntargetedRegistrationMarginal");
            yield return new TestCaseData(
                DispatchBenchmarkScenario.TargetedRegistrationMarginal
            ).SetName("WallClock_TargetedRegistrationMarginal");
            yield return new TestCaseData(
                DispatchBenchmarkScenario.BroadcastRegistrationMarginal
            ).SetName("WallClock_BroadcastRegistrationMarginal");
            yield return new TestCaseData(
                DispatchBenchmarkScenario.DeregistrationFlood1000TypesCold
            ).SetName("WallClock_DeregistrationFloodCold");
            yield return new TestCaseData(
                DispatchBenchmarkScenario.DeregistrationFlood1000TypesWarmJit
            ).SetName("WallClock_DeregistrationFloodWarmJit");
            yield return new TestCaseData(
                DispatchBenchmarkScenario.UntargetedFirstDispatchCold
            ).SetName("WallClock_UntargetedFirstDispatchCold");
            yield return new TestCaseData(
                DispatchBenchmarkScenario.TargetedFirstDispatchCold
            ).SetName("WallClock_TargetedFirstDispatchCold");
            yield return new TestCaseData(
                DispatchBenchmarkScenario.BroadcastFirstDispatchCold
            ).SetName("WallClock_BroadcastFirstDispatchCold");
        }

        [Test]
        public void ConstructionScenariosKeepStableBatchKeysAndHonestLabels()
        {
            Assert.AreEqual(1000, DispatchThroughputBenchmarks.ConstructionBatchSize);
            Assert.AreEqual(
                "MessageBusConstruction_1000",
                DispatchBenchmarkScenarios.Key(DispatchBenchmarkScenario.MessageBusConstruction1000)
            );
            Assert.AreEqual(
                "Message Bus Construction (1000)",
                DispatchBenchmarkScenarios.DisplayName(
                    DispatchBenchmarkScenario.MessageBusConstruction1000
                )
            );
            Assert.AreEqual(
                "MessageRegistrationTokenConstruction_1000_PrebuiltHandlerAndBus",
                DispatchBenchmarkScenarios.Key(
                    DispatchBenchmarkScenario.MessageRegistrationTokenConstruction1000
                )
            );
            Assert.AreEqual(
                "Registration Token Construction (1000, Prebuilt Handler + Bus)",
                DispatchBenchmarkScenarios.DisplayName(
                    DispatchBenchmarkScenario.MessageRegistrationTokenConstruction1000
                )
            );
        }

        // Result-shape lock: every cold/warm-JIT latency scenario reports zero throughput
        // (the time lives in WallClockMs) and is flagged as a wall-clock scenario. The
        // emitsPerSecond=0 property is exactly what auto-excludes these rows from the JS
        // regression gate, so this guards the contract the CI gate relies on. Runs a real
        // measurement, so it carries the PerfBench category.
        [Test, Category("PerfBench")]
        [TestCaseSource(nameof(WallClockScenarioCases))]
        public void WallClockScenarioResultReportsZeroThroughputAndIsWallClock(
            DispatchBenchmarkScenario scenario
        )
        {
            DispatchBenchmarkResult result = DispatchThroughputBenchmarks.RunScenario(
                scenario,
                logResult: false
            );

            Assert.AreEqual(
                0d,
                result.EmitsPerSecond,
                $"Wall-clock scenario '{scenario}' must report zero throughput (the time lives in WallClockMs)."
            );
            Assert.IsTrue(
                result.IsWallClockScenario,
                $"Wall-clock scenario '{scenario}' must be flagged IsWallClockScenario so renderers/gate treat it as a wall-clock row."
            );
            Assert.GreaterOrEqual(
                result.WallClockMs,
                0d,
                $"Wall-clock scenario '{scenario}' must report a non-negative wall-clock measurement."
            );
        }

        // Direction sanity: the warm-JIT registration flood pre-pays the Mono JIT bill on a
        // throwaway bus, so its timed pass must not exceed the cold flood (which times JIT +
        // registration together). Under IL2CPP/AOT the generics are precompiled so the two
        // are ~equal; this asserts DIRECTION only, with generous slack, never strict <. The
        // cold flood runs FIRST so the shared closed generics are JIT-warm for both timed
        // passes, keeping the comparison stable in EditMode under Mono.
        [Test, Category("PerfBench")]
        public void WarmJitRegistrationFloodDoesNotExceedColdFloodWithSlack()
        {
            DispatchBenchmarkResult cold = DispatchThroughputBenchmarks.RunScenario(
                DispatchBenchmarkScenario.RegistrationFlood1000TypesFromColdBus,
                logResult: false
            );
            DispatchBenchmarkResult warmJit = DispatchThroughputBenchmarks.RunScenario(
                DispatchBenchmarkScenario.RegistrationFlood1000TypesWarmJit,
                logResult: false
            );

            // Generous slack absorbs run-to-run jitter on the small wall-clock numbers; the
            // contract under test is only that warm JIT is not categorically SLOWER than
            // cold. A tiny absolute floor avoids a near-zero cold measurement making the
            // bound impossibly tight.
            const double Slack = 5d;
            const double FloorMs = 1d;
            double allowedMs = Math.Max(cold.WallClockMs, FloorMs) * Slack;
            Assert.LessOrEqual(
                warmJit.WallClockMs,
                allowedMs,
                $"Warm-JIT registration flood ({warmJit.WallClockMs:F3} ms) must not exceed the cold flood ({cold.WallClockMs:F3} ms) beyond {Slack:0}x slack."
            );
        }

        // Direction sanity for the deregistration floods, mirroring the registration check:
        // the warm-JIT flood pre-pays the Mono JIT bill (it registers AND deregisters once on
        // a throwaway bus), so its timed UnregisterAll must not exceed the cold flood (which
        // times the JIT compile of the deregistration path + the teardown together). Under
        // IL2CPP/AOT the two are ~equal; this asserts DIRECTION only, with generous slack,
        // never strict <. The cold flood runs FIRST so the shared closed generics are JIT-warm
        // for both timed passes, keeping the comparison stable in EditMode under Mono.
        [Test, Category("PerfBench")]
        public void WarmJitDeregistrationFloodDoesNotExceedColdFloodWithSlack()
        {
            DispatchBenchmarkResult cold = DispatchThroughputBenchmarks.RunScenario(
                DispatchBenchmarkScenario.DeregistrationFlood1000TypesCold,
                logResult: false
            );
            DispatchBenchmarkResult warmJit = DispatchThroughputBenchmarks.RunScenario(
                DispatchBenchmarkScenario.DeregistrationFlood1000TypesWarmJit,
                logResult: false
            );

            // Generous slack absorbs run-to-run jitter on the small wall-clock numbers; the
            // contract under test is only that warm JIT is not categorically SLOWER than
            // cold. A tiny absolute floor avoids a near-zero cold measurement making the
            // bound impossibly tight.
            const double Slack = 5d;
            const double FloorMs = 1d;
            double allowedMs = Math.Max(cold.WallClockMs, FloorMs) * Slack;
            Assert.LessOrEqual(
                warmJit.WallClockMs,
                allowedMs,
                $"Warm-JIT deregistration flood ({warmJit.WallClockMs:F3} ms) must not exceed the cold flood ({cold.WallClockMs:F3} ms) beyond {Slack:0}x slack."
            );
        }

        // The "every scenario captures GC allocations + bytes + CSV stays 8 columns" lock. This
        // runs the real 5s measurement window per scenario, so it carries the PerfBench category
        // and stays out of the fast metadata gate above. Because this runs in the Editor (where
        // both the GC.Alloc recorder and the "GC Allocated In Frame" byte counter are functional),
        // it also doubles as the regression guard for the dead-allocation-API bug: a non-functional
        // probe would make GcAllocations the Unmeasured sentinel and trip the IsFunctional
        // assertion. The byte column (index 7) is the gcAllocatedBytes companion appended in
        // session 068.
        [Test, Category("PerfBench")]
        [TestCaseSource(nameof(DispatchScenarioCases))]
        public void DispatchScenarioRunEmitsEightColumnCsvWithGcAllocationsAndBytes(
            DispatchBenchmarkScenario scenario
        )
        {
            Assert.IsTrue(
                AllocationProbe.IsFunctional,
                "The GC.Alloc allocation probe must be functional in the Editor; a non-functional "
                    + "probe means the benchmark allocation column would be the Unmeasured sentinel "
                    + "(the dead GC.GetAllocatedBytesForCurrentThread() bug this metric replaced)."
            );

            DispatchBenchmarkResult result = DispatchThroughputBenchmarks.RunScenario(
                scenario,
                logResult: false
            );

            string csvRow = result.ToCsvRow();
            string[] fields = csvRow.Split(',');
            Assert.AreEqual(
                8,
                fields.Length,
                $"Scenario '{scenario}' CSV row must stay exactly 8 columns "
                    + $"(gcAllocatedBytes appended as the last column). Row: '{csvRow}'."
            );
            Assert.IsNotEmpty(
                fields[5],
                $"Scenario '{scenario}' must populate the gc-allocations field (index 5). Row: '{csvRow}'."
            );
            Assert.IsNotEmpty(
                fields[7],
                $"Scenario '{scenario}' must populate the gc-allocated-bytes field (index 7). Row: '{csvRow}'."
            );
            // Functional in the Editor, so the count is a real non-negative measurement (never
            // the Unmeasured sentinel here).
            Assert.GreaterOrEqual(
                result.GcAllocations,
                0,
                $"Scenario '{scenario}' must report a non-negative GC allocation count in the Editor."
            );
            // The "GC Allocated In Frame" byte counter is functional in the Editor too, so bytes
            // are a real non-negative measurement here (never the Unmeasured sentinel).
            Assert.IsTrue(
                AllocationProbe.BytesFunctional,
                "The 'GC Allocated In Frame' byte counter must be functional in the Editor; a "
                    + "non-functional counter means the benchmark byte column would be the "
                    + "Unmeasured sentinel rather than a measured magnitude."
            );
            Assert.GreaterOrEqual(
                result.GcAllocatedBytes,
                0,
                $"Scenario '{scenario}' must report a non-negative GC allocated-bytes value in the Editor."
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
