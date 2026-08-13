#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons.UnityAtoms
{
    using System;
    using System.Collections.Generic;
    using DxMessaging.Tests.Runtime.Comparisons;

    internal static class UnityAtomsComparisonRoster
    {
        internal static readonly IReadOnlyList<(
            string key,
            Func<IMessagingTechBridge> factory
        )> Bridges = new (string key, Func<IMessagingTechBridge> factory)[]
        {
            ("UnityAtoms", () => new UnityAtomsBridge()),
        };
    }
}
#endif
