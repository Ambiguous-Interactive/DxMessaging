#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Threading;
    using NUnit.Framework;

    /// <summary>Semantic screen for the #505 test-only SPSC ring.</summary>
    public sealed class SpscFixedRingControlTests
    {
        [TestCase(1)]
        [TestCase(4)]
        public void FullEmptyAndWrapPreserveFifo(int capacity)
        {
            using SpscFixedRingControl<int> ring = new(capacity);
            Assert.That(ring.TryDequeue(out int empty), Is.False);
            Assert.That(empty, Is.Zero);
            for (int value = 1; value <= capacity; ++value)
            {
                Assert.That(ring.TryEnqueue(value), Is.True);
            }
            Assert.That(ring.TryEnqueue(999), Is.False, "Full must not overwrite the oldest item.");
            Assert.That(ring.TryDequeue(out int oldest), Is.True);
            Assert.That(oldest, Is.EqualTo(1));
            Assert.That(ring.TryEnqueue(capacity + 1), Is.True);
            for (int value = 2; value <= capacity + 1; ++value)
            {
                Assert.That(ring.TryDequeue(out int actual), Is.True);
                Assert.That(actual, Is.EqualTo(value));
            }
            Assert.That(ring.TryDequeue(out _), Is.False);
        }

        [Test]
        public void SequenceCounterOverflowPreservesRingPosition()
        {
            using SpscFixedRingControl<int> ring = new(4);
            FieldInfo head = typeof(SpscFixedRingControl<int>).GetField(
                "_head",
                BindingFlags.NonPublic | BindingFlags.Instance
            );
            FieldInfo tail = typeof(SpscFixedRingControl<int>).GetField(
                "_tail",
                BindingFlags.NonPublic | BindingFlags.Instance
            );
            Assert.That(head, Is.Not.Null);
            Assert.That(tail, Is.Not.Null);
            head.SetValue(ring, int.MaxValue - 1);
            tail.SetValue(ring, int.MaxValue - 1);
            for (int value = 1; value <= 4; ++value)
            {
                Assert.That(ring.TryEnqueue(value), Is.True);
            }
            Assert.That(ring.TryEnqueue(5), Is.False);
            for (int value = 1; value <= 4; ++value)
            {
                Assert.That(ring.TryDequeue(out int actual), Is.True);
                Assert.That(actual, Is.EqualTo(value));
            }
            Assert.That(ring.TryDequeue(out _), Is.False);
            Assert.That(head.GetValue(ring), Is.EqualTo(int.MinValue + 2));
            Assert.That(tail.GetValue(ring), Is.EqualTo(int.MinValue + 2));
        }

        [Test]
        public void ResetAndDisposeClearReferencesAndRejectDisposedWork()
        {
            SpscFixedRingControl<object> ring = new(4);
            FieldInfo field = typeof(SpscFixedRingControl<object>).GetField(
                "_items",
                BindingFlags.NonPublic | BindingFlags.Instance
            );
            Assert.That(field, Is.Not.Null);
            object[] slots = (object[])field.GetValue(ring);
            object first = new();
            object second = new();
            Assert.That(ring.TryEnqueue(first), Is.True);
            Assert.That(ring.TryEnqueue(second), Is.True);
            Assert.That(ring.TryDequeue(out object removed), Is.True);
            Assert.That(removed, Is.SameAs(first));
            Assert.That(slots.Any(item => ReferenceEquals(item, first)), Is.False);
            Assert.That(ring.Reset(), Is.EqualTo(1));
            Assert.That(slots.All(item => item == null), Is.True);
            Assert.That(ring.TryEnqueue(first), Is.True);
            ring.Dispose();
            ring.Dispose();
            Assert.That(slots.All(item => item == null), Is.True);
            Assert.Throws<ObjectDisposedException>(() => ring.TryEnqueue(second));
            Assert.Throws<ObjectDisposedException>(() => ring.TryDequeue(out _));
            Assert.Throws<ObjectDisposedException>(() => ring.Reset());
        }

        [Test]
        public void ConcurrentProducerPublishesCompletePayloadsInOrder()
        {
            const int total = 2048;
            using SpscFixedRingControl<Payload> ring = new(8);
            using ManualResetEventSlim start = new(false);
            Payload[] observed = new Payload[total];
            Exception[] failures = new Exception[2];
            int stop = 0;
            int received = 0;
            Thread producer = new(() =>
            {
                try
                {
                    start.Wait();
                    SpinWait spin = new();
                    for (int sequence = 1; sequence <= total; ++sequence)
                    {
                        Payload item = new(sequence);
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
                    failures[0] = error;
                    Interlocked.Exchange(ref stop, 1);
                }
            })
            {
                IsBackground = true,
            };
            Thread consumer = new(() =>
            {
                try
                {
                    start.Wait();
                    SpinWait spin = new();
                    while (received < total && Volatile.Read(ref stop) == 0)
                    {
                        if (ring.TryDequeue(out Payload item))
                        {
                            observed[received++] = item;
                        }
                        else
                        {
                            spin.SpinOnce();
                        }
                    }
                }
                catch (Exception error)
                {
                    failures[1] = error;
                    Interlocked.Exchange(ref stop, 1);
                }
            })
            {
                IsBackground = true,
            };
            producer.Start();
            consumer.Start();
            start.Set();
            bool producerFinished = producer.Join(TimeSpan.FromSeconds(10));
            bool consumerFinished = consumer.Join(TimeSpan.FromSeconds(10));
            if (!producerFinished || !consumerFinished)
            {
                Interlocked.Exchange(ref stop, 1);
                producer.Join(TimeSpan.FromSeconds(1));
                consumer.Join(TimeSpan.FromSeconds(1));
            }
            Assert.That(producerFinished && consumerFinished, Is.True, "Both workers must finish.");
            Assert.That(failures.All(error => error == null), Is.True, "No worker may fail.");
            Assert.That(received, Is.EqualTo(total));
            for (int index = 0; index < total; ++index)
            {
                Assert.That(observed[index].Sequence, Is.EqualTo(index + 1));
                Assert.That(observed[index].Checksum, Is.EqualTo(Payload.Check(index + 1)));
            }
            Assert.That(ring.TryDequeue(out _), Is.False);
        }

        [Test]
        public void ConcurrentResetAndDisposalLeaveNoRetainedReferences()
        {
            SpscFixedRingControl<object> ring = new(8);
            FieldInfo field = typeof(SpscFixedRingControl<object>).GetField(
                "_items",
                BindingFlags.NonPublic | BindingFlags.Instance
            );
            Assert.That(field, Is.Not.Null);
            object[] slots = (object[])field.GetValue(ring);
            using ManualResetEventSlim start = new(false);
            using ManualResetEventSlim activity = new(false);
            Exception[] failures = new Exception[2];
            int stop = 0;
            object payload = new();
            Thread producer = new(() =>
            {
                try
                {
                    start.Wait();
                    while (Volatile.Read(ref stop) == 0)
                    {
                        try
                        {
                            if (ring.TryEnqueue(payload))
                            {
                                activity.Set();
                            }
                        }
                        catch (ObjectDisposedException)
                        {
                            return;
                        }
                        catch (InvalidOperationException) { }
                    }
                }
                catch (Exception error)
                {
                    failures[0] = error;
                }
            })
            {
                IsBackground = true,
            };
            Thread consumer = new(() =>
            {
                try
                {
                    start.Wait();
                    while (Volatile.Read(ref stop) == 0)
                    {
                        try
                        {
                            ring.TryDequeue(out _);
                        }
                        catch (ObjectDisposedException)
                        {
                            return;
                        }
                        catch (InvalidOperationException) { }
                    }
                }
                catch (Exception error)
                {
                    failures[1] = error;
                }
            })
            {
                IsBackground = true,
            };
            producer.Start();
            consumer.Start();
            start.Set();
            Assert.That(activity.Wait(TimeSpan.FromSeconds(10)), Is.True);
            for (int iteration = 0; iteration < 32; ++iteration)
            {
                Assert.That(ring.Reset(), Is.InRange(0, ring.Capacity));
                Thread.Yield();
            }
            ring.Dispose();
            Interlocked.Exchange(ref stop, 1);
            Assert.That(producer.Join(TimeSpan.FromSeconds(10)), Is.True);
            Assert.That(consumer.Join(TimeSpan.FromSeconds(10)), Is.True);
            Assert.That(failures.All(error => error == null), Is.True);
            Assert.That(slots.All(item => item == null), Is.True);
            Assert.Throws<ObjectDisposedException>(() => ring.TryEnqueue(new object()));
        }

        [Test]
        public void CapacityMustBePositivePowerOfTwo()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new SpscFixedRingControl<int>(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SpscFixedRingControl<int>(3));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SpscFixedRingControl<int>(-1));
        }

        private readonly struct Payload
        {
            internal Payload(int sequence)
            {
                Sequence = sequence;
                Checksum = Check(sequence);
            }

            internal int Sequence { get; }

            internal long Checksum { get; }

            internal static long Check(int sequence) => (long)sequence * 123456789 + 987654321;
        }
    }
}
#endif
