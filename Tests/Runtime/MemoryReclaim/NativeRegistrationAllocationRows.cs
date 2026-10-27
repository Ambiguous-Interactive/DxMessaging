#if UNITY_6000_5 && UNITY_STANDALONE_WIN && ENABLE_IL2CPP
namespace DxMessaging.Tests.Runtime.MemoryReclaim
{
    using System;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Core.Pooling;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;

#pragma warning disable RCS1242 // Production fast handlers require readonly references.
    /// <summary>Untimed first-registration and activation windows over prepared independent owners.</summary>
    internal static class NativeRegistrationAllocationRows
    {
        private const int Iterations = 32;
        private const int Warmups = 64;
        private static readonly InstanceId Context = new(1700082);

        internal enum Phase
        {
            StageFirst,
            RegisterLiveFirst,
            EnableStaged,
            DisableActive,
        }

        internal static void Measure(Func<int> begin, Func<long> end, Func<long> foreign)
        {
            using DiagnosticsScope diagnostics = new(
                DiagnosticsTarget.Off,
                diagnosticsStackTraces: false
            );
            bool previousLogging = MessagingDebug.enabled;
            MessagingDebug.enabled = false;
            try
            {
                MeasureRows(begin, end, foreign);
            }
            finally
            {
                MessagingDebug.enabled = previousLogging;
            }
        }

        private static void MeasureRows(Func<int> begin, Func<long> end, Func<long> foreign)
        {
            object[] sink = new object[Iterations];
            int forcedIndex = 0;
            Action force = () => sink[forcedIndex % Iterations] = new ForcedObject(forcedIndex++);
            Action empty = Empty;
            for (int index = 0; index < Warmups; ++index)
            {
                force();
            }
            int rows = 0;
            foreach (MessageScenario scenario in MessageScenarios.AllKindsIncludingWithoutContext)
            {
                for (int representation = 0; representation < 2; ++representation)
                {
                    for (int callback = 0; callback < 2; ++callback)
                    {
                        for (int phaseIndex = 0; phaseIndex < 4; ++phaseIndex)
                        {
                            Phase phase = (Phase)phaseIndex;
                            for (int index = 0; index < Warmups; ++index)
                            {
                                using Owner warm = Create(
                                    scenario,
                                    representation == 1,
                                    callback == 1,
                                    phase
                                );
                                warm.Operate();
                                warm.Validate();
                            }

                            Owner[] owners = new Owner[Iterations];
                            int operationIndex = 0;
                            Action operation = () => owners[operationIndex++].Operate();
                            try
                            {
                                for (int index = 0; index < Iterations; ++index)
                                {
                                    owners[index] = Create(
                                        scenario,
                                        representation == 1,
                                        callback == 1,
                                        phase
                                    );
                                }
                                long emptyBefore = NativeDispatchAllocationRows.MeasureWindow(
                                    empty,
                                    begin,
                                    end
                                );
                                forcedIndex = 0;
                                long forcedBefore = NativeDispatchAllocationRows.MeasureWindow(
                                    force,
                                    begin,
                                    end
                                );
                                ValidateForced(sink);
                                Assert.That(emptyBefore, Is.Zero);
                                Assert.That(forcedBefore, Is.EqualTo(Iterations));
                                long events = NativeDispatchAllocationRows.MeasureWindow(
                                    operation,
                                    begin,
                                    end
                                );
                                long foreignEvents = foreign();
                                forcedIndex = 0;
                                long forcedAfter = NativeDispatchAllocationRows.MeasureWindow(
                                    force,
                                    begin,
                                    end
                                );
                                ValidateForced(sink);
                                long emptyAfter = NativeDispatchAllocationRows.MeasureWindow(
                                    empty,
                                    begin,
                                    end
                                );
                                Assert.That(forcedAfter, Is.EqualTo(Iterations));
                                Assert.That(emptyAfter, Is.Zero);
                                Assert.That(operationIndex, Is.EqualTo(Iterations));
                                Assert.That(foreignEvents, Is.Zero);
                                Assert.That(events, Is.GreaterThanOrEqualTo(0L));
                                foreach (Owner owner in owners)
                                {
                                    owner.Validate();
                                }
                                TestContext.Out.WriteLine(
                                    $"NATIVE_REGISTRATION_EVENTS row={scenario.Kind}/{(representation == 0 ? "Struct" : "Class")}/{(callback == 0 ? "Action" : "Fast")}/{phase}, "
                                        + $"owners={Iterations}, operations={operationIndex}, warmups={Warmups}, events={events}, foreign={foreignEvents}, "
                                        + $"emptyBefore={emptyBefore}, forcedBefore={forcedBefore}, forcedAfter={forcedAfter}, emptyAfter={emptyAfter}, "
                                        + "stateValidated=True, deliveries=32, payloadMatched=True, contextMatched=True"
                                );
                                ++rows;
                            }
                            finally
                            {
                                foreach (Owner owner in owners)
                                {
                                    owner?.Dispose();
                                }
                            }
                        }
                    }
                }
            }
            Assert.That(rows, Is.EqualTo(80));
            GC.KeepAlive(sink);
        }

        private static void Empty() { }

        private static void ValidateForced(object[] sink)
        {
            for (int index = 0; index < Iterations; ++index)
            {
                Assert.That(((ForcedObject)sink[index]).Value, Is.EqualTo(index));
                for (int prior = 0; prior < index; ++prior)
                {
                    Assert.That(ReferenceEquals(sink[index], sink[prior]), Is.False);
                }
            }
        }

        private static Owner Create(
            MessageScenario scenario,
            bool reference,
            bool fast,
            Phase phase
        )
        {
            Owner owner;
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                    owner = reference
                        ? Untargeted(new ClassUntargetedMessage("native-registration"), fast, phase)
                        : Untargeted(new SimpleUntargetedMessage(), fast, phase);
                    break;
                case MessageKind.Targeted:
                case MessageKind.TargetedWithoutTargeting:
                    owner = reference
                        ? Targeted(
                            new ClassTargetedMessage("native-registration"),
                            scenario.Kind,
                            fast,
                            phase
                        )
                        : Targeted(new SimpleTargetedMessage(), scenario.Kind, fast, phase);
                    break;
                case MessageKind.Broadcast:
                case MessageKind.BroadcastWithoutSource:
                    owner = reference
                        ? Broadcast(
                            new ClassBroadcastMessage("native-registration"),
                            scenario.Kind,
                            fast,
                            phase
                        )
                        : Broadcast(new SimpleBroadcastMessage(), scenario.Kind, fast, phase);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario));
            }
            try
            {
                owner.Prepare();
                return owner;
            }
            catch
            {
                owner.Dispose();
                throw;
            }
        }

        private static Owner Untargeted<T>(T message, bool fast, Phase phase)
            where T : IUntargetedMessage
        {
            Owner owner = new(typeof(T).IsValueType ? null : message, phase);
            Action<T> action = value => owner.Record(in value);
            MessageHandler.FastHandler<T> typed = (in T value) => owner.Record(in value);
            owner.Register = fast
                ? () => owner.Handle = owner.Token.RegisterUntargeted(typed)
                : () => owner.Handle = owner.Token.RegisterUntargeted(action);
            owner.Emit = () => owner.Bus.UntargetedBroadcast(ref message);
            return owner;
        }

        private static Owner Targeted<T>(T message, MessageKind kind, bool fast, Phase phase)
            where T : ITargetedMessage
        {
            Owner owner = new(typeof(T).IsValueType ? null : message, phase);
            if (kind == MessageKind.TargetedWithoutTargeting)
            {
                Action<InstanceId, T> action = (context, value) =>
                    owner.Record(in context, in value);
                MessageHandler.FastHandlerWithContext<T> typed = (
                    in InstanceId context,
                    in T value
                ) => owner.Record(in context, in value);
                owner.Register = fast
                    ? () => owner.Handle = owner.Token.RegisterTargetedWithoutTargeting(typed)
                    : () => owner.Handle = owner.Token.RegisterTargetedWithoutTargeting(action);
            }
            else
            {
                Action<T> action = value => owner.Record(in value);
                MessageHandler.FastHandler<T> typed = (in T value) => owner.Record(in value);
                owner.Register = fast
                    ? () => owner.Handle = owner.Token.RegisterTargeted(Context, typed)
                    : () => owner.Handle = owner.Token.RegisterTargeted(Context, action);
            }
            InstanceId emittedContext = Context;
            owner.Emit = () => owner.Bus.TargetedBroadcast(ref emittedContext, ref message);
            return owner;
        }

        private static Owner Broadcast<T>(T message, MessageKind kind, bool fast, Phase phase)
            where T : IBroadcastMessage
        {
            Owner owner = new(typeof(T).IsValueType ? null : message, phase);
            if (kind == MessageKind.BroadcastWithoutSource)
            {
                Action<InstanceId, T> action = (context, value) =>
                    owner.Record(in context, in value);
                MessageHandler.FastHandlerWithContext<T> typed = (
                    in InstanceId context,
                    in T value
                ) => owner.Record(in context, in value);
                owner.Register = fast
                    ? () => owner.Handle = owner.Token.RegisterBroadcastWithoutSource(typed)
                    : () => owner.Handle = owner.Token.RegisterBroadcastWithoutSource(action);
            }
            else
            {
                Action<T> action = value => owner.Record(in value);
                MessageHandler.FastHandler<T> typed = (in T value) => owner.Record(in value);
                owner.Register = fast
                    ? () => owner.Handle = owner.Token.RegisterBroadcast(Context, typed)
                    : () => owner.Handle = owner.Token.RegisterBroadcast(Context, action);
            }
            InstanceId emittedContext = Context;
            owner.Emit = () => owner.Bus.SourcedBroadcast(ref emittedContext, ref message);
            return owner;
        }

        private sealed class Owner : IDisposable
        {
            internal readonly MessageBus Bus = MessageBus.CreateForInternalUse(
                StopwatchClock.Instance,
                idleEvictionTicks: 0,
                evictionTickIntervalSeconds: double.PositiveInfinity,
                idleEvictionEnabled: false,
                trimApiEnabled: true
            );
            internal readonly MessageRegistrationToken Token;
            internal MessageRegistrationHandle Handle;
            internal Action Register;
            internal Action Emit;
            private readonly Phase _phase;
            private readonly object _expectedPayload;
            private readonly LeakWatcher _watcher;
            private int _deliveries;
            private bool _payloadMatched = true;
            private bool _contextMatched = true;

            internal Owner(object expectedPayload, Phase phase)
            {
                _phase = phase;
                _expectedPayload = expectedPayload;
                _watcher = new LeakWatcher(bus: Bus, label: "native-registration");
                MessageHandler handler = new(new InstanceId(1700081), Bus) { active = true };
                Token = MessageRegistrationToken.Create(handler, Bus);
            }

            internal void Prepare()
            {
                Assert.That(Token.Enabled, Is.False);
                Assert.That(Bus.DiagnosticsMode, Is.False);
                Assert.That(Token.DiagnosticMode, Is.False);
                if (_phase == Phase.RegisterLiveFirst)
                {
                    Token.Enable();
                }
                else if (_phase == Phase.EnableStaged || _phase == Phase.DisableActive)
                {
                    Register();
                    if (_phase == Phase.DisableActive)
                    {
                        Token.Enable();
                    }
                }
            }

            internal void Operate()
            {
                switch (_phase)
                {
                    case Phase.StageFirst:
                    case Phase.RegisterLiveFirst:
                        Register();
                        break;
                    case Phase.EnableStaged:
                        Token.Enable();
                        break;
                    case Phase.DisableActive:
                        Token.Disable();
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(_phase));
                }
            }

            internal void Validate()
            {
                Assert.That(Handle, Is.Not.EqualTo(default(MessageRegistrationHandle)));
                bool disabled = _phase == Phase.StageFirst || _phase == Phase.DisableActive;
                Assert.That(Token.Enabled, Is.EqualTo(!disabled));
                if (disabled)
                {
                    Emit();
                    Assert.That(_deliveries, Is.Zero);
                    Token.Enable();
                }
                Emit();
                Assert.That(_deliveries, Is.EqualTo(1));
                Assert.That(_payloadMatched, Is.True);
                Assert.That(_contextMatched, Is.True);
            }

            internal void Record<T>(in T message)
                where T : IMessage
            {
                ++_deliveries;
                if (_expectedPayload != null && !ReferenceEquals(_expectedPayload, message))
                {
                    _payloadMatched = false;
                }
            }

            internal void Record<T>(in InstanceId context, in T message)
                where T : IMessage
            {
                _contextMatched &= context == Context;
                Record(in message);
            }

            public void Dispose()
            {
                try
                {
                    Token.Dispose();
                }
                finally
                {
                    _watcher.Dispose();
                }
            }
        }

        private sealed class ForcedObject
        {
            internal readonly int Value;

            internal ForcedObject(int value) => Value = value;
        }
    }
#pragma warning restore RCS1242
}
#endif
