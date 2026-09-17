#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using NUnit.Framework;

    /// <summary>Semantic checks for the #505 managed reference-command payload control.</summary>
    public sealed class ReferenceCommandQueueControlTests
    {
        [Test]
        public void BudgetOverflowAndWrapPreserveCommandOrder()
        {
            using ReferenceCommandQueueControl queue = new(2);
            List<int> calls = new();
            Assert.That(queue.TryEnqueue(() => calls.Add(1)), Is.True);
            Assert.That(queue.TryEnqueue(() => calls.Add(2)), Is.True);
            Assert.That(queue.TryEnqueue(() => calls.Add(99)), Is.False);
            Assert.That(queue.Drain(1), Is.EqualTo(1));
            Assert.That(calls, Is.EqualTo(new[] { 1 }));
            Assert.That(queue.Count, Is.EqualTo(1));
            Assert.That(queue.TryEnqueue(() => calls.Add(3)), Is.True);
            Assert.That(queue.Drain(2), Is.EqualTo(2));
            Assert.That(calls, Is.EqualTo(new[] { 1, 2, 3 }));
            Assert.That(queue.Drain(2), Is.Zero);
        }

        [Test]
        public void FirstCallbackFailureConsumesPrefixAndLeavesLaterWork()
        {
            using ReferenceCommandQueueControl queue = new(3);
            List<int> calls = new();
            queue.TryEnqueue(() => calls.Add(1));
            queue.TryEnqueue(() =>
            {
                calls.Add(2);
                throw new InvalidOperationException("expected callback failure");
            });
            queue.TryEnqueue(() => calls.Add(3));
            Assert.Throws<InvalidOperationException>(() => queue.Drain(3));
            Assert.That(calls, Is.EqualTo(new[] { 1, 2 }));
            Assert.That(queue.Count, Is.EqualTo(1));
            Assert.That(queue.Drain(3), Is.EqualTo(1));
            Assert.That(calls, Is.EqualTo(new[] { 1, 2, 3 }));
        }

        [Test]
        public void CommandObservesStateAtDrainAndCanEnqueueNewWork()
        {
            using ReferenceCommandQueueControl queue = new(2);
            List<int> calls = new();
            int value = 1;
            queue.TryEnqueue(() =>
            {
                calls.Add(value);
                Assert.That(queue.TryEnqueue(() => calls.Add(3)), Is.True);
            });
            value = 2;
            Assert.That(queue.Drain(1), Is.EqualTo(1));
            Assert.That(calls, Is.EqualTo(new[] { 2 }));
            Assert.That(queue.Count, Is.EqualTo(1));
            Assert.That(queue.Drain(1), Is.EqualTo(1));
            Assert.That(calls, Is.EqualTo(new[] { 2, 3 }));
        }

        [Test]
        public void DrainResetAndDisposeClearCapturedDelegates()
        {
            ReferenceCommandQueueControl queue = new(2);
            FieldInfo ringField = typeof(ReferenceCommandQueueControl).GetField(
                "_ring",
                BindingFlags.NonPublic | BindingFlags.Instance
            );
            Assert.That(ringField, Is.Not.Null);
            object ring = ringField.GetValue(queue);
            FieldInfo slotsField = ring.GetType()
                .GetField("_items", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(slotsField, Is.Not.Null);
            Action[] slots = (Action[])slotsField.GetValue(ring);
            Holder holder = new();
            Assert.That(queue.TryEnqueue(holder.Invoke), Is.True);
            Assert.That(slots.Any(command => command?.Target == holder), Is.True);
            Assert.That(queue.Drain(1), Is.EqualTo(1));
            Assert.That(holder.Calls, Is.EqualTo(1));
            Assert.That(slots.All(command => command == null), Is.True);
            Assert.That(queue.TryEnqueue(holder.Invoke), Is.True);
            Assert.That(queue.Reset(), Is.EqualTo(1));
            Assert.That(holder.Calls, Is.EqualTo(1));
            Assert.That(slots.All(command => command == null), Is.True);
            Assert.That(queue.TryEnqueue(holder.Invoke), Is.True);
            queue.Dispose();
            queue.Dispose();
            Assert.That(slots.All(command => command == null), Is.True);
            Assert.Throws<ObjectDisposedException>(() => queue.TryEnqueue(holder.Invoke));
            Assert.Throws<ObjectDisposedException>(() => queue.Drain(1));
        }

        [Test]
        public void NullCommandAndInvalidBudgetAreRejected()
        {
            using ReferenceCommandQueueControl queue = new(1);
            Assert.Throws<ArgumentNullException>(() => queue.TryEnqueue(null));
            Assert.Throws<ArgumentOutOfRangeException>(() => queue.Drain(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => queue.Drain(-1));
        }

        private sealed class Holder
        {
            internal int Calls { get; private set; }

            internal void Invoke() => ++Calls;
        }
    }
}
#endif
