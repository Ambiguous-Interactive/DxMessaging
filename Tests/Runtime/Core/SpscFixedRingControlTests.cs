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

        /// <remarks>
        /// 2026-11-13: maintenance must fence admission before observing active operations.
        /// Always stop and join workers, including when a reset-count assertion fails.
        /// </remarks>
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
                        catch (InvalidOperationException)
                        {
                            // Reset rejects concurrent operations; the owning loop retries afterward.
                        }
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
                        catch (InvalidOperationException)
                        {
                            // Reset rejects concurrent operations; the owning loop retries afterward.
                        }
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
            bool producerFinished = false;
            bool consumerFinished = false;
            try
            {
                Assert.That(activity.Wait(TimeSpan.FromSeconds(10)), Is.True);
                for (int iteration = 0; iteration < 32; ++iteration)
                {
                    int dropped = ring.Reset();
                    Assert.That(
                        dropped,
                        Is.InRange(0, ring.Capacity),
                        $"Reset iteration {iteration}."
                    );
                    Thread.Yield();
                }
            }
            finally
            {
                try
                {
                    ring.Dispose();
                }
                finally
                {
                    Interlocked.Exchange(ref stop, 1);
                    producerFinished = producer.Join(TimeSpan.FromSeconds(10));
                    consumerFinished = consumer.Join(TimeSpan.FromSeconds(10));
                }
            }
            Assert.That(producerFinished, Is.True);
            Assert.That(consumerFinished, Is.True);
            Assert.That(failures.All(error => error == null), Is.True);
            Assert.That(slots.All(item => item == null), Is.True);
            Assert.Throws<ObjectDisposedException>(() => ring.TryEnqueue(new object()));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MaintenanceWaitsForAdmittedOperationsAndRejectsNewWork(bool dispose)
        {
            using SpscFixedRingControl<object> ring = new(4);
            object payload = new();
            Assert.That(ring.TryEnqueue(payload), Is.True);
            Type type = typeof(SpscFixedRingControl<object>);
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            MethodInfo enter = type.GetMethod("EnterOperation", flags);
            MethodInfo exit = type.GetMethod("ExitOperation", flags);
            FieldInfo state = type.GetField("_state", flags);
            FieldInfo items = type.GetField("_items", flags);
            Assert.That(enter, Is.Not.Null);
            Assert.That(exit, Is.Not.Null);
            Assert.That(state, Is.Not.Null);
            Assert.That(items, Is.Not.Null);
            object[] slots = (object[])items.GetValue(ring);
            using ManualResetEventSlim finished = new(false);
            Exception failure = null;
            int dropped = -1;
            Thread maintenance = new(() =>
            {
                try
                {
                    if (dispose)
                    {
                        ring.Dispose();
                    }
                    else
                    {
                        dropped = ring.Reset();
                    }
                }
                catch (Exception error)
                {
                    failure = error;
                }
                finally
                {
                    finished.Set();
                }
            })
            {
                IsBackground = true,
            };
            // Hold the real admission boundary while maintenance closes new admission.
            enter.Invoke(ring, null);
            bool joined = false;
            bool started = false;
            try
            {
                maintenance.Start();
                started = true;
                Assert.That(
                    SpinWait.SpinUntil(
                        () => (int)state.GetValue(ring) != 0,
                        TimeSpan.FromSeconds(10)
                    ),
                    Is.True,
                    "Maintenance must close admission."
                );
                Assert.That(finished.IsSet, Is.False, "An admitted operation must finish first.");
                Assert.That(
                    slots[0],
                    Is.SameAs(payload),
                    "Maintenance must not clear active storage."
                );
                Type exceptionType = dispose
                    ? typeof(ObjectDisposedException)
                    : typeof(InvalidOperationException);
                Assert.Throws(exceptionType, () => ring.TryEnqueue(new object()));
                Assert.Throws(exceptionType, () => ring.TryDequeue(out _));
            }
            finally
            {
                exit.Invoke(ring, null);
                if (started)
                {
                    joined = maintenance.Join(TimeSpan.FromSeconds(10));
                }
            }
            Assert.That(
                joined,
                Is.True,
                "Maintenance must finish after the admitted operation exits."
            );
            Assert.That(failure, Is.Null);
            Assert.That(slots.All(item => item == null), Is.True);
            if (!dispose)
            {
                Assert.That(dropped, Is.EqualTo(1));
                Assert.That(ring.TryEnqueue(payload), Is.True, "Reset must reopen admission.");
                Assert.That(ring.TryDequeue(out object actual), Is.True);
                Assert.That(actual, Is.SameAs(payload));
            }
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
