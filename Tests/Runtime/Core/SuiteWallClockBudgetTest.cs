#if UNITY_2021_3_OR_NEWER
[assembly: DxMessaging.Tests.Runtime.NoteGatedCategoryAction]

namespace DxMessaging.Tests.Runtime
{
    using System;
    using System.Diagnostics;
    using NUnit.Framework;
    using NUnit.Framework.Interfaces;

    /// <summary>
    /// Suite-level wall-clock budget covering every test under the
    /// <c>DxMessaging.Tests.Runtime</c> namespace and its sub-namespaces
    /// (Core, Integrations, Unity) within the runtime test assembly. The
    /// default Unity Edit + Play mode test run is supposed to finish in
    /// under 60 seconds once the <c>Stress</c>, <c>Allocation</c>,
    /// <c>Performance</c>, <c>MemoryReclaim</c>, and <c>UnityRuntime</c>
    /// categories are filtered out. This setup fixture captures a timestamp at suite start (via
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
    /// Gated-category detection: an assembly-scoped <see cref="NoteGatedCategoryAction"/>
    /// runs <see cref="ITestAction.BeforeTest"/> for every test in the run.
    /// The action reads the test's NUnit categories (from
    /// <see cref="ITest.Properties"/> with the <c>"Category"</c> key, as
    /// defined by NUnit's internal <c>PropertyNames</c> table) and forwards
    /// each one to <see cref="NoteGatedCategoryObserved"/>.
    /// If any test in the session is in the gated set
    /// (<c>Stress</c>, <c>Performance</c>, <c>Allocation</c>,
    /// <c>MemoryReclaim</c>, or <c>UnityRuntime</c>), the wall-clock budget assertion is skipped
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
            "UnityRuntime",
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

            // Sanity: dump the elapsed time so CI logs make the budget
            // proximity visible without a failure. The Unity version is
            // included because the hard budget is selected per version (the
            // 2021.x runner gets a wider ceiling); seeing both together makes
            // a near-budget run easy to triage.
            UnityEngine.Debug.Log(
                $"DxMessaging suite wall clock: {elapsed.TotalSeconds:0.00}s "
                    + $"(soft budget {SoftBudget.TotalSeconds:0.0}s, hard budget {HardBudget.TotalSeconds:0.0}s "
                    + $"for Unity {UnityEngine.Application.unityVersion})."
            );

            if (_gatedCategoryDetected)
            {
                UnityEngine.Debug.Log(
                    "Skipping default-suite wall-clock assertion: a Stress/Performance/Allocation/MemoryReclaim/UnityRuntime "
                        + "test was observed in this run."
                );
                return;
            }

            if (elapsed > HardBudget)
            {
                Assert.Fail(
                    $"DxMessaging default-suite wall-clock budget exceeded: {elapsed.TotalSeconds:0.00}s "
                        + $"over the {HardBudget.TotalSeconds:0.0}s hard budget for Unity "
                        + $"{UnityEngine.Application.unityVersion}. This per-version ceiling already "
                        + "absorbs runner-speed variance (2021.x gets a wider bound), so a breach this "
                        + "large means a genuine regression, not slowness. Reduce iteration counts or move "
                        + "offending tests behind a gated category "
                        + "(Stress/Performance/Allocation/MemoryReclaim/UnityRuntime)."
                );
            }
            else if (elapsed > SoftBudget)
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
        /// <see cref="NoteGatedCategoryAction.BeforeTest"/> for every test
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
    /// Assembly-scoped <see cref="ITestAction"/> that fires before every
    /// test runs, scans the test's NUnit categories, and forwards each one
    /// to <see cref="SuiteWallClockBudgetTest.NoteGatedCategoryObserved"/>.
    /// Combined with the assembly-level attribute application (see the
    /// <c>[assembly: NoteGatedCategoryAction]</c> declaration at the top
    /// of this file) this covers every test in every fixture in the
    /// assembly without requiring a base class.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
    public sealed class NoteGatedCategoryAction : Attribute, ITestAction
    {
        public ActionTargets Targets => ActionTargets.Test;

        public void BeforeTest(ITest test)
        {
            if (test == null)
            {
                return;
            }

            // ITest.Properties is a flat IPropertyBag; categories live under
            // the well-known "Category" key (NUnit 3.x's PropertyNames.Category
            // resolves to the same literal). Each test may have multiple
            // categories, and NUnit applies fixture-level [Category]
            // attributes to each child test automatically, so a class-level
            // [Category("Allocation")] also shows up here.
            const string CategoryPropertyName = "Category";
            System.Collections.IList categories = test.Properties[CategoryPropertyName];
            if (categories == null)
            {
                return;
            }

            for (int i = 0; i < categories.Count; ++i)
            {
                SuiteWallClockBudgetTest.NoteGatedCategoryObserved(categories[i] as string);
            }
        }

        public void AfterTest(ITest test) { }
    }
}
#endif
