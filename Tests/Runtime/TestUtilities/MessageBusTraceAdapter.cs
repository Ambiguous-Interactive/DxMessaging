#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;

    /// <summary>Runs trace operations through production bus and token APIs; callback output is never simulated.</summary>
    internal sealed class MessageBusTraceAdapter : IBusTraceAdapter
    {
        private readonly MessageScenario _scenario;
        private readonly IMessageBus _bus;
        private readonly IMessageBus _emitter;
        private readonly Action _reset;
        private int _resetOnCallbackToken = -1;
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
            _leaks = new LeakWatcher(bus: bus, label: "Differential replay " + scenario.Kind);
            try
            {
                for (int slot = 0; slot < _tokens.Length; ++slot)
                {
                    MessageHandler handler = new(new InstanceId(1000 + slot), bus)
                    {
                        active = true,
                    };
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

        /// <summary>Records callbacks, exact exception outcome, six public counters, occupancy, and token activity.</summary>
        public BusTraceObservation Execute(BusTraceOperation operation)
        {
            _callbacks.Clear();
            string exception = null;
            try
            {
                switch (operation.Kind)
                {
                    case BusTraceOperationKind.Register:
                        Register(operation);
                        break;
                    case BusTraceOperationKind.Remove:
                        _tokens[operation.Token].RemoveRegistration(_handles[operation.Token]);
                        break;
                    case BusTraceOperationKind.Enable:
                        _tokens[operation.Token].Enable();
                        break;
                    case BusTraceOperationKind.Disable:
                        _tokens[operation.Token].Disable();
                        break;
                    case BusTraceOperationKind.Emit:
                        Emit(operation);
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
            string state =
                $"counts={_bus.RegisteredUntargeted},{_bus.RegisteredTargeted},{_bus.RegisteredBroadcast},{_bus.RegisteredInterceptors},{_bus.RegisteredPostProcessors},{_bus.RegisteredGlobalAcceptAll}; slots={_bus.OccupiedTypeSlots},{_bus.OccupiedTargetSlots}; enabled={enabled}; diagnostics={_bus.DiagnosticsMode}";
            return new BusTraceObservation(_callbacks, state, exception);
        }

        /// <summary>Always attempts every token cleanup and reports cleanup or registration-leak failures.</summary>
        public void Dispose()
        {
            List<Exception> errors = new();
            foreach (MessageRegistrationToken token in _tokens)
            {
                try
                {
                    token?.Dispose();
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
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

        private void Register(BusTraceOperation operation)
        {
            int slot = operation.Token;
            MessageRegistrationToken token = _tokens[slot];
            InstanceId context = new(2000 + operation.Context);
            switch (_scenario.Kind)
            {
                case MessageKind.Untargeted:
                    _handles[slot] = ScenarioHarness.RegisterUntargeted<UntargetedPayload>(
                        _scenario,
                        token,
                        (in UntargetedPayload message) => Record(slot, message.Value),
                        operation.Priority
                    );
                    break;
                case MessageKind.Targeted:
                    _handles[slot] = ScenarioHarness.RegisterTargeted<TargetedPayload>(
                        _scenario,
                        token,
                        context,
                        (in TargetedPayload message) => Record(slot, message.Value),
                        operation.Priority
                    );
                    break;
                case MessageKind.Broadcast:
                    _handles[slot] = ScenarioHarness.RegisterBroadcast<BroadcastPayload>(
                        _scenario,
                        token,
                        context,
                        (in BroadcastPayload message) => Record(slot, message.Value),
                        operation.Priority
                    );
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(_scenario));
            }
        }

        private void Emit(BusTraceOperation operation)
        {
            InstanceId context = new(2000 + operation.Context);
            switch (_scenario.Kind)
            {
                case MessageKind.Untargeted:
                    UntargetedPayload untargeted = new(operation.Value);
                    ScenarioHarness.EmitUntargeted(_scenario, ref untargeted, _emitter);
                    break;
                case MessageKind.Targeted:
                    TargetedPayload targeted = new(operation.Value);
                    ScenarioHarness.EmitTargeted(_scenario, ref targeted, context, _emitter);
                    break;
                case MessageKind.Broadcast:
                    BroadcastPayload broadcast = new(operation.Value);
                    ScenarioHarness.EmitBroadcast(_scenario, ref broadcast, context, _emitter);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(_scenario));
            }
        }

        private void Record(int token, int value)
        {
            _callbacks.Add($"token={token},value={value}");
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
