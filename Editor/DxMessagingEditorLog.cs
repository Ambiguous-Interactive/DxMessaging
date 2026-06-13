namespace DxMessaging.Editor
{
#if UNITY_EDITOR
    using System;
    using UnityEngine;

    internal static class DxMessagingEditorLog
    {
        internal static void LogWarning(string message, Exception exception)
        {
            if (exception == null)
            {
                Debug.LogWarning($"[DxMessaging] {message}");
                return;
            }

            Debug.LogWarning($"[DxMessaging] {message}{Environment.NewLine}{exception}");
        }

        internal static void LogError(string message, Exception exception)
        {
            if (exception == null)
            {
                Debug.LogError($"[DxMessaging] {message}");
                return;
            }

            Debug.LogError($"[DxMessaging] {message}{Environment.NewLine}{exception}");
        }
    }
#endif
}
