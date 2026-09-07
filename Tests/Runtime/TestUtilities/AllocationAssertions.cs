#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime
{
    using System;
    using NUnit.Framework;
    using NUnit.Framework.Constraints;
    using NUnit.Framework.Internal;
    using UnityEngine.TestTools.Constraints;
    using Is = NUnit.Framework.Is;

    /// <summary>
    /// Centralizes the warm-up + assert pattern that previously lived inline in
    /// every benchmark. Keeping it in one place ensures each call site uses the
    /// same warmup and measured-iteration counts so a regression in one path
    /// shows the same way as a regression in any other.
    /// </summary>
    public static class AllocationAssertions
    {
        public const int DefaultWarmupIterations = 8;
        public const int DefaultMeasuredIterations = 32;

        /// <summary>
        /// Runs <paramref name="action"/> a handful of times to JIT it, then
        /// asserts that running it <paramref name="measuredIterations"/> more
        /// times makes zero managed allocations. Both the inner action and the
        /// outer assertion lambda are warmed before measurement so first-call
        /// JIT overhead does not pollute the result. Optional failure context is rendered
        /// once, after recording stops, without repeating the measured action.
        /// </summary>
        public static void AssertNoAllocations(
            string label,
            Action action,
            int warmupIterations = DefaultWarmupIterations,
            int measuredIterations = DefaultMeasuredIterations,
            Func<string> failureContext = null
        )
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (warmupIterations < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(warmupIterations));
            }

            if (measuredIterations <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(measuredIterations));
            }

            for (int i = 0; i < warmupIterations; ++i)
            {
                action();
            }

            TestDelegate lambdaUnderTest = () =>
            {
                for (int i = 0; i < measuredIterations; ++i)
                {
                    action();
                }
            };

            // Warm the wrapper lambda itself once so the first invocation's
            // delegate-creation / JIT cost does not show up inside the
            // AllocatingGCMemoryConstraint measurement below.
            lambdaUnderTest();

            ConstraintResult measurement = new AllocatingGCMemoryConstraint().ApplyTo(
                lambdaUnderTest
            );
            if (measurement.IsSuccess)
            {
                // Negating the constraint hides its allocation count in NUnit's failure
                // message. Render the original result after the recorder has stopped.
                TextMessageWriter writer = new TextMessageWriter();
                measurement.WriteMessageTo(writer);
                if (failureContext != null)
                {
                    writer.WriteLine(failureContext());
                }
                Assert.Fail(
                    $"{label}: expected zero GC allocations across {measuredIterations} iterations.\n{writer}"
                );
            }

            Assert.That(measurement.IsSuccess, Is.False, label);
        }
    }
}
#endif
