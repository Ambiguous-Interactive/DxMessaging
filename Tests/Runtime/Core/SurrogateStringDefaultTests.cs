namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Globalization;
    using DxMessaging.Core.Attributes;
    using NUnit.Framework;

    // cspell:ignore DFFF FFFD
    /// <summary>
    /// Records whether isolated UTF-16 surrogates survive string literals and generated defaults
    /// in each scripting backend. Unity IL2CPP replaces unpaired surrogates in literals with
    /// U+FFFD; runtime construction from a character array preserves them. The runtime controls
    /// keep the workaround covered on every backend.
    /// </summary>
    [TestFixture]
    public sealed partial class SurrogateStringDefaultTests
    {
        private const string HighSurrogate = "\uD800";
        private const string LowSurrogate = "\uDFFF";

#if ENABLE_IL2CPP
        // IL2CPP stores literals in metadata and replaces unpaired surrogates with U+FFFD.
        private const string LiteralHighSurrogateUnits = "FFFD";
        private const string LiteralLowSurrogateUnits = "FFFD";
#else
        private const string LiteralHighSurrogateUnits = "D800";
        private const string LiteralLowSurrogateUnits = "DFFF";
#endif

        [Test]
        public void StringLiteralHighSurrogateUsesBackendUnits()
        {
            AssertUnits("literal-high", HighSurrogate, LiteralHighSurrogateUnits);
        }

        [Test]
        public void StringLiteralLowSurrogateUsesBackendUnits()
        {
            AssertUnits("literal-low", LowSurrogate, LiteralLowSurrogateUnits);
        }

        [Test]
        public void StringRuntimeHighSurrogateRetainsUtf16Unit()
        {
            AssertUnits("runtime-high", new string(new[] { '\uD800' }), "D800");
        }

        [Test]
        public void StringRuntimeLowSurrogateRetainsUtf16Unit()
        {
            AssertUnits("runtime-low", new string(new[] { '\uDFFF' }), "DFFF");
        }

        [Test]
        public void GeneratedStringHighSurrogateDefaultUsesBackendUnits()
        {
            GeneratedDefaults defaults = new(1);
            AssertUnits("generated-high", defaults.High, LiteralHighSurrogateUnits);
        }

        [Test]
        public void GeneratedStringLowSurrogateDefaultUsesBackendUnits()
        {
            GeneratedDefaults defaults = new(1);
            AssertUnits("generated-low", defaults.Low, LiteralLowSurrogateUnits);
        }

        [Test]
        public void HandwrittenStringHighSurrogateDefaultUsesBackendUnits()
        {
            HandwrittenDefaults defaults = new(1);
            AssertUnits("handwritten-high", defaults.High, LiteralHighSurrogateUnits);
        }

        [Test]
        public void HandwrittenStringLowSurrogateDefaultUsesBackendUnits()
        {
            HandwrittenDefaults defaults = new(1);
            AssertUnits("handwritten-low", defaults.Low, LiteralLowSurrogateUnits);
        }

        [Test]
        public void StringSupplementaryPairRetainsUtf16Units()
        {
            AssertUnits("supplementary", "\uD83D\uDE80", "D83D-DE80");
        }

        [Test]
        public void CharHighSurrogateRetainsUtf16Unit()
        {
            AssertUnits("char-high", '\uD800', "D800");
        }

        [Test]
        public void CharLowSurrogateRetainsUtf16Unit()
        {
            AssertUnits("char-low", '\uDFFF', "DFFF");
        }

        private static void AssertUnits(string label, object value, string expectedUnits)
        {
            string actualUnits = Units(value);
            string message = $"Isolated surrogate observation: label={label}, expected={expectedUnits}, actual={actualUnits}";
            Console.WriteLine(message);
            Assert.That(actualUnits, Is.EqualTo(expectedUnits), message);
        }

        private static string Units(object value)
        {
            if (value is char character)
            {
                return ((int)character).ToString("X4", CultureInfo.InvariantCulture);
            }

            string text = (string)value;
            if (text.Length == 0)
            {
                return string.Empty;
            }

            string[] units = new string[text.Length];
            for (int index = 0; index < text.Length; ++index)
            {
                units[index] = ((int)text[index]).ToString("X4", CultureInfo.InvariantCulture);
            }
            return string.Join("-", units);
        }

        [DxAutoConstructor]
        private readonly partial struct GeneratedDefaults
        {
            public readonly int Required;

            [DxOptionalParameter("\uD800")]
            public readonly string High;

            [DxOptionalParameter("\uDFFF")]
            public readonly string Low;
        }

        private readonly struct HandwrittenDefaults
        {
            public readonly int Required;
            public readonly string High;
            public readonly string Low;

            public HandwrittenDefaults(int required, string high = "\uD800", string low = "\uDFFF")
            {
                Required = required;
                High = high;
                Low = low;
            }
        }
    }
}
