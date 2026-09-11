namespace DxMessaging.Core.MessageBus.Internal
{
    using System;
    using System.Runtime.CompilerServices;

    /// <summary>
    /// Bit-packed coordinate that identifies a single dispatch slot along three
    /// axes: <see cref="DispatchKind"/>, <see cref="DispatchPhase"/>, and
    /// <see cref="DispatchVariant"/>. The byte-sized layout lets the bus index
    /// into a small fixed-size array of slots without per-axis branching on the
    /// hot dispatch path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bit layout of <see cref="Packed"/>: <c>KKKK P VVV</c> where K is the
    /// 4-bit <see cref="DispatchKind"/>, P is the 1-bit
    /// <see cref="DispatchPhase"/>, and V is the 3-bit
    /// <see cref="DispatchVariant"/>. The exact formula is
    /// <c>packed = (kind &lt;&lt; 4) | (phase &lt;&lt; 3) | variant</c>.
    /// </para>
    /// <para>
    /// The sentinel value <see cref="None"/> uses <c>packed = 0xFF</c> and
    /// represents "no axis applies" -- it is returned for registration methods
    /// (such as <see cref="DxMessaging.Core.MessageBus.RegistrationMethod.Interceptor"/>
    /// and <see cref="DxMessaging.Core.MessageBus.RegistrationMethod.GlobalAcceptAll"/>)
    /// whose dispatch is multi-slot or otherwise outside the axis grid.
    /// </para>
    /// <para>
    /// <c>default(SlotKey)</c> is bit-identical to
    /// <c>new SlotKey(DispatchKind.Untargeted, DispatchPhase.Handle, DispatchVariant.Default)</c>.
    /// This is intentional -- uninitialized <see cref="SlotKey"/> fields decode
    /// to a real, valid slot. Use <see cref="None"/> (packed = 0xFF) as the
    /// sentinel for "no slot applies"; never use <c>default(SlotKey)</c> as a
    /// sentinel.
    /// </para>
    /// </remarks>
    internal readonly struct SlotKey : IEquatable<SlotKey>
    {
        private const int KindShift = 4;
        private const int PhaseShift = 3;
        private const byte KindMask = 0x0F;
        private const byte PhaseMask = 0x01;
        private const byte VariantMask = 0x07;

        private const byte NonePacked = 0xFF;

        /// <summary>
        /// Sentinel slot key that represents "no axis applies". Distinct from
        /// <c>default(SlotKey)</c>; equality with <c>default</c> is false.
        /// </summary>
        /// <remarks>
        /// 0xFF is unreachable from the public ctor by construction; the ctor explicitly rejects the (15, 1, 7) triple.
        /// </remarks>
        public static readonly SlotKey None = new SlotKey(unpacked: NonePacked);

        /// <summary>
        /// The packed byte representation. Layout is
        /// <c>(kind &lt;&lt; 4) | (phase &lt;&lt; 3) | variant</c>; the sentinel
        /// <see cref="None"/> uses <c>0xFF</c>.
        /// </summary>
        public readonly byte Packed;

        /// <summary>
        /// Constructs a slot key from its three component axes.
        /// </summary>
        /// <param name="kind">Dispatch kind. Must fit in 4 bits (0..15).</param>
        /// <param name="phase">Dispatch phase. Must fit in 1 bit (0..1).</param>
        /// <param name="variant">Dispatch variant. Must fit in 3 bits (0..7).</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when any axis exceeds the bits allotted for it.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when the <c>(kind, phase, variant)</c> triple packs to the
        /// reserved <see cref="None"/> sentinel bit pattern (<c>0xFF</c>),
        /// i.e. <c>(15, 1, 7)</c>.
        /// </exception>
        public SlotKey(DispatchKind kind, DispatchPhase phase, DispatchVariant variant)
        {
            byte k = (byte)kind;
            byte p = (byte)phase;
            byte v = (byte)variant;
            if (KindMask < k)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(kind),
                    k,
                    "DispatchKind must fit in 4 bits (0..15)."
                );
            }
            if (PhaseMask < p)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(phase),
                    p,
                    "DispatchPhase must fit in 1 bit (0..1)."
                );
            }
            if (VariantMask < v)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(variant),
                    v,
                    "DispatchVariant must fit in 3 bits (0..7)."
                );
            }
            byte packed = (byte)((k << KindShift) | (p << PhaseShift) | v);
            if (packed == NonePacked)
            {
                throw new ArgumentException(
                    "(kind, phase, variant) triple packs to the SlotKey.None sentinel. "
                        + "The bit pattern 0xFF is reserved for SlotKey.None and cannot be "
                        + "constructed via the public ctor, i.e. (kind=15, phase=1, variant=7).",
                    nameof(variant)
                );
            }
            Packed = packed;
        }

        private SlotKey(byte unpacked)
        {
            Packed = unpacked;
        }

        /// <summary>The decoded <see cref="DispatchKind"/> axis.</summary>
        public DispatchKind Kind
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (DispatchKind)((Packed >> KindShift) & KindMask);
        }

        /// <summary>The decoded <see cref="DispatchPhase"/> axis.</summary>
        public DispatchPhase Phase
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (DispatchPhase)((Packed >> PhaseShift) & PhaseMask);
        }

        /// <summary>The decoded <see cref="DispatchVariant"/> axis.</summary>
        public DispatchVariant Variant
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (DispatchVariant)(Packed & VariantMask);
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(SlotKey other)
        {
            return Packed == other.Packed;
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return obj is SlotKey other && Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return Packed;
        }

        /// <summary>Equality operator. Compares the packed byte value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(SlotKey left, SlotKey right)
        {
            return left.Packed == right.Packed;
        }

        /// <summary>Inequality operator. Compares the packed byte value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(SlotKey left, SlotKey right)
        {
            return left.Packed != right.Packed;
        }

        /// <summary>
        /// Returns "None" for the <see cref="None"/> sentinel; otherwise
        /// returns a slash-delimited triple of the form
        /// <c>"{Kind}/{Phase}/{Variant}"</c>.
        /// </summary>
        public override string ToString()
        {
            if (Packed == NonePacked)
            {
                return "None";
            }
            return Kind + "/" + Phase + "/" + Variant;
        }
    }
}
