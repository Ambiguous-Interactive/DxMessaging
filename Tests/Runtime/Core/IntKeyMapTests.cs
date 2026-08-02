#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core.DataStructure;
    using NUnit.Framework;

    public sealed class IntKeyMapTests
    {
        [Test]
        public void SetAndLookupCoverIntegerEdgeKeysAndGrowth()
        {
            IntKeyMap<string> map = new();
            int[] keys = { 0, -1, 1, int.MinValue, int.MaxValue, 4, 8, 12, 16, 1024, -1024 };

            for (int index = 0; index < keys.Length; ++index)
            {
                map[keys[index]] = $"value-{index}";
            }

            Assert.AreEqual(keys.Length, map.Count, "Every distinct key should occupy one entry.");
            Assert.GreaterOrEqual(map.Capacity, map.Count, "Growth must retain enough storage.");
            for (int index = 0; index < keys.Length; ++index)
            {
                Assert.IsTrue(
                    map.TryGetValue(keys[index], out string value),
                    $"Missing key {keys[index]}."
                );
                Assert.AreEqual($"value-{index}", value, $"Wrong value for key {keys[index]}.");
            }
        }

        [Test]
        public void UpdatingExistingKeyPreservesCountAndRejectsNull()
        {
            IntKeyMap<string> map = new();
            map[7] = "first";
            map[7] = "second";

            Assert.AreEqual(1, map.Count, "Replacing a value must not add an entry.");
            Assert.IsTrue(map.TryGetValue(7, out string value), "The updated key should exist.");
            Assert.AreEqual("second", value, "Lookup should return the replacement value.");
            Assert.Throws<ArgumentNullException>(
                () => map[8] = null,
                "Null cannot be represented because it marks an empty bucket."
            );
            Assert.AreEqual(1, map.Count, "A rejected null value must leave the map unchanged.");
        }

        [Test]
        public void RemoveBackShiftsWrappedCollisionCluster()
        {
            IntKeyMap<string> map = new();
            Assert.AreEqual(
                3,
                IntKeyMap<string>.Bucket(-100, 3),
                "The collision must begin in the last bucket to exercise wraparound."
            );
            Assert.AreEqual(
                IntKeyMap<string>.Bucket(-100, 3),
                IntKeyMap<string>.Bucket(-99, 3),
                "The first two keys must share a home bucket for this structural test."
            );
            Assert.AreEqual(
                IntKeyMap<string>.Bucket(-100, 3),
                IntKeyMap<string>.Bucket(-98, 3),
                "All three keys must share a home bucket for this structural test."
            );
            map[-100] = "first";
            map[-99] = "second";
            map[-98] = "third";
            Assert.AreEqual(4, map.Capacity, "The cluster should wrap within four buckets.");

            Assert.IsTrue(map.Remove(-100), "The cluster head should be removable.");
            Assert.IsFalse(map.TryGetValue(-100, out _), "The removed key must stay absent.");
            Assert.IsTrue(
                map.TryGetValue(-99, out string second),
                "The first shifted key should remain."
            );
            Assert.AreEqual("second", second, "The first wrapped entry must remain reachable.");
            Assert.IsTrue(
                map.TryGetValue(-98, out string third),
                "The cluster tail should remain."
            );
            Assert.AreEqual("third", third, "The second wrapped entry must remain reachable.");

            Assert.IsTrue(map.Remove(-99), "A shifted entry should remain removable.");
            Assert.IsTrue(map.TryGetValue(-98, out third), "The cluster tail should remain.");
            Assert.AreEqual("third", third, "The cluster tail value should remain unchanged.");
            Assert.IsFalse(map.Remove(99), "Removing an absent key should be a no-op.");
            Assert.AreEqual(1, map.Count, "Only the cluster tail should remain.");
        }

        [Test]
        public void ClearReleasesValuesAndKeepsReusableCapacity()
        {
            IntKeyMap<object> map = new();
            object first = new();
            object second = new();
            map[1] = first;
            map[5] = second;
            int capacity = map.Capacity;

            map.Clear();

            Assert.AreEqual(0, map.Count, "Clear should remove every logical entry.");
            Assert.AreEqual(
                capacity,
                map.Capacity,
                "A pooled map should retain its reusable arrays."
            );
            Assert.IsFalse(map.TryGetValue(1, out _), "Clear should remove the first value.");
            Assert.IsFalse(map.TryGetValue(5, out _), "Clear should remove the second value.");
            int enumerated = 0;
            foreach (object _ in map)
            {
                enumerated++;
            }
            Assert.AreEqual(0, enumerated, "Clear must remove every enumerable value.");

            map[-9] = first;
            Assert.IsTrue(
                map.TryGetValue(-9, out object reused),
                "Cleared storage should accept a new key."
            );
            Assert.AreSame(first, reused, "Cleared storage should remain reusable.");
        }

        [Test]
        public void HighBitSpacedKeysDistributeAcrossBucketsAndRemainReachable()
        {
            const int capacity = 1024;
            const int keyCount = 256;
            HashSet<int> homeBuckets = new();
            IntKeyMap<string> map = new();

            for (int index = 0; index < keyCount; ++index)
            {
                int key = index << 20;
                homeBuckets.Add(IntKeyMap<string>.Bucket(key, capacity - 1));
                map[key] = $"value-{index}";
            }

            Assert.GreaterOrEqual(
                homeBuckets.Count,
                192,
                "Hashing should mix high key bits into low power-of-two bucket bits."
            );
            for (int index = 0; index < keyCount; ++index)
            {
                int key = index << 20;
                Assert.IsTrue(
                    map.TryGetValue(key, out string value),
                    $"High-bit-spaced key {key} should remain reachable."
                );
                Assert.AreEqual(
                    $"value-{index}",
                    value,
                    $"High-bit-spaced key {key} returned the wrong value."
                );
            }
        }

        [Test]
        public void RandomizedOperationsMatchDictionaryOracle()
        {
            const int operationCount = 20000;
            Random random = new(0x289);
            IntKeyMap<string> actual = new();
            Dictionary<int, string> expected = new();

            for (int operation = 0; operation < operationCount; ++operation)
            {
                int key = random.Next(-128, 129) * 4 + 3;
                switch (random.Next(3))
                {
                    case 0:
                        string value = $"{operation}:{key}";
                        actual[key] = value;
                        expected[key] = value;
                        break;
                    case 1:
                        Assert.AreEqual(
                            expected.Remove(key),
                            actual.Remove(key),
                            $"Remove diverged at operation {operation} for key {key}."
                        );
                        break;
                    default:
                        bool expectedFound = expected.TryGetValue(key, out string expectedValue);
                        bool actualFound = actual.TryGetValue(key, out string actualValue);
                        Assert.AreEqual(
                            expectedFound,
                            actualFound,
                            $"Lookup presence diverged at operation {operation} for key {key}."
                        );
                        Assert.AreEqual(
                            expectedValue,
                            actualValue,
                            $"Lookup value diverged at operation {operation} for key {key}."
                        );
                        break;
                }

                Assert.AreEqual(
                    expected.Count,
                    actual.Count,
                    $"Count diverged after operation {operation}."
                );
            }

            HashSet<string> enumerated = new();
            foreach (string value in actual)
            {
                Assert.IsTrue(enumerated.Add(value), "Enumeration returned a duplicate value.");
            }
            CollectionAssert.AreEquivalent(
                expected.Values,
                enumerated,
                "Enumeration should return exactly the oracle's live values."
            );
        }
    }
}
#endif
