#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using DxMessaging.Core.DataStructure;
    using NUnit.Framework;

    public sealed class CyclicBufferTests
    {
        [Test]
        public void AddAndOverwritePreservesChronology()
        {
            CyclicBuffer<int> buf = new(3) { 0, 1, 2 };

            Assert.AreEqual(
                3,
                buf.Count,
                "Count should reflect number of elements added up to capacity."
            );
            Assert.AreEqual(0, buf[0], "Oldest element should be at index 0 before wrap.");
            Assert.AreEqual(1, buf[1], "Next element should be index 1.");
            Assert.AreEqual(2, buf[2], "Newest element should be index 2 before wrap.");

            // Overwrite oldest
            buf.Add(3);
            Assert.AreEqual(3, buf.Count, "Count should not grow beyond capacity.");
            Assert.AreEqual(1, buf[0], "After overwrite, oldest is dropped.");
            Assert.AreEqual(2, buf[1], "Element order should advance by one.");
            Assert.AreEqual(3, buf[2], "Newest written value should be last.");

            // Remove middle element
            bool removed = buf.Remove(2);
            Assert.IsTrue(removed, "Remove should return true when element existed.");
            Assert.AreEqual(2, buf.Count, "Count should decrease after remove.");
            Assert.AreEqual(1, buf[0], "Remaining first element should be unchanged.");
            Assert.AreEqual(3, buf[1], "Remaining second element should be next in order.");
        }

        [Test]
        public void ResizeTruncatesOrExtends()
        {
            CyclicBuffer<int> buf = new(5);
            for (int i = 0; i < 5; ++i)
            {
                buf.Add(i);
            }
            Assert.AreEqual(5, buf.Count, "Filled buffer should have full count.");

            // Shrink: oldest entries should be truncated
            buf.Resize(3);
            Assert.AreEqual(3, buf.Count, "Count should reflect new capacity after shrink.");
            Assert.AreEqual(2, buf[0], "Shrink should retain most recent entries and drop oldest.");
            Assert.AreEqual(3, buf[1], "Remaining order should be preserved (middle).");
            Assert.AreEqual(4, buf[2], "Remaining order should be preserved (newest).");

            // Grow: capacity increases, order stays
            buf.Resize(6);
            Assert.AreEqual(3, buf.Count, "Growing capacity should not change current count.");
            Assert.AreEqual(2, buf[0], "Growing capacity should not alter order (first).");
            Assert.AreEqual(3, buf[1], "Growing capacity should not alter order (second).");
            Assert.AreEqual(4, buf[2], "Growing capacity should not alter order (third).");
        }

        [Test]
        [TestCase(0, 5, 0, Description = "Zero capacity discards all items")]
        [TestCase(1, 5, 1, Description = "Capacity 1 keeps only most recent")]
        [TestCase(3, 5, 3, Description = "Capacity 3 keeps 3 most recent of 5")]
        [TestCase(5, 5, 5, Description = "Capacity equals item count")]
        [TestCase(10, 5, 5, Description = "Capacity larger than items")]
        public void CapacityBehaviorDataDriven(int capacity, int itemsToAdd, int expectedCount)
        {
            CyclicBuffer<int> buffer = new(capacity);
            for (int i = 0; i < itemsToAdd; ++i)
            {
                buffer.Add(i);
            }

            Assert.AreEqual(
                expectedCount,
                buffer.Count,
                $"Buffer with capacity {capacity} after adding {itemsToAdd} items."
            );

            // Verify the most recent items are retained (if any)
            if (expectedCount > 0)
            {
                int expectedNewest = itemsToAdd - 1;
                Assert.AreEqual(
                    expectedNewest,
                    buffer[expectedCount - 1],
                    "Most recent item should be preserved."
                );
            }
        }

        [Test]
        [TestCase(
            new[] { 1, 2, 3 },
            1,
            new[] { 2, 3 },
            Description = "Remove first element by value"
        )]
        [TestCase(
            new[] { 1, 2, 3 },
            2,
            new[] { 1, 3 },
            Description = "Remove middle element by value"
        )]
        [TestCase(
            new[] { 1, 2, 3 },
            3,
            new[] { 1, 2 },
            Description = "Remove last element by value"
        )]
        [TestCase(
            new[] { 1, 2, 3 },
            99,
            new[] { 1, 2, 3 },
            Description = "Remove non-existent element"
        )]
        [TestCase(new[] { 1 }, 1, new int[0], Description = "Remove only element")]
        public void RemoveBehaviorDataDriven(int[] initial, int toRemove, int[] expected)
        {
            CyclicBuffer<int> buffer = new(initial.Length, initial);
            bool wasRemoved = buffer.Remove(toRemove);

            bool shouldHaveBeenRemoved = initial.Contains(toRemove);
            Assert.AreEqual(
                shouldHaveBeenRemoved,
                wasRemoved,
                $"Remove({toRemove}) should return {shouldHaveBeenRemoved} for initial buffer [{string.Join(", ", initial)}]."
            );

            Assert.AreEqual(
                expected.Length,
                buffer.Count,
                $"Count after Remove({toRemove}) from [{string.Join(", ", initial)}] should be {expected.Length}, but was {buffer.Count}."
            );

            int[] actual = buffer.ToArray();
            CollectionAssert.AreEqual(
                expected,
                actual,
                $"After Remove({toRemove}) from [{string.Join(", ", initial)}], expected [{string.Join(", ", expected)}] but got [{string.Join(", ", actual)}]."
            );
        }

        [Test]
        [TestCase(
            new[] { 1, 2, 2, 3 },
            2,
            new[] { 1, 2, 3 },
            Description = "Remove first occurrence of duplicate"
        )]
        [TestCase(
            new[] { 2, 2, 2 },
            2,
            new[] { 2, 2 },
            Description = "Remove first of all duplicates"
        )]
        [TestCase(
            new[] { 1, 1, 1 },
            1,
            new[] { 1, 1 },
            Description = "Remove first of identical elements"
        )]
        public void RemoveDuplicatesBehavior(int[] initial, int toRemove, int[] expected)
        {
            CyclicBuffer<int> buffer = new(initial.Length, initial);
            bool wasRemoved = buffer.Remove(toRemove);

            Assert.IsTrue(
                wasRemoved,
                $"Remove({toRemove}) should return true when element exists in [{string.Join(", ", initial)}]."
            );

            Assert.AreEqual(
                expected.Length,
                buffer.Count,
                $"Count after Remove({toRemove}) from [{string.Join(", ", initial)}] should be {expected.Length}, but was {buffer.Count}."
            );

            int[] actual = buffer.ToArray();
            CollectionAssert.AreEqual(
                expected,
                actual,
                $"After Remove({toRemove}) from [{string.Join(", ", initial)}], expected [{string.Join(", ", expected)}] but got [{string.Join(", ", actual)}]."
            );
        }

        [Test]
        public void RemoveFromEmptyBuffer()
        {
            CyclicBuffer<int> buffer = new(5);
            bool wasRemoved = buffer.Remove(1);
            Assert.IsFalse(wasRemoved, "Remove from empty buffer should return false");
            Assert.AreEqual(0, buffer.Count, "Count should remain 0");
        }

        [Test]
        public void ScratchCacheDoesNotRetainReferencesAfterRemovePathsOrClear()
        {
            object first = new();
            object second = new();
            object missing = new();
            CyclicBuffer<object> buffer = new(3) { first, second };

            Assert.IsFalse(
                buffer.Remove(missing),
                "Control failed: missing item should not remove."
            );
            Assert.AreEqual(
                0,
                buffer.ScratchCacheCount,
                "Failed Remove must clear the scratch cache."
            );

            Assert.AreEqual(
                0,
                buffer.RemoveAll(_ => false),
                "Control failed: RemoveAll should remove nothing."
            );
            Assert.AreEqual(
                0,
                buffer.ScratchCacheCount,
                "No-op RemoveAll must clear the scratch cache."
            );

            Assert.Throws<InvalidOperationException>(
                () => buffer.Remove(first, new ThrowingEqualityComparer<object>()),
                "Control failed: throwing comparer must surface its exception."
            );
            Assert.AreEqual(
                0,
                buffer.ScratchCacheCount,
                "Throwing Remove comparer must clear the scratch cache."
            );

            Assert.Throws<InvalidOperationException>(
                () =>
                    buffer.RemoveAll(_ =>
                        throw new InvalidOperationException("Predicate failure.")
                    ),
                "Control failed: throwing predicate must surface its exception."
            );
            Assert.AreEqual(
                0,
                buffer.ScratchCacheCount,
                "Throwing RemoveAll predicate must clear the scratch cache."
            );

            Assert.IsTrue(buffer.Remove(first), "Control failed: existing item should remove.");
            Assert.AreEqual(
                0,
                buffer.ScratchCacheCount,
                "Successful Remove must clear the scratch cache after rebuilding."
            );

            buffer.Add(first);
            buffer.Clear();
            Assert.AreEqual(0, buffer.Count, "Control failed: Clear must empty the buffer.");
            Assert.AreEqual(0, buffer.ScratchCacheCount, "Clear must clear the scratch cache.");
        }

        [Test]
        [TestCase(2, new[] { 3, 4 }, Description = "Remove first element from wrapped buffer")]
        [TestCase(3, new[] { 2, 4 }, Description = "Remove middle element from wrapped buffer")]
        [TestCase(4, new[] { 2, 3 }, Description = "Remove last element from wrapped buffer")]
        public void RemoveFromWrappedBuffer(int toRemove, int[] expected)
        {
            // Create a buffer that has wrapped around
            CyclicBuffer<int> buffer = new(3)
            {
                1,
                2,
                3,
                // Buffer is now [1, 2, 3], full
                4,
            };
            // Buffer has wrapped, now contains [2, 3, 4] logically

            int[] beforeRemove = buffer.ToArray();
            Assert.AreEqual(
                new[] { 2, 3, 4 },
                beforeRemove,
                $"Before remove, wrapped buffer should be [2, 3, 4] but was [{string.Join(", ", beforeRemove)}]."
            );

            bool wasRemoved = buffer.Remove(toRemove);
            Assert.IsTrue(
                wasRemoved,
                $"Remove({toRemove}) should return true for wrapped buffer containing {toRemove}."
            );

            int[] afterRemove = buffer.ToArray();
            Assert.AreEqual(
                expected.Length,
                buffer.Count,
                $"Count after Remove({toRemove}) from wrapped buffer should be {expected.Length}, but was {buffer.Count}."
            );
            CollectionAssert.AreEqual(
                expected,
                afterRemove,
                $"After Remove({toRemove}) from wrapped buffer [2, 3, 4], expected [{string.Join(", ", expected)}] but got [{string.Join(", ", afterRemove)}]."
            );
        }

        [Test]
        public void RemoveWithCustomComparer()
        {
            // Case-insensitive string comparer
            CyclicBuffer<string> buffer = new(5) { "Apple", "Banana", "Cherry" };

            // Should find and remove "BANANA" using case-insensitive comparison
            bool wasRemoved = buffer.Remove("BANANA", StringComparer.OrdinalIgnoreCase);
            Assert.IsTrue(
                wasRemoved,
                "Remove with case-insensitive comparer should find 'BANANA' matching 'Banana'."
            );

            string[] afterRemove = buffer.ToArray();
            Assert.AreEqual(2, buffer.Count, "Count should be 2 after removal.");
            CollectionAssert.AreEqual(
                new[] { "Apple", "Cherry" },
                afterRemove,
                $"After Remove('BANANA') with case-insensitive comparer, expected ['Apple', 'Cherry'] but got [{string.Join(", ", afterRemove)}]."
            );

            // Should NOT find "banana" using default (case-sensitive) comparison
            buffer.Add("Durian");
            wasRemoved = buffer.Remove("APPLE"); // default comparer is case-sensitive
            Assert.IsFalse(
                wasRemoved,
                "Remove with default comparer should NOT find 'APPLE' when buffer contains 'Apple'."
            );
            Assert.AreEqual(3, buffer.Count, "Count should remain 3 when element not found.");
        }

        [Test]
        public void SequentialRemoves()
        {
            CyclicBuffer<int> buffer = new(5, new[] { 1, 2, 3, 4, 5 });

            Assert.IsTrue(buffer.Remove(2), "First remove should succeed");
            CollectionAssert.AreEqual(new[] { 1, 3, 4, 5 }, buffer.ToArray(), "After first remove");

            Assert.IsTrue(buffer.Remove(4), "Second remove should succeed");
            CollectionAssert.AreEqual(new[] { 1, 3, 5 }, buffer.ToArray(), "After second remove");

            Assert.IsTrue(buffer.Remove(1), "Third remove should succeed");
            CollectionAssert.AreEqual(new[] { 3, 5 }, buffer.ToArray(), "After third remove");

            Assert.AreEqual(2, buffer.Count, "Final count should be 2");
        }

        [Test]
        public void WrappedBufferEnumerationConsistencyAfterRemove()
        {
            // Create a wrapped buffer
            CyclicBuffer<int> buffer = new(3)
            {
                1,
                2,
                3,
                4, // Now wrapped: [2, 3, 4]
            };

            buffer.Remove(3); // Remove middle: should be [2, 4]

            // Verify ToArray() and foreach enumeration give same results
            int[] fromToArray = buffer.ToArray();
            List<int> fromEnumeration = new();
            foreach (int item in buffer)
            {
                fromEnumeration.Add(item);
            }

            CollectionAssert.AreEqual(
                new[] { 2, 4 },
                fromToArray,
                "ToArray() should return [2, 4]"
            );
            CollectionAssert.AreEqual(
                fromToArray,
                fromEnumeration.ToArray(),
                "Enumeration should match ToArray()"
            );
        }

        [Test]
        public void ZeroCapacityBufferIsEmpty()
        {
            CyclicBuffer<int> buffer = new(0)
            {
                // Add multiple items - all should be silently discarded
                1,
                2,
                3,
            };

            Assert.AreEqual(0, buffer.Capacity, "Capacity should remain 0.");
            Assert.AreEqual(0, buffer.Count, "Count should remain 0.");
        }

        private sealed class ThrowingEqualityComparer<T> : IEqualityComparer<T>
        {
            public bool Equals(T x, T y)
            {
                throw new InvalidOperationException("Comparer failure.");
            }

            public int GetHashCode(T obj)
            {
                return EqualityComparer<T>.Default.GetHashCode(obj);
            }
        }
    }
}
#endif
