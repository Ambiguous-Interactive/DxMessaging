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
        private int _resetOnCallbackToken = -1;
        private int _throwOnCallbackToken = -1;
        private int _disableOnCallbackToken = -1;
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
        private readonly List<string> _callbacks = new();
        private readonly LeakWatcher _leaks;

        internal MessageBusTraceAdapter(
            MessageScenario scenario,
            IMessageBus bus,
            IMessageBus emitter = null,
            Action reset = null
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
                    MessageHandler handler = new(new InstanceId(1000 + slot), bus)
                    {
                        active = true,
                    };
                    _handlers[slot] = handler;
                    _tokens[slot] = MessageRegistrationToken.Create(handler, bus);
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
            string exception = null;
            IMessageBus.TrimResult? trimResult = null;
            try
            {
                switch (operation.Kind)
                {
                    case BusTraceOperationKind.Register:
                        Register(operation);
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
                        _disableOnCallbackToken = operation.Token;
                        try
                        {
                            Emit(operation);
                        }
                        finally
                        {
                            _disableOnCallbackToken = -1;
                        }
                        break;
                    case BusTraceOperationKind.EmitWithThrow:
                        _throwOnCallbackToken = operation.Token;
                        try
                        {
                            Emit(operation);
                        }
                        finally
                        {
                            _throwOnCallbackToken = -1;
                        }
                        break;
                    case BusTraceOperationKind.EmitWithReset:
                        if (_reset == null)
                        {
                            throw new NotSupportedException(
                                "This adapter has no local reset action."
                            );
                        }
                        _resetOnCallbackToken = operation.Token;
                        try
                        {
                            Emit(operation);
                        }
                        finally
                        {
                            // A disabled or differently routed trigger may never receive a callback.
                            _resetOnCallbackToken = -1;
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
            string handlerActive = string.Empty;
            foreach (MessageHandler handler in _handlers)
            {
                handlerActive += handler.active ? "1" : "0";
            }
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
                $"counts={_bus.RegisteredUntargeted},{_bus.RegisteredTargeted},{_bus.RegisteredBroadcast},{_bus.RegisteredInterceptors},{_bus.RegisteredPostProcessors},{_bus.RegisteredGlobalAcceptAll}; slots={_bus.OccupiedTypeSlots},{_bus.OccupiedTargetSlots}; enabled={enabled}; handlerActive={handlerActive}; diagnostics={_bus.DiagnosticsMode}; tokenMetadataCallsHistory={diagnostics}; retainedMessages={retainedMessages}";
            return new BusTraceObservation(
                _callbacks,
                state,
                exception,
                trimResult,
                _bus.OccupiedTypeSlots,
                _bus.OccupiedTargetSlots
            );
        }

        /// <summary>Always attempts every token cleanup and reports cleanup or registration-leak failures.</summary>
        public void Dispose()
        {
            List<Exception> errors = new();
            foreach (MessageRegistrationToken token in _tokens)
            {
                try
                {
                    if (token == null)
                    {
                        continue;
                    }
                    token.Dispose();
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

        protected MessageRegistrationToken Token(int slot) => _tokens[slot];

        protected MessageRegistrationHandle Handle(int slot) => _handles[slot];

        protected IMessageBus Bus => _bus;

        protected virtual IMessageBus.TrimResult Trim(bool force) => _bus.Trim(force);

        protected virtual void Remove(BusTraceOperation operation)
        {
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

        private void Emit(BusTraceOperation operation)
        {
            MessageScenario scenario = Scenario(operation.KindOffset);
            InstanceId context = new(2000 + operation.Context);
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                    UntargetedPayload untargeted = new(operation.Value);
                    ScenarioHarness.EmitUntargeted(scenario, ref untargeted, _emitter);
                    break;
                case MessageKind.Targeted:
                    TargetedPayload targeted = new(operation.Value);
                    ScenarioHarness.EmitTargeted(scenario, ref targeted, context, _emitter);
                    break;
                case MessageKind.Broadcast:
                    BroadcastPayload broadcast = new(operation.Value);
                    ScenarioHarness.EmitBroadcast(scenario, ref broadcast, context, _emitter);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(_scenario));
            }
        }

        private void Record(int token, int value, IMessage message)
        {
            _callbacks.Add($"token={token},value={value}");
            OnCallback(token, message);
            if (_handlerActiveOperation.HasValue && token == _handlerActiveOperation.Value.Token)
            {
                BusTraceOperation operation = _handlerActiveOperation.Value;
                _handlerActiveOperation = null;
                SetHandlerActive(operation.HandlerToken, operation.HandlerActive);
            }
            if (token == _disableOnCallbackToken)
            {
                _disableOnCallbackToken = -1;
                DisableFromCallback(token);
            }
            if (_nestedOperation.HasValue)
            {
                BusTraceOperation nested = _nestedOperation.Value;
                int trigger = (_depth & 1) == 0 ? nested.Token : nested.NestedToken;
                if (token == trigger && _depth < nested.Depth)
                {
                    long emission = _bus.EmissionId;
                    int next = (_depth & 1) == 0 ? nested.NestedToken : nested.Token;
                    BusTraceOperation registration = _registrations[next];
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
            if (token == _throwOnCallbackToken)
            {
                _throwOnCallbackToken = -1;
                try
                {
                    throw new InvalidOperationException("intentional trace callback failure");
                }
                finally
                {
                    CleanupThrowingCallback(token);
                }
            }
            if (token == _resetOnCallbackToken)
            {
                _resetOnCallbackToken = -1;
                _reset();
            }
        }

        private readonly struct UntargetedPayload : IUntargetedMessage<UntargetedPayload>
        {
            internal UntargetedPayload(int value)
            {
                Value = value;
            }

            internal int Value { get; }
        }

        private readonly struct TargetedPayload : ITargetedMessage<TargetedPayload>
        {
            internal TargetedPayload(int value)
            {
                Value = value;
            }

            internal int Value { get; }
        }

        private readonly struct BroadcastPayload : IBroadcastMessage<BroadcastPayload>
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
