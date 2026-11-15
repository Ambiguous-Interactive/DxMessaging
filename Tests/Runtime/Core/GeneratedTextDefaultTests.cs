namespace DxMessaging.Tests.Runtime.Core
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;
    using DxMessaging.Core.Attributes;
    using NUnit.Framework;

    // cspell:ignore DFFF
    /// <summary>Consumes generated text defaults against handwritten controls and fixed UTF16 units.</summary>
    [TestFixture]
    public sealed partial class GeneratedTextDefaultTests
    {
        [TestCaseSource(nameof(DefaultCases))]
        public void OmittedTextDefaultsMatchHandwrittenConstructorCodeUnits(
            int index,
            string expectedUnits,
            string boundary
        )
        {
            string context = $"index={index}, expectedUnits={expectedUnits}, boundary={boundary}";
            HandwrittenDefaults manual = new(1);
            Assert.That(manual.Required, Is.EqualTo(1), "Manual constructor ran: " + context);
            object manualValue = index switch
            {
                0 => manual.CharZero,
                1 => manual.CharSingleQuote,
                2 => manual.CharDoubleQuote,
                3 => manual.CharBackslash,
                4 => manual.CharTab,
                5 => manual.CharLineFeed,
                6 => manual.CharCarriageReturn,
                7 => manual.CharNextLine,
                8 => manual.CharLineSeparator,
                9 => manual.CharParagraphSeparator,
                10 => manual.CharHighSurrogate,
                11 => manual.CharLowSurrogate,
                12 => manual.CharMaximum,
                13 => manual.CharOrdinary,
                14 => manual.StringNull,
                15 => manual.StringEmpty,
                16 => manual.StringQuoteAndBackslash,
                17 => manual.StringEmbeddedZero,
                18 => manual.StringCarriageReturnLineFeed,
                19 => manual.StringLiteralBackslashUnicodeText,
                20 => manual.StringSeparators,
                21 => manual.StringHighSurrogate,
                22 => manual.StringLowSurrogate,
                23 => manual.StringSupplementaryPair,
                _ => throw new ArgumentOutOfRangeException(nameof(index)),
            };
            Assert.That(
                Units(manualValue),
                Is.EqualTo(expectedUnits),
                "Manual native control: " + context
            );

            Defaults generated = new(1);
            Assert.That(generated.Required, Is.EqualTo(1), "Generated constructor ran: " + context);
            object generatedValue = index switch
            {
                0 => generated.CharZero,
                1 => generated.CharSingleQuote,
                2 => generated.CharDoubleQuote,
                3 => generated.CharBackslash,
                4 => generated.CharTab,
                5 => generated.CharLineFeed,
                6 => generated.CharCarriageReturn,
                7 => generated.CharNextLine,
                8 => generated.CharLineSeparator,
                9 => generated.CharParagraphSeparator,
                10 => generated.CharHighSurrogate,
                11 => generated.CharLowSurrogate,
                12 => generated.CharMaximum,
                13 => generated.CharOrdinary,
                14 => generated.StringNull,
                15 => generated.StringEmpty,
                16 => generated.StringQuoteAndBackslash,
                17 => generated.StringEmbeddedZero,
                18 => generated.StringCarriageReturnLineFeed,
                19 => generated.StringLiteralBackslashUnicodeText,
                20 => generated.StringSeparators,
                21 => generated.StringHighSurrogate,
                22 => generated.StringLowSurrogate,
                23 => generated.StringSupplementaryPair,
                _ => throw new ArgumentOutOfRangeException(nameof(index)),
            };
            Assert.That(
                Units(generatedValue),
                Is.EqualTo(expectedUnits),
                "Generated native value: " + context
            );
        }

        // SYNC: TextDefaultBoundaryTests.Cases uses the same fixed boundary roster.
        private static IEnumerable<TestCaseData> DefaultCases()
        {
            (string Label, string Units)[] values =
            {
                ("CharZero", "0000"),
                ("CharSingleQuote", "0027"),
                ("CharDoubleQuote", "0022"),
                ("CharBackslash", "005C"),
                ("CharTab", "0009"),
                ("CharLineFeed", "000A"),
                ("CharCarriageReturn", "000D"),
                ("CharNextLine", "0085"),
                ("CharLineSeparator", "2028"),
                ("CharParagraphSeparator", "2029"),
                ("CharHighSurrogate", "D800"),
                ("CharLowSurrogate", "DFFF"),
                ("CharMaximum", "FFFF"),
                ("CharOrdinary", "0041"),
                ("StringNull", "<null>"),
                ("StringEmpty", ""),
                ("StringQuoteAndBackslash", "0022-005C"),
                ("StringEmbeddedZero", "0041-0000-0042"),
                ("StringCarriageReturnLineFeed", "0041-000D-000A-0042"),
                ("StringLiteralBackslashUnicodeText", "005C-0075-0030-0030-0034-0031"),
                ("StringSeparators", "0085-2028-2029"),
                ("StringHighSurrogate", "D800"),
                ("StringLowSurrogate", "DFFF"),
                ("StringSupplementaryPair", "D83D-DE80"),
            };
            for (int index = 0; index < values.Length; ++index)
            {
                (string label, string units) = values[index];
                yield return new TestCaseData(index, units, label).SetName(
                    $"GeneratedText{label}DefaultRetainsCodeUnits"
                );
            }
        }

        private static string Units(object value)
        {
            if (value == null)
            {
                return "<null>";
            }
            if (value is char character)
            {
                return ((int)character).ToString("X4", CultureInfo.InvariantCulture);
            }
            Assert.That(
                value,
                Is.TypeOf<string>(),
                "Text boundary values must be char, string, or null."
            );
            StringBuilder units = new();
            foreach (char unit in (string)value)
            {
                if (units.Length != 0)
                {
                    units.Append('-');
                }
                units.Append(((int)unit).ToString("X4", CultureInfo.InvariantCulture));
            }
            return units.ToString();
        }

        private readonly struct HandwrittenDefaults
        {
            public readonly int Required;
            public readonly char CharZero;
            public readonly char CharSingleQuote;
            public readonly char CharDoubleQuote;
            public readonly char CharBackslash;
            public readonly char CharTab;
            public readonly char CharLineFeed;
            public readonly char CharCarriageReturn;
            public readonly char CharNextLine;
            public readonly char CharLineSeparator;
            public readonly char CharParagraphSeparator;
            public readonly char CharHighSurrogate;
            public readonly char CharLowSurrogate;
            public readonly char CharMaximum;
            public readonly char CharOrdinary;
            public readonly string StringNull;
            public readonly string StringEmpty;
            public readonly string StringQuoteAndBackslash;
            public readonly string StringEmbeddedZero;
            public readonly string StringCarriageReturnLineFeed;
            public readonly string StringLiteralBackslashUnicodeText;
            public readonly string StringSeparators;
            public readonly string StringHighSurrogate;
            public readonly string StringLowSurrogate;
            public readonly string StringSupplementaryPair;

            public HandwrittenDefaults(
                int required,
                char charZero = '\0',
                char charSingleQuote = '\'',
                char charDoubleQuote = '"',
                char charBackslash = '\\',
                char charTab = '\t',
                char charLineFeed = '\n',
                char charCarriageReturn = '\r',
                char charNextLine = '\u0085',
                char charLineSeparator = '\u2028',
                char charParagraphSeparator = '\u2029',
                char charHighSurrogate = '\uD800',
                char charLowSurrogate = '\uDFFF',
                char charMaximum = '\uFFFF',
                char charOrdinary = 'A',
                string stringNull = (string)null,
                string stringEmpty = "",
                string stringQuoteAndBackslash = "\"\\",
                string stringEmbeddedZero = "A\0B",
                string stringCarriageReturnLineFeed = "A\r\nB",
                string stringLiteralBackslashUnicodeText = @"\u0041",
                string stringSeparators = "\u0085\u2028\u2029",
                string stringHighSurrogate = "\uD800",
                string stringLowSurrogate = "\uDFFF",
                string stringSupplementaryPair = "\U0001F680"
            )
            {
                Required = required;
                CharZero = charZero;
                CharSingleQuote = charSingleQuote;
                CharDoubleQuote = charDoubleQuote;
                CharBackslash = charBackslash;
                CharTab = charTab;
                CharLineFeed = charLineFeed;
                CharCarriageReturn = charCarriageReturn;
                CharNextLine = charNextLine;
                CharLineSeparator = charLineSeparator;
                CharParagraphSeparator = charParagraphSeparator;
                CharHighSurrogate = charHighSurrogate;
                CharLowSurrogate = charLowSurrogate;
                CharMaximum = charMaximum;
                CharOrdinary = charOrdinary;
                StringNull = stringNull;
                StringEmpty = stringEmpty;
                StringQuoteAndBackslash = stringQuoteAndBackslash;
                StringEmbeddedZero = stringEmbeddedZero;
                StringCarriageReturnLineFeed = stringCarriageReturnLineFeed;
                StringLiteralBackslashUnicodeText = stringLiteralBackslashUnicodeText;
                StringSeparators = stringSeparators;
                StringHighSurrogate = stringHighSurrogate;
                StringLowSurrogate = stringLowSurrogate;
                StringSupplementaryPair = stringSupplementaryPair;
            }
        }

        [DxAutoConstructor]
        private readonly partial struct Defaults
        {
            public readonly int Required;

            [DxOptionalParameter('\0')]
            public readonly char CharZero;

            [DxOptionalParameter('\'')]
            public readonly char CharSingleQuote;

            [DxOptionalParameter('"')]
            public readonly char CharDoubleQuote;

            [DxOptionalParameter('\\')]
            public readonly char CharBackslash;

            [DxOptionalParameter('\t')]
            public readonly char CharTab;

            [DxOptionalParameter('\n')]
            public readonly char CharLineFeed;

            [DxOptionalParameter('\r')]
            public readonly char CharCarriageReturn;

            [DxOptionalParameter('\u0085')]
            public readonly char CharNextLine;

            [DxOptionalParameter('\u2028')]
            public readonly char CharLineSeparator;

            [DxOptionalParameter('\u2029')]
            public readonly char CharParagraphSeparator;

            [DxOptionalParameter('\uD800')]
            public readonly char CharHighSurrogate;

            [DxOptionalParameter('\uDFFF')]
            public readonly char CharLowSurrogate;

            [DxOptionalParameter('\uFFFF')]
            public readonly char CharMaximum;

            [DxOptionalParameter('A')]
            public readonly char CharOrdinary;

            [DxOptionalParameter((string)null)]
            public readonly string StringNull;

            [DxOptionalParameter("")]
            public readonly string StringEmpty;

            [DxOptionalParameter("\"\\")]
            public readonly string StringQuoteAndBackslash;

            [DxOptionalParameter("A\0B")]
            public readonly string StringEmbeddedZero;

            [DxOptionalParameter("A\r\nB")]
            public readonly string StringCarriageReturnLineFeed;

            [DxOptionalParameter(@"\u0041")]
            public readonly string StringLiteralBackslashUnicodeText;

            [DxOptionalParameter("\u0085\u2028\u2029")]
            public readonly string StringSeparators;

            [DxOptionalParameter("\uD800")]
            public readonly string StringHighSurrogate;

            [DxOptionalParameter("\uDFFF")]
            public readonly string StringLowSurrogate;

            [DxOptionalParameter("\U0001F680")]
            public readonly string StringSupplementaryPair;
        }
    }
}
