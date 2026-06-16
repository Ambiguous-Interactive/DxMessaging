#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Text.RegularExpressions;
    using DxMessaging.Core;
    using DxMessaging.Core.MessageBus;
    using DxMessaging.Tests.Runtime;
    using NUnit.Framework;
    using UnityEngine.TestTools;

    public sealed class TestAttributeContractTests
    {
        private const string TestAssemblyNamePrefix = "WallstopStudios.DxMessaging.Tests";
        private const string TestNamespacePrefix = "DxMessaging.Tests";

        /// <summary>
        /// Matches the C# 9 target-typed pattern <c>GameObject identifier = new(...)</c>.
        /// Used by <see cref="FixturesUsingMessagingTestBaseUseSpawnedCleanupPattern"/>
        /// to detect spawn calls that the older substring grep for
        /// <c>"new GameObject("</c> would miss. Compiled once because the
        /// fixture scan runs across every test source file.
        /// </summary>
        private static readonly Regex GameObjectTargetTypedNewPattern = new(
            @"\bGameObject\b\s+\w+\s*=\s*new\s*\(",
            RegexOptions.Compiled | RegexOptions.CultureInvariant
        );

        private static bool IsDxMessagingTestAssembly(Assembly assembly)
        {
            string assemblyName = assembly.GetName().Name;
            return assemblyName != null
                && assemblyName.StartsWith(TestAssemblyNamePrefix, StringComparison.Ordinal);
        }

        private static bool IsDxMessagingTestNamespace(string namespaceName)
        {
            return namespaceName != null
                && namespaceName.StartsWith(TestNamespacePrefix, StringComparison.Ordinal);
        }

        [Test]
        public void UnityTestsDoNotUseTestCaseAttributes()
        {
            List<string> offenders = FindMethods(method =>
                    HasAttribute<UnityTestAttribute>(method)
                    && (
                        HasAttribute<TestCaseAttribute>(method)
                        || HasAttribute<TestCaseSourceAttribute>(method)
                    )
                )
                .Select(FormatMethod)
                .ToList();

            Assert.That(
                offenders,
                Is.Empty,
                "Found [UnityTest] methods decorated with [TestCase] or [TestCaseSource]. Use [ValueSource] for parameterized coroutine tests.\n"
                    + string.Join("\n", offenders)
            );
        }

        [Test]
        public void NonUnityTestsDoNotReturnIEnumerator()
        {
            List<string> offenders = FindMethods(method =>
                    method.ReturnType == typeof(IEnumerator)
                    && !HasAttribute<UnityTestAttribute>(method)
                    && (
                        HasAttribute<TestAttribute>(method)
                        || HasAttribute<TestCaseAttribute>(method)
                        || HasAttribute<TestCaseSourceAttribute>(method)
                    )
                )
                .Select(FormatMethod)
                .ToList();

            Assert.That(
                offenders,
                Is.Empty,
                "Found non-[UnityTest] methods returning IEnumerator. Use [UnityTest] for coroutine tests.\n"
                    + string.Join("\n", offenders)
            );
        }

        [Test]
        public void UnityTestsReturnIEnumerator()
        {
            List<string> offenders = FindMethods(method =>
                    HasAttribute<UnityTestAttribute>(method)
                    && method.ReturnType != typeof(IEnumerator)
                )
                .Select(FormatMethod)
                .ToList();

            Assert.That(
                offenders,
                Is.Empty,
                "Found [UnityTest] methods that do not return IEnumerator.\n"
                    + string.Join("\n", offenders)
            );
        }

        /// <summary>
        /// Flags kind-duplicated tests that should be consolidated via
        /// <c>[ValueSource(typeof(MessageScenarios), ...)]</c>. Two shapes are
        /// caught:
        /// <list type="bullet">
        /// <item><description>
        /// TRUE TRIPLETS: methods whose names start with <c>Untargeted</c>,
        /// <c>Targeted</c>, AND <c>Broadcast</c> for the same kind-stripped
        /// base name within a single fixture (suggested source:
        /// <c>MessageScenarios.AllKinds</c>).
        /// </description></item>
        /// <item><description>
        /// TWO-KIND PAIRS: methods whose names start with <c>Targeted</c> AND
        /// <c>Broadcast</c> for the same kind-stripped base name (with no
        /// <c>Untargeted</c> sibling) within a single fixture. These differ
        /// only by message kind and should collapse to a single method over
        /// <c>MessageScenarios.KindsWithComponentTarget</c>.
        /// </description></item>
        /// </list>
        /// Both <c>[UnityTest]</c> and plain <c>[Test]</c> methods are scanned,
        /// and the two-kind Targeted+Broadcast pair shape is detected in addition
        /// to triplets, as a forward-looking guard so a future kind-named
        /// <c>[Test]</c> or Targeted+Broadcast group cannot slip past a
        /// <c>[UnityTest]</c>-only, triplet-only scan. Methods already
        /// parameterized over <see cref="MessageScenario"/>
        /// short-circuit out so they cannot satisfy either criterion. Methods
        /// that exist in only one kind variant (legitimate kind-asymmetric
        /// tests) are not flagged. Kind-specific fixtures named
        /// <c>*Specific*Tests</c> are exempt because their assertion semantics
        /// do not translate across kinds. The contract pin lives in the
        /// <c>tests-must-be-parameterized-by-message-kind</c> skill.
        /// </summary>
        [Test]
        public void TripletEmitTestsUseScenarioParameterization()
        {
            // Group [UnityTest] AND [Test] methods by their declaring fixture
            // and by the kind-stripped base name, tracking which leading kinds
            // appear for each base name. A "triplet" is a base name that has
            // Untargeted, Targeted, AND Broadcast siblings in the same fixture;
            // a "two-kind pair" is a base name with exactly Targeted AND
            // Broadcast (no Untargeted). Already-consolidated methods (those
            // accepting a MessageScenario parameter) short-circuit out so they
            // cannot accidentally satisfy either criterion.
            Dictionary<Type, Dictionary<string, HashSet<string>>> kindsByFixture = new();

            foreach (MethodInfo method in GetDxMessagingTestMethods())
            {
                if (
                    !HasAttribute<UnityTestAttribute>(method)
                    && !HasAttribute<TestAttribute>(method)
                )
                {
                    continue;
                }

                Type fixture = method.DeclaringType;
                if (fixture == null)
                {
                    continue;
                }

                if (fixture.Name.IndexOf("Specific", StringComparison.Ordinal) >= 0)
                {
                    // Kind-specific fixtures are exempt by design.
                    continue;
                }

                string name = method.Name;
                string kind;
                string kindStripped;

                if (name.StartsWith("Untargeted", StringComparison.Ordinal))
                {
                    kind = "Untargeted";
                    kindStripped = name.Substring("Untargeted".Length);
                }
                else if (name.StartsWith("Targeted", StringComparison.Ordinal))
                {
                    kind = "Targeted";
                    kindStripped = name.Substring("Targeted".Length);
                }
                else if (name.StartsWith("Broadcast", StringComparison.Ordinal))
                {
                    kind = "Broadcast";
                    kindStripped = name.Substring("Broadcast".Length);
                }
                else
                {
                    continue;
                }

                if (HasMessageScenarioParameter(method))
                {
                    // Already consolidated; do not contribute to any bucket.
                    continue;
                }

                if (
                    !kindsByFixture.TryGetValue(
                        fixture,
                        out Dictionary<string, HashSet<string>> nameMap
                    )
                )
                {
                    nameMap = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                    kindsByFixture[fixture] = nameMap;
                }

                if (!nameMap.TryGetValue(kindStripped, out HashSet<string> kindsSet))
                {
                    kindsSet = new HashSet<string>(StringComparer.Ordinal);
                    nameMap[kindStripped] = kindsSet;
                }

                kindsSet.Add(kind);
            }

            // Triplets intentionally not consolidated due to kind-asymmetric behavior.
            // Each entry must include a justification comment explaining why
            // consolidation is unsafe. Future maintainers should be able to remove
            // an exemption if they consolidate the triplet later.
            HashSet<string> exemptedTriplets = new HashSet<string>(StringComparer.Ordinal)
            {
                // NominalTests.RemoveOrder: the Untargeted variant exercises three
                // Run blocks, while Targeted and Broadcast each exercise five Run
                // blocks (extra ComponentTargeted/ComponentBroadcast permutations
                // only available for those kinds). The bodies are not structurally
                // identical, and consolidating would weaken the kind-asymmetric
                // coverage the longer variants provide.
                // Remove when the Targeted/Broadcast variants drop their extra
                // Component-* Run blocks OR the harness gains a uniform way to
                // declare per-kind extra emit paths (then the body becomes
                // identical and consolidation is safe).
                "DxMessaging.Tests.Runtime.Core.NominalTests.RemoveOrder",
                // OrderingManyRegistrationsTests.PostProcessorsManyRegistrationsMaintainOrder:
                // the Untargeted variant registers only fast post-processors with
                // a single ordering list, while Targeted and Broadcast register
                // both fast and action post-processors with two lists. The number
                // of register loops and assertion shape differs across kinds, so
                // consolidation would either drop assertions or test a code path
                // (action post-processors) that is not exercised today on the
                // untargeted bus.
                // Remove when action post-processors are wired into the
                // untargeted bus (so the untargeted variant uses the same
                // dual-list shape as the targeted/broadcast variants).
                "DxMessaging.Tests.Runtime.Core.OrderingManyRegistrationsTests.PostProcessorsManyRegistrationsMaintainOrder",
                // RegistrationTests.Interceptor: the Untargeted variant emits via
                // a single EmitUntargeted path, while Targeted and Broadcast each
                // exercise BOTH the GameObject-targeted and Component-targeted
                // (or GameObject-broadcast and Component-broadcast) emit paths in
                // the post-deregistration assertion to prove deregistration applies
                // across both targeting/source variants. Consolidation would
                // reduce the targeted/broadcast assertions to a single emit path.
                // Remove when the Untargeted variant grows a Component-style
                // second emit path OR the helper harness collapses the per-kind
                // emit list so each variant exercises the same number of paths.
                "DxMessaging.Tests.Runtime.Core.RegistrationTests.Interceptor",
            };

            // Two-kind Targeted+Broadcast pairs intentionally NOT consolidated.
            // Each entry's bodies differ by more than message-kind plumbing, so
            // a ScenarioHarness merge (which expresses a target as a single
            // InstanceId) would silently drop coverage. Remove an entry only
            // when the bodies become plumbing-only-different.
            HashSet<string> exemptedTwoKindPairs = new HashSet<string>(StringComparer.Ordinal)
            {
                // MutationDestructionTests.ComponentDestroyOtherListenerDoesNotRun:
                // the Targeted/Broadcast Component variants register and emit via
                // the COMPONENT-identity overloads (RegisterComponentTargeted /
                // EmitComponentTargeted, RegisterComponentBroadcast /
                // EmitComponentBroadcast). The fixture's parameterized
                // DestroyOtherListenerDoesNotRun already covers the GameObject /
                // *WithoutTargeting paths; these two pin the distinct
                // Component-identity dispatch path that ScenarioHarness's single
                // InstanceId target cannot express without collapsing the two
                // identity kinds. Remove when ScenarioHarness grows a
                // Component-vs-GameObject target distinction.
                "DxMessaging.Tests.Runtime.Core.MutationDestructionTests.ComponentDestroyOtherListenerDoesNotRun",
                // OrderingTests Targeted/Broadcast GameObject-identity ordering
                // pairs: each pair registers and emits through the
                // GameObject-targeted / GameObject-broadcast overloads and has a
                // sibling Component-identity variant (the
                // *Component* methods below). The "...GameObject..." vs
                // "...Component..." split is the coverage these tests exist to
                // pin; ScenarioHarness resolves a target to a single InstanceId
                // and cannot preserve the GameObject-vs-Component identity-path
                // distinction, so a merge would erase one path. Remove an entry
                // when ScenarioHarness can express the target-identity kind.
                "DxMessaging.Tests.Runtime.Core.OrderingTests.SamePriorityActionsGameObjectInRegistrationOrder",
                "DxMessaging.Tests.Runtime.Core.OrderingTests.SamePriorityFastGameObjectInRegistrationOrder",
                "DxMessaging.Tests.Runtime.Core.OrderingTests.MixedFastBeforeActionsGameObject",
                // OrderingTests Targeted/Broadcast Component-identity ordering
                // pairs: the mirror of the GameObject pairs above, pinning the
                // Component-identity ordering path. Same justification and same
                // removal condition.
                "DxMessaging.Tests.Runtime.Core.OrderingTests.SamePriorityActionsComponentInRegistrationOrder",
                "DxMessaging.Tests.Runtime.Core.OrderingTests.SamePriorityFastComponentInRegistrationOrder",
                "DxMessaging.Tests.Runtime.Core.OrderingTests.MixedFastBeforeActionsComponent",
                "DxMessaging.Tests.Runtime.Core.OrderingTests.MixedFastThenActionsComponent",
            };

            List<string> offenders = new();
            foreach (
                KeyValuePair<
                    Type,
                    Dictionary<string, HashSet<string>>
                > fixturePair in kindsByFixture
            )
            {
                foreach (KeyValuePair<string, HashSet<string>> namePair in fixturePair.Value)
                {
                    HashSet<string> kinds = namePair.Value;
                    string fullKey = $"{fixturePair.Key.FullName}.{namePair.Key}";

                    if (kinds.Count == 3)
                    {
                        if (exemptedTriplets.Contains(fullKey))
                        {
                            continue;
                        }

                        offenders.Add(
                            $"{fixturePair.Key.FullName}: triplet '*{namePair.Key}' "
                                + "(Untargeted, Targeted, Broadcast variants all exist; "
                                + "consolidate via [ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))])"
                        );
                    }
                    else if (
                        kinds.Count == 2
                        && kinds.Contains("Targeted")
                        && kinds.Contains("Broadcast")
                    )
                    {
                        if (exemptedTwoKindPairs.Contains(fullKey))
                        {
                            continue;
                        }

                        offenders.Add(
                            $"{fixturePair.Key.FullName}: Targeted+Broadcast pair '*{namePair.Key}' "
                                + "(Targeted and Broadcast variants of the same base name differ only by "
                                + "message kind; consolidate via "
                                + "[ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.KindsWithComponentTarget))])"
                        );
                    }
                }
            }

            Assert.That(
                offenders,
                Is.Empty,
                "Found kind-duplicated [UnityTest]/[Test] methods (Untargeted/Targeted/Broadcast triplets "
                    + "or Targeted+Broadcast pairs of the same base name in the same fixture) that are not "
                    + "parameterized by MessageScenario. Consolidate triplets via "
                    + "[ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))] and "
                    + "Targeted+Broadcast pairs via "
                    + "[ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.KindsWithComponentTarget))]. "
                    + "See .llm/skills/testing/tests-must-be-parameterized-by-message-kind.md. Offenders:\n"
                    + string.Join("\n", offenders)
            );
        }

        private static bool HasMessageScenarioParameter(MethodInfo method)
        {
            return method
                .GetParameters()
                .Any(parameter => parameter.ParameterType == typeof(MessageScenario));
        }

        /// <summary>
        /// Pins the allocation-coverage contract: every value of
        /// <see cref="MessageKind"/> must be represented in the scenario source
        /// that covers the full dispatch surface. Adding a new kind without
        /// updating the source will trip this guard. The contract pin lives in
        /// the <c>allocation-coverage-required-for-dispatch</c> skill.
        /// </summary>
        [Test]
        public void EveryEmitPathHasAllocationCoverage()
        {
            HashSet<MessageKind> covered = new(
                MessageScenarios.AllKindsIncludingWithoutContext.Select(s => s.Kind)
            );
            List<string> missing = new();

            foreach (MessageKind kind in Enum.GetValues(typeof(MessageKind)))
            {
                if (!covered.Contains(kind))
                {
                    missing.Add(kind.ToString());
                }
            }

            Assert.That(
                missing,
                Is.Empty,
                "MessageScenarios.AllKindsIncludingWithoutContext must yield every MessageKind "
                    + "so full-surface parameterized tests cover all dispatch paths. Missing kinds: "
                    + string.Join(", ", missing)
                    + ". Actual kinds: "
                    + string.Join(", ", covered)
                    + ". See .llm/skills/testing/allocation-coverage-required-for-dispatch.md."
            );
        }

        /// <summary>
        /// Smoke-checks that <see cref="DxMessagingStaticState.Reset"/> - which
        /// every <c>MessagingTestBase.Setup</c> invokes - actually wipes the
        /// global bus counters back to zero. If a future change splits the
        /// reset into pieces this guard will fail before contaminated state
        /// leaks into the rest of the suite.
        /// </summary>
        [Test]
        public void DxMessagingStaticStateResetClearsBusCounts()
        {
            // Pollute the global bus so we can prove Reset clears it.
            MessageHandler pollutingHandler = new(
                new InstanceId(unchecked((int)0x517E0001)),
                MessageHandler.MessageBus
            )
            {
                active = true,
            };
            MessageRegistrationToken pollutingToken = MessageRegistrationToken.Create(
                pollutingHandler,
                MessageHandler.MessageBus
            );
            _ =
                pollutingToken.RegisterUntargeted<DxMessaging.Tests.Runtime.Scripts.Messages.SimpleUntargetedMessage>(
                    (
                        ref DxMessaging.Tests.Runtime.Scripts.Messages.SimpleUntargetedMessage _
                    ) => { }
                );
            pollutingToken.Enable();

            try
            {
                DxMessagingStaticState.Reset();

                IMessageBus bus = MessageHandler.MessageBus;
                Assert.IsNotNull(bus, "MessageHandler.MessageBus must not be null after Reset.");
                Assert.Zero(bus.RegisteredUntargeted, "Setup must leave Untargeted count at zero.");
                Assert.Zero(bus.RegisteredTargeted, "Setup must leave Targeted count at zero.");
                Assert.Zero(bus.RegisteredBroadcast, "Setup must leave Broadcast count at zero.");
            }
            finally
            {
                // Best-effort cleanup; Reset above already cleared the bus,
                // but disposing the token is a no-op on a fresh state.
                pollutingToken.Disable();
                DxMessagingStaticState.Reset();
            }
        }

        /// <summary>
        /// Tightens <see cref="TripletEmitTestsUseScenarioParameterization"/>:
        /// once a fixture adopts <see cref="MessageScenario"/>-parameterized
        /// tests (any <c>[UnityTest]</c> in the fixture takes a
        /// <see cref="MessageScenario"/> via <c>[ValueSource]</c>), every
        /// kind-named <c>[UnityTest]</c> method in the SAME fixture must
        /// also be parameterized. Mixing parameterized and per-kind methods
        /// in the same fixture is almost always an oversight - either the
        /// kind-named methods predate the consolidation and were missed, or
        /// they exercise materially different mechanics and should move
        /// into a <c>*Specific*</c> fixture.
        /// </summary>
        /// <remarks>
        /// Fixtures with materially asymmetric overload assertions (e.g.
        /// <c>NominalTests</c>'s <c>TargetedWithoutTargeting</c> tests that
        /// have no Untargeted counterpart) are exempted via the
        /// <c>allowedMixedFixtures</c> set below; each entry includes a
        /// short justification. Removing an exemption later is the
        /// consolidation milestone.
        /// </remarks>
        [Test]
        public void MixedParameterizationAndKindNamedTestsInSameFixture()
        {
            string[] kindTokens = { "Untargeted", "Targeted", "Broadcast" };
            Dictionary<Type, List<MethodInfo>> kindNamedByFixture = new();
            HashSet<Type> fixturesWithParameterization = new();

            foreach (MethodInfo method in GetDxMessagingTestMethods())
            {
                if (!HasAttribute<UnityTestAttribute>(method))
                {
                    continue;
                }

                Type fixture = method.DeclaringType;
                if (fixture == null)
                {
                    continue;
                }

                if (
                    fixture.Name.IndexOf("Specific", StringComparison.Ordinal) >= 0
                    || fixture.Name.IndexOf("Equivalence", StringComparison.Ordinal) >= 0
                    || fixture.Name.IndexOf("Prefreeze", StringComparison.Ordinal) >= 0
                )
                {
                    continue;
                }

                if (HasMessageScenarioParameter(method))
                {
                    fixturesWithParameterization.Add(fixture);
                    continue;
                }

                bool nameHasKindToken = false;
                foreach (string token in kindTokens)
                {
                    if (method.Name.IndexOf(token, StringComparison.Ordinal) >= 0)
                    {
                        nameHasKindToken = true;
                        break;
                    }
                }

                if (!nameHasKindToken)
                {
                    continue;
                }

                if (!kindNamedByFixture.TryGetValue(fixture, out List<MethodInfo> bucket))
                {
                    bucket = new List<MethodInfo>();
                    kindNamedByFixture[fixture] = bucket;
                }

                bucket.Add(method);
            }

            // Allowlist: fixtures known to mix kind-named and parameterized
            // tests for justified reasons. Adding a NEW fixture should be
            // accompanied by a comment explaining why consolidation is unsafe.
            HashSet<string> allowedMixedFixtures = new(StringComparer.Ordinal)
            {
                // MutationDestructionTests pairs a parameterized
                // DestroyOtherListenerDoesNotRun with kind-asymmetric
                // overloads (TargetedComponent / TargetedWithoutTargeting /
                // BroadcastComponent / BroadcastWithoutSource) that have
                // no Untargeted counterpart. Consolidating would erase the
                // overload-specific assertions.
                // Remove when the Untargeted bus grows a Component or
                // *WithoutTargeting analogue (so every overload has a
                // counterpart and the asymmetric methods can collapse).
                "DxMessaging.Tests.Runtime.Core.MutationDestructionTests",
                // MutationDuringEmissionTests pins a wide matrix of mutation
                // x emission permutations. Several methods cover the
                // *WithoutTargeting / *WithoutSource overloads which are
                // kind-asymmetric (only Targeted and Broadcast have
                // without-* variants).
                // Remove when the Untargeted bus exposes equivalent
                // *WithoutTargeting / *WithoutSource overloads (so every
                // mutation entry has a corresponding Untargeted variant).
                "DxMessaging.Tests.Runtime.Core.MutationDuringEmissionTests",
                // OrderingManyRegistrationsTests has the same shape: the
                // *WithoutTargeting and per-kind PostProcessor variants
                // are kind-asymmetric.
                // Remove when action post-processors and *WithoutTargeting
                // are unified across kinds (matching the resolution
                // condition above for the same fixture's triplet entry).
                "DxMessaging.Tests.Runtime.Core.OrderingManyRegistrationsTests",
            };

            List<string> offenders = new();
            foreach (KeyValuePair<Type, List<MethodInfo>> pair in kindNamedByFixture)
            {
                if (!fixturesWithParameterization.Contains(pair.Key))
                {
                    continue;
                }

                if (allowedMixedFixtures.Contains(pair.Key.FullName))
                {
                    continue;
                }

                foreach (MethodInfo method in pair.Value)
                {
                    offenders.Add(FormatMethod(method));
                }
            }

            Assert.That(
                offenders,
                Is.Empty,
                "Fixtures that adopt MessageScenario parameterization must consolidate ALL kind-named "
                    + "[UnityTest] methods, OR be added to the allowlist with a justification comment. "
                    + "Offenders:\n"
                    + string.Join("\n", offenders)
            );
        }

        /// <summary>
        /// Pins the cleanup pattern: every fixture inheriting from
        /// <c>MessagingTestBase</c> must rely on the <c>_spawned</c> list
        /// for GameObject teardown rather than calling
        /// <c>UnityEngine.Object.Destroy(go)</c> directly without registering
        /// the object with <c>_spawned</c> first. This is enforced by
        /// scanning fixture sources for <c>Object.Destroy(</c>/<c>Destroy(</c>
        /// occurrences that are not preceded by a <c>_spawned.Add</c>; we
        /// only flag occurrences in test fixtures (not in the base class
        /// itself, which legitimately calls Destroy as part of the cleanup
        /// loop). Source-text heuristic: any <c>_spawned.Add(</c> token in
        /// the file is treated as evidence the fixture follows the
        /// convention.
        /// </summary>
        [Test]
        public void FixturesUsingMessagingTestBaseUseSpawnedCleanupPattern()
        {
            // Scan every loaded test assembly (Runtime + Benchmarks +
            // siblings) so the rule applies uniformly across the test
            // surface, not just to the assembly that hosts this fixture.
            HashSet<Type> messagingBaseFixtures = new();
            foreach (Assembly testAssembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!IsDxMessagingTestAssembly(testAssembly))
                {
                    continue;
                }

                Type[] assemblyTypes;
                try
                {
                    assemblyTypes = testAssembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    assemblyTypes = ex.Types.Where(t => t != null).ToArray();
                }

                foreach (Type type in assemblyTypes)
                {
                    if (type == null)
                    {
                        continue;
                    }

                    if (type.Namespace == null || !IsDxMessagingTestNamespace(type.Namespace))
                    {
                        continue;
                    }

                    if (type.IsAbstract)
                    {
                        continue;
                    }

                    if (
                        !typeof(DxMessaging.Tests.Runtime.Core.MessagingTestBase).IsAssignableFrom(
                            type
                        )
                    )
                    {
                        continue;
                    }

                    // Skip fixtures that have NO test methods (likely helper
                    // scaffolding); the rule applies to fixtures that exercise
                    // the bus.
                    bool hasTest = false;
                    foreach (
                        MethodInfo method in type.GetMethods(
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                        )
                    )
                    {
                        if (
                            HasAttribute<TestAttribute>(method)
                            || HasAttribute<UnityTestAttribute>(method)
                        )
                        {
                            hasTest = true;
                            break;
                        }
                    }

                    if (!hasTest)
                    {
                        continue;
                    }

                    messagingBaseFixtures.Add(type);
                }
            }

            // Source-text approximation: walk the test source roots and
            // for each fixture pair its source file, then flag fixtures
            // whose source spawns GameObjects (`new GameObject(`) but
            // never calls `_spawned.Add(`. Files that cannot be located
            // on disk are simply not classified (they fall into the
            // "uncovered" bucket and do not fail the test).
            List<string> sourceRoots = ResolveTestSourceRootsFallback();
            Dictionary<string, string> fixtureToSource = new(StringComparer.Ordinal);

            foreach (string root in sourceRoots)
            {
                if (!System.IO.Directory.Exists(root))
                {
                    continue;
                }

                foreach (
                    string file in System.IO.Directory.EnumerateFiles(
                        root,
                        "*.cs",
                        System.IO.SearchOption.AllDirectories
                    )
                )
                {
                    string text;
                    try
                    {
                        text = System.IO.File.ReadAllText(file);
                    }
                    catch (System.IO.IOException)
                    {
                        continue;
                    }

                    foreach (Type fixture in messagingBaseFixtures)
                    {
                        // Match by simple type name; the file name pattern
                        // mirrors the fixture name across the test tree.
                        if (
                            !string.Equals(
                                System.IO.Path.GetFileNameWithoutExtension(file),
                                fixture.Name,
                                StringComparison.Ordinal
                            )
                        )
                        {
                            continue;
                        }

                        fixtureToSource[fixture.FullName] = text;
                        break;
                    }
                }
            }

            List<string> offenders = new();
            foreach (Type fixture in messagingBaseFixtures)
            {
                if (!fixtureToSource.TryGetValue(fixture.FullName, out string text))
                {
                    continue;
                }

                // Match BOTH classic `new GameObject(...)` AND C# 9 target-typed
                // `GameObject identifier = new(...)` patterns. The latter became
                // common when fixtures adopted target-typed instantiation; without
                // this alternate the check would silently miss spawn calls written
                // in the new style. Roslyn would be more robust but the runtime
                // tests asmdef does not reference the syntax APIs, so the regex
                // form is the pragmatic option.
                bool spawnsGameObjects =
                    text.IndexOf("new GameObject(", StringComparison.Ordinal) >= 0
                    || GameObjectTargetTypedNewPattern.IsMatch(text);
                bool tracksWithSpawned =
                    text.IndexOf("_spawned.Add", StringComparison.Ordinal) >= 0;

                if (spawnsGameObjects && !tracksWithSpawned)
                {
                    offenders.Add(fixture.FullName);
                }
            }

            Assert.That(
                offenders,
                Is.Empty,
                "Fixtures inheriting MessagingTestBase that spawn GameObjects must register them via _spawned.Add(...) "
                    + "for proper teardown. Offenders:\n  "
                    + string.Join("\n  ", offenders)
            );
        }

        /// <summary>
        /// Bans real-time waits anywhere under the <c>Tests/</c> source tree. The
        /// suite measures deterministic frame-based and condition-based progress,
        /// not wall-clock time, so blocking sleeps, awaited delays, the
        /// real-seconds coroutine yields, and time-scale manipulation would trade
        /// determinism for flake and slowness (and the common "lower the time
        /// scale" tip does not even apply when there is no real time to compress).
        /// </summary>
        /// <remarks>
        /// Source-text scan over both test subtrees (EditMode + Runtime). The
        /// banned token list is assembled by string concatenation so this
        /// scanner's own source never contains a banned token in its matchable
        /// form and cannot flag itself; keep any future prose in this file the
        /// same way. Files that cannot be located on disk (for example in a
        /// standalone player run) leave the scan vacuously satisfied, matching the
        /// other source-scan contract tests.
        /// </remarks>
        [Test]
        public void TestSourcesAvoidRealTimeWaitAntiPatterns()
        {
            string[] bannedTokens =
            {
                "Thread" + ".Sleep(",
                "Task" + ".Delay(",
                "WaitForSeconds" + "(",
                "WaitForSecondsRealtime" + "(",
                "Time" + ".timeScale",
            };

            List<string> roots = ResolveTestsTreeRootsFallback();
            HashSet<string> scannedFiles = new(StringComparer.OrdinalIgnoreCase);
            List<string> offenders = new();

            foreach (string root in roots)
            {
                if (!System.IO.Directory.Exists(root))
                {
                    continue;
                }

                foreach (
                    string file in System.IO.Directory.EnumerateFiles(
                        root,
                        "*.cs",
                        System.IO.SearchOption.AllDirectories
                    )
                )
                {
                    string normalized = System.IO.Path.GetFullPath(file);
                    if (!scannedFiles.Add(normalized))
                    {
                        continue;
                    }

                    string text;
                    try
                    {
                        text = System.IO.File.ReadAllText(file);
                    }
                    catch (System.IO.IOException)
                    {
                        continue;
                    }

                    foreach (string token in bannedTokens)
                    {
                        // Report EVERY occurrence (not just the first) so a fix
                        // sees all offending lines in one pass.
                        int searchFrom = 0;
                        while (true)
                        {
                            int index = text.IndexOf(token, searchFrom, StringComparison.Ordinal);
                            if (index < 0)
                            {
                                break;
                            }

                            int line = 1;
                            for (int i = 0; i < index; ++i)
                            {
                                if (text[i] == '\n')
                                {
                                    ++line;
                                }
                            }

                            offenders.Add(
                                $"{System.IO.Path.GetFileName(file)}:{line} uses '{token}'"
                            );
                            searchFrom = index + token.Length;
                        }
                    }
                }
            }

            Assert.That(
                offenders,
                Is.Empty,
                "Tests must wait on deterministic frame or condition progress, never real time. "
                    + "Replace the offending blocking-sleep / awaited-delay / real-seconds-yield / "
                    + "time-scale usage with a frame budget or a synchronous condition check "
                    + "(see .llm/skills/testing/fast-unity-tests.md):\n  "
                    + string.Join("\n  ", offenders)
            );
        }

        /// <summary>
        /// Pins that no namespace contains more than one
        /// <c>[SetUpFixture]</c>. NUnit applies a SetUpFixture's hooks to
        /// every test in the namespace and its sub-namespaces; multiple
        /// fixtures in the same namespace produce undefined ordering and
        /// can race each other's <c>[OneTimeSetUp]</c> /
        /// <c>[OneTimeTearDown]</c> bodies.
        /// </summary>
        [Test]
        public void AtMostOneSetUpFixturePerNamespace()
        {
            Dictionary<string, List<string>> fixturesByNamespace = new(StringComparer.Ordinal);

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!IsDxMessagingTestAssembly(assembly))
                {
                    continue;
                }

                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).ToArray();
                }

                foreach (Type type in types)
                {
                    if (type == null || type.Namespace == null)
                    {
                        continue;
                    }

                    if (!IsDxMessagingTestNamespace(type.Namespace))
                    {
                        continue;
                    }

                    if (
                        type.GetCustomAttributes(
                            typeof(SetUpFixtureAttribute),
                            inherit: false
                        ).Length == 0
                    )
                    {
                        continue;
                    }

                    if (!fixturesByNamespace.TryGetValue(type.Namespace, out List<string> bucket))
                    {
                        bucket = new List<string>();
                        fixturesByNamespace[type.Namespace] = bucket;
                    }

                    bucket.Add(type.FullName);
                }
            }

            List<string> offenders = new();
            foreach (KeyValuePair<string, List<string>> pair in fixturesByNamespace)
            {
                if (pair.Value.Count > 1)
                {
                    offenders.Add(
                        $"{pair.Key}: {pair.Value.Count} SetUpFixture types ("
                            + string.Join(", ", pair.Value)
                            + ")"
                    );
                }
            }

            Assert.That(
                offenders,
                Is.Empty,
                "Namespaces with multiple [SetUpFixture] declarations:\n  "
                    + string.Join("\n  ", offenders)
            );
        }

        /// <summary>
        /// Pins that <see cref="MessageScenario"/> parameter sources on
        /// <c>[UnityTest]</c> methods always reference one of the canonical
        /// scenario sources (<see cref="MessageScenarios.AllKinds"/>,
        /// <see cref="MessageScenarios.WithAndWithoutInterceptor"/>, etc.).
        /// </summary>
        [Test]
        public void MessageScenarioParametersUseValueSource()
        {
            List<string> offenders = new();

            foreach (MethodInfo method in GetDxMessagingTestMethods())
            {
                if (
                    !HasAttribute<UnityTestAttribute>(method)
                    && !HasAttribute<TestAttribute>(method)
                )
                {
                    continue;
                }

                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    if (parameter.ParameterType != typeof(MessageScenario))
                    {
                        continue;
                    }

                    bool hasValueSource =
                        parameter
                            .GetCustomAttributes(
                                typeof(NUnit.Framework.ValueSourceAttribute),
                                inherit: false
                            )
                            .Length > 0;
                    if (hasValueSource)
                    {
                        continue;
                    }

                    offenders.Add(FormatMethod(method) + " (parameter " + parameter.Name + ")");
                }
            }

            Assert.That(
                offenders,
                Is.Empty,
                "Found MessageScenario parameters without [ValueSource]. Use "
                    + "[ValueSource(typeof(MessageScenarios), nameof(MessageScenarios.AllKinds))] "
                    + "(or another canonical source) to drive parameterized tests:\n"
                    + string.Join("\n", offenders)
            );
        }

        private static List<string> ResolveTestSourceRootsFallback()
        {
            return ResolveTestSubtreeRootsFallback(System.IO.Path.Combine("Tests", "Runtime"));
        }

        /// <summary>
        /// Resolves the whole <c>Tests/</c> source tree (both the EditMode and
        /// the Runtime subtrees) on disk, for scans that must cover every test
        /// assembly rather than just the runtime fixtures.
        /// </summary>
        private static List<string> ResolveTestsTreeRootsFallback()
        {
            return ResolveTestSubtreeRootsFallback("Tests");
        }

        /// <summary>
        /// Best-effort on-disk location of a subdirectory of the package's
        /// <c>Tests/</c> tree. Returns every candidate root that exists; an empty
        /// list (source not on disk, for example in a standalone player run)
        /// leaves a caller's scan vacuously satisfied, matching the rest of the
        /// source-scan contract tests.
        /// </summary>
        private static List<string> ResolveTestSubtreeRootsFallback(string subtree)
        {
            List<string> roots = new List<string>();
            string[] candidates =
            {
                System.IO.Path.Combine(
                    UnityEngine.Application.dataPath,
                    "..",
                    "Packages",
                    "com.wallstop-studios.dxmessaging",
                    subtree
                ),
                System.IO.Path.Combine(UnityEngine.Application.dataPath, "..", subtree),
                System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), subtree),
            };

            foreach (string candidate in candidates)
            {
                string full = System.IO.Path.GetFullPath(candidate);
                if (System.IO.Directory.Exists(full))
                {
                    roots.Add(full);
                }
            }

            return roots;
        }

        private static IEnumerable<MethodInfo> FindMethods(Func<MethodInfo, bool> predicate)
        {
            return GetDxMessagingTestMethods().Where(predicate);
        }

        /// <summary>
        /// Enumerates every test method (any method with <c>[Test]</c>,
        /// <c>[UnityTest]</c>, <c>[TestCase]</c>, or <c>[TestCaseSource]</c>) in
        /// every loaded assembly whose name begins with
        /// <c>WallstopStudios.DxMessaging.Tests</c>. This intentionally covers
        /// the runtime test asmdef AND the Benchmarks asmdef (and any future
        /// sibling test assemblies), so contract tests apply uniformly across
        /// the test surface and not just the assembly that hosts this fixture.
        /// </summary>
        private static IEnumerable<MethodInfo> GetDxMessagingTestMethods()
        {
            BindingFlags methodFlags =
                BindingFlags.Instance
                | BindingFlags.Static
                | BindingFlags.Public
                | BindingFlags.NonPublic;

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!IsDxMessagingTestAssembly(assembly))
                {
                    continue;
                }

                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).ToArray();
                }

                foreach (Type type in types)
                {
                    if (type == null)
                    {
                        continue;
                    }

                    if (type.Namespace == null || !IsDxMessagingTestNamespace(type.Namespace))
                    {
                        continue;
                    }

                    foreach (MethodInfo method in type.GetMethods(methodFlags))
                    {
                        if (method.IsSpecialName)
                        {
                            continue;
                        }

                        bool isTestMethod =
                            HasAttribute<TestAttribute>(method)
                            || HasAttribute<UnityTestAttribute>(method)
                            || HasAttribute<TestCaseAttribute>(method)
                            || HasAttribute<TestCaseSourceAttribute>(method);
                        // ValueSource is parameter data only and is always paired with a test-defining attribute.

                        if (isTestMethod)
                        {
                            yield return method;
                        }
                    }
                }
            }
        }

        private static bool HasAttribute<TAttribute>(MemberInfo method)
            where TAttribute : Attribute
        {
            return method.GetCustomAttributes(typeof(TAttribute), inherit: false).Length > 0;
        }

        private static string FormatMethod(MethodInfo method)
        {
            Type declaringType = method.DeclaringType;
            string declaringTypeName = declaringType == null ? "<unknown>" : declaringType.FullName;
            return $"{declaringTypeName}.{method.Name} returns {method.ReturnType.FullName}";
        }
    }
}

#endif
