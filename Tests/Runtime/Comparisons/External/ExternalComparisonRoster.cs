#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime.Comparisons.External
{
#if MESSAGEPIPE_PRESENT && UNIRX_PRESENT && ZENJECT_PRESENT
    using System;
    using System.Collections.Generic;
    using DxMessaging.Tests.Runtime.Comparisons;

    /// <summary>
    /// Single source of truth for the external-package comparison bridge roster. The
    /// performance and fast contract fixtures enumerate the same factories so a bridge cannot
    /// retain contract coverage while silently disappearing from the benchmark matrix.
    /// </summary>
    public static class ExternalComparisonRoster
    {
        public static readonly IReadOnlyList<(
            string key,
            Func<IMessagingTechBridge> factory
        )> Bridges = new (string key, Func<IMessagingTechBridge> factory)[]
        {
            ("MessagePipe", () => new MessagePipeBridge()),
            ("UniRx", () => new UniRxBridge()),
            ("ZenjectSignalBus", () => new ZenjectSignalBusBridge()),
        };

        public static IMessagingTechBridge Create(string key)
        {
            foreach ((string rosterKey, Func<IMessagingTechBridge> factory) in Bridges)
            {
                if (string.Equals(rosterKey, key, StringComparison.Ordinal))
                {
                    return factory();
                }
            }

            throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown external bridge key.");
        }
    }
#endif
}
#endif
