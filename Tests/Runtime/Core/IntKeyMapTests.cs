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
        public void TopologyObserverReportsExactWrappedProbeAndDeletionWork()
        {
            IntKeyMap<string> map = new();
            IntKeyMapTopologyObservation empty = map.ObserveTopologyForBenchmark(-100);

            Assert.IsFalse(empty.Found, "An empty map cannot contain the requested key.");
            Assert.AreEqual(-1, empty.SlotIndex, "An empty map must not report a slot index.");
            Assert.AreEqual(0, empty.LookupProbes, "An unallocated map performs no slot probes.");
            Assert.AreEqual(0, empty.DeletionScans, "An absent key predicts no deletion scans.");
            Assert.AreEqual(0, empty.DeletionMoves, "An absent key predicts no deletion moves.");
            Assert.AreEqual(
                0,
                map.LongestClusterForBenchmark,
                "An empty map must report no occupied cluster."
            );

            map[-100] = "first";
            map[-99] = "second";
            map[-98] = "third";

            IntKeyMapTopologyObservation head = map.ObserveTopologyForBenchmark(-100);
            IntKeyMapTopologyObservation middle = map.ObserveTopologyForBenchmark(-99);
            IntKeyMapTopologyObservation tail = map.ObserveTopologyForBenchmark(-98);
            int collisionMissKey = -97;
            while (IntKeyMap<string>.Bucket(collisionMissKey, 3) != 3)
            {
                collisionMissKey++;
            }
            IntKeyMapTopologyObservation collisionMiss = map.ObserveTopologyForBenchmark(
                collisionMissKey
            );
            int emptyBucketMissKey = 0;
            while (IntKeyMap<string>.Bucket(emptyBucketMissKey, 3) != 2)
            {
                emptyBucketMissKey++;
            }
            IntKeyMapTopologyObservation emptyBucketMiss = map.ObserveTopologyForBenchmark(
                emptyBucketMissKey
            );

            Assert.IsTrue(head.Found, "The wrapped cluster head must be observable.");
            Assert.AreEqual(3, head.SlotIndex, "The wrapped cluster head must occupy bucket 3.");
            Assert.AreEqual(1, head.LookupProbes, "The cluster head must resolve in one probe.");
            Assert.AreEqual(2, head.DeletionScans, "Deleting the head must scan both followers.");
            Assert.AreEqual(2, head.DeletionMoves, "Deleting the head must move both followers.");
            Assert.AreEqual(2, middle.LookupProbes, "The middle entry must resolve in two probes.");
            Assert.AreEqual(1, middle.DeletionScans, "Deleting the middle must scan the tail.");
            Assert.AreEqual(1, middle.DeletionMoves, "Deleting the middle must move the tail.");
            Assert.AreEqual(3, tail.LookupProbes, "The tail must resolve in three probes.");
            Assert.AreEqual(0, tail.DeletionScans, "Deleting the tail must scan no followers.");
            Assert.AreEqual(0, tail.DeletionMoves, "Deleting the tail must move no followers.");
            Assert.IsFalse(collisionMiss.Found, "A same-home missing key must remain absent.");
            Assert.AreEqual(
                4,
                collisionMiss.LookupProbes,
                "A same-home miss must inspect the full wrapped cluster and its empty terminator."
            );
            Assert.IsFalse(emptyBucketMiss.Found, "An empty-home missing key must remain absent.");
            Assert.AreEqual(
                1,
                emptyBucketMiss.LookupProbes,
                "An empty-home miss must stop after one probe."
            );
            Assert.AreEqual(
                3,
                map.LongestClusterForBenchmark,
                "The wrapped occupied run must count as one three-entry cluster."
            );

            Assert.IsTrue(map.Remove(-100), "The observed cluster head must remain removable.");
            IntKeyMapTopologyObservation shiftedMiddle = map.ObserveTopologyForBenchmark(-99);
            IntKeyMapTopologyObservation shiftedTail = map.ObserveTopologyForBenchmark(-98);
            Assert.AreEqual(
                middle.LookupProbes - 1,
                shiftedMiddle.LookupProbes,
                "The predicted first move should close one probe position."
            );
            Assert.AreEqual(
                tail.LookupProbes - 1,
                shiftedTail.LookupProbes,
                "The predicted second move should close one probe position."
            );
            Assert.AreEqual(
                2,
                map.LongestClusterForBenchmark,
                "Removing the head must leave one two-entry wrapped cluster."
            );
        }

        [Test]
        public void TopologyObserverPredictsSelectiveMovesInMixedHomeCluster()
        {
            IntKeyMap<string> map = new();
            Assert.AreEqual(3, IntKeyMap<string>.Bucket(-195, 3), "The gap key must home at 3.");
            Assert.AreEqual(
                0,
                IntKeyMap<string>.Bucket(-191, 3),
                "The stationary key must home at 0."
            );
            Assert.AreEqual(
                3,
                IntKeyMap<string>.Bucket(-192, 3),
                "The movable key must home at 3."
            );
            map[-195] = "gap";
            map[-191] = "stays-at-home";
            map[-192] = "moves-across-wrap";

            IntKeyMapTopologyObservation removed = map.ObserveTopologyForBenchmark(-195);
            IntKeyMapTopologyObservation staying = map.ObserveTopologyForBenchmark(-191);
            IntKeyMapTopologyObservation moving = map.ObserveTopologyForBenchmark(-192);

            Assert.AreEqual(2, removed.DeletionScans, "Deletion must inspect both followers.");
            Assert.AreEqual(1, removed.DeletionMoves, "Only one follower may fill the gap.");
            Assert.AreEqual(0, staying.SlotIndex, "The at-home follower must occupy slot 0.");
            Assert.AreEqual(1, moving.SlotIndex, "The displaced follower must occupy slot 1.");
            Assert.IsTrue(map.Remove(-195), "The observed mixed-home cluster head must remove.");

            IntKeyMapTopologyObservation stayed = map.ObserveTopologyForBenchmark(-191);
            IntKeyMapTopologyObservation moved = map.ObserveTopologyForBenchmark(-192);
            Assert.AreEqual(
                staying.SlotIndex,
                stayed.SlotIndex,
                "An entry at its home bucket must not move into the wrapped gap."
            );
            Assert.AreEqual(
                3,
                moved.SlotIndex,
                "The one predicted movable entry must fill the wrapped gap."
            );
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
