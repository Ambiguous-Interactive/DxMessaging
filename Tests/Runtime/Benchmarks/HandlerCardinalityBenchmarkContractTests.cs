#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Benchmarks
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using NUnit.Framework;

    public sealed class HandlerCardinalityBenchmarkContractTests
    {
        [Test]
        public void MatrixCoversEveryOperationAtRequiredCardinalities()
        {
            int[] cardinalities = { 1, 2, 3, 4, 5, 8, 9, 16, 64 };
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
            bool distinctPriority =
                benchmarkCase.Operation == HandlerCardinalityOperation.PriorityDispatch
                || benchmarkCase.Operation == HandlerCardinalityOperation.PriorityChurn;
            int expectedHandlerEntries =
                sameHandler || distinctPriority ? 1 : benchmarkCase.Cardinality;
            int expectedPriorityEntries = distinctPriority ? benchmarkCase.Cardinality : 1;
            MessageBus.PriorityStorageObservation busStorage = observation.BusPriorityStorage;
            Assert.AreEqual(expectedPriorityEntries, busStorage.Entries);
            Assert.GreaterOrEqual(busStorage.MapCapacity, busStorage.Entries);
            Assert.GreaterOrEqual(busStorage.OrderCapacity, busStorage.Entries);
            MessageHandler.HandlerCacheStorageObservation storage = observation.Storage;
            Assert.AreEqual(expectedPriorityEntries, storage.PriorityEntries);
            Assert.AreEqual(expectedHandlerEntries, storage.HandlerEntries);
            AssertStorageCoherent(
                storage.PriorityEntries,
                storage.PriorityInlineCapacity,
                storage.PriorityMapCapacity,
                storage.PriorityOrderCapacity,
                storage.PriorityUsesSpillStorage,
                "priority"
            );
            AssertStorageCoherent(
                storage.HandlerEntries,
                storage.HandlerInlineCapacity,
                storage.HandlerMapCapacity,
                storage.HandlerOrderCapacity,
                storage.HandlerUsesSpillStorage,
                "handler"
            );
        }

        [TestCase(2, false)]
        [TestCase(3, true)]
        public void HandlerStorageObservationReportsInlineSpillBoundary(
            int cardinality,
            bool expectedSpill
        )
        {
            HandlerCardinalityObservation observation =
                HandlerCardinalityBenchmarks.RunOnceForContract(
                    new HandlerCardinalityBenchmarkCase(
                        HandlerCardinalityOperation.HandlerDispatch,
                        cardinality
                    )
                );
            MessageHandler.HandlerCacheStorageObservation storage = observation.Storage;
            Assert.AreEqual(cardinality, storage.HandlerEntries);
            Assert.AreEqual(cardinality > 2, expectedSpill);
            if (UsesRegistrationSlotArena)
            {
                Assert.AreEqual(expectedSpill, storage.HandlerUsesSpillStorage);
                Assert.AreEqual(2, storage.HandlerInlineCapacity);
                if (expectedSpill)
                {
                    Assert.GreaterOrEqual(storage.HandlerMapCapacity, cardinality);
                    Assert.GreaterOrEqual(storage.HandlerOrderCapacity, cardinality);
                }
                else
                {
                    Assert.Zero(storage.HandlerMapCapacity);
                    Assert.Zero(storage.HandlerOrderCapacity);
                }
            }
            else
            {
                Assert.IsTrue(storage.HandlerUsesSpillStorage);
                Assert.Zero(storage.HandlerInlineCapacity);
                Assert.GreaterOrEqual(storage.HandlerMapCapacity, cardinality);
                Assert.GreaterOrEqual(storage.HandlerOrderCapacity, cardinality);
            }
        }

        private static bool UsesRegistrationSlotArena =>
            typeof(MessageRegistrationHandle).GetProperty(
                "Slot",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
            ) != null;

        [Test]
        public void ResultSchemaKeepsCsvAndStructuredLogTopologyAligned()
        {
            HandlerCardinalityBenchmarkResult legacyResult = new(
                new HandlerCardinalityBenchmarkCase(HandlerCardinalityOperation.HandlerDispatch, 4),
                1d,
                -1,
                -1,
                2d,
                3,
                4,
                5,
                6,
                7,
                0,
                7,
                8,
                true,
                9,
                0,
                9,
                10,
                true
            );

            HandlerCardinalityBenchmarkResult result = new(
                new HandlerCardinalityBenchmarkCase(HandlerCardinalityOperation.HandlerDispatch, 4),
                1d,
                -1,
                -1,
                2d,
                3,
                4,
                5,
                6,
                7,
                0,
                7,
                8,
                true,
                9,
                0,
                9,
                10,
                true,
                11,
                12,
                13
            );

            string[] header = HandlerCardinalityBenchmarkResult.CsvHeader.Split(',');
            string[] row = result.ToCsvRow().Split(',');
            string[] legacyRow = legacyResult.ToCsvRow().Split(',');
            Assert.AreEqual(header.Length, row.Length);
            Assert.AreEqual(header.Length, legacyRow.Length);
            Assert.AreEqual("-1", row[2], "Unmeasured operation allocations must stay explicit.");
            CollectionAssert.AreEqual(
                new[]
                {
                    "scenario",
                    "operationsPerSecond",
                    "gcAllocations",
                    "wallClockMs",
                    "gcAllocatedBytes",
                    "dispatchFanOut",
                    "distinctMapEntries",
                    "liveRegistrations",
                    "occupiedTypeSlots",
                    "priorityEntries",
                    "priorityInlineCapacity",
                    "priorityMapCapacity",
                    "priorityOrderCapacity",
                    "priorityUsesSpillStorage",
                    "handlerEntries",
                    "handlerInlineCapacity",
                    "handlerMapCapacity",
                    "handlerOrderCapacity",
                    "handlerUsesSpillStorage",
                },
                new ArraySegment<string>(header, 0, 19)
            );
            CollectionAssert.AreEqual(
                new[]
                {
                    "busPriorityEntries",
                    "busPriorityMapCapacity",
                    "busPriorityOrderCapacity",
                },
                new ArraySegment<string>(header, 19, 3)
            );
            CollectionAssert.AreEqual(new[] { "-1", "-1", "-1" }, legacyRow[^3..]);

            string structured = result.ToStructuredLog();
            foreach (string key in header)
            {
                StringAssert.Contains(key + "=", structured, $"Missing structured key {key}.");
            }
        }

        [Test]
        public void ConstructionMatrixAndResultSchemaCoverEveryStorageOwner()
        {
            CollectionAssert.AreEquivalent(
                new[]
                {
                    HandlerStorageConstructionKind.HandlerCache,
                    HandlerStorageConstructionKind.PrioritySlot,
                    HandlerStorageConstructionKind.BusPriorityOwner,
                },
                (HandlerStorageConstructionKind[])
                    Enum.GetValues(typeof(HandlerStorageConstructionKind))
            );

            HandlerStorageConstructionBenchmarkResult result = new(
                HandlerStorageConstructionKind.HandlerCache,
                1d,
                -1,
                -1,
                2d,
                1_000
            );
            string[] header = HandlerStorageConstructionBenchmarkResult.CsvHeader.Split(',');
            string[] row = result.ToCsvRow().Split(',');
            Assert.AreEqual(header.Length, row.Length);
            Assert.AreEqual("-1", row[2], "Unmeasured construction allocations stay explicit.");

            string structured = result.ToStructuredLog();
            foreach (string key in header)
            {
                StringAssert.Contains(key + "=", structured, $"Missing structured key {key}.");
            }
        }

        [Test]
        public void FreshBusPriorityOwnerStartsEmptyWithZeroCapacity()
        {
            object owner = MessageBus.CreatePriorityStorageOwnerForBenchmark();
            Assert.IsTrue(
                MessageBus.TryObservePriorityStorageOwnerForBenchmark(
                    owner,
                    out MessageBus.PriorityStorageObservation observation
                )
            );
            Assert.Zero(observation.Entries);
            Assert.Zero(observation.MapCapacity);
            Assert.Zero(observation.OrderCapacity);
        }

        private static void AssertStorageCoherent(
            int entries,
            int inlineCapacity,
            int mapCapacity,
            int orderCapacity,
            bool usesSpillStorage,
            string label
        )
        {
            if (usesSpillStorage)
            {
                Assert.GreaterOrEqual(mapCapacity, entries, $"{label} map capacity is too small.");
                Assert.GreaterOrEqual(
                    orderCapacity,
                    entries,
                    $"{label} order capacity is too small."
                );
                return;
            }

            Assert.Zero(mapCapacity, $"Inline {label} storage must not own a spill map.");
            Assert.Zero(orderCapacity, $"Inline {label} storage must not own a spill order list.");
            Assert.GreaterOrEqual(
                inlineCapacity,
                entries,
                $"Inline {label} capacity is too small."
            );
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
