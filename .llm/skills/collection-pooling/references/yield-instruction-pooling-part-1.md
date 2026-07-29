## Overview

Continuation material extracted from `yield-instruction-pooling.md` to keep .llm files within the 300-line budget.

## Solution

### Core Concept

```text
+-----------------------------------------------------------------+
|                 WaitForSeconds Cache                            |
+-----------------------------------------------------------------+
|  Key (quantized seconds) | Value (WaitForSeconds instance)      |
| ----------------------- | -----------------------------------  |
|  0.0f                    | WaitForSeconds(0.0f)                 |
|  0.1f                    | WaitForSeconds(0.1f)                 |
|  0.5f                    | WaitForSeconds(0.5f)                 |
|  1.0f                    | WaitForSeconds(1.0f)                 |
|  2.0f                    | WaitForSeconds(2.0f)                 |
|  ...                     | ...                                  |
+-----------------------------------------------------------------+

GetWaitForSeconds(1.5f) -> Quantize to 1.5f -> Return cached instance
```

### Implementation

```csharp
namespace WallstopStudios.UnityHelpers.Utils
{
    using System.Collections.Generic;
    using UnityEngine;

    public static partial class Buffers
    {
        /// <summary>
        /// Quantization step for WaitForSeconds caching.
        /// Smaller = more precision, larger cache.
        /// </summary>
        public static float WaitInstructionQuantizationStepSeconds { get; set; } = 0.05f;

        private static readonly Dictionary<float, WaitForSeconds> waitForSecondsCache =
            new Dictionary<float, WaitForSeconds>(64);

        private static readonly Dictionary<float, WaitForSecondsRealtime> waitForSecondsRealtimeCache =
            new Dictionary<float, WaitForSecondsRealtime>(32);

        // Static singleton instances for common instructions
        private static WaitForEndOfFrame waitForEndOfFrame;
        private static WaitForFixedUpdate waitForFixedUpdate;

        /// <summary>
        /// Gets a cached WaitForEndOfFrame instance.
        /// </summary>
        public static WaitForEndOfFrame WaitForEndOfFrame =>
            waitForEndOfFrame ?? (waitForEndOfFrame = new WaitForEndOfFrame());

        /// <summary>
        /// Gets a cached WaitForFixedUpdate instance.
        /// </summary>
        public static WaitForFixedUpdate WaitForFixedUpdate =>
            waitForFixedUpdate ?? (waitForFixedUpdate = new WaitForFixedUpdate());

        /// <summary>
        /// Gets a cached WaitForSeconds for the given duration.
        /// Duration is quantized to reduce cache entries.
        /// </summary>
        public static WaitForSeconds GetWaitForSeconds(float seconds)
        {
            float quantized = QuantizeTime(seconds);

            if (!waitForSecondsCache.TryGetValue(quantized, out WaitForSeconds cached))
            {
                cached = new WaitForSeconds(quantized);
                waitForSecondsCache[quantized] = cached;
            }

            return cached;
        }

        /// <summary>
        /// Gets a cached WaitForSecondsRealtime for the given duration.
        /// </summary>
        public static WaitForSecondsRealtime GetWaitForSecondsRealtime(float seconds)
        {
            float quantized = QuantizeTime(seconds);

            if (!waitForSecondsRealtimeCache.TryGetValue(quantized, out WaitForSecondsRealtime cached))
            {
                cached = new WaitForSecondsRealtime(quantized);
                waitForSecondsRealtimeCache[quantized] = cached;
            }

            return cached;
        }

        /// <summary>
        /// Try to get cached WaitForSeconds. Returns null if cache is at capacity.
        /// Use this in memory-constrained scenarios.
        /// </summary>
        public static WaitForSeconds TryGetWaitForSecondsPooled(
            float seconds,
            int maxCacheSize = 100)
        {
            float quantized = QuantizeTime(seconds);

            if (waitForSecondsCache.TryGetValue(quantized, out WaitForSeconds cached))
            {
                return cached;
            }

            if (waitForSecondsCache.Count >= maxCacheSize)
            {
                return null; // Caller should create new instance
            }

            cached = new WaitForSeconds(quantized);
            waitForSecondsCache[quantized] = cached;
            return cached;
        }

        private static float QuantizeTime(float seconds)
        {
            if (seconds <= 0f) return 0f;

            float step = WaitInstructionQuantizationStepSeconds;
            return Mathf.Round(seconds / step) * step;
        }

        /// <summary>
        /// Clears all cached yield instructions.
        /// Call when loading new scene if memory is tight.
        /// </summary>
        public static void ClearYieldCaches()
        {
            waitForSecondsCache.Clear();
            waitForSecondsRealtimeCache.Clear();
        }
    }
}
```

## See Also

- [WaitForSeconds and Yield Instruction Pooling](./yield-instruction-pooling.md)
