namespace DxMessaging.Core.Configuration
{
    using System.Threading;
#if UNITY_2021_3_OR_NEWER
    using UnityEngine;
#endif

#if UNITY_2021_3_OR_NEWER
    /// <summary>
    /// Lazy provider that hands out the active <see cref="DxMessagingRuntimeSettings"/>
    /// instance. Tries <c>Resources.Load&lt;DxMessagingRuntimeSettings&gt;("DxMessagingRuntimeSettings")</c>
    /// once per AppDomain; on miss, returns a defaulted in-memory instance so the
    /// runtime always has a usable settings object.
    /// </summary>
    public static class DxMessagingRuntimeSettingsProvider
    {
        private static DxMessagingRuntimeSettings _cached;
        private static readonly object _gate = new();

        /// <summary>
        /// Returns the active settings instance. Loads the asset on first call;
        /// subsequent calls return the cached reference.
        /// </summary>
        /// <remarks>
        /// This provider and its ScriptableObject settings type are available only in Unity.
        /// Plain .NET builds use the bus defaults and do not expose this property.
        /// </remarks>
        public static DxMessagingRuntimeSettings Current
        {
            get
            {
                DxMessagingRuntimeSettings local = Volatile.Read(ref _cached);
                if (local != null)
                {
                    return local;
                }
                lock (_gate)
                {
                    local = _cached;
                    if (local != null)
                    {
                        return local;
                    }
                    local = LoadOrCreate();
                    Volatile.Write(ref _cached, local);
                    return local;
                }
            }
        }

        /// <summary>
        /// Clears the cached reference, forcing the next <see cref="Current"/>
        /// access to reload from <c>Resources</c>. Called when resetting static state or entering a new player session.
        /// </summary>
        internal static void ResetCachedSettings()
        {
            lock (_gate)
            {
                _cached = null;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void OnSubsystemRegistration()
        {
            ResetCachedSettings();
        }

        private static DxMessagingRuntimeSettings LoadOrCreate()
        {
            DxMessagingRuntimeSettings asset = null;
            try
            {
                asset = Resources.Load<DxMessagingRuntimeSettings>(
                    DxMessagingRuntimeSettings.ResourceName
                );
            }
            catch
            {
                asset = null;
            }
            if (asset != null)
            {
                return asset;
            }
            DxMessagingRuntimeSettings fallback =
                ScriptableObject.CreateInstance<DxMessagingRuntimeSettings>();
            fallback.MarkAsFallbackInstance();
            fallback.hideFlags = HideFlags.HideAndDontSave;
            fallback.name = DxMessagingRuntimeSettings.ResourceName + " (Default)";
            return fallback;
        }
    }
#endif
}
