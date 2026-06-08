#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Single source of truth for the zero-dependency comparison bridge roster. Both the
    /// data-driven benchmark entry point (<see cref="ZeroDependencyComparisonTests"/>) and
    /// the static contract test (<see cref="ComparisonContractTests"/>) enumerate THIS list,
    /// so the "baselines always present" guarantee and the per-case benchmark matrix can
    /// never silently drift apart. Each entry pairs the bridge's stable TechKey with a fresh
    /// factory; a fresh instance is created per (tech, scenario) case.
    /// </summary>
    public static class ZeroDependencyComparisonRoster
    {
        public static readonly IReadOnlyList<(
            string key,
            Func<IMessagingTechBridge> factory
        )> Bridges = new (string key, Func<IMessagingTechBridge> factory)[]
        {
            ("DxMessaging", () => new DxMessagingBridge()),
            ("CsEvent", () => new CsharpEventBridge()),
            ("UnityEvent", () => new UnityEventBridge()),
            ("ScriptableObject", () => new ScriptableObjectChannelBridge()),
            ("UnitySendMessage", () => new UnitySendMessageBridge()),
        };
    }
}
#endif
