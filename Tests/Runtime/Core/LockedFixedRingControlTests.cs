#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Threading;
    using NUnit.Framework;

    /// <summary>Semantic controls for the #505 lock-plus-fixed-ring laboratory baseline.</summary>
    public sealed class LockedFixedRingControlTests
    {
        [TestCase(1)]
        [TestCase(3)]
        public void FullEmptyAndWrapPreserveFifoWithoutOverwrite(int capacity)
        {
            using LockedFixedRingControl<int> ring = new(capacity);
            Assert.That(ring.TryDequeue(out int empty), Is.False, "A new ring must be empty.");
            Assert.That(empty, Is.Zero, "An empty dequeue must return the default value.");
            for (int value = 1; value <= capacity; ++value)
            {
                Assert.That(ring.TryEnqueue(value), Is.True, $"Enqueue {value} must fit.");
            }
            Assert.That(
                ring.TryEnqueue(999),
                Is.False,
                "Full must return failure without overwrite."
            );
            Assert.That(ring.Count, Is.EqualTo(capacity), "Overflow must not change the count.");
            Assert.That(ring.TryDequeue(out int first), Is.True, "The oldest item must dequeue.");
            Assert.That(first, Is.EqualTo(1), "Full must preserve the oldest item.");
            Assert.That(
                ring.TryEnqueue(capacity + 1),
                Is.True,
                "A free slot must wrap and accept."
            );
            for (int value = 2; value <= capacity + 1; ++value)
            {
                Assert.That(
                    ring.TryDequeue(out int actual),
                    Is.True,
                    $"Dequeue {value} must exist."
                );
                Assert.That(actual, Is.EqualTo(value), "Wrap must preserve FIFO order.");
            }
            Assert.That(ring.Count, Is.Zero, "The ring must be empty after draining.");
            Assert.That(ring.TryDequeue(out _), Is.False, "Draining must not duplicate an item.");
        }

        [Test]
        public void ResetAndDisposeClearOwnedReferences()
        {
            LockedFixedRingControl<object> ring = new(2);
            FieldInfo field = typeof(LockedFixedRingControl<object>).GetField(
                "_items",
                BindingFlags.NonPublic | BindingFlags.Instance
            );
            Assert.That(field, Is.Not.Null, "The test must inspect the actual payload slots.");
            object[] slots = (object[])field.GetValue(ring);
            object first = new();
            object second = new();
            Assert.That(ring.TryEnqueue(first), Is.True, "First reference must enqueue.");
            Assert.That(ring.TryEnqueue(second), Is.True, "Second reference must enqueue.");
            Assert.That(
                ring.TryDequeue(out object removed),
                Is.True,
                "First reference must dequeue."
            );
            Assert.That(removed, Is.SameAs(first), "FIFO must return the original reference.");
            Assert.That(
                slots.Any(item => ReferenceEquals(item, first)),
                Is.False,
                "Dequeue must clear the consumed slot."
            );
            Assert.That(ring.Reset(), Is.EqualTo(1), "Reset must report the one dropped item.");
            Assert.That(
                slots.All(item => item == null),
                Is.True,
                "Reset must clear every retained reference."
            );
            Assert.That(ring.TryEnqueue(first), Is.True, "Reset must allow a new enqueue.");
            ring.Dispose();
            ring.Dispose();
            Assert.That(
                slots.All(item => item == null),
                Is.True,
                "Disposal must clear every retained reference."
            );
            Assert.Throws<ObjectDisposedException>(() => ring.TryEnqueue(second));
            Assert.Throws<ObjectDisposedException>(() => ring.TryDequeue(out _));
            Assert.Throws<ObjectDisposedException>(() => ring.Reset());
            Assert.Throws<ObjectDisposedException>(() => _ = ring.Count);
        }

        [Test]
        public void ConcurrentProducersDrainEachItemOnceAndRetainProducerOrder()
        {
            const int producers = 4;
            const int itemsPerProducer = 64;
            using LockedFixedRingControl<Item> ring = new(producers * itemsPerProducer);
            using ManualResetEventSlim start = new(false);
            Thread[] threads = new Thread[producers];
            Exception[] failures = new Exception[producers];
            for (int producer = 0; producer < producers; ++producer)
            {
                int owner = producer;
                threads[producer] = new Thread(() =>
                {
                    try
                    {
                        start.Wait();
                        for (int index = 0; index < itemsPerProducer; ++index)
                        {
                            if (!ring.TryEnqueue(new Item(owner, index)))
                            {
                                throw new InvalidOperationException(
                                    "Capacity was lost before all producers finished."
                                );
                            }
                        }
                    }
                    catch (Exception error)
                    {
                        failures[owner] = error;
                    }
                })
                {
                    IsBackground = true,
                };
                threads[producer].Start();
            }
            start.Set();
            foreach (Thread thread in threads)
            {
                Assert.That(
                    thread.Join(TimeSpan.FromSeconds(10)),
                    Is.True,
                    "Every producer must finish."
                );
            }
            Assert.That(failures.All(error => error == null), Is.True, "No producer may fail.");
            Assert.That(
                ring.Count,
                Is.EqualTo(producers * itemsPerProducer),
                "All values must be published."
            );
            int[] next = new int[producers];
            HashSet<int> seen = new();
            for (int drained = 0; drained < producers * itemsPerProducer; ++drained)
            {
                Assert.That(
                    ring.TryDequeue(out Item item),
                    Is.True,
                    "Every accepted item must drain."
                );
                Assert.That(
                    item.Index,
                    Is.EqualTo(next[item.Producer]++),
                    "Each producer's FIFO order must survive interleaving."
                );
                Assert.That(
                    seen.Add(item.Producer * itemsPerProducer + item.Index),
                    Is.True,
                    "An item must appear once."
                );
            }
            Assert.That(
                seen.Count,
                Is.EqualTo(producers * itemsPerProducer),
                "No item may disappear."
            );
            Assert.That(
                ring.TryDequeue(out _),
                Is.False,
                "The final drain must leave the ring empty."
            );
        }

        [Test]
        public void ConcurrentProducerAndConsumerPreserveEveryItemAcrossWrap()
        {
            const int producers = 4;
            const int itemsPerProducer = 128;
            const int total = producers * itemsPerProducer;
            using LockedFixedRingControl<Item> ring = new(8);
            using ManualResetEventSlim start = new(false);
            Item[] observed = new Item[total];
            Exception[] failures = new Exception[producers + 1];
            Thread[] threads = new Thread[producers + 1];
            int stop = 0;
            int drained = 0;

            for (int producer = 0; producer < producers; ++producer)
            {
                int owner = producer;
                threads[producer] = new Thread(() =>
                {
                    try
                    {
                        start.Wait();
                        SpinWait spin = new();
                        for (int index = 0; index < itemsPerProducer; ++index)
                        {
                            Item item = new(owner, index);
                            while (!ring.TryEnqueue(item))
                            {
                                if (Volatile.Read(ref stop) != 0)
                                {
                                    return;
                                }
                                spin.SpinOnce();
                            }
                        }
                    }
                    catch (Exception error)
                    {
                        failures[owner] = error;
                        Interlocked.Exchange(ref stop, 1);
                    }
                })
                {
                    IsBackground = true,
                };
                threads[producer].Start();
            }

            threads[producers] = new Thread(() =>
            {
                try
                {
                    start.Wait();
                    SpinWait spin = new();
                    while (drained < total && Volatile.Read(ref stop) == 0)
                    {
                        if (ring.TryDequeue(out Item item))
                        {
                            observed[drained++] = item;
                        }
                        else
                        {
                            spin.SpinOnce();
                        }
                    }
                }
                catch (Exception error)
                {
                    failures[producers] = error;
                    Interlocked.Exchange(ref stop, 1);
                }
            })
            {
                IsBackground = true,
            };
            threads[producers].Start();
            start.Set();

            bool allFinished = true;
            foreach (Thread thread in threads)
            {
                if (!thread.Join(TimeSpan.FromSeconds(10)))
                {
                    allFinished = false;
                    Interlocked.Exchange(ref stop, 1);
                }
            }
            foreach (Thread thread in threads)
            {
                if (thread.IsAlive)
                {
                    thread.Join(TimeSpan.FromSeconds(1));
                }
            }
            Assert.That(allFinished, Is.True, "All producer and consumer workers must finish.");
            Assert.That(failures.All(error => error == null), Is.True, "No worker may fail.");
            Assert.That(drained, Is.EqualTo(total), "The consumer must observe every item.");

            int[] next = new int[producers];
            HashSet<int> seen = new();
            foreach (Item item in observed)
            {
                Assert.That(item.Producer, Is.InRange(0, producers - 1));
                Assert.That(item.Index, Is.EqualTo(next[item.Producer]++));
                Assert.That(seen.Add(item.Producer * itemsPerProducer + item.Index), Is.True);
            }
            Assert.That(seen.Count, Is.EqualTo(total), "Every item must appear exactly once.");
            Assert.That(ring.Count, Is.Zero, "The consumer must leave no retained items.");
        }

        [Test]
        public void InvalidCapacityIsRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new LockedFixedRingControl<int>(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new LockedFixedRingControl<int>(-1));
        }

        private readonly struct Item
        {
            internal Item(int producer, int index)
            {
                Producer = producer;
                Index = index;
            }

            internal int Producer { get; }

            internal int Index { get; }
        }
    }
}
#endif
