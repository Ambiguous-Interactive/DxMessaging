namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Core.Attributes;
    using NUnit.Framework;

    /// <summary>Consumes generated floating defaults in Mono and IL2CPP test players.</summary>
    /// <remarks>
    /// 2026-10-30: non-finite defaults produced invalid literals and double negative zero lost
    /// its sign. A required argument forces the generated constructor. NaN payload bits are
    /// outside this contract; infinities and signed zero have exact expected representations.
    /// </remarks>
    [TestFixture]
    public sealed partial class GeneratedFloatingDefaultTests
    {
        [TestCaseSource(nameof(FloatingDefaultCases))]
        public void OmittedFloatingDefaultsPreserveTheirValues(int index)
        {
            Defaults message = new(1);
            Assert.That(
                message.Required,
                Is.EqualTo(1),
                $"index={index}: keep the required argument."
            );
            if (index == 0)
            {
                Assert.That(
                    float.IsNaN(message.FloatNaN),
                    Is.True,
                    "index=0: default must be float NaN."
                );
                return;
            }
            if (index == 4)
            {
                Assert.That(
                    double.IsNaN(message.DoubleNaN),
                    Is.True,
                    "index=4: default must be double NaN."
                );
                return;
            }
            byte[] actual = index switch
            {
                1 => BitConverter.GetBytes(message.FloatPositiveInfinity),
                2 => BitConverter.GetBytes(message.FloatNegativeInfinity),
                3 => BitConverter.GetBytes(message.FloatNegativeZero),
                5 => BitConverter.GetBytes(message.DoublePositiveInfinity),
                6 => BitConverter.GetBytes(message.DoubleNegativeInfinity),
                7 => BitConverter.GetBytes(message.DoubleNegativeZero),
                _ => throw new ArgumentOutOfRangeException(nameof(index)),
            };
            byte[] expected = index switch
            {
                1 => BitConverter.GetBytes(float.PositiveInfinity),
                2 => BitConverter.GetBytes(float.NegativeInfinity),
                3 => BitConverter.GetBytes(-0.0f),
                5 => BitConverter.GetBytes(double.PositiveInfinity),
                6 => BitConverter.GetBytes(double.NegativeInfinity),
                7 => BitConverter.GetBytes(-0.0d),
                _ => throw new ArgumentOutOfRangeException(nameof(index)),
            };
            Assert.That(
                actual,
                Is.EqualTo(expected),
                $"index={index}: keep the floating-default bits."
            );
        }

        private static IEnumerable<TestCaseData> FloatingDefaultCases()
        {
            yield return new TestCaseData(0).SetName("GeneratedFloatNaNDefaultIsNaN");
            yield return new TestCaseData(1).SetName(
                "GeneratedFloatPositiveInfinityDefaultRetainsBits"
            );
            yield return new TestCaseData(2).SetName(
                "GeneratedFloatNegativeInfinityDefaultRetainsBits"
            );
            yield return new TestCaseData(3).SetName(
                "GeneratedFloatNegativeZeroDefaultRetainsBits"
            );
            yield return new TestCaseData(4).SetName("GeneratedDoubleNaNDefaultIsNaN");
            yield return new TestCaseData(5).SetName(
                "GeneratedDoublePositiveInfinityDefaultRetainsBits"
            );
            yield return new TestCaseData(6).SetName(
                "GeneratedDoubleNegativeInfinityDefaultRetainsBits"
            );
            yield return new TestCaseData(7).SetName(
                "GeneratedDoubleNegativeZeroDefaultRetainsBits"
            );
        }

        [DxAutoConstructor]
        private readonly partial struct Defaults
        {
            public readonly int Required;

            [DxOptionalParameter(float.NaN)]
            public readonly float FloatNaN;

            [DxOptionalParameter(float.PositiveInfinity)]
            public readonly float FloatPositiveInfinity;

            [DxOptionalParameter(float.NegativeInfinity)]
            public readonly float FloatNegativeInfinity;

            [DxOptionalParameter(-0.0f)]
            public readonly float FloatNegativeZero;

            [DxOptionalParameter(double.NaN)]
            public readonly double DoubleNaN;

            [DxOptionalParameter(double.PositiveInfinity)]
            public readonly double DoublePositiveInfinity;

            [DxOptionalParameter(double.NegativeInfinity)]
            public readonly double DoubleNegativeInfinity;

            [DxOptionalParameter(-0.0d)]
            public readonly double DoubleNegativeZero;
        }
    }
}
