namespace DxMessaging.Core.Configuration
{
    using System;
    using MessageBus;
#if UNITY_2021_3_OR_NEWER
    using UnityEngine;

    /// <summary>
    /// Runtime-loaded settings asset that controls memory-reclamation policy and
    /// pool sizing for DxMessaging. Loaded at first bus construction via
    /// <c>Resources.Load&lt;DxMessagingRuntimeSettings&gt;("DxMessagingRuntimeSettings")</c>;
    /// when the asset is absent a defaulted instance is used so the package works
    /// out-of-the-box.
    /// </summary>
    /// <remarks>
    /// To customize, create the asset via <c>Assets &gt; Create &gt; Wallstop Studios &gt;
    /// DxMessaging &gt; Runtime Settings</c> and place it under any
    /// <c>Resources/</c> folder named <c>DxMessagingRuntimeSettings.asset</c>.
    /// Field changes raise <see cref="SettingsChanged"/>; consumers should
    /// re-read derived state on the event.
    /// </remarks>
    [CreateAssetMenu(
        fileName = ResourceName,
        menuName = "Wallstop Studios/DxMessaging/Runtime Settings"
    )]
    public sealed class DxMessagingRuntimeSettings : ScriptableObject
    {
        /// <summary>Resource name (no extension) used by <c>Resources.Load</c>.</summary>
        public const string ResourceName = "DxMessagingRuntimeSettings";

        /// <summary>Default soft cap on retained entries in shared pools and the per-bus priority-leaf spare.</summary>
        public const int DefaultBufferMaxDistinctEntries = 512;

        /// <summary>Default idle threshold in seconds before an empty slot is eligible for eviction.</summary>
        public const float DefaultIdleEvictionSeconds = 30f;

        /// <summary>Default minimum interval between idle sweeps, in seconds.</summary>
        public const float DefaultEvictionTickIntervalSeconds = 5f;

        [SerializeField]
        [Tooltip(
            "Idle threshold in seconds. Empty per-message-type slots are evicted only after going at least this long without a register/deregister/dispatch touch. See IdleEvictionSeconds."
        )]
        [Min(0f)]
        internal float _idleEvictionSeconds = DefaultIdleEvictionSeconds;

        [SerializeField]
        [Tooltip(
            "Soft cap on distinct entries retained by shared collection pools and the per-bus empty priority-leaf spare. Zero disables retention; LRU selection does not affect the one-entry spare."
        )]
        [Min(0)]
        internal int _bufferMaxDistinctEntries = DefaultBufferMaxDistinctEntries;

        [SerializeField]
        [Tooltip(
            "When true, shared collection pools use LRU eviction; otherwise pools behave as a bounded LIFO stack. See BufferUseLruEviction."
        )]
        internal bool _bufferUseLruEviction = true;

        [SerializeField]
        [Tooltip(
            "When true, IMessageBus.Trim performs its work; when false it is a no-op returning default. Lets shipped titles disable on-demand reclamation. See EnableTrimApi."
        )]
        internal bool _enableTrimApi = true;

        [SerializeField]
        [Tooltip(
            "Minimum interval in seconds between idle sweeps. Emit-time idle eviction samples the clock periodically instead of at the top of every Emit, and sweeps only when this much wall time has elapsed since the last sweep. See EvictionTickIntervalSeconds."
        )]
        [Min(0f)]
        internal float _evictionTickIntervalSeconds = DefaultEvictionTickIntervalSeconds;

        [SerializeField]
        [Tooltip(
            "Master switch for idle-time eviction. When false neither inline emit-time sweeps nor PlayerLoop sweeps run; explicit Trim still works (gated by EnableTrimApi). See EvictionEnabled."
        )]
        internal bool _evictionEnabled = true;

        [SerializeField]
        [Tooltip(
            "Diagnostic message buffer size used when the bus is constructed. Mirrors IMessageBus.DefaultMessageBufferSize so the runtime asset can override the global default without touching code. See MessageBufferSize."
        )]
        [Min(0)]
        internal int _messageBufferSize = IMessageBus.DefaultMessageBufferSize;

        private bool _isFallbackInstance;

        /// <summary>Idle threshold in seconds. See <c>_idleEvictionSeconds</c>.</summary>
        public float IdleEvictionSeconds => _idleEvictionSeconds;

        /// <summary>Per-pool retained-entry cap. See <c>_bufferMaxDistinctEntries</c>.</summary>
        public int BufferMaxDistinctEntries => _bufferMaxDistinctEntries;

        /// <summary>True when shared pools use LRU eviction.</summary>
        public bool BufferUseLruEviction => _bufferUseLruEviction;

        /// <summary>True when explicit Trim APIs perform work.</summary>
        public bool EnableTrimApi => _enableTrimApi;

        /// <summary>Minimum interval between idle sweeps, in seconds.</summary>
        public float EvictionTickIntervalSeconds => _evictionTickIntervalSeconds;

        /// <summary>Master switch for idle-time eviction.</summary>
        public bool EvictionEnabled => _evictionEnabled;

        /// <summary>Diagnostic message buffer size.</summary>
        public int MessageBufferSize => _messageBufferSize;

        internal bool IsFallbackInstance => _isFallbackInstance;

        /// <summary>
        /// Raised when this asset is mutated in the editor or via test-only override.
        /// Subscribers should be small and re-entrancy-safe; the event is invoked
        /// synchronously from <c>OnValidate</c>.
        /// </summary>
        public static event Action<DxMessagingRuntimeSettings> SettingsChanged;

        /// <summary>
        /// Used by <see cref="DxMessagingRuntimeSettingsProvider.Override"/> to push
        /// a test-supplied override and notify subscribers.
        /// </summary>
        internal static void RaiseSettingsChanged(DxMessagingRuntimeSettings settings)
        {
            SettingsChanged?.Invoke(settings);
        }

        internal void MarkAsFallbackInstance()
        {
            _isFallbackInstance = true;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ClearSubscribersOnLoad()
        {
            SettingsChanged = null;
        }

#if UNITY_EDITOR
        private const string ResourceFolder = "Assets/Resources";
        private const string ResourceAssetPath = ResourceFolder + "/" + ResourceName + ".asset";

        [UnityEditor.MenuItem(
            "Assets/Create/Wallstop Studios/DxMessaging/Runtime Settings (in Resources)"
        )]
        private static void CreateAssetInResources()
        {
            if (!UnityEditor.AssetDatabase.IsValidFolder(ResourceFolder))
            {
                UnityEditor.AssetDatabase.CreateFolder("Assets", "Resources");
            }
            string targetPath = UnityEditor.AssetDatabase.GenerateUniqueAssetPath(
                ResourceAssetPath
            );
            DxMessagingRuntimeSettings asset =
                ScriptableObject.CreateInstance<DxMessagingRuntimeSettings>();
            UnityEditor.AssetDatabase.CreateAsset(asset, targetPath);
            UnityEditor.AssetDatabase.SaveAssets();
            UnityEditor.EditorGUIUtility.PingObject(asset);
        }

        private void OnValidate()
        {
            if (_idleEvictionSeconds < 0f)
            {
                _idleEvictionSeconds = 0f;
            }
            if (_bufferMaxDistinctEntries < 0)
            {
                _bufferMaxDistinctEntries = 0;
            }
            if (_evictionTickIntervalSeconds < 0f)
            {
                _evictionTickIntervalSeconds = 0f;
            }
            if (_messageBufferSize < 0)
            {
                _messageBufferSize = 0;
            }
            string assetPath = UnityEditor.AssetDatabase.GetAssetPath(this);
            if (
                !string.IsNullOrEmpty(assetPath)
                && assetPath.IndexOf("/Resources/", StringComparison.OrdinalIgnoreCase) < 0
            )
            {
                Debug.LogWarning(
                    "[DxMessaging] Runtime settings asset is not under a Resources/ folder; Resources.Load will not find it. Move it under Assets/Resources/ or use the 'Assets/Create/Wallstop Studios/DxMessaging/Runtime Settings (in Resources)' menu.",
                    this
                );
            }
            RaiseSettingsChanged(this);
        }
#endif
    }
#endif
}
