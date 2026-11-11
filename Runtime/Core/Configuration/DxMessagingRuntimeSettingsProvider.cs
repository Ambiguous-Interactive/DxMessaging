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
        private static DxMessagingRuntimeSettings _ownedFallback;
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
        /// <remarks>
        /// Releases only the fallback created by this provider. Resource assets and externally
        /// supplied settings remain alive. A retained provider fallback is invalid after reset;
        /// player destruction completes at the end of the frame.
        /// </remarks>
        internal static void ResetCachedSettings()
        {
            ReleaseOwnedFallbacks(immediate: !Application.isPlaying);
        }

        private static void ReleaseOwnedFallbacks(bool immediate)
        {
            DxMessagingRuntimeSettings ownedFallback;
#if UNITY_EDITOR
            DxMessagingRuntimeSettings retainedFallback;
#endif
            lock (_gate)
            {
                _cached = null;
                ownedFallback = _ownedFallback;
                _ownedFallback = null;
#if UNITY_EDITOR
                DxMessagingRuntimeSettingsOwnership ownership =
                    DxMessagingRuntimeSettingsOwnership.instance;
                retainedFallback = ownership.Fallback;
                ownership.Fallback = null;
#endif
            }
            DestroyFallback(ownedFallback, immediate);
#if UNITY_EDITOR
            if (!ReferenceEquals(ownedFallback, retainedFallback))
            {
                DestroyFallback(retainedFallback, immediate);
            }
#endif
        }

        private static void DestroyFallback(DxMessagingRuntimeSettings fallback, bool immediate)
        {
            if (fallback == null)
            {
                return;
            }
            if (immediate)
            {
                Object.DestroyImmediate(fallback);
            }
            else
            {
                Object.Destroy(fallback);
            }
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void RegisterReloadCleanup()
        {
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= ReleaseBeforeAssemblyReload;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += ReleaseBeforeAssemblyReload;
        }

        private static void ReleaseBeforeAssemblyReload()
        {
            ReleaseOwnedFallbacks(immediate: true);
        }
#endif

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
#if UNITY_EDITOR
            DxMessagingRuntimeSettingsOwnership ownership =
                DxMessagingRuntimeSettingsOwnership.instance;
            if (ownership.Fallback != null)
            {
                _ownedFallback = ownership.Fallback;
                return _ownedFallback;
            }
#endif
            DxMessagingRuntimeSettings fallback =
                ScriptableObject.CreateInstance<DxMessagingRuntimeSettings>();
            fallback.MarkAsFallbackInstance();
            fallback.hideFlags = HideFlags.HideAndDontSave;
            fallback.name = DxMessagingRuntimeSettings.ResourceName + " (Default)";
            _ownedFallback = fallback;
#if UNITY_EDITOR
            ownership.Fallback = fallback;
#endif
            return fallback;
        }
    }
#endif
}
