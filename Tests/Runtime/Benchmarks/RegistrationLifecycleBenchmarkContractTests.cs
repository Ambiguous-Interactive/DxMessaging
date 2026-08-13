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
            MethodInfo attributionMethod = typeof(DispatchThroughputBenchmarks).GetMethod(
                nameof(DispatchThroughputBenchmarks.DeregistrationAttributionBenchmark)
            );

            Assert.IsNotNull(dispatchMethod);
            Assert.IsNotNull(attributionMethod);
            Assert.AreEqual(typeof(DispatchThroughputBenchmarks), dispatchMethod.DeclaringType);
            Assert.AreEqual(typeof(DispatchThroughputBenchmarks), attributionMethod.DeclaringType);

            OrderAttribute dispatchOrder = dispatchMethod.GetCustomAttribute<OrderAttribute>();
            OrderAttribute attributionOrder =
                attributionMethod.GetCustomAttribute<OrderAttribute>();
            Assert.IsNotNull(dispatchOrder);
            Assert.IsNotNull(attributionOrder);
            Assert.AreEqual(
                DispatchThroughputBenchmarks.PublishedDispatchOrder,
                dispatchOrder.Order
            );
            Assert.AreEqual(
                DispatchThroughputBenchmarks.DeregistrationAttributionOrder,
                attributionOrder.Order
            );
            Assert.Less(
                dispatchOrder.Order,
                attributionOrder.Order,
                "Published dispatch windows must complete before high-cardinality attribution."
            );

            MethodInfo[] independentAttributionEntries = typeof(DeregistrationAttributionBenchmarks)
                .GetMethods(
                    BindingFlags.Public
                        | BindingFlags.NonPublic
                        | BindingFlags.Static
                        | BindingFlags.Instance
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
