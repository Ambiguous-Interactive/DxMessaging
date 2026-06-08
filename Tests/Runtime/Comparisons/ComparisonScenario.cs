#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons
{
    using System;
    using DxMessaging.Tests.Runtime.Benchmarks;

    /// <summary>
    /// Apples-to-apples scenarios used to compare messaging technologies. Each scenario
    /// describes ONE dispatch shape that every bridge implements with its idiomatic API
    /// (or declares unsupported). The renderer joins on <see cref="ComparisonScenarios.Key"/>,
    /// never on the display name.
    /// </summary>
    public enum ComparisonScenario
    {
        GlobalToOneSubscriber,
        GlobalToManySubscribers,
        KeyedToOneOfMany,
        PriorityOrderedDispatch,
        FilteredDispatch,
        PostProcessingDispatch,
        SubscribeUnsubscribeChurn,
        StructMessageZeroCopy,
    }

    /// <summary>
    /// Metadata for the comparison scenarios. <see cref="Key"/> returns the STABLE machine
    /// identifier baked into the row scenario id; it must never change. <see cref="DisplayName"/>
    /// returns the human-readable label shown in rendered docs and PR comments; it is
    /// presentation only and is never used as a join key.
    /// </summary>
    public static class ComparisonScenarios
    {
        public const int FanOutSubscribers = 16; // S2
        public const int KeyedListenerCount = 16; // S3 (dispatch to 1 of K)

        public static readonly ComparisonScenario[] All = (ComparisonScenario[])
            Enum.GetValues(typeof(ComparisonScenario));

        /// <summary>
        /// Per-scenario warm-up emit count. Every comparison scenario is a steady-state
        /// dispatch shape (none is a cold-bus first-touch scenario), so they all keep the
        /// shared <see cref="BenchmarkProtocol.WarmupEmits"/> default. This mirrors
        /// <see cref="DispatchBenchmarkScenarios.WarmupEmits"/> so warm-up policy is
        /// declared per scenario on both benchmark families.
        /// </summary>
        public static int WarmupEmits(ComparisonScenario scenario) => BenchmarkProtocol.WarmupEmits;

        // Stable MACHINE key used inside the row scenario id. Never change.
        public static string Key(ComparisonScenario s)
        {
            return s switch
            {
                ComparisonScenario.GlobalToOneSubscriber => "GlobalToOne",
                ComparisonScenario.GlobalToManySubscribers => "GlobalToMany",
                ComparisonScenario.KeyedToOneOfMany => "KeyedToOne",
                ComparisonScenario.PriorityOrderedDispatch => "PriorityOrdered",
                ComparisonScenario.FilteredDispatch => "Filtered",
                ComparisonScenario.PostProcessingDispatch => "PostProcess",
                ComparisonScenario.SubscribeUnsubscribeChurn => "SubUnsub",
                ComparisonScenario.StructMessageZeroCopy => "StructNoBox",
                _ => throw new ArgumentOutOfRangeException(nameof(s), s, null),
            };
        }

        // Human label for docs/PR comment (presentation only).
        public static string DisplayName(ComparisonScenario s)
        {
            return s switch
            {
                ComparisonScenario.GlobalToOneSubscriber => "Global -> 1 subscriber",
                ComparisonScenario.GlobalToManySubscribers => "Global -> 16 subscribers",
                ComparisonScenario.KeyedToOneOfMany => "Keyed/targeted -> 1 of many",
                ComparisonScenario.PriorityOrderedDispatch => "Priority-ordered dispatch",
                ComparisonScenario.FilteredDispatch => "Filtered/intercepted dispatch",
                ComparisonScenario.PostProcessingDispatch => "Post-processing dispatch",
                ComparisonScenario.SubscribeUnsubscribeChurn => "Subscribe/unsubscribe churn",
                ComparisonScenario.StructMessageZeroCopy => "Struct message (zero-copy)",
                _ => throw new ArgumentOutOfRangeException(nameof(s), s, null),
            };
        }
    }
}
#endif
