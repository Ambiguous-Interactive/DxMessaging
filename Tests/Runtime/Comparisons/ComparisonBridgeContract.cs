#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons
{
    using System;
    using System.Collections.Generic;
    using NUnit.Framework;

    /// <summary>
    /// Shared contract assertions and NUnit case sources every comparison bridge must satisfy,
    /// factored out so the zero-dependency roster AND the gated package rosters (External,
    /// UnityAtoms) all enforce the SAME fast checks from one place. None of these open a benchmark
    /// measurement window, so a broken bridge fails here in milliseconds with a precise message
    /// instead of deep inside the multi-minute performance run.
    /// </summary>
    public static class ComparisonBridgeContract
    {
        /// <summary>
        /// One identity case per roster bridge, named <c>Bridge_&lt;key&gt;</c>; feeds a test
        /// taking <c>(string rosterKey, Func&lt;IMessagingTechBridge&gt; factory)</c>.
        /// </summary>
        public static IEnumerable<TestCaseData> IdentityCases(
            IEnumerable<(string key, Func<IMessagingTechBridge> factory)> roster
        )
        {
            foreach ((string key, Func<IMessagingTechBridge> factory) in roster)
            {
                yield return new TestCaseData(key, factory).SetName($"Bridge_{key}");
            }
        }

        /// <summary>
        /// One case per (roster bridge, scenario), named <c>Bridge_&lt;key&gt;_&lt;scenarioKey&gt;</c>;
        /// feeds a test taking <c>(string rosterKey, Func&lt;IMessagingTechBridge&gt; factory,
        /// ComparisonScenario scenario)</c>.
        /// </summary>
        public static IEnumerable<TestCaseData> EmitOnceAccountingCases(
            IEnumerable<(string key, Func<IMessagingTechBridge> factory)> roster
        )
        {
            foreach ((string key, Func<IMessagingTechBridge> factory) in roster)
            {
                foreach (ComparisonScenario scenario in ComparisonScenarios.All)
                {
                    yield return new TestCaseData(key, factory, scenario).SetName(
                        $"Bridge_{key}_{ComparisonScenarios.Key(scenario)}"
                    );
                }
            }
        }

        /// <summary>
        /// Asserts the bridge declares non-empty identity metadata and that its self-reported
        /// <see cref="IMessagingTechBridge.TechKey"/> matches the roster key it is registered under.
        /// </summary>
        public static void AssertTechIdentity(string rosterKey, Func<IMessagingTechBridge> factory)
        {
            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }

            using IMessagingTechBridge bridge = factory();
            Assert.IsNotEmpty(
                bridge.TechKey,
                $"Bridge '{rosterKey}' must declare a non-empty TechKey."
            );
            Assert.IsNotEmpty(
                bridge.TechName,
                $"Bridge '{rosterKey}' must declare a non-empty TechName."
            );
            Assert.AreEqual(
                rosterKey,
                bridge.TechKey,
                $"Roster key '{rosterKey}' must match the bridge's own TechKey '{bridge.TechKey}'."
            );
        }

        /// <summary>
        /// For one supported (bridge, scenario): asserts a single <see cref="IMessagingTechBridge.EmitOnce"/>
        /// advances <see cref="IMessagingTechBridge.ProgressMarker"/> by EXACTLY the declared
        /// <see cref="IMessagingTechBridge.InvocationsPerOperation"/> (which must be positive).
        /// Unsupported and PlayMode-only pairs are ignored. This is the check that catches a
        /// fan-out collapsing because the bridge subscribed value-equal delegates a bus deduped.
        ///
        /// It detects a collapse only where the declared fan-out exceeds 1 (e.g. GlobalToMany, and
        /// DxMessaging's PriorityOrdered). A scenario that fires exactly one of many keyed
        /// subscribers advances by 1 whether or not a bus deduped across keys, so keyed dispatch is
        /// out of this check's reach -- by design, since no bridge here dedupes across distinct keys.
        /// </summary>
        public static void AssertEmitOnceAccounting(
            string rosterKey,
            Func<IMessagingTechBridge> factory,
            ComparisonScenario scenario
        )
        {
            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }

            using IMessagingTechBridge bridge = factory();
            if (bridge.RequiresPlayMode && !UnityEngine.Application.isPlaying)
            {
                Assert.Ignore($"{bridge.TechName} requires PlayMode; skipping in EditMode.");
                return;
            }
            if (!bridge.Supports(scenario))
            {
                Assert.Ignore(
                    $"{bridge.TechName} does not support '{ComparisonScenarios.DisplayName(scenario)}'."
                );
                return;
            }

            long fanOut = bridge.InvocationsPerOperation(scenario);
            Assert.Greater(
                fanOut,
                0,
                $"Bridge '{rosterKey}' supports '{scenario}' so it must declare a positive fan-out "
                    + "(InvocationsPerOperation > 0)."
            );

            bridge.Prepare(scenario);
            long before = bridge.ProgressMarker;
            bridge.EmitOnce();

            Assert.AreEqual(
                before + fanOut,
                bridge.ProgressMarker,
                $"Bridge '{rosterKey}' scenario '{scenario}': one EmitOnce() advanced ProgressMarker by "
                    + $"{bridge.ProgressMarker - before}, expected {fanOut}. For fan-out scenarios this usually "
                    + "means the bridge subscribed delegates that are Delegate.Equals-equal (e.g. a loop of "
                    + "identical lambdas capturing only 'this') and the bus deduped them; subscribe genuinely-distinct "
                    + "delegates via a FanOut group instead. This fast check exists to fail before the long "
                    + "performance measurement window floods CI logs."
            );
        }

        /// <summary>
        /// Locks the StructMessageNoBoxing payload-fidelity contract: a bridge may not mark the
        /// struct scenario Supported while secretly dispatching a primitive (e.g. an int via a
        /// fake event) or a boxed/different payload. No-ops for the seven non-struct scenarios,
        /// so wiring it into a per-(bridge,scenario) case source effectively asserts once per
        /// bridge. If the bridge does not support the struct scenario, its
        /// <see cref="IMessagingTechBridge.DispatchedPayloadType"/> must be null; if it does,
        /// the declared payload must be a non-primitive, non-enum value type, and every
        /// non-DxMessaging bridge must declare exactly <see cref="ComparisonStructPayload"/>.
        /// This is pure metadata, so it opens no benchmark window.
        /// </summary>
        public static void AssertStructScenarioPayloadFidelity(
            string rosterKey,
            Func<IMessagingTechBridge> factory,
            ComparisonScenario scenario
        )
        {
            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }
            if (scenario != ComparisonScenario.StructMessageNoBoxing)
            {
                return;
            }

            using IMessagingTechBridge bridge = factory();
            if (!bridge.Supports(scenario))
            {
                Assert.IsNull(
                    bridge.DispatchedPayloadType(scenario),
                    $"Bridge '{rosterKey}' does not support the struct scenario, so DispatchedPayloadType must be null."
                );
                return;
            }

            Type payload = bridge.DispatchedPayloadType(scenario);
            Assert.IsNotNull(
                payload,
                $"Bridge '{rosterKey}' supports the struct scenario; DispatchedPayloadType must be non-null."
            );
            Assert.IsTrue(
                payload.IsValueType && !payload.IsPrimitive && !payload.IsEnum,
                $"Bridge '{rosterKey}' marks the struct scenario Supported but dispatches '{payload.Name}', "
                    + "which is not a boxing-free non-primitive struct. Dispatch ComparisonStructPayload (or, for "
                    + "DxMessaging, an IUntargetedMessage struct) or mark the scenario unsupported."
            );
            if (!string.Equals(bridge.TechKey, "DxMessaging", StringComparison.Ordinal))
            {
                Assert.AreEqual(
                    typeof(ComparisonStructPayload),
                    payload,
                    $"Bridge '{rosterKey}' must dispatch the canonical ComparisonStructPayload for the struct scenario; "
                        + $"it declared '{payload.FullName}'."
                );
            }
        }
    }
}
#endif
