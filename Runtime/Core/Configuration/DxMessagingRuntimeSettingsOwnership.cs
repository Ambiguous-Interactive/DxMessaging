#if UNITY_EDITOR && UNITY_2021_3_OR_NEWER
namespace DxMessaging.Core.Configuration
{
    using UnityEditor;
    using UnityEngine;

    /// <summary>Keeps exact Editor fallback ownership across managed assembly reloads.</summary>
    internal sealed class DxMessagingRuntimeSettingsOwnership
        : ScriptableSingleton<DxMessagingRuntimeSettingsOwnership>
    {
        [SerializeField]
        private DxMessagingRuntimeSettings _fallback;

        internal DxMessagingRuntimeSettings Fallback
        {
            get => _fallback;
            set => _fallback = value;
        }
    }
}
#endif
