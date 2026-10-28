// cspell:ignore Cdecl
#if UNITY_6000_5 && UNITY_STANDALONE_WIN && ENABLE_IL2CPP
namespace DxMessaging.Tests.Runtime.MemoryReclaim
{
    using System;
    using System.Runtime.InteropServices;
    using NUnit.Framework;

    /// <summary>Untimed SDK nominal object sizes, distinct from allocator or owner cost.</summary>
    internal static class NativeObjectSizeRows
    {
        private const string Library = "DxmNativeAllocationCounter";
        private const int Iterations = 32;
        private const int Warmups = 64;
        private const int Shapes = 18;
        private static readonly int[] Lengths = { 1, 17, 33 };
        private static readonly string[] Names =
        {
            "none",
            "small-object",
            "large-object",
            "small-box",
            "large-box",
            "byte-1",
            "byte-17",
            "byte-33",
            "int-1",
            "int-17",
            "int-33",
            "reference-1",
            "reference-17",
            "reference-33",
            "string-1",
            "string-17",
            "string-33",
            "int-2x3",
        };

        internal static void MeasureAndAssert()
        {
            object[] roots = new object[Shapes * Iterations];
            Observation[] observations = new Observation[Shapes];
            Assert.That(DxmCounterBindSizes(), Is.Zero, "Bind both size APIs separately.");
            for (int shape = 0; shape < Shapes; ++shape)
            {
                for (int index = 0; index < Warmups; ++index)
                {
                    Allocate(shape, index, roots, shape * Iterations + index % Iterations);
                }
            }
            for (int index = 0; index < Warmups; ++index)
            {
                Assert.That(DxmCounterBeginSizes(), Is.Zero);
                _ = DxmCounterEnd();
                ReadObservation(0);
            }
            for (int shape = 0; shape < Shapes; ++shape)
            {
                int began = DxmCounterBeginSizes();
                if (began != 0)
                {
                    Assert.Fail($"Cannot begin size row {shape}: status={began}.");
                }
                for (int index = 0; index < Iterations; ++index)
                {
                    Allocate(shape, index, roots, shape * Iterations + index);
                }
                long events = DxmCounterEnd();
                observations[shape] = ReadObservation(events);
            }
            for (int shape = 0; shape < Shapes; ++shape)
            {
                Observation row = observations[shape];
                TestContext.Out.WriteLine(
                    $"NATIVE_OBJECT_SIZE row={Names[shape]}, iterations={Iterations}, warmups={Warmups}, "
                        + $"events={row.Events}, foreign={row.Foreign}, errors={row.Errors}, "
                        + $"nominalBytes={row.Bytes}, nominalClasses={row.Classes}, "
                        + $"minimumSize={row.Minimum}, maximumSize={row.Maximum}, "
                        + $"minimumClass={row.MinimumClass}, maximumClass={row.MaximumClass}"
                );
            }
            for (int shape = 0; shape < Shapes; ++shape)
            {
                AssertRow(shape, observations[shape]);
                ValidatePayloads(shape, roots);
            }
            Assert.That(observations[2].Minimum, Is.GreaterThan(observations[1].Minimum));
            Assert.That(observations[4].Minimum, Is.GreaterThan(observations[3].Minimum));
            GC.KeepAlive(roots);
        }

        private static Observation ReadObservation(long events)
        {
            return new Observation
            {
                Events = events,
                Foreign = DxmCounterForeign(),
                Errors = DxmCounterSizeErrors(),
                Bytes = DxmCounterNominalBytes(),
                Classes = DxmCounterNominalClasses(),
                Minimum = DxmCounterMinimumSize(),
                Maximum = DxmCounterMaximumSize(),
                MinimumClass = DxmCounterMinimumClass(),
                MaximumClass = DxmCounterMaximumClass(),
            };
        }

        private static void AssertRow(int shape, Observation row)
        {
            string name = Names[shape];
            Assert.That(row.Events, Is.EqualTo(shape == 0 ? 0 : Iterations), name);
            Assert.That(row.Foreign, Is.Zero, name);
            Assert.That(row.Errors, Is.Zero, name);
            Assert.That(row.Minimum, Is.EqualTo(row.Maximum), name);
            Assert.That(row.MinimumClass, Is.EqualTo(row.MaximumClass), name);
            Assert.That(row.Bytes, Is.EqualTo(row.Minimum * Iterations), name);
            Assert.That(row.Classes, Is.EqualTo(row.MinimumClass * Iterations), name);
            if (shape == 0)
            {
                Assert.That(row.Bytes, Is.Zero, name);
                Assert.That(row.Classes, Is.Zero, name);
                return;
            }
            Assert.That(row.MinimumClass, Is.GreaterThanOrEqualTo(16), name);
            if (shape <= 4)
            {
                Assert.That(row.Minimum, Is.EqualTo(row.MinimumClass), name);
                Assert.That(row.Minimum, Is.GreaterThanOrEqualTo(shape % 2 == 1 ? 20 : 48), name);
                return;
            }
            long expected;
            if (shape <= 13)
            {
                int elementSize =
                    shape <= 7 ? 1
                    : shape <= 10 ? 4
                    : 8;
                expected = 32 + elementSize * Lengths[(shape - 5) % 3];
            }
            else if (shape <= 16)
            {
                expected = 24 + 2 * (Lengths[shape - 14] + 1);
            }
            else
            {
                // SDK: vector header32 + six int elements24 + two 16-byte bounds.
                expected = 88;
            }
            Assert.That(
                row.Minimum,
                Is.EqualTo(expected),
                name + ": frozen SDK nominal size model."
            );
        }

        private static void Allocate(int shape, int value, object[] roots, int slot)
        {
            switch (shape)
            {
                case 0:
                    roots[slot] = null;
                    break;
                case 1:
                    roots[slot] = new SmallObject(value);
                    break;
                case 2:
                    roots[slot] = new LargeObject(value);
                    break;
                case 3:
                    roots[slot] = new SmallValue(value);
                    break;
                case 4:
                    roots[slot] = new LargeValue(value);
                    break;
                case 5:
                case 6:
                case 7:
                    byte[] bytes = new byte[Lengths[shape - 5]];
                    bytes[bytes.Length - 1] = (byte)value;
                    roots[slot] = bytes;
                    break;
                case 8:
                case 9:
                case 10:
                    int[] integers = new int[Lengths[shape - 8]];
                    integers[integers.Length - 1] = value;
                    roots[slot] = integers;
                    break;
                case 11:
                case 12:
                case 13:
                    object[] references = new object[Lengths[shape - 11]];
                    references[references.Length - 1] = roots;
                    roots[slot] = references;
                    break;
                case 14:
                case 15:
                case 16:
                    roots[slot] = new string((char)('a' + value % 26), Lengths[shape - 14]);
                    break;
                case 17:
                    int[,] matrix = new int[2, 3];
                    matrix[1, 2] = value;
                    roots[slot] = matrix;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(shape));
            }
        }

        private static void ValidatePayloads(int shape, object[] roots)
        {
            for (int index = 0; index < Iterations; ++index)
            {
                object value = roots[shape * Iterations + index];
                switch (shape)
                {
                    case 0:
                        Assert.That(value, Is.Null);
                        break;
                    case 1:
                        Assert.That(((SmallObject)value).Value, Is.EqualTo(index));
                        break;
                    case 2:
                        LargeObject large = (LargeObject)value;
                        Assert.That(large.A, Is.EqualTo(index));
                        Assert.That(large.B, Is.EqualTo(index + 1));
                        Assert.That(large.C, Is.EqualTo(index + 2));
                        Assert.That(large.D, Is.EqualTo(index + 3));
                        break;
                    case 3:
                        Assert.That(((SmallValue)value).Value, Is.EqualTo(index));
                        break;
                    case 4:
                        LargeValue boxed = (LargeValue)value;
                        Assert.That(boxed.A, Is.EqualTo(index));
                        Assert.That(boxed.B, Is.EqualTo(index + 1));
                        Assert.That(boxed.C, Is.EqualTo(index + 2));
                        Assert.That(boxed.D, Is.EqualTo(index + 3));
                        break;
                    default:
                        if (shape <= 13)
                        {
                            Array array = (Array)value;
                            int length = Lengths[(shape - 5) % 3];
                            Assert.That(array.Length, Is.EqualTo(length));
                            if (shape <= 7)
                                Assert.That(((byte[])value)[length - 1], Is.EqualTo((byte)index));
                            else if (shape <= 10)
                                Assert.That(((int[])value)[length - 1], Is.EqualTo(index));
                            else
                                Assert.That(((object[])value)[length - 1], Is.SameAs(roots));
                        }
                        else if (shape <= 16)
                        {
                            string text = (string)value;
                            Assert.That(text.Length, Is.EqualTo(Lengths[shape - 14]));
                            foreach (char character in text)
                                Assert.That(character, Is.EqualTo((char)('a' + index % 26)));
                        }
                        else
                        {
                            int[,] matrix = (int[,])value;
                            Assert.That(matrix.GetLength(0), Is.EqualTo(2));
                            Assert.That(matrix.GetLength(1), Is.EqualTo(3));
                            Assert.That(matrix[1, 2], Is.EqualTo(index));
                        }
                        break;
                }
                if (shape == 0)
                    continue;
                for (int prior = 0; prior < index; ++prior)
                {
                    Assert.That(
                        ReferenceEquals(value, roots[shape * Iterations + prior]),
                        Is.False
                    );
                }
            }
        }

        private struct Observation
        {
            internal long Events;
            internal long Foreign;
            internal long Errors;
            internal long Bytes;
            internal long Classes;
            internal long Minimum;
            internal long Maximum;
            internal long MinimumClass;
            internal long MaximumClass;
        }

        private sealed class SmallObject
        {
            internal readonly int Value;

            internal SmallObject(int value)
            {
                Value = value;
            }
        }

        private sealed class LargeObject
        {
            internal readonly long A;
            internal readonly long B;
            internal readonly long C;
            internal readonly long D;

            internal LargeObject(int value)
            {
                A = value;
                B = value + 1;
                C = value + 2;
                D = value + 3;
            }
        }

        private readonly struct SmallValue
        {
            internal readonly int Value;

            internal SmallValue(int value)
            {
                Value = value;
            }
        }

        private readonly struct LargeValue
        {
            internal readonly long A;
            internal readonly long B;
            internal readonly long C;
            internal readonly long D;

            internal LargeValue(int value)
            {
                A = value;
                B = value + 1;
                C = value + 2;
                D = value + 3;
            }
        }

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int DxmCounterBindSizes();

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int DxmCounterBeginSizes();

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern long DxmCounterEnd();

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern long DxmCounterForeign();

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern long DxmCounterSizeErrors();

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern long DxmCounterNominalBytes();

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern long DxmCounterNominalClasses();

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern long DxmCounterMinimumSize();

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern long DxmCounterMaximumSize();

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern long DxmCounterMinimumClass();

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern long DxmCounterMaximumClass();
    }
}
#endif
