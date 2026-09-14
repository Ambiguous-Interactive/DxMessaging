#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using NUnit.Framework;

    public sealed class PayloadAbiBenchmarkContractTests
    {
        [Test]
        public void MatrixHasExactPrimaryAndLocatorWorkloadCounts()
        {
            Assert.AreEqual(50, PayloadAbiScenarios.All.Count);
            Assert.AreEqual(42, PayloadAbiScenarios.All.Count(workload => !workload.IsLocator));
            Assert.AreEqual(8, PayloadAbiScenarios.All.Count(workload => workload.IsLocator));
            Assert.AreEqual(28, PayloadAbiScenarios.Measurements.Count);
            Assert.AreEqual(
                50,
                PayloadAbiScenarios.All.Select(workload => workload.Key).Distinct().Count()
            );
        }

        [Test]
        public void PrimaryMatrixCoversEveryDeclaredPayloadShapeAndAbi()
        {
            PayloadAbiPayload[] payloads =
            {
                PayloadAbiPayload.Reference,
                PayloadAbiPayload.CanonicalStruct,
                PayloadAbiPayload.Readonly0,
                PayloadAbiPayload.Readonly16,
                PayloadAbiPayload.Readonly64,
                PayloadAbiPayload.Readonly256,
            };
            foreach (PayloadAbiPayload payload in payloads)
            {
                AssertExactlyOne(payload, PayloadAbiShape.EmptyRoute, PayloadAbiKind.None);
                foreach (
                    PayloadAbiShape shape in new[]
                    {
                        PayloadAbiShape.OneHandler,
                        PayloadAbiShape.MonomorphicSixteen,
                        PayloadAbiShape.HeterogeneousSixteen,
                    }
                )
                {
                    AssertExactlyOne(payload, shape, PayloadAbiKind.ByValue);
                    AssertExactlyOne(payload, shape, PayloadAbiKind.ReadonlyIn);
                }
            }
        }

        [Test]
        public void LocatorUsesExactMachineWordPayloadsAndOneHandlerPairs()
        {
            PayloadAbiPayload[] payloads =
            {
                PayloadAbiPayload.Word1,
                PayloadAbiPayload.Word2,
                PayloadAbiPayload.Word4,
                PayloadAbiPayload.Word8,
            };
            for (int index = 0; index < payloads.Length; ++index)
            {
                AssertExactlyOne(
                    payloads[index],
                    PayloadAbiShape.OneHandler,
                    PayloadAbiKind.ByValue
                );
                AssertExactlyOne(
                    payloads[index],
                    PayloadAbiShape.OneHandler,
                    PayloadAbiKind.ReadonlyIn
                );
                foreach (
                    PayloadAbiWorkload workload in PayloadAbiScenarios.All.Where(workload =>
                        workload.Payload == payloads[index]
                    )
                )
                {
                    Assert.IsTrue(workload.IsLocator);
                    Assert.AreEqual(IntPtr.Size * (1 << index), workload.PhysicalBytes);
                    Assert.AreEqual(workload.PhysicalBytes, workload.LogicalBytes);
                }
            }
        }

        [Test]
        public void PayloadIdentityAndPhysicalLayoutsAreFrozen()
        {
            Assert.AreSame(
                typeof(ComparisonStructPayload),
                PayloadAbiScenarios.PayloadType(PayloadAbiPayload.CanonicalStruct)
            );
            Assert.AreEqual(0, Single(PayloadAbiPayload.Readonly0).LogicalBytes);
            Assert.Greater(Single(PayloadAbiPayload.Readonly0).PhysicalBytes, 0);
            Assert.AreEqual(16, Single(PayloadAbiPayload.Readonly16).PhysicalBytes);
            Assert.AreEqual(64, Single(PayloadAbiPayload.Readonly64).PhysicalBytes);
            Assert.AreEqual(256, Single(PayloadAbiPayload.Readonly256).PhysicalBytes);
            Assert.IsTrue(PayloadAbiScenarios.PayloadType(PayloadAbiPayload.Reference).IsSealed);
            Assert.IsFalse(
                PayloadAbiScenarios.PayloadType(PayloadAbiPayload.Reference).IsValueType
            );
            Assert.AreEqual(-1, Single(PayloadAbiPayload.Reference).PhysicalBytes);
        }

        [Test]
        public void PairedCasesAlternateFirstArmAndNeverPairEmptyRoutes()
        {
            PayloadAbiMeasurementCase[] pairs = PayloadAbiScenarios
                .Measurements.Where(measurement => !measurement.IsEmptyRoute)
                .ToArray();
            for (int index = 0; index < pairs.Length; ++index)
            {
                PayloadAbiMeasurementCase pair = pairs[index];
                Assert.AreEqual(
                    index % 2 == 1,
                    pair.ReadonlyFirst,
                    $"Pair {index} broke deterministic arm alternation."
                );
                Assert.AreEqual(PayloadAbiKind.ByValue, pair.ByValue.Abi);
                Assert.AreEqual(PayloadAbiKind.ReadonlyIn, pair.ReadonlyIn.Abi);
                Assert.AreEqual(pair.ByValue.Payload, pair.ReadonlyIn.Payload);
                Assert.AreEqual(pair.ByValue.Shape, pair.ReadonlyIn.Shape);
            }
        }

        [TestCaseSource(nameof(Workloads))]
        public void OneEmitProvesFanOutTopologyDiagnosticsAndDistinctDelegates(
            PayloadAbiWorkload workload
        )
        {
            PayloadAbiObservation observation = PayloadAbiBenchmarks.ObserveOnceForContract(
                workload
            );
            Assert.AreEqual(
                workload.HandlerCount,
                observation.Progress,
                $"[{workload.Key}] One emit must invoke exactly the declared fan-out."
            );
            Assert.AreEqual(
                workload.HandlerCount,
                observation.LiveRegistrations,
                $"[{workload.Key}] Registration count drifted."
            );
            Assert.AreEqual(
                workload.HandlerCount == 0 ? 0 : 1,
                observation.OccupiedTypeSlots,
                $"[{workload.Key}] Message-type occupancy drifted."
            );
            Assert.IsFalse(
                observation.BusDiagnostics,
                $"[{workload.Key}] Bus diagnostics must be disabled."
            );
            Assert.IsFalse(
                observation.TokenDiagnostics,
                $"[{workload.Key}] Token diagnostics must be disabled."
            );
            Assert.IsTrue(observation.TokenEnabled, $"[{workload.Key}] Token must be enabled.");
            Assert.IsTrue(observation.HandlerActive, $"[{workload.Key}] Handler must be active.");
            Assert.AreEqual(
                workload.HandlerCount,
                observation.DistinctDelegates,
                $"[{workload.Key}] Delegate equality collapsed registrations."
            );
            Assert.AreEqual(
                ExpectedReceiverTypes(workload),
                observation.DistinctMethods,
                $"[{workload.Key}] Receiver method bodies collapsed."
            );
            Assert.AreEqual(
                ExpectedReceiverTypes(workload),
                observation.ReceiverTypes,
                $"[{workload.Key}] Receiver topology drifted."
            );
        }

        [Test]
        public void DiagnosticRowsDoNotMutateThePublicComparisonRoster()
        {
            CollectionAssert.AreEquivalent(
                (ComparisonScenario[])Enum.GetValues(typeof(ComparisonScenario)),
                ComparisonScenarios.All
            );
            Assert.AreEqual(9, ComparisonScenarios.All.Length);
            Assert.IsFalse(
                PayloadAbiScenarios.All.Any(workload =>
                    workload.Key.StartsWith("Comparison_", StringComparison.Ordinal)
                )
            );
        }

        [Test]
        public void ResultSchemaRetainsEveryPreregisteredMeasurementField()
        {
            string[] fields = PayloadAbiResult.CsvHeader.Split(',');
            CollectionAssert.IsSupersetOf(
                fields,
                new[]
                {
                    "scenario",
                    "payload",
                    "logicalBytes",
                    "physicalBytes",
                    "handlers",
                    "shape",
                    "abi",
                    "timedOperations",
                    "operationsPerSecond",
                    "gcAllocations",
                    "gcAllocatedBytes",
                    "readonlyFirst",
                    "readonlyToByValueRatio",
                    "cycleRatioSpreadPercent",
                    "cycleRatios",
                    "progress",
                }
            );
            Assert.AreEqual(fields.Length, fields.Distinct().Count());
        }

        private static int ExpectedReceiverTypes(PayloadAbiWorkload workload)
        {
            if (workload.HandlerCount == 0)
                return 0;
            return workload.Shape == PayloadAbiShape.HeterogeneousSixteen ? 16 : 1;
        }

        private static PayloadAbiWorkload Single(PayloadAbiPayload payload) =>
            PayloadAbiScenarios.All.First(workload => workload.Payload == payload);

        private static void AssertExactlyOne(
            PayloadAbiPayload payload,
            PayloadAbiShape shape,
            PayloadAbiKind abi
        )
        {
            Assert.That(
                PayloadAbiScenarios.All,
                Has.Exactly(1)
                    .Matches<PayloadAbiWorkload>(workload =>
                        workload.Payload == payload
                        && workload.Shape == shape
                        && workload.Abi == abi
                    )
            );
        }

        private static IEnumerable<TestCaseData> Workloads()
        {
            foreach (PayloadAbiWorkload workload in PayloadAbiScenarios.All)
            {
                yield return new TestCaseData(workload).SetName(
                    $"PayloadAbiContract_{workload.Key}"
                );
            }
        }
    }
}
#endif
