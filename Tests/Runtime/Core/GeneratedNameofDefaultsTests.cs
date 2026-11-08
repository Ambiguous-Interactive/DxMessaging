namespace DxMessaging.Tests.Runtime.Core
{
    using DxMessaging.Core.Attributes;
    using NUnit.Framework;

    [DxAutoConstructor]
    public readonly partial struct NameofDefaultValue
    {
        public enum DefaultMode
        {
            Zero = 0,
            Other = 37,
        }

        public const int Marker = 37;
        public readonly int Required;

        [DxOptionalParameter(Expression = "nameof(Marker)")]
        public readonly string Name;

        [DxOptionalParameter(Expression = "nameof(Marker) + \"Suffix\"")]
        public readonly string Label;

        [DxOptionalParameter(Expression = "nameof(Marker) == \"Marker\" ? 7 : 256")]
        public readonly byte Small;

        [DxOptionalParameter(Expression = "nameof(DefaultMode.Other)")]
        public readonly string ModeName;

        [DxOptionalParameter(
            Expression = "nameof(Marker) == \"Marker\" ? DefaultMode.Other : DefaultMode.Zero"
        )]
        public readonly DefaultMode Mode;
    }

    [DxAutoConstructor]
    public sealed partial class NameofDefaultReference
    {
        public enum DefaultMode
        {
            Zero = 0,
            Other = 37,
        }

        public const int Marker = 37;
        public readonly int Required;

        [DxOptionalParameter(Expression = "nameof(Marker)")]
        public readonly string Name;

        [DxOptionalParameter(Expression = "nameof(Marker) + \"Suffix\"")]
        public readonly string Label;

        [DxOptionalParameter(Expression = "nameof(Marker) == \"Marker\" ? 7 : 256")]
        public readonly byte Small;

        [DxOptionalParameter(Expression = "nameof(DefaultMode.Other)")]
        public readonly string ModeName;

        [DxOptionalParameter(
            Expression = "nameof(Marker) == \"Marker\" ? DefaultMode.Other : DefaultMode.Zero"
        )]
        public readonly DefaultMode Mode;
    }

    public sealed class GeneratedNameofDefaultsTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void GeneratedNameofDefaultsPreserveConstantValues(bool referenceType)
        {
            string context = $"referenceType={referenceType}";
            if (referenceType)
            {
                NameofDefaultReference message = new(42);
                AssertDefaults(
                    message.Required,
                    message.Name,
                    message.Label,
                    message.Small,
                    message.ModeName,
                    (int)message.Mode,
                    context
                );
                NameofDefaultReference explicitValue = new(
                    99,
                    "custom",
                    "label",
                    8,
                    "mode",
                    NameofDefaultReference.DefaultMode.Zero
                );
                AssertExplicit(
                    explicitValue.Required,
                    explicitValue.Name,
                    explicitValue.Label,
                    explicitValue.Small,
                    explicitValue.ModeName,
                    (int)explicitValue.Mode,
                    context
                );
            }
            else
            {
                NameofDefaultValue message = new(42);
                AssertDefaults(
                    message.Required,
                    message.Name,
                    message.Label,
                    message.Small,
                    message.ModeName,
                    (int)message.Mode,
                    context
                );
                NameofDefaultValue explicitValue = new(
                    99,
                    "custom",
                    "label",
                    8,
                    "mode",
                    NameofDefaultValue.DefaultMode.Zero
                );
                AssertExplicit(
                    explicitValue.Required,
                    explicitValue.Name,
                    explicitValue.Label,
                    explicitValue.Small,
                    explicitValue.ModeName,
                    (int)explicitValue.Mode,
                    context
                );
            }
        }

        private static void AssertDefaults(
            int required,
            string name,
            string label,
            byte small,
            string modeName,
            int mode,
            string context
        )
        {
            Assert.That(required, Is.EqualTo(42), "The required argument is retained: " + context);
            Assert.That(name, Is.EqualTo("Marker"), "nameof keeps its symbol name: " + context);
            Assert.That(
                label,
                Is.EqualTo("MarkerSuffix"),
                "Concatenation retains the nameof value: " + context
            );
            Assert.That(
                small,
                Is.EqualTo(7),
                "Constant narrowing retains the selected branch: " + context
            );
            Assert.That(
                modeName,
                Is.EqualTo("Other"),
                "Enum member nameof remains a string: " + context
            );
            Assert.That(
                mode,
                Is.EqualTo(37),
                "Enum conditional retains its underlying value: " + context
            );
        }

        private static void AssertExplicit(
            int required,
            string name,
            string label,
            byte small,
            string modeName,
            int mode,
            string context
        )
        {
            Assert.That(
                required,
                Is.EqualTo(99),
                "The explicit required argument is retained: " + context
            );
            Assert.That(
                name,
                Is.EqualTo("custom"),
                "The explicit name replaces the default: " + context
            );
            Assert.That(
                label,
                Is.EqualTo("label"),
                "The explicit label replaces the default: " + context
            );
            Assert.That(small, Is.EqualTo(8), "The explicit byte replaces the default: " + context);
            Assert.That(
                modeName,
                Is.EqualTo("mode"),
                "The explicit mode name replaces the default: " + context
            );
            Assert.That(mode, Is.Zero, "The explicit enum replaces the default: " + context);
        }
    }
}
