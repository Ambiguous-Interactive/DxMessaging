#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Text;

    /// <summary>Supported replay operations, including callback-time reset of an isolated bus.</summary>
    internal enum BusTraceOperationKind
    {
        Register,
        Remove,
        Enable,
        Disable,
        Emit,
        EmitWithReset,
    }

    /// <summary>Replay input with stable logical token identity, route, payload, and priority.</summary>
    internal readonly struct BusTraceOperation
    {
        internal BusTraceOperation(
            BusTraceOperationKind kind,
            int token = 0,
            int context = 0,
            int value = 0,
            int priority = 0
        )
        {
            Kind = kind;
            Token = token;
            Context = context;
            Value = value;
            Priority = priority;
        }

        internal BusTraceOperationKind Kind { get; }
        internal int Token { get; }
        internal int Context { get; }
        internal int Value { get; }
        internal int Priority { get; }

        public override string ToString() =>
            $"{Kind}(token={Token},context={Context},value={Value},priority={Priority})";
    }

    /// <summary>Immutable, versioned replay inputs; a seed identifies the original generator sequence.</summary>
    internal sealed class BusTraceSequence
    {
        internal const int GeneratorVersion = 2;
        internal const int TokenCount = 4;
        internal const int MaxOperations = 256;

        internal BusTraceSequence(
            MessageScenario scenario,
            uint seed,
            IEnumerable<BusTraceOperation> operations,
            int generatorVersion = GeneratorVersion
        )
        {
            Scenario = scenario ?? throw new ArgumentNullException(nameof(scenario));
            Seed = seed;
            if (generatorVersion < 1 || generatorVersion > GeneratorVersion)
            {
                throw new ArgumentOutOfRangeException(nameof(generatorVersion));
            }
            Version = generatorVersion;
            if (operations == null)
            {
                throw new ArgumentNullException(nameof(operations));
            }
            List<BusTraceOperation> copy = new();
            foreach (BusTraceOperation operation in operations)
            {
                if (copy.Count == MaxOperations)
                {
                    throw new ArgumentException(
                        "Trace exceeds the operation limit.",
                        nameof(operations)
                    );
                }
                copy.Add(operation);
            }
            Operations = copy.AsReadOnly();
        }

        internal MessageScenario Scenario { get; }
        internal uint Seed { get; }
        internal int Version { get; }
        internal ReadOnlyCollection<BusTraceOperation> Operations { get; }
    }

    /// <summary>Actual ordered callback observations and post-operation state, not a predicted routing result.</summary>
    internal sealed class BusTraceObservation
    {
        internal BusTraceObservation(IEnumerable<string> callbacks, string state, string exception)
        {
            Callbacks = new List<string>(callbacks).AsReadOnly();
            State = state;
            Exception = exception;
        }

        internal ReadOnlyCollection<string> Callbacks { get; }
        internal string State { get; }
        internal string Exception { get; }

        public override string ToString() =>
            $"callbacks=[{string.Join(",", Callbacks)}]; state={State}; exception={Exception ?? "none"}";
    }

    /// <summary>Owns isolated implementation state for one complete replay.</summary>
    internal interface IBusTraceAdapter : IDisposable
    {
        BusTraceObservation Execute(BusTraceOperation operation);
    }

    /// <summary>The earliest observable difference, including both sides and reproducible input identity.</summary>
    internal sealed class BusTraceMismatch
    {
        internal BusTraceMismatch(
            int index,
            string category,
            BusTraceObservation control,
            BusTraceObservation candidate
        )
        {
            Index = index;
            Category = category;
            Control = control;
            Candidate = candidate;
        }

        internal int Index { get; }
        internal string Category { get; }
        internal BusTraceObservation Control { get; }
        internal BusTraceObservation Candidate { get; }

        internal string BuildReport(BusTraceSequence sequence)
        {
            StringBuilder report = new();
            report.Append(
                $"generator={sequence.Version}, seed={sequence.Seed}, kind={sequence.Scenario.Kind}, firstMismatch={Index}, category={Category}\noperation={sequence.Operations[Index]}\ncontrol: {Control}\ncandidate: {Candidate}\nsequenceLength={sequence.Operations.Count}"
            );
            // The immutable sequence caps this complete replay input at MaxOperations.
            // A minimized or hand-written trace cannot be reconstructed from its seed alone.
            for (int index = 0; index < sequence.Operations.Count; ++index)
            {
                report.Append($"\n[{index}] {sequence.Operations[index]}");
            }
            return report.ToString();
        }
    }

    /// <summary>Generates, validates, replays, compares, and deletion-shrinks deterministic inputs without modeling dispatch.</summary>
    internal static class DifferentialBusTrace
    {
        /// <summary>Generates a stable xorshift sequence independent of System.Random implementation changes.</summary>
        internal static BusTraceSequence Generate(
            MessageScenario scenario,
            uint seed,
            int length,
            int generatorVersion = BusTraceSequence.GeneratorVersion
        )
        {
            if (length < 0 || length > BusTraceSequence.MaxOperations)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }
            List<BusTraceOperation> operations = new(length);
            bool[] registered = new bool[BusTraceSequence.TokenCount];
            uint state = seed == 0 ? 0x9e3779b9u : seed;
            for (int index = 0; index < length; ++index)
            {
                int token = (int)(Next(ref state) % BusTraceSequence.TokenCount);
                BusTraceOperationKind kind = (BusTraceOperationKind)(
                    Next(ref state) % (generatorVersion == 1 ? 5U : 6U)
                );
                if (index == 0)
                {
                    kind = BusTraceOperationKind.Register;
                    token = 0;
                }
                if (index == 1)
                {
                    kind = BusTraceOperationKind.Emit;
                    token = 0;
                }
                if (kind == BusTraceOperationKind.Register && registered[token])
                {
                    kind = BusTraceOperationKind.Emit;
                }
                if (kind == BusTraceOperationKind.Remove && !registered[token])
                {
                    kind = BusTraceOperationKind.Register;
                }
                if (kind == BusTraceOperationKind.EmitWithReset && !registered[token])
                {
                    kind = BusTraceOperationKind.Emit;
                }
                int context = index < 2 ? 0 : (int)(Next(ref state) % 2);
                operations.Add(
                    new BusTraceOperation(
                        kind,
                        token,
                        context,
                        unchecked((int)Next(ref state)),
                        (int)(Next(ref state) % 3) - 1
                    )
                );
                if (kind == BusTraceOperationKind.Register)
                {
                    registered[token] = true;
                }
                if (kind == BusTraceOperationKind.Remove)
                {
                    registered[token] = false;
                }
            }
            return new BusTraceSequence(scenario, seed, operations, generatorVersion);
        }

        /// <summary>Checks handle existence and input bounds only; never predicts which callbacks should execute.</summary>
        internal static bool IsValid(BusTraceSequence sequence)
        {
            if (sequence == null)
            {
                return false;
            }
            if (
                sequence.Scenario.Kind != MessageKind.Untargeted
                && sequence.Scenario.Kind != MessageKind.Targeted
                && sequence.Scenario.Kind != MessageKind.Broadcast
            )
            {
                return false;
            }
            if (
                sequence.Scenario.UseInterceptor
                || sequence.Scenario.UsePostProcessor
                || sequence.Scenario.DiagnosticsEnabled
            )
            {
                return false;
            }
            bool[] registered = new bool[BusTraceSequence.TokenCount];
            foreach (BusTraceOperation operation in sequence.Operations)
            {
                if (
                    operation.Token < 0
                    || operation.Token >= registered.Length
                    || operation.Context < 0
                    || operation.Context > 1
                )
                {
                    return false;
                }
                switch (operation.Kind)
                {
                    case BusTraceOperationKind.Register:
                        if (registered[operation.Token])
                        {
                            return false;
                        }
                        registered[operation.Token] = true;
                        break;
                    case BusTraceOperationKind.Remove:
                        if (!registered[operation.Token])
                        {
                            return false;
                        }
                        registered[operation.Token] = false;
                        break;
                    case BusTraceOperationKind.Enable:
                    case BusTraceOperationKind.Disable:
                    case BusTraceOperationKind.Emit:
                        break;
                    case BusTraceOperationKind.EmitWithReset:
                        if (sequence.Version < 2 || !registered[operation.Token])
                        {
                            return false;
                        }
                        // A bus reset does not remove token-owned staged registrations.
                        // Leave handle dependencies intact for stale cleanup and re-enable.
                        break;
                    default:
                        return false;
                }
            }
            return true;
        }

        /// <summary>Creates a fresh adapter per replay; infrastructure and cleanup failures propagate and are not shrinkable mismatches.</summary>
        internal static ReadOnlyCollection<BusTraceObservation> Replay(
            BusTraceSequence sequence,
            Func<MessageScenario, IBusTraceAdapter> factory
        )
        {
            if (!IsValid(sequence))
            {
                throw new ArgumentException(
                    "Trace contains unsupported operations or invalid handle dependencies.",
                    nameof(sequence)
                );
            }
            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }
            IBusTraceAdapter adapter =
                factory(sequence.Scenario)
                ?? throw new InvalidOperationException("Adapter factory returned null.");
            Exception replayError = null;
            try
            {
                List<BusTraceObservation> observations = new(sequence.Operations.Count);
                foreach (BusTraceOperation operation in sequence.Operations)
                {
                    observations.Add(adapter.Execute(operation));
                }
                return observations.AsReadOnly();
            }
            catch (Exception error)
            {
                replayError = error;
                throw;
            }
            finally
            {
                try
                {
                    adapter.Dispose();
                }
                catch (Exception cleanupError) when (replayError != null)
                {
                    throw new AggregateException(
                        "Replay and adapter cleanup both failed.",
                        replayError,
                        cleanupError
                    );
                }
            }
        }

        /// <summary>Compares observations in order and stops at the first differing callback, exception, or state.</summary>
        internal static BusTraceMismatch Compare(
            IReadOnlyList<BusTraceObservation> control,
            IReadOnlyList<BusTraceObservation> candidate
        )
        {
            if (control == null)
            {
                throw new ArgumentNullException(nameof(control));
            }
            if (candidate == null)
            {
                throw new ArgumentNullException(nameof(candidate));
            }
            if (control.Count != candidate.Count)
            {
                throw new ArgumentException("Replays must contain the same operation count.");
            }
            for (int index = 0; index < control.Count; ++index)
            {
                BusTraceObservation expected = control[index];
                BusTraceObservation actual = candidate[index];
                bool callbacksEqual = expected.Callbacks.Count == actual.Callbacks.Count;
                for (
                    int callback = 0;
                    callbacksEqual && callback < expected.Callbacks.Count;
                    ++callback
                )
                {
                    callbacksEqual = string.Equals(
                        expected.Callbacks[callback],
                        actual.Callbacks[callback],
                        StringComparison.Ordinal
                    );
                }
                string category =
                    !callbacksEqual ? "callbacks"
                    : !string.Equals(expected.Exception, actual.Exception, StringComparison.Ordinal)
                        ? "exception"
                    : !string.Equals(expected.State, actual.State, StringComparison.Ordinal)
                        ? "state"
                    : null;
                if (category != null)
                {
                    return new BusTraceMismatch(index, category, expected, actual);
                }
            }
            return null;
        }

        /// <summary>Finds a deterministic one-deletion-minimal trace while preserving validity and mismatch category.</summary>
        internal static BusTraceSequence Shrink(
            BusTraceSequence original,
            Func<BusTraceSequence, BusTraceMismatch> evaluate
        )
        {
            if (!IsValid(original))
            {
                throw new ArgumentException("Cannot shrink an invalid trace.", nameof(original));
            }
            if (evaluate == null)
            {
                throw new ArgumentNullException(nameof(evaluate));
            }
            BusTraceMismatch initial =
                evaluate(original)
                ?? throw new ArgumentException("Cannot shrink a passing trace.", nameof(original));
            BusTraceSequence current = original;
            int index = 0;
            while (index < current.Operations.Count)
            {
                List<BusTraceOperation> remaining = new(current.Operations);
                remaining.RemoveAt(index);
                BusTraceSequence candidate = new(
                    current.Scenario,
                    current.Seed,
                    remaining,
                    current.Version
                );
                BusTraceMismatch mismatch = IsValid(candidate) ? evaluate(candidate) : null;
                if (mismatch != null && mismatch.Category == initial.Category)
                {
                    current = candidate;
                    index = 0;
                }
                else
                {
                    ++index;
                }
            }
            return current;
        }

        private static uint Next(ref uint state)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }
    }
}
#endif
