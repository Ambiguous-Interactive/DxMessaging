namespace DxMessaging.Core.Internal
{
    /// <summary>
    /// Const-int positions into <c>TypedHandler&lt;IMessage&gt;._globalSlots[]</c>.
    /// Indices are hand-written so call sites inline as immediate operands.
    /// Array length and per-index <c>null</c>-ness are validated in DEBUG
    /// builds via <c>TypedHandler&lt;T&gt;.ValidateSlotArrays()</c>.
    /// </summary>
    /// <remarks>
    /// Positions are laid out in lex-(<c>Kind</c>, <c>Variant</c>) order:
    /// Untargeted -&gt; Targeted -&gt; Broadcast within Kind, Default before
    /// Fast within Variant. The xmldoc on each constant names the legacy
    /// <c>TypedHandler&lt;T&gt;</c> field whose storage role the slot will
    /// assume in typed global storage.
    /// </remarks>
    internal static class TypedGlobalSlotIndex
    {
        /// <summary>Legacy field: <c>_globalUntargetedHandlers</c>.</summary>
        public const int UntargetedDefault = 0;

        /// <summary>Legacy field: <c>_globalUntargetedFastHandlers</c>.</summary>
        public const int UntargetedFast = 1;

        /// <summary>Legacy field: <c>_globalTargetedHandlers</c>.</summary>
        public const int TargetedDefault = 2;

        /// <summary>Legacy field: <c>_globalTargetedFastHandlers</c>.</summary>
        public const int TargetedFast = 3;

        /// <summary>Legacy field: <c>_globalBroadcastHandlers</c>.</summary>
        public const int BroadcastDefault = 4;

        /// <summary>Legacy field: <c>_globalBroadcastFastHandlers</c>.</summary>
        public const int BroadcastFast = 5;

        public const int Length = 6;
    }
}
