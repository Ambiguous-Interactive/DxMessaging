#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Tests.Runtime.Benchmarks;
    using DxMessaging.Tests.Runtime.Scripts.Messages;
    using NUnit.Framework;

    /// <summary>
    /// Executes each public comparison's exact internal registration and emission path.
    /// Topology equivalence does not establish MessagePipe semantic or timing parity.
    /// Keep the mapping in sync with docs/runbooks/perf-benchmark-methodology.md.
    /// </summary>
    [Category("ComparisonContract")]
    public sealed class ComparisonDispatchTopologyTests
    {
        // Null dispatch means the independent ComparisonTopologyBenchmarks workload.
        private static readonly IReadOnlyDictionary<
            ComparisonScenario,
            DispatchBenchmarkScenario?
        > Map = new Dictionary<ComparisonScenario, DispatchBenchmarkScenario?>
        {
            [ComparisonScenario.GlobalToOneSubscriber] =
                DispatchBenchmarkScenario.UntargetedFloodOneHandler,
            [ComparisonScenario.GlobalToManySubscribers] =
                DispatchBenchmarkScenario.UntargetedFloodSixteenHandlersOnePriority,
            [ComparisonScenario.KeyedToOneOfMany] = null,
            [ComparisonScenario.PriorityOrderedDispatch] = null,
            [ComparisonScenario.FilteredDispatch] = null,
            [ComparisonScenario.PostProcessingDispatch] = null,
            [ComparisonScenario.InterceptedPostProcessingDispatch] = null,
            [ComparisonScenario.SubscribeUnsubscribeChurn] = null,
            [ComparisonScenario.StructMessageNoBoxing] = null,
        };

        [Test]
        public void EveryPublicComparisonHasExactlyOneExecutableTwin()
        {
            CollectionAssert.AreEquivalent(
                ComparisonScenarios.All,
                Map.Keys,
                "Every public row must map to exactly one internal topology twin."
            );
            CollectionAssert.AreEquivalent(
                Map.Where(pair => pair.Value == null).Select(pair => pair.Key),
                ComparisonTopologyBenchmarks.Scenarios,
                "Every missing dispatch-table shape must have an independently executable benchmark."
            );
            Assert.AreEqual(
                DispatchBenchmarkScenario.UntargetedFloodSixteenHandlersOnePriority,
                Map[ComparisonScenario.GlobalToManySubscribers],
                "GlobalToMany must never regress to the stale four-handler mapping."
            );
            foreach (KeyValuePair<ComparisonScenario, DispatchBenchmarkScenario?> pair in Map)
            {
                string key = pair.Value.HasValue
                    ? DispatchBenchmarkScenarios.Key(pair.Value.Value)
                    : ComparisonTopologyBenchmarks.RowKey(pair.Key);
                Assert.IsNotEmpty(key, $"[{pair.Key}] The mapped internal row needs a stable key.");
                Assert.IsFalse(
                    key.StartsWith("Comparison_", StringComparison.Ordinal),
                    $"[{pair.Key}] An internal result must not overwrite a public comparison row."
                );
            }
        }

        private static IEnumerable<TestCaseData> TwinCases()
        {
            foreach (ComparisonScenario scenario in ComparisonScenarios.All)
            {
                foreach (int emits in new[] { 0, 1, 17 })
                {
                    foreach (bool diagnostics in new[] { false, true })
                    {
                        yield return new TestCaseData(scenario, emits, diagnostics).SetName(
                            $"TrueTwin{scenario}Emits{emits}Diagnostics{diagnostics}"
                        );
                    }
                }
            }
        }

        [TestCaseSource(nameof(TwinCases))]
        public void TrueTwinsObserveMatchingRegistrationsDispatchAndCleanup(
            ComparisonScenario scenario,
            int emits,
            bool globalDiagnostics
        )
        {
            string label = $"[{scenario}, emits={emits}, diagnostics={globalDiagnostics}]";
            using DiagnosticsScope diagnostics = new(
                globalDiagnostics ? DiagnosticsTarget.All : DiagnosticsTarget.Off,
                diagnosticsStackTraces: globalDiagnostics
            );
            (string[] topology, long invocations) = ObserveInternal(scenario, emits);
            using DxMessagingBridge bridge = new();
            bridge.Prepare(scenario);
            MessageBus bus = bridge.BusForContract;
            AssertTopology(topology, bridge.CaptureTopologyForContract(), label);
            AssertDeclaredShape(scenario, topology, label);
            Assert.AreEqual(
                ExpectedPayload(scenario),
                bridge.DispatchedPayloadType(scenario),
                $"{label} The closed generic payload path must match the manifest."
            );
            EnableChurnLog(scenario, bus, emits);
            for (int index = 0; index < emits; index++)
            {
                bridge.EmitOnce();
            }
            AssertChurnLog(scenario, bus, emits);
            ComparisonTopologyBenchmarks.AssertAccounting(scenario, emits, invocations);
            ComparisonTopologyBenchmarks.AssertAccounting(scenario, emits, bridge.ProgressMarker);
            AssertTopology(
                topology,
                bridge.CaptureTopologyForContract(),
                label + " after operations"
            );
            bridge.Dispose();
            ComparisonTopologyBenchmarks.AssertClean(bus, label);
        }

        private static (string[] Topology, long Invocations) ObserveInternal(
            ComparisonScenario scenario,
            int emits
        )
        {
            if (Map[scenario] is DispatchBenchmarkScenario dispatch)
            {
                return DispatchThroughputBenchmarks.ObserveTopologyForContract(dispatch, emits);
            }
            using ComparisonTopologyBenchmarks.Workload workload = new(scenario);
            string[] topology = workload.CaptureTopology();
            EnableChurnLog(scenario, workload.Bus, emits);
            workload.EmitMany(emits);
            AssertChurnLog(scenario, workload.Bus, emits);
            AssertTopology(
                topology,
                workload.CaptureTopology(),
                $"{scenario} internal after operations"
            );
            workload.Dispose();
            ComparisonTopologyBenchmarks.AssertClean(workload.Bus, scenario.ToString());
            return (topology, workload.Progress);
        }

        private static void EnableChurnLog(ComparisonScenario scenario, MessageBus bus, int emits)
        {
            if (scenario == ComparisonScenario.SubscribeUnsubscribeChurn)
            {
                DispatchThroughputBenchmarks.EnableRegistrationLogForContract(
                    bus,
                    Math.Max(1, emits * 2)
                );
            }
        }

        private static void AssertChurnLog(ComparisonScenario scenario, MessageBus bus, int emits)
        {
            if (scenario != ComparisonScenario.SubscribeUnsubscribeChurn)
            {
                return;
            }
            IReadOnlyList<MessagingRegistration> log = bus.Log.Registrations;
            Assert.AreEqual(
                emits * 2,
                log.Count,
                "SubUnsub must perform one actual register/remove pair per operation; a progress increment alone is insufficient."
            );
            for (int index = 0; index < log.Count; index++)
            {
                MessagingRegistration entry = log[index];
                string label = $"[SubUnsub operation={index / 2}, event={index}]";
                Assert.AreEqual(
                    index % 2 == 0 ? RegistrationType.Register : RegistrationType.Deregister,
                    entry.registrationType,
                    $"{label} Registration must precede removal in each cycle."
                );
                Assert.AreEqual(
                    RegistrationMethod.Untargeted,
                    entry.registrationMethod,
                    $"{label} Each operation must use the untargeted registration path."
                );
                Assert.AreEqual(
                    typeof(SimpleUntargetedMessage),
                    entry.type,
                    $"{label} Each operation must register the canonical payload."
                );
                Assert.AreEqual(
                    log[0].id,
                    entry.id,
                    $"{label} Every operation must reuse the same handler owner."
                );
            }
        }

        private static Type ExpectedPayload(ComparisonScenario scenario) =>
            scenario switch
            {
                ComparisonScenario.KeyedToOneOfMany => typeof(SimpleTargetedMessage),
                ComparisonScenario.StructMessageNoBoxing => typeof(ComparisonStructPayload),
                _ => typeof(SimpleUntargetedMessage),
            };

        // Independent manifest: do not derive expected counts from either workload builder.
        private static void AssertDeclaredShape(
            ComparisonScenario scenario,
            string[] topology,
            string label
        )
        {
            int tokens = scenario == ComparisonScenario.GlobalToManySubscribers ? 16 : 1;
            int registrations = scenario switch
            {
                ComparisonScenario.GlobalToManySubscribers => 16,
                ComparisonScenario.KeyedToOneOfMany => 16,
                ComparisonScenario.PriorityOrderedDispatch => 4,
                ComparisonScenario.FilteredDispatch => 2,
                ComparisonScenario.PostProcessingDispatch => 2,
                ComparisonScenario.InterceptedPostProcessingDispatch => 3,
                ComparisonScenario.SubscribeUnsubscribeChurn => 0,
                _ => 1,
            };
            Assert.AreEqual(
                tokens,
                topology.Count(row => row.StartsWith("token:", StringComparison.Ordinal)),
                $"{label} Each handler owner must have exactly one enabled token, including the empty churn token."
            );
            string[] actual = topology
                .Where(row => row.StartsWith("registration:", StringComparison.Ordinal))
                .ToArray();
            Assert.AreEqual(
                registrations,
                actual.Length,
                $"{label} Registration cardinality must match."
            );
            for (int index = 0; index < tokens; index++)
            {
                CollectionAssert.Contains(
                    topology,
                    $"token:{index}:True:False",
                    $"{label} Token {index} must be enabled with diagnostics disabled."
                );
            }
            string payload = ExpectedPayload(scenario).FullName;
            List<string> expected = new();
            if (scenario == ComparisonScenario.KeyedToOneOfMany)
            {
                for (int index = 0; index < 16; index++)
                {
                    expected.Add($"registration:0:Targeted:{payload}:0:{41000 + index}");
                }
            }
            else if (scenario != ComparisonScenario.SubscribeUnsubscribeChurn)
            {
                if (
                    scenario == ComparisonScenario.FilteredDispatch
                    || scenario == ComparisonScenario.InterceptedPostProcessingDispatch
                )
                {
                    expected.Add($"registration:0:UntargetedInterceptor:{payload}:0:none");
                }
                if (
                    scenario == ComparisonScenario.PostProcessingDispatch
                    || scenario == ComparisonScenario.InterceptedPostProcessingDispatch
                )
                {
                    expected.Add($"registration:0:UntargetedPostProcessor:{payload}:0:none");
                }
                int handlers = scenario == ComparisonScenario.PriorityOrderedDispatch ? 4 : tokens;
                for (int index = 0; index < handlers; index++)
                {
                    int token = tokens == 16 ? index : 0;
                    int priority =
                        scenario == ComparisonScenario.PriorityOrderedDispatch ? index : 0;
                    expected.Add($"registration:{token}:Untargeted:{payload}:{priority}:none");
                }
            }
            CollectionAssert.AreEquivalent(
                expected,
                actual,
                $"{label} Actual payloads, handler ownership, priorities, hooks, and distinct routes must match the independent manifest."
            );
        }

        private static void AssertTopology(string[] expected, string[] actual, string label) =>
            CollectionAssert.AreEqual(
                expected,
                actual,
                $"[{label}] Actual bus counters, token ownership, payloads, priorities, routes, and diagnostics must match."
            );

        [TestCase("token")]
        [TestCase("handler")]
        [TestCase("priority")]
        [TestCase("payload")]
        [TestCase("route")]
        public void ActualTopologyDriftFailsValidation(string dimension)
        {
            ComparisonScenario scenario =
                dimension == "route"
                    ? ComparisonScenario.KeyedToOneOfMany
                    : ComparisonScenario.PriorityOrderedDispatch;
            using DxMessagingBridge bridge = new();
            bridge.Prepare(scenario);
            using ComparisonTopologyBenchmarks.Workload workload = new(scenario);
            string[] expected = bridge.CaptureTopologyForContract();
            AssertTopology(expected, workload.CaptureTopology(), dimension + " before mutation");
            switch (dimension)
            {
                case "token":
                    _ = workload.AddToken();
                    break;
                case "handler":
                    _ = workload.Token.RegisterUntargeted<SimpleUntargetedMessage>(
                        (in SimpleUntargetedMessage message) => { }
                    );
                    break;
                case "priority":
                case "payload":
                    workload.Token.UnregisterAll();
                    for (int priority = 0; priority < 4; priority++)
                    {
                        if (dimension == "payload" && priority == 3)
                        {
                            _ = workload.Token.RegisterUntargeted<ComparisonStructPayload>(
                                (in ComparisonStructPayload message) => { },
                                priority
                            );
                        }
                        else
                        {
                            _ = workload.Token.RegisterUntargeted<SimpleUntargetedMessage>(
                                (in SimpleUntargetedMessage message) => { },
                                dimension == "priority" && priority == 3 ? 7 : priority
                            );
                        }
                    }
                    break;
                case "route":
                    workload.Token.UnregisterAll();
                    for (int index = 0; index < 16; index++)
                    {
                        _ = workload.Token.RegisterTargeted<SimpleTargetedMessage>(
                            new InstanceId(index == 15 ? 41999 : 41000 + index),
                            (in SimpleTargetedMessage message) => { }
                        );
                    }
                    break;
            }
            if (dimension == "priority" || dimension == "payload" || dimension == "route")
            {
                workload.Token.Enable();
                Assert.AreEqual(
                    expected.Length,
                    workload.CaptureTopology().Length,
                    $"The {dimension} mutation must preserve token and registration cardinality."
                );
                Assert.AreEqual(
                    1,
                    expected
                        .Zip(workload.CaptureTopology(), (before, after) => before != after)
                        .Count(changed => changed),
                    $"The {dimension} mutation must change exactly one registration row, with bus and token state preserved."
                );
            }
            Assert.Throws<AssertionException>(
                () => AssertTopology(expected, workload.CaptureTopology(), dimension),
                $"Changing an actual {dimension} must fail exact topology validation."
            );
        }

        [Test]
        public void MissingTransientChurnWorkFailsEvenWithEmptyFinalTopology()
        {
            const ComparisonScenario scenario = ComparisonScenario.SubscribeUnsubscribeChurn;
            using ComparisonTopologyBenchmarks.Workload workload = new(scenario);
            EnableChurnLog(scenario, workload.Bus, 1);
            ComparisonTopologyBenchmarks.AssertClean(workload.Bus, "empty churn state");
            Assert.Throws<AssertionException>(
                () => AssertChurnLog(scenario, workload.Bus, 1),
                "An empty bus with no register/remove events cannot stand in for a completed cycle."
            );
            workload.EmitMany(1);
            AssertChurnLog(scenario, workload.Bus, 1);
            ComparisonTopologyBenchmarks.AssertClean(workload.Bus, "completed churn state");
        }

        [Test]
        public void LostFanOutAndIncompleteTeardownFailReconciliation()
        {
            const ComparisonScenario scenario = ComparisonScenario.PriorityOrderedDispatch;
            using ComparisonTopologyBenchmarks.Workload workload = new(scenario);
            Assert.Throws<AssertionException>(
                () =>
                    ComparisonTopologyBenchmarks.AssertClean(
                        workload.Bus,
                        "intentional live registrations"
                    ),
                "Live registrations must fail teardown validation."
            );
            workload.Token.Disable();
            workload.EmitMany(1);
            Assert.Throws<AssertionException>(
                () => ComparisonTopologyBenchmarks.AssertAccounting(scenario, 1, workload.Progress),
                "Disabling actual callbacks must fail exact fan-out reconciliation."
            );
            workload.Dispose();
            ComparisonTopologyBenchmarks.AssertClean(workload.Bus, "after disposal");
        }

        [Test]
        public void TwinTimingRequiresItsOwnExplicitCategory()
        {
            object[] categories = typeof(ComparisonTopologyBenchmarks).GetCustomAttributes(
                typeof(CategoryAttribute),
                true
            );
            string[] names = categories
                .Cast<CategoryAttribute>()
                .Select(category => category.Name)
                .ToArray();
            CollectionAssert.Contains(
                names,
                "PerfTopologyTwin",
                "Twin timing must be explicitly selectable."
            );
            CollectionAssert.DoesNotContain(
                names,
                "PerfComparison",
                "Ordinary comparison timing must not gain seven windows."
            );
            CollectionAssert.DoesNotContain(
                names,
                "ComparisonContract",
                "Fast contract checks must never execute timing windows."
            );
        }
    }
}
#endif
