#if UNITY_EDITOR
namespace DxMessaging.Tests.Editor
{
    using System;
    using System.Collections.Generic;

    internal static class EditorTestCleanup
    {
        internal static void Run(params Action[] restorations)
        {
            List<Exception> failures = new();
            foreach (Action restore in restorations)
            {
                try
                {
                    restore();
                }
                catch (Exception failure)
                {
                    failures.Add(failure);
                }
            }
            if (0 < failures.Count)
            {
                throw new AggregateException("Editor fixture cleanup failed.", failures);
            }
        }
    }
}
#endif
