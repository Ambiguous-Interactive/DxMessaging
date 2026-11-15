namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
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

        /// <summary>Checks native compiler controls before reading generated finite defaults.</summary>
        [TestCaseSource(nameof(FiniteDefaultCases))]
        public void FiniteDefaultsMatchHandwrittenConstructorBits(
            int index,
            string expectedBits,
            string boundary
        )
        {
            string context = $"index={index}, expectedBits={expectedBits}, boundary={boundary}";
            HandwrittenFiniteDefaults manual = new(1);
            Assert.That(manual.Required, Is.EqualTo(1), "Manual constructor ran: " + context);
            string manualBits = index switch
            {
                0 => Bits(manual.FloatSmallestSubnormal),
                1 => Bits(manual.FloatLargestSubnormal),
                2 => Bits(manual.FloatSmallestNormal),
                3 => Bits(manual.FloatLargestFinite),
                4 => Bits(manual.FloatNegativeSmallestSubnormal),
                5 => Bits(manual.FloatNegativeLargestSubnormal),
                6 => Bits(manual.FloatNegativeSmallestNormal),
                7 => Bits(manual.FloatNegativeLargestFinite),
                8 => Bits(manual.DoubleSmallestSubnormal),
                9 => Bits(manual.DoubleLargestSubnormal),
                10 => Bits(manual.DoubleSmallestNormal),
                11 => Bits(manual.DoubleLargestFinite),
                12 => Bits(manual.DoubleNegativeSmallestSubnormal),
                13 => Bits(manual.DoubleNegativeLargestSubnormal),
                14 => Bits(manual.DoubleNegativeSmallestNormal),
                15 => Bits(manual.DoubleNegativeLargestFinite),
                _ => throw new ArgumentOutOfRangeException(nameof(index)),
            };
            Assert.That(manualBits, Is.EqualTo(expectedBits), "Manual native control: " + context);

            Defaults generated = new(1);
            Assert.That(generated.Required, Is.EqualTo(1), "Generated constructor ran: " + context);
            string generatedBits = index switch
            {
                0 => Bits(generated.FloatSmallestSubnormal),
                1 => Bits(generated.FloatLargestSubnormal),
                2 => Bits(generated.FloatSmallestNormal),
                3 => Bits(generated.FloatLargestFinite),
                4 => Bits(generated.FloatNegativeSmallestSubnormal),
                5 => Bits(generated.FloatNegativeLargestSubnormal),
                6 => Bits(generated.FloatNegativeSmallestNormal),
                7 => Bits(generated.FloatNegativeLargestFinite),
                8 => Bits(generated.DoubleSmallestSubnormal),
                9 => Bits(generated.DoubleLargestSubnormal),
                10 => Bits(generated.DoubleSmallestNormal),
                11 => Bits(generated.DoubleLargestFinite),
                12 => Bits(generated.DoubleNegativeSmallestSubnormal),
                13 => Bits(generated.DoubleNegativeLargestSubnormal),
                14 => Bits(generated.DoubleNegativeSmallestNormal),
                15 => Bits(generated.DoubleNegativeLargestFinite),
                _ => throw new ArgumentOutOfRangeException(nameof(index)),
            };
            Assert.That(
                generatedBits,
                Is.EqualTo(expectedBits),
                "Generated native value: " + context
            );
        }

        private static string Bits(float value) =>
            BitConverter
                .ToInt32(BitConverter.GetBytes(value), 0)
                .ToString("X8", CultureInfo.InvariantCulture);

        private static string Bits(double value) =>
            BitConverter
                .ToInt64(BitConverter.GetBytes(value), 0)
                .ToString("X16", CultureInfo.InvariantCulture);

        // SYNC: FiniteFloatingDefaultBoundaryTests.Cases uses the same fixed boundary roster.
        private static IEnumerable<TestCaseData> FiniteDefaultCases()
        {
            (string Label, string Bits)[] values =
            {
                ("FloatSmallestSubnormal", "00000001"),
                ("FloatLargestSubnormal", "007FFFFF"),
                ("FloatSmallestNormal", "00800000"),
                ("FloatLargestFinite", "7F7FFFFF"),
                ("FloatNegativeSmallestSubnormal", "80000001"),
                ("FloatNegativeLargestSubnormal", "807FFFFF"),
                ("FloatNegativeSmallestNormal", "80800000"),
                ("FloatNegativeLargestFinite", "FF7FFFFF"),
                ("DoubleSmallestSubnormal", "0000000000000001"),
                ("DoubleLargestSubnormal", "000FFFFFFFFFFFFF"),
                ("DoubleSmallestNormal", "0010000000000000"),
                ("DoubleLargestFinite", "7FEFFFFFFFFFFFFF"),
                ("DoubleNegativeSmallestSubnormal", "8000000000000001"),
                ("DoubleNegativeLargestSubnormal", "800FFFFFFFFFFFFF"),
                ("DoubleNegativeSmallestNormal", "8010000000000000"),
                // cspell:ignore FFEFFFFFFFFFFFFF
                ("DoubleNegativeLargestFinite", "FFEFFFFFFFFFFFFF"),
            };
            for (int index = 0; index < values.Length; ++index)
            {
                (string label, string bits) = values[index];
                yield return new TestCaseData(index, bits, label).SetName(
                    $"GeneratedFinite{label}DefaultRetainsBits"
                );
            }
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

        /// <summary>Retains an independent compiler-built constructor before generated reads.</summary>
        private readonly struct HandwrittenFiniteDefaults
        {
            public readonly int Required;
            public readonly float FloatSmallestSubnormal;
            public readonly float FloatLargestSubnormal;
            public readonly float FloatSmallestNormal;
            public readonly float FloatLargestFinite;
            public readonly float FloatNegativeSmallestSubnormal;
            public readonly float FloatNegativeLargestSubnormal;
            public readonly float FloatNegativeSmallestNormal;
            public readonly float FloatNegativeLargestFinite;
            public readonly double DoubleSmallestSubnormal;
            public readonly double DoubleLargestSubnormal;
            public readonly double DoubleSmallestNormal;
            public readonly double DoubleLargestFinite;
            public readonly double DoubleNegativeSmallestSubnormal;
            public readonly double DoubleNegativeLargestSubnormal;
            public readonly double DoubleNegativeSmallestNormal;
            public readonly double DoubleNegativeLargestFinite;

            public HandwrittenFiniteDefaults(
                int required,
                float floatSmallestSubnormal = float.Epsilon,
                float floatLargestSubnormal = 1.1754942E-38f,
                float floatSmallestNormal = 1.17549435E-38f,
                float floatLargestFinite = float.MaxValue,
                float floatNegativeSmallestSubnormal = -float.Epsilon,
                float floatNegativeLargestSubnormal = -1.1754942E-38f,
                float floatNegativeSmallestNormal = -1.17549435E-38f,
                float floatNegativeLargestFinite = float.MinValue,
                double doubleSmallestSubnormal = double.Epsilon,
                double doubleLargestSubnormal = 2.2250738585072009E-308d,
                double doubleSmallestNormal = 2.2250738585072014E-308d,
                double doubleLargestFinite = double.MaxValue,
                double doubleNegativeSmallestSubnormal = -double.Epsilon,
                double doubleNegativeLargestSubnormal = -2.2250738585072009E-308d,
                double doubleNegativeSmallestNormal = -2.2250738585072014E-308d,
                double doubleNegativeLargestFinite = double.MinValue
            )
            {
                Required = required;
                FloatSmallestSubnormal = floatSmallestSubnormal;
                FloatLargestSubnormal = floatLargestSubnormal;
                FloatSmallestNormal = floatSmallestNormal;
                FloatLargestFinite = floatLargestFinite;
                FloatNegativeSmallestSubnormal = floatNegativeSmallestSubnormal;
                FloatNegativeLargestSubnormal = floatNegativeLargestSubnormal;
                FloatNegativeSmallestNormal = floatNegativeSmallestNormal;
                FloatNegativeLargestFinite = floatNegativeLargestFinite;
                DoubleSmallestSubnormal = doubleSmallestSubnormal;
                DoubleLargestSubnormal = doubleLargestSubnormal;
                DoubleSmallestNormal = doubleSmallestNormal;
                DoubleLargestFinite = doubleLargestFinite;
                DoubleNegativeSmallestSubnormal = doubleNegativeSmallestSubnormal;
                DoubleNegativeLargestSubnormal = doubleNegativeLargestSubnormal;
                DoubleNegativeSmallestNormal = doubleNegativeSmallestNormal;
                DoubleNegativeLargestFinite = doubleNegativeLargestFinite;
            }
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

            [DxOptionalParameter(float.Epsilon)]
            public readonly float FloatSmallestSubnormal;

            [DxOptionalParameter(1.1754942E-38f)]
            public readonly float FloatLargestSubnormal;

            [DxOptionalParameter(1.17549435E-38f)]
            public readonly float FloatSmallestNormal;

            [DxOptionalParameter(float.MaxValue)]
            public readonly float FloatLargestFinite;

            [DxOptionalParameter(-float.Epsilon)]
            public readonly float FloatNegativeSmallestSubnormal;

            [DxOptionalParameter(-1.1754942E-38f)]
            public readonly float FloatNegativeLargestSubnormal;

            [DxOptionalParameter(-1.17549435E-38f)]
            public readonly float FloatNegativeSmallestNormal;

            [DxOptionalParameter(float.MinValue)]
            public readonly float FloatNegativeLargestFinite;

            [DxOptionalParameter(double.Epsilon)]
            public readonly double DoubleSmallestSubnormal;

            [DxOptionalParameter(2.2250738585072009E-308d)]
            public readonly double DoubleLargestSubnormal;

            [DxOptionalParameter(2.2250738585072014E-308d)]
            public readonly double DoubleSmallestNormal;

            [DxOptionalParameter(double.MaxValue)]
            public readonly double DoubleLargestFinite;

            [DxOptionalParameter(-double.Epsilon)]
            public readonly double DoubleNegativeSmallestSubnormal;

            [DxOptionalParameter(-2.2250738585072009E-308d)]
            public readonly double DoubleNegativeLargestSubnormal;

            [DxOptionalParameter(-2.2250738585072014E-308d)]
            public readonly double DoubleNegativeSmallestNormal;

            [DxOptionalParameter(double.MinValue)]
            public readonly double DoubleNegativeLargestFinite;
        }
    }
}
