namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Reflection;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;
    using BusType = DxMessaging.Core.MessageBus.MessageBus;

    /// <summary>Preserves individual foreign cleanup leases accepted during token teardown.</summary>
    /// <remarks>2026-10-25: Equal callback counts hid rollback consuming a newer lease while retaining an older one.</remarks>
    [TestFixture]
#if !UNITY_2021_3_OR_NEWER
    [NonParallelizable]
#endif
    public sealed class TokenCleanupSnapshotOwnershipTests
    {
        private static readonly InstanceId Context = new InstanceId(992);

        private static IEnumerable<TestCaseData> SnapshotCases()
        {
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            {
                for (int family = 0; family < 5; ++family)
                {
                    foreach (int count in new[] { 2, 3, 8 })
                    {
                        foreach (string outer in new[] { "Rollback", "Disable", "Unregister" })
                        {
                            foreach (
                                string mutation in new[]
                                {
                                    "None",
                                    "Enable",
                                    "DisableEnable",
                                    "ClearRestage",
                                    "ReplaceLater",
                                }
                            )
                            {
                                foreach (bool after in new[] { false, true })
                                {
                                    foreach (
                                        string rejection in new[] { "None", "Before", "After" }
                                    )
                                    {
                                        foreach (bool diagnostics in new[] { false, true })
                                        {
                                            yield return new TestCaseData(
                                                scenario,
                                                family,
                                                count,
                                                outer,
                                                mutation,
                                                after,
                                                rejection,
                                                diagnostics
                                            ).SetName(
                                                $"CleanupOwnership_{scenario}_family{family}_count{count}_{outer}_{mutation}_after{after}_reject{rejection}_diagnostics{diagnostics}"
                                            );
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>Invokes only captured cleanup and preserves every accepted nested lease.</summary>
        [TestCaseSource(nameof(SnapshotCases))]
        public void SnapshotKeepsExactAcceptedCleanupOwnership(
            MessageScenario scenario,
            int family,
            int count,
            string outer,
            string mutation,
            bool after,
            string rejection,
            bool diagnostics
        )
        {
            Scope scope = new Scope(
                scenario,
                family,
                count,
                outer,
                mutation,
                after,
                rejection,
                diagnostics
            );
            RunScopeAndAssert(scope, outer, rejection);
        }

        private static void RunScopeAndAssert(Scope scope, string outer, string rejection)
        {
            using (new LeakWatcher(scope.Inner, label: scope.Label))
            {
                try
                {
                    scope.Run();
                }
                finally
                {
                    scope.Dispose();
                }
            }
            string report = scope.Report;
            Assert.That(scope.Setup, Is.True, report);
            Assert.That(scope.Bus.Callbacks, Is.EqualTo(1), report);
            Assert.That(scope.Bus.InvalidHandles, Is.Zero, report);
            Assert.That(scope.EnabledAtCallback, Is.EqualTo(outer != "Rollback"), report);
            Assert.That(
                scope.Failure,
                Is.SameAs(
                    outer == "Rollback" ? scope.Bus.RegistrationMarker
                    : rejection == "None" ? null
                    : scope.Bus.CleanupMarker
                ),
                report
            );
            Assert.That(scope.NestedAcceptanceAdmitted, Is.True, report);
            Assert.That(scope.PostLiveIds, Is.EqualTo(scope.ExpectedLiveIds), report);
            Assert.That(scope.PostDelivery, Is.EqualTo(scope.ExpectedPostDelivery), report);
            Assert.That(
                scope.EnabledAfterOuter,
                Is.EqualTo(scope.NestedLeaseCount != 0 || rejection != "None"),
                report
            );
            Assert.That(
                scope.MetadataAfterOuter,
                Is.EqualTo(scope.ExpectedRecoveryIndices.Count),
                report
            );
            Assert.That(scope.DisableFailure, Is.Null, report);
            Assert.That(scope.DisabledDelivery, Is.All.EqualTo(0), report);
            Assert.That(scope.DisabledLiveIds, Is.Empty, report);
            Assert.That(scope.EnabledAfterDisable, Is.False, report);
            Assert.That(scope.RecoveryFailure, Is.Null, report);
            Assert.That(scope.RecoveryDelivery, Is.EqualTo(scope.ExpectedRecoveryDelivery), report);
            Assert.That(scope.UnregisterFailure, Is.Null, report);
            Assert.That(scope.UnregisteredDelivery, Is.All.EqualTo(0), report);
            Assert.That(scope.TokenCleanupLiveIds, Is.Empty, report);
            Assert.That(scope.TokenCleanupCounters, Is.All.EqualTo(0), report);
            Assert.That(scope.MetadataAfterUnregister, Is.Zero, report);
            Assert.That(scope.CallCountsAfterUnregister, Is.Zero, report);
            Assert.That(scope.HistoryAfterUnregister, Is.Zero, report);
            Assert.That(scope.FinalFailure, Is.Null, report);
            Assert.That(scope.FinalCounters, Is.All.EqualTo(0), report);
            Assert.That(scope.StaticsRestored, Is.True, report);
        }

        private static IEnumerable<TestCaseData> SaturationCases()
        {
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            {
                for (int family = 0; family < 5; ++family)
                {
                    foreach (int seed in new[] { int.MaxValue - 2, int.MaxValue - 1, int.MaxValue })
                    {
                        foreach (bool after in new[] { false, true })
                        {
                            foreach (string rejection in new[] { "None", "Before", "After" })
                            {
                                foreach (bool diagnostics in new[] { false, true })
                                {
                                    yield return new TestCaseData(
                                        scenario,
                                        family,
                                        seed,
                                        after,
                                        rejection,
                                        diagnostics
                                    ).SetName(
                                        $"CleanupSaturation_{scenario}_family{family}_seed{seed}_after{after}_reject{rejection}_diagnostics{diagnostics}"
                                    );
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>Saturates inline versions without consuming replacement cleanup or retaining snapshot references.</summary>
        [TestCaseSource(nameof(SaturationCases))]
        public void InlineVersionSaturationPreservesSnapshotCleanup(
            MessageScenario scenario,
            int family,
            int seed,
            bool after,
            string rejection,
            bool diagnostics
        )
        {
            Scope scope = new Scope(
                scenario,
                family,
                2,
                "Disable",
                "DisableEnable",
                after,
                rejection,
                diagnostics,
                seed
            );
            RunScopeAndAssert(scope, "Disable", rejection);
            Assert.That(scope.VersionsAfter, Has.Length.EqualTo(2), scope.Report);
            Assert.That(scope.VersionsAfter, Is.All.EqualTo(int.MaxValue), scope.Report);
            Assert.That(scope.SnapshotCachesScrubbed, Is.True, scope.Report);
        }

        private static PropertyInfo VersionProperty(object registration)
        {
            for (Type type = registration.GetType(); type != null; type = type.BaseType)
            {
                PropertyInfo property = type.GetProperty(
                    "DeregistrationVersion",
                    BindingFlags.Instance
                        | BindingFlags.Public
                        | BindingFlags.NonPublic
                        | BindingFlags.DeclaredOnly
                );
                if (property != null)
                {
                    return property;
                }
            }
            throw new MissingMemberException(
                registration.GetType().FullName,
                "DeregistrationVersion"
            );
        }

        private static bool CachesScrubbed(MessageRegistrationToken token)
        {
            object queue = MessageBusInspection.ReadField(token, "_teardownQueue");
            if (queue == null || ((ICollection)queue).Count != 0)
            {
                return false;
            }
            Array entries = (Array)MessageBusInspection.ReadField(queue, "_items");
            foreach (object entry in entries)
            {
                if (MessageBusInspection.ReadField(entry, "Deregistration") != null)
                {
                    return false;
                }
            }
            object baseline = MessageBusInspection.ReadField(token, "_rollbackBaseline");
            if (baseline == null)
            {
                return false;
            }
            foreach (object entry in (IEnumerable)baseline)
            {
                return false;
            }
            return true;
        }

        private static IEnumerable<TestCaseData> RetargetCases()
        {
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            {
                for (int family = 0; family < 5; ++family)
                {
                    foreach (int count in new[] { 2, 3, 8 })
                    {
                        foreach (bool after in new[] { false, true })
                        {
                            foreach (string rejection in new[] { "None", "Before", "After" })
                            {
                                foreach (bool diagnostics in new[] { false, true })
                                {
                                    yield return new TestCaseData(
                                        scenario,
                                        family,
                                        count,
                                        after,
                                        rejection,
                                        diagnostics
                                    ).SetName(
                                        $"CleanupRetarget_{scenario}_family{family}_count{count}_after{after}_reject{rejection}_diagnostics{diagnostics}"
                                    );
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>Preserves nested new-bus leases while failed retargeting restores the future bus selection.</summary>
        [TestCaseSource(nameof(RetargetCases))]
        public void ActiveRetargetKeepsNewCleanupAndPreviousBusRecovery(
            MessageScenario scenario,
            int family,
            int count,
            bool after,
            string rejection,
            bool diagnostics
        )
        {
            RetargetScope scope = new RetargetScope(
                scenario,
                family,
                count,
                after,
                rejection,
                diagnostics
            );
            using (new LeakWatcher(scope.OldInner, label: scope.Label + ":old"))
            using (new LeakWatcher(scope.Inner, label: scope.Label + ":new"))
            {
                try
                {
                    scope.Run();
                }
                finally
                {
                    scope.Dispose();
                }
            }
            string report = scope.Report;
            Assert.That(scope.Setup, Is.True, report);
            Assert.That(scope.Failure, Is.SameAs(scope.Bus.RegistrationMarker), report);
            Assert.That(scope.Bus.Callbacks, Is.EqualTo(1), report);
            Assert.That(scope.Bus.InvalidHandles + scope.OldBus.InvalidHandles, Is.Zero, report);
            Assert.That(scope.EnabledAtCallback, Is.False, report);
            Assert.That(scope.NestedLeaseCount, Is.EqualTo(count), report);
            Assert.That(scope.PostLiveIds, Is.EqualTo(scope.ExpectedLiveIds), report);
            Assert.That(scope.OldPostLiveIds, Is.Empty, report);
            Assert.That(scope.OldRestorationAttempts, Is.Zero, report);
            Assert.That(scope.PostDelivery, Is.All.EqualTo(1), report);
            Assert.That(scope.OldPostDelivery, Is.All.EqualTo(0), report);
            Assert.That(scope.EnabledAfterOuter, Is.True, report);
            Assert.That(scope.MetadataAfterOuter, Is.EqualTo(count), report);
            Assert.That(scope.DisableFailure, Is.Null, report);
            Assert.That(scope.EnabledAfterDisable, Is.False, report);
            Assert.That(scope.DisabledDelivery, Is.All.EqualTo(0), report);
            Assert.That(scope.DisabledLiveIds, Is.Empty, report);
            Assert.That(scope.RecoveryFailure, Is.Null, report);
            Assert.That(scope.RecoveryDelivery, Is.All.EqualTo(1), report);
            Assert.That(scope.NewRecoveryDelivery, Is.All.EqualTo(0), report);
            Assert.That(scope.UnregisterFailure, Is.Null, report);
            Assert.That(scope.UnregisteredDelivery, Is.All.EqualTo(0), report);
            Assert.That(scope.CleanupLiveIds, Is.Empty, report);
            Assert.That(scope.CleanupCounters, Is.All.EqualTo(0), report);
            Assert.That(scope.MetadataAfterUnregister, Is.Zero, report);
            Assert.That(scope.CallCountsAfterUnregister, Is.Zero, report);
            Assert.That(scope.HistoryAfterUnregister, Is.Zero, report);
            Assert.That(scope.FinalFailure, Is.Null, report);
            Assert.That(scope.FinalCounters, Is.All.EqualTo(0), report);
            Assert.That(scope.StaticsRestored, Is.True, report);
        }

        private sealed class RetargetScope : IDisposable
        {
            private readonly bool _oldDebug = MessagingDebug.enabled;
            private readonly Action<LogLevel, string> _oldLog = MessagingDebug.LogFunction;
            private readonly MessageScenario _scenario;
            private readonly int _family,
                _count;
            private readonly string _rejection;
            private readonly MessageHandler _handler;
            private readonly MessageRegistrationToken _token;
            private readonly int[] _calls,
                _indices;
            private long _firstCleanupId;
            internal readonly BusType OldInner = new BusType { DiagnosticsMode = false };
            internal readonly BusType Inner = new BusType { DiagnosticsMode = false };
            internal readonly ProbeBus Bus,
                OldBus;
            internal readonly string Label;
            internal Exception Failure,
                DisableFailure,
                RecoveryFailure,
                UnregisterFailure,
                FinalFailure;
            internal bool Setup,
                EnabledAtCallback,
                EnabledAfterOuter,
                EnabledAfterDisable,
                StaticsRestored;
            internal int NestedLeaseCount,
                OldRestorationAttempts,
                MetadataAfterOuter,
                MetadataAfterUnregister,
                CallCountsAfterUnregister,
                HistoryAfterUnregister;
            internal long[] PostLiveIds,
                ExpectedLiveIds,
                OldPostLiveIds,
                DisabledLiveIds,
                CleanupLiveIds;
            internal int[] PostDelivery,
                OldPostDelivery,
                DisabledDelivery,
                RecoveryDelivery,
                NewRecoveryDelivery,
                UnregisteredDelivery,
                CleanupCounters,
                FinalCounters;

            internal RetargetScope(
                MessageScenario scenario,
                int family,
                int count,
                bool after,
                string rejection,
                bool diagnostics
            )
            {
                _scenario = scenario;
                _family = family;
                _count = count;
                _rejection = rejection;
                _calls = new int[count];
                _indices = new int[count];
                for (int index = 0; index < count; ++index)
                {
                    _indices[index] = index;
                }
                Label =
                    $"[{scenario.Kind}] retarget,family={family},count={count},after={after},rejection={rejection},diagnostics={diagnostics}";
                OldBus = new ProbeBus(OldInner, false, "None");
                Bus = new ProbeBus(Inner, after, rejection);
                _handler = new MessageHandler(new InstanceId(994), OldBus) { active = true };
                _token = MessageRegistrationToken.Create(_handler, OldBus);
                _token.DiagnosticMode = diagnostics;
                MessagingDebug.enabled = false;
                MessagingDebug.LogFunction = null;
            }

            internal string Report =>
                Label
                + $": failure={Failure},live={string.Join(",", PostLiveIds ?? Array.Empty<long>())},expected={string.Join(",", ExpectedLiveIds ?? Array.Empty<long>())},new={string.Join(";", Bus.Events)},old={string.Join(";", OldBus.Events)}";

            internal void Run()
            {
                for (int index = 0; index < _count; ++index)
                {
                    RegisterForScenario(_token, _scenario, _family, _calls, index);
                }
                OldBus.BeginPhase("Initial", _indices);
                _token.Enable();
                Setup =
                    _token.Enabled
                    && _token._metadata.Count == _count
                    && OldBus.Leases.Count == _count
                    && Bus.Leases.Count == 0;
                int oldAttempts = OldBus.RegistrationAttempts;
                OldBus.BeginPhase("Restore", _indices);
                Bus.BeginPhase("Outer", _indices);
                Bus.FailOnAttempt = _count;
                Bus.Callback = () =>
                {
                    EnabledAtCallback = _token.Enabled;
                    _firstCleanupId = Bus.CurrentCleanupId;
                    ProbeBus.Phase saved = Bus.SavePhase();
                    Bus.BeginPhase("Nested", _indices);
                    try
                    {
                        _token.Enable();
                    }
                    finally
                    {
                        Bus.RestorePhase(saved);
                    }
                };
                Failure = Capture(() =>
                    _token.RetargetMessageBus(Bus, MessageBusRebindMode.RebindActive)
                );
                EnabledAfterOuter = _token.Enabled;
                MetadataAfterOuter = _token._metadata.Count;
                OldRestorationAttempts = OldBus.RegistrationAttempts - oldAttempts;
                SortedSet<long> expected = new SortedSet<long>();
                foreach (Lease lease in Bus.Leases)
                {
                    if (lease.AcquisitionPhase == "Nested")
                    {
                        ++NestedLeaseCount;
                        expected.Add(lease.Id);
                    }
                    if (_rejection == "Before" && lease.Id == _firstCleanupId)
                    {
                        expected.Add(lease.Id);
                    }
                }
                ExpectedLiveIds = new long[expected.Count];
                expected.CopyTo(ExpectedLiveIds);
                PostLiveIds = Bus.LiveIds();
                OldPostLiveIds = OldBus.LiveIds();
                PostDelivery = Deliver(Inner);
                OldPostDelivery = Deliver(OldInner);
                DisableFailure = Capture(_token.Disable);
                EnabledAfterDisable = _token.Enabled;
                DisabledLiveIds = CombinedLiveIds();
                DisabledDelivery = BothDeliveries();
                OldBus.BeginPhase("Recovery", _indices);
                Bus.BeginPhase("UnexpectedNewRecovery", _indices);
                RecoveryFailure = Capture(_token.Enable);
                RecoveryDelivery = Deliver(OldInner);
                NewRecoveryDelivery = Deliver(Inner);
                UnregisterFailure = Capture(_token.UnregisterAll);
                CleanupLiveIds = CombinedLiveIds();
                CleanupCounters = Counters();
                UnregisteredDelivery = BothDeliveries();
                MetadataAfterUnregister = _token._metadata.Count;
                CallCountsAfterUnregister = _token._callCounts.Count;
                HistoryAfterUnregister = _token._emissionBuffer.Count;
            }

            private long[] CombinedLiveIds()
            {
                List<long> ids = new List<long>(Bus.LiveIds());
                ids.AddRange(OldBus.LiveIds());
                return ids.ToArray();
            }

            private int[] Deliver(BusType bus)
            {
                Array.Clear(_calls, 0, _calls.Length);
                ProbeMessage message = default;
                switch (_scenario.Kind)
                {
                    case MessageKind.Untargeted:
                        ScenarioHarness.EmitUntargeted(_scenario, ref message, bus);
                        break;
                    case MessageKind.Targeted:
                        ScenarioHarness.EmitTargeted(_scenario, ref message, Context, bus);
                        break;
                    case MessageKind.Broadcast:
                        ScenarioHarness.EmitBroadcast(_scenario, ref message, Context, bus);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(_scenario));
                }
                return (int[])_calls.Clone();
            }

            private int[] BothDeliveries()
            {
                int[] first = Deliver(Inner),
                    second = Deliver(OldInner);
                for (int index = 0; index < first.Length; ++index)
                {
                    first[index] += second[index];
                }
                return first;
            }

            private int[] Counters() =>
                new[]
                {
                    Inner.RegisteredUntargeted,
                    Inner.RegisteredTargeted,
                    Inner.RegisteredBroadcast,
                    Inner.RegisteredInterceptors,
                    Inner.RegisteredPostProcessors,
                    Inner.RegisteredGlobalAcceptAll,
                    OldInner.RegisteredUntargeted,
                    OldInner.RegisteredTargeted,
                    OldInner.RegisteredBroadcast,
                    OldInner.RegisteredInterceptors,
                    OldInner.RegisteredPostProcessors,
                    OldInner.RegisteredGlobalAcceptAll,
                };

            public void Dispose()
            {
                try
                {
                    Bus.Callback = null;
                    OldBus.Callback = null;
                    FinalFailure = Capture(_token.Dispose);
                    foreach (Lease lease in Bus.Leases)
                    {
                        Exception failure = Capture(lease.Drop);
                        FinalFailure ??= failure;
                    }
                    foreach (Lease lease in OldBus.Leases)
                    {
                        Exception failure = Capture(lease.Drop);
                        FinalFailure ??= failure;
                    }
                    _handler.active = false;
                    Inner.Trim(force: true);
                    OldInner.Trim(force: true);
                    FinalCounters = Counters();
                }
                finally
                {
                    MessagingDebug.enabled = _oldDebug;
                    MessagingDebug.LogFunction = _oldLog;
                    StaticsRestored =
                        MessagingDebug.enabled == _oldDebug
                        && ReferenceEquals(MessagingDebug.LogFunction, _oldLog);
                }
            }
        }

        private sealed class Scope : IDisposable
        {
            private readonly bool _oldDebug = MessagingDebug.enabled;
            private readonly Action<LogLevel, string> _oldLog = MessagingDebug.LogFunction;
            private readonly MessageScenario _scenario;
            private readonly int _family,
                _count;
            private readonly string _outer,
                _mutation,
                _rejection;
            private readonly MessageHandler _handler;
            private readonly MessageRegistrationToken _token;
            private readonly int[] _calls;
            private readonly List<int> _staged = new List<int>();
            private readonly List<MessageRegistrationHandle> _handles =
                new List<MessageRegistrationHandle>();
            private readonly int _versionSeed;
            private readonly List<object> _versionOwners = new List<object>();
            private long _firstCleanupId;
            internal readonly BusType Inner = new BusType { DiagnosticsMode = false };
            internal readonly ProbeBus Bus;
            internal readonly string Label;
            internal readonly SortedSet<int> ExpectedRecoveryIndices = new SortedSet<int>();
            internal Exception Failure,
                DisableFailure,
                RecoveryFailure,
                UnregisterFailure,
                FinalFailure;
            internal bool Setup,
                EnabledAtCallback,
                EnabledAfterOuter,
                EnabledAfterDisable,
                NestedAcceptanceAdmitted,
                StaticsRestored;
            internal int NestedLeaseCount,
                MetadataAfterOuter,
                MetadataAfterUnregister,
                CallCountsAfterUnregister,
                HistoryAfterUnregister;
            internal long[] PostLiveIds,
                ExpectedLiveIds,
                DisabledLiveIds,
                TokenCleanupLiveIds;
            internal int[] PostDelivery,
                ExpectedPostDelivery,
                DisabledDelivery,
                RecoveryDelivery,
                ExpectedRecoveryDelivery,
                UnregisteredDelivery,
                TokenCleanupCounters,
                FinalCounters;

            internal int[] VersionsAfter;
            internal bool SnapshotCachesScrubbed;

            internal Scope(
                MessageScenario scenario,
                int family,
                int count,
                string outer,
                string mutation,
                bool after,
                string rejection,
                bool diagnostics,
                int versionSeed = -1
            )
            {
                _versionSeed = versionSeed;
                _scenario = scenario;
                _family = family;
                _count = count;
                _outer = outer;
                _mutation = mutation;
                _rejection = rejection;
                _calls = new int[2 * count];
                Label =
                    $"[{scenario.Kind}] family={family},count={count},outer={outer},mutation={mutation},after={after},rejection={rejection},diagnostics={diagnostics}";
                if (0 <= versionSeed)
                {
                    Label += $",versionSeed={versionSeed}";
                }
                Bus = new ProbeBus(Inner, after, rejection);
                _handler = new MessageHandler(new InstanceId(993), Bus) { active = true };
                _token = MessageRegistrationToken.Create(_handler, Bus);
                _token.DiagnosticMode = diagnostics;
                MessagingDebug.enabled = false;
                MessagingDebug.LogFunction = null;
            }

            internal string Report =>
                Label
                + $": failure={Failure}; live={string.Join(",", PostLiveIds ?? Array.Empty<long>())}; expected={string.Join(",", ExpectedLiveIds ?? Array.Empty<long>())}; trace={string.Join(";", Bus.Events)}";

            internal void Run()
            {
                for (int index = 0; index < _count; ++index)
                {
                    Stage(index);
                }
                if (0 <= _versionSeed)
                {
                    Array slots = (Array)MessageBusInspection.ReadField(_token, "_slots");
                    foreach (MessageRegistrationHandle handle in _handles)
                    {
                        object registration = MessageBusInspection.ReadField(
                            slots.GetValue(handle.Slot),
                            "Registration"
                        );
                        VersionProperty(registration).SetValue(registration, _versionSeed);
                        _versionOwners.Add(registration);
                    }
                }
                Setup = !_token.Enabled && _token._metadata.Count == _count;
                foreach (int value in Counters())
                {
                    Setup &= value == 0;
                }
                if (_outer != "Rollback")
                {
                    Bus.BeginPhase("Initial", _staged.ToArray());
                    _token.Enable();
                    Setup &= _token.Enabled && Bus.Leases.Count == _count;
                }
                else
                {
                    Bus.FailOnAttempt = _count;
                }
                Bus.BeginPhase("Outer", _staged.ToArray());
                Bus.Callback = Mutate;
                Failure = Capture(() =>
                {
                    switch (_outer)
                    {
                        case "Rollback":
                            _token.Enable();
                            break;
                        case "Disable":
                            _token.Disable();
                            break;
                        case "Unregister":
                            _token.UnregisterAll();
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(_outer));
                    }
                });
                EnabledAfterOuter = _token.Enabled;
                MetadataAfterOuter = _token._metadata.Count;
                BuildExpectations();
                PostLiveIds = Bus.LiveIds();
                PostDelivery = Deliver();
                DisableFailure = Capture(_token.Disable);
                EnabledAfterDisable = _token.Enabled;
                DisabledLiveIds = Bus.LiveIds();
                DisabledDelivery = Deliver();
                Bus.BeginPhase("Recovery", new List<int>(ExpectedRecoveryIndices).ToArray());
                RecoveryFailure = Capture(_token.Enable);
                RecoveryDelivery = Deliver();
                UnregisterFailure = Capture(_token.UnregisterAll);
                TokenCleanupLiveIds = Bus.LiveIds();
                TokenCleanupCounters = Counters();
                UnregisteredDelivery = Deliver();
                MetadataAfterUnregister = _token._metadata.Count;
                CallCountsAfterUnregister = _token._callCounts.Count;
                HistoryAfterUnregister = _token._emissionBuffer.Count;
                if (0 <= _versionSeed)
                {
                    VersionsAfter = new int[_versionOwners.Count];
                    for (int index = 0; index < _versionOwners.Count; ++index)
                    {
                        VersionsAfter[index] = (int)
                            VersionProperty(_versionOwners[index]).GetValue(_versionOwners[index]);
                    }
                    SnapshotCachesScrubbed = CachesScrubbed(_token);
                }
            }

            private void Mutate()
            {
                EnabledAtCallback = _token.Enabled;
                _firstCleanupId = Bus.CurrentCleanupId;
                ProbeBus.Phase saved = Bus.SavePhase();
                try
                {
                    switch (_mutation)
                    {
                        case "None":
                            break;
                        case "Enable":
                            Bus.BeginPhase("Nested", _staged.ToArray());
                            _token.Enable();
                            break;
                        case "DisableEnable":
                            _token.Disable();
                            Bus.BeginPhase("Nested", _staged.ToArray());
                            _token.Enable();
                            break;
                        case "ClearRestage":
                            _token.UnregisterAll();
                            _staged.Clear();
                            _handles.Clear();
                            for (int index = 0; index < _count; ++index)
                            {
                                Stage(_count + index);
                            }
                            Bus.BeginPhase("Nested", _staged.ToArray());
                            _token.Enable();
                            break;
                        case "ReplaceLater":
                            _token.RemoveRegistration(_handles[1]);
                            _handles.RemoveAt(1);
                            _staged.RemoveAt(1);
                            Bus.BeginPhase("Nested", new[] { _count });
                            Stage(_count);
                            if (!_token.Enabled)
                            {
                                Bus.BeginPhase("Nested", _staged.ToArray());
                            }
                            _token.Enable();
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(_mutation));
                    }
                }
                finally
                {
                    Bus.RestorePhase(saved);
                }
            }

            private void BuildExpectations()
            {
                SortedSet<long> live = new SortedSet<long>();
                SortedSet<int> callbacks = new SortedSet<int>();
                foreach (Lease lease in Bus.Leases)
                {
                    if (lease.AcquisitionPhase == "Nested")
                    {
                        ++NestedLeaseCount;
                        live.Add(lease.Id);
                        callbacks.Add(lease.CallbackIndex);
                        if (_outer == "Unregister")
                        {
                            ExpectedRecoveryIndices.Add(lease.CallbackIndex);
                        }
                    }
                    if (_rejection == "Before" && lease.Id == _firstCleanupId)
                    {
                        live.Add(lease.Id);
                        callbacks.Add(lease.CallbackIndex);
                    }
                }
                if (_outer != "Unregister")
                {
                    foreach (int index in _staged)
                    {
                        ExpectedRecoveryIndices.Add(index);
                    }
                }
                else if (_rejection != "None" && _staged.Contains(0))
                {
                    ExpectedRecoveryIndices.Add(0);
                }
                ExpectedLiveIds = new long[live.Count];
                live.CopyTo(ExpectedLiveIds);
                /*
                    A failed typed/global cleanup has not yet removed its original callback.
                    If a broker remains live, require that pending callback until retry succeeds.
                    Interceptors have no handler-side cleanup tail after their bus call.
                */
                if (_family != 2 && _rejection != "None" && 0 < live.Count)
                {
                    callbacks.Add(0);
                }
                ExpectedPostDelivery = Vector(callbacks);
                ExpectedRecoveryDelivery = Vector(ExpectedRecoveryIndices);
                int expectedNested =
                    _mutation == "None" || (_mutation == "Enable" && _outer != "Rollback")
                        ? 0
                        : _count;
                NestedAcceptanceAdmitted =
                    _mutation == "ReplaceLater"
                        ? 0 < NestedLeaseCount
                        : NestedLeaseCount == expectedNested;
            }

            private int[] Vector(IEnumerable<int> indices)
            {
                int[] result = new int[_calls.Length];
                foreach (int index in indices)
                {
                    result[index] = 1;
                }
                return result;
            }

            private void Stage(int index)
            {
                MessageRegistrationHandle handle = RegisterForScenario(
                    _token,
                    _scenario,
                    _family,
                    _calls,
                    index
                );
                _staged.Add(index);
                _handles.Add(handle);
            }

            private int[] Deliver()
            {
                Array.Clear(_calls, 0, _calls.Length);
                ProbeMessage message = default;
                switch (_scenario.Kind)
                {
                    case MessageKind.Untargeted:
                        ScenarioHarness.EmitUntargeted(_scenario, ref message, Inner);
                        break;
                    case MessageKind.Targeted:
                        ScenarioHarness.EmitTargeted(_scenario, ref message, Context, Inner);
                        break;
                    case MessageKind.Broadcast:
                        ScenarioHarness.EmitBroadcast(_scenario, ref message, Context, Inner);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(_scenario));
                }
                return (int[])_calls.Clone();
            }

            private int[] Counters() =>
                new[]
                {
                    Inner.RegisteredUntargeted,
                    Inner.RegisteredTargeted,
                    Inner.RegisteredBroadcast,
                    Inner.RegisteredInterceptors,
                    Inner.RegisteredPostProcessors,
                    Inner.RegisteredGlobalAcceptAll,
                };

            public void Dispose()
            {
                try
                {
                    Bus.Callback = null;
                    FinalFailure = Capture(_token.Dispose);
                    foreach (Lease lease in Bus.Leases)
                    {
                        Exception failure = Capture(lease.Drop);
                        FinalFailure ??= failure;
                    }
                    _handler.active = false;
                    Inner.Trim(force: true);
                    FinalCounters = Counters();
                }
                finally
                {
                    MessagingDebug.enabled = _oldDebug;
                    MessagingDebug.LogFunction = _oldLog;
                    StaticsRestored =
                        MessagingDebug.enabled == _oldDebug
                        && ReferenceEquals(MessagingDebug.LogFunction, _oldLog);
                }
            }
        }

        private static MessageRegistrationHandle RegisterForScenario(
            MessageRegistrationToken token,
            MessageScenario scenario,
            int family,
            int[] calls,
            int index
        )
        {
            Action<ProbeMessage> action = _ => ++calls[index];
            MessageHandler.FastHandler<ProbeMessage> fast = (in ProbeMessage _) => ++calls[index];
            MessageRegistrationHandle handle;
            switch (family)
            {
                case 0:
                case 1:
                    switch (scenario.Kind)
                    {
                        case MessageKind.Untargeted:
                            handle =
                                family == 0
                                    ? token.RegisterUntargeted(action)
                                    : ScenarioHarness.RegisterUntargeted(scenario, token, fast);
                            break;
                        case MessageKind.Targeted:
                            handle =
                                family == 0
                                    ? token.RegisterTargeted(Context, action)
                                    : ScenarioHarness.RegisterTargeted(
                                        scenario,
                                        token,
                                        Context,
                                        fast
                                    );
                            break;
                        case MessageKind.Broadcast:
                            handle =
                                family == 0
                                    ? token.RegisterBroadcast(Context, action)
                                    : ScenarioHarness.RegisterBroadcast(
                                        scenario,
                                        token,
                                        Context,
                                        fast
                                    );
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(scenario));
                    }
                    break;
                case 2:
                    bool Scalar(ref ProbeMessage _)
                    {
                        ++calls[index];
                        return true;
                    }
                    bool Contextual(ref InstanceId _, ref ProbeMessage __)
                    {
                        ++calls[index];
                        return true;
                    }
                    handle = scenario.Kind switch
                    {
                        MessageKind.Untargeted => token.RegisterUntargetedInterceptor<ProbeMessage>(
                            Scalar
                        ),
                        MessageKind.Targeted => token.RegisterTargetedInterceptor<ProbeMessage>(
                            Contextual
                        ),
                        MessageKind.Broadcast => token.RegisterBroadcastInterceptor<ProbeMessage>(
                            Contextual
                        ),
                        _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
                    };
                    break;
                case 3:
                    Action<IUntargetedMessage> untargetedAction = _ => ++calls[index];
                    Action<InstanceId, ITargetedMessage> targetedAction = (_, __) => ++calls[index];
                    Action<InstanceId, IBroadcastMessage> broadcastAction = (_, __) =>
                        ++calls[index];
                    handle = token.RegisterGlobalAcceptAll(
                        untargetedAction,
                        targetedAction,
                        broadcastAction
                    );
                    break;
                case 4:
                    void Untargeted(in IUntargetedMessage _)
                    {
                        ++calls[index];
                    }
                    void Targeted(in InstanceId _, in ITargetedMessage __)
                    {
                        ++calls[index];
                    }
                    void Broadcast(in InstanceId _, in IBroadcastMessage __)
                    {
                        ++calls[index];
                    }
                    handle = token.RegisterGlobalAcceptAll(Untargeted, Targeted, Broadcast);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(family));
            }
            return handle;
        }

        private abstract class Lease
        {
            internal ProbeBus Owner;
            internal long Id;
            internal int CallbackIndex;
            internal string AcquisitionPhase;
            internal bool Live = true;
            internal abstract Type MessageType { get; }
            internal abstract void Drop();
        }

        private sealed class Lease<T> : Lease
            where T : IMessage
        {
            internal IMessageBus Inner;
            internal MessageBusRegistration Raw;
            internal override Type MessageType => typeof(T);

            internal override void Drop()
            {
                if (!Live)
                {
                    return;
                }
                Inner.Deregister<T>(in Raw);
                Live = false;
            }
        }

        private sealed class ProbeBus : DelegatingMessageBus
        {
            private readonly bool _after;
            private readonly string _rejection;
            private string _phase;
            private int[] _indices = Array.Empty<int>();
            private int _cursor,
                _attempts;
            private long _nextId;
            internal int RegistrationAttempts => _attempts;
            internal Action Callback;
            internal long CurrentCleanupId;
            internal int Callbacks,
                InvalidHandles,
                FailOnAttempt = -1;
            internal readonly Exception RegistrationMarker = new InvalidOperationException(
                "snapshot-registration-marker"
            );
            internal readonly Exception CleanupMarker = new InvalidOperationException(
                "snapshot-cleanup-marker"
            );
            internal readonly List<Lease> Leases = new List<Lease>();
            internal readonly List<string> Events = new List<string>();

            internal ProbeBus(IMessageBus inner, bool after, string rejection)
                : base(inner)
            {
                _after = after;
                _rejection = rejection;
            }

            internal readonly struct Phase
            {
                internal readonly string Name;
                internal readonly int[] Indices;
                internal readonly int Cursor;

                internal Phase(string name, int[] indices, int cursor)
                {
                    Name = name;
                    Indices = indices;
                    Cursor = cursor;
                }
            }

            internal Phase SavePhase() => new Phase(_phase, _indices, _cursor);

            internal void RestorePhase(Phase phase)
            {
                _phase = phase.Name;
                _indices = phase.Indices;
                _cursor = phase.Cursor;
            }

            internal void BeginPhase(string phase, int[] indices)
            {
                _phase = phase;
                _indices = indices;
                _cursor = 0;
            }

            internal long[] LiveIds()
            {
                List<long> ids = new List<long>();
                foreach (Lease lease in Leases)
                {
                    if (lease.Live)
                    {
                        ids.Add(lease.Id);
                    }
                }
                return ids.ToArray();
            }

            private int BeforeRegistration()
            {
                int cursor = _cursor++;
                if (++_attempts == FailOnAttempt)
                {
                    throw RegistrationMarker;
                }
                if (_indices.Length <= cursor)
                {
                    throw new InvalidOperationException(
                        "Unexpected registration outside the caller's declared staging order."
                    );
                }
                return _indices[cursor];
            }

            private MessageBusRegistration Keep<T>(MessageBusRegistration raw, int index)
                where T : IMessage
            {
                Lease<T> lease = new Lease<T>
                {
                    Owner = this,
                    Inner = Inner,
                    Raw = raw,
                    Id = ++_nextId,
                    CallbackIndex = index,
                    AcquisitionPhase = _phase,
                };
                Leases.Add(lease);
                Events.Add($"accept:{_phase}:{lease.Id}:callback{index}");
                return new MessageBusRegistration(lease.Id, lease);
            }

            public override MessageBusRegistration RegisterUntargeted<T>(
                MessageHandler handler,
                int priority = 0
            )
            {
                int index = BeforeRegistration();
                return Keep<T>(base.RegisterUntargeted<T>(handler, priority), index);
            }

            public override MessageBusRegistration RegisterTargeted<T>(
                InstanceId context,
                MessageHandler handler,
                int priority = 0
            )
            {
                int index = BeforeRegistration();
                return Keep<T>(base.RegisterTargeted<T>(context, handler, priority), index);
            }

            public override MessageBusRegistration RegisterSourcedBroadcast<T>(
                InstanceId context,
                MessageHandler handler,
                int priority = 0
            )
            {
                int index = BeforeRegistration();
                return Keep<T>(base.RegisterSourcedBroadcast<T>(context, handler, priority), index);
            }

            public override MessageBusRegistration RegisterUntargetedInterceptor<T>(
                IMessageBus.UntargetedInterceptor<T> interceptor,
                int priority = 0
            )
            {
                int index = BeforeRegistration();
                return Keep<T>(base.RegisterUntargetedInterceptor(interceptor, priority), index);
            }

            public override MessageBusRegistration RegisterTargetedInterceptor<T>(
                IMessageBus.TargetedInterceptor<T> interceptor,
                int priority = 0
            )
            {
                int index = BeforeRegistration();
                return Keep<T>(base.RegisterTargetedInterceptor(interceptor, priority), index);
            }

            public override MessageBusRegistration RegisterBroadcastInterceptor<T>(
                IMessageBus.BroadcastInterceptor<T> interceptor,
                int priority = 0
            )
            {
                int index = BeforeRegistration();
                return Keep<T>(base.RegisterBroadcastInterceptor(interceptor, priority), index);
            }

            public override MessageBusRegistration RegisterGlobalAcceptAll(MessageHandler handler)
            {
                int index = BeforeRegistration();
                return Keep<IMessage>(base.RegisterGlobalAcceptAll(handler), index);
            }

            public override void Deregister<T>(in MessageBusRegistration registration)
            {
                Lease lease = registration.ExternalState as Lease;
                if (
                    lease == null
                    || !ReferenceEquals(lease.Owner, this)
                    || lease.Id != registration.ExternalId
                    || lease.MessageType != typeof(T)
                )
                {
                    ++InvalidHandles;
                    throw new InvalidOperationException("Foreign lease roundtrip mismatch.");
                }
                Action callback = Callback;
                Callback = null;
                CurrentCleanupId = lease.Id;
                Events.Add($"attempt:{_phase}:{lease.Id}");
                if (callback != null && !_after)
                {
                    ++Callbacks;
                    callback();
                }
                bool rejectBefore = callback != null && _rejection == "Before";
                if (!rejectBefore)
                {
                    lease.Drop();
                    Events.Add($"retire:{_phase}:{lease.Id}");
                }
                if (callback != null && _after)
                {
                    ++Callbacks;
                    callback();
                }
                if (callback != null && _rejection != "None")
                {
                    throw CleanupMarker;
                }
            }
        }

        private static Exception Capture(Action action)
        {
            try
            {
                action();
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
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
