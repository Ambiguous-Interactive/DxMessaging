#if UNITY_EDITOR
namespace DxMessaging.Tests.Editor
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using DxMessaging.Core;
    using NUnit.Framework;

    /// <summary>
    /// Locks in the inline-first-action storage of the token's private
    /// <c>PendingDeregistration</c> (the per-handle de-registration holder).
    /// <para>
    /// The pre-change form stored every handle's de-registration Action(s) in an
    /// eagerly-allocated <see cref="List{Action}"/>, costing two managed allocations on
    /// EVERY registration (the list object plus its backing array) to hold what is almost
    /// always a single Action. The current form stores that single head Action inline and
    /// spills to a lazily-allocated overflow list only on a rare second de-registration
    /// for the same handle (a re-entrant retarget-recovery replay can stage one beyond the
    /// rollback baseline). Measured cold-total A/B (same warm editor): Untargeted
    /// 13.29 -&gt; 11.29 allocs/registration, a clean -2.00; Targeted/Broadcast -2.22/-2.47.
    /// </para>
    /// <para>
    /// These guards are deterministic and backend-independent (no allocation probe), so
    /// they run in the EditMode correctness leg on every PR -- not only in the dedicated
    /// Allocation scope. The structural test pins the allocation reduction; the behavioral
    /// tests pin the rollback-baseline (<c>startIndex</c>), partial-failure-retryable, and
    /// head-promotion semantics of the reworked <c>InvokeFrom</c>, which the eager-list
    /// form got for free from <c>List&lt;Action&gt;.RemoveAt</c> but the inline form must
    /// reproduce. The multi-de-registration-per-handle path is hard to drive through the
    /// public token API, so it is covered here directly.
    /// </para>
    /// </summary>
    [TestFixture]
    public sealed class PendingDeregistrationStorageTests
    {
        private static readonly Type PendingType = typeof(MessageRegistrationToken).GetNestedType(
            "PendingDeregistration",
            BindingFlags.NonPublic
        );

        [Test]
        public void NestedPendingDeregistrationTypeResolves()
        {
            Assert.That(
                PendingType,
                Is.Not.Null,
                "MessageRegistrationToken.PendingDeregistration was renamed or removed; "
                    + "update PendingDeregistrationStorageTests."
            );
        }

        [Test]
        public void FreshInstanceDoesNotEagerlyAllocateOverflowList()
        {
            Pending pending = new Pending();
            Assert.That(
                pending.OverflowList,
                Is.Null,
                "A freshly-constructed PendingDeregistration must not allocate its overflow "
                    + "List<Action>. The eager-list form (a readonly List<Action> field "
                    + "initialized at construction) costs two managed allocations per "
                    + "registration; the head Action must be stored inline instead."
            );
            Assert.That(pending.Count, Is.EqualTo(0));
        }

        [Test]
        public void FirstDeregistrationUsesInlineHeadWithoutOverflowList()
        {
            Pending pending = new Pending();
            pending.Add(() => { });

            Assert.That(
                pending.OverflowList,
                Is.Null,
                "The first de-registration must be stored in the inline head, not in the "
                    + "overflow list, so the common single-de-registration case allocates no "
                    + "List<Action> or backing array."
            );
            Assert.That(pending.Count, Is.EqualTo(1));
        }

        [Test]
        public void SecondDeregistrationSpillsToOverflowList()
        {
            Pending pending = new Pending();
            pending.Add(() => { });
            pending.Add(() => { });

            Assert.That(
                pending.OverflowList,
                Is.Not.Null,
                "A second de-registration for the same handle must spill to the overflow " + "list."
            );
            Assert.That(pending.OverflowList.Count, Is.EqualTo(1));
            Assert.That(pending.Count, Is.EqualTo(2));
        }

        [Test]
        public void InvokeFromZeroInvokesEveryDeregistrationInInsertionOrderThenClears()
        {
            List<int> order = new List<int>();
            Pending pending = new Pending();
            pending.Add(() => order.Add(0));
            pending.Add(() => order.Add(1));
            pending.Add(() => order.Add(2));

            Exception exception = pending.InvokeFrom(0);

            Assert.That(exception, Is.Null);
            Assert.That(order, Is.EqualTo(new[] { 0, 1, 2 }), "Insertion order must be preserved.");
            Assert.That(
                pending.Count,
                Is.EqualTo(0),
                "Every successful de-registration is removed."
            );
        }

        [Test]
        public void InvokeFromBaselineIndexInvokesOnlyAddedDeregistrationsKeepingBaseline()
        {
            List<int> order = new List<int>();
            Pending pending = new Pending();
            pending.Add(() => order.Add(0)); // baseline (logical index 0)
            pending.Add(() => order.Add(1)); // baseline (logical index 1)
            pending.Add(() => order.Add(2)); // added (logical index 2)

            // The rollback path invokes only de-registrations added beyond the baseline
            // count, leaving the baseline ones intact and retryable.
            Exception exception = pending.InvokeFrom(2);

            Assert.That(exception, Is.Null);
            Assert.That(order, Is.EqualTo(new[] { 2 }), "Only the entry at/after startIndex runs.");
            Assert.That(pending.Count, Is.EqualTo(2), "The two baseline entries are retained.");
        }

        [Test]
        public void InvokeFromKeepsThrowingDeregistrationRetryableAndReturnsFirstException()
        {
            int firstInvocations = 0;
            int secondInvocations = 0;
            InvalidOperationException thrown = new InvalidOperationException("boom");
            Pending pending = new Pending();
            pending.Add(() =>
            {
                ++firstInvocations;
                throw thrown;
            });
            pending.Add(() => ++secondInvocations);

            Exception exception = pending.InvokeFrom(0);

            Assert.That(exception, Is.SameAs(thrown), "The first exception is surfaced.");
            Assert.That(firstInvocations, Is.EqualTo(1));
            Assert.That(secondInvocations, Is.EqualTo(1), "Later de-registrations still run.");
            Assert.That(
                pending.Count,
                Is.EqualTo(1),
                "The throwing de-registration stays retryable; the successful one is removed."
            );

            // The retained de-registration retries on a second pass.
            Exception second = pending.InvokeFrom(0);
            Assert.That(second, Is.SameAs(thrown));
            Assert.That(firstInvocations, Is.EqualTo(2), "The retained de-registration retries.");
        }

        [Test]
        public void InvokeFromPromotesSurvivingOverflowDeregistrationToHead()
        {
            int headInvocations = 0;
            int overflowInvocations = 0;
            InvalidOperationException thrown = new InvalidOperationException("boom");
            Pending pending = new Pending();
            pending.Add(() => ++headInvocations); // head: succeeds
            pending.Add(() =>
            {
                ++overflowInvocations;
                throw thrown; // overflow: fails, must be kept and promoted to head
            });

            Exception exception = pending.InvokeFrom(0);

            Assert.That(exception, Is.SameAs(thrown));
            Assert.That(headInvocations, Is.EqualTo(1), "The head ran and was removed.");
            Assert.That(overflowInvocations, Is.EqualTo(1));
            Assert.That(
                pending.Count,
                Is.EqualTo(1),
                "The failed overflow de-registration is retained (promoted to the head slot)."
            );
            Assert.That(
                pending.OverflowList,
                Is.Empty,
                "Promoting the lone survivor to the head leaves the overflow list empty."
            );

            // The promoted survivor retries from the head slot.
            Exception second = pending.InvokeFrom(0);
            Assert.That(second, Is.SameAs(thrown));
            Assert.That(
                overflowInvocations,
                Is.EqualTo(2),
                "The promoted de-registration retries."
            );
        }

        [Test]
        public void InvokeFromOneInvokesEveryOverflowEntryButKeepsTheBaselineHead()
        {
            List<int> order = new List<int>();
            Pending pending = new Pending();
            pending.Add(() => order.Add(0)); // head, logical index 0 (baseline)
            pending.Add(() => order.Add(1)); // overflow, logical index 1
            pending.Add(() => order.Add(2)); // overflow, logical index 2

            // startIndex == 1 leaves the head untouched and runs both overflow entries
            // (overflowStart == 0). This is the boundary where startIndex maps to the
            // first overflow entry.
            Exception exception = pending.InvokeFrom(1);

            Assert.That(exception, Is.Null);
            Assert.That(order, Is.EqualTo(new[] { 1, 2 }), "Both overflow entries run, in order.");
            Assert.That(pending.Count, Is.EqualTo(1), "Only the baseline head is retained.");
        }

        [Test]
        public void InvokeFromDeepBaselineInvokesOnlyTheSuffixOfALargeOverflow()
        {
            List<int> order = new List<int>();
            Pending pending = new Pending();
            pending.Add(() => order.Add(0)); // head        -> logical 0
            pending.Add(() => order.Add(1)); // overflow[0] -> logical 1
            pending.Add(() => order.Add(2)); // overflow[1] -> logical 2
            pending.Add(() => order.Add(3)); // overflow[2] -> logical 3

            // startIndex == 3 must skip the head and overflow[0..1] (logical 0..2) and
            // invoke only overflow[2] (logical 3): the load-bearing startIndex - 1
            // mapping (overflowStart == 2) with multiple skipped overflow entries.
            Exception exception = pending.InvokeFrom(3);

            Assert.That(exception, Is.Null);
            Assert.That(order, Is.EqualTo(new[] { 3 }), "Only the logical-index-3 entry runs.");
            Assert.That(
                pending.Count,
                Is.EqualTo(3),
                "The head plus the two skipped overflow entries are retained."
            );
        }

        [Test]
        public void InvokeFromZeroRetainsMultipleFailedOverflowEntriesInOrder()
        {
            List<int> order = new List<int>();
            InvalidOperationException firstThrown = new InvalidOperationException("first");
            Pending pending = new Pending();
            pending.Add(() => order.Add(0)); // head: succeeds, removed
            pending.Add(() =>
            {
                order.Add(1);
                throw firstThrown; // overflow[0]: fails, retained
            });
            pending.Add(() =>
            {
                order.Add(2);
                throw new InvalidOperationException("second"); // overflow[1]: fails, retained
            });

            Exception exception = pending.InvokeFrom(0);

            Assert.That(exception, Is.SameAs(firstThrown), "The FIRST exception is surfaced.");
            Assert.That(order, Is.EqualTo(new[] { 0, 1, 2 }), "All three run, in logical order.");
            Assert.That(
                pending.Count,
                Is.EqualTo(2),
                "Both failed overflow entries are retained (one promoted to the head slot)."
            );

            // The two retained failures retry in their original relative order (the
            // promote step kept the failed overflow[0] as the logical head).
            order.Clear();
            Exception retry = pending.InvokeFrom(0);
            Assert.That(retry, Is.SameAs(firstThrown));
            Assert.That(
                order,
                Is.EqualTo(new[] { 1, 2 }),
                "The retained failures retry in their original relative order."
            );
            Assert.That(pending.Count, Is.EqualTo(2), "Both still fail, so both stay retryable.");
        }

        [Test]
        public void InvokeFromNegativeStartIndexClampsToZeroAndInvokesEverything()
        {
            List<int> order = new List<int>();
            Pending pending = new Pending();
            pending.Add(() => order.Add(0));
            pending.Add(() => order.Add(1));

            Exception exception = pending.InvokeFrom(-5);

            Assert.That(exception, Is.Null);
            Assert.That(order, Is.EqualTo(new[] { 0, 1 }), "A negative startIndex clamps to 0.");
            Assert.That(pending.Count, Is.EqualTo(0));
        }

        [Test]
        public void InvokeFromOnEmptyPendingReturnsNullAndStaysEmpty()
        {
            Pending pending = new Pending();

            Exception exception = pending.InvokeFrom(0);

            Assert.That(exception, Is.Null);
            Assert.That(pending.Count, Is.EqualTo(0));
            Assert.That(pending.OverflowList, Is.Null);
        }

        [Test]
        public void ReentrantAddDuringHeadInvokeAppendsToTailAndRunsInTheSamePass()
        {
            // Reproduces the List form's behavior: a de-registration that, while running,
            // adds another de-registration for the same handle is appended to the logical
            // tail AND invoked in the same InvokeFrom pass (the overflow loop re-reads its
            // Count). The Add guard keeps it out of the just-consumed head slot.
            List<int> order = new List<int>();
            Pending pending = new Pending();
            pending.Add(() =>
            {
                order.Add(0);
                pending.Add(() => order.Add(1)); // re-entrant add during head invoke
            });

            Exception exception = pending.InvokeFrom(0);

            Assert.That(exception, Is.Null);
            Assert.That(
                order,
                Is.EqualTo(new[] { 0, 1 }),
                "The re-entrant de-registration runs in the same pass, after the head."
            );
            Assert.That(pending.Count, Is.EqualTo(0), "Both are consumed.");
            Assert.That(pending.OverflowList, Is.Null.Or.Empty);
        }

        /// <summary>
        /// Reflection wrapper over the private nested <c>PendingDeregistration</c> so the
        /// tests read like ordinary usage. Resolves the type, its <c>Add</c>/<c>InvokeFrom</c>
        /// members, the <c>Count</c> property, and the lone <see cref="List{Action}"/> field
        /// (the overflow list) once.
        /// </summary>
        private sealed class Pending
        {
            private static readonly MethodInfo AddMethod = PendingType?.GetMethod(
                "Add",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            private static readonly MethodInfo InvokeFromMethod = PendingType?.GetMethod(
                "InvokeFrom",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            private static readonly PropertyInfo CountProperty = PendingType?.GetProperty(
                "Count",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            private static readonly FieldInfo OverflowField = ResolveOverflowField();

            private readonly object _instance;

            internal Pending()
            {
                Assert.That(PendingType, Is.Not.Null, "PendingDeregistration type missing.");
                Assert.That(AddMethod, Is.Not.Null, "PendingDeregistration.Add missing.");
                Assert.That(
                    InvokeFromMethod,
                    Is.Not.Null,
                    "PendingDeregistration.InvokeFrom missing."
                );
                Assert.That(CountProperty, Is.Not.Null, "PendingDeregistration.Count missing.");
                Assert.That(
                    OverflowField,
                    Is.Not.Null,
                    "PendingDeregistration must keep a List<HandlerDeregistration> overflow field."
                );
                _instance = Activator.CreateInstance(PendingType, nonPublic: true);
            }

            internal int Count => (int)CountProperty.GetValue(_instance);

            // The overflow field is now a List<MessageHandler.HandlerDeregistration>; the
            // tests only assert its Count / null-or-empty, so expose it through the
            // non-generic ICollection surface.
            internal System.Collections.ICollection OverflowList =>
                (System.Collections.ICollection)OverflowField.GetValue(_instance);

            internal void Add(Action action)
            {
                // PendingDeregistration.Add now takes a HandlerDeregistration; wrap the
                // test's Action so the existing test bodies (which express each
                // de-registration as a lambda) keep reading like ordinary usage.
                AddMethod.Invoke(_instance, new object[] { new ActionDeregistration(action) });
            }

            internal Exception InvokeFrom(int startIndex)
            {
                return (Exception)InvokeFromMethod.Invoke(_instance, new object[] { startIndex });
            }

            private static FieldInfo ResolveOverflowField()
            {
                if (PendingType == null)
                {
                    return null;
                }

                foreach (
                    FieldInfo field in PendingType.GetFields(
                        BindingFlags.Instance | BindingFlags.NonPublic
                    )
                )
                {
                    if (field.FieldType == typeof(List<MessageHandler.HandlerDeregistration>))
                    {
                        return field;
                    }
                }

                return null;
            }
        }

        /// <summary>
        /// Test-only <see cref="MessageHandler.HandlerDeregistration"/> that runs a plain
        /// <see cref="Action"/> when deregistered, so the storage tests can express each
        /// de-registration as a lambda (mirroring the old <c>List&lt;Action&gt;</c> form).
        /// </summary>
        private sealed class ActionDeregistration : MessageHandler.HandlerDeregistration
        {
            private readonly Action _action;

            internal ActionDeregistration(Action action)
            {
                _action = action;
            }

            internal override void Deregister()
            {
                _action?.Invoke();
            }
        }
    }
}
#endif
