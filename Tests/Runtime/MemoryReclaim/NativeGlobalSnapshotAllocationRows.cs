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
    /// <summary>Untimed native windows after the maintained nested global snapshot mutation.</summary>
    internal static class NativeGlobalSnapshotAllocationRows
    {
        private const int Iterations = 32;
        private const int Warmups = 64;
        private static readonly InstanceId Context = new(1700062);

        internal sealed class Observation
        {
            internal string Name;
            internal long Events;
        }

        internal static Observation[] Measure(Func<int> begin, Func<long> end, Func<long> foreign)
        {
            using DiagnosticsScope diagnostics = new(
                DiagnosticsTarget.Off,
                diagnosticsStackTraces: false
            );
            Observation[] observations = new Observation[18];
            object[] forcedSink = new object[Iterations];
            int forcedIndex = 0;
            Action force = () =>
                forcedSink[forcedIndex % Iterations] = new ForcedObject(forcedIndex++);
            Action empty = Empty;
            for (int index = 0; index < Warmups; ++index)
            {
                force();
            }
            int rowIndex = 0;
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            {
                for (int shape = 0; shape < 3; ++shape)
                {
                    foreach (int count in new[] { 3, 8 })
                    {
                        using SnapshotRow row = Create(scenario, shape, count);
                        row.Prepare();
                        for (int index = 0; index < Warmups; ++index)
                        {
                            row.Emit();
                        }
                        row.ValidateSteady(Warmups);
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
                        ValidateForced(forcedSink);
                        Assert.That(emptyBefore, Is.Zero);
                        Assert.That(forcedBefore, Is.EqualTo(Iterations));
                        long events = NativeDispatchAllocationRows.MeasureWindow(
                            row.Emit,
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
                        ValidateForced(forcedSink);
                        long emptyAfter = NativeDispatchAllocationRows.MeasureWindow(
                            empty,
                            begin,
                            end
                        );
                        Assert.That(forcedAfter, Is.EqualTo(Iterations));
                        Assert.That(emptyAfter, Is.Zero);
                        row.ValidateSteady(Warmups + Iterations);
                        observations[rowIndex++] = new Observation
                        {
                            Name = row.Name,
                            Events = events,
                        };
                        TestContext.Out.WriteLine(
                            $"NATIVE_GLOBAL_SNAPSHOT_EVENTS row={row.Name}, iterations={Iterations}, warmups={Warmups}, "
                                + $"events={events}, foreign={foreignEvents}, emptyBefore={emptyBefore}, forcedBefore={forcedBefore}, "
                                + $"forcedAfter={forcedAfter}, emptyAfter={emptyAfter}, typed={row.Typed}, global={row.Global}, "
                                + $"originalPeers={row.OriginalPeers}, replacement={row.Replacement}, removed={row.Removed}, "
                                + $"mutations={row.Mutations}, nested={row.Nested}, payloadMatched={row.PayloadMatched}, contextMatched={row.ContextMatched}"
                        );
                    }
                }
            }
            Assert.That(rowIndex, Is.EqualTo(observations.Length));
            GC.KeepAlive(forcedSink);
            return observations;
        }

        internal static void AssertZeroEvents(Observation[] observations)
        {
            foreach (Observation row in observations)
            {
                Assert.That(
                    row.Events,
                    Is.Zero,
                    $"{row.Name}: warmed native global snapshot allocation events."
                );
            }
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

        private static SnapshotRow Create(MessageScenario scenario, int shape, int count)
        {
            SnapshotRow row;
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                {
                    ClassUntargetedMessage message = new("native-global");
                    row = new SnapshotRow(scenario, shape, count, message);
                    _ = row.Token.RegisterUntargeted<ClassUntargetedMessage>(
                        (in ClassUntargetedMessage value) => row.RecordTyped(in value)
                    );
                    row.Emit = () => row.Bus.UntargetedBroadcast(ref message);
                    break;
                }
                case MessageKind.Targeted:
                {
                    ClassTargetedMessage message = new("native-global");
                    row = new SnapshotRow(scenario, shape, count, message);
                    _ = row.Token.RegisterTargeted<ClassTargetedMessage>(
                        Context,
                        (in ClassTargetedMessage value) => row.RecordTyped(in value)
                    );
                    InstanceId context = Context;
                    row.Emit = () => row.Bus.TargetedBroadcast(ref context, ref message);
                    break;
                }
                case MessageKind.Broadcast:
                {
                    ClassBroadcastMessage message = new("native-global");
                    row = new SnapshotRow(scenario, shape, count, message);
                    _ = row.Token.RegisterBroadcast<ClassBroadcastMessage>(
                        Context,
                        (in ClassBroadcastMessage value) => row.RecordTyped(in value)
                    );
                    InstanceId context = Context;
                    row.Emit = () => row.Bus.SourcedBroadcast(ref context, ref message);
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario));
            }
            row.RegisterCallbacks();
            return row;
        }

        private sealed class SnapshotRow : IDisposable
        {
            internal readonly MessageBus Bus = MessageBus.CreateForInternalUse(
                StopwatchClock.Instance,
                idleEvictionTicks: 0,
                evictionTickIntervalSeconds: double.PositiveInfinity,
                idleEvictionEnabled: false,
                trimApiEnabled: true
            );
            internal readonly MessageRegistrationToken Token;
            internal readonly string Name;
            private readonly int _shape;
            private readonly int _count;
            private readonly object _payload;
            private readonly LeakWatcher _watcher;
            private MessageRegistrationHandle _removed;
            private int _mutator;
            internal Action Emit;
            internal int Typed;
            internal int OriginalPeers;
            internal int Replacement;
            internal int Removed;
            internal int Mutations;
            internal int Nested;
            internal bool PayloadMatched = true;
            internal bool ContextMatched = true;
            internal int Global => _mutator + OriginalPeers + Replacement + Removed;

            internal SnapshotRow(MessageScenario scenario, int shape, int count, object payload)
            {
                _shape = shape;
                _count = count;
                _payload = payload;
                Name = scenario.Kind + "/Shape" + shape + "/Count" + count;
                _watcher = new LeakWatcher(bus: Bus, label: Name);
                MessageHandler handler = new(new InstanceId(1700061), Bus) { active = true };
                Token = MessageRegistrationToken.Create(handler, Bus);
            }

            internal void RegisterCallbacks()
            {
                _ = Register(_shape != 0, Mutate);
                for (int index = 0; index < _count - 1; ++index)
                {
                    bool removed = index == _count - 2;
                    _removed = Register(
                        _shape == 1,
                        () =>
                        {
                            if (removed)
                            {
                                ++Removed;
                            }
                            else
                            {
                                ++OriginalPeers;
                            }
                        }
                    );
                }
            }

            private void Mutate()
            {
                ++_mutator;
                if (Mutations != 0)
                {
                    return;
                }
                ++Mutations;
                Token.RemoveRegistration(_removed);
                _ = Register(_shape == 1, () => ++Replacement);
                ++Nested;
                Emit();
            }

            private MessageRegistrationHandle Register(bool fast, Action callback)
            {
                if (fast)
                {
                    return Token.RegisterGlobalAcceptAll(
                        (in IUntargetedMessage value) =>
                        {
                            Record(in value);
                            callback();
                        },
                        (in InstanceId context, in ITargetedMessage value) =>
                        {
                            Record(in context, in value);
                            callback();
                        },
                        (in InstanceId context, in IBroadcastMessage value) =>
                        {
                            Record(in context, in value);
                            callback();
                        }
                    );
                }
                return Token.RegisterGlobalAcceptAll(
                    (IUntargetedMessage value) =>
                    {
                        Record(in value);
                        callback();
                    },
                    (InstanceId context, ITargetedMessage value) =>
                    {
                        Record(in context, in value);
                        callback();
                    },
                    (InstanceId context, IBroadcastMessage value) =>
                    {
                        Record(in context, in value);
                        callback();
                    }
                );
            }

            internal void Prepare()
            {
                Token.Enable();
                Assert.That(Bus.DiagnosticsMode, Is.False);
                Assert.That(Token.DiagnosticMode, Is.False);
                Emit();
                Assert.That(Mutations, Is.EqualTo(1));
                Assert.That(Nested, Is.EqualTo(1));
                Assert.That(Typed, Is.EqualTo(2));
                Assert.That(_mutator, Is.EqualTo(2));
                Assert.That(OriginalPeers, Is.EqualTo(2 * (_count - 2)));
                Assert.That(Replacement, Is.EqualTo(1));
                Assert.That(Removed, Is.EqualTo(1));
                Assert.That(PayloadMatched, Is.True);
                Assert.That(ContextMatched, Is.True);
                Typed = _mutator = OriginalPeers = Replacement = Removed = 0;
            }

            internal void ValidateSteady(int emissions)
            {
                Assert.That(Typed, Is.EqualTo(emissions));
                Assert.That(_mutator, Is.EqualTo(emissions));
                Assert.That(OriginalPeers, Is.EqualTo(emissions * (_count - 2)));
                Assert.That(Replacement, Is.EqualTo(emissions));
                Assert.That(Removed, Is.Zero);
                Assert.That(Global, Is.EqualTo(emissions * _count));
                Assert.That(Mutations, Is.EqualTo(1));
                Assert.That(Nested, Is.EqualTo(1));
                Assert.That(PayloadMatched, Is.True);
                Assert.That(ContextMatched, Is.True);
            }

            internal void RecordTyped<T>(in T message)
                where T : IMessage
            {
                ++Typed;
                Record(in message);
            }

            private void Record<T>(in T message)
                where T : IMessage
            {
                PayloadMatched &= ReferenceEquals(_payload, message);
            }

            private void Record<T>(in InstanceId context, in T message)
                where T : IMessage
            {
                ContextMatched &= context == Context;
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
