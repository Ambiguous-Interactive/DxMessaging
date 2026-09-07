#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;

    /// <summary>Runs trace operations through production bus and token APIs; callback output is never simulated.</summary>
    internal class MessageBusTraceAdapter : IBusTraceAdapter
    {
        private readonly MessageScenario _scenario;
        private readonly IMessageBus _bus;
        private readonly IMessageBus _emitter;
        private readonly Action _reset;
        private BusTraceOperation? _resetOperation;
        private BusTraceOperation? _throwOperation;
        private BusTraceOperation? _disableOperation;
        private BusTraceOperation? _handlerActiveOperation;
        private BusTraceOperation? _nestedOperation;
        private int _depth;
        private readonly BusTraceOperation[] _registrations = new BusTraceOperation[
            BusTraceSequence.TokenCount
        ];
        private readonly MessageRegistrationHandle[] _staleHandles = new MessageRegistrationHandle[
            BusTraceSequence.TokenCount
        ];
        private readonly MessageHandler[] _handlers = new MessageHandler[
            BusTraceSequence.TokenCount
        ];
        private readonly MessageRegistrationToken[] _tokens = new MessageRegistrationToken[
            BusTraceSequence.TokenCount
        ];
        private readonly MessageRegistrationHandle[] _handles = new MessageRegistrationHandle[
            BusTraceSequence.TokenCount
        ];
        private readonly MessageRegistrationHandle[] _explicitHandles =
            new MessageRegistrationHandle[BusTraceSequence.HandleSlotCount];
        private readonly Func<MessageRegistrationHandle>[] _registrationFactories =
            new Func<MessageRegistrationHandle>[BusTraceSequence.HandleSlotCount];
        private readonly int[] _explicitCallbackIdentities = new int[
            BusTraceSequence.HandleSlotCount
        ];
        private readonly BusTraceOperation[] _explicitRegistrations = new BusTraceOperation[
            BusTraceSequence.HandleSlotCount
        ];
        private int _nextCallbackIdentity;
        private readonly List<string> _callbacks = new();
        private readonly List<string> _finalEmissions = new();
        private readonly List<string> _unmatchedDiagnostics = new();
        private int _nextEmissionOrdinal;
        private readonly LeakWatcher _leaks;

        internal MessageBusTraceAdapter(
            MessageScenario scenario,
            IMessageBus bus,
            IMessageBus emitter = null,
            Action reset = null,
            Func<int, MessageRegistrationToken> tokenFactory = null
        )
        {
            _scenario = scenario ?? throw new ArgumentNullException(nameof(scenario));
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));
            // Keep token storage keyed by the real implementation, even when a mutant intercepts emission.
            _emitter = emitter ?? bus;
            _reset = reset;
            // TrimResult includes process-shared retained pools. Start every isolated replay
            // from the same real empty-pool baseline, without predicting any eviction result.
            _bus.Trim(force: true);
            _leaks = LeakWatcher.WatchWithSlots(bus, label: "Differential replay " + scenario.Kind);
            try
            {
                for (int slot = 0; slot < _tokens.Length; ++slot)
                {
                    if (tokenFactory == null)
                    {
                        MessageHandler handler = new(new InstanceId(1000 + slot), bus)
                        {
                            active = true,
                        };
                        _handlers[slot] = handler;
                        _tokens[slot] = MessageRegistrationToken.Create(handler, bus);
                    }
                    else
                    {
                        _tokens[slot] = tokenFactory(slot);
                    }
                    _tokens[slot].DiagnosticMode = false;
                    _tokens[slot].Enable();
                }
            }
            catch (Exception setupError)
            {
                try
                {
                    Dispose();
                }
                catch (Exception cleanupError)
                {
                    throw new AggregateException(
                        "Adapter setup and cleanup both failed.",
                        setupError,
                        cleanupError
                    );
                }
                throw;
            }
        }

        /// <summary>Records callbacks, exceptions, counters, occupancy, token activity, diagnostics, and retained diagnostic messages.</summary>
        public BusTraceObservation Execute(BusTraceOperation operation)
        {
            _callbacks.Clear();
            _finalEmissions.Clear();
            _unmatchedDiagnostics.Clear();
            _nextEmissionOrdinal = 0;
            string exception = null;
            IMessageBus.TrimResult? trimResult = null;
            try
            {
                switch (operation.Kind)
                {
                    case BusTraceOperationKind.Register:
                        Register(operation);
                        break;
                    case BusTraceOperationKind.DuplicateRegistration:
                        DuplicateRegistration(operation);
                        break;
                    case BusTraceOperationKind.CopyHandle:
                        CopyHandle(operation);
                        break;
                    case BusTraceOperationKind.Remove:
                    case BusTraceOperationKind.RemoveStale:
                    case BusTraceOperationKind.RemoveForeign:
                        Remove(operation);
                        break;
                    case BusTraceOperationKind.Enable:
                        _tokens[operation.Token].Enable();
                        break;
                    case BusTraceOperationKind.Disable:
                        _tokens[operation.Token].Disable();
                        break;
                    case BusTraceOperationKind.SetHandlerActive:
                        SetHandlerActive(operation.Token, operation.HandlerActive);
                        break;
                    case BusTraceOperationKind.EmitWithHandlerActive:
                        _handlerActiveOperation = operation;
                        try
                        {
                            Emit(operation);
                        }
                        finally
                        {
                            // The trigger may be disabled, inactive, or on a different route.
                            _handlerActiveOperation = null;
                        }
                        break;
                    case BusTraceOperationKind.Emit:
                        Emit(operation);
                        break;
                    case BusTraceOperationKind.Trim:
                        trimResult = Trim(operation.Value != 0);
                        break;
                    case BusTraceOperationKind.SetDiagnostics:
                        _tokens[operation.Token].DiagnosticMode = operation.Value != 0;
                        break;
                    case BusTraceOperationKind.EmitNested:
                        _nestedOperation = operation;
                        try
                        {
                            Emit(operation);
                        }
                        finally
                        {
                            _nestedOperation = null;
                            _depth = 0;
                        }
                        break;
                    case BusTraceOperationKind.EmitWithDisable:
                        _disableOperation = operation;
                        try
                        {
                            Emit(operation);
                        }
                        finally
                        {
                            _disableOperation = null;
                        }
                        break;
                    case BusTraceOperationKind.EmitWithThrow:
                        _throwOperation = operation;
                        try
                        {
                            Emit(operation);
                        }
                        finally
                        {
                            _throwOperation = null;
                        }
                        break;
                    case BusTraceOperationKind.EmitWithReset:
                        if (_reset == null)
                        {
                            throw new NotSupportedException(
                                "This adapter has no local reset action."
                            );
                        }
                        _resetOperation = operation;
                        try
                        {
                            Emit(operation);
                        }
                        finally
                        {
                            // A disabled or differently routed trigger may never receive a callback.
                            _resetOperation = null;
                        }
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(operation));
                }
            }
            catch (Exception error)
            {
                // Exceptions are observable output, not ignored failures; later operations still run.
                exception = error.GetType().FullName + ": " + error.Message;
            }
            string enabled = string.Empty;
            foreach (MessageRegistrationToken token in _tokens)
            {
                enabled += token.Enabled ? "1" : "0";
            }
            string handlerActive = DescribeHandlerActivity();
            string diagnostics = string.Empty;
            string retainedMessages = string.Empty;
            foreach (MessageRegistrationToken token in _tokens)
            {
                int calls = 0;
                foreach (int count in token._callCounts.Values)
                {
                    calls += count;
                }
                diagnostics += $"{token._metadata.Count}/{calls}/{token._emissionBuffer.Count},";
                int references = 0;
                for (int index = 0; index < token._emissionBuffer.Count; ++index)
                {
                    if (token._emissionBuffer[index].message != null)
                    {
                        ++references;
                    }
                }
                retainedMessages += $"{references},";
            }
            string state =
                $"counts={_bus.RegisteredUntargeted},{_bus.RegisteredTargeted},{_bus.RegisteredBroadcast},{_bus.RegisteredInterceptors},{_bus.RegisteredPostProcessors},{_bus.RegisteredGlobalAcceptAll}; slots={_bus.OccupiedTypeSlots},{_bus.OccupiedTargetSlots}; enabled={enabled}; handlerActive={handlerActive}; diagnostics={_bus.DiagnosticsMode}; tokenMetadataCallsHistory={diagnostics}; retainedMessages={retainedMessages}"
                + DescribeAdapterState();
            return new BusTraceObservation(
                _callbacks,
                state,
                exception,
                trimResult,
                _bus.OccupiedTypeSlots,
                _bus.OccupiedTargetSlots,
                _finalEmissions,
                _unmatchedDiagnostics
            );
        }

        /// <summary>Always attempts every token cleanup and reports cleanup or registration-leak failures.</summary>
        public void Dispose()
        {
            List<Exception> errors = new();
            for (int slot = 0; slot < _tokens.Length; ++slot)
            {
                MessageRegistrationToken token = _tokens[slot];
                try
                {
                    if (token == null)
                    {
                        continue;
                    }
                    DisposeToken(slot);
                    if (
                        token._metadata.Count != 0
                        || token._callCounts.Count != 0
                        || token._emissionBuffer.Count != 0
                    )
                    {
                        throw new InvalidOperationException(
                            $"Disposed token retained metadata={token._metadata.Count}, callCounts={token._callCounts.Count}, history={token._emissionBuffer.Count}."
                        );
                    }
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
            }
            try
            {
                _bus.Trim(force: true);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
            try
            {
                _leaks.Dispose();
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
            if (errors.Count > 0)
            {
                throw new AggregateException("Differential replay cleanup failed.", errors);
            }
        }

        protected virtual void DisposeToken(int slot) => _tokens[slot].Dispose();

        protected virtual string DescribeHandlerActivity()
        {
            string activity = string.Empty;
            foreach (MessageHandler handler in _handlers)
            {
                activity += handler.active ? "1" : "0";
            }
            return activity;
        }

        protected virtual string DescribeAdapterState() => string.Empty;

        protected MessageRegistrationToken Token(int slot) => _tokens[slot];

        protected MessageRegistrationHandle Handle(int slot) => _handles[slot];

        protected IMessageBus Bus => _bus;

        protected virtual IMessageBus.TrimResult Trim(bool force) => _bus.Trim(force);

        protected virtual void CopyHandle(BusTraceOperation operation)
        {
            _explicitHandles[operation.HandleSlot] = _explicitHandles[operation.SourceHandleSlot];
            _registrationFactories[operation.HandleSlot] = _registrationFactories[
                operation.SourceHandleSlot
            ];
            CopyRegistrationIdentity(operation);
        }

        protected virtual void DuplicateRegistration(BusTraceOperation operation)
        {
            Func<MessageRegistrationHandle> register = _registrationFactories[
                operation.SourceHandleSlot
            ];
            _explicitHandles[operation.HandleSlot] = register();
            _registrationFactories[operation.HandleSlot] = register;
            CopyRegistrationIdentity(operation);
        }

        private void CopyRegistrationIdentity(BusTraceOperation operation)
        {
            _explicitCallbackIdentities[operation.HandleSlot] = _explicitCallbackIdentities[
                operation.SourceHandleSlot
            ];
            _explicitRegistrations[operation.HandleSlot] = _explicitRegistrations[
                operation.SourceHandleSlot
            ];
        }

        protected virtual void Remove(BusTraceOperation operation)
        {
            if (operation.HandleSlot >= 0)
            {
                _tokens[operation.Token].RemoveRegistration(_explicitHandles[operation.HandleSlot]);
                return;
            }
            int slot = operation.Token;
            MessageRegistrationHandle handle =
                operation.Kind == BusTraceOperationKind.RemoveStale ? _staleHandles[slot]
                : operation.Kind == BusTraceOperationKind.RemoveForeign
                    ? _handles[operation.HandleToken]
                : _handles[slot];
            _tokens[slot].RemoveRegistration(handle);
            if (operation.Kind == BusTraceOperationKind.Remove)
            {
                _staleHandles[slot] = handle;
            }
        }

        protected virtual void CleanupThrowingCallback(int slot) =>
            _tokens[slot].RemoveRegistration(_handles[slot]);

        protected virtual void CleanupThrowingCallback(BusTraceOperation operation)
        {
            if (operation.HandleSlot >= 0)
            {
                Remove(operation);
            }
            else
            {
                CleanupThrowingCallback(operation.Token);
            }
        }

        // Mutants change real callback behavior here; observation construction remains shared.
        protected virtual void OnCallback(int slot, IMessage message) { }

        protected virtual void SetHandlerActive(int slot, bool active) =>
            _handlers[slot].active = active;

        protected virtual void DisableFromCallback(int slot) => _tokens[slot].Disable();

        protected virtual void EmitNested(BusTraceOperation operation) => Emit(operation);

        private MessageScenario Scenario(int offset) =>
            offset == 0
                ? _scenario
                : new MessageScenario((MessageKind)(((int)_scenario.Kind + offset) % 3));

        protected virtual void Register(BusTraceOperation operation)
        {
            if (operation.HandleSlot >= 0)
            {
                Func<MessageRegistrationHandle> register = CreateRegistrationFactory(operation);
                _explicitHandles[operation.HandleSlot] = register();
                _registrationFactories[operation.HandleSlot] = register;
                return;
            }
            int slot = operation.Token;
            MessageRegistrationToken token = _tokens[slot];
            _registrations[slot] = operation;
            MessageScenario scenario = Scenario(operation.KindOffset);
            InstanceId context = new(2000 + operation.Context);
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                    _handles[slot] = ScenarioHarness.RegisterUntargeted<UntargetedPayload>(
                        scenario,
                        token,
                        (in UntargetedPayload message) => Record(slot, message.Value, message),
                        operation.Priority
                    );
                    break;
                case MessageKind.Targeted:
                    _handles[slot] = ScenarioHarness.RegisterTargeted<TargetedPayload>(
                        scenario,
                        token,
                        context,
                        (in TargetedPayload message) => Record(slot, message.Value, message),
                        operation.Priority
                    );
                    break;
                case MessageKind.Broadcast:
                    _handles[slot] = ScenarioHarness.RegisterBroadcast<BroadcastPayload>(
                        scenario,
                        token,
                        context,
                        (in BroadcastPayload message) => Record(slot, message.Value, message),
                        operation.Priority
                    );
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(_scenario));
            }
        }

        private Func<MessageRegistrationHandle> CreateRegistrationFactory(
            BusTraceOperation operation
        )
        {
            int callbackIdentity = _nextCallbackIdentity++;
            _explicitCallbackIdentities[operation.HandleSlot] = callbackIdentity;
            _explicitRegistrations[operation.HandleSlot] = operation;
            MessageScenario scenario = Scenario(operation.KindOffset);
            MessageRegistrationToken token = _tokens[operation.Token];
            InstanceId context = new(2000 + operation.Context);
            // A duplicate invokes this same factory, retaining the original route, priority,
            // and delegate identity. All reference counting remains production behavior.
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                    MessageHandler.FastHandler<UntargetedPayload> untargeted = (
                        in UntargetedPayload message
                    ) =>
                        Record(
                            operation.Token,
                            message.Value,
                            message,
                            operation.HandleSlot,
                            callbackIdentity
                        );
                    return () =>
                        ScenarioHarness.RegisterUntargeted(
                            scenario,
                            token,
                            untargeted,
                            operation.Priority
                        );
                case MessageKind.Targeted:
                    MessageHandler.FastHandler<TargetedPayload> targeted = (
                        in TargetedPayload message
                    ) =>
                        Record(
                            operation.Token,
                            message.Value,
                            message,
                            operation.HandleSlot,
                            callbackIdentity
                        );
                    return () =>
                        ScenarioHarness.RegisterTargeted(
                            scenario,
                            token,
                            context,
                            targeted,
                            operation.Priority
                        );
                case MessageKind.Broadcast:
                    MessageHandler.FastHandler<BroadcastPayload> broadcast = (
                        in BroadcastPayload message
                    ) =>
                        Record(
                            operation.Token,
                            message.Value,
                            message,
                            operation.HandleSlot,
                            callbackIdentity
                        );
                    return () =>
                        ScenarioHarness.RegisterBroadcast(
                            scenario,
                            token,
                            context,
                            broadcast,
                            operation.Priority
                        );
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation));
            }
        }

        private void Emit(BusTraceOperation operation)
        {
            MessageScenario scenario = Scenario(operation.KindOffset);
            InstanceId context = new(2000 + operation.Context);
            int ordinal = _nextEmissionOrdinal++;
            bool previousEnabled = MessagingDebug.enabled;
            Action<LogLevel, string> previousLog = MessagingDebug.LogFunction;
            // Capture only this synchronous emission. A nested scope restores this collector,
            // without forwarding its unmatched reports into its parent's observations.
            MessagingDebug.enabled = true;
            MessagingDebug.LogFunction = (level, message) =>
            {
                if (
                    level == LogLevel.Info
                    && message != null
                    && (
                        message.StartsWith(
                            "Could not find a matching untargeted broadcast handler ",
                            StringComparison.Ordinal
                        )
                        || message.StartsWith(
                            "Could not find a matching targeted broadcast handler ",
                            StringComparison.Ordinal
                        )
                        || message.StartsWith(
                            "Could not find a matching sourced broadcast handler ",
                            StringComparison.Ordinal
                        )
                    )
                )
                {
                    _unmatchedDiagnostics.Add($"call={ordinal};{message}");
                }
                else
                {
                    // Keep unrelated logging behavior, including nulls and literal braces.
                    previousLog?.Invoke(level, message);
                }
            };
            try
            {
                // These are caller-visible typed ref values, including when dispatch throws.
                // Untyped APIs and extension methods' value-context boundaries are not observed here.
                switch (scenario.Kind)
                {
                    case MessageKind.Untargeted:
                        UntargetedPayload untargeted = new(operation.Value);
                        try
                        {
                            _emitter.UntargetedBroadcast(ref untargeted);
                        }
                        finally
                        {
                            _finalEmissions.Add(
                                $"call={ordinal},kind={scenario.Kind},value={untargeted.Value},context=none"
                            );
                        }
                        break;
                    case MessageKind.Targeted:
                        TargetedPayload targeted = new(operation.Value);
                        try
                        {
                            _emitter.TargetedBroadcast(ref context, ref targeted);
                        }
                        finally
                        {
                            _finalEmissions.Add(
                                $"call={ordinal},kind={scenario.Kind},value={targeted.Value},context={context.Id}"
                            );
                        }
                        break;
                    case MessageKind.Broadcast:
                        BroadcastPayload broadcast = new(operation.Value);
                        try
                        {
                            _emitter.SourcedBroadcast(ref context, ref broadcast);
                        }
                        finally
                        {
                            _finalEmissions.Add(
                                $"call={ordinal},kind={scenario.Kind},value={broadcast.Value},context={context.Id}"
                            );
                        }
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(_scenario));
                }
            }
            finally
            {
                MessagingDebug.LogFunction = previousLog;
                MessagingDebug.enabled = previousEnabled;
            }
        }

        private void Record(
            int token,
            int value,
            IMessage message,
            int handleSlot = -1,
            int callbackIdentity = -1
        )
        {
            _callbacks.Add(
                $"token={token},value={value}"
                    + (
                        handleSlot >= 0
                            ? $",registration={handleSlot},callback={callbackIdentity}"
                            : string.Empty
                    )
            );
            OnCallback(token, message);
            if (
                _handlerActiveOperation.HasValue
                && MatchesCallback(_handlerActiveOperation.Value, token, callbackIdentity)
            )
            {
                BusTraceOperation operation = _handlerActiveOperation.Value;
                _handlerActiveOperation = null;
                SetHandlerActive(operation.HandlerToken, operation.HandlerActive);
            }
            if (
                _disableOperation.HasValue
                && MatchesCallback(_disableOperation.Value, token, callbackIdentity)
            )
            {
                _disableOperation = null;
                DisableFromCallback(token);
            }
            if (_nestedOperation.HasValue)
            {
                BusTraceOperation nested = _nestedOperation.Value;
                bool useNested = (_depth & 1) != 0;
                if (
                    MatchesCallback(nested, token, callbackIdentity, useNested)
                    && _depth < nested.Depth
                )
                {
                    long emission = _bus.EmissionId;
                    int next = (_depth & 1) == 0 ? nested.NestedToken : nested.Token;
                    BusTraceOperation registration =
                        nested.HandleSlot >= 0
                            ? _explicitRegistrations[
                                useNested ? nested.HandleSlot : nested.SourceHandleSlot
                            ]
                            : _registrations[next];
                    _callbacks.Add($"nested-enter:depth={_depth},emission={emission}");
                    ++_depth;
                    try
                    {
                        EmitNested(
                            new BusTraceOperation(
                                BusTraceOperationKind.Emit,
                                next,
                                registration.Context,
                                unchecked(value + 1),
                                kindOffset: registration.KindOffset
                            )
                        );
                    }
                    finally
                    {
                        --_depth;
                        _callbacks.Add($"nested-return:depth={_depth},emission={_bus.EmissionId}");
                    }
                }
            }
            if (
                _throwOperation.HasValue
                && MatchesCallback(_throwOperation.Value, token, callbackIdentity)
            )
            {
                BusTraceOperation operation = _throwOperation.Value;
                _throwOperation = null;
                try
                {
                    throw new InvalidOperationException("intentional trace callback failure");
                }
                finally
                {
                    CleanupThrowingCallback(operation);
                }
            }
            if (
                _resetOperation.HasValue
                && MatchesCallback(_resetOperation.Value, token, callbackIdentity)
            )
            {
                _resetOperation = null;
                _reset();
            }
        }

        protected virtual bool MatchesCallback(
            BusTraceOperation operation,
            int token,
            int callbackIdentity,
            bool useNested = false
        )
        {
            int owner = useNested ? operation.NestedToken : operation.Token;
            if (token != owner)
            {
                return false;
            }
            if (operation.HandleSlot < 0)
            {
                return callbackIdentity < 0;
            }
            int slot = useNested ? operation.SourceHandleSlot : operation.HandleSlot;
            // Copies and duplicates retain delegate identity even after the original slot
            // is reused. Actual token state excludes handles consumed by callback cleanup.
            return callbackIdentity == _explicitCallbackIdentities[slot]
                && _tokens[owner]._metadata.ContainsKey(_explicitHandles[slot]);
        }

        internal readonly struct UntargetedPayload : IUntargetedMessage<UntargetedPayload>
        {
            internal UntargetedPayload(int value)
            {
                Value = value;
            }

            internal int Value { get; }
        }

        internal readonly struct TargetedPayload : ITargetedMessage<TargetedPayload>
        {
            internal TargetedPayload(int value)
            {
                Value = value;
            }

            internal int Value { get; }
        }

        internal readonly struct BroadcastPayload : IBroadcastMessage<BroadcastPayload>
        {
            internal BroadcastPayload(int value)
            {
                Value = value;
            }

            internal int Value { get; }
        }
    }
}
#endif
