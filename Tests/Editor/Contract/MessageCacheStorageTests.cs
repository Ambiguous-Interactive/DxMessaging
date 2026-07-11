#if UNITY_EDITOR
namespace DxMessaging.Tests.Editor.Contract
{
    using System;
    using System.Linq;
    using System.Reflection;
    using DxMessaging.Core;
    using DxMessaging.Core.Helper;
    using DxMessaging.Core.Messages;
    using NUnit.Framework;

    [TestFixture]
    [Category("Contract")]
    public sealed class MessageCacheStorageTests
    {
        [Test]
        public void NewCacheUsesEmptyArrayBackedStorage()
        {
            FieldInfo values = typeof(MessageCache<object>).GetField(
                "_values",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            Assert.That(values, Is.Not.Null, "MessageCache<T>._values was renamed.");
            Assert.That(
                values.FieldType,
                Is.EqualTo(typeof(object[])),
                "MessageCache<T> must use a sparse array rather than eagerly allocating a List<T>."
            );

            MessageCache<object> cache = new MessageCache<object>();
            object[] storage = (object[])values.GetValue(cache);
            Assert.That(
                storage,
                Is.SameAs(Array.Empty<object>()),
                "A new MessageCache<T> must share Array.Empty<T>() until its first stored value."
            );
        }

        [Test]
        public void SparseStoragePreservesLookupRemovalEnumerationAndClearSemantics()
        {
            MessageCache<Box> cache = new MessageCache<Box>();
            MessageCache<Box> gapOwner = new MessageCache<Box>();
            Box low = new Box("low");
            Box high = new Box("high");

            cache.Set<LowMessage>(low);
            gapOwner.Set<GapMessage>(new Box("gap"));
            cache.Set<HighMessage>(high);

            Assert.That(cache.TryGetValue<LowMessage>(out Box foundLow), Is.True);
            Assert.That(foundLow, Is.SameAs(low));
            Assert.That(cache.TryGetValue<GapMessage>(out _), Is.False);
            Assert.That(cache.TryGetValue<HighMessage>(out Box foundHigh), Is.True);
            Assert.That(foundHigh, Is.SameAs(high));
            CollectionAssert.AreEqual(new[] { low, high }, cache.ToArray());

            cache.Remove<LowMessage>();
            CollectionAssert.AreEqual(new[] { high }, cache.ToArray());

            cache.Clear();
            Assert.That(cache, Is.Empty);
            Assert.That(cache.TryGetValue<HighMessage>(out _), Is.False);

            Box replacement = cache.GetOrAdd<HighMessage>();
            Assert.That(replacement, Is.Not.Null);
            Assert.That(cache.TryGetValue<HighMessage>(out Box foundReplacement), Is.True);
            Assert.That(foundReplacement, Is.SameAs(replacement));
            CollectionAssert.AreEqual(new[] { replacement }, cache.ToArray());
        }

        [Test]
        public void EnumeratorObservesLaterSparseInsertionLikeListBackedEnumerator()
        {
            MessageCache<Box> cache = new MessageCache<Box>();
            Box first = new Box("first");
            Box later = new Box("later");
            cache.Set<EnumerationFirstMessage>(first);

            MessageCache<Box>.MessageCacheEnumerator enumerator = cache.GetEnumerator();
            Assert.That(enumerator.MoveNext(), Is.True);
            Assert.That(enumerator.Current, Is.SameAs(first));

            cache.Set<EnumerationLaterMessage>(later);

            Assert.That(enumerator.MoveNext(), Is.True);
            Assert.That(enumerator.Current, Is.SameAs(later));
            Assert.That(enumerator.MoveNext(), Is.False);
        }

        [Test]
        public void SparseStorageRejectsAnIndexThatWouldOverflowCapacityGrowth()
        {
            MessageCache<Box> cache = new MessageCache<Box>();
            int previousIndex = MessageHelperIndexer<OverflowMessage>.SequentialId;
            try
            {
                MessageHelperIndexer<OverflowMessage>.SequentialId = int.MaxValue;

                InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
                    cache.Set<OverflowMessage>(new Box("overflow"))
                );

                StringAssert.Contains("supported capacity", exception.Message);
                Assert.That(cache, Is.Empty, "A rejected index must not change cache enumeration.");
                Assert.That(
                    cache.TryGetValue<OverflowMessage>(out _),
                    Is.False,
                    "A rejected index must not leave a partial cache entry."
                );
            }
            finally
            {
                MessageHelperIndexer<OverflowMessage>.SequentialId = previousIndex;
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NewTypeAssignmentRemainsUnpublishedWhenCapacityGrowthIsRejected(
            bool useGetOrAdd
        )
        {
            const int FirstUnsupportedIndex = 1 << 30;
            MessageCache<Box> cache = new MessageCache<Box>();
            int previousIndex = MessageHelperIndexer<UnassignedOverflowMessage>.SequentialId;
            int previousTotal = MessageHelperIndexer.TotalMessages;
            try
            {
                MessageHelperIndexer<UnassignedOverflowMessage>.SequentialId = -1;
                MessageHelperIndexer.TotalMessages = FirstUnsupportedIndex;

                Assert.Throws<InvalidOperationException>(() =>
                {
                    if (useGetOrAdd)
                    {
                        cache.GetOrAdd<UnassignedOverflowMessage>();
                    }
                    else
                    {
                        cache.Set<UnassignedOverflowMessage>(new Box("overflow"));
                    }
                });

                Assert.That(MessageHelperIndexer.TotalMessages, Is.EqualTo(FirstUnsupportedIndex));
                Assert.That(
                    MessageHelperIndexer<UnassignedOverflowMessage>.SequentialId,
                    Is.EqualTo(-1)
                );
                Assert.That(cache, Is.Empty, "A rejected assignment must leave the cache empty.");
                Assert.That(cache.TryGetValue<UnassignedOverflowMessage>(out _), Is.False);
            }
            finally
            {
                MessageHelperIndexer<UnassignedOverflowMessage>.SequentialId = previousIndex;
                MessageHelperIndexer.TotalMessages = previousTotal;
            }
        }

        private sealed class Box
        {
            public Box() { }

            public Box(string value)
            {
                Value = value;
            }

            public string Value { get; }
        }

        private readonly struct LowMessage : IUntargetedMessage { }

        private readonly struct GapMessage : IUntargetedMessage { }

        private readonly struct HighMessage : IUntargetedMessage { }

        private readonly struct EnumerationFirstMessage : IUntargetedMessage { }

        private readonly struct EnumerationLaterMessage : IUntargetedMessage { }

        private readonly struct OverflowMessage : IUntargetedMessage { }

        private readonly struct UnassignedOverflowMessage : IUntargetedMessage { }
    }
}
#endif
