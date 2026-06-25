#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons
{
    using NUnit.Framework;

    /// <summary>
    /// Unit coverage for <see cref="ComparisonHarness.DescribeFanOutDelta"/>: the fan-out
    /// mismatch diagnostic must NOT silently truncate via integer division. A delta that is a
    /// whole number of operations reads cleanly; a delta that is NOT a whole number (a real
    /// signal of inconsistent fan-out) must surface the leftover invocations so the message
    /// stays arithmetically exact and the defect is visible. These are pure-function tests
    /// (no measurement window), so they are plain fast tests, not PerfBench.
    /// </summary>
    public sealed class ComparisonHarnessDeltaDiagnosticTests
    {
        [Test]
        public void ExactMultipleReadsAsWholeOps()
        {
            Assert.AreEqual(
                "40000 invocations = 10000 ops",
                ComparisonHarness.DescribeFanOutDelta(40000, 4)
            );
        }

        [Test]
        public void ZeroDeltaReadsAsZeroOps()
        {
            Assert.AreEqual("0 invocations = 0 ops", ComparisonHarness.DescribeFanOutDelta(0, 4));
        }

        [Test]
        public void NonIntegralDeltaSurfacesLeftoverAndFlagsDefect()
        {
            // 40001 = 10000 ops * 4 + 1 leftover; the old truncating "= 10000 ops" hid the +1.
            string message = ComparisonHarness.DescribeFanOutDelta(40001, 4);
            StringAssert.Contains("40001 invocations = 10000 ops + 1 leftover", message);
            StringAssert.Contains("NON-INTEGRAL", message);
        }

        [Test]
        public void NonIntegralDeltaIsArithmeticallyExact()
        {
            // The "N ops + R leftover" form must reconstruct the raw delta: N * ipo + R == delta.
            const long ipo = 16;
            const long delta = 160007;
            long ops = delta / ipo;
            long remainder = delta % ipo;
            Assert.AreEqual(delta, (ops * ipo) + remainder, "Sanity: the division identity holds.");
            string message = ComparisonHarness.DescribeFanOutDelta(delta, ipo);
            StringAssert.Contains(
                $"{delta} invocations = {ops} ops + {remainder} leftover",
                message
            );
        }

        [Test]
        public void NegativeDeltaStaysExact()
        {
            // A dropped-message defect yields observed < expected (a negative delta). The message
            // must stay readable while preserving exact arithmetic:
            // (-5 / 4) * 4 + (-5 % 4) = (-1 * 4) + -1 = -5.
            string message = ComparisonHarness.DescribeFanOutDelta(-5, 4);
            StringAssert.Contains("-5 invocations = -1 ops - 1 leftover", message);
        }

        [Test]
        public void NonPositivePerOperationReportsRawDelta()
        {
            Assert.AreEqual(
                "7 invocations (invocationsPerOperation=0)",
                ComparisonHarness.DescribeFanOutDelta(7, 0)
            );
        }
    }
}
#endif
