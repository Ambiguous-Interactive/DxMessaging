#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Benchmarks
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using NUnit.Framework;

    public sealed class RegistrationLifecycleBenchmarkContractTests
    {
        [Test, Category("PerfBench")]
        [TestCaseSource(nameof(LifecycleOperationCases))]
        public void PreparedLifecycleOperationProducesExactPublicBehavior(
            RegistrationLifecycleOperation operation,
            int expectedPrimaryRegistrations,
            int expectedSecondaryRegistrations,
            int expectedInvocations
        )
        {
            const int Cardinality = 4;
            RegistrationLifecycleObservation observation =
                RegistrationLifecycleBenchmarks.ExecuteOnceForContract(operation, Cardinality);

            Assert.AreEqual(expectedPrimaryRegistrations, observation.PrimaryRegistrations);
            Assert.AreEqual(expectedSecondaryRegistrations, observation.SecondaryRegistrations);
            Assert.AreEqual(expectedInvocations, observation.HandlerInvocations);
        }

        [Test, Category("PerfBench")]
        public void LifecycleMatrixContainsEveryOperationAtEveryRequiredCardinality()
        {
            int[] cardinalities = { 1, 4, 16, 1000 };
            RegistrationLifecycleOperation[] operations =
            {
                RegistrationLifecycleOperation.Stage,
                RegistrationLifecycleOperation.Enable,
                RegistrationLifecycleOperation.Disable,
                RegistrationLifecycleOperation.ReEnable,
                RegistrationLifecycleOperation.Remove,
                RegistrationLifecycleOperation.Retarget,
                RegistrationLifecycleOperation.Dispose,
            };
            CollectionAssert.AreEqual(
                operations,
                (RegistrationLifecycleOperation[])
                    Enum.GetValues(typeof(RegistrationLifecycleOperation))
            );

            RegistrationLifecycleBenchmarkCase[] actual =
                RegistrationLifecycleScenarios.All.ToArray();
            Assert.AreEqual(operations.Length * cardinalities.Length, actual.Length);
            CollectionAssert.AllItemsAreUnique(actual.Select(benchmarkCase => benchmarkCase.Key));

            foreach (RegistrationLifecycleOperation operation in operations)
            {
                foreach (int cardinality in cardinalities)
                {
                    Assert.That(
                        actual.Count(benchmarkCase =>
                            benchmarkCase.Operation == operation
                            && benchmarkCase.Cardinality == cardinality
                        ),
                        Is.EqualTo(1),
                        $"Expected exactly one lifecycle case for {operation}/{cardinality}."
                    );
                }
            }
        }

        [Test]
        [Category("PerfBench")]
        [TestCase(RegistrationAttributionOperation.DirectBus, 1, 0, 0, 0)]
        [TestCase(RegistrationAttributionOperation.DirectHandler, 1, 1, 0, 1)]
        [TestCase(RegistrationAttributionOperation.TokenStage, 0, 0, 1, 0)]
        [TestCase(RegistrationAttributionOperation.TokenActive, 1, 1, 1, 1)]
        public void RegistrationAttributionProducesExactLayerState(
            RegistrationAttributionOperation operation,
            int expectedBusRegistrations,
            int expectedHandlerRegistrations,
            int expectedTokenRegistrations,
            int expectedInvocations
        )
        {
            RegistrationAttributionObservation observation =
                RegistrationAttributionBenchmarks.ExecuteOnceForContract(operation);

            Assert.AreEqual(
                operation,
                observation.Operation,
                $"{operation}: observation operation drifted."
            );
            Assert.AreEqual(
                expectedBusRegistrations,
                observation.Live.BusRegistrations,
                $"{operation}: live bus layer state drifted."
            );
            Assert.AreEqual(
                expectedHandlerRegistrations,
                observation.Live.HandlerRegistrations,
                $"{operation}: live handler layer state drifted."
            );
            Assert.AreEqual(
                expectedTokenRegistrations,
                observation.Live.TokenRegistrations,
                $"{operation}: live token layer state drifted."
            );
            Assert.AreEqual(
                expectedInvocations,
                observation.Live.HandlerInvocations,
                $"{operation}: live delivery count drifted."
            );
            Assert.AreEqual(
                0,
                observation.Final.BusRegistrations,
                $"{operation}: final bus state must be empty."
            );
            Assert.AreEqual(
                0,
                observation.Final.HandlerRegistrations,
                $"{operation}: final handler state must be empty."
            );
            Assert.AreEqual(
                0,
                observation.Final.TokenRegistrations,
                $"{operation}: final token state must be empty."
            );
            Assert.AreEqual(
                0,
                observation.Final.HandlerInvocations,
                $"{operation}: final state must not deliver."
            );
        }

        [Test]
        [Category("PerfBench")]
        public void RegistrationAttributionScenarioKeysCoverEveryOperationAtFixedCycleCount()
        {
            RegistrationAttributionOperation[] operations =
            {
                RegistrationAttributionOperation.DirectBus,
                RegistrationAttributionOperation.DirectHandler,
                RegistrationAttributionOperation.TokenStage,
                RegistrationAttributionOperation.TokenActive,
            };
            CollectionAssert.AreEqual(
                operations,
                (RegistrationAttributionOperation[])
                    Enum.GetValues(typeof(RegistrationAttributionOperation)),
                "Registration attribution cases must cover every operation in declaration order."
            );
            Assert.AreEqual(
                131_072,
                RegistrationAttributionBenchmarks.CycleCount,
                "Registration attribution must retain its fixed cycle count."
            );
            Assert.AreEqual(
                BenchmarkProtocol.BatchSize,
                RegistrationAttributionBenchmarks.AllocationCycleCount,
                "Registration attribution and comparison allocation batches must stay comparable."
            );

            string[] keys = operations
                .Select(RegistrationAttributionBenchmarks.ScenarioKey)
                .ToArray();
            CollectionAssert.AllItemsAreUnique(
                keys,
                "Registration attribution scenario keys must be unique."
            );
            foreach (RegistrationAttributionOperation operation in operations)
            {
                StringAssert.Contains(
                    operation.ToString(),
                    RegistrationAttributionBenchmarks.ScenarioKey(operation),
                    $"{operation}: scenario key must name the measured registration layer."
                );
                StringAssert.EndsWith(
                    "_131072",
                    RegistrationAttributionBenchmarks.ScenarioKey(operation),
                    $"{operation}: scenario key must encode the fixed cycle count."
                );
            }
        }

        [Test]
        [Category("PerfBench")]
        [TestCase(RegistrationAttributionOperation.DirectBus)]
        [TestCase(RegistrationAttributionOperation.DirectHandler)]
        [TestCase(RegistrationAttributionOperation.TokenStage)]
        [TestCase(RegistrationAttributionOperation.TokenActive)]
        public void RegistrationAttributionSharedCyclePathSupportsTwoConsecutiveBatches(
            RegistrationAttributionOperation operation
        )
        {
            RegistrationAttributionBenchmarks.ExecuteCyclesForContract(operation, cycleCount: 257);
        }

        [Test]
        [Category("PerfBench")]
        [TestCase(DeregistrationAttributionOperation.DirectBus)]
        [TestCase(DeregistrationAttributionOperation.DirectHandler)]
        [TestCase(DeregistrationAttributionOperation.TokenRemove)]
        [TestCase(DeregistrationAttributionOperation.TokenDisable)]
        public void DeregistrationAttributionProducesExactTeardownBehavior(
            DeregistrationAttributionOperation operation
        )
        {
            const int Cardinality = 16;
            DeregistrationAttributionObservation observation =
                DeregistrationAttributionBenchmarks.ExecuteOnceForContract(operation, Cardinality);

            Assert.AreEqual(
                operation,
                observation.Operation,
                $"{operation}/{Cardinality}: observation operation drifted."
            );
            Assert.AreEqual(
                Cardinality,
                observation.Cardinality,
                $"{operation}/{Cardinality}: observation cardinality drifted."
            );
            Assert.AreEqual(
                0,
                observation.BusRegistrations,
                $"{operation}/{Cardinality}: teardown left a bus registration live."
            );
            Assert.AreEqual(
                0,
                observation.HandlerRegistrations,
                $"{operation}/{Cardinality}: teardown left a handler registration live."
            );
            Assert.AreEqual(
                0,
                observation.HandlerInvocations,
                $"{operation}/{Cardinality}: teardown still dispatched a handler."
            );
        }

        [Test]
        [Category("PerfBench")]
        public void DeregistrationAttributionScenarioKeysCoverEveryOperationAtCalibratedCardinality()
        {
            DeregistrationAttributionOperation[] operations =
            {
                DeregistrationAttributionOperation.DirectBus,
                DeregistrationAttributionOperation.DirectHandler,
                DeregistrationAttributionOperation.TokenRemove,
                DeregistrationAttributionOperation.TokenDisable,
            };
            CollectionAssert.AreEqual(
                operations,
                (DeregistrationAttributionOperation[])
                    Enum.GetValues(typeof(DeregistrationAttributionOperation))
            );
            Assert.AreEqual(131_072, DeregistrationAttributionBenchmarks.Cardinality);

            string[] keys = operations
                .Select(DeregistrationAttributionBenchmarks.ScenarioKey)
                .ToArray();
            CollectionAssert.AllItemsAreUnique(keys);
            foreach (DeregistrationAttributionOperation operation in operations)
            {
                StringAssert.Contains(
                    operation.ToString(),
                    DeregistrationAttributionBenchmarks.ScenarioKey(operation),
                    $"{operation}: scenario key must name the measured teardown layer."
                );
                StringAssert.EndsWith(
                    "_131072",
                    DeregistrationAttributionBenchmarks.ScenarioKey(operation),
                    $"{operation}: scenario key must encode the calibrated cardinality."
                );
            }
        }

        [Test]
        [Category("PerfBench")]
        [TestCaseSource(nameof(DeregistrationPalindromeCases))]
        public void DeregistrationPalindromeClassifiesEveryEvidenceBoundary(
            double handlerA,
            double busA,
            double busB,
            double handlerB,
            int expectedClassification
        )
        {
            DeregistrationAttributionPalindromeDiagnostic diagnostic = AnalyzePalindrome(
                handlerA,
                busA,
                busB,
                handlerB
            );
            int actualClassification =
                (diagnostic.HandlerDriftWithinThreshold ? 1 : 0)
                | (diagnostic.BusDriftWithinThreshold ? 2 : 0)
                | (diagnostic.HandlerExcessSpreadWithinThreshold ? 4 : 0)
                | (diagnostic.Interpretable ? 8 : 0);
            string caseContext =
                $"handlerA={handlerA}, busA={busA}, busB={busB}, handlerB={handlerB}, "
                + $"expectedClassification={expectedClassification}, "
                + $"actualEvidence={diagnostic.ToStructuredLog()}";

            Assert.AreEqual(
                expectedClassification,
                actualClassification,
                $"Palindrome classification bits changed (handler=1, bus=2, excess=4, interpretable=8). {caseContext}"
            );
        }

        [Test]
        [Category("PerfBench")]
        public void DeregistrationPalindromeIsArmSwapInvariant()
        {
            DeregistrationAttributionPalindromeDiagnostic first = AnalyzePalindrome(
                198.5d,
                100d,
                101d,
                200.5d
            );
            DeregistrationAttributionPalindromeDiagnostic swapped = AnalyzePalindrome(
                200.5d,
                101d,
                100d,
                198.5d
            );

            Assert.AreEqual(
                first.HandlerDriftPercent,
                swapped.HandlerDriftPercent,
                "Swapping palindrome arms must not change handler drift."
            );
            Assert.AreEqual(
                first.BusDriftPercent,
                swapped.BusDriftPercent,
                "Swapping palindrome arms must not change direct-bus drift."
            );
            Assert.AreEqual(
                first.HandlerExcessSpreadPercent,
                swapped.HandlerExcessSpreadPercent,
                "Swapping palindrome arms must not change additive excess spread."
            );
            Assert.AreEqual(
                first.Interpretable,
                swapped.Interpretable,
                "Swapping palindrome arms must not change interpretability."
            );
        }

        [Test]
        [Category("PerfBench")]
        public void DeregistrationPalindromeLogRejectsAcceptanceMeaning()
        {
            DeregistrationAttributionPalindromeDiagnostic diagnostic = AnalyzePalindrome(
                130d,
                100d,
                101d,
                131d
            );
            string evidence = diagnostic.ToStructuredLog();

            Assert.AreEqual(
                30d,
                diagnostic.CenteredHandlerExcess,
                "The diagnostic must arithmetically center additive handler excess without overflow."
            );
            StringAssert.StartsWith(
                "DXM_DEREGISTRATION_ATTRIBUTION_PALINDROME ",
                evidence,
                "The durable diagnostic marker changed."
            );
            StringAssert.Contains(
                "maxSamePathDriftPercent=3",
                evidence,
                "The log must retain the same-path invalidation threshold."
            );
            StringAssert.Contains(
                "maxHandlerExcessSpreadPercent=3",
                evidence,
                "The log must retain the excess-spread invalidation threshold."
            );
            StringAssert.Contains(
                "handlerDriftWithinThreshold=true busDriftWithinThreshold=true handlerExcessSpreadWithinThreshold=true",
                evidence,
                "The log must retain each threshold decision."
            );
            StringAssert.Contains(
                "handlerFirstPairTotal_ms=230 busFirstPairTotal_ms=232 palindromeTotal_ms=462 selectedTrial=-1 prepareForward=false jointTrialSelection=false sameTrialArms=false preparationDirectionAlternated=false timingTrials=0 trialSequence=none diagnosticOnly=true acceptanceEvidence=false candidateCompared=false interpretable=true",
                evidence,
                "Arithmetic-only diagnostics must not claim measured selection provenance."
            );
            StringAssert.DoesNotContain(
                " valid=",
                evidence,
                "The retired validity label returned."
            );
        }

        [Test]
        [Category("PerfBench")]
        public void DeregistrationPalindromeLogDistinguishesExactAndJustOverThreshold()
        {
            string exact = AnalyzePalindrome(1100d, 100d, 103d, 1103d).ToStructuredLog();
            string justOver = AnalyzePalindrome(1100d, 100d, 103.000001d, 1103.000001d)
                .ToStructuredLog();

            Assert.AreNotEqual(
                exact,
                justOver,
                "Round-trip evidence must distinguish exact and just-over threshold inputs."
            );
            StringAssert.Contains(
                "busDriftWithinThreshold=true",
                exact,
                "The exact threshold must remain included."
            );
            StringAssert.Contains(
                "interpretable=true",
                exact,
                "The exact threshold sample must remain interpretable."
            );
            StringAssert.Contains(
                "busDriftWithinThreshold=false",
                justOver,
                "The just-over threshold must remain excluded."
            );
            StringAssert.Contains(
                "interpretable=false",
                justOver,
                "The just-over threshold sample must remain uninterpretable."
            );
        }

        [Test]
        [Category("PerfBench")]
        [TestCase(false)]
        [TestCase(true)]
        public void DeregistrationPalindromeFloorKeepsAllArmsFromOneTrial(bool prepareForward)
        {
            DeregistrationAttributionPalindromeSample current = PalindromeSample(
                120d,
                10d,
                9d,
                121d,
                trial: 2,
                prepareForward
            );
            DeregistrationAttributionPalindromeSample mixedArmTrap = PalindromeSample(
                100d,
                40d,
                50d,
                90d,
                trial: 3,
                !prepareForward
            );
            DeregistrationAttributionPalindromeSample faster = PalindromeSample(
                121d,
                8d,
                8d,
                120d,
                trial: 4,
                !prepareForward
            );

            DeregistrationAttributionPalindromeSample kept =
                DeregistrationAttributionBenchmarks.SelectPalindromeFloor(current, mixedArmTrap);
            AssertPalindromeSample(
                current,
                kept,
                $"prepareForward={prepareForward}: retained sample"
            );

            DeregistrationAttributionPalindromeSample replaced =
                DeregistrationAttributionBenchmarks.SelectPalindromeFloor(current, faster);
            AssertPalindromeSample(
                faster,
                replaced,
                $"prepareForward={prepareForward}: replacement sample"
            );
        }

        [Test]
        [Category("PerfBench")]
        [TestCase(0, DeregistrationAttributionOperation.DirectHandler)]
        [TestCase(1, DeregistrationAttributionOperation.DirectBus)]
        [TestCase(2, DeregistrationAttributionOperation.DirectBus)]
        [TestCase(3, DeregistrationAttributionOperation.DirectHandler)]
        public void DeregistrationPalindromeExecutionOrderIsSymmetric(
            int armIndex,
            DeregistrationAttributionOperation expectedOperation
        )
        {
            Assert.AreEqual(
                expectedOperation,
                DeregistrationAttributionBenchmarks.PalindromeOperationAt(armIndex),
                $"armIndex={armIndex}, expectedOperation={expectedOperation}: the timed execution sequence must remain H/B/B/H."
            );
        }

        [Test]
        [Category("PerfBench")]
        [TestCase(-1)]
        [TestCase(4)]
        public void DeregistrationPalindromeRejectsUnknownArmIndex(int armIndex)
        {
            ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
                DeregistrationAttributionBenchmarks.PalindromeOperationAt(armIndex)
            );
            Assert.AreEqual(
                nameof(armIndex),
                exception.ParamName,
                $"armIndex={armIndex}: the diagnostic must reject an arm outside H/B/B/H."
            );
        }

        [Test]
        public void PublishedDispatchAndAttributionUseSameSupportedMethodOrderBoundary()
        {
            MethodInfo dispatchMethod = typeof(DispatchThroughputBenchmarks).GetMethod(
                nameof(DispatchThroughputBenchmarks.DispatchBenchmark)
            );
            MethodInfo registrationAttributionMethod =
                typeof(DispatchThroughputBenchmarks).GetMethod(
                    nameof(DispatchThroughputBenchmarks.RegistrationAttributionBenchmark)
                );
            MethodInfo deregistrationAttributionMethod =
                typeof(DispatchThroughputBenchmarks).GetMethod(
                    nameof(DispatchThroughputBenchmarks.DeregistrationAttributionBenchmark)
                );
            MethodInfo deregistrationDiagnosticMethod =
                typeof(DispatchThroughputBenchmarks).GetMethod(
                    nameof(
                        DispatchThroughputBenchmarks.DirectHandlerAndBusDeregistrationPalindromeDiagnostic
                    )
                );
            MethodInfo dispatchDiagnosticMethod = typeof(DispatchThroughputBenchmarks).GetMethod(
                nameof(DispatchThroughputBenchmarks.DirectAndTokenDispatchTwinPalindromeDiagnostic)
            );

            Assert.IsNotNull(dispatchMethod, "Published dispatch entry point must exist.");
            Assert.IsNotNull(
                registrationAttributionMethod,
                "Registration attribution entry point must exist."
            );
            Assert.IsNotNull(
                deregistrationAttributionMethod,
                "Deregistration attribution entry point must exist."
            );
            Assert.IsNotNull(
                deregistrationDiagnosticMethod,
                "Deregistration palindrome entry point must exist."
            );
            Assert.IsNotNull(
                dispatchDiagnosticMethod,
                "Dispatch palindrome entry point must exist."
            );
            Assert.AreEqual(
                typeof(DispatchThroughputBenchmarks),
                dispatchMethod.DeclaringType,
                "Published dispatch entry must remain on the ordered benchmark fixture."
            );
            Assert.AreEqual(
                typeof(DispatchThroughputBenchmarks),
                registrationAttributionMethod.DeclaringType,
                "Registration attribution entry must remain on the ordered benchmark fixture."
            );
            Assert.AreEqual(
                typeof(DispatchThroughputBenchmarks),
                deregistrationAttributionMethod.DeclaringType,
                "Deregistration attribution entry must remain on the ordered benchmark fixture."
            );

            OrderAttribute dispatchOrder = dispatchMethod.GetCustomAttribute<OrderAttribute>();
            OrderAttribute registrationAttributionOrder =
                registrationAttributionMethod.GetCustomAttribute<OrderAttribute>();
            OrderAttribute deregistrationAttributionOrder =
                deregistrationAttributionMethod.GetCustomAttribute<OrderAttribute>();
            OrderAttribute deregistrationDiagnosticOrder =
                deregistrationDiagnosticMethod.GetCustomAttribute<OrderAttribute>();
            OrderAttribute dispatchDiagnosticOrder =
                dispatchDiagnosticMethod.GetCustomAttribute<OrderAttribute>();
            Assert.IsNotNull(dispatchOrder, "Published dispatch entry must declare its order.");
            Assert.IsNotNull(
                registrationAttributionOrder,
                "Registration attribution entry must declare its order."
            );
            Assert.IsNotNull(
                deregistrationAttributionOrder,
                "Deregistration attribution entry must declare its order."
            );
            Assert.IsNotNull(
                deregistrationDiagnosticOrder,
                "Deregistration palindrome must declare its order."
            );
            Assert.IsNotNull(
                dispatchDiagnosticOrder,
                "Dispatch palindrome must declare its order."
            );
            Assert.AreEqual(
                DispatchThroughputBenchmarks.PublishedDispatchOrder,
                dispatchOrder.Order,
                "Published dispatch order drifted."
            );
            Assert.AreEqual(
                DispatchThroughputBenchmarks.RegistrationAttributionOrder,
                registrationAttributionOrder.Order,
                "Registration attribution order drifted."
            );
            Assert.AreEqual(
                DispatchThroughputBenchmarks.DeregistrationAttributionOrder,
                deregistrationAttributionOrder.Order,
                "Deregistration attribution order drifted."
            );
            CollectionAssert.AreEqual(
                new[] { 0, 1, 2 },
                new[]
                {
                    dispatchOrder.Order,
                    registrationAttributionOrder.Order,
                    deregistrationAttributionOrder.Order,
                },
                "Published dispatch and attribution rows must retain one deterministic boundary."
            );
            CollectionAssert.AreEqual(
                new[] { 3, 4 },
                new[] { deregistrationDiagnosticOrder.Order, dispatchDiagnosticOrder.Order },
                "Diagnostic palindromes must run immediately after published attribution rows."
            );

            Type[] attributionTypes =
            {
                typeof(RegistrationAttributionBenchmarks),
                typeof(DeregistrationAttributionBenchmarks),
            };
            MethodInfo[] independentAttributionEntries = attributionTypes
                .SelectMany(type =>
                    type.GetMethods(
                        BindingFlags.Public
                            | BindingFlags.NonPublic
                            | BindingFlags.Static
                            | BindingFlags.Instance
                    )
                )
                .Where(method => method.GetCustomAttribute<TestAttribute>() != null)
                .ToArray();
            Assert.IsEmpty(
                independentAttributionEntries,
                "Attribution must not regain an independently scheduled benchmark fixture entry."
            );
        }

        private static IEnumerable<TestCaseData> DeregistrationPalindromeCases()
        {
            yield return PalindromeCase("Stable", 130d, 100d, 101d, 131d, 15);
            yield return PalindromeCase("HandlerOnlyDrift", 101d, 1d, 1d, 104.04d, 6);
            yield return PalindromeCase("BusOnlyDrift", 1100d, 100d, 103.1d, 1103.1d, 5);
            yield return PalindromeCase("SamePathExactThreshold", 1100d, 100d, 103d, 1103d, 15);
            yield return PalindromeCase(
                "SamePathJustOverThreshold",
                1100d,
                100d,
                103.000001d,
                1103.000001d,
                5
            );
            yield return PalindromeCase("ExcessExactThreshold", 198.5d, 100d, 100d, 201.5d, 15);
            yield return PalindromeCase(
                "ExcessJustOverThreshold",
                198.5d,
                100d,
                100d,
                201.500001d,
                3
            );
            yield return PalindromeCase("ZeroDuration", 0d, 1d, 1d, 2d, 2);
            yield return PalindromeCase("NegativeDuration", -1d, 1d, 1d, 2d, 2);
            yield return PalindromeCase("ZeroExcess", 1d, 1d, 1d, 1d, 3);
            yield return PalindromeCase("NegativeExcess", 1d, 2d, 2d, 1d, 3);
            yield return PalindromeCase("NaNDuration", double.NaN, 1d, 1d, 2d, 2);
            yield return PalindromeCase("InfiniteDuration", double.PositiveInfinity, 1d, 1d, 2d, 2);
            yield return PalindromeCase(
                "FiniteMeanOverflow",
                double.MaxValue,
                1d,
                1d,
                double.MaxValue,
                15
            );
        }

        private static DeregistrationAttributionPalindromeDiagnostic AnalyzePalindrome(
            double handlerA,
            double busA,
            double busB,
            double handlerB
        )
        {
            return DeregistrationAttributionBenchmarks.AnalyzePalindrome(
                handlerA,
                busA,
                busB,
                handlerB
            );
        }

        private static DeregistrationAttributionPalindromeSample PalindromeSample(
            double handlerA,
            double busA,
            double busB,
            double handlerB,
            int trial,
            bool prepareForward
        )
        {
            return new DeregistrationAttributionPalindromeSample(
                handlerA,
                busA,
                busB,
                handlerB,
                trial,
                prepareForward
            );
        }

        private static void AssertPalindromeSample(
            DeregistrationAttributionPalindromeSample expected,
            DeregistrationAttributionPalindromeSample actual,
            string context
        )
        {
            Assert.AreEqual(expected.HandlerA, actual.HandlerA, $"{context}: handler A changed.");
            Assert.AreEqual(expected.BusA, actual.BusA, $"{context}: bus A changed.");
            Assert.AreEqual(expected.BusB, actual.BusB, $"{context}: bus B changed.");
            Assert.AreEqual(expected.HandlerB, actual.HandlerB, $"{context}: handler B changed.");
            Assert.AreEqual(expected.Trial, actual.Trial, $"{context}: trial changed.");
            Assert.AreEqual(
                expected.PrepareForward,
                actual.PrepareForward,
                $"{context}: preparation direction changed."
            );
            Assert.AreEqual(expected.TotalMs, actual.TotalMs, $"{context}: total changed.");
        }

        private static TestCaseData PalindromeCase(
            string name,
            double handlerA,
            double busA,
            double busB,
            double handlerB,
            int expectedClassification
        )
        {
            return new TestCaseData(handlerA, busA, busB, handlerB, expectedClassification).SetName(
                $"DeregistrationPalindrome_{name}"
            );
        }

        private static IEnumerable<TestCaseData> LifecycleOperationCases()
        {
            const int Cardinality = 4;
            yield return new TestCaseData(RegistrationLifecycleOperation.Stage, 1, 0, Cardinality);
            yield return new TestCaseData(RegistrationLifecycleOperation.Enable, 1, 0, Cardinality);
            yield return new TestCaseData(RegistrationLifecycleOperation.Disable, 0, 0, 0);
            yield return new TestCaseData(
                RegistrationLifecycleOperation.ReEnable,
                1,
                0,
                Cardinality
            );
            yield return new TestCaseData(RegistrationLifecycleOperation.Remove, 0, 0, 0);
            yield return new TestCaseData(
                RegistrationLifecycleOperation.Retarget,
                0,
                1,
                Cardinality
            );
            yield return new TestCaseData(RegistrationLifecycleOperation.Dispose, 0, 0, 0);
        }
    }
}
#endif
