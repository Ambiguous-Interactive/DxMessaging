#if UNITY_2021_3_OR_NEWER
namespace DxMessaging.Tests.Runtime
{
    using System;
    using System.Collections.Generic;

    /// <summary>Test-only negative control: one ordinary public operation per item, in order.</summary>
    internal static class ExactSequentialBatchControl
    {
        internal delegate void RefItemAction<T>(ref T item);

        internal static void Execute<T>(IReadOnlyList<T> items, Action<T> emit)
        {
            if (items == null)
            {
                throw new ArgumentNullException(nameof(items));
            }
            if (emit == null)
            {
                throw new ArgumentNullException(nameof(emit));
            }
            for (int index = 0; index < items.Count; ++index)
            {
                emit(items[index]);
            }
        }

        internal static void Execute<T>(T[] items, RefItemAction<T> emit)
        {
            if (items == null)
            {
                throw new ArgumentNullException(nameof(items));
            }
            if (emit == null)
            {
                throw new ArgumentNullException(nameof(emit));
            }
            for (int index = 0; index < items.Length; ++index)
            {
                emit(ref items[index]);
            }
        }
    }
}
#endif
