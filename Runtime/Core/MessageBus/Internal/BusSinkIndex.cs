namespace DxMessaging.Core.MessageBus.Internal
{
    /// <summary>
    /// Const-int positions into <c>MessageBus._scalarSinks[]</c>. Indices are
    /// hand-written so call sites inline as immediate operands. Array lengths,
    /// populated-non-null slot identities, and reserved-null slot identities
    /// are validated in DEBUG builds via <c>MessageBus.ValidateSinkArrays()</c>.
    /// </summary>
    /// <remarks>
    /// Slot 0 (<see cref="UntargetedHandleDefault"/>) holds the
    /// <c>RegisterUntargeted</c> Handle-phase cache.
    /// Slot 1 (<see cref="BroadcastHandleWithoutContext"/>) holds the
    /// <c>RegisterSourcedBroadcastWithoutSource</c> Handle-phase cache.
    /// Slot 2 (<see cref="TargetedHandleWithoutContext"/>) holds the
    /// <c>RegisterTargetedWithoutTargeting</c> Handle-phase cache.
    /// Slot 3 (<see cref="UntargetedPostProcessDefault"/>) holds the
    /// <c>RegisterUntargetedPostProcessor</c> PostProcess-phase cache.
    /// Slot 4 (<see cref="TargetedPostProcessWithoutContext"/>) holds the
    /// <c>RegisterTargetedWithoutTargetingPostProcessor</c> PostProcess-phase cache.
    /// Slot 5 (<see cref="BroadcastPostProcessWithoutContext"/>) holds the
    /// <c>RegisterBroadcastWithoutSourcePostProcessor</c> PostProcess-phase cache.
    /// Slots 6-7 (<see cref="Reserved6"/>, <see cref="Reserved7"/>) are permanent
    /// future-expansion stubs and remain null.
    /// </remarks>
    internal static class BusSinkIndex
    {
        /*
            "WithoutContext" unifies the legacy "WithoutTargeting" (Targeted) and
            "WithoutSource" (Broadcast) per-axis variants -- both lack an InstanceId.
        */
        public const int UntargetedHandleDefault = 0;
        public const int BroadcastHandleWithoutContext = 1;
        public const int TargetedHandleWithoutContext = 2;
        public const int UntargetedPostProcessDefault = 3;
        public const int TargetedPostProcessWithoutContext = 4;
        public const int BroadcastPostProcessWithoutContext = 5;
        public const int Reserved6 = 6;
        public const int Reserved7 = 7;

        public const int Length = 8;
    }
}
