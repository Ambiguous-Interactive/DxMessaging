namespace DxMessaging.Core.Extensions
{
    using System;
    using System.Runtime.CompilerServices;
    using DxMessaging.Core.Internal;

    internal static class EnumExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool HasFlagNoAlloc<T>(this T value, T flag)
            where T : unmanaged, Enum
        {
            ulong? valueUnderlying = GetUInt64(value);
            ulong? flagUnderlying = GetUInt64(flag);
            if (valueUnderlying == null || flagUnderlying == null)
            {
                // Fallback for unsupported enum sizes
                return value.HasFlag(flag);
            }

            return (valueUnderlying.Value & flagUnderlying.Value) == flagUnderlying.Value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong? GetUInt64<T>(T value)
            where T : unmanaged
        {
            try
            {
                return DxUnsafe.SizeOf<T>() switch
                {
                    1 => DxUnsafe.As<T, byte>(ref value),
                    2 => DxUnsafe.As<T, ushort>(ref value),
                    4 => DxUnsafe.As<T, uint>(ref value),
                    8 => DxUnsafe.As<T, ulong>(ref value),
                    _ => null,
                };
            }
            catch
            {
                return null;
            }
        }
    }
}
