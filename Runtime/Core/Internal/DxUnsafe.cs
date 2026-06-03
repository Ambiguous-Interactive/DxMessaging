namespace DxMessaging.Core.Internal
{
    using System.Runtime.CompilerServices;
    // Qualified with global:: because this assembly also declares a DxMessaging.Unity
    // namespace; an unqualified "using Unity.Collections..." would bind to
    // DxMessaging.Unity.Collections and fail to resolve.
    using global::Unity.Collections.LowLevel.Unsafe;

    /// <summary>
    /// Reinterpret-cast helpers used by the hot dispatch path. Each method wraps a Unity
    /// <see cref="UnsafeUtility"/> intrinsic, which resolves in both the Editor and every
    /// player build (Mono and IL2CPP, including the .NET Standard 2.0 profile) without an
    /// external precompiled assembly.
    /// </summary>
    /// <remarks>
    /// These are drop-in replacements for the corresponding
    /// <c>System.Runtime.CompilerServices.Unsafe</c> members. That type is supplied by the
    /// Editor but is absent from player builds, so referencing it compiled in the Editor yet
    /// failed standalone IL2CPP compilation. <see cref="UnsafeUtility"/> ships inside
    /// <c>UnityEngine.CoreModule</c> on every supported platform, so routing through it keeps
    /// the zero-allocation reinterpret behavior while removing the unresolved dependency.
    /// The wrapped intrinsics are pure IL (no internal-call transition), so this indirection
    /// is free once inlined.
    /// </remarks>
    internal static class DxUnsafe
    {
        /// <summary>
        /// Reinterprets a managed reference to <typeparamref name="TFrom"/> as a reference to
        /// <typeparamref name="TTo"/> in place, without copying or boxing. Callers guarantee the
        /// reinterpretation is valid for the concrete runtime layout.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ref TTo As<TFrom, TTo>(ref TFrom source)
        {
            return ref UnsafeUtility.As<TFrom, TTo>(ref source);
        }

        /// <summary>
        /// Reinterprets a reference-typed instance as <typeparamref name="TTo"/> without a type
        /// check. Callers guarantee the runtime type, mirroring the prior unchecked
        /// <c>Unsafe.As&lt;T&gt;(object)</c> usage.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static TTo As<TTo>(object value)
            where TTo : class
        {
            return UnsafeUtility.As<object, TTo>(ref value);
        }

        /// <summary>
        /// Returns the size in bytes of <typeparamref name="T"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int SizeOf<T>()
            where T : struct
        {
            return UnsafeUtility.SizeOf<T>();
        }
    }
}
