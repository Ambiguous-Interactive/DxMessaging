#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System.Collections.Generic;
    using System.Reflection;
    using NUnit.Framework;
    using NUnit.Framework.Internal;
    using UnityEngine.TestRunner;

    public sealed class SuiteCategoryObservationTests
    {
        public static IEnumerable<TestCaseData> SelectedCategoryCases()
        {
            (string category, bool gated)[] categories =
            {
                ("Stress", true),
                ("Performance", true),
                ("Allocation", true),
                ("MemoryReclaim", true),
                ("sTrEsS", true),
                ("UnityRuntime", false),
                ("unityruntime", false),
                ("Stressful", false),
                ("", false),
                (null, false),
            };
            foreach ((string category, bool gated) in categories)
            {
                for (int level = 0; level < 4; ++level)
                {
                    foreach (bool previouslyObserved in new[] { false, true })
                    {
                        yield return new TestCaseData(
                            category,
                            level,
                            previouslyObserved,
                            previouslyObserved || gated
                        );
                    }
                }
            }
        }

        /// <summary>
        /// NUnit category bags contain only local values. Fixture and parameterized-method
        /// categories must be observed through the selected leaf's parent chain, while an
        /// unselected gated sibling must not exempt the default suite budget.
        /// </summary>
        [TestCaseSource(nameof(SelectedCategoryCases))]
        public void CategoryObservationUsesSelectedAncestors(
            string category,
            int level,
            bool previouslyObserved,
            bool expected
        )
        {
            ObserveSelectedAncestors(category, level, previouslyObserved, expected, false);
        }

        [TestCaseSource(nameof(SelectedCategoryCases))]
        public void UnityCallbackObservesSelectedAncestors(
            string category,
            int level,
            bool previouslyObserved,
            bool expected
        )
        {
            ObserveSelectedAncestors(category, level, previouslyObserved, expected, true);
        }

        [Test]
        public void AssemblyRegistersUnityCallback()
        {
            object[] callbacks = typeof(SuiteWallClockBudgetTest).Assembly.GetCustomAttributes(
                typeof(TestRunCallbackAttribute),
                false
            );
            Assert.That(
                callbacks.Length,
                Is.EqualTo(1),
                "The assembly must register its Unity observer."
            );
#if UNITY_EDITOR || UNITY_STANDALONE
            Assert.That(
                NoteGatedCategoryAttribute.LastStartedTestIdForTesting,
                Is.EqualTo(TestContext.CurrentContext.Test.ID),
                "Unity must notify the registered observer for this executed leaf."
            );
#endif
        }

        [TestCase(false)]
        [TestCase(true)]
        public void UnityCallbackIgnoresSuiteAndWholeRunNotifications(bool previouslyObserved)
        {
            TestSuite suite = new("unselected gated tree");
            suite.Properties.Add("Category", "MemoryReclaim");
            AssertObservation(
                suite,
                previouslyObserved,
                previouslyObserved,
                "suite notification",
                true
            );
        }

        [TestCase(false)]
        [TestCase(true)]
        public void UnityCallbackIgnoresTestsFromAnotherAssembly(bool previouslyObserved)
        {
            TestMethod foreign = new(new MethodWrapper(typeof(TestSuite), nameof(TestSuite.Add)));
            foreign.Properties.Add("Category", "MemoryReclaim");
            AssertObservation(
                foreign,
                previouslyObserved,
                previouslyObserved,
                $"foreign assembly, previous={previouslyObserved}",
                true
            );
        }

        private static void ObserveSelectedAncestors(
            string category,
            int level,
            bool previouslyObserved,
            bool expected,
            bool unityCallback
        )
        {
            TestMethod leaf = new(
                new MethodWrapper(
                    typeof(SuiteCategoryObservationTests),
                    nameof(CategoryObservationUsesSelectedAncestors)
                )
            );
            TestSuite method = new("parameterized method");
            TestSuite fixture = new("fixture");
            TestSuite assembly = new("assembly");
            method.Add(leaf);
            fixture.Add(method);
            assembly.Add(fixture);
            Test[] hierarchy = { leaf, method, fixture, assembly };
            if (category != null)
            {
                hierarchy[level].Properties.Add("Category", category);
            }
            TestSuite sibling = new("unselected gated sibling");
            sibling.Properties.Add("Category", "MemoryReclaim");
            assembly.Add(sibling);

            AssertObservation(
                leaf,
                previouslyObserved,
                expected,
                $"category={category ?? "<null>"}, level={level}, previous={previouslyObserved}, unityCallback={unityCallback}",
                unityCallback
            );
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NullTestPreservesPreviouslyObservedCategory(bool previouslyObserved)
        {
            AssertObservation(
                null,
                previouslyObserved,
                previouslyObserved,
                $"null test, previous={previouslyObserved}"
            );
        }

        [TestCase(false)]
        [TestCase(true)]
        public void UnityCallbackIgnoresNullTest(bool previouslyObserved)
        {
            AssertObservation(
                null,
                previouslyObserved,
                previouslyObserved,
                $"null callback test, previous={previouslyObserved}",
                true
            );
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MixedCategoryBagObservesEveryValue(bool gated)
        {
            TestSuite selected = new("mixed categories");
            selected.Properties.Add("Category", "UnityRuntime");
            selected.Properties.Add("Category", 7);
            selected.Properties.Add("Category", gated ? "Allocation" : "Stressful");
            AssertObservation(selected, false, gated, $"mixed category bag, gated={gated}");
        }

        private static void AssertObservation(
            NUnit.Framework.Interfaces.ITest selected,
            bool previouslyObserved,
            bool expected,
            string context,
            bool unityCallback = false
        )
        {
            FieldInfo detected = typeof(SuiteWallClockBudgetTest).GetField(
                "_gatedCategoryDetected",
                BindingFlags.NonPublic | BindingFlags.Static
            );
            Assert.That(
                detected,
                Is.Not.Null,
                "[{0}] The suite observation field must exist.",
                context
            );
            bool original = (bool)detected.GetValue(null);
            try
            {
                detected.SetValue(null, previouslyObserved);
                NoteGatedCategoryAttribute observer = new();
                if (unityCallback)
                {
                    ITestRunCallback callback = (object)observer as ITestRunCallback;
                    Assert.That(
                        callback,
                        Is.Not.Null,
                        "[{0}] The observer implements Unity callbacks.",
                        context
                    );
                    callback.RunStarted(selected);
                    Assert.That(
                        (bool)detected.GetValue(null),
                        Is.EqualTo(previouslyObserved),
                        "[{0}] Whole-run discovery must preserve the previous gate state.",
                        context
                    );
                    callback.TestStarted(selected);
                }
                else
                {
                    observer.BeforeTest(selected);
                }
                Assert.That(
                    (bool)detected.GetValue(null),
                    Is.EqualTo(expected),
                    "[{0}] Selected ancestry must produce gated={1} without observing siblings.",
                    context,
                    expected
                );
            }
            finally
            {
                detected.SetValue(null, original);
            }
        }
    }
}
#endif
