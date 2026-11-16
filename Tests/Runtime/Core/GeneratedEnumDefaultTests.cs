namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using DxMessaging.Core.Attributes;
    using NUnit.Framework;

    /// <summary>Checks generated enum defaults with independent handwritten controls.</summary>
    [TestFixture]
    [Category("Fast")]
    public sealed partial class GeneratedEnumDefaultTests
    {
        /// <summary>Preserves nominal enum types, endpoint values and explicit zero arguments.</summary>
        [TestCaseSource(nameof(Boundaries))]
        public void OmittedEnumDefaultsPreserveIdentityAndEndpointValues(
            int index,
            string expected,
            string metadataType,
            Type enumType,
            string boundary
        )
        {
            string context =
                $"index={index}, expected={expected}, metadataType={metadataType}, enumType={enumType.FullName}, boundary={boundary}";
            HandwrittenDefaults manual = new(1);
            Assert.That(manual.Required, Is.EqualTo(1), "Manual constructor ran: " + context);
            AssertValue(
                manual.Values()[index],
                expected,
                metadataType,
                enumType,
                "Manual: " + context
            );
            ClassDefaults reference = new(1);
            StructDefaults value = new(1);
            Assert.That(
                reference.Required,
                Is.EqualTo(1),
                "Generated class constructor ran: " + context
            );
            Assert.That(
                value.Required,
                Is.EqualTo(1),
                "Generated struct constructor ran: " + context
            );
            AssertValue(
                reference.Values()[index],
                expected,
                metadataType,
                enumType,
                "Class: " + context
            );
            AssertValue(
                value.Values()[index],
                expected,
                metadataType,
                enumType,
                "Readonly struct: " + context
            );
            ClassDefaults explicitReference = new(
                2,
                default(SByteValue),
                default(SByteValue),
                default(ByteValue),
                default(ByteValue),
                default(Int16Value),
                default(Int16Value),
                default(UInt16Value),
                default(UInt16Value),
                default(Int32Value),
                default(Int32Value),
                default(UInt32Value),
                default(UInt32Value),
                default(Int64Value),
                default(Int64Value),
                default(UInt64Value),
                default(UInt64Value)
            );
            StructDefaults explicitValue = new(
                2,
                default(SByteValue),
                default(SByteValue),
                default(ByteValue),
                default(ByteValue),
                default(Int16Value),
                default(Int16Value),
                default(UInt16Value),
                default(UInt16Value),
                default(Int32Value),
                default(Int32Value),
                default(UInt32Value),
                default(UInt32Value),
                default(Int64Value),
                default(Int64Value),
                default(UInt64Value),
                default(UInt64Value)
            );
            Assert.That(
                explicitReference.Required,
                Is.EqualTo(2),
                "Explicit class constructor ran: " + context
            );
            Assert.That(
                explicitValue.Required,
                Is.EqualTo(2),
                "Explicit struct constructor ran: " + context
            );
            AssertValue(
                explicitReference.Values()[index],
                "0",
                metadataType,
                enumType,
                "Explicit class: " + context
            );
            AssertValue(
                explicitValue.Values()[index],
                "0",
                metadataType,
                enumType,
                "Explicit readonly struct: " + context
            );
        }

        private static void AssertValue(
            object value,
            string expected,
            string metadataType,
            Type enumType,
            string context
        )
        {
            Assert.That(value.GetType(), Is.EqualTo(enumType), "Exact boxed enum type: " + context);
            Type underlying = Enum.GetUnderlyingType(enumType);
            Assert.That(
                underlying.FullName,
                Is.EqualTo(metadataType),
                "Exact enum width: " + context
            );
            object number = Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture);
            Assert.That(
                number.GetType().FullName,
                Is.EqualTo(metadataType),
                "Exact boxed underlying type: " + context
            );
            Assert.That(
                Convert.ToString(number, CultureInfo.InvariantCulture),
                Is.EqualTo(expected),
                "Exact enum value: " + context
            );
        }

        // SYNC: EnumDefaultBoundaryTests.Cases uses this fixed endpoint roster.
        private static IEnumerable<TestCaseData> Boundaries()
        {
            yield return new TestCaseData(
                0,
                "-128",
                "System.SByte",
                typeof(SByteValue),
                "SByteMin"
            ).SetName("GeneratedEnumDefault(SByteMin)");
            yield return new TestCaseData(
                1,
                "127",
                "System.SByte",
                typeof(SByteValue),
                "SByteMax"
            ).SetName("GeneratedEnumDefault(SByteMax)");
            yield return new TestCaseData(
                2,
                "0",
                "System.Byte",
                typeof(ByteValue),
                "ByteMin"
            ).SetName("GeneratedEnumDefault(ByteMin)");
            yield return new TestCaseData(
                3,
                "255",
                "System.Byte",
                typeof(ByteValue),
                "ByteMax"
            ).SetName("GeneratedEnumDefault(ByteMax)");
            yield return new TestCaseData(
                4,
                "-32768",
                "System.Int16",
                typeof(Int16Value),
                "Int16Min"
            ).SetName("GeneratedEnumDefault(Int16Min)");
            yield return new TestCaseData(
                5,
                "32767",
                "System.Int16",
                typeof(Int16Value),
                "Int16Max"
            ).SetName("GeneratedEnumDefault(Int16Max)");
            yield return new TestCaseData(
                6,
                "0",
                "System.UInt16",
                typeof(UInt16Value),
                "UInt16Min"
            ).SetName("GeneratedEnumDefault(UInt16Min)");
            yield return new TestCaseData(
                7,
                "65535",
                "System.UInt16",
                typeof(UInt16Value),
                "UInt16Max"
            ).SetName("GeneratedEnumDefault(UInt16Max)");
            yield return new TestCaseData(
                8,
                "-2147483648",
                "System.Int32",
                typeof(Int32Value),
                "Int32Min"
            ).SetName("GeneratedEnumDefault(Int32Min)");
            yield return new TestCaseData(
                9,
                "2147483647",
                "System.Int32",
                typeof(Int32Value),
                "Int32Max"
            ).SetName("GeneratedEnumDefault(Int32Max)");
            yield return new TestCaseData(
                10,
                "0",
                "System.UInt32",
                typeof(UInt32Value),
                "UInt32Min"
            ).SetName("GeneratedEnumDefault(UInt32Min)");
            yield return new TestCaseData(
                11,
                "4294967295",
                "System.UInt32",
                typeof(UInt32Value),
                "UInt32Max"
            ).SetName("GeneratedEnumDefault(UInt32Max)");
            yield return new TestCaseData(
                12,
                "-9223372036854775808",
                "System.Int64",
                typeof(Int64Value),
                "Int64Min"
            ).SetName("GeneratedEnumDefault(Int64Min)");
            yield return new TestCaseData(
                13,
                "9223372036854775807",
                "System.Int64",
                typeof(Int64Value),
                "Int64Max"
            ).SetName("GeneratedEnumDefault(Int64Max)");
            yield return new TestCaseData(
                14,
                "0",
                "System.UInt64",
                typeof(UInt64Value),
                "UInt64Min"
            ).SetName("GeneratedEnumDefault(UInt64Min)");
            yield return new TestCaseData(
                15,
                "18446744073709551615",
                "System.UInt64",
                typeof(UInt64Value),
                "UInt64Max"
            ).SetName("GeneratedEnumDefault(UInt64Max)");
        }

        private enum SByteValue : sbyte
        {
            Min = -128,
            Max = 127,
        }

        private enum ByteValue : byte
        {
            Min = 0,
            Max = 255,
        }

        private enum Int16Value : short
        {
            Min = -32768,
            Max = 32767,
        }

        private enum UInt16Value : ushort
        {
            Min = 0,
            Max = 65535,
        }

        private enum Int32Value : int
        {
            Min = -2147483648,
            Max = 2147483647,
        }

        private enum UInt32Value : uint
        {
            Min = 0u,
            Max = 4294967295u,
        }

        private enum Int64Value : long
        {
            Min = -9223372036854775808L,
            Max = 9223372036854775807L,
        }

        private enum UInt64Value : ulong
        {
            Min = 0UL,
            Max = 18446744073709551615UL,
        }

        [DxAutoConstructor]
        private sealed partial class ClassDefaults
        {
            public readonly int Required;

            [DxOptionalParameter(Expression = "SByteValue.Min")]
            public readonly SByteValue SByteMin;

            [DxOptionalParameter(Expression = "SByteValue.Max")]
            public readonly SByteValue SByteMax;

            [DxOptionalParameter(Expression = "ByteValue.Min")]
            public readonly ByteValue ByteMin;

            [DxOptionalParameter(Expression = "ByteValue.Max")]
            public readonly ByteValue ByteMax;

            [DxOptionalParameter(Expression = "Int16Value.Min")]
            public readonly Int16Value Int16Min;

            [DxOptionalParameter(Expression = "Int16Value.Max")]
            public readonly Int16Value Int16Max;

            [DxOptionalParameter(Expression = "UInt16Value.Min")]
            public readonly UInt16Value UInt16Min;

            [DxOptionalParameter(Expression = "UInt16Value.Max")]
            public readonly UInt16Value UInt16Max;

            [DxOptionalParameter(Expression = "Int32Value.Min")]
            public readonly Int32Value Int32Min;

            [DxOptionalParameter(Expression = "Int32Value.Max")]
            public readonly Int32Value Int32Max;

            [DxOptionalParameter(Expression = "UInt32Value.Min")]
            public readonly UInt32Value UInt32Min;

            [DxOptionalParameter(Expression = "UInt32Value.Max")]
            public readonly UInt32Value UInt32Max;

            [DxOptionalParameter(Expression = "Int64Value.Min")]
            public readonly Int64Value Int64Min;

            [DxOptionalParameter(Expression = "Int64Value.Max")]
            public readonly Int64Value Int64Max;

            [DxOptionalParameter(Expression = "UInt64Value.Min")]
            public readonly UInt64Value UInt64Min;

            [DxOptionalParameter(Expression = "UInt64Value.Max")]
            public readonly UInt64Value UInt64Max;

            public object[] Values() =>
                new object[]
                {
                    SByteMin,
                    SByteMax,
                    ByteMin,
                    ByteMax,
                    Int16Min,
                    Int16Max,
                    UInt16Min,
                    UInt16Max,
                    Int32Min,
                    Int32Max,
                    UInt32Min,
                    UInt32Max,
                    Int64Min,
                    Int64Max,
                    UInt64Min,
                    UInt64Max,
                };
        }

        [DxAutoConstructor]
        private readonly partial struct StructDefaults
        {
            public readonly int Required;

            [DxOptionalParameter(Expression = "(SByteValue)(-128)")]
            public readonly SByteValue SByteMin;

            [DxOptionalParameter(Expression = "(SByteValue)(127)")]
            public readonly SByteValue SByteMax;

            [DxOptionalParameter(Expression = "(ByteValue)(0)")]
            public readonly ByteValue ByteMin;

            [DxOptionalParameter(Expression = "(ByteValue)(255)")]
            public readonly ByteValue ByteMax;

            [DxOptionalParameter(Expression = "(Int16Value)(-32768)")]
            public readonly Int16Value Int16Min;

            [DxOptionalParameter(Expression = "(Int16Value)(32767)")]
            public readonly Int16Value Int16Max;

            [DxOptionalParameter(Expression = "(UInt16Value)(0)")]
            public readonly UInt16Value UInt16Min;

            [DxOptionalParameter(Expression = "(UInt16Value)(65535)")]
            public readonly UInt16Value UInt16Max;

            [DxOptionalParameter(Expression = "(Int32Value)(-2147483648)")]
            public readonly Int32Value Int32Min;

            [DxOptionalParameter(Expression = "(Int32Value)(2147483647)")]
            public readonly Int32Value Int32Max;

            [DxOptionalParameter(Expression = "(UInt32Value)(0u)")]
            public readonly UInt32Value UInt32Min;

            [DxOptionalParameter(Expression = "(UInt32Value)(4294967295u)")]
            public readonly UInt32Value UInt32Max;

            [DxOptionalParameter(Expression = "(Int64Value)(-9223372036854775808L)")]
            public readonly Int64Value Int64Min;

            [DxOptionalParameter(Expression = "(Int64Value)(9223372036854775807L)")]
            public readonly Int64Value Int64Max;

            [DxOptionalParameter(Expression = "(UInt64Value)(0UL)")]
            public readonly UInt64Value UInt64Min;

            [DxOptionalParameter(Expression = "(UInt64Value)(18446744073709551615UL)")]
            public readonly UInt64Value UInt64Max;

            public object[] Values() =>
                new object[]
                {
                    SByteMin,
                    SByteMax,
                    ByteMin,
                    ByteMax,
                    Int16Min,
                    Int16Max,
                    UInt16Min,
                    UInt16Max,
                    Int32Min,
                    Int32Max,
                    UInt32Min,
                    UInt32Max,
                    Int64Min,
                    Int64Max,
                    UInt64Min,
                    UInt64Max,
                };
        }

        private readonly struct HandwrittenDefaults
        {
            public readonly int Required;
            public readonly SByteValue SByteMin;
            public readonly SByteValue SByteMax;
            public readonly ByteValue ByteMin;
            public readonly ByteValue ByteMax;
            public readonly Int16Value Int16Min;
            public readonly Int16Value Int16Max;
            public readonly UInt16Value UInt16Min;
            public readonly UInt16Value UInt16Max;
            public readonly Int32Value Int32Min;
            public readonly Int32Value Int32Max;
            public readonly UInt32Value UInt32Min;
            public readonly UInt32Value UInt32Max;
            public readonly Int64Value Int64Min;
            public readonly Int64Value Int64Max;
            public readonly UInt64Value UInt64Min;
            public readonly UInt64Value UInt64Max;

            public HandwrittenDefaults(
                int Required,
                SByteValue SByteMin = SByteValue.Min,
                SByteValue SByteMax = SByteValue.Max,
                ByteValue ByteMin = ByteValue.Min,
                ByteValue ByteMax = ByteValue.Max,
                Int16Value Int16Min = Int16Value.Min,
                Int16Value Int16Max = Int16Value.Max,
                UInt16Value UInt16Min = UInt16Value.Min,
                UInt16Value UInt16Max = UInt16Value.Max,
                Int32Value Int32Min = Int32Value.Min,
                Int32Value Int32Max = Int32Value.Max,
                UInt32Value UInt32Min = UInt32Value.Min,
                UInt32Value UInt32Max = UInt32Value.Max,
                Int64Value Int64Min = Int64Value.Min,
                Int64Value Int64Max = Int64Value.Max,
                UInt64Value UInt64Min = UInt64Value.Min,
                UInt64Value UInt64Max = UInt64Value.Max
            )
            {
                this.Required = Required;
                this.SByteMin = SByteMin;
                this.SByteMax = SByteMax;
                this.ByteMin = ByteMin;
                this.ByteMax = ByteMax;
                this.Int16Min = Int16Min;
                this.Int16Max = Int16Max;
                this.UInt16Min = UInt16Min;
                this.UInt16Max = UInt16Max;
                this.Int32Min = Int32Min;
                this.Int32Max = Int32Max;
                this.UInt32Min = UInt32Min;
                this.UInt32Max = UInt32Max;
                this.Int64Min = Int64Min;
                this.Int64Max = Int64Max;
                this.UInt64Min = UInt64Min;
                this.UInt64Max = UInt64Max;
            }

            public object[] Values() =>
                new object[]
                {
                    SByteMin,
                    SByteMax,
                    ByteMin,
                    ByteMax,
                    Int16Min,
                    Int16Max,
                    UInt16Min,
                    UInt16Max,
                    Int32Min,
                    Int32Max,
                    UInt32Min,
                    UInt32Max,
                    Int64Min,
                    Int64Max,
                    UInt64Min,
                    UInt64Max,
                };
        }
    }
}
