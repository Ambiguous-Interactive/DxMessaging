#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Benchmarks
{
    using System;
    using System.Collections.Generic;
    using NUnit.Framework;

    [Category("PerfBench")]
    public sealed class StickyFirstTypeBenchmarkTests
    {
        [TestCaseSource(nameof(PublicationCases))]
        public void FirstPublicationUsesItsOwnTypeAndBus(
            bool sticky,
            bool referencePayload,
            int trial
        )
        {
            Type type = StickyFirstTypeBenchmarks.RunContract(sticky, referencePayload, trial);
            Assert.That(
                type.IsClass,
                Is.EqualTo(referencePayload),
                $"sticky={sticky}, reference={referencePayload}, trial={trial}"
            );
        }

        public static IEnumerable<TestCaseData> PublicationCases()
        {
            foreach (bool sticky in new[] { false, true })
            {
                foreach (bool referencePayload in new[] { false, true })
                {
                    for (int trial = 0; trial < StickyFirstTypeBenchmarks.Trials; ++trial)
                    {
                        yield return new TestCaseData(sticky, referencePayload, trial).SetName(
                            $"FirstTypeContract_sticky{sticky}_reference{referencePayload}_trial{trial:D2}"
                        );
                    }
                }
            }
        }

        [Test]
        public void FirstUseAdmissionRejectsRepeatedTypesBeforeConsumingAnyNewType()
        {
            HashSet<Type> used = new();
            StickyFirstTypeBenchmarks.ReserveTypes(used, new[] { typeof(int), typeof(string) });
            Assert.Throws<InvalidOperationException>(() =>
                StickyFirstTypeBenchmarks.ReserveTypes(used, new[] { typeof(double), typeof(int) })
            );
            CollectionAssert.AreEquivalent(
                new[] { typeof(int), typeof(string) },
                used,
                "A rejected row must not consume a new type."
            );
            StickyFirstTypeBenchmarks.ReserveTypes(used, new[] { typeof(double) });
            Assert.That(
                used.Count,
                Is.EqualTo(3),
                "A new type can still be reserved after rejected admission."
            );
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FirstUseAdmissionRejectsDuplicateOrNullTypesAtomically(bool nullType)
        {
            HashSet<Type> used = new();
            Type[] requested = nullType
                ? new[] { typeof(int), null }
                : new[] { typeof(int), typeof(int) };
            Assert.Throws<InvalidOperationException>(() =>
                StickyFirstTypeBenchmarks.ReserveTypes(used, requested)
            );
            Assert.That(
                used,
                Is.Empty,
                $"nullType={nullType}: an invalid row must reserve no types."
            );
        }

        [Test]
        public void FirstUseAdmissionRequiresANonemptyRowAndOwner()
        {
            HashSet<Type> used = new();
            Assert.Throws<ArgumentNullException>(() =>
                StickyFirstTypeBenchmarks.ReserveTypes(null, new[] { typeof(int) })
            );
            Assert.Throws<ArgumentNullException>(() =>
                StickyFirstTypeBenchmarks.ReserveTypes(used, null)
            );
            Assert.Throws<ArgumentException>(() =>
                StickyFirstTypeBenchmarks.ReserveTypes(used, Array.Empty<Type>())
            );
            Assert.That(used, Is.Empty, "Invalid admission must preserve the owner.");
        }
    }
}
#endif
