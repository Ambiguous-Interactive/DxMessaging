// Vendored copy of Unity's Il2CppSetOption attribute. The IL2CPP code
// generator (il2cpp.exe) matches this attribute by its FULL NAME
// (Unity.IL2CPP.CompilerServices.Il2CppSetOptionAttribute), not by assembly,
// so an internal per-assembly copy is the sanctioned way for a package to opt
// hot methods out of generated null/bounds checks (the same pattern UniTask
// and other performance-focused packages use). The shape below mirrors the
// attribute source Unity publishes in the IL2CPP documentation; do not add
// members or change the namespace. Under Mono (editor, Mono players) the
// attribute is inert.
namespace Unity.IL2CPP.CompilerServices
{
    using System;

    /// <summary>
    /// Runtime checks that il2cpp.exe can omit for an annotated scope.
    /// </summary>
    internal enum Option
    {
        /// <summary>Implicit null checks before member access.</summary>
        NullChecks = 1,

        /// <summary>Array bounds checks on element access.</summary>
        ArrayBoundsChecks = 2,

        /// <summary>Integer divide-by-zero checks.</summary>
        DivideByZeroChecks = 3,
    }

    /// <summary>
    /// Instructs the IL2CPP code generator to enable or disable a runtime
    /// check for the annotated type, method, or property.
    /// </summary>
    /// <remarks>
    /// Only apply with <c>false</c> to code whose safety invariants are
    /// guaranteed by construction and covered by tests: with checks disabled,
    /// a violated invariant is memory corruption instead of a managed
    /// exception.
    /// </remarks>
    [AttributeUsage(
        AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Property,
        Inherited = false,
        AllowMultiple = true
    )]
    internal sealed class Il2CppSetOptionAttribute : Attribute
    {
        public Option Option { get; private set; }

        public object Value { get; private set; }

        public Il2CppSetOptionAttribute(Option option, object value)
        {
            Option = option;
            Value = value;
        }
    }
}
