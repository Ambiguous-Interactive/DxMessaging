namespace DxMessaging.Core.Configuration
{
    using System;
    using MessageBus;
#if UNITY_2021_3_OR_NEWER
    using UnityEngine;
#if UNITY_EDITOR
    using System.Collections.Concurrent;
    using System.Threading;
#endif

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

        /// <summary>Default counter-age budget for empty slots; the legacy setting name retains Seconds.</summary>
        public const float DefaultIdleEvictionSeconds = 30f;

        /// <summary>Default minimum interval between idle sweeps, in seconds.</summary>
        public const float DefaultEvictionTickIntervalSeconds = 5f;

        [SerializeField]
        [Tooltip(
            "Legacy-named idle-age budget in bus activity ticks, rounded up. Empty slots become eligible when their age exceeds this budget. Wall time controls sweep cadence only. See IdleEvictionSeconds."
        )]
        [Min(0f)]
        internal float _idleEvictionSeconds = DefaultIdleEvictionSeconds;

        [SerializeField]
        [Tooltip(
            "Caps retained pool entries, empty priority-leaf size, successful reflexive method lookups, and each reflexive scratch buffer capacity. Zero disables retention. Reflexive method lookups always use LRU; the one-entry spare has no eviction order."
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

        [SerializeField]
        [Tooltip(
            "Maximum registration-history entries per bus. Oldest entries are overwritten; zero disables storage. See RegistrationLogCapacity."
        )]
        [Min(0)]
        internal int _registrationLogCapacity = RegistrationLog.DefaultCapacity;

        private bool _isFallbackInstance;

        /// <summary>Counter-age budget for empty slots, rounded up; the legacy name retains Seconds.</summary>
        /// <remarks>
        /// Bus register, deregister and emit operations advance age. A due PlayerLoop sweep also
        /// advances it once. Wall time controls sweep cadence; it does not measure this age.
        /// </remarks>
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

        /// <summary>Maximum registration-history entries per bus; zero discards all history. Added in 4.0.</summary>
        public int RegistrationLogCapacity => _registrationLogCapacity;

        internal bool IsFallbackInstance => _isFallbackInstance;

        /// <summary>
        /// Raised on an idle Editor update after this asset is validated.
        /// Repeated validations coalesce until that update; subscribers read the latest values.
        /// Subscribers should be small and re-entrancy-safe.
        /// </summary>
        public static event Action<DxMessagingRuntimeSettings> SettingsChanged;

        /// <summary>
        /// Notifies live buses after settings validation.
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
        private static readonly ConcurrentQueue<DxMessagingRuntimeSettings> PendingValidations =
            new ConcurrentQueue<DxMessagingRuntimeSettings>();

        [NonSerialized]
        private int _validationPending;

        [UnityEditor.InitializeOnLoadMethod]
        private static void RegisterValidationUpdates()
        {
            UnityEditor.EditorApplication.update -= ProcessPendingValidations;
            UnityEditor.EditorApplication.update += ProcessPendingValidations;
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
            if (_registrationLogCapacity < 0)
            {
                _registrationLogCapacity = 0;
            }

            /*
                Validation can run during loading. Queue only managed state here; asset
                inspection and subscribers that mutate live buses run on the Editor thread.
            */
            if (Interlocked.CompareExchange(ref _validationPending, 1, 0) == 0)
            {
                PendingValidations.Enqueue(this);
            }
        }

        private static void ProcessPendingValidations()
        {
            if (
                UnityEditor.EditorApplication.isUpdating
                || UnityEditor.EditorApplication.isCompiling
            )
            {
                return;
            }

            /*
                Bound this update to the requests already queued. A subscriber can validate
                again; that new request belongs to the next update, preventing recursive drain.
            */
            int count = PendingValidations.Count;
            for (int i = 0; i < count; ++i)
            {
                if (!PendingValidations.TryDequeue(out DxMessagingRuntimeSettings settings))
                {
                    break;
                }

                Interlocked.Exchange(ref settings._validationPending, 0);
                if (settings != null && !settings.IsFallbackInstance)
                {
                    settings.ApplyValidatedSettings();
                }
            }
        }

        private void ApplyValidatedSettings()
        {
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
