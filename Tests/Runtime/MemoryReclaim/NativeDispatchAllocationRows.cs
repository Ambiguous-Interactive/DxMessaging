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
    /// <summary>Untimed native allocation-event rows over the real basic dispatch surface.</summary>
    internal static class NativeDispatchAllocationRows
    {
        private const int Iterations = 32;
        private const int Warmups = 64;
        private static readonly InstanceId Context = new(1700042);

        internal sealed class Observation
        {
            internal string Name;
            internal long Events;
            internal long Foreign;
        }

        internal static Observation[] Measure(Func<int> begin, Func<long> end, Func<long> foreign)
        {
            using DiagnosticsScope diagnostics = new(
                DiagnosticsTarget.Off,
                diagnosticsStackTraces: false
            );
            Observation[] observations = new Observation[10];
            object[] forcedSink = new object[Iterations];
            int forcedIndex = 0;
            Action force = () =>
                forcedSink[forcedIndex % Iterations] = new ForcedObject(forcedIndex++);
            Action empty = Empty;
            for (int index = 0; index < Warmups; ++index)
            {
                force();
                if (begin() != 0)
                {
                    throw new InvalidOperationException("Cannot warm counter delegate.");
                }
                _ = end();
            }
            int rowIndex = 0;
            foreach (MessageScenario scenario in MessageScenarios.AllKindsIncludingWithoutContext)
            {
                for (int representation = 0; representation < 2; ++representation)
                {
                    using ProbeRow row = Create(scenario, representation == 1);
                    row.Token.Enable();
                    Assert.That(row.Bus.DiagnosticsMode, Is.False);
                    Assert.That(row.Token.DiagnosticMode, Is.False);
                    for (int index = 0; index < Warmups; ++index)
                    {
                        row.Emit();
                    }
                    Assert.That(row.Deliveries, Is.EqualTo(Warmups));
                    long emptyBefore = MeasureWindow(empty, begin, end);
                    forcedIndex = 0;
                    long forcedBefore = MeasureWindow(force, begin, end);
                    ValidateForced(forcedSink);
                    Assert.That(emptyBefore, Is.Zero, "Observer boundary before dispatch.");
                    Assert.That(
                        forcedBefore,
                        Is.EqualTo(Iterations),
                        "Forced control before dispatch."
                    );
                    long events = MeasureWindow(row.Emit, begin, end);
                    long foreignEvents = foreign();
                    forcedIndex = 0;
                    long forcedAfter = MeasureWindow(force, begin, end);
                    ValidateForced(forcedSink);
                    long emptyAfter = MeasureWindow(empty, begin, end);
                    Assert.That(
                        forcedAfter,
                        Is.EqualTo(Iterations),
                        "Forced control after dispatch."
                    );
                    Assert.That(emptyAfter, Is.Zero, "Observer boundary after dispatch.");
                    Assert.That(row.Deliveries, Is.EqualTo(Warmups + Iterations));
                    Assert.That(row.PayloadMatched, Is.True, "Original class payload identity.");
                    Assert.That(
                        row.ContextMatched,
                        Is.True,
                        "Original without-context route identity."
                    );
                    Observation observation = new()
                    {
                        Name = row.Name,
                        Events = events,
                        Foreign = foreignEvents,
                    };
                    observations[rowIndex++] = observation;
                    TestContext.Out.WriteLine(
                        $"NATIVE_DISPATCH_EVENTS row={row.Name}, iterations={Iterations}, warmups={Warmups}, "
                            + $"events={events}, foreign={foreignEvents}, emptyBefore={emptyBefore}, "
                            + $"forcedBefore={forcedBefore}, forcedAfter={forcedAfter}, emptyAfter={emptyAfter}, "
                            + $"deliveries={row.Deliveries}, payloadMatched={row.PayloadMatched}, contextMatched={row.ContextMatched}"
                    );
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
                    $"{row.Name}: warmed native owner-thread allocation events."
                );
            }
        }

        internal static long MeasureWindow(Action operation, Func<int> begin, Func<long> end)
        {
            if (begin() != 0)
            {
                throw new InvalidOperationException("Cannot begin native event window.");
            }
            for (int index = 0; index < Iterations; ++index)
            {
                operation();
            }
            return end();
        }

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

        private static void Empty() { }

        private static ProbeRow Create(MessageScenario scenario, bool reference)
        {
            switch (scenario.Kind)
            {
                case MessageKind.Untargeted:
                    return reference
                        ? Untargeted(new ClassUntargetedMessage("native"), scenario)
                        : Untargeted(new SimpleUntargetedMessage(), scenario);
                case MessageKind.Targeted:
                case MessageKind.TargetedWithoutTargeting:
                    return reference
                        ? Targeted(new ClassTargetedMessage("native"), scenario)
                        : Targeted(new SimpleTargetedMessage(), scenario);
                case MessageKind.Broadcast:
                case MessageKind.BroadcastWithoutSource:
                    return reference
                        ? Broadcast(new ClassBroadcastMessage("native"), scenario)
                        : Broadcast(new SimpleBroadcastMessage(), scenario);
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario));
            }
        }

        private static ProbeRow Untargeted<T>(T message, MessageScenario scenario)
            where T : IUntargetedMessage
        {
            ProbeRow row = new(scenario, typeof(T).IsValueType ? null : message);
            _ = row.Token.RegisterUntargeted<T>((in T value) => row.Record(in value));
            row.Emit = () => row.Bus.UntargetedBroadcast(ref message);
            return row;
        }

        private static ProbeRow Targeted<T>(T message, MessageScenario scenario)
            where T : ITargetedMessage
        {
            ProbeRow row = new(scenario, typeof(T).IsValueType ? null : message);
            if (scenario.Kind == MessageKind.TargetedWithoutTargeting)
            {
                _ = row.Token.RegisterTargetedWithoutTargeting<T>(
                    (in InstanceId context, in T value) => row.Record(in context, in value)
                );
            }
            else
            {
                _ = row.Token.RegisterTargeted<T>(Context, (in T value) => row.Record(in value));
            }
            InstanceId emittedContext = Context;
            row.Emit = () => row.Bus.TargetedBroadcast(ref emittedContext, ref message);
            return row;
        }

        private static ProbeRow Broadcast<T>(T message, MessageScenario scenario)
            where T : IBroadcastMessage
        {
            ProbeRow row = new(scenario, typeof(T).IsValueType ? null : message);
            if (scenario.Kind == MessageKind.BroadcastWithoutSource)
            {
                _ = row.Token.RegisterBroadcastWithoutSource<T>(
                    (in InstanceId context, in T value) => row.Record(in context, in value)
                );
            }
            else
            {
                _ = row.Token.RegisterBroadcast<T>(Context, (in T value) => row.Record(in value));
            }
            InstanceId emittedContext = Context;
            row.Emit = () => row.Bus.SourcedBroadcast(ref emittedContext, ref message);
            return row;
        }

        private sealed class ProbeRow : IDisposable
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
            private readonly object _expectedPayload;
            private readonly LeakWatcher _watcher;
            internal Action Emit;
            internal int Deliveries;
            internal bool PayloadMatched = true;
            internal bool ContextMatched = true;

            internal ProbeRow(MessageScenario scenario, object expectedPayload)
            {
                _expectedPayload = expectedPayload;
                Name = scenario.Kind + "/" + (expectedPayload == null ? "Struct" : "Class");
                _watcher = new LeakWatcher(bus: Bus, label: Name);
                MessageHandler handler = new(new InstanceId(1700041), Bus) { active = true };
                Token = MessageRegistrationToken.Create(handler, Bus);
            }

            internal void Record<T>(in T message)
                where T : IMessage
            {
                ++Deliveries;
                if (_expectedPayload != null && !ReferenceEquals(_expectedPayload, message))
                {
                    PayloadMatched = false;
                }
            }

            internal void Record<T>(in InstanceId context, in T message)
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
