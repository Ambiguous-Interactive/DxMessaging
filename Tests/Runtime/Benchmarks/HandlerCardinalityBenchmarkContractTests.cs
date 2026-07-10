#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Benchmarks
{
    using System.Collections.Generic;
    using NUnit.Framework;

    public sealed class HandlerCardinalityBenchmarkContractTests
    {
        [Test]
        public void MatrixCoversEveryOperationAtRequiredCardinalities()
        {
            int[] cardinalities = { 1, 4, 16, 64 };
            HandlerCardinalityOperation[] operations =
            {
                HandlerCardinalityOperation.PriorityDispatch,
                HandlerCardinalityOperation.PriorityChurn,
                HandlerCardinalityOperation.HandlerDispatch,
                HandlerCardinalityOperation.HandlerChurn,
                HandlerCardinalityOperation.SameHandlerDispatch,
                HandlerCardinalityOperation.SameHandlerChurn,
            };

            Assert.AreEqual(
                operations.Length * cardinalities.Length,
                HandlerCardinalityScenarios.All.Count
            );
            foreach (HandlerCardinalityOperation operation in operations)
            {
                foreach (int cardinality in cardinalities)
                {
                    Assert.That(
                        HandlerCardinalityScenarios.All,
                        Has.Exactly(1)
                            .Matches<HandlerCardinalityBenchmarkCase>(benchmarkCase =>
                                benchmarkCase.Operation == operation
                                && benchmarkCase.Cardinality == cardinality
                            )
                    );
                }
            }
        }

        [TestCaseSource(nameof(Cases))]
        public void OneOperationPreservesFanOutAndLiveTopology(
            HandlerCardinalityBenchmarkCase benchmarkCase
        )
        {
            HandlerCardinalityObservation observation =
                HandlerCardinalityBenchmarks.RunOnceForContract(benchmarkCase);
            bool sameHandler =
                benchmarkCase.Operation == HandlerCardinalityOperation.SameHandlerDispatch
                || benchmarkCase.Operation == HandlerCardinalityOperation.SameHandlerChurn;
            Assert.AreEqual(sameHandler ? 1 : benchmarkCase.Cardinality, observation.DispatchCalls);
            Assert.AreEqual(benchmarkCase.Cardinality, observation.LiveRegistrations);
            Assert.AreEqual(1, observation.OccupiedTypeSlots);
        }

        private static IEnumerable<TestCaseData> Cases()
        {
            foreach (
                HandlerCardinalityBenchmarkCase benchmarkCase in HandlerCardinalityScenarios.All
            )
            {
                yield return new TestCaseData(benchmarkCase).SetName(benchmarkCase.Key);
            }
        }
    }
}
#endif
