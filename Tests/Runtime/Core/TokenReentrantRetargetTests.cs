namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using DxMessaging.Core;
    using DxMessaging.Core.Diagnostics;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;
    using Bus = DxMessaging.Core.MessageBus.MessageBus;

    /// <summary>Preserves replay ownership when recovered cleanup permits a nested active retarget.</summary>
    /// <remarks>
    /// 2026-11-05: nested retarget rebuilt the owning replay list after failed cleanup
    /// published Enabled=true. Detaching that list alone also replayed pending entries twice.
    /// Assert acquisition membership and bus origins as well as exception and cleanup behavior.
    /// </remarks>
    [TestFixture]
#if !UNITY_2021_3_OR_NEWER
    [NonParallelizable]
#endif
    public sealed class TokenReentrantRetargetTests
    {
        private static IEnumerable<TestCaseData> Cases()
        {
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            foreach (int shape in new[] { 0, 1, 2, 3, 4 })
            foreach (int count in new[] { 2, 3 })
            foreach (bool after in new[] { false, true })
            foreach (bool reject in new[] { false, true })
            foreach (bool nested in new[] { false, true })
            foreach (bool outer in new[] { false, true })
            foreach (bool diagnostics in new[] { false, true })
            foreach (bool throwing in new[] { false, true })
                yield return new TestCaseData(
                    scenario,
                    shape,
                    count,
                    after,
                    reject,
                    nested,
                    outer,
                    diagnostics,
                    throwing
                ).SetName(
                    $"ReentrantRetarget_{scenario}_shape{shape}_count{count}_after{after}_reject{reject}_nested{nested}_outer{outer}_diagnostics{diagnostics}_throw{throwing}"
                );
        }

        [TestCaseSource(nameof(Cases))]
        public void NestedRetargetMovesOnlyAcceptedRegistrations(
            MessageScenario scenario,
            int shape,
            int count,
            bool after,
            bool reject,
            bool nested,
            bool outer,
            bool diagnostics,
            bool throwing
        )
        {
            Scope state = new Scope(
                scenario,
                shape,
                count,
                after,
                reject,
                nested,
                outer,
                diagnostics,
                throwing
            );
            try
            {
                state.Run();
            }
            finally
            {
                state.Dispose();
            }
            string report = state.Report;
            TestContext.WriteLine(report);
            AssertAll(() =>
            {
                Assert.That(state.Callbacks, Is.EqualTo(1), report);
                Assert.That(state.HandledCleanupMarker, Is.EqualTo(reject), report);
                Assert.That(state.EnabledAfterDisable, Is.EqualTo(reject), report);
                Assert.That(state.RetargetReturned, Is.EqualTo(nested), report);
                Assert.That(state.NestedAcquisitions, Is.EqualTo(reject && nested ? 1 : 0), report);
                Assert.That(state.Failure, throwing ? Is.SameAs(state.Marker) : Is.Null, report);
                Assert.That(state.EnabledAfter, Is.EqualTo(!throwing || outer), report);
                Assert.That(state.MetadataAfter, Is.EqualTo(count), report);
                bool recordsDiagnostics = diagnostics && shape != 2;
                int[] expectedDiagnosticCounts = new int[count];
                for (int index = 0; index < count; ++index)
                {
                    expectedDiagnosticCounts[index] = recordsDiagnostics
                        ? Enumerable.Range(0, 3).Sum(bus => state.ExpectedDelivery(bus)[index])
                        : 0;
                    Assert.That(
                        state.DiagnosticRecords.Count(record =>
                            record.registrationHandle == state.Handles[index]
                        ),
                        Is.EqualTo(expectedDiagnosticCounts[index]),
                        report
                    );
                }
                Assert.That(state.DiagnosticCounts, Is.EqualTo(expectedDiagnosticCounts), report);
                Assert.That(
                    state.DiagnosticRecords.Length,
                    Is.EqualTo(expectedDiagnosticCounts.Sum()),
                    report
                );
                foreach (MessageEmissionData record in state.DiagnosticRecords)
                {
                    Assert.That(record.traceId, Is.Zero, report);
                    Assert.That(
                        record.message.MessageType,
                        Is.EqualTo(typeof(ProbeMessage)),
                        report
                    );
                    InstanceId? expectedContext =
                        scenario.Kind == MessageKind.Untargeted
                            ? (InstanceId?)null
                            : new InstanceId(120007);
                    Assert.That(record.context, Is.EqualTo(expectedContext), report);
                }

                for (int bus = 0; bus < 3; ++bus)
                {
                    Assert.That(
                        state.FirstDelivery[bus],
                        Is.EqualTo(state.ExpectedDelivery(bus)),
                        report
                    );
                    Assert.That(
                        state.LiveAfter[bus],
                        Is.EqualTo(state.ExpectedDelivery(bus).Sum()),
                        report
                    );
                    Assert.That(state.DisabledDelivery[bus], Is.All.EqualTo(0), report);
                    int recoveryBus =
                        throwing && outer ? 0
                        : nested ? 2
                        : state.Source;
                    Assert.That(
                        state.RecoveryDelivery[bus],
                        Is.All.EqualTo(bus == recoveryBus ? 1 : 0),
                        report
                    );
                }
                Assert.That(state.RecoveryFailure, Is.Null, report);
                Assert.That(state.CleanupFailure, Is.Null, report);
                Assert.That(state.TokenCleanupCounters, Is.All.EqualTo(0), report);
                Assert.That(state.TokenCleanupLive, Is.All.EqualTo(0), report);
                Assert.That(state.FinalCounters, Is.All.EqualTo(0), report);
                Assert.That(state.InvalidHandles, Is.Zero, report);
                Assert.That(state.DuplicateRetirements, Is.Zero, report);
            });
        }

        private static IEnumerable<TestCaseData> RecoveryCases()
        {
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            foreach (int shape in new[] { 0, 1, 2, 3, 4 })
            foreach (int count in new[] { 2, 3 })
            foreach (bool after in new[] { false, true })
            foreach (bool outer in new[] { false, true })
            foreach (bool diagnostics in new[] { false, true })
            foreach (bool rejectedAfter in new[] { false, true })
                yield return new TestCaseData(
                    scenario,
                    shape,
                    count,
                    after,
                    outer,
                    diagnostics,
                    rejectedAfter
                ).SetName(
                    $"ReentrantRetargetRecovery_{scenario}_shape{shape}_count{count}_after{after}_outer{outer}_diagnostics{diagnostics}_rejectAfter{rejectedAfter}"
                );
        }

        [TestCaseSource(nameof(RecoveryCases))]
        public void FailedNestedRetargetRestoresOnlyOwnedActiveHandles(
            MessageScenario scenario,
            int shape,
            int count,
            bool after,
            bool outer,
            bool diagnostics,
            bool rejectedAfter
        )
        {
            Scope state = new Scope(
                scenario,
                shape,
                count,
                after,
                true,
                true,
                outer,
                diagnostics,
                false,
                rejectedAfter
            );
            try
            {
                state.Run();
            }
            finally
            {
                state.Dispose();
            }
            string report = state.Report;
            TestContext.WriteLine(report);
            AssertAll(() =>
            {
                Assert.That(state.Callbacks, Is.EqualTo(1), report);
                Assert.That(state.HandledCleanupMarker, Is.True, report);
                Assert.That(state.EnabledAfterDisable, Is.True, report);
                Assert.That(state.RetargetReturned, Is.False, report);
                Assert.That(state.NestedRetargetFailure, Is.SameAs(state.NestedMarker), report);
                Assert.That(state.NestedAcquisitions, Is.EqualTo(rejectedAfter ? 1 : 0), report);
                Assert.That(state.Failure, Is.Null, report);
                Assert.That(state.EnabledAfter, Is.True, report);
                Assert.That(state.MetadataAfter, Is.EqualTo(count), report);
                for (int bus = 0; bus < 3; ++bus)
                {
                    Assert.That(
                        state.FirstDelivery[bus],
                        Is.All.EqualTo(bus == state.Source ? 1 : 0),
                        report
                    );
                    Assert.That(
                        state.LiveAfter[bus],
                        Is.EqualTo(bus == state.Source ? count : 0),
                        report
                    );
                    Assert.That(state.DisabledDelivery[bus], Is.All.EqualTo(0), report);
                    Assert.That(
                        state.RecoveryDelivery[bus],
                        Is.All.EqualTo(bus == state.Source ? 1 : 0),
                        report
                    );
                }
                bool records = diagnostics && shape != 2;
                Assert.That(state.DiagnosticCounts, Is.All.EqualTo(records ? 1 : 0), report);
                Assert.That(
                    state.DiagnosticRecords.Length,
                    Is.EqualTo(records ? count : 0),
                    report
                );
                foreach (MessageEmissionData record in state.DiagnosticRecords)
                {
                    Assert.That(state.Handles, Does.Contain(record.registrationHandle), report);
                    Assert.That(record.traceId, Is.Zero, report);
                }
                Assert.That(state.RecoveryFailure, Is.Null, report);
                Assert.That(state.CleanupFailure, Is.Null, report);
                Assert.That(state.TokenCleanupCounters, Is.All.EqualTo(0), report);
                Assert.That(state.TokenCleanupLive, Is.All.EqualTo(0), report);
                Assert.That(state.FinalCounters, Is.All.EqualTo(0), report);
                Assert.That(state.InvalidHandles, Is.Zero, report);
                Assert.That(state.DuplicateRetirements, Is.Zero, report);
            });
        }

        private sealed class Scope : IDisposable
        {
            private static readonly InstanceId Context = new InstanceId(120007);
            private readonly MessageScenario _scenario;
            private readonly int _shape,
                _count;
            private readonly bool _reject,
                _nested,
                _outer,
                _throwing;
            private readonly bool? _nestedRejectedAfter;
            private readonly bool _oldDebug = MessagingDebug.enabled;
            private readonly Action<LogLevel, string> _oldLog = MessagingDebug.LogFunction;
            private readonly int _oldBufferSize = IMessageBus.GlobalMessageBufferSize;
            private readonly bool _oldStackTraces = IMessageBus.GlobalDiagnosticsStackTraces;
            private readonly ProbeBus[] _buses;
            private readonly MessageRegistrationToken _token;
            private readonly int[] _calls;
            private readonly List<string> _trace = new List<string>();
            internal readonly Exception Marker = new InvalidOperationException(
                "nested-retarget-callback-marker"
            );
            internal readonly int Source;
            internal Exception NestedRetargetFailure;
            internal Exception NestedMarker => _buses[2].RegistrationMarker;
            internal readonly MessageRegistrationHandle[] Handles;
            internal int[] DiagnosticCounts;
            internal MessageEmissionData[] DiagnosticRecords;
            internal int Callbacks,
                NestedAcquisitions,
                MetadataAfter;
            internal bool HandledCleanupMarker,
                EnabledAfter,
                RetargetReturned;
            internal bool? EnabledAfterDisable;
            internal Exception Failure,
                RecoveryFailure,
                CleanupFailure;
            internal int[][] FirstDelivery,
                DisabledDelivery,
                RecoveryDelivery;
            internal int[] LiveAfter,
                TokenCleanupCounters,
                TokenCleanupLive,
                FinalCounters;
            internal int InvalidHandles => _buses.Sum(bus => bus.InvalidHandles);
            internal int DuplicateRetirements => _buses.Sum(bus => bus.DuplicateRetirements);
            internal string Report =>
                $"{_scenario}, shape={_shape}, count={_count}, reject={_reject}, nested={_nested}, outer={_outer}, throwing={_throwing}; "
                + string.Join(";", _trace)
                + "; accepted="
                + string.Join(",", _buses.Select(bus => bus.Accepted))
                + "; failure="
                + Failure;

            internal Scope(
                MessageScenario scenario,
                int shape,
                int count,
                bool after,
                bool reject,
                bool nested,
                bool outer,
                bool diagnostics,
                bool throwing,
                bool? nestedRejectedAfter = null
            )
            {
                _scenario = scenario;
                _shape = shape;
                _count = count;
                _reject = reject;
                _nested = nested;
                _outer = outer;
                _throwing = throwing;
                _nestedRejectedAfter = nestedRejectedAfter;
                Source = outer ? 1 : 0;
                _calls = new int[count];
                Handles = new MessageRegistrationHandle[count];
                _buses = new[] { new ProbeBus(after), new ProbeBus(after), new ProbeBus(after) };
                MessageHandler handler = new MessageHandler(new InstanceId(120008), _buses[0])
                {
                    active = true,
                };
                _token = MessageRegistrationToken.Create(handler, _buses[0]);
                _token.DiagnosticMode = diagnostics;
            }

            internal void Run()
            {
                IMessageBus.GlobalMessageBufferSize = 128;
                IMessageBus.GlobalDiagnosticsStackTraces = false;
                MessagingDebug.enabled = false;
                MessagingDebug.LogFunction = null;
                for (int index = 0; index < _count; ++index)
                    Stage(index);
                if (_outer)
                    _token.Enable();
                ProbeBus source = _buses[Source];
                _buses[2].RejectRegistrationAfter = _nestedRejectedAfter;
                source.Callback = () =>
                {
                    ++Callbacks;
                    _trace.Add("callback-enter");
                    source.RejectNextCleanup = _reject;
                    try
                    {
                        _token.Disable();
                    }
                    catch (Exception exception)
                    {
                        if (!ReferenceEquals(exception, source.Marker))
                            throw;
                        HandledCleanupMarker = true;
                        _trace.Add("exact-cleanup-marker");
                    }
                    EnabledAfterDisable = _token.Enabled;
                    source.RejectNextCleanup = false;
                    int before = _buses[2].Accepted;
                    if (_nested)
                    {
                        try
                        {
                            _token.RetargetMessageBus(_buses[2], MessageBusRebindMode.RebindActive);
                            RetargetReturned = true;
                        }
                        catch (Exception exception)
                        {
                            if (
                                !_nestedRejectedAfter.HasValue
                                || !ReferenceEquals(exception, NestedMarker)
                            )
                                throw;
                            NestedRetargetFailure = exception;
                        }
                    }
                    NestedAcquisitions = _buses[2].Accepted - before;
                    _trace.Add("nested-acquisitions=" + NestedAcquisitions);
                    if (_throwing)
                        throw Marker;
                };
                try
                {
                    if (_outer)
                        _token.RetargetMessageBus(source, MessageBusRebindMode.RebindActive);
                    else
                        _token.Enable();
                }
                catch (Exception exception)
                {
                    Failure = exception;
                }
                EnabledAfter = _token.Enabled;
                MetadataAfter = _token._metadata.Count;
                FirstDelivery = Deliver();
                DiagnosticCounts = Handles
                    .Select(handle =>
                        _token._callCounts.TryGetValue(handle, out int value) ? value : 0
                    )
                    .ToArray();
                DiagnosticRecords = _token._emissionBuffer.ToArray();
                LiveAfter = _buses.Select(bus => bus.LiveCount).ToArray();
                _trace.Add("live-after=" + string.Join(",", LiveAfter));
                try
                {
                    _token.Disable();
                    DisabledDelivery = Deliver();
                    _token.Enable();
                    RecoveryDelivery = Deliver();
                }
                catch (Exception exception)
                {
                    RecoveryFailure = exception;
                }
            }

            internal int[] ExpectedDelivery(int bus)
            {
                int[] expected = new int[_count];
                for (int index = 0; index < _count; ++index)
                {
                    if (_throwing)
                        expected[index] = _outer && bus == 0 ? 1 : 0;
                    else
                    {
                        int owner =
                            _nested && !_nestedRejectedAfter.HasValue && index != 1 ? 2 : Source;
                        expected[index] = (index != 0 || _reject) && bus == owner ? 1 : 0;
                    }
                }
                return expected;
            }

            private int[][] Deliver()
            {
                int[][] result = new int[3][];
                for (int index = 0; index < 3; ++index)
                {
                    int[] before = (int[])_calls.Clone();
                    ProbeMessage message = new ProbeMessage();
                    InstanceId context = Context;
                    switch (_scenario.Kind)
                    {
                        case MessageKind.Untargeted:
                            _buses[index].Actual.UntargetedBroadcast(ref message);
                            break;
                        case MessageKind.Targeted:
                            _buses[index].Actual.TargetedBroadcast(ref context, ref message);
                            break;
                        case MessageKind.Broadcast:
                            _buses[index].Actual.SourcedBroadcast(ref context, ref message);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(_scenario));
                    }
                    result[index] = _calls
                        .Select((value, callback) => value - before[callback])
                        .ToArray();
                }
                return result;
            }

            private void Stage(int index)
            {
                Action<ProbeMessage> normal = _ => ++_calls[index];
                MessageHandler.FastHandler<ProbeMessage> fast = (in ProbeMessage _) =>
                    ++_calls[index];
                if (_shape <= 1)
                {
                    switch (_scenario.Kind)
                    {
                        case MessageKind.Untargeted:
                            if (_shape == 0)
                                Handles[index] = _token.RegisterUntargeted(normal);
                            else
                                Handles[index] = _token.RegisterUntargeted(fast);
                            break;
                        case MessageKind.Targeted:
                            if (_shape == 0)
                                Handles[index] = _token.RegisterTargeted(Context, normal);
                            else
                                Handles[index] = _token.RegisterTargeted(Context, fast);
                            break;
                        case MessageKind.Broadcast:
                            if (_shape == 0)
                                Handles[index] = _token.RegisterBroadcast(Context, normal);
                            else
                                Handles[index] = _token.RegisterBroadcast(Context, fast);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(_scenario));
                    }
                }
                else if (_shape == 2)
                {
                    bool Scalar(ref ProbeMessage _)
                    {
                        ++_calls[index];
                        return true;
                    }
                    bool Contextual(ref InstanceId _, ref ProbeMessage __)
                    {
                        ++_calls[index];
                        return true;
                    }
                    switch (_scenario.Kind)
                    {
                        case MessageKind.Untargeted:
                            Handles[index] = _token.RegisterUntargetedInterceptor<ProbeMessage>(
                                Scalar
                            );
                            break;
                        case MessageKind.Targeted:
                            Handles[index] = _token.RegisterTargetedInterceptor<ProbeMessage>(
                                Contextual
                            );
                            break;
                        case MessageKind.Broadcast:
                            Handles[index] = _token.RegisterBroadcastInterceptor<ProbeMessage>(
                                Contextual
                            );
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(_scenario));
                    }
                }
                else if (_shape == 3)
                {
                    Action<IUntargetedMessage> untargeted = _ => ++_calls[index];
                    Action<InstanceId, ITargetedMessage> targeted = (_, __) => ++_calls[index];
                    Action<InstanceId, IBroadcastMessage> broadcast = (_, __) => ++_calls[index];
                    Handles[index] = _token.RegisterGlobalAcceptAll(
                        untargeted,
                        targeted,
                        broadcast
                    );
                }
                else
                {
                    void Untargeted(in IUntargetedMessage _)
                    {
                        ++_calls[index];
                    }
                    void Targeted(in InstanceId _, in ITargetedMessage __)
                    {
                        ++_calls[index];
                    }
                    void Broadcast(in InstanceId _, in IBroadcastMessage __)
                    {
                        ++_calls[index];
                    }
                    Handles[index] = _token.RegisterGlobalAcceptAll(
                        Untargeted,
                        Targeted,
                        Broadcast
                    );
                }
            }

            public void Dispose()
            {
                try
                {
                    foreach (ProbeBus bus in _buses)
                    {
                        bus.Callback = null;
                        bus.RejectNextCleanup = false;
                    }
                    try
                    {
                        _token.UnregisterAll();
                    }
                    catch (Exception exception)
                    {
                        CleanupFailure = exception;
                    }
                    TokenCleanupCounters = Counters();
                    TokenCleanupLive = _buses.Select(bus => bus.LiveCount).ToArray();
                    foreach (ProbeBus bus in _buses)
                        bus.Drain();
                    FinalCounters = Counters();
                }
                finally
                {
                    IMessageBus.GlobalMessageBufferSize = _oldBufferSize;
                    IMessageBus.GlobalDiagnosticsStackTraces = _oldStackTraces;
                    MessagingDebug.enabled = _oldDebug;
                    MessagingDebug.LogFunction = _oldLog;
                }
            }

            private int[] Counters() =>
                _buses
                    .SelectMany(bus =>
                        new[]
                        {
                            bus.Actual.RegisteredUntargeted,
                            bus.Actual.RegisteredTargeted,
                            bus.Actual.RegisteredBroadcast,
                            bus.Actual.RegisteredGlobalAcceptAll,
                            bus.Actual.RegisteredInterceptors,
                            bus.Actual.RegisteredPostProcessors,
                        }
                    )
                    .ToArray();
        }

        private abstract class Acquisition
        {
            internal ProbeBus Owner;
            internal long Id;
            internal bool Live = true;
            internal abstract void Drop();
        }

        private sealed class Acquisition<T> : Acquisition
            where T : IMessage
        {
            internal Bus Actual;
            internal MessageBusRegistration Raw;

            internal override void Drop()
            {
                Actual.Deregister<T>(in Raw);
                Live = false;
            }
        }

        private sealed class ProbeBus : DelegatingMessageBus
        {
            internal readonly Bus Actual;
            internal readonly Exception Marker = new InvalidOperationException(
                "nested-retarget-cleanup-marker"
            );
            internal Action Callback;
            internal bool RejectNextCleanup;
            internal int Accepted,
                InvalidHandles,
                DuplicateRetirements;
            internal bool? RejectRegistrationAfter;
            internal readonly Exception RegistrationMarker = new InvalidOperationException(
                "nested-destination-registration-marker"
            );
            private readonly bool _after;
            private readonly List<Acquisition> _acquisitions = new List<Acquisition>();
            private int _attempts;
            private long _nextId;
            internal int LiveCount => _acquisitions.Count(value => value.Live);

            internal ProbeBus(bool after)
                : this(new Bus { DiagnosticsMode = false }, after) { }

            private ProbeBus(Bus actual, bool after)
                : base(actual)
            {
                Actual = actual;
                _after = after;
            }

            private bool Before()
            {
                if (RejectRegistrationAfter == false)
                {
                    RejectRegistrationAfter = null;
                    throw RegistrationMarker;
                }
                bool selected = ++_attempts == 2;
                if (selected && !_after)
                    InvokeOnce();
                return selected;
            }

            private void InvokeOnce()
            {
                Action callback = Callback;
                Callback = null;
                callback?.Invoke();
            }

            private MessageBusRegistration Keep<T>(MessageBusRegistration value, bool selected)
                where T : IMessage
            {
                Acquisition<T> acquisition = new Acquisition<T>
                {
                    Owner = this,
                    Id = ++_nextId,
                    Actual = Actual,
                    Raw = value,
                };
                _acquisitions.Add(acquisition);
                ++Accepted;
                if (RejectRegistrationAfter == true)
                {
                    RejectRegistrationAfter = null;
                    acquisition.Drop();
                    throw RegistrationMarker;
                }
                if (selected && _after)
                {
                    try
                    {
                        InvokeOnce();
                    }
                    catch
                    {
                        acquisition.Drop();
                        throw;
                    }
                }
                return new MessageBusRegistration(acquisition.Id, acquisition);
            }

            public override void Deregister<T>(in MessageBusRegistration registration)
            {
                if (RejectNextCleanup)
                {
                    RejectNextCleanup = false;
                    throw Marker;
                }
                Acquisition value = registration.ExternalState as Acquisition;
                if (
                    value == null
                    || !ReferenceEquals(value.Owner, this)
                    || value.Id != registration.ExternalId
                )
                {
                    ++InvalidHandles;
                    throw new InvalidOperationException("Incorrect external cleanup identity.");
                }
                if (!value.Live)
                {
                    ++DuplicateRetirements;
                    throw new InvalidOperationException("Cleanup identity already retired.");
                }
                value.Drop();
            }

            internal void Drain()
            {
                foreach (Acquisition acquisition in _acquisitions)
                    if (acquisition.Live)
                        acquisition.Drop();
            }

            public override MessageBusRegistration RegisterUntargeted<T>(
                MessageHandler handler,
                int priority = 0
            )
            {
                bool selected = Before();
                return Keep<T>(base.RegisterUntargeted<T>(handler, priority), selected);
            }

            public override MessageBusRegistration RegisterTargeted<T>(
                InstanceId context,
                MessageHandler handler,
                int priority = 0
            )
            {
                bool selected = Before();
                return Keep<T>(base.RegisterTargeted<T>(context, handler, priority), selected);
            }

            public override MessageBusRegistration RegisterSourcedBroadcast<T>(
                InstanceId context,
                MessageHandler handler,
                int priority = 0
            )
            {
                bool selected = Before();
                return Keep<T>(
                    base.RegisterSourcedBroadcast<T>(context, handler, priority),
                    selected
                );
            }

            public override MessageBusRegistration RegisterUntargetedInterceptor<T>(
                IMessageBus.UntargetedInterceptor<T> interceptor,
                int priority = 0
            )
            {
                bool selected = Before();
                return Keep<T>(base.RegisterUntargetedInterceptor(interceptor, priority), selected);
            }

            public override MessageBusRegistration RegisterTargetedInterceptor<T>(
                IMessageBus.TargetedInterceptor<T> interceptor,
                int priority = 0
            )
            {
                bool selected = Before();
                return Keep<T>(base.RegisterTargetedInterceptor(interceptor, priority), selected);
            }

            public override MessageBusRegistration RegisterBroadcastInterceptor<T>(
                IMessageBus.BroadcastInterceptor<T> interceptor,
                int priority = 0
            )
            {
                bool selected = Before();
                return Keep<T>(base.RegisterBroadcastInterceptor(interceptor, priority), selected);
            }

            public override MessageBusRegistration RegisterGlobalAcceptAll(MessageHandler handler)
            {
                bool selected = Before();
                return Keep<IMessage>(base.RegisterGlobalAcceptAll(handler), selected);
            }
        }

        private static void AssertAll(Action assertions)
        {
#if UNITY_2021_3_OR_NEWER
            assertions();
#else
            Assert.Multiple(assertions);
#endif
        }

        private readonly struct ProbeMessage
            : IUntargetedMessage,
                ITargetedMessage,
                IBroadcastMessage
        {
            public Type MessageType => typeof(ProbeMessage);
        }
    }
}
