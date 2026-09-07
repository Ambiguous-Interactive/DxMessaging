#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Text;
    using DxMessaging.Core.MessageBus;

    /// <summary>Versioned replay operations for isolated bus lifecycle, callbacks, stale handles, and diagnostics.</summary>
    internal enum BusTraceOperationKind
    {
        Register,
        Remove,
        Enable,
        Disable,
        Emit,
        EmitWithReset,
        EmitWithThrow,
        RemoveStale,
        SetDiagnostics,
        Trim,
        EmitNested,
        EmitWithDisable,
        RemoveForeign,
        SetHandlerActive,
        EmitWithHandlerActive,
        DuplicateRegistration,
        CopyHandle,
    }

    /// <summary>Replay input with stable logical token identity, route, payload, and priority.</summary>
    internal readonly struct BusTraceOperation
    {
        internal BusTraceOperation(
            BusTraceOperationKind kind,
            int token = 0,
            int context = 0,
            int value = 0,
            int priority = 0,
            int kindOffset = 0,
            int nestedToken = 0,
            int depth = 0,
            int handleToken = 0,
            int handlerToken = 0,
            bool handlerActive = false,
            int handleSlot = -1,
            int sourceHandleSlot = -1
        )
        {
            Kind = kind;
            Token = token;
            Context = context;
            Value = value;
            Priority = priority;
            KindOffset = kindOffset;
            NestedToken = nestedToken;
            Depth = depth;
            HandleToken = handleToken;
            HandlerToken = handlerToken;
            HandlerActive = handlerActive;
            _handleSlot = handleSlot + 1;
            _sourceHandleSlot = sourceHandleSlot + 1;
        }

        internal BusTraceOperationKind Kind { get; }
        internal int Token { get; }
        internal int Context { get; }
        internal int Value { get; }
        internal int Priority { get; }

        internal int KindOffset { get; }
        internal int NestedToken { get; }
        internal int Depth { get; }

        // RemoveForeign passes this owner's most recently issued handle to Token.
        internal int HandleToken { get; }

        internal int HandlerToken { get; }
        internal bool HandlerActive { get; }

        // -1 retains the version-one through seven token-associated handle lane.
        // Nonnegative slots hold independent handles, including aliases and duplicates.
        private readonly int _handleSlot;
        private readonly int _sourceHandleSlot;
        internal int HandleSlot => _handleSlot - 1;
        internal int SourceHandleSlot => _sourceHandleSlot - 1;

        public override string ToString() =>
            $"{Kind}(token={Token},context={Context},value={Value},priority={Priority})"
            + (
                KindOffset != 0 || NestedToken != 0 || Depth != 0
                    ? $"[kindOffset={KindOffset},nestedToken={NestedToken},depth={Depth}]"
                    : string.Empty
            )
            + (
                Kind == BusTraceOperationKind.RemoveForeign
                    ? $"[handleToken={HandleToken}]"
                    : string.Empty
            )
            + (
                Kind == BusTraceOperationKind.SetHandlerActive
                || Kind == BusTraceOperationKind.EmitWithHandlerActive
                    ? $"[handlerToken={HandlerToken},handlerActive={HandlerActive}]"
                    : string.Empty
            )
            + (
                HandleSlot >= 0
                    ? $"[handleSlot={HandleSlot},sourceHandleSlot={SourceHandleSlot}]"
                    : string.Empty
            );
    }

    /// <summary>Immutable, versioned replay inputs; a seed identifies the original generator sequence.</summary>
    internal sealed class BusTraceSequence
    {
        internal const int GeneratorVersion = 8;
        internal const int HandleSlotCount = 8;
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
        internal const int SchemaVersion = 2;

        internal BusTraceObservation(
            IEnumerable<string> callbacks,
            string state,
            string exception,
            IMessageBus.TrimResult? trimResult = null,
            int occupiedTypeSlots = 0,
            int occupiedTargetSlots = 0,
            IEnumerable<string> finalEmissions = null,
            IEnumerable<string> unmatchedDiagnostics = null
        )
        {
            Callbacks = new List<string>(callbacks).AsReadOnly();
            State = state;
            Exception = exception;
            TrimResult = trimResult;
            OccupiedTypeSlots = occupiedTypeSlots;
            OccupiedTargetSlots = occupiedTargetSlots;
            FinalEmissions = new List<string>(finalEmissions ?? Array.Empty<string>()).AsReadOnly();
            UnmatchedDiagnostics = new List<string>(
                unmatchedDiagnostics ?? Array.Empty<string>()
            ).AsReadOnly();
        }

        internal ReadOnlyCollection<string> Callbacks { get; }

        /// <summary>Caller-visible typed ref snapshots in completion order; call ordinals start at zero per operation.</summary>
        internal ReadOnlyCollection<string> FinalEmissions { get; }

        /// <summary>Actual unmatched Info messages tagged by call. No report does not imply that a handler was found.</summary>
        internal ReadOnlyCollection<string> UnmatchedDiagnostics { get; }
        internal string State { get; }
        internal string Exception { get; }
        internal IMessageBus.TrimResult? TrimResult { get; }
        internal int OccupiedTypeSlots { get; }
        internal int OccupiedTargetSlots { get; }

        public override string ToString() =>
            $"callbacks=[{string.Join(",", Callbacks)}]; state={State}; exception={Exception ?? "none"}; trim={TrimResult?.ToString() ?? "none"}; occupiedSlots={OccupiedTypeSlots},{OccupiedTargetSlots}; finalEmissions=[{string.Join(";", FinalEmissions)}]; unmatchedDiagnostics=[{string.Join(";", UnmatchedDiagnostics)}]";
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
                $"observationSchema={BusTraceObservation.SchemaVersion}, generator={sequence.Version}, seed={sequence.Seed}, kind={sequence.Scenario.Kind}, firstMismatch={Index}, category={Category}\noperation={sequence.Operations[Index]}\ncontrol: {Control}\ncandidate: {Candidate}\nsequenceLength={sequence.Operations.Count}"
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
            if (generatorVersion == 8)
            {
                return GenerateHandleSlots(scenario, seed, length);
            }
            List<BusTraceOperation> operations = new(length);
            bool[] registered = new bool[BusTraceSequence.TokenCount];
            bool[] removed = new bool[BusTraceSequence.TokenCount];
            BusTraceOperation[] registrations = new BusTraceOperation[BusTraceSequence.TokenCount];
            uint state = seed == 0 ? 0x9e3779b9u : seed;
            for (int index = 0; index < length; ++index)
            {
                int token = (int)(Next(ref state) % BusTraceSequence.TokenCount);
                BusTraceOperationKind kind = (BusTraceOperationKind)(
                    Next(ref state)
                    % (
                        generatorVersion == 1 ? 5U
                        : generatorVersion == 2 ? 6U
                        : generatorVersion == 3 ? 9U
                        : generatorVersion == 4 ? 10U
                        : generatorVersion == 5 ? 12U
                        : generatorVersion == 6 ? 13U
                        : 15U
                    )
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
                if (
                    (
                        kind == BusTraceOperationKind.EmitWithReset
                        || kind == BusTraceOperationKind.EmitWithThrow
                        || kind == BusTraceOperationKind.EmitNested
                        || kind == BusTraceOperationKind.EmitWithDisable
                        || kind == BusTraceOperationKind.EmitWithHandlerActive
                    ) && !registered[token]
                )
                {
                    kind = BusTraceOperationKind.Emit;
                }
                if (kind == BusTraceOperationKind.RemoveStale && !removed[token])
                {
                    kind = BusTraceOperationKind.Emit;
                }
                int context = index < 2 ? 0 : (int)(Next(ref state) % 2);
                int value = unchecked((int)Next(ref state));
                if (
                    kind == BusTraceOperationKind.SetDiagnostics
                    || kind == BusTraceOperationKind.Trim
                )
                {
                    value &= 1;
                }
                int kindOffset = generatorVersion >= 5 ? (int)(Next(ref state) % 3) : 0;
                int nestedToken =
                    generatorVersion >= 5
                        ? (int)(Next(ref state) % BusTraceSequence.TokenCount)
                        : 0;
                if (kind == BusTraceOperationKind.EmitNested && !registered[nestedToken])
                {
                    nestedToken = token;
                }
                int handleToken = kind == BusTraceOperationKind.RemoveForeign ? nestedToken : 0;
                if (
                    kind == BusTraceOperationKind.RemoveForeign
                    && (handleToken == token || (!registered[handleToken] && !removed[handleToken]))
                )
                {
                    kind = BusTraceOperationKind.Emit;
                    handleToken = 0;
                }
                if (generatorVersion >= 5)
                {
                    if (index < 2)
                    {
                        kindOffset = 0;
                    }
                    if (
                        kind == BusTraceOperationKind.EmitNested
                        || kind == BusTraceOperationKind.EmitWithDisable
                        || kind == BusTraceOperationKind.EmitWithHandlerActive
                        || kind == BusTraceOperationKind.EmitWithReset
                        || kind == BusTraceOperationKind.EmitWithThrow
                    )
                    {
                        // Registration inputs are replay dependencies, not predicted dispatch state.
                        kindOffset = registrations[token].KindOffset;
                        context = registrations[token].Context;
                    }
                }
                int depth =
                    kind == BusTraceOperationKind.EmitNested ? 1 + (int)(Next(ref state) % 10) : 0;
                operations.Add(
                    new BusTraceOperation(
                        kind,
                        token,
                        context,
                        value,
                        (int)(Next(ref state) % 3) - 1,
                        kindOffset,
                        nestedToken: kind == BusTraceOperationKind.EmitNested ? nestedToken : 0,
                        depth: depth,
                        handleToken: handleToken,
                        handlerToken: kind == BusTraceOperationKind.EmitWithHandlerActive
                            ? nestedToken
                            : 0,
                        handlerActive: (
                            kind == BusTraceOperationKind.SetHandlerActive
                            || kind == BusTraceOperationKind.EmitWithHandlerActive
                        )
                            && (value & 1) != 0
                    )
                );
                if (kind == BusTraceOperationKind.Register)
                {
                    registered[token] = true;
                    registrations[token] = operations[operations.Count - 1];
                }
                if (kind == BusTraceOperationKind.Remove)
                {
                    registered[token] = false;
                    removed[token] = true;
                }
            }
            return new BusTraceSequence(scenario, seed, operations, generatorVersion);
        }

        private static BusTraceSequence GenerateHandleSlots(
            MessageScenario scenario,
            uint seed,
            int length
        )
        {
            List<BusTraceOperation> operations = new(length);
            HandleDependencies handles = new();
            BusTraceOperation[] prefix =
            {
                new(BusTraceOperationKind.Register, handleSlot: 0),
                new(
                    BusTraceOperationKind.DuplicateRegistration,
                    handleSlot: 1,
                    sourceHandleSlot: 0
                ),
                new(BusTraceOperationKind.CopyHandle, handleSlot: 2, sourceHandleSlot: 0),
                new(BusTraceOperationKind.Emit),
            };
            foreach (BusTraceOperation operation in prefix)
            {
                if (operations.Count == length)
                {
                    break;
                }
                operations.Add(operation);
                if (operation.HandleSlot >= 0)
                {
                    handles.Apply(operation);
                }
            }
            // Retain the complete earlier operation vocabulary in each longer campaign.
            // Its token-associated handles are independent of the new explicit handle slots.
            operations.AddRange(
                Generate(scenario, seed, (length - operations.Count) / 2, 7).Operations
            );
            uint state = seed ^ 0x9e3779b9u;
            if (state == 0)
            {
                state = 1;
            }
            while (operations.Count < length)
            {
                int slot = (int)(Next(ref state) % BusTraceSequence.HandleSlotCount);
                int source = (int)(Next(ref state) % BusTraceSequence.HandleSlotCount);
                int owner = (int)(Next(ref state) % BusTraceSequence.TokenCount);
                int choice = (int)(Next(ref state) % 8);
                int value = unchecked((int)Next(ref state));
                BusTraceOperation operation = choice switch
                {
                    0 => new(
                        BusTraceOperationKind.Register,
                        token: owner,
                        context: (value & 1),
                        priority: value % 3,
                        kindOffset: (int)(Next(ref state) % 3),
                        handleSlot: slot
                    ),
                    1 => new(
                        BusTraceOperationKind.DuplicateRegistration,
                        token: handles.Owner(source),
                        handleSlot: slot,
                        sourceHandleSlot: source
                    ),
                    2 => new(
                        BusTraceOperationKind.CopyHandle,
                        token: handles.Owner(source),
                        handleSlot: slot,
                        sourceHandleSlot: source
                    ),
                    3 => new(
                        BusTraceOperationKind.Remove,
                        token: handles.Owner(slot),
                        handleSlot: slot
                    ),
                    4 => new(
                        BusTraceOperationKind.Emit,
                        context: value & 1,
                        value: value,
                        kindOffset: (int)(Next(ref state) % 3)
                    ),
                    5 => new(BusTraceOperationKind.Enable, token: owner),
                    6 => new(BusTraceOperationKind.Disable, token: owner),
                    _ => new(BusTraceOperationKind.Trim, value: value & 1),
                };
                if (operation.HandleSlot >= 0 && !handles.Apply(operation))
                {
                    operation = new BusTraceOperation(BusTraceOperationKind.Emit, value: value);
                }
                operations.Add(operation);
            }
            return new BusTraceSequence(scenario, seed, operations, 8);
        }

        // Tracks issued identities and aliases only. Never predicts delivery or bus storage.
        private sealed class HandleDependencies
        {
            private readonly int[] _identities = new int[BusTraceSequence.HandleSlotCount];
            private readonly int[] _owners = new int[BusTraceSequence.HandleSlotCount];
            private readonly bool[] _live = new bool[BusTraceSequence.MaxOperations + 1];
            private int _nextIdentity;

            internal int Owner(int slot) => _owners[slot];

            internal bool Apply(BusTraceOperation operation)
            {
                int slot = operation.HandleSlot;
                int source = operation.SourceHandleSlot;
                int identity = _identities[slot];
                if (operation.Kind == BusTraceOperationKind.Remove)
                {
                    if (source != -1 || identity == 0 || _owners[slot] != operation.Token)
                    {
                        return false;
                    }
                    _live[identity] = false;
                    return true;
                }
                if (_live[identity])
                {
                    return false;
                }
                if (operation.Kind == BusTraceOperationKind.Register)
                {
                    if (source != -1)
                    {
                        return false;
                    }
                }
                else if (
                    operation.Kind == BusTraceOperationKind.DuplicateRegistration
                    || operation.Kind == BusTraceOperationKind.CopyHandle
                )
                {
                    if (
                        source < 0
                        || _identities[source] == 0
                        || _owners[source] != operation.Token
                        || (
                            operation.Kind == BusTraceOperationKind.DuplicateRegistration
                            && !_live[_identities[source]]
                        )
                    )
                    {
                        return false;
                    }
                    if (operation.Kind == BusTraceOperationKind.CopyHandle)
                    {
                        _identities[slot] = _identities[source];
                        _owners[slot] = operation.Token;
                        return true;
                    }
                }
                else
                {
                    return false;
                }
                _identities[slot] = ++_nextIdentity;
                _owners[slot] = operation.Token;
                _live[_nextIdentity] = true;
                return true;
            }
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
            bool[] removed = new bool[BusTraceSequence.TokenCount];
            HandleDependencies handles = new();
            foreach (BusTraceOperation operation in sequence.Operations)
            {
                if (
                    operation.HandleSlot < -1
                    || operation.HandleSlot >= BusTraceSequence.HandleSlotCount
                    || operation.SourceHandleSlot < -1
                    || operation.SourceHandleSlot >= BusTraceSequence.HandleSlotCount
                    || (
                        sequence.Version < 8
                        && (operation.HandleSlot != -1 || operation.SourceHandleSlot != -1)
                    )
                    || (operation.HandleSlot == -1 && operation.SourceHandleSlot != -1)
                    || operation.Token < 0
                    || operation.Token >= registered.Length
                    || operation.Context < 0
                    || operation.Context > 1
                    || operation.KindOffset < 0
                    || operation.KindOffset > 2
                    || operation.NestedToken < 0
                    || operation.NestedToken >= registered.Length
                    || operation.HandleToken < 0
                    || operation.HandleToken >= registered.Length
                    || (
                        operation.Kind != BusTraceOperationKind.RemoveForeign
                        && operation.HandleToken != 0
                    )
                    || operation.HandlerToken < 0
                    || operation.HandlerToken >= registered.Length
                    || (
                        operation.Kind != BusTraceOperationKind.EmitWithHandlerActive
                        && operation.HandlerToken != 0
                    )
                    || (
                        operation.Kind != BusTraceOperationKind.SetHandlerActive
                        && operation.Kind != BusTraceOperationKind.EmitWithHandlerActive
                        && operation.HandlerActive
                    )
                    || operation.Depth < 0
                    || operation.Depth > 10
                    || (
                        sequence.Version < 5
                        && (
                            operation.KindOffset != 0
                            || operation.NestedToken != 0
                            || operation.Depth != 0
                        )
                    )
                    || (
                        operation.Kind != BusTraceOperationKind.EmitNested
                        && (operation.NestedToken != 0 || operation.Depth != 0)
                    )
                )
                {
                    return false;
                }
                if (operation.HandleSlot >= 0)
                {
                    if (!handles.Apply(operation))
                    {
                        return false;
                    }
                    continue;
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
                        removed[operation.Token] = true;
                        break;
                    case BusTraceOperationKind.RemoveForeign:
                        if (
                            sequence.Version < 6
                            || operation.Token == operation.HandleToken
                            || (
                                !registered[operation.HandleToken]
                                && !removed[operation.HandleToken]
                            )
                        )
                        {
                            return false;
                        }
                        // Foreign cleanup owns neither the destination nor source registration.
                        // A removed source still supplies its actual stale handle for replay.
                        break;
                    case BusTraceOperationKind.Enable:
                    case BusTraceOperationKind.Disable:
                    case BusTraceOperationKind.Emit:
                        break;
                    case BusTraceOperationKind.EmitNested:
                        if (
                            sequence.Version < 5
                            || operation.Depth == 0
                            || !registered[operation.Token]
                            || !registered[operation.NestedToken]
                        )
                        {
                            return false;
                        }
                        break;
                    case BusTraceOperationKind.EmitWithDisable:
                        if (sequence.Version < 5 || !registered[operation.Token])
                        {
                            return false;
                        }
                        break;
                    case BusTraceOperationKind.SetHandlerActive:
                    case BusTraceOperationKind.EmitWithHandlerActive:
                        if (
                            sequence.Version < 7
                            || (
                                operation.Kind == BusTraceOperationKind.EmitWithHandlerActive
                                && !registered[operation.Token]
                            )
                        )
                        {
                            return false;
                        }
                        // Handlers exist before registration; toggling does not consume a handle.
                        break;
                    case BusTraceOperationKind.Trim:
                        if (sequence.Version < 4 || operation.Value < 0 || operation.Value > 1)
                        {
                            return false;
                        }
                        break;
                    case BusTraceOperationKind.SetDiagnostics:
                        if (sequence.Version < 3 || operation.Value < 0 || operation.Value > 1)
                        {
                            return false;
                        }
                        break;
                    case BusTraceOperationKind.RemoveStale:
                        if (sequence.Version < 3 || !removed[operation.Token])
                        {
                            return false;
                        }
                        break;
                    case BusTraceOperationKind.EmitWithThrow:
                        if (sequence.Version < 3 || !registered[operation.Token])
                        {
                            return false;
                        }
                        // A disabled or differently routed callback may not throw or remove its handle.
                        // An explicit Remove still owns that handle, even after callback cleanup.
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
                    : expected.TrimResult != actual.TrimResult ? "trim"
                    : !string.Equals(expected.State, actual.State, StringComparison.Ordinal)
                        ? "state"
                    : expected.OccupiedTypeSlots != actual.OccupiedTypeSlots
                    || expected.OccupiedTargetSlots != actual.OccupiedTargetSlots
                        ? "storage"
                    : !SameEntries(expected.FinalEmissions, actual.FinalEmissions)
                        ? "final-emission"
                    : !SameEntries(expected.UnmatchedDiagnostics, actual.UnmatchedDiagnostics)
                        ? "unmatched-diagnostic"
                    : null;
                if (category != null)
                {
                    return new BusTraceMismatch(index, category, expected, actual);
                }
            }
            return null;
        }

        private static bool SameEntries(
            IReadOnlyList<string> expected,
            IReadOnlyList<string> actual
        )
        {
            if (expected.Count != actual.Count)
            {
                return false;
            }
            for (int index = 0; index < expected.Count; ++index)
            {
                if (!string.Equals(expected[index], actual[index], StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
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
