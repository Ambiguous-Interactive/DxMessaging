namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Core.Messages;
    using DxMessaging.Tests.Runtime;
    using NUnit.Framework;

    /// <summary>Preserves global callback identities across nested registration mutations.</summary>
    /// <remarks>
    /// 2026-10-20: Nested global emissions rebuilt a shared callback list in place,
    /// changing callbacks still owed to the outer emission. These cases use one
    /// handler whose caches are already prefrozen; cross-handler lazy prefreeze
    /// has a separate contract and is not inferred from these expectations.
    /// </remarks>
    [TestFixture]
    public sealed class GlobalNestedMutationTests
    {
        private static IEnumerable<TestCaseData> SameKindCases()
        {
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            {
                foreach (int shape in new[] { 0, 1, 2 })
                {
                    foreach (int count in new[] { 2, 3, 4, 5, 8 })
                    {
                        if (shape == 2 && count == 2)
                        {
                            continue;
                        }
                        foreach (int mutation in new[] { 0, 1, 2, 3 })
                        {
                            foreach (int depth in new[] { 0, 1, 3 })
                            {
                                yield return Case(scenario, shape, count, mutation, depth, 0, 0);
                            }
                        }
                    }
                }
            }
        }

        private static IEnumerable<TestCaseData> CrossKindCases()
        {
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            {
                foreach (int offset in new[] { 1, 2 })
                {
                    foreach (int shape in new[] { 0, 1, 2 })
                    {
                        foreach (int count in new[] { 3, 8 })
                        {
                            foreach (int mutation in new[] { 2, 3 })
                            {
                                foreach (int depth in new[] { 1, 2 })
                                {
                                    yield return Case(
                                        scenario,
                                        shape,
                                        count,
                                        mutation,
                                        depth,
                                        offset,
                                        0
                                    );
                                }
                            }
                        }
                    }
                }
            }
        }

        private static IEnumerable<TestCaseData> ExceptionCases()
        {
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            {
                foreach (int shape in new[] { 0, 1, 2 })
                {
                    foreach (int count in new[] { 3, 8 })
                    {
                        foreach (int mutation in new[] { 2, 3 })
                        {
                            foreach (int depth in new[] { 1, 3 })
                            {
                                foreach (int throwing in new[] { 1, 2, 3 })
                                {
                                    yield return Case(
                                        scenario,
                                        shape,
                                        count,
                                        mutation,
                                        depth,
                                        0,
                                        throwing
                                    );
                                }
                            }
                        }
                    }
                }
            }
        }

        private static TestCaseData Case(
            MessageScenario scenario,
            int shape,
            int count,
            int mutation,
            int depth,
            int offset,
            int throwing
        )
        {
            return new TestCaseData(
                scenario,
                shape,
                count,
                mutation,
                depth,
                offset,
                throwing
            ).SetName(
                "GlobalNested_"
                    + scenario
                    + "_shape"
                    + shape
                    + "_count"
                    + count
                    + "_mutation"
                    + mutation
                    + "_depth"
                    + depth
                    + "_offset"
                    + offset
                    + "_throw"
                    + throwing
            );
        }

        [TestCaseSource(nameof(SameKindCases))]
        [TestCaseSource(nameof(CrossKindCases))]
        [TestCaseSource(nameof(ExceptionCases))]
        public void NestedMutationPreservesOuterCallbacksAndNextEmission(
            MessageScenario scenario,
            int shape,
            int count,
            int mutation,
            int nestingDepth,
            int kindOffset,
            int throwing
        )
        {
            string label =
                scenario
                + ",shape="
                + shape
                + ",count="
                + count
                + ",mutation="
                + mutation
                + ",depth="
                + nestingDepth
                + ",offset="
                + kindOffset
                + ",throw="
                + throwing;
            using CaseState state = new CaseState(label);
            InvalidOperationException failure = new InvalidOperationException(label);
            int depth = 0;
            bool mutated = false;
            MessageRegistrationHandle removed = default;
            Register(
                state.Token,
                shape != 0,
                () =>
                {
                    state.Calls.Add("d" + depth + ":A");
                    if (depth == 0)
                    {
                        if (mutated)
                        {
                            return;
                        }
                        mutated = true;
                        if (mutation == 2 || mutation == 3)
                        {
                            state.Token.RemoveRegistration(removed);
                        }
                        if (mutation == 1 || mutation == 3)
                        {
                            Register(
                                state.Token,
                                shape == 1,
                                () => state.Calls.Add("d" + depth + ":C")
                            );
                        }
                        if (throwing == 1)
                        {
                            throw failure;
                        }
                    }
                    if (throwing == 2 && depth == nestingDepth)
                    {
                        throw failure;
                    }
                    if (depth < nestingDepth)
                    {
                        ++depth;
                        MessageKind nestedKind =
                            depth % 2 == 0
                                ? scenario.Kind
                                : (MessageKind)(((int)scenario.Kind + kindOffset) % 3);
                        try
                        {
                            state.Emit(nestedKind);
                        }
                        finally
                        {
                            --depth;
                        }
                    }
                    if (throwing == 3 && depth == 0)
                    {
                        throw failure;
                    }
                }
            );
            for (int index = 0; index < count - 1; ++index)
            {
                int captured = index;
                removed = Register(
                    state.Token,
                    shape == 1,
                    () => state.Calls.Add("d" + depth + ":P" + captured)
                );
            }

            Exception observed = null;
            try
            {
                state.Emit(scenario.Kind);
            }
            catch (Exception exception)
            {
                observed = exception;
            }
            string[] first = state.Calls.ToArray();
            state.Calls.Clear();
            state.Emit(scenario.Kind);
            string[] second = state.Calls.ToArray();
            int beforeCleanup = state.TotalRegistrations();
            state.Token.Dispose();
            int afterCleanup = state.TotalRegistrations();
            state.CheckLeaksBeforeMaintenance();

            List<string> expectedFirst = new List<string> { "d0:A" };
            if (throwing != 1)
            {
                for (int nestedDepth = 1; nestedDepth <= nestingDepth; ++nestedDepth)
                {
                    expectedFirst.Add("d" + nestedDepth + ":A");
                }
                if (throwing != 2)
                {
                    for (int nestedDepth = nestingDepth; 0 < nestedDepth; --nestedDepth)
                    {
                        AppendPeers(expectedFirst, count, mutation, nestedDepth, updated: true);
                    }
                    if (throwing == 0)
                    {
                        AppendPeers(expectedFirst, count, mutation, 0, updated: false);
                    }
                }
            }
            List<string> expectedSecond = new List<string> { "d0:A" };
            AppendPeers(expectedSecond, count, mutation, 0, updated: true);

            Assert.That(first, Is.EqualTo(expectedFirst), label + ": first trace");
            Assert.That(second, Is.EqualTo(expectedSecond), label + ": next trace");
            Assert.That(
                observed,
                Is.SameAs(throwing == 0 ? null : failure),
                label + ": exception identity"
            );
            Assert.That(beforeCleanup, Is.EqualTo(1), label + ": same handler stays registered");
            Assert.That(
                afterCleanup,
                Is.EqualTo(0),
                label + ": valid cleanup before forced maintenance"
            );
        }

        private static IEnumerable<TestCaseData> OwnershipCases(
            string name,
            int[] shapes,
            int[] counts,
            int[] variants
        )
        {
            foreach (MessageScenario scenario in MessageScenarios.AllKinds)
            {
                foreach (int shape in shapes)
                {
                    foreach (int count in counts)
                    {
                        foreach (int variant in variants)
                        {
                            yield return new TestCaseData(scenario, shape, count, variant).SetName(
                                "GlobalOwnership_"
                                    + name
                                    + "_"
                                    + scenario
                                    + "_shape"
                                    + shape
                                    + "_count"
                                    + count
                                    + "_variant"
                                    + variant
                            );
                        }
                    }
                }
            }
        }

        private static IEnumerable<TestCaseData> ResetCases() =>
            OwnershipCases("Reset", new[] { 0, 1, 2 }, new[] { 3, 8 }, new[] { 0, 1 });

        private static IEnumerable<TestCaseData> EmptyPhaseCases() =>
            OwnershipCases("EmptyPhase", new[] { 2 }, new[] { 2, 3, 8 }, new[] { 0, 1 });

        private static IEnumerable<TestCaseData> DistinctHandlerCases() =>
            OwnershipCases("Distinct", new[] { 0, 1 }, new[] { 3, 8 }, new[] { 0, 1 });

        private static IEnumerable<TestCaseData> ActivityCases() =>
            OwnershipCases("Activity", new[] { 0, 1, 2 }, new[] { 3, 8 }, new[] { 0 });

        private static IEnumerable<TestCaseData> RepeatedMutationCases() =>
            OwnershipCases("Repeated", new[] { 0, 1, 2 }, new[] { 3, 8 }, new[] { 2, 4 });

        [TestCaseSource(nameof(ResetCases))]
        public void ResetAfterNestedRebuildStopsOldSnapshotsAndReleasesBorrowers(
            MessageScenario scenario,
            int shape,
            int count,
            int throwing
        )
        {
            string label = scenario + ",shape=" + shape + ",count=" + count + ",throw=" + throwing;
            using CaseState state = new CaseState(label);
            int depth = 0;
            MessageRegistrationHandle removed = default;
            InvalidOperationException failure = new InvalidOperationException(label);
            Register(
                state.Token,
                shape != 0,
                () =>
                {
                    state.Calls.Add("d" + depth + ":A");
                    if (depth != 0)
                    {
                        state.Bus.ResetState();
                        Register(state.Token, shape == 1, () => state.Calls.Add("R"));
                        if (throwing != 0)
                        {
                            throw failure;
                        }
                        return;
                    }
                    state.Token.RemoveRegistration(removed);
                    Register(state.Token, shape == 1, () => state.Calls.Add("C"));
                    ++depth;
                    try
                    {
                        state.Emit(scenario.Kind);
                    }
                    finally
                    {
                        --depth;
                    }
                }
            );
            for (int index = 0; index < count - 1; ++index)
            {
                int captured = index;
                removed = Register(state.Token, shape == 1, () => state.Calls.Add("P" + captured));
            }
            Exception observed = null;
            try
            {
                state.Emit(scenario.Kind);
            }
            catch (Exception exception)
            {
                observed = exception;
            }
            string[] first = state.Calls.ToArray();
            state.Calls.Clear();
            state.Emit(scenario.Kind);
            string[] second = state.Calls.ToArray();
            state.Token.Dispose();
            state.CheckLeaksBeforeMaintenance();
            Assert.That(first, Is.EqualTo(new[] { "d0:A", "d1:A" }), label + ": reset boundary");
            Assert.That(second, Is.EqualTo(new[] { "R" }), label + ": replacement after reset");
            Assert.That(observed, Is.SameAs(throwing == 0 ? null : failure), label + ": exception");
        }

        [TestCaseSource(nameof(EmptyPhaseCases))]
        public void RemovingAllActionPeersPreservesTheLiveEmptyPhaseGuard(
            MessageScenario scenario,
            int shape,
            int count,
            int nested
        )
        {
            string label = scenario + ",shape=" + shape + ",count=" + count + ",nested=" + nested;
            using CaseState state = new CaseState(label);
            List<MessageRegistrationHandle> peers = new List<MessageRegistrationHandle>();
            bool mutated = false;
            int depth = 0;
            Register(
                state.Token,
                fast: true,
                () =>
                {
                    state.Calls.Add("d" + depth + ":A");
                    if (mutated)
                    {
                        return;
                    }
                    mutated = true;
                    foreach (MessageRegistrationHandle peer in peers)
                    {
                        state.Token.RemoveRegistration(peer);
                    }
                    if (nested != 0)
                    {
                        ++depth;
                        try
                        {
                            state.Emit(scenario.Kind);
                        }
                        finally
                        {
                            --depth;
                        }
                    }
                }
            );
            for (int index = 0; index < count - 1; ++index)
            {
                peers.Add(
                    Register(state.Token, fast: false, () => state.Calls.Add("removed-action"))
                );
            }
            state.Emit(scenario.Kind);
            string[] first = state.Calls.ToArray();
            state.Calls.Clear();
            state.Emit(scenario.Kind);
            string[] second = state.Calls.ToArray();
            state.Token.Dispose();
            state.CheckLeaksBeforeMaintenance();
            Assert.That(
                first,
                Is.EqualTo(nested == 0 ? new[] { "d0:A" } : new[] { "d0:A", "d1:A" }),
                label
            );
            Assert.That(second, Is.EqualTo(new[] { "d0:A" }), label + ": next emission");
        }

        [TestCaseSource(nameof(DistinctHandlerCases))]
        public void DistinctHandlersKeepLazyPrefreezeAfterNestedMutation(
            MessageScenario scenario,
            int shape,
            int count,
            int nested
        )
        {
            string label = scenario + ",shape=" + shape + ",count=" + count + ",nested=" + nested;
            using CaseState state = new CaseState(label);
            MessageHandler peerHandler = new MessageHandler(new InstanceId(1651), state.Bus)
            {
                active = true,
            };
            using MessageRegistrationToken peer = MessageRegistrationToken.Create(
                peerHandler,
                state.Bus
            );
            peer.Enable();
            bool mutated = false;
            int depth = 0;
            MessageRegistrationHandle removed = default;
            Register(
                state.Token,
                shape == 1,
                () =>
                {
                    state.Calls.Add("d" + depth + ":A");
                    if (mutated)
                    {
                        return;
                    }
                    mutated = true;
                    peer.RemoveRegistration(removed);
                    Register(peer, shape == 1, () => state.Calls.Add("d" + depth + ":C"));
                    if (nested != 0)
                    {
                        ++depth;
                        try
                        {
                            state.Emit(scenario.Kind);
                        }
                        finally
                        {
                            --depth;
                        }
                    }
                }
            );
            for (int index = 0; index < count - 1; ++index)
            {
                int captured = index;
                removed = Register(
                    peer,
                    shape == 1,
                    () => state.Calls.Add("d" + depth + ":P" + captured)
                );
            }
            state.Emit(scenario.Kind);
            string[] first = state.Calls.ToArray();
            state.Calls.Clear();
            state.Emit(scenario.Kind);
            string[] second = state.Calls.ToArray();
            peer.Dispose();
            state.Token.Dispose();
            state.CheckLeaksBeforeMaintenance();
            List<string> expected = new List<string> { "d0:A" };
            if (nested != 0)
            {
                expected.Add("d1:A");
                AppendPeers(expected, count, 3, 1, updated: true);
            }
            AppendPeers(expected, count, 3, 0, updated: true);
            List<string> expectedSecond = new List<string> { "d0:A" };
            AppendPeers(expectedSecond, count, 3, 0, updated: true);
            Assert.That(first, Is.EqualTo(expected), label + ": lazy outer snapshot");
            Assert.That(second, Is.EqualTo(expectedSecond), label + ": next emission");
        }

        [TestCaseSource(nameof(ActivityCases))]
        public void InactiveHandlerResumesWithAllSnapshotsUsable(
            MessageScenario scenario,
            int shape,
            int count,
            int variant
        )
        {
            string label = scenario + ",shape=" + shape + ",count=" + count + ",variant=" + variant;
            using CaseState state = new CaseState(label);
            List<string> expected = new List<string>();
            for (int index = 0; index < count; ++index)
            {
                string name = "P" + index;
                expected.Add(name);
                Register(
                    state.Token,
                    shape == 1 || (shape == 2 && index == 0),
                    () => state.Calls.Add(name)
                );
            }
            state.Handler.active = false;
            state.Emit(scenario.Kind);
            string[] inactive = state.Calls.ToArray();
            state.Handler.active = true;
            state.Emit(scenario.Kind);
            string[] first = state.Calls.ToArray();
            state.Calls.Clear();
            state.Handler.active = false;
            state.Emit(scenario.Kind);
            string[] inactiveAgain = state.Calls.ToArray();
            state.Handler.active = true;
            state.Emit(scenario.Kind);
            string[] second = state.Calls.ToArray();
            state.Token.Dispose();
            state.CheckLeaksBeforeMaintenance();
            Assert.That(inactive, Is.Empty, label + ": inactive");
            Assert.That(inactiveAgain, Is.Empty, label + ": inactive again");
            Assert.That(first, Is.EqualTo(expected), label + ": reactivation");
            Assert.That(second, Is.EqualTo(expected), label + ": repeated reactivation");
        }

        [TestCaseSource(nameof(RepeatedMutationCases))]
        public void EveryNestedMutationRetainsItsOwnEntrySnapshot(
            MessageScenario scenario,
            int shape,
            int count,
            int nestingDepth
        )
        {
            string label =
                scenario + ",shape=" + shape + ",count=" + count + ",depth=" + nestingDepth;
            using CaseState state = new CaseState(label);
            int depth = 0;
            bool started = false;
            MessageRegistrationHandle removed = default;
            Register(
                state.Token,
                shape != 0,
                () =>
                {
                    state.Calls.Add("d" + depth + ":A");
                    if (depth == 0)
                    {
                        if (started)
                        {
                            return;
                        }
                        started = true;
                    }
                    if (depth == nestingDepth)
                    {
                        return;
                    }
                    state.Token.RemoveRegistration(removed);
                    string replacement = "C" + depth;
                    removed = Register(
                        state.Token,
                        shape == 1,
                        () => state.Calls.Add("d" + depth + ":" + replacement)
                    );
                    ++depth;
                    try
                    {
                        state.Emit(scenario.Kind);
                    }
                    finally
                    {
                        --depth;
                    }
                }
            );
            for (int index = 0; index < count - 1; ++index)
            {
                int captured = index;
                removed = Register(
                    state.Token,
                    shape == 1,
                    () => state.Calls.Add("d" + depth + ":P" + captured)
                );
            }
            state.Emit(scenario.Kind);
            string[] first = state.Calls.ToArray();
            state.Calls.Clear();
            state.Emit(scenario.Kind);
            string[] second = state.Calls.ToArray();
            state.Token.Dispose();
            state.CheckLeaksBeforeMaintenance();
            List<string> expected = new List<string>();
            for (int index = 0; index <= nestingDepth; ++index)
            {
                expected.Add("d" + index + ":A");
            }
            for (int level = nestingDepth; 0 <= level; --level)
            {
                for (int index = 0; index < count - 2; ++index)
                {
                    expected.Add("d" + level + ":P" + index);
                }
                expected.Add(
                    "d" + level + ":" + (level == 0 ? "P" + (count - 2) : "C" + (level - 1))
                );
            }
            List<string> expectedSecond = new List<string> { "d0:A" };
            for (int index = 0; index < count - 2; ++index)
            {
                expectedSecond.Add("d0:P" + index);
            }
            expectedSecond.Add("d0:C" + (nestingDepth - 1));
            Assert.That(first, Is.EqualTo(expected), label + ": every outer snapshot");
            Assert.That(second, Is.EqualTo(expectedSecond), label + ": final live registrations");
        }

        private static void AppendPeers(
            List<string> expected,
            int count,
            int mutation,
            int depth,
            bool updated
        )
        {
            int originalPeers = updated && (mutation == 2 || mutation == 3) ? count - 2 : count - 1;
            for (int index = 0; index < originalPeers; ++index)
            {
                expected.Add("d" + depth + ":P" + index);
            }
            if (updated && (mutation == 1 || mutation == 3))
            {
                expected.Add("d" + depth + ":C");
            }
        }

        private static MessageRegistrationHandle Register(
            MessageRegistrationToken token,
            bool fast,
            Action callback
        )
        {
            if (fast)
            {
                return token.RegisterGlobalAcceptAll(
                    (in IUntargetedMessage _) => callback(),
                    (in InstanceId _, in ITargetedMessage __) => callback(),
                    (in InstanceId _, in IBroadcastMessage __) => callback()
                );
            }
            return token.RegisterGlobalAcceptAll(
                _ => callback(),
                (_, _) => callback(),
                (_, _) => callback()
            );
        }

        private sealed class CaseState : IDisposable
        {
            private static readonly InstanceId Context = new InstanceId(2650);
            private readonly bool _previousDebug;
            private readonly LeakWatcher _watcher;

            internal CaseState(string label)
            {
                _previousDebug = MessagingDebug.enabled;
                MessagingDebug.enabled = false;
                Bus = new MessageBus { DiagnosticsMode = false };
                _watcher = new LeakWatcher(Bus, label: label);
                Handler = new MessageHandler(new InstanceId(1650), Bus) { active = true };
                Token = MessageRegistrationToken.Create(Handler, Bus);
                Token.Enable();
            }

            internal MessageBus Bus { get; }
            internal MessageHandler Handler { get; }
            internal MessageRegistrationToken Token { get; }
            internal List<string> Calls { get; } = new List<string>();

            internal int TotalRegistrations() =>
                Bus.RegisteredUntargeted
                + Bus.RegisteredTargeted
                + Bus.RegisteredBroadcast
                + Bus.RegisteredInterceptors
                + Bus.RegisteredPostProcessors
                + Bus.RegisteredGlobalAcceptAll;

            internal void CheckLeaksBeforeMaintenance() => _watcher.Dispose();

            internal void Emit(MessageKind kind)
            {
                ProbeMessage message = new ProbeMessage();
                InstanceId context = Context;
                switch (kind)
                {
                    case MessageKind.Untargeted:
                        Bus.UntargetedBroadcast(ref message);
                        break;
                    case MessageKind.Targeted:
                        Bus.TargetedBroadcast(ref context, ref message);
                        break;
                    case MessageKind.Broadcast:
                        Bus.SourcedBroadcast(ref context, ref message);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(kind));
                }
            }

            public void Dispose()
            {
                try
                {
                    Token.Dispose();
                    _watcher.Dispose();
                    Bus.Trim(force: true);
                }
                finally
                {
                    MessagingDebug.enabled = _previousDebug;
                }
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
