#if UNITY_2021_3_OR_NEWER
[assembly: DxMessaging.Tests.Runtime.NoteGatedCategoryAttribute]
[assembly: UnityEngine.TestRunner.TestRunCallback(
    typeof(DxMessaging.Tests.Runtime.NoteGatedCategoryAttribute)
)]

namespace DxMessaging.Tests.Runtime
{
    using System;
    using System.Diagnostics;
    using System.Globalization;
    using NUnit.Framework;
    using NUnit.Framework.Interfaces;
    using UnityEngine.TestRunner;

    /// <summary>
    /// Suite-level wall-clock budget covering every test under the
    /// <c>DxMessaging.Tests.Runtime</c> namespace and its sub-namespaces
    /// (Core, Integrations, Unity) within the runtime test assembly. The
    /// default Unity Edit + Play mode test run is supposed to finish in
    /// under 60 seconds once the <c>Stress</c>, <c>Allocation</c>,
    /// <c>Performance</c>, and <c>MemoryReclaim</c> categories are filtered out.
    /// The <c>UnityRuntime</c> lifecycle cases remain within this budget in
    /// PlayMode and standalone runs. This fixture captures a timestamp at suite start (via
    /// <see cref="OneTimeSetUpAttribute"/>) and asserts the elapsed wall
    /// clock at suite end is below a soft and hard budget.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cross-assembly note: the Benchmarks tests live in a separate test
    /// assembly (<c>WallstopStudios.DxMessaging.Tests.00.Runtime.Benchmarks</c>)
    /// and run independently; this fixture's hooks do not fire for those
    /// tests because NUnit applies <see cref="SetUpFixtureAttribute"/> per
    /// assembly. Benchmarks tests are gated behind
    /// <c>[Category("Allocation")]</c>/<c>"Performance"</c> categories and
    /// have their own CI budgets.
    /// </para>
    /// <para>
    /// Implementation: NUnit's <see cref="SetUpFixtureAttribute"/> declares
    /// a fixture that runs at the assembly level rather than per-test
    /// fixture. Placing the fixture in the top-level
    /// <c>DxMessaging.Tests.Runtime</c> namespace causes NUnit to apply the
    /// <see cref="OneTimeSetUpAttribute"/> / <see cref="OneTimeTearDownAttribute"/>
    /// hooks across every fixture in that namespace AND every sub-namespace
    /// (Core, Integrations, Unity) within the runtime test assembly. The
    /// Unity Test Runner honours both attributes inside its NUnit-derived
    /// runner.
    /// </para>
    /// <para>
    /// Gated-category detection: <see cref="NoteGatedCategoryAttribute"/> observes executed
    /// leaves through Unity's test-run callback and NUnit's assembly action.
    /// The action reads the test's NUnit categories (from
    /// <see cref="ITest.Properties"/> with the <c>"Category"</c> key, as
    /// defined by NUnit's internal <c>PropertyNames</c> table) and forwards
    /// each one to <see cref="NoteGatedCategoryObserved"/>.
    /// If any test in the session is in the gated set
    /// (<c>Stress</c>, <c>Performance</c>, <c>Allocation</c>,
    /// or <c>MemoryReclaim</c>), the wall-clock budget assertion is skipped
    /// because the gated suites have their own CI-side timing budgets.
    /// </para>
    /// <para>
    /// Mechanism alternative: a CI-side timer (e.g. measuring
    /// <c>vstest.console</c> wall clock) is a perfectly fine fallback. We
    /// keep this in-NUnit fixture because it is self-contained, runs
    /// alongside the tests, and reports failure with the same error
    /// formatting the rest of the suite uses.
    /// </para>
    /// </remarks>
    [SetUpFixture]
    public sealed class SuiteWallClockBudgetTest
    {
        /// <summary>
        /// Soft budget: the default suite is expected to complete under
        /// this duration. A breach triggers a warning but does not fail
        /// the suite.
        /// </summary>
        public static readonly TimeSpan SoftBudget = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Hard budget applied on Unity 2021.x runners. Deliberately wider
        /// than <see cref="DefaultHardBudget"/> because the 2021.3 PlayMode
        /// runner is the slowest supported environment; the extra headroom
        /// absorbs runner-speed variance without hiding an algorithmic
        /// regression (which the unchanged 60s soft warning still flags).
        /// </summary>
        private static readonly TimeSpan Unity2021HardBudget = TimeSpan.FromSeconds(300);

        /// <summary>
        /// Hard budget applied on Unity 2022.3 / 6000.x and newer runners,
        /// which complete the default suite comfortably under this bound.
        /// </summary>
        private static readonly TimeSpan DefaultHardBudget = TimeSpan.FromSeconds(180);

        /// <summary>
        /// Hard budget: a default-suite breach above this threshold fails
        /// the suite (so a regression is unmissable). The budget is selected
        /// per Unity version because the wall clock is inherently
        /// runner-speed dependent: the Unity 2021.3 CI runner is measurably
        /// slower than 2022.3 / 6000.x for the SAME deterministic suite
        /// (1-of-697 timing flakes were observed only on 2021.3), so a single
        /// fixed ceiling would either flake on 2021 or be uselessly loose on
        /// the faster runners. We widen the 2021.x ceiling rather than mask a
        /// real regression: the soft 60s warning below still fires on every
        /// version to surface a creeping slowdown early, and the per-version
        /// hard ceiling only trips on an unmistakable blow-out. Declared after
        /// the two component budgets so the textual static-field
        /// initialization order has them assigned before
        /// <see cref="ResolveHardBudget"/> reads them at type-init time.
        /// </summary>
        public static readonly TimeSpan HardBudget = ResolveHardBudget();

        /// <summary>
        /// Selects the hard wall-clock budget for the current Unity version.
        /// Reads <see cref="Application.unityVersion"/> at runtime (rather than
        /// a compile-time <c>#if</c>) so both component budgets are always
        /// referenced - this keeps the selection correct across editor versions
        /// and avoids a conditionally-unused-field warning under any single
        /// define configuration.
        /// </summary>
        private static TimeSpan ResolveHardBudget()
        {
            string version = UnityEngine.Application.unityVersion;
            if (version != null && version.StartsWith("2021.", StringComparison.Ordinal))
            {
                return Unity2021HardBudget;
            }

            return DefaultHardBudget;
        }

        private static readonly string[] GatedCategories =
        {
            "Stress",
            "Performance",
            "Allocation",
            "MemoryReclaim",
        };

        private static Stopwatch _suiteTimer;
        private static volatile bool _gatedCategoryDetected;

        /// <summary>
        /// Captures the suite's start timestamp.
        /// </summary>
        [OneTimeSetUp]
        public void StartSuiteTimer()
        {
            _suiteTimer = Stopwatch.StartNew();
            _gatedCategoryDetected = false;
        }

        /// <summary>
        /// Stops the timer and asserts the elapsed wall clock is within
        /// the soft / hard budget. The assertion is skipped if any tracked
        /// test in the run belongs to a gated category (see remarks).
        /// </summary>
        [OneTimeTearDown]
        public void EndSuiteTimer()
        {
            if (_suiteTimer == null)
            {
                return;
            }

            _suiteTimer.Stop();
            TimeSpan elapsed = _suiteTimer.Elapsed;

            /*
                Sanity: dump the elapsed time so CI logs make the budget
                proximity visible without a failure. The Unity version is
                included because the hard budget is selected per version (the
                2021.x runner gets a wider ceiling); seeing both together makes
                a near-budget run easy to triage.
                Invariant culture on purpose: CI lifts this line into the job summary
                (issue #410), so the decimal separator must not follow the runner's
                locale. `scripts/unity/run-ci-tests.ps1` parses exactly this shape.
            */
            CultureInfo invariant = CultureInfo.InvariantCulture;
            UnityEngine.Debug.Log(
                $"DxMessaging suite wall clock: {elapsed.TotalSeconds.ToString("0.00", invariant)}s "
                    + $"(soft budget {SoftBudget.TotalSeconds.ToString("0.0", invariant)}s, "
                    + $"hard budget {HardBudget.TotalSeconds.ToString("0.0", invariant)}s "
                    + $"for Unity {UnityEngine.Application.unityVersion})."
            );

            if (_gatedCategoryDetected)
            {
                UnityEngine.Debug.Log(
                    "Skipping default-suite wall-clock assertion: a Stress/Performance/Allocation/MemoryReclaim "
                        + "test was observed in this run."
                );
                return;
            }

            if (HardBudget < elapsed)
            {
                Assert.Fail(
                    $"DxMessaging default-suite wall-clock budget exceeded: {elapsed.TotalSeconds:0.00}s "
                        + $"over the {HardBudget.TotalSeconds:0.0}s hard budget for Unity "
                        + $"{UnityEngine.Application.unityVersion}. This per-version ceiling already "
                        + "absorbs runner-speed variance (2021.x gets a wider bound), so a breach this "
                        + "large means a genuine regression, not slowness. Reduce iteration counts or move "
                        + "offending tests behind a gated category "
                        + "(Stress/Performance/Allocation/MemoryReclaim)."
                );
            }
            else if (SoftBudget < elapsed)
            {
                UnityEngine.Debug.LogWarning(
                    $"Default suite wall clock ({elapsed.TotalSeconds:0.00}s) exceeded the soft budget "
                        + $"({SoftBudget.TotalSeconds:0.0}s). The hard budget is "
                        + $"{HardBudget.TotalSeconds:0.0}s; reduce iteration counts before it breaches."
                );
            }
        }

        /// <summary>
        /// Marks the current run as containing a gated test. Called from
        /// <see cref="NoteGatedCategoryAttribute.BeforeTest"/> for every test
        /// before it runs, so the teardown assertion can short-circuit
        /// when a gated category is in scope.
        /// </summary>
        public static void NoteGatedCategoryObserved(string category)
        {
            if (string.IsNullOrEmpty(category))
            {
                return;
            }

            for (int i = 0; i < GatedCategories.Length; ++i)
            {
                if (string.Equals(GatedCategories[i], category, StringComparison.OrdinalIgnoreCase))
                {
                    _gatedCategoryDetected = true;
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Assembly-scoped observer that scans each executed leaf's selected ancestry
    /// for NUnit categories and forwards each one
    /// to <see cref="SuiteWallClockBudgetTest.NoteGatedCategoryObserved"/>.
    /// Unity's action command does not load assembly attributes, so its public
    /// test-run callback supplies the leaf notification. NUnit runners retain
    /// the assembly action. Suite notifications do not exempt a run because they
    /// can include unselected gated descendants and precede namespace setup.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
    [UnityEngine.Scripting.Preserve]
    public sealed class NoteGatedCategoryAttribute : Attribute, ITestAction, ITestRunCallback
    {
        internal static string LastStartedTestIdForTesting { get; private set; }

        public ActionTargets Targets => ActionTargets.Test;

        public void BeforeTest(ITest test)
        {
            /*
                ITest.Properties is a flat IPropertyBag; categories live under
                the well-known "Category" key (NUnit 3.x's PropertyNames.Category
                resolves to the same literal). Inherited fixture and parameterized-method
                categories stay on their parent nodes. Follow only the selected ancestry;
                categories on unselected siblings must not exempt the default suite.
            */
            const string CategoryPropertyName = "Category";
            for (ITest current = test; current != null; current = current.Parent)
            {
                System.Collections.IList categories = current.Properties[CategoryPropertyName];
                if (categories == null)
                {
                    continue;
                }

                for (int i = 0; i < categories.Count; ++i)
                {
                    SuiteWallClockBudgetTest.NoteGatedCategoryObserved(categories[i] as string);
                }
            }
        }

        public void AfterTest(ITest test) { }

        public void RunStarted(ITest testsToRun) { }

        public void RunFinished(ITestResult testResults) { }

        public void TestStarted(ITest test)
        {
            if (
                test != null
                && !test.IsSuite
                && test.TypeInfo?.Type?.Assembly == typeof(SuiteWallClockBudgetTest).Assembly
            )
            {
                LastStartedTestIdForTesting = test.Id;
                BeforeTest(test);
            }
        }

        public void TestFinished(ITestResult result) { }
    }
}
#endif
