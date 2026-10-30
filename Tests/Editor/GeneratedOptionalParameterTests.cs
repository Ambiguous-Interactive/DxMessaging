#nullable enable

namespace DxMessaging.Tests.Editor
{
    using DxMessaging.Core.Attributes;
    using NUnit.Framework;

    /// <summary>Consumes optional defaults emitted by the shipped constructor generator.</summary>
    /// <remarks>
    /// 2026-10-30: type-only validation rejected valid constant conversions with DXMSG005.
    /// A required argument forces these calls through the generated constructor.
    /// </remarks>
    [TestFixture]
    public sealed partial class GeneratedOptionalParameterTests
    {
        [TestCase(0, 7)]
        [TestCase(1, 1)]
        [TestCase(2, 255)]
        [TestCase(3, 0)]
        [TestCase(4, 7)]
        public void OmittedArgumentsUseConvertedConstants(int field, int expected)
        {
            Packet message = new(1);
            int actual = field switch
            {
                0 => message.LiteralByte,
                1 => (int)message.UnsignedLong,
                2 => message.ExpressionByte,
                3 => (int)message.Choice,
                4 => message.NullableByte.GetValueOrDefault(),
                _ => throw new System.ArgumentOutOfRangeException(nameof(field)),
            };
            Assert.That(
                actual,
                Is.EqualTo(expected),
                $"field={field}: preserve the optional constant."
            );
            Assert.That(
                message.Required,
                Is.EqualTo(1),
                "Preserve the explicit required argument."
            );
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NullableConstructorParametersAcceptNull(bool optional)
        {
            string? value = optional
                ? new OptionalNullablePacket(1, null).Value
                : new RequiredNullablePacket(1, null).Value;
            Assert.That(
                value,
                Is.Null,
                $"optional={optional}: preserve the nullable field contract."
            );
        }

        [DxAutoConstructor]
        private readonly partial struct RequiredNullablePacket
        {
            public readonly int Required;
            public readonly string? Value;
        }

        [DxAutoConstructor]
        private readonly partial struct OptionalNullablePacket
        {
            public readonly int Required;

            [DxOptionalParameter(null)]
            public readonly string? Value;
        }

        private enum Choice
        {
            None,
            Other,
        }

        [DxAutoConstructor]
        private readonly partial struct Packet
        {
            public readonly int Required;

            [DxOptionalParameter(7)]
            public readonly byte LiteralByte;

            [DxOptionalParameter(1L)]
            public readonly ulong UnsignedLong;

            [DxOptionalParameter(Expression = "255")]
            public readonly byte ExpressionByte;

            [DxOptionalParameter(0)]
            public readonly Choice Choice;

            [DxOptionalParameter(Expression = "7")]
            public readonly byte? NullableByte;
        }
    }
}
