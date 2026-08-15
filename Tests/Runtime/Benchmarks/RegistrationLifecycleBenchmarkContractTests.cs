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

            Assert.IsNotNull(dispatchMethod, "Published dispatch entry point must exist.");
            Assert.IsNotNull(
                registrationAttributionMethod,
                "Registration attribution entry point must exist."
            );
            Assert.IsNotNull(
                deregistrationAttributionMethod,
                "Deregistration attribution entry point must exist."
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
            Assert.IsNotNull(dispatchOrder, "Published dispatch entry must declare its order.");
            Assert.IsNotNull(
                registrationAttributionOrder,
                "Registration attribution entry must declare its order."
            );
            Assert.IsNotNull(
                deregistrationAttributionOrder,
                "Deregistration attribution entry must declare its order."
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
