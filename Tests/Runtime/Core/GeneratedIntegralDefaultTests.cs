namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using DxMessaging.Core.Attributes;
    using NUnit.Framework;

    /// <summary>Consumes generated integral endpoint defaults in Mono and IL2CPP.</summary>
    [TestFixture]
    public sealed partial class GeneratedIntegralDefaultTests
    {
        /// <summary>Runs handwritten controls before generated class and readonly-struct calls.</summary>
        [TestCaseSource(nameof(Boundaries))]
        public void OmittedIntegralDefaultsRetainEndpointValues(
            int index,
            string expected,
            string metadataType,
            string boundary
        )
        {
            string context =
                $"index={index}, expected={expected}, metadataType={metadataType}, boundary={boundary}";
            HandwrittenDefaults manual = new(1);
            Assert.That(manual.Required, Is.EqualTo(1), "Manual constructor ran: " + context);
            AssertValue(manual.Values()[index], expected, metadataType, "Manual: " + context);
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
            AssertValue(reference.Values()[index], expected, metadataType, "Class: " + context);
            AssertValue(
                value.Values()[index],
                expected,
                metadataType,
                "Readonly struct: " + context
            );
            ClassDefaults explicitReference = new(
                2,
                default(sbyte),
                default(sbyte),
                default(byte),
                default(byte),
                default(short),
                default(short),
                default(ushort),
                default(ushort),
                default(int),
                default(int),
                default(uint),
                default(uint),
                default(long),
                default(long),
                default(ulong),
                default(ulong)
            );
            StructDefaults explicitValue = new(
                2,
                default(sbyte),
                default(sbyte),
                default(byte),
                default(byte),
                default(short),
                default(short),
                default(ushort),
                default(ushort),
                default(int),
                default(int),
                default(uint),
                default(uint),
                default(long),
                default(long),
                default(ulong),
                default(ulong)
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
                "Explicit class: " + context
            );
            AssertValue(
                explicitValue.Values()[index],
                "0",
                metadataType,
                "Explicit readonly struct: " + context
            );
        }

        private static void AssertValue(
            object value,
            string expected,
            string metadataType,
            string context
        )
        {
            Assert.That(
                value.GetType().FullName,
                Is.EqualTo(metadataType),
                "Exact integral type: " + context
            );
            Assert.That(
                Convert.ToString(value, CultureInfo.InvariantCulture),
                Is.EqualTo(expected),
                "Exact integral value: " + context
            );
        }

        // SYNC: IntegralDefaultBoundaryTests.Cases uses this fixed endpoint roster.
        private static IEnumerable<TestCaseData> Boundaries()
        {
            yield return new TestCaseData(0, "-128", "System.SByte", "SByteMin").SetName(
                "GeneratedIntegralDefault(SByteMin)"
            );
            yield return new TestCaseData(1, "127", "System.SByte", "SByteMax").SetName(
                "GeneratedIntegralDefault(SByteMax)"
            );
            yield return new TestCaseData(2, "0", "System.Byte", "ByteMin").SetName(
                "GeneratedIntegralDefault(ByteMin)"
            );
            yield return new TestCaseData(3, "255", "System.Byte", "ByteMax").SetName(
                "GeneratedIntegralDefault(ByteMax)"
            );
            yield return new TestCaseData(4, "-32768", "System.Int16", "Int16Min").SetName(
                "GeneratedIntegralDefault(Int16Min)"
            );
            yield return new TestCaseData(5, "32767", "System.Int16", "Int16Max").SetName(
                "GeneratedIntegralDefault(Int16Max)"
            );
            yield return new TestCaseData(6, "0", "System.UInt16", "UInt16Min").SetName(
                "GeneratedIntegralDefault(UInt16Min)"
            );
            yield return new TestCaseData(7, "65535", "System.UInt16", "UInt16Max").SetName(
                "GeneratedIntegralDefault(UInt16Max)"
            );
            yield return new TestCaseData(8, "-2147483648", "System.Int32", "Int32Min").SetName(
                "GeneratedIntegralDefault(Int32Min)"
            );
            yield return new TestCaseData(9, "2147483647", "System.Int32", "Int32Max").SetName(
                "GeneratedIntegralDefault(Int32Max)"
            );
            yield return new TestCaseData(10, "0", "System.UInt32", "UInt32Min").SetName(
                "GeneratedIntegralDefault(UInt32Min)"
            );
            yield return new TestCaseData(11, "4294967295", "System.UInt32", "UInt32Max").SetName(
                "GeneratedIntegralDefault(UInt32Max)"
            );
            yield return new TestCaseData(
                12,
                "-9223372036854775808",
                "System.Int64",
                "Int64Min"
            ).SetName("GeneratedIntegralDefault(Int64Min)");
            yield return new TestCaseData(
                13,
                "9223372036854775807",
                "System.Int64",
                "Int64Max"
            ).SetName("GeneratedIntegralDefault(Int64Max)");
            yield return new TestCaseData(14, "0", "System.UInt64", "UInt64Min").SetName(
                "GeneratedIntegralDefault(UInt64Min)"
            );
            yield return new TestCaseData(
                15,
                "18446744073709551615",
                "System.UInt64",
                "UInt64Max"
            ).SetName("GeneratedIntegralDefault(UInt64Max)");
        }

        [DxAutoConstructor]
        private sealed partial class ClassDefaults
        {
            public readonly int Required;

            [DxOptionalParameter(sbyte.MinValue)]
            public readonly sbyte SByteMin;

            [DxOptionalParameter(sbyte.MaxValue)]
            public readonly sbyte SByteMax;

            [DxOptionalParameter(byte.MinValue)]
            public readonly byte ByteMin;

            [DxOptionalParameter(byte.MaxValue)]
            public readonly byte ByteMax;

            [DxOptionalParameter(short.MinValue)]
            public readonly short Int16Min;

            [DxOptionalParameter(short.MaxValue)]
            public readonly short Int16Max;

            [DxOptionalParameter(ushort.MinValue)]
            public readonly ushort UInt16Min;

            [DxOptionalParameter(ushort.MaxValue)]
            public readonly ushort UInt16Max;

            [DxOptionalParameter(int.MinValue)]
            public readonly int Int32Min;

            [DxOptionalParameter(int.MaxValue)]
            public readonly int Int32Max;

            [DxOptionalParameter(uint.MinValue)]
            public readonly uint UInt32Min;

            [DxOptionalParameter(uint.MaxValue)]
            public readonly uint UInt32Max;

            [DxOptionalParameter(long.MinValue)]
            public readonly long Int64Min;

            [DxOptionalParameter(long.MaxValue)]
            public readonly long Int64Max;

            [DxOptionalParameter(ulong.MinValue)]
            public readonly ulong UInt64Min;

            [DxOptionalParameter(ulong.MaxValue)]
            public readonly ulong UInt64Max;

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

            [DxOptionalParameter(sbyte.MinValue)]
            public readonly sbyte SByteMin;

            [DxOptionalParameter(sbyte.MaxValue)]
            public readonly sbyte SByteMax;

            [DxOptionalParameter(byte.MinValue)]
            public readonly byte ByteMin;

            [DxOptionalParameter(byte.MaxValue)]
            public readonly byte ByteMax;

            [DxOptionalParameter(short.MinValue)]
            public readonly short Int16Min;

            [DxOptionalParameter(short.MaxValue)]
            public readonly short Int16Max;

            [DxOptionalParameter(ushort.MinValue)]
            public readonly ushort UInt16Min;

            [DxOptionalParameter(ushort.MaxValue)]
            public readonly ushort UInt16Max;

            [DxOptionalParameter(int.MinValue)]
            public readonly int Int32Min;

            [DxOptionalParameter(int.MaxValue)]
            public readonly int Int32Max;

            [DxOptionalParameter(uint.MinValue)]
            public readonly uint UInt32Min;

            [DxOptionalParameter(uint.MaxValue)]
            public readonly uint UInt32Max;

            [DxOptionalParameter(long.MinValue)]
            public readonly long Int64Min;

            [DxOptionalParameter(long.MaxValue)]
            public readonly long Int64Max;

            [DxOptionalParameter(ulong.MinValue)]
            public readonly ulong UInt64Min;

            [DxOptionalParameter(ulong.MaxValue)]
            public readonly ulong UInt64Max;

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
            public readonly sbyte SByteMin;
            public readonly sbyte SByteMax;
            public readonly byte ByteMin;
            public readonly byte ByteMax;
            public readonly short Int16Min;
            public readonly short Int16Max;
            public readonly ushort UInt16Min;
            public readonly ushort UInt16Max;
            public readonly int Int32Min;
            public readonly int Int32Max;
            public readonly uint UInt32Min;
            public readonly uint UInt32Max;
            public readonly long Int64Min;
            public readonly long Int64Max;
            public readonly ulong UInt64Min;
            public readonly ulong UInt64Max;

            public HandwrittenDefaults(
                int required,
                sbyte SByteMin = sbyte.MinValue,
                sbyte SByteMax = sbyte.MaxValue,
                byte ByteMin = byte.MinValue,
                byte ByteMax = byte.MaxValue,
                short Int16Min = short.MinValue,
                short Int16Max = short.MaxValue,
                ushort UInt16Min = ushort.MinValue,
                ushort UInt16Max = ushort.MaxValue,
                int Int32Min = int.MinValue,
                int Int32Max = int.MaxValue,
                uint UInt32Min = uint.MinValue,
                uint UInt32Max = uint.MaxValue,
                long Int64Min = long.MinValue,
                long Int64Max = long.MaxValue,
                ulong UInt64Min = ulong.MinValue,
                ulong UInt64Max = ulong.MaxValue
            )
            {
                Required = required;
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
