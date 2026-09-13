#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using UnityEngine;

    /// <summary>Writes opt-in, deterministic differential replay inputs and observations for sealing.</summary>
    internal static class DifferentialReplayEvidence
    {
        internal const string DirectoryEnvironmentVariable =
            "DXM_DIFFERENTIAL_REPLAY_EVIDENCE_DIRECTORY";

        internal static void TryWrite(
            BusTraceSequence original,
            IReadOnlyList<BusTraceObservation> originalControl,
            IReadOnlyList<BusTraceObservation> originalCandidate,
            BusTraceMismatch originalMismatch,
            BusTraceSequence minimized,
            IReadOnlyList<BusTraceObservation> minimizedControl,
            IReadOnlyList<BusTraceObservation> minimizedCandidate,
            BusTraceMismatch minimizedMismatch,
            string fault
        )
        {
            string directory = Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }
            if (originalMismatch == null || minimizedMismatch == null)
            {
                throw new ArgumentException("Differential evidence requires two failing replays.");
            }
            ReplayEvidenceDocument document = new()
            {
                schemaVersion = 1,
                observationSchemaVersion = BusTraceObservation.SchemaVersion,
                generatorVersion = original.Version,
                seed = original.Seed,
                messageKind = original.Scenario.Kind.ToString(),
                fault = fault,
                original = Capture(original, originalControl, originalCandidate, originalMismatch),
                minimized = Capture(
                    minimized,
                    minimizedControl,
                    minimizedCandidate,
                    minimizedMismatch
                ),
            };
            Directory.CreateDirectory(directory);
            string fileName = document.messageKind.ToLowerInvariant() + ".json";
            string filePath = Path.Combine(directory, fileName);
            using FileStream stream = new(
                filePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None
            );
            using StreamWriter writer = new(stream, new UTF8Encoding(false));
            writer.Write(JsonUtility.ToJson(document));
            writer.Write('\n');
        }

        private static ReplayEvidence Capture(
            BusTraceSequence sequence,
            IReadOnlyList<BusTraceObservation> control,
            IReadOnlyList<BusTraceObservation> candidate,
            BusTraceMismatch mismatch
        ) =>
            new()
            {
                operations = CaptureOperations(sequence.Operations),
                controlTrace = CaptureObservations(control),
                candidateTrace = CaptureObservations(candidate),
                mismatchIndex = mismatch.Index,
                category = mismatch.Category,
            };

        private static OperationEvidence[] CaptureOperations(
            IReadOnlyList<BusTraceOperation> operations
        )
        {
            OperationEvidence[] captured = new OperationEvidence[operations.Count];
            for (int index = 0; index < operations.Count; ++index)
            {
                BusTraceOperation operation = operations[index];
                captured[index] = new OperationEvidence
                {
                    kind = operation.Kind.ToString(),
                    token = operation.Token,
                    context = operation.Context,
                    value = operation.Value,
                    priority = operation.Priority,
                    kindOffset = operation.KindOffset,
                    nestedToken = operation.NestedToken,
                    depth = operation.Depth,
                    handleToken = operation.HandleToken,
                    handlerToken = operation.HandlerToken,
                    handlerActive = operation.HandlerActive,
                    handleSlot = operation.HandleSlot,
                    sourceHandleSlot = operation.SourceHandleSlot,
                    leaseSlot = operation.LeaseSlot,
                    sourceLeaseSlot = operation.SourceLeaseSlot,
                };
            }
            return captured;
        }

        private static ObservationEvidence[] CaptureObservations(
            IReadOnlyList<BusTraceObservation> observations
        )
        {
            ObservationEvidence[] captured = new ObservationEvidence[observations.Count];
            for (int index = 0; index < observations.Count; ++index)
            {
                BusTraceObservation observation = observations[index];
                captured[index] = new ObservationEvidence
                {
                    callbacks = Copy(observation.Callbacks),
                    state = observation.State,
                    exception = observation.Exception,
                    trimResult = observation.TrimResult?.ToString(),
                    occupiedTypeSlots = observation.OccupiedTypeSlots,
                    occupiedTargetSlots = observation.OccupiedTargetSlots,
                    finalEmissions = Copy(observation.FinalEmissions),
                    unmatchedDiagnostics = Copy(observation.UnmatchedDiagnostics),
                };
            }
            return captured;
        }

        private static string[] Copy(IReadOnlyList<string> values)
        {
            string[] copy = new string[values.Count];
            for (int index = 0; index < values.Count; ++index)
            {
                copy[index] = values[index];
            }
            return copy;
        }

        [Serializable]
        private sealed class ReplayEvidenceDocument
        {
            public int schemaVersion;
            public int observationSchemaVersion;
            public int generatorVersion;
            public uint seed;
            public string messageKind;
            public string fault;
            public ReplayEvidence original;
            public ReplayEvidence minimized;
        }

        [Serializable]
        private sealed class ReplayEvidence
        {
            public OperationEvidence[] operations;
            public ObservationEvidence[] controlTrace;
            public ObservationEvidence[] candidateTrace;
            public int mismatchIndex;
            public string category;
        }

        [Serializable]
        private sealed class OperationEvidence
        {
            public string kind;
            public int token;
            public int context;
            public int value;
            public int priority;
            public int kindOffset;
            public int nestedToken;
            public int depth;
            public int handleToken;
            public int handlerToken;
            public bool handlerActive;
            public int handleSlot;
            public int sourceHandleSlot;
            public int leaseSlot;
            public int sourceLeaseSlot;
        }

        [Serializable]
        private sealed class ObservationEvidence
        {
            public string[] callbacks;
            public string state;
            public string exception;
            public string trimResult;
            public int occupiedTypeSlots;
            public int occupiedTargetSlots;
            public string[] finalEmissions;
            public string[] unmatchedDiagnostics;
        }
    }
}
#endif
