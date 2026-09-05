namespace DxMessaging.Tests.Runtime
{
    /// <summary>
    /// Identifies one of the DxMessaging dispatch categories. Used by the
    /// parameterized test harness so a single test method can cover all kinds.
    /// <para>
    /// <see cref="Untargeted"/>, <see cref="Targeted"/>, and
    /// <see cref="Broadcast"/> are the three canonical dispatch kinds and are
    /// the only values that <see cref="MessageScenarios.AllKinds"/> emits.
    /// <see cref="TargetedWithoutTargeting"/> and <see cref="BroadcastWithoutSource"/>
    /// describe the same wire-level message types as <see cref="Targeted"/>
    /// and <see cref="Broadcast"/> but exercise the dispatch-time codepaths
    /// that intentionally drop the per-target / per-source binding. Tests that
    /// need to cover the without-context dispatch dimensions use the extended
    /// <see cref="MessageScenarios.AllKindsIncludingWithoutContext"/> source.
    /// </para>
    /// </summary>
    public enum MessageKind
    {
        Untargeted,
        Targeted,
        Broadcast,
        TargetedWithoutTargeting,
        BroadcastWithoutSource,
    }
}
